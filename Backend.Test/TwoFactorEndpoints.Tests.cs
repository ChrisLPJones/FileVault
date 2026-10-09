using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    public class TwoFactorEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Password = "TestPassw0rd";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly List<WebApplicationFactory<Program>> _factories = new();
        private readonly List<(HttpClient client, string token)> _usersToDelete = new();

        public TwoFactorEndpointsTests(WebApplicationFactory<Program> factory)
        {
            _baseFactory = factory;
        }

        // An app whose authenticator-code clock the test controls, with rate limits out of the way
        private (HttpClient client, TestClock clock) NewApp(params (string key, string value)[] settings)
        {
            var clock = new TestClock();
            var all = new Dictionary<string, string?>
            {
                ["RateLimiting:auth:PermitLimit"] = "1000",
                ["RateLimiting:two-factor:PermitLimit"] = "1000",
            };
            foreach (var (key, value) in settings)
                all[key] = value;

            var factory = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(all));
                builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
            });
            _factories.Add(factory);

            // Cookies are handled by hand so tests can check and replay them
            return (factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }), clock);
        }

        private static string NewEmail() => $"tfa_{Guid.NewGuid():N}@example.test";

        private static HttpRequestMessage Authed(HttpMethod method, string url, string token, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
                request.Content = JsonContent.Create(body);
            return request;
        }

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        private static bool SetsRefreshCookie(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(c => c.StartsWith("fv_refresh=") && !c.StartsWith("fv_refresh=;"));

        private async Task<(string email, string token)> NewUserAsync(HttpClient client)
        {
            var email = NewEmail();
            (await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Two", LastName = "Factor", Email = email, Password = Password }))
                .EnsureSuccessStatusCode();

            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var token = (await JsonAsync(login)).GetProperty("success").GetString()!;
            _usersToDelete.Add((client, token));
            return (email, token);
        }

        // Start setup and decode the secret the app would scan
        private static async Task<byte[]> StartSetupAsync(HttpClient client, string token)
        {
            var setup = await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/setup", token, new { password = Password }));
            setup.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await JsonAsync(setup);

            var secret = json.GetProperty("secret").GetString()!;
            json.GetProperty("otpAuthUri").GetString().Should()
                .StartWith("otpauth://totp/FileVault%3A")
                .And.Contain($"secret={secret}")
                .And.Contain("issuer=FileVault");
            return FromBase32(secret);
        }

        // A user with 2FA on; returns the secret and recovery codes
        private async Task<(string email, string token, byte[] secret, List<string> recoveryCodes)> NewTwoFactorUserAsync(
            HttpClient client, TestClock clock)
        {
            var (email, token) = await NewUserAsync(client);
            var secret = await StartSetupAsync(client, token);

            var enable = await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/enable", token, new { code = CodeNow(secret, clock) }));
            enable.StatusCode.Should().Be(HttpStatusCode.OK);
            var codes = (await JsonAsync(enable)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();

            // The confirming code's time step is used up; move on to the next one
            clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
            return (email, token, secret, codes);
        }

        private static string CodeNow(byte[] secret, TestClock clock) =>
            Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow()));

        private static async Task<string> StartLoginAsync(HttpClient client, string email)
        {
            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            SetsRefreshCookie(login).Should().BeFalse("the password alone mustn't start a session");

            var json = await JsonAsync(login);
            json.GetProperty("twoFactorRequired").GetBoolean().Should().BeTrue();
            json.TryGetProperty("success", out _).Should().BeFalse("no access token before the second step");
            return json.GetProperty("challengeToken").GetString()!;
        }

        private static Task<HttpResponseMessage> FinishLoginAsync(HttpClient client, string challenge, string? code = null, string? recoveryCode = null) =>
            client.PostAsJsonAsync("/user/login/2fa", new { challengeToken = challenge, code, recoveryCode });

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



        [Theory]
        // RFC 6238 appendix B (SHA-1), last six digits
        [InlineData(59, "287082")]
        [InlineData(1111111109, "081804")]
        [InlineData(1111111111, "050471")]
        [InlineData(1234567890, "005924")]
        [InlineData(2000000000, "279037")]
        public void Totp_MatchesRfc6238TestVectors(long unixTime, string expected)
        {
            var secret = "12345678901234567890"u8.ToArray();
            var now = DateTimeOffset.FromUnixTimeSeconds(unixTime);

            Totp.Code(secret, Totp.CurrentStep(now)).Should().Be(expected);
            Totp.Match(secret, expected, now).Should().Be(Totp.CurrentStep(now));
        }

        [Fact]
        public void Totp_AcceptsOneStepOfDrift_AndRejectsMore()
        {
            var secret = Totp.NewSecret();
            var now = DateTimeOffset.UtcNow;
            var step = Totp.CurrentStep(now);

            Totp.Match(secret, Totp.Code(secret, step - 1), now).Should().Be(step - 1);
            Totp.Match(secret, Totp.Code(secret, step + 1), now).Should().Be(step + 1);
            Totp.Match(secret, Totp.Code(secret, step - 2), now).Should().BeNull();
            Totp.Match(secret, Totp.Code(secret, step + 2), now).Should().BeNull();
            Totp.Match(secret, "12345", now).Should().BeNull();
            Totp.Match(secret, "abcdef", now).Should().BeNull();
            Totp.Match(secret, null, now).Should().BeNull();
        }

        [Fact]
        public void Base32_MatchesRfc4648()
        {
            Totp.ToBase32("foobar"u8).Should().Be("MZXW6YTBOI");
            Totp.ToBase32("f"u8).Should().Be("MY");
            FromBase32(Totp.ToBase32(new byte[] { 0, 255, 1, 254, 2, 253, 3, 252, 4, 251 }))
                .Should().Equal(0, 255, 1, 254, 2, 253, 3, 252, 4, 251);
        }



        [Fact]
        public async Task Login_WithoutTwoFactor_StillReturnsTokensDirectly()
        {
            var (client, _) = NewApp();
            var (email, token) = await NewUserAsync(client);

            var status = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/user/2fa", token)));
            status.GetProperty("enabled").GetBoolean().Should().BeFalse();

            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            SetsRefreshCookie(login).Should().BeTrue();
            var json = await JsonAsync(login);
            json.GetProperty("success").GetString().Should().NotBeNullOrEmpty();
            json.TryGetProperty("twoFactorRequired", out _).Should().BeFalse();
        }

        [Fact]
        public async Task Setup_NeedsThePassword_AndAValidCodeToTurnOn()
        {
            var (client, clock) = NewApp();
            var (_, token) = await NewUserAsync(client);

            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/setup", token, new { password = "Wr0ngPassword" })))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var secret = await StartSetupAsync(client, token);

            // A wrong code doesn't turn it on
            var wrong = await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/enable", token,
                new { code = Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow()) + 5) }));
            wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/user/2fa", token))))
                .GetProperty("enabled").GetBoolean().Should().BeFalse();

            var enable = await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/enable", token, new { code = CodeNow(secret, clock) }));
            enable.StatusCode.Should().Be(HttpStatusCode.OK);
            var codes = (await JsonAsync(enable)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()).ToList();
            codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
            codes.Should().AllSatisfy(c => c.Should().MatchRegex("^[a-z2-7]{4}-[a-z2-7]{4}-[a-z2-7]{4}$"));

            var status = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/user/2fa", token)));
            status.GetProperty("enabled").GetBoolean().Should().BeTrue();
            status.GetProperty("recoveryCodesLeft").GetInt32().Should().Be(10);

            // Already on: setup can't replace the secret, and enabling again does nothing
            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/setup", token, new { password = Password })))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/enable", token, new { code = CodeNow(secret, clock) })))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Log in again as an existing user; returns the access token and "fv_refresh=..." cookie
        private static async Task<(string token, string cookie)> LoginWithCookieAsync(HttpClient client, string email)
        {
            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).Split(';')[0];
            return ((await JsonAsync(login)).GetProperty("success").GetString()!, cookie);
        }

        private static async Task<HttpStatusCode> RefreshStatusAsync(HttpClient client, string cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            request.Headers.Add("Cookie", cookie);
            return (await client.SendAsync(request)).StatusCode;
        }

        private static async Task<int> SessionCountAsync(HttpClient client, string token)
        {
            var sessions = await client.SendAsync(Authed(HttpMethod.Get, "/user/sessions", token));
            sessions.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await JsonAsync(sessions)).GetArrayLength();
        }

        [Fact]
        public async Task TurningOn_SignsOutOtherSessions_AndKeepsThisOne()
        {
            var (client, clock) = NewApp();
            var (email, _) = await NewUserAsync(client);
            var (token, thisDevice) = await LoginWithCookieAsync(client, email);
            var (_, laptop) = await LoginWithCookieAsync(client, email);
            var (_, phone) = await LoginWithCookieAsync(client, email);
            var before = await SessionCountAsync(client, token);
            before.Should().BeGreaterThanOrEqualTo(4, "the first login plus the three here");

            var secret = await StartSetupAsync(client, token);
            var enable = Authed(HttpMethod.Post, "/user/2fa/enable", token, new { code = CodeNow(secret, clock) });
            enable.Headers.Add("Cookie", thisDevice);
            var response = await client.SendAsync(enable);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(response)).GetProperty("otherSessionsSignedOut").GetInt32().Should().Be(before - 1);

            (await RefreshStatusAsync(client, laptop)).Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshStatusAsync(client, phone)).Should().Be(HttpStatusCode.Unauthorized);
            (await RefreshStatusAsync(client, thisDevice)).Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task TurningOn_WithAWrongCode_SignsNothingOut()
        {
            var (client, clock) = NewApp();
            var (email, _) = await NewUserAsync(client);
            var (token, thisDevice) = await LoginWithCookieAsync(client, email);
            var (_, laptop) = await LoginWithCookieAsync(client, email);
            var before = await SessionCountAsync(client, token);

            var secret = await StartSetupAsync(client, token);
            var enable = Authed(HttpMethod.Post, "/user/2fa/enable", token,
                new { code = Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow()) + 5) });
            enable.Headers.Add("Cookie", thisDevice);
            (await client.SendAsync(enable)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            (await SessionCountAsync(client, token)).Should().Be(before);
            (await RefreshStatusAsync(client, laptop)).Should().Be(HttpStatusCode.OK);
            (await RefreshStatusAsync(client, thisDevice)).Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Login_WithTwoFactor_NeedsTheCode_ThenWorksLikeANormalLogin()
        {
            var (client, clock) = NewApp();
            var (email, _, secret, _) = await NewTwoFactorUserAsync(client, clock);

            var challenge = await StartLoginAsync(client, email);

            var wrong = await FinishLoginAsync(client, challenge, Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow()) + 3));
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            SetsRefreshCookie(wrong).Should().BeFalse();

            var done = await FinishLoginAsync(client, challenge, CodeNow(secret, clock));
            done.StatusCode.Should().Be(HttpStatusCode.OK);
            SetsRefreshCookie(done).Should().BeTrue();
            var token = (await JsonAsync(done)).GetProperty("success").GetString()!;

            (await client.SendAsync(Authed(HttpMethod.Get, "/user/info", token))).StatusCode.Should().Be(HttpStatusCode.OK);

            // The refresh cookie from the second step works like any other
            var cookie = done.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).Split(';')[0];
            var refresh = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            refresh.Headers.Add("Cookie", cookie);
            (await client.SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Login_WithTwoFactor_AndWrongPassword_LooksLikeAnyFailedLogin()
        {
            var (client, clock) = NewApp();
            var (email, _, _, _) = await NewTwoFactorUserAsync(client, clock);
            var (plainEmail, _) = await NewUserAsync(client);

            // Nothing about 2FA is revealed until the password is right
            var withTwoFactor = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = "Wr0ngPassword" });
            var withoutTwoFactor = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = plainEmail, Password = "Wr0ngPassword" });

            withTwoFactor.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await withTwoFactor.Content.ReadAsStringAsync()).Should().Be(await withoutTwoFactor.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Code_CantBeReplayed()
        {
            var (client, clock) = NewApp();
            var (email, _, secret, _) = await NewTwoFactorUserAsync(client, clock);
            var code = CodeNow(secret, clock);

            (await FinishLoginAsync(client, await StartLoginAsync(client, email), code)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Same code, fresh challenge: rejected even though it's still within its 30 seconds
            var replay = await FinishLoginAsync(client, await StartLoginAsync(client, email), code);
            replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await replay.Content.ReadAsStringAsync()).Should().Contain("code isn't valid");

            // An earlier step's code is rejected too, once a later one has been used
            var earlier = Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow()) - 1);
            (await FinishLoginAsync(client, await StartLoginAsync(client, email), earlier)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // The next step's code works
            clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
            (await FinishLoginAsync(client, await StartLoginAsync(client, email), CodeNow(secret, clock))).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Challenge_IsSingleUse()
        {
            var (client, clock) = NewApp();
            var (email, _, secret, _) = await NewTwoFactorUserAsync(client, clock);
            var challenge = await StartLoginAsync(client, email);

            (await FinishLoginAsync(client, challenge, CodeNow(secret, clock))).StatusCode.Should().Be(HttpStatusCode.OK);

            clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
            var again = await FinishLoginAsync(client, challenge, CodeNow(secret, clock));
            again.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await again.Content.ReadAsStringAsync()).Should().Contain("log in again");
        }

        [Fact]
        public async Task Challenge_AllowsFiveAttempts()
        {
            var (client, clock) = NewApp();
            var (email, _, secret, _) = await NewTwoFactorUserAsync(client, clock);
            var challenge = await StartLoginAsync(client, email);
            var wrongCode = Totp.Code(secret, Totp.CurrentStep(clock.GetUtcNow()) + 4);

            for (var i = 0; i < 5; i++)
                (await FinishLoginAsync(client, challenge, wrongCode)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Even the right code is refused now: the password has to be entered again
            var sixth = await FinishLoginAsync(client, challenge, CodeNow(secret, clock));
            sixth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await sixth.Content.ReadAsStringAsync()).Should().Contain("log in again");

            (await FinishLoginAsync(client, await StartLoginAsync(client, email), CodeNow(secret, clock)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Challenge_Expires()
        {
            var (client, clock) = NewApp(("TwoFactor:ChallengeSeconds", "1"));
            var (email, _, secret, _) = await NewTwoFactorUserAsync(client, clock);
            var challenge = await StartLoginAsync(client, email);

            await Task.Delay(TimeSpan.FromSeconds(2));

            var late = await FinishLoginAsync(client, challenge, CodeNow(secret, clock));
            late.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await late.Content.ReadAsStringAsync()).Should().Contain("log in again");
        }

        [Fact]
        public async Task Challenge_ThatIsMadeUp_OrMissing_IsRejected()
        {
            var (client, clock) = NewApp();
            var (_, _, secret, _) = await NewTwoFactorUserAsync(client, clock);

            (await FinishLoginAsync(client, "not-a-real-challenge", CodeNow(secret, clock))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await FinishLoginAsync(client, "", CodeNow(secret, clock))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await FinishLoginAsync(client, "anything")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task RecoveryCode_LogsIn_OnlyOnce()
        {
            var (client, clock) = NewApp();
            var (email, token, _, codes) = await NewTwoFactorUserAsync(client, clock);

            // Case, spaces and dashes don't matter
            var typed = codes[0].Replace("-", " ").ToUpperInvariant();
            (await FinishLoginAsync(client, await StartLoginAsync(client, email), recoveryCode: typed))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            (await FinishLoginAsync(client, await StartLoginAsync(client, email), recoveryCode: codes[0]))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            (await FinishLoginAsync(client, await StartLoginAsync(client, email), recoveryCode: "aaaa-bbbb-cccc"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var status = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/user/2fa", token)));
            status.GetProperty("recoveryCodesLeft").GetInt32().Should().Be(9);
        }

        [Fact]
        public async Task RecoveryCodes_OfAnotherUser_DontWork()
        {
            var (client, clock) = NewApp();
            var (email, _, _, _) = await NewTwoFactorUserAsync(client, clock);
            var (_, _, _, otherCodes) = await NewTwoFactorUserAsync(client, clock);

            (await FinishLoginAsync(client, await StartLoginAsync(client, email), recoveryCode: otherCodes[0]))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task RegeneratingRecoveryCodes_NeedsACode_AndReplacesTheOldOnes()
        {
            var (client, clock) = NewApp();
            var (email, token, secret, oldCodes) = await NewTwoFactorUserAsync(client, clock);

            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/recovery-codes", token, new { code = "000000" })))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var regenerate = await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/recovery-codes", token, new { code = CodeNow(secret, clock) }));
            regenerate.StatusCode.Should().Be(HttpStatusCode.OK);
            var newCodes = (await JsonAsync(regenerate)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
            newCodes.Should().HaveCount(10).And.NotIntersectWith(oldCodes);

            (await FinishLoginAsync(client, await StartLoginAsync(client, email), recoveryCode: oldCodes[1]))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await FinishLoginAsync(client, await StartLoginAsync(client, email), recoveryCode: newCodes[1]))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Disable_NeedsPasswordAndCode_ThenLoginIsPasswordOnly()
        {
            var (client, clock) = NewApp();
            var (email, token, secret, codes) = await NewTwoFactorUserAsync(client, clock);

            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/disable", token,
                new { password = "Wr0ngPassword", code = CodeNow(secret, clock) }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/disable", token,
                new { password = Password, code = "123456" }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/disable", token,
                new { password = Password }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // A recovery code works in place of the app (e.g. a lost phone)
            (await client.SendAsync(Authed(HttpMethod.Post, "/user/2fa/disable", token,
                new { password = Password, recoveryCode = codes[0] }))).StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await JsonAsync(await client.SendAsync(Authed(HttpMethod.Get, "/user/2fa", token)));
            status.GetProperty("enabled").GetBoolean().Should().BeFalse();

            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            SetsRefreshCookie(login).Should().BeTrue();

            // The old secret is gone: setting up again gives a new one
            (await StartSetupAsync(client, token)).Should().NotEqual(secret);
        }

        [Fact]
        public async Task TwoFactorEndpoints_NeedLogin()
        {
            var (client, _) = NewApp();

            (await client.GetAsync("/user/2fa")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/user/2fa/setup", new { password = Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/user/2fa/enable", new { code = "123456" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/user/2fa/disable", new { password = Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/user/2fa/recovery-codes", new { code = "123456" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task SecondStep_IsRateLimited()
        {
            var (client, _) = NewApp(("RateLimiting:two-factor:PermitLimit", "3"));

            for (var i = 0; i < 3; i++)
                (await FinishLoginAsync(client, "made-up", "123456")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var limited = await FinishLoginAsync(client, "made-up", "123456");
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            limited.Headers.Contains("Retry-After").Should().BeTrue();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var (client, token) in _usersToDelete)
                await client.SendAsync(Authed(HttpMethod.Delete, "/user", token));

            foreach (var factory in _factories)
                await factory.DisposeAsync();
        }
    }
}
