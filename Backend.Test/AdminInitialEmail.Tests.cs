using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backend.Test
{
    // The INITIAL_ADMIN_EMAIL setting (Admin:InitialEmail): with it set, nobody becomes admin at
    // registration; that email does once it is confirmed (link, password reset or login), if there
    // is no administrator. Each test has its own scratch database (see FreshDatabase).
    public partial class AdminInitialEmailTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Owner = "owner@example.test";
        private const string SquatterPassword = "Squatt3rPassword";
        private const string OwnerPassword = "Own3rsNewPassword";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly CapturingEmailSender _emails = new();
        private readonly List<WebApplicationFactory<Program>> _factories = [];
        private FreshDatabase _db = null!;

        public AdminInitialEmailTests(WebApplicationFactory<Program> factory) => _baseFactory = factory;

        public async Task InitializeAsync() => _db = await FreshDatabase.CreateAsync();

        public async Task DisposeAsync()
        {
            foreach (var factory in _factories)
                await factory.DisposeAsync();
            await _db.DisposeAsync();
        }

        [GeneratedRegex(@"/(verify-email|reset-password|confirm-email)\?token=([A-Za-z0-9_-]{43})")]
        private static partial Regex LinkPattern();

        // The API against the scratch database with the setting (null = not set) and emails captured
        private WebApplicationFactory<Program> Api(string? initialEmail = Owner)
        {
            var factory = _db.CreateFactory(_baseFactory, initialEmail).WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimiting:email:PermitLimit", "1000");
                builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(_emails));
            });
            _factories.Add(factory);
            return factory;
        }

        private async Task<string> WaitForTokenAsync(string to, string kind, int skip = 0)
        {
            for (var i = 0; i < 100; i++)
            {
                var match = _emails.Sent
                    .Where(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase))
                    .Select(m => LinkPattern().Match(m.Body))
                    .Where(m => m.Success && m.Groups[1].Value == kind)
                    .Skip(skip)
                    .FirstOrDefault();
                if (match != null)
                    return match.Groups[2].Value;
                await Task.Delay(50);
            }
            throw new TimeoutException($"No {kind} email to {to}");
        }

        private static async Task RegisterAsync(WebApplicationFactory<Program> api, string email, string password = TestAccounts.Password)
        {
            var response = await api.CreateClient().PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Some", LastName = "One", Email = email, Password = password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        private static Task<HttpResponseMessage> LoginAsync(WebApplicationFactory<Program> api, string email, string password) =>
            api.CreateClient().PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = password });

        private static async Task<HttpResponseMessage> VerifyAsync(WebApplicationFactory<Program> api, string token) =>
            await api.CreateClient().PostAsJsonAsync("/user/verify-email", new { token });

        private static async Task<bool> PasswordResetFlagAsync(HttpResponseMessage response) =>
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("passwordReset").GetBoolean();

        private async Task ResetPasswordAsync(WebApplicationFactory<Program> api, string email, string newPassword)
        {
            var client = api.CreateClient();
            (await client.PostAsJsonAsync("/user/forgot-password", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
            var token = await WaitForTokenAsync(email, "reset-password");
            (await client.PostAsJsonAsync("/user/reset-password", new { token, newPassword })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        private async Task<bool> ApiSaysAdminAsync(WebApplicationFactory<Program> api, string email, string password)
        {
            var client = api.CreateClient();
            var login = await LoginAsync(api, email, password);
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            await TestAccounts.LoginAsync(client, email, password);
            return (await client.GetFromJsonAsync<AdminStatusResponse>("/admin/me"))!.IsAdmin;
        }

        [Fact]
        public async Task SettingSet_FirstRegistrationIsNotAdmin()
        {
            var api = Api();
            await RegisterAsync(api, "first@example.test");
            await RegisterAsync(api, Owner);

            (await _db.AdminCountAsync()).Should().Be(0);
        }

        [Fact]
        public async Task SettingSet_ConfirmingTheOwnersEmail_MakesThemAdmin_AndTheLinkClearsThePassword()
        {
            var api = Api();
            await RegisterAsync(api, Owner);

            var verify = await VerifyAsync(api, await WaitForTokenAsync(Owner, "verify-email"));

            verify.StatusCode.Should().Be(HttpStatusCode.OK);
            (await PasswordResetFlagAsync(verify)).Should().BeTrue();
            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);

            // The password chosen at registration no longer works; the owner sets their own
            (await LoginAsync(api, Owner, TestAccounts.Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            await ResetPasswordAsync(api, Owner, OwnerPassword);
            (await ApiSaysAdminAsync(api, Owner, OwnerPassword)).Should().BeTrue();
        }

        [Fact]
        public async Task SettingSet_IsMatchedIgnoringCaseAndWhitespace()
        {
            var api = Api("  OWNER@Example.TEST ");
            await RegisterAsync(api, "Owner@example.test");

            await VerifyAsync(api, await WaitForTokenAsync("Owner@example.test", "verify-email"));

            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
        }

        [Fact]
        public async Task SettingSet_AnotherEmailConfirmingFirst_IsNotAdmin_AndTheOwnerStillBecomesOne()
        {
            var api = Api();
            await RegisterAsync(api, Owner);
            await RegisterAsync(api, "other@example.test");

            var other = await VerifyAsync(api, await WaitForTokenAsync("other@example.test", "verify-email"));
            (await PasswordResetFlagAsync(other)).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(0);
            (await LoginAsync(api, "other@example.test", TestAccounts.Password)).StatusCode.Should().Be(HttpStatusCode.OK);

            await VerifyAsync(api, await WaitForTokenAsync(Owner, "verify-email"));
            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await _db.IsAdminAsync("other@example.test")).Should().BeFalse();
        }

        [Fact]
        public async Task Squatter_RegisteringTheOwnersEmail_CannotBlockTheOwner_OrKeepAccess_WhenTheOwnerResetsThePassword()
        {
            var api = Api();
            await RegisterAsync(api, Owner, SquatterPassword);

            // The squatter can't log in (unconfirmed), and the real owner can't register the address...
            (await LoginAsync(api, Owner, SquatterPassword)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var register = await api.CreateClient().PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Real", LastName = "Owner", Email = Owner, Password = OwnerPassword });
            register.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // ...but Forgot password reaches their mailbox, sets their password and confirms the address
            await ResetPasswordAsync(api, Owner, OwnerPassword);

            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await LoginAsync(api, Owner, SquatterPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ApiSaysAdminAsync(api, Owner, OwnerPassword)).Should().BeTrue();
        }

        [Fact]
        public async Task Squatter_OwnerOpeningTheConfirmationLinkSentToThem_DoesNotActivateTheSquattersPassword()
        {
            var api = Api();
            await RegisterAsync(api, Owner, SquatterPassword);

            // The owner receives the "confirm your email" mail the squatter's sign-up triggered and opens it
            var verify = await VerifyAsync(api, await WaitForTokenAsync(Owner, "verify-email"));

            verify.StatusCode.Should().Be(HttpStatusCode.OK);
            (await PasswordResetFlagAsync(verify)).Should().BeTrue();
            (await LoginAsync(api, Owner, SquatterPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // The squatter has no refresh token or access either way; the owner recovers with Forgot password
            await ResetPasswordAsync(api, Owner, OwnerPassword);
            (await ApiSaysAdminAsync(api, Owner, OwnerPassword)).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task SettingSet_ButAnAdminExists_ChangesNothing()
        {
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, EmailVerified, IsAdmin)
                VALUES ('Existing', 'Admin', 'existing@example.test', 'x', 1, 1)");
            var api = Api();
            await RegisterAsync(api, Owner);

            var verify = await VerifyAsync(api, await WaitForTokenAsync(Owner, "verify-email"));

            (await PasswordResetFlagAsync(verify)).Should().BeFalse();
            (await _db.IsAdminAsync(Owner)).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(1);
            (await LoginAsync(api, Owner, TestAccounts.Password)).StatusCode.Should().Be(HttpStatusCode.OK); // password kept
            await ResetPasswordAsync(api, Owner, OwnerPassword);
            (await _db.IsAdminAsync(Owner)).Should().BeFalse();
        }

        [Fact]
        public async Task SettingNotSet_FirstAccountIsAdminAtRegistration_AndConfirmationPromotesNobody()
        {
            var api = Api(initialEmail: null);
            await RegisterAsync(api, "first@example.test");
            await RegisterAsync(api, Owner);

            (await _db.IsAdminAsync("first@example.test")).Should().BeTrue();
            (await _db.IsAdminAsync(Owner)).Should().BeFalse();

            await _db.ExecuteAsync("UPDATE Users SET IsAdmin = 0"); // even with no admin at all
            var verify = await VerifyAsync(api, await WaitForTokenAsync(Owner, "verify-email"));
            (await PasswordResetFlagAsync(verify)).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(0);
            (await LoginAsync(api, Owner, TestAccounts.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.AdminCountAsync()).Should().Be(0);
        }

        [Fact]
        public async Task SettingSet_LoggingIn_NeverPromotes_ButTheNextStartupPromotesAnAlreadyConfirmedOwner()
        {
            // Confirmed some other way, or before the setting was added; nobody is admin
            var api = Api();
            await RegisterAsync(api, Owner);
            await RegisterAsync(api, "other@example.test");
            await _db.ExecuteAsync("UPDATE Users SET EmailVerified = 1");

            (await LoginAsync(api, "other@example.test", TestAccounts.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await LoginAsync(api, Owner, TestAccounts.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.AdminCountAsync()).Should().Be(0);

            _db.StartApi(_baseFactory, Owner);
            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        private static Task<HttpResponseMessage> RequestChangeAsync(HttpClient client, string email) =>
            client.PostAsJsonAsync("/user/email/change", new { email, currentPassword = TestAccounts.Password });

        [Fact]
        public async Task AskingToChangeToTheOwnersAddress_DoesNotTakeItOrBlockTheOwner_AndTheSquattersLinkIsUseless()
        {
            var api = Api();
            var squatter = await TestAccounts.CreateAsync(api, "squat");
            (await _db.AdminCountAsync()).Should().Be(0);

            // A signed-in user asks for the initial-admin address: it is only pending
            (await RequestChangeAsync(squatter.Client, Owner)).StatusCode.Should().Be(HttpStatusCode.OK);
            var squatterLink = await WaitForTokenAsync(Owner, "confirm-email");

            // The owner can still register it, and confirming it makes them the administrator
            await RegisterAsync(api, Owner);
            var verify = await VerifyAsync(api, await WaitForTokenAsync(Owner, "verify-email"));
            verify.StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);
            await ResetPasswordAsync(api, Owner, OwnerPassword);

            // Opening the squatter's link signed out does nothing; as the owner it is not their link
            (await api.CreateClient().PostAsJsonAsync("/user/email/confirm", new { token = squatterLink })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var owner = api.CreateClient();
            await TestAccounts.LoginAsync(owner, Owner, OwnerPassword);
            (await owner.PostAsJsonAsync("/user/email/confirm", new { token = squatterLink })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // The squatter's own attempt finds the address taken and changes nothing
            (await squatter.Client.PostAsJsonAsync("/user/email/confirm", new { token = squatterLink })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await squatter.Client.GetFromJsonAsync<JsonElement>("/user/info")).GetProperty("email").GetString().Should().Be(squatter.Email);
            (await squatter.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task ASquatterWhoReallyConfirmsTheOwnersAddress_NeverBecomesAdmin_ByStartupOrReset()
        {
            var api = Api();
            var squatter = await TestAccounts.CreateAsync(api, "squat");

            // The test holds the mailbox, so the squatter can complete the change
            (await RequestChangeAsync(squatter.Client, Owner)).StatusCode.Should().Be(HttpStatusCode.OK);
            var confirm = await squatter.Client.PostAsJsonAsync("/user/email/confirm", new { token = await WaitForTokenAsync(Owner, "confirm-email") });
            confirm.StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.IsAdminAsync(Owner)).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(0);

            // Nor does the next start make them admin
            _db.StartApi(_baseFactory, Owner);
            (await _db.AdminCountAsync()).Should().Be(0);

            // Resetting the password (the mail goes to the squatter's account now) doesn't promote it, and their token stays powerless
            await ResetPasswordAsync(api, Owner, OwnerPassword);
            (await _db.IsAdminAsync(Owner)).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(0);
            (await squatter.Client.GetAsync("/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task LookAlikeEmail_WithAnAccentFoldingCollation_IsNeverTheInitialAdmin()
        {
            var api = Api("chris@business.com");
            await RegisterAsync(api, "chris@busineß.com");
            await _db.ExecuteAsync("UPDATE Users SET EmailVerified = 1");

            _db.StartApi(_baseFactory, "chris@business.com");

            (await _db.AdminCountAsync()).Should().Be(0);
            (await _db.IsAdminAsync("chris@busineß.com")).Should().BeFalse();

            // Confirming through the link doesn't promote it either
            await _db.ExecuteAsync("UPDATE Users SET EmailVerified = 0");
            var verify = await VerifyAsync(api, await WaitForTokenAsync("chris@busineß.com", "verify-email"));
            verify.StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.AdminCountAsync()).Should().Be(0);
        }

        [Fact]
        public async Task Startup_WithTheSettingSet_PromotesOnlyTheOwner_EvenIfOthersAreOlderAndConfirmed()
        {
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, CreatedAt, EmailVerified, IsAdmin) VALUES
                ('Old', 'Confirmed', 'old@example.test', 'x', '2019-01-01', 1, 0),
                ('The', 'Owner', @Owner, 'x', '2024-01-01', 0, 0)", ("@Owner", Owner));

            // The owner isn't confirmed yet: nobody is handed admin
            _db.StartApi(_baseFactory, Owner);
            (await _db.AdminCountAsync()).Should().Be(0);

            await _db.ExecuteAsync("UPDATE Users SET EmailVerified = 1 WHERE Email = @Owner", ("@Owner", Owner));
            _db.StartApi(_baseFactory, Owner);
            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task Startup_WithoutTheSetting_PutsAccountsWithoutACreatedAtLast()
        {
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, CreatedAt, EmailVerified, IsAdmin) VALUES
                ('No', 'Date', 'a-nodate@example.test', 'x', NULL, 1, 0),
                ('Has', 'Date', 'z-dated@example.test', 'x', '2022-01-01', 1, 0)");

            _db.StartApi(_baseFactory);

            (await _db.IsAdminAsync("z-dated@example.test")).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task SimultaneousConfirmations_OfTheOwner_PromoteExactlyOnce_AndOthersNeverGetAdmin()
        {
            var api = Api();
            await RegisterAsync(api, Owner);
            var others = Enumerable.Range(0, 5).Select(i => $"other{i}@example.test").ToArray();
            foreach (var other in others)
                await RegisterAsync(api, other);

            var ownerId = await _db.ScalarAsync<Guid>("SELECT Id FROM Users WHERE Email = @Email", ("@Email", Owner));
            var ids = new List<Guid>();
            foreach (var other in others)
                ids.Add(await _db.ScalarAsync<Guid>("SELECT Id FROM Users WHERE Email = @Email", ("@Email", other)));

            // Direct calls so the transactions really overlap: the owner confirmed 4 times at once,
            // alongside everyone else
            var calls = Enumerable.Range(0, 4).Select(_ => ownerId).Concat(ids).Select(id => Task.Run(async () =>
            {
                using var scope = api.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<DatabaseServices>().ConfirmEmailAsync(id.ToString(), false);
            }));
            var results = await Task.WhenAll(calls);

            results.Count(promoted => promoted).Should().Be(1);
            (await _db.AdminCountAsync()).Should().Be(1);
            (await _db.IsAdminAsync(Owner)).Should().BeTrue();
            (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE EmailVerified = 1")).Should().Be(6);
        }
    }
}
