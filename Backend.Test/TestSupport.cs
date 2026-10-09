using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Backend.Test
{
    // Direct database access for tests only (the API has no way to skip email verification)
    public static class TestDatabase
    {
        public static async Task ExecuteAsync(WebApplicationFactory<Program> factory, string sql, params (string name, object value)[] parameters)
        {
            var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public static async Task<object?> ScalarAsync(WebApplicationFactory<Program> factory, string sql, params (string name, object value)[] parameters)
        {
            var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value ? null : result;
        }

        // Make SQL Server pick the API's statement as a deadlock victim. A second session takes an
        // update lock on one row (lockedId) before the request starts; it is taken on the primary
        // key, as a lock on the GUID index alone wouldn't stop the request updating the row. Once
        // the request is stuck waiting on it, the session updates another row (updatedId) the
        // request has already locked: the two wait on each other, and the session is the high
        // priority one, so the request's statement is rolled back with error 1205. This relies on
        // the request locking updatedId before lockedId; if a change reverses that order there is
        // no deadlock, and the check on SQL Server's deadlock count below fails the test.
        public static async Task<HttpResponseMessage> RequestAsDeadlockVictimAsync(
            WebApplicationFactory<Program> factory, string lockedId, string updatedId, Func<Task<HttpResponseMessage>> request)
        {
            // Not pooled, so closing the connection ends the session and releases its locks even
            // if the test fails before the commit below
            var connectionString = new SqlConnectionStringBuilder(
                factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")) { Pooling = false }.ConnectionString;
            await using var blocker = new SqlConnection(connectionString);
            await blocker.OpenAsync();

            const string deadlockCount = @"
                SELECT cntr_value FROM sys.dm_os_performance_counters
                WHERE counter_name = 'Number of Deadlocks/sec' AND instance_name = '_Total'";
            var deadlocksBefore = Convert.ToInt64(await ScalarAsync(factory, deadlockCount));

            await ExecuteAsync(blocker, "SET DEADLOCK_PRIORITY HIGH; BEGIN TRAN;");
            Task<HttpResponseMessage>? pending = null;
            try
            {
                await ExecuteAsync(blocker, "SELECT 1 FROM Files WITH (UPDLOCK, ROWLOCK) WHERE Id = (SELECT Id FROM Files WHERE GUID = @Locked)", ("@Locked", lockedId));
                int blockerSession;
                await using (var spid = new SqlCommand("SELECT @@SPID", blocker))
                    blockerSession = Convert.ToInt32(await spid.ExecuteScalarAsync());

                pending = request();

                // Wait until the request really is stuck behind the blocker, not for a fixed time
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (Convert.ToInt32(await ScalarAsync(factory,
                           "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @Session AND wait_type LIKE 'LCK_M_%'",
                           ("@Session", blockerSession)) ?? 0) == 0)
                {
                    var finished = pending.IsCompleted ? $"; it already finished: {(int)pending.Result.StatusCode} {await pending.Result.Content.ReadAsStringAsync()}" : "";
                    DateTime.UtcNow.Should().BeBefore(deadline, "the request should have been waiting on the locked row" + finished);
                    await Task.Delay(20);
                }

                await ExecuteAsync(blocker, "UPDATE Files SET UpdatedAt = UpdatedAt WHERE GUID = @Updated", ("@Updated", updatedId));
                await ExecuteAsync(blocker, "COMMIT");
            }
            finally
            {
                // On failure, end the session so the request isn't left waiting on the locked row
                if (blocker.State == System.Data.ConnectionState.Open)
                    await blocker.CloseAsync();
            }

            var response = await pending;
            Convert.ToInt64(await ScalarAsync(factory, deadlockCount)).Should().BeGreaterThan(deadlocksBefore,
                "SQL Server should have had to break a deadlock, or the test proves nothing");
            return response;
        }

        private static async Task ExecuteAsync(SqlConnection connection, string sql, params (string name, object value)[] parameters)
        {
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        // New accounts must confirm their email before logging in; tests that register and log
        // straight in mark the address confirmed, as if the emailed link had been opened
        public static Task MarkEmailVerifiedAsync(WebApplicationFactory<Program> factory, string email) =>
            ExecuteAsync(factory, "UPDATE Users SET EmailVerified = 1 WHERE Email = @Email", ("@Email", email.Trim().ToLowerInvariant()));
    }

    // Shared helpers for the integration tests of the share, recycle bin, email and upload
    // endpoints: throwaway accounts that are deleted afterwards, and common file operations.
    public abstract class IntegrationTestBase : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        protected const string Password = "TestPassw0rd";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly List<WebApplicationFactory<Program>> _extraFactories = new();
        private readonly List<HttpClient> _usersToDelete = new();
        private readonly List<string> _registeredEmails = new();

        protected WebApplicationFactory<Program> Factory { get; }

        protected IntegrationTestBase(WebApplicationFactory<Program> factory, params (string key, string value)[] settings)
        {
            _baseFactory = factory;
            // These tests register and log in many accounts; keep the login rate limit out of the way
            Factory = WithSettings([("RateLimiting:auth:PermitLimit", "1000"), .. settings]);
        }

        protected WebApplicationFactory<Program> WithSettings(params (string key, string value)[] settings) =>
            WithHost(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(settings.ToDictionary(s => s.key, s => (string?)s.value))));

        protected WebApplicationFactory<Program> WithHost(Action<IWebHostBuilder> configure, WebApplicationFactory<Program>? from = null)
        {
            var factory = (from ?? _baseFactory).WithWebHostBuilder(configure);
            _extraFactories.Add(factory);
            return factory;
        }

        protected static string NewEmail() => $"core_{Guid.NewGuid():N}@example.test";

        // Register a new account without confirming its email address (so it can't log in yet).
        // It is removed afterwards even if it never logs in.
        protected async Task RegisterOnlyAsync(WebApplicationFactory<Program>? factory, string email)
        {
            var client = Anonymous(factory);
            var register = await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Core", LastName = "Tester", Email = email, Password = Password });
            register.StatusCode.Should().Be(HttpStatusCode.OK, await register.Content.ReadAsStringAsync());
            _registeredEmails.Add(email);
        }

        // Log in an account whose address is confirmed; the returned client sends its access token
        protected async Task<HttpClient> LogInAsync(WebApplicationFactory<Program>? factory, string email, string password = Password)
        {
            var client = Anonymous(factory);
            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password });
            login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("success").GetString());

            _usersToDelete.Add(client);
            return client;
        }

        // Register, confirm the email address and log in
        protected async Task<HttpClient> NewUserAsync(WebApplicationFactory<Program>? factory = null, string? email = null)
        {
            email ??= NewEmail();
            await RegisterOnlyAsync(factory, email);
            await TestDatabase.MarkEmailVerifiedAsync(Factory, email);
            return await LogInAsync(factory, email);
        }

        // A client with no access token
        protected HttpClient Anonymous(WebApplicationFactory<Program>? factory = null) =>
            (factory ?? Factory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, string text, string? parentId = null)
        {
            var multipart = new MultipartFormDataContent();
            if (parentId != null)
                multipart.Add(new StringContent(parentId), "parentId");
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            multipart.Add(file, "file", fileName);
            return await client.PostAsync("/upload", multipart);
        }

        protected static async Task<string> UploadFileAsync(HttpClient client, string fileName, string text, string? parentId = null)
        {
            (await UploadAsync(client, fileName, text, parentId)).StatusCode.Should().Be(HttpStatusCode.OK);
            var files = await ListFilesAsync(client);
            var parentPath = parentId == null ? "" : files.Single(f => Id(f) == parentId).GetProperty("path").GetString();
            return Id(files.Single(f => f.GetProperty("path").GetString() == $"{parentPath}/{fileName}"));
        }

        protected static async Task<string> CreateFolderAsync(HttpClient client, string name, string? parentId = null)
        {
            var response = await client.PostAsJsonAsync("/folder", new { name, parentId });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("_id").GetString()!;
        }

        protected static async Task<JsonElement[]> ListFilesAsync(HttpClient client)
        {
            var response = await client.GetAsync("/files");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await ReadArrayAsync(response);
        }

        protected static async Task<JsonElement[]> ReadArrayAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        protected static string Id(JsonElement item) => item.GetProperty("_id").GetString()!;

        protected static string[] Paths(JsonElement[] files) =>
            files.Select(f => f.GetProperty("path").GetString()!).ToArray();

        protected static Task<HttpResponseMessage> DeleteWithBodyAsync(HttpClient client, string url, object body) =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) });

        protected string StoredFilePath(string guid) =>
            Path.Combine(Factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("StorageRoot")!, guid);

        // Run SQL against the test database (to simulate time passing, e.g. an expired link)
        protected Task ExecuteSqlAsync(string sql, params (string name, object value)[] parameters) =>
            TestDatabase.ExecuteAsync(Factory, sql, parameters);

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var client in _usersToDelete)
                await client.DeleteAsync("/user");

            // Accounts that never logged in (or changed address since): remove them directly
            foreach (var email in _registeredEmails)
                await ExecuteSqlAsync(@"
                    DELETE FROM Files WHERE UserId IN (SELECT Id FROM Users WHERE Email = @Email);
                    DELETE FROM Users WHERE Email = @Email;", ("@Email", email));

            foreach (var factory in _extraFactories)
                await factory.DisposeAsync();
        }
    }
}
