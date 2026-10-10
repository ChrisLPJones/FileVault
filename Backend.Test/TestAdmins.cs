using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // Finds the repository and the connection string the tests use. That is never the one the API's
    // settings give (the dev database): SentinelAdmin.Ensure creates a per-run FvTest_Shared_<guid>
    // database on the same server and points ConnectionStrings__DefaultConnection at it, so every
    // WebApplicationFactory and everything reading ConnectionString uses that database.
    public static class TestEnvironment
    {
        public static string RepositoryRoot { get; } = FindRepositoryRoot();

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Backend", "Docker", "db", "init.sql")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        // The same sources the API uses for ConnectionStrings:DefaultConnection. Only used to find the
        // SQL Server; never to run anything against its database.
        public static string LoadConfiguredConnectionString() => new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot, "Backend", "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(RepositoryRoot, "Backend", "appsettings.Development.json"), optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set for the tests");

        // The per-run test database; set by SentinelAdmin.Ensure before any host is built
        public static string ConnectionString { get; internal set; } = "";
    }

    // Refuses any database whose name doesn't start with FvTest_
    public static class TestDatabaseGuard
    {
        public const string Prefix = "FvTest_";

        public static void AssertTestDatabase(string connectionString)
        {
            var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            if (string.IsNullOrEmpty(name) || !name.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Refusing to run tests against database '{name}': test databases must be named {Prefix}*.");
        }
    }

    // The first account registered on an empty database becomes an administrator, and the last
    // administrator can't be removed or deleted. So that the tests don't depend on which of them
    // runs first (CI starts with an empty database), the shared test database always has an
    // administrator before any test starts. Tests that need an empty database, or exactly one
    // administrator, use FreshDatabase instead.
    //
    // At assembly load this creates FvTest_Shared_<guid> from init.sql on the configured server (after
    // dropping FvTest_ databases more than a day old), sets ConnectionStrings__DefaultConnection to it
    // for the process, checks the name with TestDatabaseGuard, and only then adds the sentinel admin.
    // The database is dropped when the test process exits. Local and CI runs take the same path.
    // The account's password hash is a real hash of a random value, so logging in as it gives a
    // 401, and it is only added when the database has no administrator at all.
    public static class SentinelAdmin
    {
        public const string Email = "sentinel-admin@example.test";
        private const string ConnectionStringVariable = "ConnectionStrings__DefaultConnection";

        private static FreshDatabase? _shared;

        [ModuleInitializer]
        public static void Ensure()
        {
            var configured = TestEnvironment.LoadConfiguredConnectionString();

            // The name is checked here before anything is created; the effective check is the assertion
            // on TestEnvironment.ConnectionString below, which is what every host and test then uses
            var name = TestDatabaseGuard.Prefix + "Shared_" + Guid.NewGuid().ToString("N");
            var testConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = name }.ConnectionString;
            TestDatabaseGuard.AssertTestDatabase(testConnectionString);

            // Synchronous on purpose: a module initializer that waits on thread-pool work can deadlock the assembly load
            FreshDatabase.DropStale(configured);
            _shared = FreshDatabase.Create(configured, name);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _shared.Drop(); } catch { /* stale ones are dropped at the next start */ }
            };

            Environment.SetEnvironmentVariable(ConnectionStringVariable, testConnectionString);
            TestEnvironment.ConnectionString = testConnectionString;
            TestDatabaseGuard.AssertTestDatabase(TestEnvironment.ConnectionString);

            using var connection = new SqlConnection(TestEnvironment.ConnectionString);
            connection.Open();
            using var command = new SqlCommand(@"
                BEGIN TRAN;
                EXEC sp_getapplock @Resource = 'fv-admin-membership', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
                IF NOT EXISTS (SELECT 1 FROM Users WHERE IsAdmin = 1)
                BEGIN
                    DELETE FROM Users WHERE Email = @Email;
                    INSERT INTO Users (FirstName, LastName, Email, PasswordHash, IsAdmin, EmailVerified)
                    VALUES ('Sentinel', 'Admin', @Email, @PasswordHash, 1, 1);
                END
                COMMIT;", connection);
            command.Parameters.AddWithValue("@Email", Email);
            command.Parameters.AddWithValue("@PasswordHash", BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), workFactor: 4));
            command.ExecuteNonQuery();
        }
    }

    // A scratch database on the test SQL Server, created from init.sql and dropped afterwards,
    // for tests that need an empty Users table or exactly one administrator without touching
    // the shared database. Creating one also checks init.sql works on a fresh server.
    public sealed class FreshDatabase : IAsyncDisposable
    {
        public string Name { get; }
        public string ConnectionString { get; }

        private readonly string _serverConnectionString;

        // baseConnectionString only says which server to use
        private FreshDatabase(string baseConnectionString, string name)
        {
            Name = name;
            TestDatabaseGuard.AssertTestDatabase(new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = name }.ConnectionString);
            var builder = new SqlConnectionStringBuilder(baseConnectionString);
            _serverConnectionString = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            builder.InitialCatalog = Name;
            ConnectionString = builder.ConnectionString;
        }

        public static Task<FreshDatabase> CreateAsync() =>
            CreateAsync(TestEnvironment.ConnectionString, TestDatabaseGuard.Prefix + Guid.NewGuid().ToString("N"));

        public static async Task<FreshDatabase> CreateAsync(string baseConnectionString, string name)
        {
            var database = new FreshDatabase(baseConnectionString, name);
            await RunAsync(database._serverConnectionString, $"CREATE DATABASE [{database.Name}]");
            await database.RunInitScriptAsync();
            return database;
        }

        // The same, without async (used while the test assembly loads, see SentinelAdmin.Ensure)
        public static FreshDatabase Create(string baseConnectionString, string name)
        {
            var database = new FreshDatabase(baseConnectionString, name);
            Run(database._serverConnectionString, $"CREATE DATABASE [{database.Name}]");
            using var connection = new SqlConnection(database._serverConnectionString);
            connection.Open();
            foreach (var batch in database.InitScriptBatches())
            {
                using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                command.ExecuteNonQuery();
            }
            return database;
        }

        // Left behind by runs that were killed: any FvTest_ database more than a day old, and any
        // FvTest_Shared_ one with no sessions that is also more than an hour old. The session check alone
        // isn't reliable (idle pooled connections are pruned, and without VIEW SERVER STATE only your own
        // sessions are visible), so the hour keeps a live run's database safe.
        public const string StaleDatabasesSql = @"
            SELECT d.name FROM sys.databases d
            WHERE d.name LIKE 'FvTest[_]%'
              AND (d.create_date < DATEADD(DAY, -1, GETDATE())
                   OR (d.name LIKE 'FvTest[_]Shared[_]%'
                       AND d.create_date < DATEADD(HOUR, -1, GETDATE())
                       AND NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions s WHERE s.database_id = d.database_id)))";

        // Drops one database by name; QUOTENAME builds the identifier server-side
        private const string DropDatabaseSql = @"
            DECLARE @Sql NVARCHAR(400) = N'ALTER DATABASE ' + QUOTENAME(@Name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@Name) + N';';
            IF DB_ID(@Name) IS NOT NULL EXEC (@Sql);";

        public static void DropStale(string baseConnectionString)
        {
            try
            {
                var server = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
                var stale = new List<string>();
                using (var connection = new SqlConnection(server))
                {
                    connection.Open();
                    using var command = new SqlCommand(
                        StaleDatabasesSql, connection);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        stale.Add(reader.GetString(0));
                }
                foreach (var name in stale)
                    Run(server, DropDatabaseSql, ("@Name", name));
            }
            catch (SqlException)
            {
                // Best effort: a database that can't be dropped now is tried again at the next start
            }
        }

        // Run init.sql against the scratch database; it can be run again to check it is idempotent
        public async Task RunInitScriptAsync()
        {
            // One connection throughout: the script's USE statement applies to the rest of it
            await using var connection = new SqlConnection(_serverConnectionString);
            await connection.OpenAsync();
            foreach (var batch in InitScriptBatches())
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync();
            }
        }

        private IEnumerable<string> InitScriptBatches()
        {
            var script = File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "Backend", "Docker", "db", "init.sql"));
            script = script.Replace("SecureVaultDb", Name);
            return Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                .Where(batch => !string.IsNullOrWhiteSpace(batch));
        }

        public Task ExecuteAsync(string sql, params (string name, object value)[] parameters) =>
            RunAsync(ConnectionString, sql, parameters);

        public async Task<T> ScalarAsync<T>(string sql, params (string name, object value)[] parameters)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
        }

        public Task<int> AdminCountAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE IsAdmin = 1");

        public Task<bool> IsAdminAsync(string email) =>
            ScalarAsync<bool>("SELECT IsAdmin FROM Users WHERE Email = @Email", ("@Email", email));

        // The app, pointed at this database, with the login rate limit out of the way
        // (initialAdminEmail is the INITIAL_ADMIN_EMAIL setting)
        public WebApplicationFactory<Program> CreateFactory(WebApplicationFactory<Program> from, string? initialAdminEmail = null) =>
            from.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimiting:auth:PermitLimit", "1000");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                        ["Admin:InitialEmail"] = initialAdminEmail
                    }));
            });

        // Start the API against this database and stop it again: what happens at every real start
        // (the startup step that makes sure an administrator exists)
        public void StartApi(WebApplicationFactory<Program> from, string? initialAdminEmail = null)
        {
            using var factory = CreateFactory(from, initialAdminEmail);
            factory.CreateClient().Dispose();
        }

        private static void Run(string connectionString, string sql, params (string name, object value)[] parameters)
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        private static async Task RunAsync(string connectionString, string sql, params (string name, object value)[] parameters)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public void Drop()
        {
            SqlConnection.ClearAllPools();
            Run(_serverConnectionString, DropDatabaseSql, ("@Name", Name));
        }

        public ValueTask DisposeAsync()
        {
            Drop();
            return ValueTask.CompletedTask;
        }
    }
}
