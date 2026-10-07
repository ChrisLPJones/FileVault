using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    public class AuthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Password = "TestPassw0rd";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly WebApplicationFactory<Program> _factory;
        private readonly List<WebApplicationFactory<Program>> _extraFactories = new();
        private readonly List<(HttpClient client, string token)> _usersToDelete = new();

        public AuthEndpointsTests(WebApplicationFactory<Program> factory)
        {
            _baseFactory = factory;
            // These tests log in many times; keep the login rate limit out of the way
            _factory = WithSettings(("RateLimiting:auth:PermitLimit", "1000"));
        }

        private WebApplicationFactory<Program> WithSettings(params (string key, string value)[] settings)
        {
            var factory = _baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(settings.ToDictionary(s => s.key, s => (string?)s.value))));
            _extraFactories.Add(factory);
            return factory;
        }

        // Clients don't store cookies automatically so tests can replay old refresh tokens
        private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
            factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        private static string NewEmail() => $"auth_{Guid.NewGuid():N}@example.test";

        private static async Task RegisterAsync(HttpClient client, string email, string password = Password)
        {
            var response = await client.PostAsJsonAsync("/user/register",
                new UserModel { Username = $"u_{Guid.NewGuid():N}"[..20], Email = email, Password = password });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        private static async Task<(string accessToken, string refreshCookie, HttpResponseMessage response)> LoginAsync(
            HttpClient client, string email, string password = Password)
        {
            var response = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await ReadTokenAsync(response), RefreshCookie(response), response);
        }

        private async Task<(HttpClient client, string accessToken, string refreshCookie)> NewUserSessionAsync(
            WebApplicationFactory<Program>? factory = null)
        {
            var client = NewClient(factory ?? _factory);
            var email = NewEmail();
            await RegisterAsync(client, email);
            var (token, cookie, _) = await LoginAsync(client, email);
            _usersToDelete.Add((client, token));
            return (client, token, cookie);
        }

        private static async Task<string> ReadTokenAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty(json.RootElement.TryGetProperty("token", out _) ? "token" : "success").GetString()!;
        }

        // "fv_refresh=<value>" from the response's Set-Cookie header
        private static string RefreshCookie(HttpResponseMessage response) =>
            response.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith("fv_refresh=") && !c.StartsWith("fv_refresh=;"))
                .Split(';')[0];

        private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }

        private static Task<HttpResponseMessage> GetInfoAsync(HttpClient client, string accessToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/user/info");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client.SendAsync(request);
        }

        [Fact]
        public async Task Login_SetsHttpOnlyRefreshCookie_ThatIssuesNewTokens()
        {
            var client = NewClient(_factory);
            var email = NewEmail();
            await RegisterAsync(client, email);
            var (token, cookie, response) = await LoginAsync(client, email);
            _usersToDelete.Add((client, token));

            var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).ToLowerInvariant();
            setCookie.Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/user");

            var refresh = await RefreshAsync(client, cookie);
            refresh.StatusCode.Should().Be(HttpStatusCode.OK);
            var newToken = await ReadTokenAsync(refresh);
            RefreshCookie(refresh).Should().NotBe(cookie);

            (await GetInfoAsync(client, newToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Login_WithWrongPassword_ReturnsGenericError()
        {
            var (client, _, _) = await NewUserSessionAsync();

            var wrongPassword = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = NewEmail(), Password = "Wr0ngPassword" });
            wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await wrongPassword.Content.ReadAsStringAsync()).Should().Contain("Invalid email/username or password");
        }

        [Fact]
        public async Task RefreshToken_ReusedAfterGrace_RevokesEverySession()
        {
            var factory = WithSettings(
                ("RateLimiting:auth:PermitLimit", "1000"),
                ("Jwt:RefreshReuseGraceSeconds", "0"));
            var (client, _, first) = await NewUserSessionAsync(factory);

            var refresh = await RefreshAsync(client, first);
            refresh.StatusCode.Should().Be(HttpStatusCode.OK);
            var second = RefreshCookie(refresh);

            // Replaying the used token looks like theft...
            (await RefreshAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // ...so the legitimate newer token is revoked too
            (await RefreshAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task RefreshToken_ReusedWithinGrace_IsRejectedWithoutEndingOtherSessions()
        {
            var (client, _, first) = await NewUserSessionAsync();

            var refresh = await RefreshAsync(client, first);
            var second = RefreshCookie(refresh);

            // e.g. two tabs refreshing at the same moment
            (await RefreshAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Refresh_WithoutOrWithUnknownCookie_IsUnauthorized()
        {
            var client = NewClient(_factory);

            (await client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshAsync(client, "fv_refresh=not-a-real-token")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Logout_RevokesRefreshToken_AndClearsCookie()
        {
            var (client, _, cookie) = await NewUserSessionAsync();

            var request = new HttpRequestMessage(HttpMethod.Post, "/user/logout");
            request.Headers.Add("Cookie", cookie);
            var logout = await client.SendAsync(request);

            logout.StatusCode.Should().Be(HttpStatusCode.OK);
            logout.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("fv_refresh=;"));
            (await RefreshAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Theory]
        [InlineData("short1A", "at least 8 characters")]
        [InlineData("alllowercase1", "uppercase")]
        [InlineData("ALLUPPERCASE1", "lowercase")]
        [InlineData("NoNumbersHere", "number")]
        public async Task Register_RejectsWeakPasswords(string password, string expectedMessage)
        {
            var client = NewClient(_factory);

            var response = await client.PostAsJsonAsync("/user/register",
                new UserModel { Username = "weakpassuser", Email = NewEmail(), Password = password });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain(expectedMessage);
        }

        [Theory]
        [InlineData("ab", "valid@example.test", "Username")]
        [InlineData("validname", "not-an-email", "Email is invalid")]
        public async Task Register_RejectsInvalidUsernameOrEmail(string username, string email, string expectedMessage)
        {
            var client = NewClient(_factory);

            var response = await client.PostAsJsonAsync("/user/register",
                new UserModel { Username = username, Email = email, Password = Password });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain(expectedMessage);
        }

        [Fact]
        public async Task PasswordChange_EndsOtherSessions_AndKeepsThisOne()
        {
            var client = NewClient(_factory);
            var email = NewEmail();
            await RegisterAsync(client, email);
            var (tokenA, cookieA, _) = await LoginAsync(client, email);
            var (_, cookieB, _) = await LoginAsync(client, email);

            var update = new HttpRequestMessage(HttpMethod.Post, "/user/password")
            {
                Content = JsonContent.Create(new { currentPassword = Password, newPassword = "Changed1Pass" })
            };
            update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
            update.Headers.Add("Cookie", cookieA);
            var response = await client.SendAsync(update);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var newToken = await ReadTokenAsync(response);
            var newCookie = RefreshCookie(response);
            _usersToDelete.Add((client, newToken));

            (await RefreshAsync(client, cookieB)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshAsync(client, newCookie)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Old password no longer works, new one does
            (await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            await LoginAsync(client, email, "Changed1Pass");
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
        public async Task PasswordChange_WithWrongCurrentPassword_OrWeakNewPassword_IsRejected()
        {
            var (client, token, _) = await NewUserSessionAsync();

            var wrongCurrent = await client.SendAsync(Authed(HttpMethod.Post, "/user/password", token,
                new { currentPassword = "Wr0ngCurrent", newPassword = "Another1Pass" }));
            wrongCurrent.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await wrongCurrent.Content.ReadAsStringAsync()).Should().Contain("Current password is incorrect");

            var weakNew = await client.SendAsync(Authed(HttpMethod.Post, "/user/password", token,
                new { currentPassword = Password, newPassword = "weak" }));
            weakNew.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task ProfileUpdate_RejectsEmailOrUsernameUsedByAnotherAccount()
        {
            var otherClient = NewClient(_factory);
            var otherEmail = NewEmail();
            await RegisterAsync(otherClient, otherEmail);
            var (otherToken, _, _) = await LoginAsync(otherClient, otherEmail);
            _usersToDelete.Add((otherClient, otherToken));

            var (client, token, _) = await NewUserSessionAsync();

            var emailTaken = await client.SendAsync(Authed(HttpMethod.Patch, "/user/profile", token,
                new { username = "freshname_" + Guid.NewGuid().ToString("N")[..8], email = otherEmail.ToUpperInvariant() }));
            emailTaken.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await emailTaken.Content.ReadAsStringAsync()).Should().Contain("Email already exists");
        }

        [Fact]
        public async Task Usage_ReportsStoredBytesQuotaAndUploadLimit()
        {
            var (client, token, _) = await NewUserSessionAsync();

            var upload = Authed(HttpMethod.Post, "/upload", token);
            upload.Content = new MultipartFormDataContent { { new ByteArrayContent(new byte[1234]), "file", "a.bin" } };
            (await client.SendAsync(upload)).StatusCode.Should().Be(HttpStatusCode.OK);

            var usage = await client.SendAsync(Authed(HttpMethod.Get, "/user/usage", token));
            using var json = JsonDocument.Parse(await usage.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("used").GetInt64().Should().Be(1234);
            json.RootElement.GetProperty("quota").GetInt64().Should().Be(1L * 1024 * 1024 * 1024);
            json.RootElement.GetProperty("maxUploadBytes").GetInt64().Should().Be(100L * 1024 * 1024);
        }

        [Fact]
        public async Task UploadAndCopy_BeyondQuotaOrUploadLimit_AreRejected()
        {
            var factory = WithSettings(
                ("RateLimiting:auth:PermitLimit", "1000"),
                ("Storage:DefaultQuotaBytes", "1000"),
                ("Storage:MaxUploadBytes", "800"));
            var (client, token, _) = await NewUserSessionAsync(factory);

            HttpRequestMessage Upload(int bytes, string name)
            {
                var request = Authed(HttpMethod.Post, "/upload", token);
                request.Content = new MultipartFormDataContent { { new ByteArrayContent(new byte[bytes]), "file", name } };
                return request;
            }

            var tooBig = await client.SendAsync(Upload(900, "big.bin"));
            tooBig.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await tooBig.Content.ReadAsStringAsync()).Should().Contain("upload limit");

            (await client.SendAsync(Upload(600, "first.bin"))).StatusCode.Should().Be(HttpStatusCode.OK);

            var overQuota = await client.SendAsync(Upload(600, "second.bin"));
            overQuota.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await overQuota.Content.ReadAsStringAsync()).Should().Contain("Not enough storage space");

            // Copying the 600-byte file would also go over the 1000-byte quota
            var files = await client.SendAsync(Authed(HttpMethod.Get, "/files", token));
            using var json = JsonDocument.Parse(await files.Content.ReadAsStringAsync());
            var fileId = json.RootElement.EnumerateArray().First(e => !e.GetProperty("isDirectory").GetBoolean()).GetProperty("_id").GetString();
            var copy = await client.SendAsync(Authed(HttpMethod.Post, "/copy", token, new { sourceIds = new[] { fileId } }));
            copy.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        }

        [Fact]
        public async Task DeleteAccount_ClearsRefreshCookie_AndEndsSession()
        {
            var client = NewClient(_factory);
            var email = NewEmail();
            await RegisterAsync(client, email);
            var (token, cookie, _) = await LoginAsync(client, email);

            var delete = await client.SendAsync(Authed(HttpMethod.Delete, "/user", token));
            delete.StatusCode.Should().Be(HttpStatusCode.OK);
            delete.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("fv_refresh=;"));

            (await RefreshAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task UnexpectedError_ReturnsJson500_WithCorsHeaders()
        {
            var (_, token, _) = await NewUserSessionAsync();

            // Same app, but the database is unreachable
            var brokenDb = WithSettings(("ConnectionStrings:DefaultConnection",
                "Server=127.0.0.1,1;Database=SecureVaultDb;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True"));
            var client = NewClient(brokenDb);

            var request = Authed(HttpMethod.Get, "/user/info", token);
            request.Headers.Add("Origin", "http://localhost:5173");
            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"An internal error has occurred\"}");
            response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle("http://localhost:5173");
        }

        [Fact]
        public async Task NewAccount_StartsWithDefaultFolders()
        {
            var (client, token, _) = await NewUserSessionAsync();

            var files = await client.SendAsync(Authed(HttpMethod.Get, "/files", token));
            using var json = JsonDocument.Parse(await files.Content.ReadAsStringAsync());
            var items = json.RootElement.EnumerateArray()
                .Select(e => (path: e.GetProperty("path").GetString(), isDirectory: e.GetProperty("isDirectory").GetBoolean()))
                .ToList();

            items.Should().BeEquivalentTo(new[]
            {
                ("/Documents", true), ("/Pictures", true), ("/Music", true), ("/Videos", true)
            });
        }

        [Fact]
        public async Task Login_WorksWithEmailOrUsername_IgnoringCase()
        {
            var client = NewClient(_factory);
            var email = NewEmail();
            var username = $"Name_{Guid.NewGuid():N}"[..20];
            (await client.PostAsJsonAsync("/user/register", new UserModel { Username = username, Email = email, Password = Password }))
                .EnsureSuccessStatusCode();

            async Task<HttpStatusCode> LoginAs(object body) => (await client.PostAsJsonAsync("/user/login", body)).StatusCode;

            (await LoginAs(new { login = username, password = Password })).Should().Be(HttpStatusCode.OK);
            (await LoginAs(new { login = username.ToUpperInvariant(), password = Password })).Should().Be(HttpStatusCode.OK);
            (await LoginAs(new { login = email, password = Password })).Should().Be(HttpStatusCode.OK);
            (await LoginAs(new { email, password = Password })).Should().Be(HttpStatusCode.OK); // older clients
            (await LoginAs(new { login = username, password = "Wr0ngPassword" })).Should().Be(HttpStatusCode.Unauthorized);

            var (token, _, _) = await LoginAsync(client, email);
            _usersToDelete.Add((client, token));
        }

        [Fact]
        public async Task Register_RejectsUsernamesContainingAt()
        {
            var client = NewClient(_factory);

            var response = await client.PostAsJsonAsync("/user/register",
                new UserModel { Username = "someone@home", Email = NewEmail(), Password = Password });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("can't contain @");
        }

        [Fact]
        public async Task Login_IsRateLimitedPerClient()
        {
            var client = NewClient(WithSettings(("RateLimiting:auth:PermitLimit", "3")));
            var attempt = () => client.PostAsJsonAsync("/user/login", new LoginModel { Email = NewEmail(), Password = "Wr0ngPassword" });

            for (var i = 0; i < 3; i++)
                (await attempt()).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var limited = await attempt();
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            limited.Headers.Contains("Retry-After").Should().BeTrue();
            (await limited.Content.ReadAsStringAsync()).Should().Contain("Too many attempts");
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

            foreach (var factory in _extraFactories)
                await factory.DisposeAsync();
        }
    }
}
