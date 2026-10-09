using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;

namespace Backend.Test
{
    // Who is an administrator: the first account on a new install, the migration for existing
    // installs, and the rule that there is always at least one. These need an empty Users table
    // or exactly one administrator, so each test gets its own scratch database built from
    // init.sql (see FreshDatabase) and never touches the shared test database.
    public class AdminRolesTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private FreshDatabase _db = null!;
        private WebApplicationFactory<Program> _factory = null!;

        public AdminRolesTests(WebApplicationFactory<Program> factory) => _baseFactory = factory;

        public async Task InitializeAsync()
        {
            _db = await FreshDatabase.CreateAsync();
            _factory = _db.CreateFactory(_baseFactory);
        }

        public async Task DisposeAsync()
        {
            await _factory.DisposeAsync();
            await _db.DisposeAsync();
        }

        private Task<TestAccounts.Account> NewAccountAsync(string prefix = "user") => TestAccounts.CreateAsync(_factory, prefix);

        private static Task<HttpResponseMessage> SetAdminAsync(HttpClient client, string userId, bool isAdmin) =>
            client.PutAsJsonAsync($"/admin/users/{userId}/admin", new { isAdmin });

        private async Task<string> ErrorAsync(HttpResponseMessage response) =>
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;

        [Fact]
        public async Task TheTestDatabase_StartsWithNoUsers()
        {
            (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM Users")).Should().Be(0);
        }

        [Fact]
        public async Task FirstAccount_IsAdmin_AndLaterAccountsAreNot()
        {
            var first = await NewAccountAsync("first");
            var second = await NewAccountAsync("second");

            (await _db.IsAdminAsync(first.Email)).Should().BeTrue();
            (await _db.IsAdminAsync(second.Email)).Should().BeFalse();
            (await first.Client.GetFromJsonAsync<AdminStatusResponse>("/admin/me"))!.IsAdmin.Should().BeTrue();
            (await second.Client.GetFromJsonAsync<AdminStatusResponse>("/admin/me"))!.IsAdmin.Should().BeFalse();
        }

        [Fact]
        public async Task AdminEmailsSetting_DoesNotMakeAnyoneAdmin()
        {
            var email = $"listed_{Guid.NewGuid():N}@example.test";
            var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("Admin:Emails", email));
            await NewAccountAsync("first");

            var listed = await TestAccounts.CreateAsync(factory, "listed", email);

            (await _db.IsAdminAsync(listed.Email)).Should().BeFalse();
        }

        [Fact]
        public async Task SimultaneousFirstRegistrations_MakeExactlyOneAdmin()
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                var client = _factory.CreateClient();
                return await client.PostAsJsonAsync("/user/register", new UserModel
                {
                    FirstName = "Race",
                    LastName = i.ToString(),
                    Email = $"race{i}_{Guid.NewGuid():N}@example.test",
                    Password = TestAccounts.Password
                });
            }));

            results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
            (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM Users")).Should().Be(8);
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task Admin_CanGrantAdmin_AndThenEitherCanRemoveTheOther()
        {
            var first = await NewAccountAsync("first");
            var second = await NewAccountAsync("second");

            (await SetAdminAsync(second.Client, second.UserId, true)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            (await SetAdminAsync(first.Client, second.UserId, true)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.AdminCountAsync()).Should().Be(2);

            // The new admin can take admin from the first one
            (await SetAdminAsync(second.Client, first.UserId, false)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.IsAdminAsync(first.Email)).Should().BeFalse();
            (await first.Client.GetAsync("/admin/stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task LastAdmin_CannotBeDemoted_NotEvenByThemselves()
        {
            var admin = await NewAccountAsync("only");

            var response = await SetAdminAsync(admin.Client, admin.UserId, false);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ErrorAsync(response)).Should().Be("There must always be at least one administrator");
            (await _db.IsAdminAsync(admin.Email)).Should().BeTrue();
        }

        [Fact]
        public async Task LastAdmin_CannotDeleteTheirAccount_UntilAnotherAdminExists()
        {
            var admin = await NewAccountAsync("only");
            var other = await NewAccountAsync("other");

            var refused = await admin.Client.DeleteAsync("/user");
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ErrorAsync(refused)).Should().Be("There must always be at least one administrator");
            (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Email = @Email", ("@Email", admin.Email))).Should().Be(1);

            // A non-admin can still delete theirs
            (await other.Client.DeleteAsync("/user")).StatusCode.Should().Be(HttpStatusCode.OK);

            // With a second admin, the first may go
            var second = await NewAccountAsync("second");
            (await SetAdminAsync(admin.Client, second.UserId, true)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.Client.DeleteAsync("/user")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _db.AdminCountAsync()).Should().Be(1);

            // ... and now the second is the last one
            (await second.Client.DeleteAsync("/user")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task TwoAdmins_RemovingEachOtherAtOnce_LeavesExactlyOne()
        {
            for (var round = 0; round < 3; round++)
            {
                var a = await NewAccountAsync("a");
                await _db.ExecuteAsync("UPDATE Users SET IsAdmin = 0"); // exactly the two below are admins this round
                var b = await NewAccountAsync("b");
                await _db.ExecuteAsync("UPDATE Users SET IsAdmin = 1 WHERE Id IN (@A, @B)", ("@A", a.UserId), ("@B", b.UserId));

                var responses = await Task.WhenAll(
                    SetAdminAsync(a.Client, b.UserId, false),
                    SetAdminAsync(b.Client, a.UserId, false));

                // One succeeds. The other is refused as the last admin (409), or, if the first got there
                // before its admin check, is no longer an admin at all (403).
                responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
                responses.Single(r => r.StatusCode != HttpStatusCode.OK).StatusCode
                    .Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.Forbidden);
                (await _db.AdminCountAsync()).Should().Be(1);
            }
        }

        [Fact]
        public async Task TwoAdmins_DeletingTheirAccountsAtOnce_LeavesExactlyOne()
        {
            var a = await NewAccountAsync("a");
            var b = await NewAccountAsync("b");
            await _db.ExecuteAsync("UPDATE Users SET IsAdmin = 1 WHERE Id = @B", ("@B", b.UserId));

            // Both delete their own accounts at the same moment: whichever goes second is the last admin and is refused
            var responses = await Task.WhenAll(
                a.Client.DeleteAsync("/user"),
                b.Client.DeleteAsync("/user"));

            responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task Startup_MakesTheOldestConfirmedAccountAdmin_OnlyWhenThereIsNone_AndIsIdempotent()
        {
            // An existing install: accounts but no administrator. The oldest account isn't confirmed.
            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, CreatedAt, EmailVerified, IsAdmin) VALUES
                ('Old', 'Unconfirmed', 'a-unconfirmed@example.test', 'x', '2020-01-01', 0, 0),
                ('Old', 'Confirmed', 'b-confirmed@example.test', 'x', '2020-02-01', 1, 0),
                ('New', 'Confirmed', 'c-newer@example.test', 'x', '2021-01-01', 1, 0)");

            _db.StartApi(_baseFactory);

            (await _db.IsAdminAsync("b-confirmed@example.test")).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);

            // Running it again (as every start does) changes nothing
            _db.StartApi(_baseFactory);
            (await _db.IsAdminAsync("b-confirmed@example.test")).Should().BeTrue();
            (await _db.AdminCountAsync()).Should().Be(1);

            // An admin who was chosen by hand is kept and nobody else is added
            await _db.ExecuteAsync("UPDATE Users SET IsAdmin = 0; UPDATE Users SET IsAdmin = 1 WHERE Email = 'c-newer@example.test'");
            _db.StartApi(_baseFactory);
            (await _db.IsAdminAsync("c-newer@example.test")).Should().BeTrue();
            (await _db.IsAdminAsync("b-confirmed@example.test")).Should().BeFalse();
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task Startup_DoesNothing_WhenNoAccountIsConfirmed_OrThereAreNoAccounts()
        {
            _db.StartApi(_baseFactory); // no accounts at all
            (await _db.AdminCountAsync()).Should().Be(0);

            await _db.ExecuteAsync(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, EmailVerified, IsAdmin)
                VALUES ('Not', 'Confirmed', 'unconfirmed@example.test', 'x', 0, 0)");
            _db.StartApi(_baseFactory);
            (await _db.AdminCountAsync()).Should().Be(0);

            // A later registration is not the first account, so it isn't made admin either
            var later = await NewAccountAsync("later");
            (await _db.IsAdminAsync(later.Email)).Should().BeFalse();
        }

        [Fact]
        public async Task InitScript_CanBeRunTwiceOnAFreshDatabase_WithoutErrorsOrChanges()
        {
            var admin = await NewAccountAsync("first");

            await _db.RunInitScriptAsync();
            await _db.RunInitScriptAsync();

            (await _db.ScalarAsync<int>("SELECT COUNT(*) FROM Users")).Should().Be(1);
            (await _db.IsAdminAsync(admin.Email)).Should().BeTrue();
        }
    }
}
