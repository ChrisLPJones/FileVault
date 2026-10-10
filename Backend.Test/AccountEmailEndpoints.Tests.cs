using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // Keeps sent emails so tests can follow their links
    public class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    public partial class AccountEmailEndpointsTests : IntegrationTestBase
    {
        private readonly CapturingEmailSender _emails = new();
        private readonly WebApplicationFactory<Program> _app;

        public AccountEmailEndpointsTests(WebApplicationFactory<Program> factory)
            : base(factory, ("RateLimiting:email:PermitLimit", "1000"))
        {
            _app = WithHost(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(_emails)), Factory);
        }

        [GeneratedRegex(@"/(verify-email|reset-password)\?token=([A-Za-z0-9_-]{43})")]
        private static partial Regex LinkPattern();

        // Wait for the background sender to deliver an email to the address; returns its link's token
        private async Task<string> WaitForTokenAsync(string to, string kind, int alreadySeen = 0)
        {
            for (var i = 0; i < 100; i++)
            {
                var match = _emails.Sent
                    .Where(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase))
                    .Select(m => LinkPattern().Match(m.Body))
                    .Where(m => m.Success && m.Groups[1].Value == kind)
                    .Skip(alreadySeen)
                    .FirstOrDefault();
                if (match != null)
                    return match.Groups[2].Value;
                await Task.Delay(50);
            }
            throw new TimeoutException($"No {kind} email to {to}");
        }

        private int EmailsTo(string to) => _emails.Sent.Count(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase));

        private static async Task<bool> IsVerifiedAsync(HttpClient client) =>
            (await ReadJsonAsync(await client.GetAsync("/user/info"))).GetProperty("emailVerified").GetBoolean();

        private async Task<HttpStatusCode> LoginStatusAsync(string email, string password) =>
            (await Anonymous(_app).PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password })).StatusCode;

        [Fact]
        public async Task NewAccount_CantLogIn_UntilTheEmailedLinkIsOpened()
        {
            var email = NewEmail();
            await RegisterOnlyAsync(_app, email);
            var anonymous = Anonymous(_app);

            // The right password: told to confirm the address, and no tokens or cookie
            var blocked = await anonymous.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var body = await ReadJsonAsync(blocked);
            body.GetProperty("emailNotVerified").GetBoolean().Should().BeTrue();
            body.GetProperty("error").GetString().Should().Be("Please confirm your email address first");
            body.TryGetProperty("success", out _).Should().BeFalse();
            blocked.Headers.Contains("Set-Cookie").Should().BeFalse();

            // A wrong password gives the usual answer, revealing nothing about the account
            var wrong = await anonymous.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = "Wr0ngPassword" });
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await wrong.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"Invalid email or password\"}");

            var token = await WaitForTokenAsync(email, "verify-email");
            (await anonymous.PostAsJsonAsync("/user/verify-email", new { token })).StatusCode.Should().Be(HttpStatusCode.OK);
            var client = await LogInAsync(_app, email);
            (await IsVerifiedAsync(client)).Should().BeTrue();

            // Single use; malformed or unknown tokens are rejected
            (await anonymous.PostAsJsonAsync("/user/verify-email", new { token })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await anonymous.PostAsJsonAsync("/user/verify-email", new { token = "nope" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await anonymous.PostAsJsonAsync("/user/verify-email", new { token = new string('A', 43) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task ForgotPassword_SendsALink_ThatSetsANewPasswordOnce_AndEndsSessions()
        {
            var email = NewEmail();
            await NewUserAsync(_app, email);
            var anonymous = Anonymous(_app);

            // An old session's refresh cookie
            var login = await anonymous.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            var refreshCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).Split(';')[0];

            var known = await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = email.ToUpperInvariant() });
            var unknown = await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = NewEmail() });
            known.StatusCode.Should().Be(HttpStatusCode.OK);
            unknown.StatusCode.Should().Be(HttpStatusCode.OK);
            (await unknown.Content.ReadAsStringAsync()).Should().Be(await known.Content.ReadAsStringAsync());

            var token = await WaitForTokenAsync(email, "reset-password");

            // A weak password is refused without using up the link
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token, newPassword = "weak" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            const string newPassword = "N3wPassword!";
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token, newPassword })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token, newPassword = "An0therPassword" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            (await LoginStatusAsync(email, Password)).Should().Be(HttpStatusCode.Unauthorized);
            (await LoginStatusAsync(email, newPassword)).Should().Be(HttpStatusCode.OK);

            var refresh = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            refresh.Headers.Add("Cookie", refreshCookie);
            (await anonymous.SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task ResettingThePassword_StopsEveryAccessToken_AndRefreshCookie()
        {
            var email = NewEmail();
            await NewUserAsync(_app, email);
            var anonymous = Anonymous(_app);

            var sessions = new List<(string Token, string Cookie)>();
            for (var i = 0; i < 2; i++)
            {
                var login = await anonymous.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
                var token = (await ReadJsonAsync(login)).GetProperty("success").GetString()!;
                sessions.Add((token, login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).Split(';')[0]));
            }

            async Task<HttpStatusCode> InfoAsync(string token)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/user/info");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return (await anonymous.SendAsync(request)).StatusCode;
            }

            foreach (var session in sessions)
                (await InfoAsync(session.Token)).Should().Be(HttpStatusCode.OK);

            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var resetToken = await WaitForTokenAsync(email, "reset-password");
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token = resetToken, newPassword = "N3wPassword!" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            foreach (var session in sessions)
            {
                (await InfoAsync(session.Token)).Should().Be(HttpStatusCode.Unauthorized);
                var refresh = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
                refresh.Headers.Add("Cookie", session.Cookie);
                (await anonymous.SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            (await LoginStatusAsync(email, "N3wPassword!")).Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task ResettingThePassword_AlsoConfirmsTheAddress()
        {
            var email = NewEmail();
            await RegisterOnlyAsync(_app, email);
            var anonymous = Anonymous(_app);

            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var token = await WaitForTokenAsync(email, "reset-password");
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token, newPassword = "N3wPassword!" })).StatusCode.Should().Be(HttpStatusCode.OK);

            // The link came from the account's inbox, so the address is confirmed and login works
            var client = await LogInAsync(_app, email, "N3wPassword!");
            (await IsVerifiedAsync(client)).Should().BeTrue();
        }

        [Fact]
        public async Task ResetLinks_Expire_AndOnlyTheNewestWorks()
        {
            var email = NewEmail();
            await NewUserAsync(_app, email);
            var anonymous = Anonymous(_app);

            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var first = await WaitForTokenAsync(email, "reset-password");
            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var second = await WaitForTokenAsync(email, "reset-password", alreadySeen: 1);

            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token = first, newPassword = "N3wPassword!" }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Simulate the hour passing
            await ExecuteSqlAsync(@"UPDATE t SET ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME())
                FROM AccountTokens t JOIN Users u ON u.Id = t.UserId WHERE u.Email = @Email", ("@Email", email));
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token = second, newPassword = "N3wPassword!" }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await LoginStatusAsync(email, Password)).Should().Be(HttpStatusCode.OK);

            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task SavingTheProfile_KeepsTheAddressConfirmed_AndSendsNothing()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            await WaitForTokenAsync(email, "verify-email");
            var sent = EmailsTo(email);

            // The email is optional and, if given, must be the current one (any case)
            (await client.PatchAsJsonAsync("/user/profile", new { firstName = "Renamed", lastName = "", email = email.ToUpperInvariant() }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PatchAsJsonAsync("/user/profile", new { firstName = "Renamed Again", lastName = "Tester" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await IsVerifiedAsync(client)).Should().BeTrue();
            var info = await ReadJsonAsync(await client.GetAsync("/user/info"));
            info.GetProperty("firstName").GetString().Should().Be("Renamed Again");
            info.GetProperty("email").GetString().Should().Be(email);
            await Task.Delay(300);
            EmailsTo(email).Should().Be(sent);
        }

        [Fact]
        public async Task ResendVerification_WhenLoggedIn_OnlySendsWhenUnverified()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            await WaitForTokenAsync(email, "verify-email");

            (await Anonymous(_app).PostAsync("/user/resend-verification", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Confirmed: nothing to send
            var sent = EmailsTo(email);
            (await (await client.PostAsync("/user/resend-verification", null)).Content.ReadAsStringAsync()).Should().Contain("already confirmed");
            await Task.Delay(300);
            EmailsTo(email).Should().Be(sent);

            // An account whose address is not confirmed (e.g. changed under the old flow) gets a new link
            await ExecuteSqlAsync("UPDATE Users SET EmailVerified = 0 WHERE Email = @Email", ("@Email", email));
            (await client.PostAsync("/user/resend-verification", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            var token = await WaitForTokenAsync(email, "verify-email", alreadySeen: 1);
            (await Anonymous(_app).PostAsJsonAsync("/user/verify-email", new { token })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await IsVerifiedAsync(client)).Should().BeTrue();
        }

        [Fact]
        public async Task ResendVerificationByEmail_WorksWithoutLogin_AndAlwaysAnswersTheSame()
        {
            var email = NewEmail();
            await RegisterOnlyAsync(_app, email);
            await WaitForTokenAsync(email, "verify-email");
            var verifiedEmail = NewEmail();
            await NewUserAsync(_app, verifiedEmail);
            var anonymous = Anonymous(_app);

            async Task<string> Resend(string address)
            {
                var response = await anonymous.PostAsJsonAsync("/user/resend-verification-email", new { email = address });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                return await response.Content.ReadAsStringAsync();
            }

            var unverifiedReply = await Resend(email.ToUpperInvariant());
            (await Resend(NewEmail())).Should().Be(unverifiedReply);
            var sentToVerified = EmailsTo(verifiedEmail);
            (await Resend(verifiedEmail)).Should().Be(unverifiedReply);

            // Only the unverified account got a (new) link, and it works
            var token = await WaitForTokenAsync(email, "verify-email", alreadySeen: 1);
            await Task.Delay(300);
            EmailsTo(verifiedEmail).Should().Be(sentToVerified);
            (await anonymous.PostAsJsonAsync("/user/verify-email", new { token })).StatusCode.Should().Be(HttpStatusCode.OK);
            await LogInAsync(_app, email);

            (await anonymous.PostAsJsonAsync("/user/resend-verification-email", new { email = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task ResendVerificationByEmail_IsRateLimited()
        {
            var client = Anonymous(WithSettings(("RateLimiting:email:PermitLimit", "2")));

            for (var i = 0; i < 2; i++)
                (await client.PostAsJsonAsync("/user/resend-verification-email", new { email = NewEmail() })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsJsonAsync("/user/resend-verification-email", new { email = NewEmail() })).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Fact]
        public async Task ForgotPassword_IsRateLimited()
        {
            var limited = WithSettings(("RateLimiting:email:PermitLimit", "2"));
            var client = Anonymous(limited);

            for (var i = 0; i < 2; i++)
                (await client.PostAsJsonAsync("/user/forgot-password", new { email = NewEmail() })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsJsonAsync("/user/forgot-password", new { email = NewEmail() })).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
    }
}
