using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace Backend.Test
{
    // Hosted mode: inactive-account warning and removal, the activity clock, and the first-login
    // notice. Accounts are throwaway ones in the shared test database; accounts are backdated
    // directly in the database (the clock is the database's), and the job is run only for the
    // test's own accounts, so real or other tests' accounts are never touched.
    public class HostedModeTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Contact = "contact@example.test";

        private readonly CapturingEmailSender _emails = new();
        private readonly WebApplicationFactory<Program> _selfHosted;
        private readonly WebApplicationFactory<Program> _hosted;
        private readonly List<string> _created = [];

        public HostedModeTests(WebApplicationFactory<Program> factory)
        {
            var open = TestAccounts.WithoutLoginLimit(factory);
            _selfHosted = open.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IEmailSender>(_emails)));
            _hosted = open.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("App:Mode", "hosted");
                builder.UseSetting("Hosted:MaxRemovalsPerRun", "1000");
                builder.UseSetting("Hosted:ContactEmail", Contact);
                builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(_emails));
            });
        }

        public Task InitializeAsync() => Task.CompletedTask;

        // Remove whatever the test left (including administrator, permanent and suspended accounts)
        public async Task DisposeAsync()
        {
            using var scope = _hosted.Services.CreateScope();
            var deletion = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
            foreach (var id in _created)
            {
                await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET IsAdmin = 0 WHERE Id = @Id", ("@Id", id));
                await deletion.DeleteAsync(id);
            }
        }

        private async Task<TestAccounts.Account> NewUserAsync(WebApplicationFactory<Program>? factory = null)
        {
            var account = await TestAccounts.CreateAsync(factory ?? _hosted, "hosted");
            _created.Add(account.UserId);
            return account;
        }

        // Pretend the account was last active activeDaysAgo days ago and, if given, warned warnedDaysAgo days ago
        private Task BackdateAsync(string userId, int activeDaysAgo, int? warnedDaysAgo = null) =>
            TestDatabase.ExecuteAsync(_hosted, @"
                UPDATE Users SET LastActiveAt = DATEADD(day, -@Active, SYSUTCDATETIME()),
                    InactivityWarnedAt = DATEADD(day, -CAST(@Warned AS INT), SYSUTCDATETIME())
                WHERE Id = @Id",
                ("@Id", userId), ("@Active", activeDaysAgo), ("@Warned", (object?)warnedDaysAgo ?? DBNull.Value));

        private async Task<InactiveAccountService.RunResult> RunJobAsync(WebApplicationFactory<Program> factory, params TestAccounts.Account[] only)
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<InactiveAccountService>()
                .RunAsync(only.Select(a => Guid.Parse(a.UserId)).ToList());
        }

        private static async Task<bool> ExistsAsync(WebApplicationFactory<Program> factory, TestAccounts.Account account) =>
            Convert.ToInt32(await TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM Users WHERE Id = @Id", ("@Id", account.UserId))) == 1;

        private static async Task<DateTime?> ColumnAsync(WebApplicationFactory<Program> factory, TestAccounts.Account account, string column) =>
            (DateTime?)await TestDatabase.ScalarAsync(factory, $"SELECT {column} FROM Users WHERE Id = @Id", ("@Id", account.UserId));

        private static bool IsWarning(EmailMessage m) => m.Subject.Contains("will be removed");

        private async Task<EmailMessage?> WaitForEmailAsync(string to)
        {
            for (var i = 0; i < 100; i++)
            {
                var message = _emails.Sent.FirstOrDefault(m => m.To == to && IsWarning(m));
                if (message != null)
                    return message;
                await Task.Delay(50);
            }
            return null;
        }

        private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        // --- Mode setting ---

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("self-hosted", false)]
        [InlineData("hosted", true)]
        [InlineData(" Hosted ", true)]
        public void Mode_IsParsed(string? value, bool hosted)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:Mode"] = value }).Build();

            HostedOptions.From(config).IsHosted.Should().Be(hosted);
        }

        [Fact]
        public void Mode_UnknownValue_RefusesToStart()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:Mode"] = "public" }).Build();

            var parse = () => HostedOptions.From(config);

            parse.Should().Throw<InvalidOperationException>().WithMessage("*FILEVAULT_MODE*self-hosted*hosted*");
        }

        [Fact]
        public void Api_WithUnknownMode_FailsToStart()
        {
            var bad = _selfHosted.WithWebHostBuilder(builder => builder.UseSetting("App:Mode", "bogus"));

            var start = () => bad.CreateClient();

            start.Should().Throw<Exception>().Which.ToString().Should().Contain("FILEVAULT_MODE");
        }

        // --- The inactivity job ---

        [Fact]
        public async Task SelfHosted_Job_DoesNothing()
        {
            var user = await NewUserAsync(_selfHosted);
            await BackdateAsync(user.UserId, 60, 10);

            var result = await RunJobAsync(_selfHosted, user);

            result.Should().Be(new InactiveAccountService.RunResult(0, 0));
            (await ExistsAsync(_selfHosted, user)).Should().BeTrue();
        }

        [Fact]
        public async Task Warn_MarksTheAccountAndEmailsConfirmedAddressesOnly()
        {
            var verified = await NewUserAsync();
            var unverified = await NewUserAsync();
            var recent = await NewUserAsync();
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET EmailVerified = 0 WHERE Id = @Id", ("@Id", unverified.UserId));
            await BackdateAsync(verified.UserId, 25);
            await BackdateAsync(unverified.UserId, 25);
            await BackdateAsync(recent.UserId, 5);

            var result = await RunJobAsync(_hosted, verified, unverified, recent);

            result.Should().Be(new InactiveAccountService.RunResult(2, 0));
            (await ColumnAsync(_hosted, verified, "InactivityWarnedAt")).Should().NotBeNull();
            (await ColumnAsync(_hosted, unverified, "InactivityWarnedAt")).Should().NotBeNull("the stage is recorded either way");
            (await ColumnAsync(_hosted, recent, "InactivityWarnedAt")).Should().BeNull();

            var email = await WaitForEmailAsync(verified.Email);
            email.Should().NotBeNull();
            email!.Subject.Should().Contain("removed");
            email.Body.Should().Contain(Contact).And.Contain("30 days");
            await Task.Delay(300);
            _emails.Sent.Should().NotContain(m => m.To == unverified.Email && IsWarning(m), "an unconfirmed address is never mailed");
            _emails.Sent.Should().NotContain(m => m.To == recent.Email && IsWarning(m));

            // Warned accounts are still there, and a second run does not warn (or mail) them again
            (await ExistsAsync(_hosted, verified)).Should().BeTrue();
            (await RunJobAsync(_hosted, verified, unverified, recent)).Should().Be(new InactiveAccountService.RunResult(0, 0));
            _emails.Sent.Count(m => m.To == verified.Email && IsWarning(m)).Should().Be(1);
        }

        [Fact]
        public async Task Warn_WithoutContactEmail_LeavesThePermanentAccountSentenceOut()
        {
            var noContact = _hosted.WithWebHostBuilder(builder => builder.UseSetting("Hosted:ContactEmail", ""));
            var user = await NewUserAsync();
            await BackdateAsync(user.UserId, 25);

            await RunJobAsync(noContact, user);

            var email = await WaitForEmailAsync(user.Email);
            email.Should().NotBeNull();
            email!.Body.Should().NotContain("permanent account");
        }

        [Fact]
        public async Task Warn_WithoutEmailSetUp_StillRecordsTheStage()
        {
            // No IEmailSender override: the log sender, so nothing is mailed (or logged)
            var logOnly = TestAccounts.WithoutLoginLimit(_hosted).WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender, LogEmailSender>()));
            var user = await NewUserAsync();
            await BackdateAsync(user.UserId, 25);

            var result = await RunJobAsync(logOnly, user);

            result.Warned.Should().Be(1);
            (await ColumnAsync(_hosted, user, "InactivityWarnedAt")).Should().NotBeNull();
            await Task.Delay(300);
            _emails.Sent.Should().NotContain(m => m.To == user.Email && IsWarning(m));
        }

        [Fact]
        public async Task Remove_NotBeforeWarningDaysHavePassed()
        {
            var justWarned = await NewUserAsync();
            var neverWarned = await NewUserAsync();
            await BackdateAsync(justWarned.UserId, 40, 3);
            await BackdateAsync(neverWarned.UserId, 40);

            var result = await RunJobAsync(_hosted, justWarned, neverWarned);

            result.Removed.Should().Be(0);
            (await ExistsAsync(_hosted, justWarned)).Should().BeTrue();
            (await ExistsAsync(_hosted, neverWarned)).Should().BeTrue("a long-stale account is warned first and gets the notice period");
            (await ColumnAsync(_hosted, neverWarned, "InactivityWarnedAt")).Should().NotBeNull();
        }

        [Fact]
        public async Task Remove_DeletesTheAccountAndItsFiles_AfterTheWarningPeriod()
        {
            var user = await NewUserAsync();
            var fileId = await TestAccounts.UploadAsync(user.Client, "gone.txt", "bye"u8.ToArray(), "text/plain");
            await BackdateAsync(user.UserId, 40, 8);

            var result = await RunJobAsync(_hosted, user);

            result.Removed.Should().Be(1);
            (await ExistsAsync(_hosted, user)).Should().BeFalse();
            Convert.ToInt32(await TestDatabase.ScalarAsync(_hosted, "SELECT COUNT(*) FROM Files WHERE UserId = @Id", ("@Id", user.UserId))).Should().Be(0);
            fileId.Should().NotBeNullOrEmpty();
            (await user.Client.GetAsync("/user/info")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Remove_RespectsThePerRunLimit()
        {
            var limited = _hosted.WithWebHostBuilder(builder => builder.UseSetting("Hosted:MaxRemovalsPerRun", "1"));
            var first = await NewUserAsync();
            var second = await NewUserAsync();
            await BackdateAsync(first.UserId, 40, 8);
            await BackdateAsync(second.UserId, 40, 8);

            var result = await RunJobAsync(limited, first, second);

            result.Removed.Should().Be(1);
            (await ExistsAsync(_hosted, first)).Should().NotBe(await ExistsAsync(_hosted, second));
        }

        [Fact]
        public async Task Job_SkipsAdminPermanentAndSuspendedAccounts()
        {
            var admin = await NewUserAsync();
            var permanent = await NewUserAsync();
            var suspended = await NewUserAsync();
            await TestDatabase.SetAdminAsync(_hosted, admin.UserId, true);
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", permanent.UserId));
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET SuspendedAt = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", suspended.UserId));
            foreach (var account in new[] { admin, permanent, suspended })
                await BackdateAsync(account.UserId, 90, 30);

            var result = await RunJobAsync(_hosted, admin, permanent, suspended);

            result.Should().Be(new InactiveAccountService.RunResult(0, 0));
            foreach (var account in new[] { admin, permanent, suspended })
            {
                (await ExistsAsync(_hosted, account)).Should().BeTrue();
            }
        }

        // --- The activity clock ---

        [Fact]
        public async Task Refresh_CountsAsUse_AndCancelsTheWarning()
        {
            var user = await NewUserAsync();
            await BackdateAsync(user.UserId, 40, 8);

            (await user.Client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ColumnAsync(_hosted, user, "InactivityWarnedAt")).Should().BeNull();
            (await ColumnAsync(_hosted, user, "LastActiveAt")).Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
            (await RunJobAsync(_hosted, user)).Should().Be(new InactiveAccountService.RunResult(0, 0));
            (await ExistsAsync(_hosted, user)).Should().BeTrue();
        }

        [Fact]
        public async Task SignIn_CountsAsUse()
        {
            var user = await NewUserAsync();
            await BackdateAsync(user.UserId, 40, 8);

            await TestAccounts.LoginAsync(_hosted.CreateClient(), user.Email);

            (await ColumnAsync(_hosted, user, "InactivityWarnedAt")).Should().BeNull();
            (await ColumnAsync(_hosted, user, "LastActiveAt")).Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        }

        [Fact]
        public async Task Refresh_WritesActivityAtMostOncePerHour()
        {
            var user = await NewUserAsync();
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET LastActiveAt = DATEADD(minute, -10, SYSUTCDATETIME()) WHERE Id = @Id", ("@Id", user.UserId));
            var before = await ColumnAsync(_hosted, user, "LastActiveAt");

            (await user.Client.PostAsync("/user/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ColumnAsync(_hosted, user, "LastActiveAt")).Should().Be(before);
        }

        [Fact]
        public async Task Remove_RechecksInsideTheDeleteTransaction()
        {
            var user = await NewUserAsync();
            await BackdateAsync(user.UserId, 40, 8);
            using var scope = _hosted.Services.CreateScope();
            var hosted = scope.ServiceProvider.GetRequiredService<HostedOptions>();
            var deletion = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();

            // The job picked the account up as due ...
            (await db.GetAccountsDueForRemovalAsync(hosted, 1000, [Guid.Parse(user.UserId)])).Should().Contain(user.UserId);
            // ... then they used the app before it got to deleting them
            await db.TouchActivityAsync(user.UserId);
            var result = await deletion.DeleteAsync(user.UserId, hosted);

            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(409);
            (await ExistsAsync(_hosted, user)).Should().BeTrue();
        }

        [Fact]
        public async Task Remove_RechecksThatTheAccountIsStillNotExempt()
        {
            var user = await NewUserAsync();
            await BackdateAsync(user.UserId, 40, 8);
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", user.UserId));
            using var scope = _hosted.Services.CreateScope();

            var result = await scope.ServiceProvider.GetRequiredService<AccountDeletionService>()
                .DeleteAsync(user.UserId, scope.ServiceProvider.GetRequiredService<HostedOptions>());

            result.Success.Should().BeFalse();
            (await ExistsAsync(_hosted, user)).Should().BeTrue();
        }

        // --- The first-login notice ---

        [Fact]
        public async Task Notice_ShownInHostedMode_UntilDismissed()
        {
            var user = await NewUserAsync();

            var info = await GetJsonAsync(user.Client, "/user/info");
            info.NoticeShown().Should().BeTrue();
            info.GetProperty("hostedContactEmail").GetString().Should().Be(Contact);

            (await user.Client.PostAsync("/user/notices/hosted/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            // Dismissing again is harmless
            (await user.Client.PostAsync("/user/notices/hosted/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            info = await GetJsonAsync(user.Client, "/user/info");
            info.NoticeShown().Should().BeFalse();
            info.TryGetProperty("hostedContactEmail", out _).Should().BeFalse("nothing to show");
            (await ColumnAsync(_hosted, user, "HostedNoticeDismissedAt")).Should().NotBeNull();

            // Still dismissed after signing in again
            var again = await TestAccounts.LoginAsync(_hosted.CreateClient(), user.Email);
            (await GetJsonAsync(again, "/user/info")).NoticeShown().Should().BeFalse();
        }

        [Fact]
        public async Task Notice_NotShownInSelfHostedMode()
        {
            var user = await NewUserAsync(_selfHosted);

            (await GetJsonAsync(user.Client, "/user/info")).NoticeShown().Should().BeFalse();
        }

        [Fact]
        public async Task Notice_NotShownToAdminsOrPermanentAccounts()
        {
            var admin = await NewUserAsync();
            var permanent = await NewUserAsync();
            await TestDatabase.SetAdminAsync(_hosted, admin.UserId, true);
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", permanent.UserId));

            (await GetJsonAsync(admin.Client, "/user/info")).NoticeShown().Should().BeFalse();
            (await GetJsonAsync(permanent.Client, "/user/info")).NoticeShown().Should().BeFalse();
        }

        [Fact]
        public async Task Notice_WithoutContactEmail_OmitsTheAddress()
        {
            var noContact = _hosted.WithWebHostBuilder(builder => builder.UseSetting("Hosted:ContactEmail", ""));
            var user = await NewUserAsync(noContact);

            var info = await GetJsonAsync(user.Client, "/user/info");

            info.NoticeShown().Should().BeTrue();
            info.TryGetProperty("hostedContactEmail", out _).Should().BeFalse("nothing to show");
        }

        [Fact]
        public async Task DismissNotice_RequiresSignIn()
        {
            (await _hosted.CreateClient().PostAsync("/user/notices/hosted/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // --- The admin page ---

        [Fact]
        public async Task Admin_SeesModeLastActiveAndRemovalDate_WhenHosted()
        {
            var admin = await NewUserAsync();
            var user = await NewUserAsync();
            var permanent = await NewUserAsync();
            await TestDatabase.SetAdminAsync(_hosted, admin.UserId, true);
            await TestDatabase.ExecuteAsync(_hosted, "UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", permanent.UserId));
            await BackdateAsync(user.UserId, 20);

            (await GetJsonAsync(admin.Client, "/admin/stats")).GetProperty("mode").GetString().Should().Be("hosted");
            var users = (await GetJsonAsync(admin.Client, "/admin/users")).EnumerateArray().ToList();

            var row = users.Single(u => u.GetProperty("id").GetString() == user.UserId);
            var lastActive = row.GetProperty("lastActiveAt").GetDateTime();
            lastActive.Should().BeCloseTo(DateTime.UtcNow.AddDays(-20), TimeSpan.FromMinutes(5));
            row.GetProperty("removalDueAt").GetDateTime().Should().BeCloseTo(lastActive.AddDays(30), TimeSpan.FromSeconds(1));
            users.Single(u => u.GetProperty("id").GetString() == permanent.UserId)
                .GetProperty("removalDueAt").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task Admin_SeesNoRemovalDate_WhenSelfHosted()
        {
            var admin = await NewUserAsync(_selfHosted);
            var user = await NewUserAsync(_selfHosted);
            await TestDatabase.SetAdminAsync(_hosted, admin.UserId, true);

            (await GetJsonAsync(admin.Client, "/admin/stats")).GetProperty("mode").GetString().Should().Be("self-hosted");
            var row = (await GetJsonAsync(admin.Client, "/admin/users")).EnumerateArray()
                .Single(u => u.GetProperty("id").GetString() == user.UserId);
            row.GetProperty("removalDueAt").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }


    internal static class UserInfoExtensions
    {
        // The notice fields are left out of /user/info unless the notice is due
        public static bool NoticeShown(this JsonElement info) =>
            info.TryGetProperty("hostedNotice", out var shown) && shown.GetBoolean();
    }
}
