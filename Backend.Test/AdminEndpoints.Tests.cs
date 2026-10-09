using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Backend.Test
{
    public class AdminEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        // This class's admin, listed in Admin:Emails (in capitals: matching ignores case)
        private readonly string _adminEmail = $"admin_{Guid.NewGuid():N}@example.test";
        private readonly WebApplicationFactory<Program> _factory;

        public AdminEndpointsTests(WebApplicationFactory<Program> factory)
        {
            _factory = TestAccounts.WithoutLoginLimit(factory)
                .WithWebHostBuilder(builder => builder.UseSetting("Admin:Emails:0", _adminEmail.ToUpperInvariant()));
        }

        private Task<TestAccounts.Account> NewAdminAsync() => TestAccounts.CreateAsync(_factory, "admin", _adminEmail);
        private Task<TestAccounts.Account> NewUserAsync() => TestAccounts.CreateAsync(_factory, "plain");

        private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        private static async Task<JsonElement> UserRowAsync(HttpClient admin, string userId) =>
            (await GetJsonAsync(admin, "/admin/users")).EnumerateArray().Single(u => u.GetProperty("id").GetString() == userId);

        private static Task<HttpResponseMessage> SetQuotaAsync(HttpClient client, string userId, long? quotaBytes) =>
            client.PatchAsync($"/admin/users/{userId}/quota", JsonContent.Create(new { quotaBytes }));

        [Fact]
        public async Task NonAdmins_GetForbidden_OnEveryAdminEndpoint()
        {
            var user = await NewUserAsync();
            try
            {
                (await GetJsonAsync(user.Client, "/admin/me")).GetProperty("isAdmin").GetBoolean().Should().BeFalse();

                (await user.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await user.Client.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await SetQuotaAsync(user.Client, user.UserId, 1)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

                // And their quota wasn't changed by trying
                using var usage = JsonDocument.Parse(await user.Client.GetStringAsync("/user/usage"));
                usage.RootElement.GetProperty("quota").GetInt64().Should().Be(FileServices.DefaultQuotaBytes);
            }
            finally
            {
                await user.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task AdminEndpoints_RequireLogin()
        {
            var anonymous = _factory.CreateClient();
            (await anonymous.GetAsync("/admin/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SetQuotaAsync(anonymous, Guid.NewGuid().ToString(), 1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task ListedEmail_BecomesAdmin_AndSeesEveryUsersUsage()
        {
            var admin = await NewAdminAsync();
            var user = await NewUserAsync();
            try
            {
                (await GetJsonAsync(admin.Client, "/admin/me")).GetProperty("isAdmin").GetBoolean().Should().BeTrue();

                await TestAccounts.UploadAsync(user.Client, "a.txt", new byte[1000], "text/plain");
                await TestAccounts.UploadAsync(user.Client, "b.txt", new byte[234], "text/plain");

                var row = await UserRowAsync(admin.Client, user.UserId);
                row.GetProperty("email").GetString().Should().Be(user.Email);
                row.GetProperty("firstName").GetString().Should().Be("plain");
                row.GetProperty("bytesUsed").GetInt64().Should().Be(1234);
                row.GetProperty("fileCount").GetInt32().Should().Be(2); // the default folders don't count
                row.GetProperty("quota").GetInt64().Should().Be(FileServices.DefaultQuotaBytes);
                row.GetProperty("quotaOverride").ValueKind.Should().Be(JsonValueKind.Null);
                row.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
                row.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.String);
                row.GetProperty("lastLogin").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));

                (await UserRowAsync(admin.Client, admin.UserId)).GetProperty("isAdmin").GetBoolean().Should().BeTrue();
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
                await user.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Admin_CanChangeAndResetAQuota_WhichIsEnforced()
        {
            var admin = await NewAdminAsync();
            var user = await NewUserAsync();
            try
            {
                (await SetQuotaAsync(admin.Client, user.UserId, 500)).StatusCode.Should().Be(HttpStatusCode.OK);

                var row = await UserRowAsync(admin.Client, user.UserId);
                row.GetProperty("quota").GetInt64().Should().Be(500);
                row.GetProperty("quotaOverride").GetInt64().Should().Be(500);

                using (var usage = JsonDocument.Parse(await user.Client.GetStringAsync("/user/usage")))
                    usage.RootElement.GetProperty("quota").GetInt64().Should().Be(500);

                var tooBig = new MultipartFormDataContent { { new ByteArrayContent(new byte[600]), "file", "big.bin" } };
                (await user.Client.PostAsync("/upload", tooBig)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

                (await SetQuotaAsync(admin.Client, user.UserId, null)).StatusCode.Should().Be(HttpStatusCode.OK);
                row = await UserRowAsync(admin.Client, user.UserId);
                row.GetProperty("quota").GetInt64().Should().Be(FileServices.DefaultQuotaBytes);
                row.GetProperty("quotaOverride").ValueKind.Should().Be(JsonValueKind.Null);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
                await user.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task QuotaChanges_AreValidated()
        {
            var admin = await NewAdminAsync();
            try
            {
                (await SetQuotaAsync(admin.Client, admin.UserId, -1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SetQuotaAsync(admin.Client, admin.UserId, (1L << 50) + 1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await SetQuotaAsync(admin.Client, Guid.NewGuid().ToString(), 1000)).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await SetQuotaAsync(admin.Client, "not-a-guid", 1000)).StatusCode.Should().Be(HttpStatusCode.NotFound);

                var noBody = new HttpRequestMessage(HttpMethod.Patch, $"/admin/users/{admin.UserId}/quota")
                {
                    Content = new StringContent("not json", Encoding.UTF8, "application/json")
                };
                (await admin.Client.SendAsync(noBody)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

                (await SetQuotaAsync(admin.Client, admin.UserId, 0)).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Stats_CountUsersFilesAndStorage()
        {
            var admin = await NewAdminAsync();
            var user = await NewUserAsync();
            try
            {
                await TestAccounts.UploadAsync(user.Client, "data.bin", new byte[4096]);

                var stats = await GetJsonAsync(admin.Client, "/admin/stats");
                stats.GetProperty("userCount").GetInt32().Should().BeGreaterThanOrEqualTo(2);
                stats.GetProperty("adminCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);
                stats.GetProperty("fileCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);
                stats.GetProperty("folderCount").GetInt32().Should().BeGreaterThanOrEqualTo(8); // two sets of default folders
                stats.GetProperty("totalStoredBytes").GetInt64().Should().BeGreaterThanOrEqualTo(4096);
                stats.GetProperty("defaultQuotaBytes").GetInt64().Should().Be(FileServices.DefaultQuotaBytes);
                stats.GetProperty("storageBytesOnDisk").GetInt64().Should().BeGreaterThanOrEqualTo(4096);
                stats.GetProperty("diskTotalBytes").GetInt64().Should().BeGreaterThan(0);
                stats.GetProperty("diskFreeBytes").GetInt64().Should().BeGreaterThan(0);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
                await user.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task AdminRights_AreCheckedInTheDatabase_NotJustTheToken()
        {
            var admin = await NewAdminAsync();
            try
            {
                (await admin.Client.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.OK);

                // Revoke in the database; the same (still valid) access token no longer works
                using (var scope = _factory.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<DatabaseServices>().SyncAdminsAsync([], admin.UserId);

                (await admin.Client.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await GetJsonAsync(admin.Client, "/admin/me")).GetProperty("isAdmin").GetBoolean().Should().BeFalse();

                // Logging in again applies Admin:Emails, which still lists them
                var again = await TestAccounts.LoginAsync(_factory.CreateClient(), _adminEmail);
                (await again.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task AdminEmails_CanBeOneCommaSeparatedValue_LikeTheDockerEnvironmentVariable()
        {
            var email = $"envadmin_{Guid.NewGuid():N}@example.test";
            var envFactory = TestAccounts.WithoutLoginLimit(new WebApplicationFactory<Program>())
                .WithWebHostBuilder(builder => builder.UseSetting("Admin:Emails", $"someone@elsewhere.test, {email} ;"));
            var admin = await TestAccounts.CreateAsync(envFactory, "envadmin", email);
            var other = await TestAccounts.CreateAsync(envFactory, "envplain");
            try
            {
                (await admin.Client.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await other.Client.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            finally
            {
                await admin.Client.DeleteAsync("/user");
                await other.Client.DeleteAsync("/user");
                await envFactory.DisposeAsync();
            }
        }

        [Fact]
        public async Task WithoutAdminEmails_NobodyIsMadeAdmin()
        {
            var plainFactory = TestAccounts.WithoutLoginLimit(new WebApplicationFactory<Program>());
            var user = await TestAccounts.CreateAsync(plainFactory, "noadmin");
            try
            {
                (await GetJsonAsync(user.Client, "/admin/me")).GetProperty("isAdmin").GetBoolean().Should().BeFalse();
                (await user.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            finally
            {
                await user.Client.DeleteAsync("/user");
                await plainFactory.DisposeAsync();
            }
        }
    }
}
