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
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // Changing the email address: the new one is pending until its emailed link is confirmed from a
    // signed-in session of the same account
    public partial class EmailChangeEndpointsTests : IntegrationTestBase
    {
        private readonly CapturingEmailSender _emails = new();
        private readonly WebApplicationFactory<Program> _app;

        public EmailChangeEndpointsTests(WebApplicationFactory<Program> factory)
            : base(factory, ("RateLimiting:email:PermitLimit", "1000"), ("EmailChange:ResendCooldownSeconds", "0"))
        {
            _app = AppWith();
        }

        // The app with emails captured and the given settings on top of the usual ones
        private WebApplicationFactory<Program> AppWith(params (string key, string value)[] settings) =>
            WithHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(settings.ToDictionary(s => s.key, s => (string?)s.value)));
                builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(_emails));
            }, Factory);

        [GeneratedRegex(@"/(verify-email|reset-password|confirm-email|cancel-email-change)\?token=([A-Za-z0-9_-]{43})")]
        private static partial Regex LinkPattern();

        private IEnumerable<EmailMessage> EmailsTo(string to) =>
            _emails.Sent.Where(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase));

        // Wait for an email to the address with a link of that kind; returns its token
        private async Task<string> WaitForTokenAsync(string to, string kind, int alreadySeen = 0)
        {
            for (var i = 0; i < 100; i++)
            {
                var match = EmailsTo(to)
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

        private int LinksTo(string to, string kind) =>
            EmailsTo(to).Count(m => LinkPattern().Match(m.Body) is { Success: true } link && link.Groups[1].Value == kind);

        private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string email, string? password = Password) =>
            client.PostAsJsonAsync("/user/email/change", new { email, currentPassword = password });

        private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string token) =>
            client.PostAsJsonAsync("/user/email/confirm", new { token });

        private static async Task<JsonElement> InfoAsync(HttpClient client) =>
            await ReadJsonAsync(await client.GetAsync("/user/info"));

        private async Task<HttpStatusCode> LoginStatusAsync(string email, string password = Password) =>
            (await Anonymous(_app).PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password })).StatusCode;

        private async Task<object?> UserColumnAsync(string column, string email) =>
            await TestDatabase.ScalarAsync(Factory, $"SELECT {column} FROM Users WHERE Email = @Email", ("@Email", email));

        // Request a change and return the new address's token
        private async Task<string> RequestAsync(HttpClient client, string newEmail)
        {
            (await ChangeAsync(client, newEmail)).StatusCode.Should().Be(HttpStatusCode.OK);
            return await WaitForTokenAsync(newEmail, "confirm-email");
        }

        [Fact]
        public async Task Request_LeavesTheAccountAsItWas_AndOnlyMarksTheNewAddressPending()
        {
            var email = NewEmail();
            var newEmail = NewEmail();
            var client = await NewUserAsync(_app, email);

            // A session with a refresh cookie
            var login = await Anonymous(_app).PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).Split(';')[0];

            var response = await ChangeAsync(client, newEmail.ToUpperInvariant());
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await ReadJsonAsync(response);
            body.GetProperty("pendingEmail").GetString().Should().Be(newEmail);
            body.GetProperty("expiresAt").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddHours(23));
            await WaitForTokenAsync(newEmail, "confirm-email");

            var info = await InfoAsync(client);
            info.GetProperty("email").GetString().Should().Be(email);
            info.GetProperty("emailVerified").GetBoolean().Should().BeTrue();
            info.GetProperty("pendingEmail").GetString().Should().Be(newEmail);
            info.GetProperty("pendingEmailExpiresAt").GetDateTime().Should().BeAfter(DateTime.UtcNow);

            // The old address is still the login; the new one is not
            (await LoginStatusAsync(email)).Should().Be(HttpStatusCode.OK);
            (await LoginStatusAsync(newEmail)).Should().Be(HttpStatusCode.Unauthorized);
            var refresh = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            refresh.Headers.Add("Cookie", cookie);
            (await Anonymous(_app).SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Forgot password and the resend page know nothing about the pending address
            var anonymous = Anonymous(_app);
            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email = newEmail })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await anonymous.PostAsJsonAsync("/user/resend-verification-email", new { email = newEmail })).StatusCode.Should().Be(HttpStatusCode.OK);
            await Task.Delay(300);
            EmailsTo(newEmail).Should().ContainSingle();

            // The old (confirmed) address was told, with a way to cancel
            await WaitForTokenAsync(email, "cancel-email-change");
            EmailsTo(email).Single(m => m.Body.Contains("cancel-email-change")).Body.Should().Contain(newEmail);
            Convert.ToInt32(await UserColumnAsync("EmailChanged", email)).Should().Be(0);
        }

        [Fact]
        public async Task Request_IsRefused_ForBadInput_AndAnonymousCallers()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            var other = NewEmail();
            await RegisterOnlyAsync(_app, other);

            (await ChangeAsync(client, NewEmail(), "Wr0ngPassword")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ChangeAsync(client, NewEmail(), null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ChangeAsync(client, email.ToUpperInvariant())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ChangeAsync(client, "not an email")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ChangeAsync(client, "")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var taken = await ChangeAsync(client, other.ToUpperInvariant());
            taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await taken.Content.ReadAsStringAsync()).Should().Contain("Email already exists");
            (await ChangeAsync(Anonymous(_app), NewEmail())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            (await InfoAsync(client)).TryGetProperty("pendingEmail", out _).Should().BeFalse();
            await Task.Delay(300);
            _emails.Sent.Count(m => m.Body.Contains("confirm-email")).Should().Be(0);
        }

        [Fact]
        public async Task Request_IsRateLimitedPerIp()
        {
            var limited = AppWith(("RateLimiting:email:PermitLimit", "2"));
            var client = await NewUserAsync(limited);

            (await ChangeAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ChangeAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ChangeAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Fact]
        public async Task APendingAddress_NeverBlocksRegistration_AndTheLaterConfirmationGetsAConflict()
        {
            var client = await NewUserAsync(_app);
            var contested = NewEmail();
            var token = await RequestAsync(client, contested);

            // Its real owner registers it meanwhile: not blocked
            await RegisterOnlyAsync(_app, contested);

            var confirm = await ConfirmAsync(client, token);
            confirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var info = await InfoAsync(client);
            info.GetProperty("email").GetString().Should().NotBe(contested);
            info.TryGetProperty("pendingEmail", out _).Should().BeFalse();
            (await ConfirmAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Confirm_ChangesTheLogin_OnlyForTheAccountThatAsked_AndOnlyOnce()
        {
            var email = NewEmail();
            var newEmail = NewEmail();
            var client = await NewUserAsync(_app, email);
            var bystander = await NewUserAsync(_app);
            var anonymous = Anonymous(_app);

            // Links that were sent to the old mailbox
            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var resetToken = await WaitForTokenAsync(email, "reset-password");
            var token = await RequestAsync(client, newEmail);
            var cancelToken = await WaitForTokenAsync(email, "cancel-email-change");

            // Signed out, or signed in as another account: refused, and the link survives
            (await ConfirmAsync(anonymous, token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ConfirmAsync(bystander, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(client, "nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(client, new string('A', 43))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await InfoAsync(client)).GetProperty("email").GetString().Should().Be(email);

            var confirm = await ConfirmAsync(client, token);
            confirm.StatusCode.Should().Be(HttpStatusCode.OK);
            var confirmed = await ReadJsonAsync(confirm);
            confirmed.GetProperty("email").GetString().Should().Be(newEmail);

            // The returned access token carries the new email and works
            var renewed = Anonymous(_app);
            renewed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", confirmed.GetProperty("token").GetString());
            var info = await InfoAsync(renewed);
            info.GetProperty("email").GetString().Should().Be(newEmail);
            info.GetProperty("emailVerified").GetBoolean().Should().BeTrue();
            info.TryGetProperty("pendingEmail", out _).Should().BeFalse();
            Convert.ToInt32(await UserColumnAsync("EmailChanged", newEmail)).Should().Be(1);

            // The new address is the login now
            (await LoginStatusAsync(newEmail)).Should().Be(HttpStatusCode.OK);
            (await LoginStatusAsync(email)).Should().Be(HttpStatusCode.Unauthorized);

            // Single use; the old mailbox's reset and cancel links died with the old address
            (await ConfirmAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token = resetToken, newPassword = "N3wPassword!" }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await anonymous.PostAsJsonAsync("/user/email/cancel", new { token = cancelToken })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await LoginStatusAsync(newEmail)).Should().Be(HttpStatusCode.OK);

            // The old address is told, without a link
            var notice = EmailsTo(email).Last();
            for (var i = 0; i < 100 && !notice.Subject.Contains("changed"); i++)
            {
                await Task.Delay(50);
                notice = EmailsTo(email).Last();
            }
            notice.Subject.Should().Contain("changed");
            notice.Body.Should().Contain(newEmail).And.NotContain("token=");
        }

        [Fact]
        public async Task Confirm_RefusesAnExpiredLink()
        {
            var client = await NewUserAsync(_app);
            var newEmail = NewEmail();
            var token = await RequestAsync(client, newEmail);

            await ExecuteSqlAsync("UPDATE AccountTokens SET ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE Email = @Email", ("@Email", newEmail));

            (await ConfirmAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await InfoAsync(client)).TryGetProperty("pendingEmail", out _).Should().BeFalse();
        }

        [Fact]
        public async Task Confirm_WhenTwoAccountsWaitOnTheSameAddress_OnlyOneWins()
        {
            var first = await NewUserAsync(_app);
            var second = await NewUserAsync(_app);
            var contested = NewEmail();
            var firstToken = await RequestAsync(first, contested);
            (await ChangeAsync(second, contested)).StatusCode.Should().Be(HttpStatusCode.OK);
            var secondToken = await WaitForTokenAsync(contested, "confirm-email", alreadySeen: 1);

            var results = await Task.WhenAll(ConfirmAsync(first, firstToken), ConfirmAsync(second, secondToken));

            results.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
            ((int)(await TestDatabase.ScalarAsync(Factory, "SELECT COUNT(*) FROM Users WHERE Email = @Email", ("@Email", contested)))!).Should().Be(1);
        }

        [Fact]
        public async Task Resend_ReplacesTheLink_AndNeedsAPendingChange()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            (await client.PostAsync("/user/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Anonymous(_app).PostAsync("/user/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var newEmail = NewEmail();
            var first = await RequestAsync(client, newEmail);
            var resent = await client.PostAsync("/user/email/resend", null);
            resent.StatusCode.Should().Be(HttpStatusCode.OK, await resent.Content.ReadAsStringAsync());
            var second = await WaitForTokenAsync(newEmail, "confirm-email", alreadySeen: 1);

            // Asking again for the same address does the same
            (await ChangeAsync(client, newEmail)).StatusCode.Should().Be(HttpStatusCode.OK);
            var third = await WaitForTokenAsync(newEmail, "confirm-email", alreadySeen: 2);
            await WaitForTokenAsync(email, "cancel-email-change");
            await Task.Delay(300);
            LinksTo(email, "cancel-email-change").Should().Be(1);

            (await ConfirmAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ConfirmAsync(client, third)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Requests_AreLimitedPerAccount_ByCooldownAndByDay()
        {
            var cooldown = AppWith(("EmailChange:ResendCooldownSeconds", "60"));
            var slow = await NewUserAsync(cooldown);
            (await ChangeAsync(slow, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ChangeAsync(slow, NewEmail())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (await slow.PostAsync("/user/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            var daily = AppWith(("EmailChange:MaxPerDay", "2"));
            var capped = await NewUserAsync(daily);
            (await ChangeAsync(capped, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            (await capped.PostAsync("/user/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ChangeAsync(capped, NewEmail())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (await capped.PostAsync("/user/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Fact]
        public async Task CancellingInTheApp_ClearsThePendingChange_AndKillsBothLinks()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            var newEmail = NewEmail();
            var token = await RequestAsync(client, newEmail);
            var cancelToken = await WaitForTokenAsync(email, "cancel-email-change");

            (await Anonymous(_app).DeleteAsync("/user/email/pending")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.DeleteAsync("/user/email/pending")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.DeleteAsync("/user/email/pending")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await InfoAsync(client)).TryGetProperty("pendingEmail", out _).Should().BeFalse();
            (await ConfirmAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Anonymous(_app).PostAsJsonAsync("/user/email/cancel", new { token = cancelToken })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task TheWasntMeLink_CancelsTheChange_AndSignsOutEveryDeviceOnce()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            var anonymous = Anonymous(_app);

            var login = await anonymous.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).Split(';')[0];

            var newEmail = NewEmail();
            var token = await RequestAsync(client, newEmail);
            var cancelToken = await WaitForTokenAsync(email, "cancel-email-change");

            (await anonymous.PostAsJsonAsync("/user/email/cancel", new { token = "nope" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await anonymous.PostAsJsonAsync("/user/email/cancel", new { token = cancelToken })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await anonymous.PostAsJsonAsync("/user/email/cancel", new { token = cancelToken })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Every session is over: the refresh token, and the access token already issued
            var refresh = new HttpRequestMessage(HttpMethod.Post, "/user/refresh");
            refresh.Headers.Add("Cookie", cookie);
            (await anonymous.SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.GetAsync("/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // The change is gone, and the account is still usable by signing in again
            var again = await LogInAsync(_app, email);
            (await InfoAsync(again)).TryGetProperty("pendingEmail", out _).Should().BeFalse();
            (await ConfirmAsync(again, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task ChangingThePassword_DropsAPendingEmailChange()
        {
            var client = await NewUserAsync(_app);
            var token = await RequestAsync(client, NewEmail());

            var change = await client.PostAsJsonAsync("/user/password", new { currentPassword = Password, newPassword = "N3wPassword!" });
            change.StatusCode.Should().Be(HttpStatusCode.OK);

            (await InfoAsync(client)).TryGetProperty("pendingEmail", out _).Should().BeFalse();
            (await ConfirmAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task ResettingThePassword_DropsAPendingEmailChange()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            var token = await RequestAsync(client, NewEmail());

            var anonymous = Anonymous(_app);
            (await anonymous.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var reset = await WaitForTokenAsync(email, "reset-password");
            (await anonymous.PostAsJsonAsync("/user/reset-password", new { token = reset, newPassword = "N3wPassword!" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var again = await LogInAsync(_app, email, "N3wPassword!");
            (await InfoAsync(again)).TryGetProperty("pendingEmail", out _).Should().BeFalse();
            (await ConfirmAsync(again, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task AnUnconfirmedCurrentAddress_IsNotSentTheCancelNotice()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            await ExecuteSqlAsync("UPDATE Users SET EmailVerified = 0 WHERE Email = @Email", ("@Email", email));

            var newEmail = NewEmail();
            var token = await RequestAsync(client, newEmail);
            await Task.Delay(300);
            LinksTo(email, "cancel-email-change").Should().Be(0);

            // It still works, and confirming the new address confirms the account; no notice goes to the unconfirmed old one
            var before = EmailsTo(email).Count();
            (await ConfirmAsync(client, token)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await InfoAsync(client)).GetProperty("emailVerified").GetBoolean().Should().BeTrue();
            await Task.Delay(300);
            EmailsTo(email).Count().Should().Be(before);
        }

        [Fact]
        public async Task TheEmailToTheNewAddress_CarriesNoNames()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);
            (await client.PatchAsJsonAsync("/user/profile", new { firstName = "Zorblax", lastName = "Quenby" })).StatusCode.Should().Be(HttpStatusCode.OK);

            var newEmail = NewEmail();
            await RequestAsync(client, newEmail);

            var toNew = EmailsTo(newEmail).Single();
            toNew.Body.Should().NotContain("Zorblax").And.NotContain("Quenby").And.NotContain(email);
            toNew.Subject.Should().NotContain("Zorblax");
        }

        [Fact]
        public async Task ProfileUpdate_RefusesADifferentEmail_AndKeepsTheNameChangeWorking()
        {
            var email = NewEmail();
            var client = await NewUserAsync(_app, email);

            var refused = await client.PatchAsJsonAsync("/user/profile", new { firstName = "Nope", lastName = "", email = NewEmail() });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("/user/email/change");
            (await InfoAsync(client)).GetProperty("firstName").GetString().Should().NotBe("Nope");

            (await client.PatchAsJsonAsync("/user/profile", new { firstName = "Fine", lastName = "Name", email = email.ToUpperInvariant() }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await InfoAsync(client)).GetProperty("firstName").GetString().Should().Be("Fine");
        }
    }
}
