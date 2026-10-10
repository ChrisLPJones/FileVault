using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // Suspending accounts: a suspended user can't sign in or keep using what they hold, their data
    // stays, and unsuspending brings the account back. Accounts are throwaway ones in the shared
    // test database (which always has the sentinel admin).
    public partial class AccountSuspensionTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private const string RecoveryCode = "abcd-efgh-jkmn";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly WebApplicationFactory<Program> _factory;   // access-token check not cached
        private readonly WebApplicationFactory<Program> _cached;    // the default 30 s cache
        private readonly CapturingEmailSender _emails = new();

        public AccountSuspensionTests(WebApplicationFactory<Program> factory)
        {
            _baseFactory = factory;
            var open = TestAccounts.WithoutLoginLimit(factory)
                .WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:two-factor:PermitLimit", "1000")
                    .UseSetting("RateLimiting:email:PermitLimit", "1000")
                    .UseSetting("RateLimiting:share:PermitLimit", "1000")
                    .UseSetting("RateLimiting:share-download:PermitLimit", "1000"));
            _cached = open.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IEmailSender>(_emails)));
            _factory = _cached.WithWebHostBuilder(builder => builder.UseSetting("Auth:UserStateCacheSeconds", "0"));
        }

        [GeneratedRegex(@"/reset-password\?token=([A-Za-z0-9_-]{43})")]
        private static partial Regex ResetLinkPattern();

        private async Task<TestAccounts.Account> NewAdminAsync(WebApplicationFactory<Program>? factory = null)
        {
            var account = await TestAccounts.CreateAsync(factory ?? _factory, "admin");
            await TestDatabase.SetAdminAsync(_factory, account.UserId, true);
            return account;
        }

        private Task<TestAccounts.Account> NewUserAsync(WebApplicationFactory<Program>? factory = null) =>
            TestAccounts.CreateAsync(factory ?? _factory, "plain");

        private static Task<HttpResponseMessage> SuspendAsync(HttpClient admin, string userId, bool suspended = true) =>
            admin.PutAsJsonAsync($"/admin/users/{userId}/suspended", new { suspended });

        private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        private static Task<HttpResponseMessage> LoginAsync(WebApplicationFactory<Program> factory, string email, string password = TestAccounts.Password) =>
            factory.CreateClient().PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password });

        // Remove a throwaway pair of accounts (an administrator can delete a suspended one)
        private static async Task CleanUpAsync(TestAccounts.Account admin, params TestAccounts.Account[] targets)
        {
            foreach (var target in targets)
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
            await admin.Client.DeleteAsync("/user");
        }

        [Fact]
        public async Task NonAdmins_CannotSuspend()
        {
            var user = await NewUserAsync();
            var other = await NewUserAsync();
            try
            {
                (await SuspendAsync(user.Client, other.UserId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await LoginAsync(_factory, other.Email)).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await user.Client.DeleteAsync("/user");
                await other.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Suspend_RefusesBadRequests_AndOwnAccount()
        {
            var admin = await NewAdminAsync();
            try
            {
                (await SuspendAsync(admin.Client, admin.UserId)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SuspendAsync(admin.Client, Guid.NewGuid().ToString())).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await SuspendAsync(admin.Client, "not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await admin.Client.PutAsJsonAsync($"/admin/users/{admin.UserId}/suspended", new { })).StatusCode
                    .Should().Be(HttpStatusCode.BadRequest);
                (await admin.Client.GetAsync("/admin/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Login_IsRefusedWithTheSuspendedMessage_OnlyAfterTheCorrectPassword_AndWorksAgainAfterUnsuspending()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                var folder = await TestAccounts.CreateFolderAsync(target.Client, "Kept");
                var lastLoginBefore = await TestDatabase.ScalarAsync(_factory, "SELECT LastLogin FROM Users WHERE Id = @Id", ("@Id", target.UserId));

                (await SuspendAsync(admin.Client, target.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);
                await Task.Delay(30); // LastLogin has a coarse resolution; give a wrongly written one time to differ

                var refused = await LoginAsync(_factory, target.Email);
                refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                var body = await ReadAsync(refused);
                body.GetProperty("error").GetString().Should().Be("This account has been suspended");
                body.GetProperty("suspended").GetBoolean().Should().BeTrue();
                refused.Headers.Contains("Set-Cookie").Should().BeFalse("no session is started");

                // A wrong password still looks like any other account's
                var wrong = await LoginAsync(_factory, target.Email, "Wr0ngPassword");
                wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await wrong.Content.ReadAsStringAsync()).Should().NotContain("suspended");

                (await TestDatabase.ScalarAsync(_factory, "SELECT LastLogin FROM Users WHERE Id = @Id", ("@Id", target.UserId)))
                    .Should().Be(lastLoginBefore, "a refused sign-in isn't a login");

                // Unsuspending brings the account back with its data
                (await SuspendAsync(admin.Client, target.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
                var client = await TestAccounts.LoginAsync(_factory.CreateClient(), target.Email);
                (await TestAccounts.ListAsync(client)).Should().Contain(f => f.GetProperty("_id").GetString() == folder);
            }
            finally
            {
                await CleanUpAsync(admin, target);
            }
        }

        [Fact]
        public async Task AdminList_AndStats_ShowSuspendedAccounts()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                async Task<JsonElement> RowAsync() => (await ReadAsync(await admin.Client.GetAsync("/admin/users")))
                    .EnumerateArray().Single(u => u.GetProperty("id").GetString() == target.UserId);

                (await RowAsync()).GetProperty("suspendedAt").ValueKind.Should().Be(JsonValueKind.Null);
                var before = (await ReadAsync(await admin.Client.GetAsync("/admin/stats"))).GetProperty("suspendedCount").GetInt32();

                (await SuspendAsync(admin.Client, target.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

                var row = await RowAsync();
                row.GetProperty("suspendedAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
                (await ReadAsync(await admin.Client.GetAsync("/admin/stats"))).GetProperty("suspendedCount").GetInt32()
                    .Should().BeGreaterThan(before - 1).And.BeGreaterThanOrEqualTo(1);

                (await SuspendAsync(admin.Client, target.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await RowAsync()).GetProperty("suspendedAt").ValueKind.Should().Be(JsonValueKind.Null);
            }
            finally
            {
                await CleanUpAsync(admin, target);
            }
        }

        [Fact]
        public async Task ExistingAccessToken_StopsAtOnce_EvenWithTheCacheOn_AndRefreshIsRefused()
        {
            var admin = await NewAdminAsync(_cached);
            var target = await NewUserAsync(_cached);
            try
            {
                // Warm the cache with an accepted request
                (await target.Client.GetAsync("/user/info")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await SuspendAsync(admin.Client, target.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

                (await target.Client.GetAsync("/user/info")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await target.Client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // Unsuspending doesn't revive what they held; a new login does
                (await SuspendAsync(admin.Client, target.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await target.Client.GetAsync("/user/info")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await target.Client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                var client = await TestAccounts.LoginAsync(_cached.CreateClient(), target.Email);
                (await client.GetAsync("/user/info")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await CleanUpAsync(admin, target);
            }
        }

        [Fact]
        public async Task Refresh_IsRefused_ForASuspendedAccount_EvenWithAnUnrevokedToken()
        {
            var target = await NewUserAsync();
            try
            {
                // Suspended behind the API's back, so nothing was revoked
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", target.UserId));
                (await target.Client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            finally
            {
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = NULL WHERE Id = @Id", ("@Id", target.UserId));
                var client = await TestAccounts.LoginAsync(_factory.CreateClient(), target.Email);
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Suspending_EndsEverySession_AndDropsPendingChallengesAndResetLinks()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                await TestDatabase.ExecuteAsync(_factory, @"
                    INSERT INTO AccountTokens (UserId, Purpose, TokenHash, ExpiresAt)
                    VALUES (@Id, 'reset-password', 'hash-' + CAST(NEWID() AS NVARCHAR(40)), DATEADD(hour, 1, SYSUTCDATETIME()));
                    INSERT INTO LoginChallenges (UserId, TokenHash, ExpiresAt)
                    VALUES (@Id, 'hash-' + CAST(NEWID() AS NVARCHAR(40)), DATEADD(minute, 5, SYSUTCDATETIME()));", ("@Id", target.UserId));

                (await SuspendAsync(admin.Client, target.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE UserId = @Id AND RevokedAt IS NULL", ("@Id", target.UserId))).Should().Be(0);
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM LoginChallenges WHERE UserId = @Id", ("@Id", target.UserId))).Should().Be(0);
                (await TestDatabase.ScalarAsync(_factory,
                    "SELECT COUNT(*) FROM AccountTokens WHERE UserId = @Id AND Purpose = 'reset-password' AND UsedAt IS NULL", ("@Id", target.UserId))).Should().Be(0);
                (await TestDatabase.ScalarAsync(_factory, "SELECT TokensValidAfter FROM Users WHERE Id = @Id", ("@Id", target.UserId))).Should().NotBeNull();

                // Suspending twice keeps the first time
                var first = await TestDatabase.ScalarAsync(_factory, "SELECT SuspendedAt FROM Users WHERE Id = @Id", ("@Id", target.UserId));
                (await SuspendAsync(admin.Client, target.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await TestDatabase.ScalarAsync(_factory, "SELECT SuspendedAt FROM Users WHERE Id = @Id", ("@Id", target.UserId))).Should().Be(first);
            }
            finally
            {
                await CleanUpAsync(admin, target);
            }
        }

        [Fact]
        public async Task TwoFactor_LoginIsRefusedWithoutAChallenge_AndASuspensionBetweenTheStepsIsCaught()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                // Two-factor on, with a known recovery code
                string hash;
                using (var scope = _factory.Services.CreateScope())
                    hash = scope.ServiceProvider.GetRequiredService<SecretProtector>().Hash(RecoveryCode, $"recovery:{target.UserId}");
                await TestDatabase.ExecuteAsync(_factory, @"
                    UPDATE Users SET TotpEnabled = 1, TotpSecret = 'unused' WHERE Id = @Id;
                    INSERT INTO TotpRecoveryCodes (UserId, CodeHash) VALUES (@Id, @Hash);", ("@Id", target.UserId), ("@Hash", hash));

                // Password first step gives a challenge for an active account
                var challenge = (await ReadAsync(await LoginAsync(_factory, target.Email))).GetProperty("challengeToken").GetString();
                challenge.Should().NotBeNullOrEmpty();

                // Suspended between the password and the code: the second step is refused
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", target.UserId));
                var second = await _factory.CreateClient().PostAsJsonAsync("/user/login/2fa", new { challengeToken = challenge, recoveryCode = RecoveryCode });
                second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await ReadAsync(second)).GetProperty("suspended").GetBoolean().Should().BeTrue();
                second.Headers.Contains("Set-Cookie").Should().BeFalse();

                // And a new sign-in gets the suspended answer instead of a challenge
                var refused = await LoginAsync(_factory, target.Email);
                refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await ReadAsync(refused)).TryGetProperty("challengeToken", out _).Should().BeFalse();
            }
            finally
            {
                await CleanUpAsync(admin, target);
            }
        }

        [Fact]
        public async Task ForgotPassword_SendsNothingForASuspendedAccount_WithTheSameAnswer_AndResetLinksAreRefused()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            var anonymous = _factory.CreateClient();
            try
            {
                // A reset link requested before the suspension
                var active = await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = target.Email });
                var token = await WaitForResetTokenAsync(target.Email, 0);

                (await SuspendAsync(admin.Client, target.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

                var suspended = await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = target.Email });
                suspended.StatusCode.Should().Be(HttpStatusCode.OK);
                (await suspended.Content.ReadAsStringAsync()).Should().Be(await active.Content.ReadAsStringAsync());

                (await anonymous.PostAsJsonAsync("/user/reset-password", new { token, newPassword = "N3wPassword!" }))
                    .StatusCode.Should().Be(HttpStatusCode.BadRequest);

                // After unsuspending, requests send mail again. Mail is delivered in order, so
                // once it has arrived the one asked for while suspended would have too.
                (await SuspendAsync(admin.Client, target.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = target.Email })).StatusCode.Should().Be(HttpStatusCode.OK);
                await WaitForResetTokenAsync(target.Email, 1);
                _emails.Sent.Count(m => m.To.Equals(target.Email, StringComparison.OrdinalIgnoreCase) && ResetLinkPattern().IsMatch(m.Body))
                    .Should().Be(2, "one before the suspension and one after it, none while suspended");

                // The old password still works (the refused reset changed nothing)
                (await LoginAsync(_factory, target.Email)).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await CleanUpAsync(admin, target);
            }
        }

        private async Task<string> WaitForResetTokenAsync(string to, int alreadySeen)
        {
            for (var i = 0; i < 100; i++)
            {
                var match = _emails.Sent
                    .Where(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase))
                    .Select(m => ResetLinkPattern().Match(m.Body))
                    .Where(m => m.Success)
                    .Skip(alreadySeen)
                    .FirstOrDefault();
                if (match != null)
                    return match.Groups[1].Value;
                await Task.Delay(50);
            }
            throw new TimeoutException($"No reset email to {to}");
        }

        [Fact]
        public async Task ShareLinks_Return404WhileTheOwnerIsSuspended_AndWorkAgainAfterwards()
        {
            var admin = await NewAdminAsync();
            var owner = await NewUserAsync();
            try
            {
                var fileId = await TestAccounts.UploadAsync(owner.Client, "shared.txt", "hello"u8.ToArray(), "text/plain");
                var created = await owner.Client.PostAsJsonAsync("/shares", new { itemId = fileId });
                created.StatusCode.Should().Be(HttpStatusCode.OK);
                var token = (await ReadAsync(created)).GetProperty("token").GetString()!;
                var visitor = _factory.CreateClient();

                (await visitor.GetAsync($"/s/{token}")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await SuspendAsync(admin.Client, owner.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);
                var info = await visitor.GetAsync($"/s/{token}");
                info.StatusCode.Should().Be(HttpStatusCode.NotFound);
                var missing = await visitor.GetAsync($"/s/{new string('a', 43)}");
                (await info.Content.ReadAsStringAsync()).Should().Be(await missing.Content.ReadAsStringAsync(), "looks like any missing link");
                (await visitor.PostAsJsonAsync($"/s/{token}/download", new { password = (string?)null })).StatusCode.Should().Be(HttpStatusCode.NotFound);

                (await SuspendAsync(admin.Client, owner.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await visitor.GetAsync($"/s/{token}")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await visitor.PostAsJsonAsync($"/s/{token}/download", new { password = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await CleanUpAsync(admin, owner);
            }
        }

        [Fact]
        public async Task ASuspendedAdmin_LosesAdminAccess_AndRegainsItAfterUnsuspending()
        {
            var actor = await NewAdminAsync();
            var other = await NewAdminAsync();
            try
            {
                (await other.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await SuspendAsync(actor.Client, other.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

                // Their old token is dead outright, and a database check says they are no admin
                (await other.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                using (var scope = _factory.Services.CreateScope())
                    (await scope.ServiceProvider.GetRequiredService<DatabaseServices>().IsAdminAsync(other.UserId)).Should().BeFalse();

                (await SuspendAsync(actor.Client, other.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
                var again = await TestAccounts.LoginAsync(_factory.CreateClient(), other.Email);
                (await again.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await CleanUpAsync(actor, other);
            }
        }

        // ---- the last administrator (scratch database: exactly one administrator) ----

        [Fact]
        public async Task SuspendingTheLastEffectiveAdministrator_Is409_AndASuspendedAdminDoesNotCount()
        {
            await using var db = await FreshDatabase.CreateAsync();
            await using var factory = db.CreateFactory(_baseFactory);
            var first = await TestAccounts.CreateAsync(factory, "first"); // the first account is the administrator
            var second = await TestAccounts.CreateAsync(factory, "second");
            using var scope = factory.Services.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<DatabaseServices>();

            // The only administrator can't be suspended (the HTTP route can't reach this: the caller is another admin)
            var refused = await data.SetSuspendedAsync(first.UserId, true);
            refused.Success.Should().BeFalse();
            refused.StatusCode.Should().Be(409);
            refused.Message.Should().Be(DatabaseServices.LastAdminMessage);
            (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE SuspendedAt IS NOT NULL")).Should().Be(0);

            // Make a second admin, suspend the first: the second is now the only one who counts
            (await first.Client.PutAsJsonAsync($"/admin/users/{second.UserId}/admin", new { isAdmin = true })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await data.SetSuspendedAsync(first.UserId, true)).Success.Should().BeTrue();
            (await data.SetSuspendedAsync(second.UserId, true)).StatusCode.Should().Be(409);
            (await data.SetAdminAsync(second.UserId, false)).StatusCode.Should().Be(409);

            // Removing admin from the suspended one never reduces the effective admins
            (await data.SetAdminAsync(first.UserId, false)).Success.Should().BeTrue();

            // Unsuspending counts again
            (await data.SetSuspendedAsync(first.UserId, false)).Success.Should().BeTrue();
            (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE IsAdmin = 1 AND SuspendedAt IS NULL")).Should().Be(1);
        }

        [Fact]
        public async Task TwoAdministratorsSuspendingEachOtherAtOnce_LeavesExactlyOneEffective()
        {
            await using var db = await FreshDatabase.CreateAsync();
            await using var factory = db.CreateFactory(_baseFactory);
            var a = await TestAccounts.CreateAsync(factory, "a");
            var b = await TestAccounts.CreateAsync(factory, "b");
            (await a.Client.PutAsJsonAsync($"/admin/users/{b.UserId}/admin", new { isAdmin = true })).StatusCode.Should().Be(HttpStatusCode.OK);

            var results = await Task.WhenAll(
                SuspendAsync(a.Client, b.UserId),
                SuspendAsync(b.Client, a.UserId));

            results.Select(r => r.StatusCode).Should().Contain(HttpStatusCode.OK);
            (await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE IsAdmin = 1 AND SuspendedAt IS NULL")).Should().Be(1);
        }
    }
}
