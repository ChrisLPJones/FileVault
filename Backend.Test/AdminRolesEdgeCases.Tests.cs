using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Backend.Test
{
    // Edge cases around administrator membership, each in its own scratch database (see FreshDatabase)
    public class AdminRolesEdgeCasesTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private FreshDatabase _db = null!;
        private WebApplicationFactory<Program> _factory = null!;

        public AdminRolesEdgeCasesTests(WebApplicationFactory<Program> factory) => _baseFactory = factory;

        public async Task InitializeAsync()
        {
            _db = await FreshDatabase.CreateAsync();
            _factory = _db.CreateFactory(_baseFactory);
        }

        public async Task DisposeAsync()
        {
            await _factory.DisposeAsync();
            await _db.DisposeAsync();
        }

        private Task<TestAccounts.Account> NewAccountAsync(string prefix = "user") => TestAccounts.CreateAsync(_factory, prefix);

        private static Task<HttpResponseMessage> SetAdminAsync(HttpClient client, string userId, bool isAdmin) =>
            client.PutAsJsonAsync($"/admin/users/{userId}/admin", new { isAdmin });

        private sealed class CapturingProvider : ILoggerProvider
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
            public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
            public void Dispose() { }

            private sealed class CapturingLogger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }

        [Fact]
        public async Task Registering_WhenOnlyUnconfirmedAccountsExist_DoesNotMakeAnAdmin()
        {
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, EmailVerified, IsAdmin)
                VALUES ('Not', 'Confirmed', 'unconfirmed@example.test', 'x', 0, 0)");

            var account = await NewAccountAsync("second");

            (await _db.IsAdminAsync(account.Email)).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(0);
        }

        [Fact]
        public async Task Admin_CanDemoteThemselves_WhenAnotherAdminExists()
        {
            var first = await NewAccountAsync("first");
            var second = await NewAccountAsync("second");
            (await SetAdminAsync(first.Client, second.UserId, true)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await SetAdminAsync(first.Client, first.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await _db.IsAdminAsync(first.Email)).Should().BeFalse();
            (await _db.IsAdminAsync(second.Email)).Should().BeTrue();
            (await first.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // Now the second is the last one again
            (await SetAdminAsync(second.Client, second.UserId, false)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Theory]
        [InlineData("{\"isAdmin\":\"true\"}")]
        [InlineData("{\"isAdmin\":1}")]
        [InlineData("{\"isAdmin\":null}")]
        [InlineData("{\"isAdmin\":[true]}")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("")]
        public async Task NonBooleanBodies_AreRejectedWith400_AndChangeNothing(string body)
        {
            var admin = await NewAccountAsync("first");
            var user = await NewAccountAsync("second");

            var request = new HttpRequestMessage(HttpMethod.Put, $"/admin/users/{user.UserId}/admin")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            var response = await admin.Client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await _db.AdminCountAsync()).Should().Be(1);
            (await _db.IsAdminAsync(user.Email)).Should().BeFalse();
        }

        [Fact]
        public async Task GarbageAndUnknownIds_Return404_AndAnonymousOrNonAdminCallsAreRefusedFirst()
        {
            var admin = await NewAccountAsync("first");
            var user = await NewAccountAsync("second");

            foreach (var id in new[] { Guid.NewGuid().ToString(), "garbage", "00000000-0000-0000-0000-000000000000", "1%27%3B--" })
                (await SetAdminAsync(admin.Client, id, true)).StatusCode.Should().Be(HttpStatusCode.NotFound, id);

            (await SetAdminAsync(user.Client, Guid.NewGuid().ToString(), true)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SetAdminAsync(_factory.CreateClient(), user.UserId, true)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task LastAdmin_CannotBeLockedOut_WhileTheyGrantAnotherUserAtTheSameTime()
        {
            for (var round = 0; round < 4; round++)
            {
                var admin = await NewAccountAsync("only");
                var other = await NewAccountAsync("other");
                // Exactly the first of this round's two accounts is the only administrator
                await _db.ExecuteAsync("UPDATE Users SET IsAdmin = CASE WHEN Id = @Id THEN 1 ELSE 0 END", ("@Id", admin.UserId));

                var responses = await Task.WhenAll(admin.Client.DeleteAsync("/user"), SetAdminAsync(admin.Client, other.UserId, true));

                // The grant may land before the delete (the delete then succeeds) or after (the delete is
                // refused). Either way somebody is always an administrator.
                (await _db.AdminCountAsync()).Should().BeGreaterThanOrEqualTo(1, $"round {round}: {responses[0].StatusCode}/{responses[1].StatusCode}");
                responses[0].StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            }
        }

        [Fact]
        public async Task Startup_BreaksACreatedAtTieByEmail_RegardlessOfInsertOrder()
        {
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, CreatedAt, EmailVerified, IsAdmin) VALUES
                ('Z', 'Z', 'zed@example.test', 'x', '2020-01-01', 1, 0),
                ('M', 'M', 'mid@example.test', 'x', '2020-01-01', 1, 0),
                ('A', 'A', 'alpha@example.test', 'x', '2020-01-01', 1, 0)");

            _db.StartApi(_baseFactory);

            (await _db.AdminCountAsync()).Should().Be(1);
            (await _db.IsAdminAsync("alpha@example.test")).Should().BeTrue();
        }

        [Fact]
        public async Task Startup_LeavesAnExistingAdminSetUntouched()
        {
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, CreatedAt, EmailVerified, IsAdmin) VALUES
                ('Old', 'Old', 'old@example.test', 'x', '2019-01-01', 1, 0),
                ('Adm', 'One', 'adm1@example.test', 'x', '2021-01-01', 0, 1),
                ('Adm', 'Two', 'adm2@example.test', 'x', '2022-01-01', 1, 1)");

            _db.StartApi(_baseFactory);

            (await _db.AdminCountAsync()).Should().Be(2);
            (await _db.IsAdminAsync("old@example.test")).Should().BeFalse();
            (await _db.IsAdminAsync("adm1@example.test")).Should().BeTrue();
            (await _db.IsAdminAsync("adm2@example.test")).Should().BeTrue();
        }

        [Fact]
        public void StartupWarning_IsLogged_OnlyWhenAdminEmailsIsSet()
        {
            var withSetting = new CapturingProvider();
            using (var factory = _factory.WithWebHostBuilder(b =>
            {
                b.UseSetting("Admin:Emails", "someone@example.test");
                b.ConfigureLogging(l => l.AddProvider(withSetting));
            }))
                factory.CreateClient();
            withSetting.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Admin:Emails"));

            var without = new CapturingProvider();
            using (var factory = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(without))))
                factory.CreateClient();
            without.Entries.Should().NotContain(e => e.Message.Contains("Admin:Emails"));
        }
    }
}
