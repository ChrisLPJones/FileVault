using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    // Creating and deleting accounts, setting a password, the permanent flag and avatars on the
    // admin page. Accounts are throwaway ones in the shared test database (which always has the
    // sentinel admin, so a new admin is never the only one).
    public class AdminAccountManagementTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private const string NewPassword = "N3wPassword!";
        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        private readonly WebApplicationFactory<Program> _factory;

        public AdminAccountManagementTests(WebApplicationFactory<Program> factory)
        {
            // No caching of the access-token check, so every request sees the latest database state
            _factory = TestAccounts.WithoutLoginLimit(factory)
                .WithWebHostBuilder(builder => builder.UseSetting("Auth:UserStateCacheSeconds", "0"));
        }

        private async Task<TestAccounts.Account> NewAdminAsync(WebApplicationFactory<Program>? factory = null)
        {
            factory ??= _factory;
            var account = await TestAccounts.CreateAsync(factory, "admin");
            await TestDatabase.SetAdminAsync(factory, account.UserId, true);
            return account;
        }

        private Task<TestAccounts.Account> NewUserAsync() => TestAccounts.CreateAsync(_factory, "plain");

        private static string NewEmail() => $"created_{Guid.NewGuid():N}@example.test";

        private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
            (await ReadAsync(response)).GetProperty("error").GetString()!;

        private static Task<HttpResponseMessage> CreateAsync(HttpClient admin, string email, string password = TestAccounts.Password,
            bool? isAdmin = null, bool? isPermanent = null, string firstName = "Created", string lastName = "Person", long? quotaBytes = null) =>
            admin.PostAsJsonAsync("/admin/users", new { firstName, lastName, email, password, isAdmin, isPermanent, quotaBytes });

        private static Task<HttpResponseMessage> SetPasswordAsync(HttpClient admin, string userId, string password) =>
            admin.PutAsJsonAsync($"/admin/users/{userId}/password", new { password });

        private static async Task<JsonElement> UserRowAsync(HttpClient admin, string userId)
        {
            var users = await ReadAsync(await admin.GetAsync("/admin/users"));
            return users.EnumerateArray().Single(u => u.GetProperty("id").GetString() == userId);
        }

        private async Task<HttpStatusCode> LoginStatusAsync(string email, string password) =>
            (await _factory.CreateClient().PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password })).StatusCode;

        private string StorageRoot => _factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("StorageRoot")!;

        private static Task<HttpResponseMessage> UploadAvatarAsync(HttpClient client) =>
            client.PutAsync("/user/avatar", new MultipartFormDataContent
            {
                { new ByteArrayContent([.. PngSignature, .. new byte[200]]) { Headers = { ContentType = MediaTypeHeaderValue.Parse("image/png") } }, "avatar", "me.png" }
            });

        // ---- create ----

        [Fact]
        public async Task NonAdmins_AreForbidden_OnTheNewEndpoints()
        {
            var user = await NewUserAsync();
            try
            {
                var other = Guid.NewGuid().ToString();
                (await CreateAsync(user.Client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await user.Client.DeleteAsync($"/admin/users/{other}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await SetPasswordAsync(user.Client, other, NewPassword)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await user.Client.PutAsJsonAsync($"/admin/users/{other}/permanent", new { isPermanent = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await user.Client.GetAsync($"/admin/users/{other}/avatar")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            finally
            {
                await user.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Create_MakesAnAccountThatCanSignInStraightAway_WithStarterFolders()
        {
            var admin = await NewAdminAsync();
            var email = NewEmail();
            try
            {
                var response = await CreateAsync(admin.Client, email.ToUpperInvariant());
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                var id = (await ReadAsync(response)).GetProperty("id").GetString()!;

                var created = await TestAccounts.LoginAsync(_factory.CreateClient(), email);
                (await TestAccounts.ListAsync(created)).Select(f => f.GetProperty("name").GetString())
                    .Should().BeEquivalentTo(["Documents", "Pictures", "Music", "Videos"]);

                var row = await UserRowAsync(admin.Client, id);
                row.GetProperty("email").GetString().Should().Be(email);
                row.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
                row.GetProperty("isPermanent").GetBoolean().Should().BeFalse();

                await created.DeleteAsync("/user");
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Create_AdministratorIsPermanent_EvenWithoutThePermanentFlag()
        {
            var admin = await NewAdminAsync();
            try
            {
                var id = (await ReadAsync(await CreateAsync(admin.Client, NewEmail(), isAdmin: true, isPermanent: false))).GetProperty("id").GetString()!;
                var row = await UserRowAsync(admin.Client, id);
                row.GetProperty("isAdmin").GetBoolean().Should().BeTrue();
                row.GetProperty("isPermanent").GetBoolean().Should().BeTrue();
                await admin.Client.DeleteAsync($"/admin/users/{id}");
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Create_WithQuota_StoresIt_AndWithoutUsesTheDefault()
        {
            var admin = await NewAdminAsync();
            try
            {
                var withQuota = (await ReadAsync(await CreateAsync(admin.Client, NewEmail(), quotaBytes: 5_000_000))).GetProperty("id").GetString()!;
                var row = await UserRowAsync(admin.Client, withQuota);
                row.GetProperty("quota").GetInt64().Should().Be(5_000_000);
                row.GetProperty("quotaOverride").GetInt64().Should().Be(5_000_000);

                var without = (await ReadAsync(await CreateAsync(admin.Client, NewEmail()))).GetProperty("id").GetString()!;
                (await UserRowAsync(admin.Client, without)).GetProperty("quotaOverride").ValueKind.Should().Be(JsonValueKind.Null);

                await admin.Client.DeleteAsync($"/admin/users/{withQuota}");
                await admin.Client.DeleteAsync($"/admin/users/{without}");
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Theory]
        [InlineData(-1L)]
        [InlineData((1L << 50) + 1)]
        public async Task Create_WithQuotaOutOfRange_IsRefused_AndCreatesNothing(long quota)
        {
            var admin = await NewAdminAsync();
            var email = NewEmail();
            try
            {
                (await CreateAsync(admin.Client, email, quotaBytes: quota)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await LoginStatusAsync(email, TestAccounts.Password)).Should().Be(HttpStatusCode.Unauthorized);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Create_CanSetAdminAndPermanent()
        {
            var admin = await NewAdminAsync();
            var email = NewEmail();
            try
            {
                var id = (await ReadAsync(await CreateAsync(admin.Client, email, isAdmin: true, isPermanent: true))).GetProperty("id").GetString()!;

                var row = await UserRowAsync(admin.Client, id);
                row.GetProperty("isAdmin").GetBoolean().Should().BeTrue();
                row.GetProperty("isPermanent").GetBoolean().Should().BeTrue();

                // Administrators are always permanent, and can't be deleted from the admin page
                (await admin.Client.DeleteAsync($"/admin/users/{id}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await admin.Client.PutAsJsonAsync($"/admin/users/{id}/admin", new { isAdmin = false })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await admin.Client.DeleteAsync($"/admin/users/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Create_RejectsWhatRegisterRejects()
        {
            var admin = await NewAdminAsync();
            try
            {
                (await CreateAsync(admin.Client, NewEmail(), "short")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await CreateAsync(admin.Client, NewEmail(), "alllowercase1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await CreateAsync(admin.Client, "not-an-email")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await CreateAsync(admin.Client, NewEmail(), firstName: "")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await CreateAsync(admin.Client, NewEmail(), firstName: new string('a', 51))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

                var malformed = await admin.Client.PostAsync("/admin/users", new StringContent("{nope", System.Text.Encoding.UTF8, "application/json"));
                malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Create_WithAnExistingEmail_Is409_NotCaseSensitive()
        {
            var admin = await NewAdminAsync();
            var email = NewEmail();
            try
            {
                var id = (await ReadAsync(await CreateAsync(admin.Client, email))).GetProperty("id").GetString()!;

                var duplicate = await CreateAsync(admin.Client, email.ToUpperInvariant());
                duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await ErrorAsync(duplicate)).Should().Be("Email already exists");
                (await CreateAsync(admin.Client, admin.Email)).StatusCode.Should().Be(HttpStatusCode.Conflict);

                await admin.Client.DeleteAsync($"/admin/users/{id}");
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        // ---- delete ----

        [Fact]
        public async Task Delete_RemovesTheAccountAndEverythingItStored()
        {
            var admin = await NewAdminAsync();
            var victim = await NewUserAsync();
            try
            {
                var fileId = await TestAccounts.UploadAsync(victim.Client, "doomed.txt", [1, 2, 3, 4]);
                (await UploadAvatarAsync(victim.Client)).StatusCode.Should().Be(HttpStatusCode.OK);
                var blob = Path.Combine(StorageRoot, fileId);
                var avatar = Path.Combine(StorageRoot, "avatars", victim.UserId);
                var tmpDir = Path.Combine(StorageRoot, "tmp", victim.UserId, Guid.NewGuid().ToString());
                Directory.CreateDirectory(tmpDir);
                await File.WriteAllBytesAsync(Path.Combine(tmpDir, "0.part"), [1]);
                File.Exists(blob).Should().BeTrue();
                File.Exists(avatar).Should().BeTrue();

                var response = await admin.Client.DeleteAsync($"/admin/users/{victim.UserId}");

                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                File.Exists(blob).Should().BeFalse("the encrypted file is removed");
                File.Exists(avatar).Should().BeFalse("the avatar is removed");
                Directory.Exists(Path.Combine(StorageRoot, "tmp", victim.UserId)).Should().BeFalse("unfinished upload chunks are removed");
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM Users WHERE Id = @Id", ("@Id", victim.UserId))).Should().Be(0);
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM Files WHERE UserId = @Id", ("@Id", victim.UserId))).Should().Be(0);
                (await LoginStatusAsync(victim.Email, TestAccounts.Password)).Should().Be(HttpStatusCode.Unauthorized);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Delete_StopsTheDeletedUsersAccessToken_Immediately()
        {
            var admin = await NewAdminAsync();
            var victim = await NewUserAsync();
            try
            {
                (await victim.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await admin.Client.DeleteAsync($"/admin/users/{victim.UserId}")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await victim.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Delete_OfYourOwnAccount_Is400_AndOfAnUnknownOne_Is404()
        {
            var admin = await NewAdminAsync();
            try
            {
                var self = await admin.Client.DeleteAsync($"/admin/users/{admin.UserId}");
                self.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await UserRowAsync(admin.Client, admin.UserId)).GetProperty("email").GetString().Should().Be(admin.Email);

                (await admin.Client.DeleteAsync($"/admin/users/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await admin.Client.DeleteAsync("/admin/users/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task AdminOnAdminActions_AreRefused_ForPasswordSuspendDeleteAndPermanent()
        {
            var first = await NewAdminAsync();
            var second = await NewAdminAsync();
            try
            {
                var c = first.Client;
                var message = "Administrator accounts can't be changed from the admin page";

                var password = await c.PutAsJsonAsync($"/admin/users/{second.UserId}/password", new { password = "Another-Passw0rd!x" });
                password.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await ReadAsync(password)).GetProperty("error").GetString().Should().Be(message);

                var suspend = await c.PutAsJsonAsync($"/admin/users/{second.UserId}/suspended", new { suspended = true });
                suspend.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await ReadAsync(suspend)).GetProperty("error").GetString().Should().Be(message);

                var delete = await c.DeleteAsync($"/admin/users/{second.UserId}");
                delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await ReadAsync(delete)).GetProperty("error").GetString().Should().Be(message);

                var clear = await c.PutAsJsonAsync($"/admin/users/{second.UserId}/permanent", new { isPermanent = false });
                clear.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await ReadAsync(clear)).GetProperty("error").GetString().Should().Be("Administrator accounts are always permanent");
                (await c.PutAsJsonAsync($"/admin/users/{second.UserId}/permanent", new { isPermanent = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM Users WHERE Id = @Id AND SuspendedAt IS NULL", ("@Id", second.UserId))).Should().Be(1);
            }
            finally
            {
                await first.Client.DeleteAsync("/user");
                await second.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task GrantingAdmin_AlsoMakesTheAccountPermanent()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("isPermanent").GetBoolean().Should().BeFalse();
                (await admin.Client.PutAsJsonAsync($"/admin/users/{target.UserId}/admin", new { isAdmin = true })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("isPermanent").GetBoolean().Should().BeTrue();
            }
            finally
            {
                await admin.Client.PutAsJsonAsync($"/admin/users/{target.UserId}/admin", new { isAdmin = false });
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task DeleteUser_StillRemovesTheOwnAccountCompletely()
        {
            var user = await NewUserAsync();
            var fileId = await TestAccounts.UploadAsync(user.Client, "mine.txt", [9, 9, 9]);
            await UploadAvatarAsync(user.Client);
            var tmp = Path.Combine(StorageRoot, "tmp", user.UserId, "abc");
            Directory.CreateDirectory(tmp);

            (await user.Client.DeleteAsync("/user")).StatusCode.Should().Be(HttpStatusCode.OK);

            File.Exists(Path.Combine(StorageRoot, fileId)).Should().BeFalse();
            File.Exists(Path.Combine(StorageRoot, "avatars", user.UserId)).Should().BeFalse();
            Directory.Exists(Path.Combine(StorageRoot, "tmp", user.UserId)).Should().BeFalse();
            (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM Users WHERE Id = @Id", ("@Id", user.UserId))).Should().Be(0);
        }

        // ---- set password ----

        [Fact]
        public async Task SetPassword_ChangesTheLogin_AndEndsEverySession()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                // A second session for the same user, with its own refresh cookie
                var other = _factory.CreateClient();
                await TestAccounts.LoginAsync(other, target.Email);
                (await other.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);

                var response = await SetPasswordAsync(admin.Client, target.UserId, NewPassword);

                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                (await target.Client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await other.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await LoginStatusAsync(target.Email, TestAccounts.Password)).Should().Be(HttpStatusCode.Unauthorized);
                (await LoginStatusAsync(target.Email, NewPassword)).Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task SetPassword_StopsTheUsersCurrentAccessToken_Immediately_ButNotANewOne()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                (await target.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await SetPasswordAsync(admin.Client, target.UserId, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);

                (await target.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // Signing in straight afterwards (even within the same second) gives a working token
                var again = await TestAccounts.LoginAsync(_factory.CreateClient(), target.Email, NewPassword);
                (await again.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task SetPassword_WithTheCacheOn_StillStopsTheTokenAtOnce()
        {
            // The default 30-second cache: changes made through the API evict it
            var cached = _factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:UserStateCacheSeconds", "60"));
            var admin = await NewAdminAsync(cached);
            var target = await TestAccounts.CreateAsync(cached, "cached");
            var doomed = await TestAccounts.CreateAsync(cached, "cacheddoomed");
            try
            {
                // Prime the cache for both users
                (await target.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await doomed.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await SetPasswordAsync(admin.Client, target.UserId, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await admin.Client.DeleteAsync($"/admin/users/{doomed.UserId}")).StatusCode.Should().Be(HttpStatusCode.OK);

                (await target.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await doomed.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task SetPassword_EnforcesThePolicy_AndRefusesYourOwnAccount()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                (await SetPasswordAsync(admin.Client, target.UserId, "short")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SetPasswordAsync(admin.Client, target.UserId, "alllowercase1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SetPasswordAsync(admin.Client, target.UserId, new string('A', 40) + new string('a', 40) + "1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SetPasswordAsync(admin.Client, admin.UserId, NewPassword)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SetPasswordAsync(admin.Client, Guid.NewGuid().ToString(), NewPassword)).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await SetPasswordAsync(admin.Client, "not-a-guid", NewPassword)).StatusCode.Should().Be(HttpStatusCode.NotFound);

                // Nothing changed for the target or the admin
                (await LoginStatusAsync(target.Email, TestAccounts.Password)).Should().Be(HttpStatusCode.OK);
                (await LoginStatusAsync(admin.Email, TestAccounts.Password)).Should().Be(HttpStatusCode.OK);
                (await target.Client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task SetPassword_DropsPendingResetLinksAndLoginChallenges()
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

                (await SetPasswordAsync(admin.Client, target.UserId, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);

                (await TestDatabase.ScalarAsync(_factory,
                    "SELECT COUNT(*) FROM AccountTokens WHERE UserId = @Id AND Purpose = 'reset-password' AND UsedAt IS NULL", ("@Id", target.UserId))).Should().Be(0);
                (await TestDatabase.ScalarAsync(_factory,
                    "SELECT COUNT(*) FROM LoginChallenges WHERE UserId = @Id", ("@Id", target.UserId))).Should().Be(0);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        // ---- permanent ----

        [Fact]
        public async Task Permanent_CanBeSetAndCleared()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("isPermanent").GetBoolean().Should().BeFalse();

                (await admin.Client.PutAsJsonAsync($"/admin/users/{target.UserId}/permanent", new { isPermanent = true })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("isPermanent").GetBoolean().Should().BeTrue();

                (await admin.Client.PutAsJsonAsync($"/admin/users/{target.UserId}/permanent", new { isPermanent = false })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("isPermanent").GetBoolean().Should().BeFalse();

                (await admin.Client.PutAsJsonAsync($"/admin/users/{target.UserId}/permanent", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await admin.Client.PutAsJsonAsync($"/admin/users/{Guid.NewGuid()}/permanent", new { isPermanent = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        // ---- avatars ----

        [Fact]
        public async Task Avatar_IsServedToAdmins_PrivatelyAndNotCached_Or404WithoutOne()
        {
            var admin = await NewAdminAsync();
            var target = await NewUserAsync();
            try
            {
                (await admin.Client.GetAsync($"/admin/users/{target.UserId}/avatar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("avatarUpdatedAt").ValueKind.Should().Be(JsonValueKind.Null);

                (await UploadAvatarAsync(target.Client)).StatusCode.Should().Be(HttpStatusCode.OK);

                var response = await admin.Client.GetAsync($"/admin/users/{target.UserId}/avatar");
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
                response.Headers.CacheControl!.Private.Should().BeTrue();
                response.Headers.CacheControl.NoCache.Should().BeTrue();
                (await response.Content.ReadAsByteArrayAsync()).Take(8).Should().Equal(PngSignature);
                (await UserRowAsync(admin.Client, target.UserId)).GetProperty("avatarUpdatedAt").ValueKind.Should().Be(JsonValueKind.String);

                // Another user's avatar is not available to non-admins through this route
                (await target.Client.GetAsync($"/admin/users/{admin.UserId}/avatar")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await admin.Client.GetAsync($"/admin/users/{Guid.NewGuid()}/avatar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await admin.Client.GetAsync("/admin/users/not-a-guid/avatar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            finally
            {
                await admin.Client.DeleteAsync($"/admin/users/{target.UserId}");
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Stats_SaysWhichAdministratorIsAsking()
        {
            var admin = await NewAdminAsync();
            try
            {
                var stats = await ReadAsync(await admin.Client.GetAsync("/admin/stats"));
                stats.GetProperty("currentUserId").GetString().Should().Be(admin.UserId);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        // ---- audit log ----

        private sealed class CapturingProvider : ILoggerProvider
        {
            public ConcurrentQueue<string> Entries { get; } = new();
            public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
            public void Dispose() { }

            private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    entries.Enqueue($"{category}|{formatter(state, exception)}");
            }
        }

        [Fact]
        public async Task EveryAdminMutation_IsLoggedWithIds_NeverPasswordsOrEmails()
        {
            var logs = new CapturingProvider();
            var logged = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
            var admin = await NewAdminAsync(logged);
            var email = NewEmail();
            const string secret = "Sup3rSecretPassw0rd";
            try
            {
                var id = (await ReadAsync(await CreateAsync(admin.Client, email, secret))).GetProperty("id").GetString()!;
                await SetPasswordAsync(admin.Client, id, secret + "x");
                await admin.Client.PutAsJsonAsync($"/admin/users/{id}/permanent", new { isPermanent = true });
                await admin.Client.DeleteAsync($"/admin/users/{id}");

                var audit = logs.Entries.Where(e => e.StartsWith("Backend.AdminAudit|")).ToList();
                audit.Should().HaveCount(4);
                audit.Should().OnlyContain(e => e.Contains(admin.UserId) && e.Contains(id));
                audit.Should().Contain(e => e.Contains("create-user"));
                audit.Should().Contain(e => e.Contains("set-password"));
                audit.Should().Contain(e => e.Contains("mark-permanent"));
                audit.Should().Contain(e => e.Contains("delete-user"));
                logs.Entries.Should().NotContain(e => e.Contains(secret) || e.Contains(email));
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }
    }
}
