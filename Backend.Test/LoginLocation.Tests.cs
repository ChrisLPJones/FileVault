using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    // Where the last login came from: the address is recorded when a session is issued, and the
    // admin page resolves its country from the local GeoLite2 test database at display time.
    // 2.125.160.216 is in MaxMind's GeoLite2-Country-Test.mmdb (Apache-2.0/MIT test data) as GB.
    public class LoginLocationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Password = "TestPassw0rd";
        private const string GbAddress = "2.125.160.216";

        internal static readonly string TestDatabasePath =
            Path.Combine(AppContext.BaseDirectory, "TestData", "GeoLite2-Country-Test.mmdb");

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly List<WebApplicationFactory<Program>> _factories = new();
        private readonly List<(HttpClient client, string token)> _usersToDelete = new();

        public LoginLocationTests(WebApplicationFactory<Program> factory)
        {
            _baseFactory = factory;
        }

        private WebApplicationFactory<Program> NewApp(TestClock? clock = null, bool geoIp = true, bool forwarded = true)
        {
            var settings = new Dictionary<string, string?>
            {
                ["RateLimiting:auth:PermitLimit"] = "1000",
                ["RateLimiting:two-factor:PermitLimit"] = "1000",
                ["ForwardedHeaders:Enabled"] = forwarded ? "true" : "false",
                ["GeoIp:DatabasePath"] = geoIp ? TestDatabasePath : "",
            };
            var factory = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                if (clock != null)
                    builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
            });
            _factories.Add(factory);
            return factory;
        }

        private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
            factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string? forwardedFor = GbAddress)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/user/login")
            {
                Content = JsonContent.Create(new LoginModel { Email = email, Password = Password }),
            };
            if (forwardedFor != null)
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            return client.SendAsync(request);
        }

        // Registers an account; confirms its email unless told not to
        private async Task<(string email, string userId)> RegisterAsync(
            WebApplicationFactory<Program> factory, HttpClient client, bool verified = true)
        {
            var email = $"loc_{Guid.NewGuid():N}@example.test";
            (await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Loc", LastName = "User", Email = email, Password = Password }))
                .EnsureSuccessStatusCode();
            if (verified)
                await TestDatabase.MarkEmailVerifiedAsync(factory, email);
            var userId = ((string)(await TestDatabase.ScalarAsync(factory, "SELECT CAST(Id AS NVARCHAR(36)) FROM Users WHERE Email = @e", ("@e", email)))!).ToLowerInvariant();
            return (email, userId);
        }

        private static async Task<(string? ip, bool hasLastLogin)> StoredLoginAsync(WebApplicationFactory<Program> factory, string userId)
        {
            var ip = await TestDatabase.ScalarAsync(factory, "SELECT LastLoginIp FROM Users WHERE Id = @id", ("@id", userId));
            var last = await TestDatabase.ScalarAsync(factory, "SELECT LastLogin FROM Users WHERE Id = @id", ("@id", userId));
            return ((string?)ip, last != null);
        }

        private async Task<string> TokenAsync(HttpResponseMessage login, HttpClient client)
        {
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var token = (await JsonAsync(login)).GetProperty("success").GetString()!;
            _usersToDelete.Add((client, token));
            return token;
        }

        private static HttpRequestMessage Authed(HttpMethod method, string url, string token, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
                request.Content = JsonContent.Create(body);
            return request;
        }

        [Fact]
        public async Task Login_RecordsTheForwardedAddress_AndAdminListShowsItsCountry()
        {
            var factory = NewApp();
            var client = NewClient(factory);
            var (email, userId) = await RegisterAsync(factory, client);
            await TokenAsync(await LoginAsync(client, email), client);

            (await StoredLoginAsync(factory, userId)).Should().Be((GbAddress, true));

            var (adminEmail, adminId) = await RegisterAsync(factory, client);
            await TestDatabase.SetAdminAsync(factory, adminId, true);
            var adminToken = await TokenAsync(await LoginAsync(client, adminEmail, "10.1.2.3"), client);

            var users = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/admin/users", adminToken)));
            var row = users.EnumerateArray().Single(u => u.GetProperty("id").GetString() == userId);
            row.GetProperty("lastLoginIp").GetString().Should().Be(GbAddress);
            row.GetProperty("lastLoginCountryCode").GetString().Should().Be("GB");
            row.GetProperty("lastLoginCountry").GetString().Should().Be("United Kingdom");

            // A private address is kept but has no country
            var adminRow = users.EnumerateArray().Single(u => u.GetProperty("id").GetString() == adminId);
            adminRow.GetProperty("lastLoginIp").GetString().Should().Be("10.1.2.3");
            adminRow.GetProperty("lastLoginCountry").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task AdminList_HasNoCountry_WhenNoGeoIpDatabaseIsConfigured()
        {
            var factory = NewApp(geoIp: false);
            var client = NewClient(factory);
            var (email, userId) = await RegisterAsync(factory, client);
            await TokenAsync(await LoginAsync(client, email), client);
            await TestDatabase.SetAdminAsync(factory, userId, true);

            var users = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/admin/users",
                _usersToDelete.Last().token)));
            var row = users.EnumerateArray().Single(u => u.GetProperty("id").GetString() == userId);
            row.GetProperty("lastLoginIp").GetString().Should().Be(GbAddress);
            row.GetProperty("lastLoginCountry").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task ForwardedAddress_IsIgnored_WhenForwardedHeadersAreOff()
        {
            var factory = NewApp(forwarded: false);
            var client = NewClient(factory);
            var (email, userId) = await RegisterAsync(factory, client);
            await TokenAsync(await LoginAsync(client, email), client);

            var (ip, hasLastLogin) = await StoredLoginAsync(factory, userId);
            ip.Should().NotBe(GbAddress);
            hasLastLogin.Should().BeTrue();
        }

        [Fact]
        public async Task UnconfirmedEmailLogin_DoesNotCountAsALogin()
        {
            var factory = NewApp();
            var client = NewClient(factory);
            var (email, userId) = await RegisterAsync(factory, client, verified: false);
            _unverified.Add((factory, userId));

            (await LoginAsync(client, email)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await StoredLoginAsync(factory, userId)).Should().Be(((string?)null, false));
        }

        [Fact]
        public async Task WrongPassword_DoesNotRecordAnything()
        {
            var factory = NewApp();
            var client = NewClient(factory);
            var (email, userId) = await RegisterAsync(factory, client);
            _unverified.Add((factory, userId));

            var request = new HttpRequestMessage(HttpMethod.Post, "/user/login")
            {
                Content = JsonContent.Create(new LoginModel { Email = email, Password = "Wrong-Passw0rd" }),
            };
            request.Headers.Add("X-Forwarded-For", GbAddress);
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await StoredLoginAsync(factory, userId)).Should().Be(((string?)null, false));
        }

        [Fact]
        public async Task TwoFactorLogin_RecordsTheAddressOnlyAfterTheSecondStep()
        {
            var clock = new TestClock();
            var factory = NewApp(clock);
            var client = NewClient(factory);
            var (email, userId) = await RegisterAsync(factory, client);
            var token = await TokenAsync(await LoginAsync(client, email, "10.9.9.9"), client);

            // Turn two-factor on
            var setup = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/setup", token, new { password = Password })));
            var secret = FromBase32(setup.GetProperty("secret").GetString()!);
            var enable = await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/enable", token,
                new { code = Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow())) }));
            enable.StatusCode.Should().Be(HttpStatusCode.OK);
            clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

            // The password alone starts no session, so nothing is recorded
            var first = await LoginAsync(client, email, GbAddress);
            var challenge = (await JsonAsync(first)).GetProperty("challengeToken").GetString()!;
            (await StoredLoginAsync(factory, userId)).ip.Should().Be("10.9.9.9");

            var finish = new HttpRequestMessage(HttpMethod.Post, "/user/login/2fa")
            {
                Content = JsonContent.Create(new { challengeToken = challenge, code = Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow())) }),
            };
            finish.Headers.Add("X-Forwarded-For", GbAddress);
            (await client.SendAsync(finish)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await StoredLoginAsync(factory, userId)).ip.Should().Be(GbAddress);
        }

        // ---- GeoIpService itself ----

        private static GeoIpService Geo(string? path, TimeSpan? recheck = null) =>
            new(path, NullLogger<GeoIpService>.Instance, recheck);

        [Fact]
        public void GeoIp_ResolvesAKnownAddress_WithTheTestDatabase()
        {
            var geo = Geo(TestDatabasePath);
            geo.IsAvailable.Should().BeTrue();
            geo.TryCountry(GbAddress).Should().Be(("GB", "United Kingdom"));
            geo.TryCountry($"::ffff:{GbAddress}").Should().Be(("GB", "United Kingdom"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void GeoIp_IsDisabled_WithoutAPath(string? path)
        {
            var geo = Geo(path);
            geo.IsAvailable.Should().BeFalse();
            geo.TryCountry(GbAddress).Should().BeNull();
        }

        [Fact]
        public void GeoIp_ReturnsNull_WhenTheFileIsMissing_WithoutThrowing()
        {
            var geo = Geo(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.mmdb"));
            geo.TryCountry(GbAddress).Should().BeNull();
            geo.IsAvailable.Should().BeFalse();
        }

        [Fact]
        public void GeoIp_ReturnsNull_WhenTheFileIsNotADatabase()
        {
            var path = Path.Combine(Path.GetTempPath(), $"junk_{Guid.NewGuid():N}.mmdb");
            File.WriteAllText(path, "not a database");
            try
            {
                Geo(path).TryCountry(GbAddress).Should().BeNull();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("10.0.0.1")]
        [InlineData("172.16.5.5")]
        [InlineData("192.168.1.20")]
        [InlineData("169.254.1.1")]
        [InlineData("100.64.0.1")]
        [InlineData("fe80::1")]
        [InlineData("fd00::1")]
        [InlineData("::ffff:192.168.0.9")]
        [InlineData("not an address")]
        [InlineData(null)]
        public void GeoIp_SkipsPrivateAndInvalidAddresses(string? address)
        {
            Geo(TestDatabasePath).TryCountry(address).Should().BeNull();
        }

        [Fact]
        public void GeoIp_PicksUpADatabaseThatAppearsOrChanges()
        {
            var path = Path.Combine(Path.GetTempPath(), $"geo_{Guid.NewGuid():N}.mmdb");
            try
            {
                var geo = Geo(path, recheck: TimeSpan.Zero);
                geo.TryCountry(GbAddress).Should().BeNull();

                File.Copy(TestDatabasePath, path);
                geo.TryCountry(GbAddress).Should().Be(("GB", "United Kingdom"));

                // Replaced by something unreadable: the working copy keeps serving until a good file arrives
                File.WriteAllText(path, "garbage");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
                geo.TryCountry(GbAddress).Should().Be(("GB", "United Kingdom"));

                File.Copy(TestDatabasePath, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(10));
                geo.TryCountry(GbAddress).Should().Be(("GB", "United Kingdom"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private readonly List<(WebApplicationFactory<Program> factory, string userId)> _unverified = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var (client, token) in _usersToDelete.Where(u => u.token != ""))
                await client.SendAsync(Authed(HttpMethod.Delete, "/user", token));

            foreach (var (factory, userId) in _unverified)
                await TestDatabase.ExecuteAsync(factory, "DELETE FROM Files WHERE UserId = @id; DELETE FROM Users WHERE Id = @id", ("@id", userId));

            foreach (var factory in _factories)
                await factory.DisposeAsync();
        }

        private static byte[] FromBase32(string value)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var c in value.TrimEnd('='))
            {
                buffer = (buffer << 5) | alphabet.IndexOf(char.ToUpperInvariant(c));
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)(buffer >> (bits - 8)));
                    bits -= 8;
                }
            }
            return bytes.ToArray();
        }
    }
}
