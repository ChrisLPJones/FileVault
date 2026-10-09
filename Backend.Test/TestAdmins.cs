using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // Finds the repository and the connection string the API's settings give the tests
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

        // The same sources the API uses for ConnectionStrings:DefaultConnection
        public static string ConnectionString { get; } = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot, "Backend", "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(RepositoryRoot, "Backend", "appsettings.Development.json"), optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set for the tests");
    }

    // The first account registered on an empty database becomes an administrator, and the last
    // administrator can't be removed or deleted. So that the tests don't depend on which of them
    // runs first (CI starts with an empty database), the shared test database always has an
    // administrator before any test starts. Tests that need an empty database, or exactly one
    // administrator, use FreshDatabase instead.
    //
    // This writes an admin account into whatever database the connection string points at, so it
    // refuses to unless FILEVAULT_TEST_DB=1 says that database is a test database. Locally
    // Backend.Test/test.runsettings sets it for `dotnet test` (the project points at that file);
    // CI sets it in the workflow. Anything else that loads this assembly, or a shell whose
    // connection string points at a real database, gets an error instead of a seeded account.
    // The account's password hash is a real hash of a random value, so logging in as it gives a
    // 401, and it is only added when the database has no administrator at all.
    public static class SentinelAdmin
    {
        public const string Email = "sentinel-admin@example.test";
        public const string TestDatabaseFlag = "FILEVAULT_TEST_DB";

        [ModuleInitializer]
        public static void Ensure()
        {
            if (Environment.GetEnvironmentVariable(TestDatabaseFlag) != "1")
                throw new InvalidOperationException(
                    $"Refusing to add a test administrator to the database in the connection string: set {TestDatabaseFlag}=1 " +
                    "to say it is a test database (`dotnet test` does this through Backend.Test/test.runsettings).");

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
        public string Name { get; } = "FvTest_" + Guid.NewGuid().ToString("N");
        public string ConnectionString { get; }

        private readonly string _serverConnectionString;

        private FreshDatabase()
        {
            var builder = new SqlConnectionStringBuilder(TestEnvironment.ConnectionString);
            _serverConnectionString = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            builder.InitialCatalog = Name;
            ConnectionString = builder.ConnectionString;
        }

        public static async Task<FreshDatabase> CreateAsync()
        {
            var database = new FreshDatabase();
            await RunAsync(database._serverConnectionString, $"CREATE DATABASE [{database.Name}]");
            await database.RunInitScriptAsync();
            return database;
        }

        // Run init.sql against the scratch database; it can be run again to check it is idempotent
        public async Task RunInitScriptAsync()
        {
            var script = await File.ReadAllTextAsync(Path.Combine(TestEnvironment.RepositoryRoot, "Backend", "Docker", "db", "init.sql"));
            script = script.Replace("SecureVaultDb", Name);

            // One connection throughout: the script's USE statement applies to the rest of it
            await using var connection = new SqlConnection(_serverConnectionString);
            await connection.OpenAsync();
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch))
                    continue;
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync();
            }
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

        private static async Task RunAsync(string connectionString, string sql, params (string name, object value)[] parameters)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await RunAsync(_serverConnectionString,
                $"IF DB_ID('{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }
    }
}
