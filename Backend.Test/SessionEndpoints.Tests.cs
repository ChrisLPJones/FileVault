using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    public class SessionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Password = "TestPassw0rd";
        private const string ChromeOnWindows =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        private const string SafariOnIPhone =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly List<WebApplicationFactory<Program>> _factories = new();
        private readonly List<(HttpClient client, string token)> _usersToDelete = new();

        public SessionEndpointsTests(WebApplicationFactory<Program> factory)
        {
            _baseFactory = factory;
        }

        private HttpClient NewClient(params (string key, string value)[] settings)
        {
            var all = new Dictionary<string, string?> { ["RateLimiting:auth:PermitLimit"] = "1000" };
            foreach (var (key, value) in settings)
                all[key] = value;

            var factory = _baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(all)));
            _factories.Add(factory);
            return factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        }

        private record Session(string Token, string Cookie);

        private static string NewEmail() => $"sess_{Guid.NewGuid():N}@example.test";

        private async Task<string> RegisterAsync(HttpClient client)
        {
            var email = NewEmail();
            (await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Session", LastName = "Tester", Email = email, Password = Password }))
                .EnsureSuccessStatusCode();
            await TestDatabase.MarkEmailVerifiedAsync(_baseFactory, email);
            return email;
        }

        private async Task<Session> LoginAsync(HttpClient client, string email, string userAgent = ChromeOnWindows, string? cookie = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/user/login")
            {
                Content = JsonContent.Create(new LoginModel { Email = email, Password = Password })
            };
            request.Headers.UserAgent.ParseAdd(userAgent);
            if (cookie != null)
                request.Headers.Add("Cookie", cookie);

            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var session = new Session(await TokenAsync(response), RefreshCookie(response));
            _usersToDelete.Add((client, session.Token));
            return session;
        }

        private static async Task<string> TokenAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("success").GetString()!;
        }

        private static string RefreshCookie(HttpResponseMessage response) =>
            response.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith("fv_refresh=") && !c.StartsWith("fv_refresh=;"))
                .Split(';')[0];

        // An authorised request that also carries the session's refresh cookie, as the browser sends it to /user/*
        private static HttpRequestMessage Request(HttpMethod method, string url, Session session, bool withCookie = true)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            if (withCookie)
                request.Headers.Add("Cookie", session.Cookie);
            return request;
        }

        private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            request.Headers.Add("Cookie", cookie);
            return await client.SendAsync(request);
        }

        private static async Task<List<JsonElement>> ListAsync(HttpClient client, Session session, bool withCookie = true)
        {
            var response = await client.SendAsync(Request(HttpMethod.Get, "/user/sessions", session, withCookie));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }



        [Theory]
        [InlineData(ChromeOnWindows, "Chrome on Windows")]
        [InlineData(SafariOnIPhone, "Safari on iPhone")]
        [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on macOS")]
        [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Edge on Windows")]
        [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) FileVault/1.0.0 Chrome/128.0.0.0 Electron/32.0.0 Safari/537.36", "FileVault desktop app on Linux")]
        [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36", "Chrome on Android")]
        [InlineData("curl/8.4.0", "curl")]
        [InlineData("", "Unknown device")]
        [InlineData(null, "Unknown device")]
        public void DeviceDescription_NamesBrowserAndOs(string? userAgent, string expected)
        {
            DeviceDescription.FromUserAgent(userAgent).Should().Be(expected);
        }

        [Fact]
        public async Task Sessions_ListEachLogin_WithDevice_AndMarkThisOne()
        {
            var client = NewClient();
            var email = await RegisterAsync(client);
            var laptop = await LoginAsync(client, email, ChromeOnWindows);
            var phone = await LoginAsync(client, email, SafariOnIPhone);

            var fromLaptop = await ListAsync(client, laptop);
            fromLaptop.Should().HaveCount(2);
            fromLaptop[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue("the current session comes first");
            fromLaptop[0].GetProperty("device").GetString().Should().Be("Chrome on Windows");
            fromLaptop[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();
            fromLaptop[1].GetProperty("device").GetString().Should().Be("Safari on iPhone");
            fromLaptop[0].GetProperty("ipAddress").ValueKind.Should().NotBe(JsonValueKind.Undefined);
            fromLaptop[0].GetProperty("createdAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
            fromLaptop[0].GetProperty("lastActiveAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

            var fromPhone = await ListAsync(client, phone);
            fromPhone.Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("device").GetString()
                .Should().Be("Safari on iPhone");

            // Without the refresh cookie no session is marked as this one
            (await ListAsync(client, laptop, withCookie: false)).Should().OnlyContain(s => !s.GetProperty("isCurrent").GetBoolean());
        }

        [Fact]
        public async Task Refresh_StaysInTheSameSession()
        {
            var client = NewClient();
            var email = await RegisterAsync(client);
            var login = await LoginAsync(client, email);
            var id = (await ListAsync(client, login)).Single().GetProperty("id").GetString();

            var refresh = await RefreshAsync(client, login.Cookie);
            refresh.StatusCode.Should().Be(HttpStatusCode.OK);
            var refreshed = new Session(await TokenAsync(refresh), RefreshCookie(refresh));

            var sessions = await ListAsync(client, refreshed);
            sessions.Should().ContainSingle();
            sessions[0].GetProperty("id").GetString().Should().Be(id);
            sessions[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task SigningOutASession_EndsItsNextRefresh_AndNothingElse()
        {
            // Grace 0: any replay of a rotated token would count as theft
            var client = NewClient(("Jwt:RefreshReuseGraceSeconds", "0"));
            var email = await RegisterAsync(client);
            var laptop = await LoginAsync(client, email, ChromeOnWindows);
            var phone = await LoginAsync(client, email, SafariOnIPhone);

            var phoneId = (await ListAsync(client, laptop)).Single(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetString();
            var signOut = await client.SendAsync(Request(HttpMethod.Delete, $"/user/sessions/{phoneId}", laptop));
            signOut.StatusCode.Should().Be(HttpStatusCode.OK);
            signOut.Headers.Contains("Set-Cookie").Should().BeFalse("the laptop's own cookie is untouched");

            // The phone is logged out at its next refresh, and stays out
            (await RefreshAsync(client, phone.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshAsync(client, phone.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // That isn't mistaken for a stolen token: the laptop keeps working
            (await RefreshAsync(client, laptop.Cookie)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ListAsync(client, laptop)).Should().ContainSingle();

            // Signing out again finds nothing
            (await client.SendAsync(Request(HttpMethod.Delete, $"/user/sessions/{phoneId}", laptop)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task SigningOutThisSession_ClearsTheCookie()
        {
            var client = NewClient();
            var email = await RegisterAsync(client);
            var login = await LoginAsync(client, email);
            var id = (await ListAsync(client, login)).Single().GetProperty("id").GetString();

            var signOut = await client.SendAsync(Request(HttpMethod.Delete, $"/user/sessions/{id}", login));
            signOut.StatusCode.Should().Be(HttpStatusCode.OK);
            signOut.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("fv_refresh=;"));
            (await RefreshAsync(client, login.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task AnotherUsersSession_CantBeSignedOut()
        {
            var client = NewClient();
            var victim = await LoginAsync(client, await RegisterAsync(client));
            var attacker = await LoginAsync(client, await RegisterAsync(client));
            var victimId = (await ListAsync(client, victim)).Single().GetProperty("id").GetString();

            (await ListAsync(client, attacker)).Should().NotContain(s => s.GetProperty("id").GetString() == victimId);

            var attempt = await client.SendAsync(Request(HttpMethod.Delete, $"/user/sessions/{victimId}", attacker));
            attempt.StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await RefreshAsync(client, victim.Cookie)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task SessionIds_ThatAreMadeUp_AreNotFound()
        {
            var client = NewClient();
            var login = await LoginAsync(client, await RegisterAsync(client));

            (await client.SendAsync(Request(HttpMethod.Delete, $"/user/sessions/{Guid.NewGuid()}", login)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.SendAsync(Request(HttpMethod.Delete, "/user/sessions/not-a-guid", login)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task RevokeOthers_KeepsOnlyThisSession()
        {
            var client = NewClient();
            var email = await RegisterAsync(client);
            var a = await LoginAsync(client, email);
            var b = await LoginAsync(client, email, SafariOnIPhone);
            var c = await LoginAsync(client, email);

            (await client.SendAsync(Request(HttpMethod.Post, "/user/sessions/revoke-others", b)))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            (await RefreshAsync(client, a.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshAsync(client, c.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var refresh = await RefreshAsync(client, b.Cookie);
            refresh.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessions = await ListAsync(client, new Session(await TokenAsync(refresh), RefreshCookie(refresh)));
            sessions.Should().ContainSingle().Which.GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task LoggedOutSessions_AreNotListed()
        {
            var client = NewClient();
            var email = await RegisterAsync(client);
            var stays = await LoginAsync(client, email);
            var leaves = await LoginAsync(client, email);

            var logout = new HttpRequestMessage(HttpMethod.Post, "/user/logout");
            logout.Headers.Add("Cookie", leaves.Cookie);
            (await client.SendAsync(logout)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ListAsync(client, stays)).Should().ContainSingle();
        }

        [Fact]
        public async Task LoggingInAgain_RetiresThisBrowsersOldSession()
        {
            var client = NewClient();
            var email = await RegisterAsync(client);
            var first = await LoginAsync(client, email);

            // Same browser (it still sends its refresh cookie) logs in again
            var second = await LoginAsync(client, email, cookie: first.Cookie);

            (await ListAsync(client, second)).Should().ContainSingle();
            (await RefreshAsync(client, first.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshAsync(client, second.Cookie)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task SessionEndpoints_NeedLogin()
        {
            var client = NewClient();

            (await client.GetAsync("/user/sessions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.DeleteAsync($"/user/sessions/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsync("/user/sessions/revoke-others", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var (client, token) in _usersToDelete)
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, "/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                await client.SendAsync(request);
            }

            foreach (var factory in _factories)
                await factory.DisposeAsync();
        }
    }
}
