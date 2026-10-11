using Backend.Services;
using Microsoft.AspNetCore.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Test
{
    // The API's SQL login in the Docker install (Backend/Docker/db/app-login.sql): it can read and write the
    // data and nothing else. The whole suite's hosts connect as such a login (see SentinelAdmin.Ensure); these
    // tests prove the login itself on a scratch database: what works, what is refused, and that the script
    // can be run again.
    public class LeastPrivilegeLoginTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
    {
        private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        private static SqlConnection Open(string connectionString)
        {
            var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            connection.Open();
            return connection;
        }

        private static async Task<object?> ScalarAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        private WebApplicationFactory<Program> App(FreshDatabase db, string masterKey) =>
            db.CreateFactory(factory, masterKey: masterKey);

        [Fact]
        public async Task ApiHosts_RunAsTheLeastPrivilegeLogin()
        {
            await using var db = await FreshDatabase.CreateAsync();
            await using var app = App(db, NewKey());

            var login = (string)(await TestDatabase.ScalarAsync(app, "SELECT SUSER_NAME()"))!;

            login.Should().Be(TestEnvironment.AppLogin).And.StartWith(AppLoginScript.LoginPrefix);
            (await TestDatabase.ScalarAsync(app, "SELECT IS_SRVROLEMEMBER('sysadmin')")).Should().Be(0);
        }

        [Fact]
        public async Task EverydayUse_Works()
        {
            await using var db = await FreshDatabase.CreateAsync();
            await using var app = App(db, NewKey());

            var owner = await TestAccounts.CreateAsync(app, "lpl");
            var content = Encoding.UTF8.GetBytes("least privilege");
            var fileId = await TestAccounts.UploadAsync(owner.Client, "lp.txt", content, "text/plain");
            (await owner.Client.GetStringAsync($"/download/{fileId}")).Should().Be("least privilege");

            var share = await owner.Client.PostAsJsonAsync("/shares", new { itemId = fileId });
            share.StatusCode.Should().Be(HttpStatusCode.OK, await share.Content.ReadAsStringAsync());
            (await owner.Client.GetAsync("/shares")).StatusCode.Should().Be(HttpStatusCode.OK);

            // The first account on an empty database is the administrator
            (await owner.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await owner.Client.DeleteAsync($"/delete/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Login_CannotChangeTheSchemaOrLeaveItsDatabase()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var otherDatabase = new SqlConnectionStringBuilder(TestEnvironment.ConnectionString).InitialCatalog; // the shared test database
            // The run's login is mapped into every test database, so use one that is mapped into this database only
            var login = AppLoginScript.LoginPrefix + Guid.NewGuid().ToString("N");
            var password = AppLoginScript.NewPassword();
            var server = new SqlConnectionStringBuilder(TestEnvironment.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
            AppLoginScript.Run(server, login, password, db.Name);

            try
            {
            await using var connection = new SqlConnection(AppLoginScript.ConnectionStringFor(db.ConnectionString, login, password));
            await connection.OpenAsync();
            async Task<SqlException> FailsAsync(string sql)
            {
                await using var command = new SqlCommand(sql, connection);
                try { await command.ExecuteNonQueryAsync(); }
                catch (SqlException ex) { return ex; }
                throw new Xunit.Sdk.XunitException($"Expected a permission error, but this ran: {sql}");
            }

            // Data: allowed
            await using (var insert = new SqlCommand("SELECT COUNT(*) FROM Users", connection))
                (await insert.ExecuteScalarAsync()).Should().NotBeNull();

            // Schema: refused (permission-denied errors; 3701 is DROP's version of it)
            foreach (var sql in new[]
            {
                "CREATE TABLE Smuggled (Id INT)",
                "ALTER TABLE Users ADD Smuggled INT NULL",
                "DROP TABLE Sessions",
                "TRUNCATE TABLE Users",
                "CREATE USER Smuggled WITHOUT LOGIN",
                "ALTER ROLE db_owner ADD MEMBER filevault_app_role",
                $"ALTER ROLE filevault_app_role ADD MEMBER [{login}]",
                "GRANT ALTER ON SCHEMA::dbo TO filevault_app_role",
                "EXECUTE AS LOGIN = 'sa'",
                "EXEC xp_cmdshell 'id'",
                "EXEC sp_configure 'show advanced options', 1",
                "CREATE LOGIN FvTest_login_smuggled WITH PASSWORD = 'Aa1-Smuggled-Passw0rd'",
                $"SELECT COUNT(*) FROM [{otherDatabase}].dbo.Users",
            })
                (await FailsAsync(sql)).Number.Should().BeOneOf([262, 229, 230, 297, 15247, 15151, 916, 1088, 4060, 15007, 15406, 3701], sql);

            await using (var admin = new SqlCommand("SELECT IS_SRVROLEMEMBER('sysadmin'), IS_ROLEMEMBER('db_owner')", connection))
            await using (var reader = await admin.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt32(0).Should().Be(0);
                reader.GetInt32(1).Should().Be(0);
            }
            (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Smuggled'")).Should().Be(0);
            (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Sessions'")).Should().Be(1);
            }
            finally
            {
                SqlConnection.ClearAllPools();
                AppLoginScript.Drop(server, login);
            }
        }

        // --- Master key rotation as the app login (the one place the API reads server state) ---

        private static async Task<(int exitCode, string output)> RotateAsync(FreshDatabase db, string oldKey, string? newKey, params string[] args)
        {
            SqlConnection.ClearAllPools();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = db.AppConnectionString,
                ["Encryption:MasterKey"] = oldKey,
                ["Encryption:NewMasterKey"] = newKey,
            }).Build();
            var output = new StringWriter();
            var exit = await MasterKeyCommand.RunAsync(args, config, output);
            return (exit, output.ToString());
        }

        [Fact]
        public async Task Rotation_DryRunAndRealRotation_Succeed()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            string fileId;
            await using (var appA = App(db, keyA))
            {
                var account = await TestAccounts.CreateAsync(appA, "lplrot");
                fileId = await TestAccounts.UploadAsync(account.Client, "rot.txt", "rotate me"u8.ToArray(), "text/plain");
                account.Client.Dispose();
            }
            SqlConnection.ClearAllPools();

            var (exit, output) = await RotateAsync(db, keyA, keyB, "--dry-run");
            exit.Should().Be(0, output);
            output.Should().NotContain("no permission");

            (exit, output) = await RotateAsync(db, keyA, keyB);
            exit.Should().Be(0, output);
            output.Should().NotContain("no permission", "the login sees other connections, so the check is real");
            fileId.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task Rotation_RefusesWhileAnotherConnectionIsOpen()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            SqlConnection.ClearAllPools();

            // Another connection to the database, as a different login, that the app login can only see through its server permission
            using var other = Open(db.ConnectionString);

            var (exit, output) = await RotateAsync(db, keyA, keyB, "--dry-run");

            exit.Should().Be(1, output);
            output.Should().Contain("Other connections are open");
        }

        // --- The script ---

        [Fact]
        public async Task Script_CanBeRunAgain_AndANewPasswordReplacesTheOldOne()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var login = AppLoginScript.LoginPrefix + Guid.NewGuid().ToString("N");
            var first = AppLoginScript.NewPassword();
            var second = AppLoginScript.NewPassword();
            var server = new SqlConnectionStringBuilder(TestEnvironment.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
            string AsLogin(string password) => new SqlConnectionStringBuilder(db.ConnectionString) { UserID = login, Password = password, Pooling = false }.ConnectionString;

            try
            {
                AppLoginScript.Run(server, login, first, db.Name);
                AppLoginScript.Run(server, login, first, db.Name); // again: harmless
                (await ScalarAsync(AsLogin(first), "SELECT COUNT(*) FROM Users")).Should().NotBeNull();

                AppLoginScript.Run(server, login, second, db.Name);
                var oldPassword = async () => await ScalarAsync(AsLogin(first), "SELECT 1");
                await oldPassword.Should().ThrowAsync<SqlException>();
                (await ScalarAsync(AsLogin(second), "SELECT COUNT(*) FROM Users")).Should().NotBeNull();

                (await ScalarAsync(server, $"SELECT COUNT(*) FROM sys.sql_logins WHERE name = N'{login}' AND is_expiration_checked = 0")).Should().Be(1);
                (await ScalarAsync(db.ConnectionString, $"SELECT COUNT(*) FROM sys.database_principals WHERE name = N'{login}'")).Should().Be(1);
            }
            finally
            {
                SqlConnection.ClearAllPools();
                AppLoginScript.Drop(server, login);
            }
        }

        [Fact]
        public void Drop_RefusesALoginThatIsNotATestLogin()
        {
            var drop = () => AppLoginScript.Drop(TestEnvironment.ConnectionString, "sa");

            drop.Should().Throw<InvalidOperationException>();
        }

        // --- The warning for a privileged login ---

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PrivilegedLoginWarning_NamesTheProblem(bool sysadmin, bool dbOwner)
        {
            var warning = MasterKeyGuard.PrivilegedLoginWarning(sysadmin, dbOwner);

            warning.Should().Contain("MSSQL_APP_PASSWORD");
        }

        [Fact]
        public void PrivilegedLoginWarning_IsNullForTheLeastPrivilegeLogin() =>
            MasterKeyGuard.PrivilegedLoginWarning(false, false).Should().BeNull();

        [Fact]
        public async Task Guard_WarnsWhenTheApiRunsAsSa_ButNotAsTheAppLogin()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var key = NewKey();
            var sa = new List<string>();
            var app = new List<string>();

            WebApplicationFactory<Program> WithLogs(string connectionString, List<string> into)
            {
                var provider = new CollectingLoggerProvider(into);
                return App(db, key).WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString }));
                    builder.ConfigureLogging(logging => logging.AddProvider(provider));
                });
            }

            await using (var asSa = WithLogs(db.ConnectionString, sa))
                asSa.CreateClient().Dispose();
            await using (var asApp = WithLogs(db.AppConnectionString, app))
                asApp.CreateClient().Dispose();

            sa.Should().Contain(m => m.Contains("MSSQL_APP_PASSWORD"));
            app.Should().NotContain(m => m.Contains("MSSQL_APP_PASSWORD"));
        }

        private sealed class CollectingLoggerProvider(List<string> messages) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new Collector(messages);
            public void Dispose() { }

            private sealed class Collector(List<string> messages) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (messages)
                        messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
