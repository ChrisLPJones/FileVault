using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    // A drive with made-up figures, so the disk-limit tests never depend on the real disk
    public sealed class FakeDiskSpace : DiskSpace
    {
        public long? Total { get; set; }
        public long? Available { get; set; }

        public FakeDiskSpace(int percent, long? total = 1_000_000_000_000, long? used = 0)
            : base(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageRoot"] = "SecureVaultStorage",
                ["Storage:MaxDiskUsePercent"] = percent.ToString()
            }).Build(), NullLogger<DiskSpace>.Instance)
        {
            Total = total;
            Available = total - used;
        }

        // The drive has `used` bytes in use
        public void SetUsed(long used) => Available = Total - used;

        protected override (long total, long available)? ReadCore(string path) =>
            Total is { } t && Available is { } a ? (t, a) : throw new IOException("Fake drive that can't be read");
    }

    public class ClientIpKeyTests
    {
        private static string Key(string ip) => ClientIpKey.From(System.Net.IPAddress.Parse(ip));

        [Fact]
        public void IPv4_KeysOnTheAddress() => Key("203.0.113.7").Should().Be("203.0.113.7");

        [Fact]
        public void IPv4Mapped_SharesTheKeyOfTheIPv4Address() => Key("::ffff:203.0.113.7").Should().Be(Key("203.0.113.7"));

        [Fact]
        public void IPv6_SameSlash64_SharesAKey() => Key("2001:db8:1:2:aaaa::1").Should().Be(Key("2001:db8:1:2:bbbb:cccc:dddd:eeee"));

        [Fact]
        public void IPv6_DifferentSlash64_DiffersInKey() => Key("2001:db8:1:2::1").Should().NotBe(Key("2001:db8:1:3::1"));

        [Fact]
        public void NoAddress_UsesASharedFallback() => ClientIpKey.From(null).Should().Be(ClientIpKey.Unknown);
    }

    // Parsing of the new settings, and start-up failures for bad ones
    public class HostedAbuseLimitsOptionsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public HostedAbuseLimitsOptionsTests(WebApplicationFactory<Program> factory) => _factory = factory;

        private static HostedOptions Parse(params (string key, string? value)[] settings) =>
            HostedOptions.From(new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.key, s => s.value)).Build());

        [Fact]
        public void Defaults_ForSelfHostedAndHosted()
        {
            foreach (var mode in new[] { "self-hosted", "hosted" })
            {
                var options = Parse(("App:Mode", mode));
                options.DefaultQuotaBytes.Should().BeNull();
                options.MaxSignupsPerHour.Should().Be(0);
                options.MaxSignupsPerDay.Should().Be(0);
                options.AbuseContactEmail.Should().BeNull();
                options.DefaultQuotaFor(1000, permanentOrAdmin: false).Should().Be(1000, "the hosted tier follows the main default until it is set");
            }
        }

        [Fact]
        public void HostedQuota_AppliesOnlyToNonPermanentAccountsInHostedMode()
        {
            var hosted = Parse(("App:Mode", "hosted"), ("Hosted:DefaultQuotaBytes", "200"));
            hosted.DefaultQuotaFor(1000, permanentOrAdmin: false).Should().Be(200);
            hosted.DefaultQuotaFor(1000, permanentOrAdmin: true).Should().Be(1000);

            Parse(("Hosted:DefaultQuotaBytes", "200")).DefaultQuotaFor(1000, false).Should().Be(1000, "self-hosted ignores it");
            Parse(("App:Mode", "hosted"), ("Hosted:DefaultQuotaBytes", "0")).DefaultQuotaFor(1000, false).Should().Be(1000, "values <= 0 are ignored");
            Parse(("App:Mode", "hosted"), ("Hosted:DefaultQuotaBytes", "")).DefaultQuotaFor(1000, false).Should().Be(1000);
        }

        [Fact]
        public void AbuseContact_ExplicitValue_AndHostedFallback()
        {
            Parse(("App:AbuseContactEmail", " abuse@example.test ")).AbuseContactEmail.Should().Be("abuse@example.test");

            Parse(("App:Mode", "hosted"), ("Hosted:ContactEmail", "owner@example.test")).AbuseContactEmail.Should().Be("owner@example.test");
            Parse(("Hosted:ContactEmail", "owner@example.test")).AbuseContactEmail.Should().BeNull("self-hosted has no fallback");
            Parse(("App:Mode", "hosted"), ("Hosted:ContactEmail", "owner@example.test"), ("App:AbuseContactEmail", "abuse@example.test"))
                .AbuseContactEmail.Should().Be("abuse@example.test");
        }

        [Theory]
        [InlineData("not an address")]
        [InlineData("a@example.test?subject=x")]
        [InlineData("a@example.test&cc=b@example.test")]
        [InlineData("Name <a@example.test>")]
        [InlineData("a@example.test,b@example.test")]
        public void InvalidAbuseContact_IsRefused(string value)
        {
            var parse = () => Parse(("App:AbuseContactEmail", value));

            parse.Should().Throw<InvalidOperationException>().WithMessage("*ABUSE_CONTACT_EMAIL*");
        }

        [Fact]
        public void Api_WithInvalidAbuseContact_FailsToStart()
        {
            var bad = _factory.WithWebHostBuilder(builder => builder.UseSetting("App:AbuseContactEmail", "nope"));

            var start = () => bad.CreateClient();

            start.Should().Throw<Exception>().Which.ToString().Should().Contain("ABUSE_CONTACT_EMAIL");
        }

        [Theory]
        [InlineData(null, 80)]
        [InlineData("", 80)]
        [InlineData("  ", 80)]
        [InlineData("0", 0)]
        [InlineData("1", 1)]
        [InlineData("75", 75)]
        [InlineData("99", 99)]
        public void DiskPercent_Parses(string? raw, int expected) => DiskSpace.ParsePercent(raw).Should().Be(expected);

        [Theory]
        [InlineData("100")]
        [InlineData("-1")]
        [InlineData("abc")]
        [InlineData("80.5")]
        public void DiskPercent_OutOfRange_IsRefused(string raw)
        {
            var parse = () => DiskSpace.ParsePercent(raw);

            parse.Should().Throw<InvalidOperationException>().WithMessage("*STORAGE_MAX_DISK_USE_PERCENT*");
        }

        [Fact]
        public void Api_WithSignupLimitOfZero_FailsToStart()
        {
            var bad = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("App:Mode", "hosted");
                builder.UseSetting("RateLimiting:signup:PermitLimit", "0");
            });

            var start = () => bad.CreateClient();

            start.Should().Throw<Exception>().Which.ToString().Should().Contain("RateLimiting:signup:PermitLimit");
        }

        [Fact]
        public void Api_WithDiskPercent100_FailsToStart()
        {
            var bad = _factory.WithWebHostBuilder(builder => builder.UseSetting("Storage:MaxDiskUsePercent", "100"));

            var start = () => bad.CreateClient();

            start.Should().Throw<Exception>().Which.ToString().Should().Contain("STORAGE_MAX_DISK_USE_PERCENT");
        }
    }

    // Quota tiers, the disk limit, share links, per-IP sign-up limits and /site. All on the shared test
    // database, so nothing here depends on how many accounts or uploads other tests have made.
    public class HostedAbuseLimitsTests : IntegrationTestBase
    {
        public HostedAbuseLimitsTests(WebApplicationFactory<Program> factory) : base(factory) { }

        private WebApplicationFactory<Program> Hosted(params (string key, string value)[] settings) =>
            WithSettings([("App:Mode", "hosted"), ("RateLimiting:auth:PermitLimit", "1000"), ("RateLimiting:signup:PermitLimit", "1000"), .. settings]);

        private WebApplicationFactory<Program> WithDisk(FakeDiskSpace disk, bool hosted = false, params (string key, string value)[] settings) =>
            WithHost(builder =>
            {
                var all = new List<(string key, string value)> { ("RateLimiting:auth:PermitLimit", "1000"), ("RateLimiting:signup:PermitLimit", "1000") };
                if (hosted)
                    all.Add(("App:Mode", "hosted"));
                all.AddRange(settings);
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    all.ToDictionary(s => s.key, s => (string?)s.value)));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DiskSpace>();
                    services.AddSingleton<DiskSpace>(disk);
                });
            });

        private static async Task<long> QuotaAsync(HttpClient client) =>
            (await ReadJsonAsync(await client.GetAsync("/user/usage"))).GetProperty("quota").GetInt64();

        private static string UserId(HttpClient client) =>
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;

        // --- Quota tiers ---

        [Fact]
        public async Task HostedQuota_Unset_FollowsTheMainDefault()
        {
            var user = await NewUserAsync(Hosted());

            (await QuotaAsync(user)).Should().Be(FileServices.DefaultQuotaBytes);
        }

        [Fact]
        public async Task HostedQuota_SmallerTier_AppliesToNormalAccountsOnly_AndOverrideWins()
        {
            var factory = Hosted(("Hosted:DefaultQuotaBytes", "5000"));
            var user = await NewUserAsync(factory);
            var id = UserId(user);

            (await QuotaAsync(user)).Should().Be(5000);

            await ExecuteSqlAsync("UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", id));
            (await QuotaAsync(user)).Should().Be(FileServices.DefaultQuotaBytes, "permanent accounts keep the main default");

            await ExecuteSqlAsync("UPDATE Users SET IsPermanent = 0 WHERE Id = @Id", ("@Id", id));
            (await QuotaAsync(user)).Should().Be(5000, "removing the mark puts the account back on the hosted tier at once");

            await ExecuteSqlAsync("UPDATE Users SET IsAdmin = 1 WHERE Id = @Id", ("@Id", id));
            (await QuotaAsync(user)).Should().Be(FileServices.DefaultQuotaBytes, "admins are on the main default");
            await ExecuteSqlAsync("UPDATE Users SET IsAdmin = 0 WHERE Id = @Id", ("@Id", id));

            await ExecuteSqlAsync("UPDATE Users SET StorageQuota = 777 WHERE Id = @Id", ("@Id", id));
            (await QuotaAsync(user)).Should().Be(777, "a per-user override beats the tier");
            await ExecuteSqlAsync("UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", id));
            (await QuotaAsync(user)).Should().Be(777, "also for permanent accounts");
        }

        [Fact]
        public async Task SelfHosted_IgnoresTheHostedQuota()
        {
            var user = await NewUserAsync(WithSettings(("Hosted:DefaultQuotaBytes", "5000"), ("RateLimiting:auth:PermitLimit", "1000")));

            (await QuotaAsync(user)).Should().Be(FileServices.DefaultQuotaBytes);
        }

        [Fact]
        public async Task HostedQuota_RefusesUploadsOverTheTier_SingleAndChunked()
        {
            var user = await NewUserAsync(Hosted(("Hosted:DefaultQuotaBytes", "5000")));

            (await UploadAsync(user, "big.txt", new string('x', 6000))).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await UploadAsync(user, "ok.txt", new string('x', 100))).StatusCode.Should().Be(HttpStatusCode.OK);

            var start = await user.PostAsJsonAsync("/uploads", new { name = "big.bin", size = 6000 });
            start.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await start.Content.ReadAsStringAsync()).Should().Contain("Not enough storage space");
        }

        [Fact]
        public async Task AdminPage_ShowsTheEffectiveQuotaPerRow_AndBothTiers()
        {
            var factory = Hosted(("Hosted:DefaultQuotaBytes", "5000"));
            var admin = await NewUserAsync(factory);
            await TestDatabase.SetAdminAsync(Factory, UserId(admin), true);
            var normal = await NewUserAsync(factory);
            var permanent = await NewUserAsync(factory);
            await ExecuteSqlAsync("UPDATE Users SET IsPermanent = 1 WHERE Id = @Id", ("@Id", UserId(permanent)));

            var users = await ReadArrayAsync(await admin.GetAsync("/admin/users"));
            long QuotaOf(string id) => users.Single(u => u.GetProperty("id").GetString() == id).GetProperty("quota").GetInt64();

            QuotaOf(UserId(normal)).Should().Be(5000);
            QuotaOf(UserId(permanent)).Should().Be(FileServices.DefaultQuotaBytes);
            QuotaOf(UserId(admin)).Should().Be(FileServices.DefaultQuotaBytes);

            var stats = await ReadJsonAsync(await admin.GetAsync("/admin/stats"));
            stats.GetProperty("defaultQuotaBytes").GetInt64().Should().Be(5000);
            stats.GetProperty("permanentQuotaBytes").GetInt64().Should().Be(FileServices.DefaultQuotaBytes);
        }

        [Fact]
        public async Task AdminStats_HaveNoSecondTier_WhenTheTiersAreEqual()
        {
            var admin = await NewUserAsync(Hosted());
            await TestDatabase.SetAdminAsync(Factory, UserId(admin), true);

            var stats = await ReadJsonAsync(await admin.GetAsync("/admin/stats"));

            stats.GetProperty("defaultQuotaBytes").GetInt64().Should().Be(FileServices.DefaultQuotaBytes);
            stats.GetProperty("permanentQuotaBytes").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // --- Disk limit ---

        [Fact]
        public async Task DiskLimit_RefusesSingleUploads_ForEveryoneIncludingAdmins()
        {
            var disk = new FakeDiskSpace(80);
            var factory = WithDisk(disk);
            var user = await NewUserAsync(factory);
            var admin = await NewUserAsync(factory);
            await TestDatabase.SetAdminAsync(Factory, UserId(admin), true);

            (await UploadAsync(user, "fits.txt", "hello")).StatusCode.Should().Be(HttpStatusCode.OK);

            disk.SetUsed(disk.Total!.Value / 100 * 80); // exactly at the limit: anything more is refused
            foreach (var client in new[] { user, admin })
            {
                var refused = await UploadAsync(client, "no.txt", "hello");
                refused.StatusCode.Should().Be((HttpStatusCode)507);
                (await refused.Content.ReadAsStringAsync()).Should().Contain("uploads are paused");
            }
        }

        [Fact]
        public async Task DiskLimit_ChunkedStart_And_Complete()
        {
            var disk = new FakeDiskSpace(80);
            var user = await NewUserAsync(WithDisk(disk));
            var data = new byte[1000];

            var started = await user.PostAsJsonAsync("/uploads", new { name = "a.bin", size = data.Length });
            started.StatusCode.Should().Be(HttpStatusCode.OK);
            var uploadId = (await ReadJsonAsync(started)).GetProperty("uploadId").GetString()!;
            await PutChunkAsync(user, uploadId, data);

            // The drive fills up before the upload is completed
            disk.SetUsed(disk.Total!.Value / 100 * 80);
            (await user.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be((HttpStatusCode)507);
            (await user.PostAsJsonAsync("/uploads", new { name = "b.bin", size = 10 })).StatusCode.Should().Be((HttpStatusCode)507);

            // Space is freed: the same upload can be completed (the refusal released its claim)
            disk.SetUsed(0);
            (await user.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DiskLimit_PendingUploadWithNoChunks_DoesNotBlockAnotherUsersUpload()
        {
            var disk = new FakeDiskSpace(80, total: 10_000, used: 0); // the limit is 8000 bytes
            var factory = WithDisk(disk);
            var first = await NewUserAsync(factory);
            var second = await NewUserAsync(factory);

            (await first.PostAsJsonAsync("/uploads", new { name = "big.bin", size = 5000 })).StatusCode.Should().Be(HttpStatusCode.OK);

            // The first upload only reserves a declared size; nothing is on disk, so this still fits
            (await second.PostAsJsonAsync("/uploads", new { name = "other.bin", size = 5000 })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DiskLimit_ChunkWrite_IsRefusedWhenOverTheCap()
        {
            var disk = new FakeDiskSpace(80);
            var user = await NewUserAsync(WithDisk(disk));
            var data = new byte[1000];
            var started = await user.PostAsJsonAsync("/uploads", new { name = "a.bin", size = data.Length });
            var uploadId = (await ReadJsonAsync(started)).GetProperty("uploadId").GetString()!;

            disk.SetUsed(disk.Total!.Value / 100 * 80);
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await user.PutAsync($"/uploads/{uploadId}/chunks/0", content)).StatusCode.Should().Be((HttpStatusCode)507);

            disk.SetUsed(0);
            await PutChunkAsync(user, uploadId, data);
        }

        [Fact]
        public async Task DiskLimit_CopyIsNotChecked()
        {
            var disk = new FakeDiskSpace(80);
            var user = await NewUserAsync(WithDisk(disk));
            var fileId = await UploadFileAsync(user, "orig.txt", "hello");
            var folderId = await CreateFolderAsync(user, "target");

            disk.SetUsed(disk.Total!.Value); // completely full
            var copy = await user.PostAsJsonAsync("/copy", new { sourceIds = new[] { fileId }, destinationId = folderId });

            copy.StatusCode.Should().Be(HttpStatusCode.OK, await copy.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task DiskLimit_ReadFailure_AllowsUploads()
        {
            var disk = new FakeDiskSpace(80, total: null);
            var user = await NewUserAsync(WithDisk(disk));

            (await UploadAsync(user, "a.txt", "hello")).StatusCode.Should().Be(HttpStatusCode.OK);

            disk.Total = 0; // a drive that reports no size is unreadable too
            disk.Available = 0;
            (await UploadAsync(user, "b.txt", "hello")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DiskLimit_Zero_MeansNoCheck()
        {
            var disk = new FakeDiskSpace(0);
            var user = await NewUserAsync(WithDisk(disk));
            disk.SetUsed(disk.Total!.Value);

            (await UploadAsync(user, "a.txt", "hello")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DiskLimit_AppliesInHostedMode_Too()
        {
            var disk = new FakeDiskSpace(80);
            var user = await NewUserAsync(WithDisk(disk, hosted: true));
            disk.SetUsed(disk.Total!.Value);

            (await UploadAsync(user, "a.txt", "hello")).StatusCode.Should().Be((HttpStatusCode)507);
        }

        [Fact]
        public async Task AdminStats_ShowTheDiskLimit_AndWhetherUploadsArePaused()
        {
            var disk = new FakeDiskSpace(80);
            var admin = await NewUserAsync(WithDisk(disk));
            await TestDatabase.SetAdminAsync(Factory, UserId(admin), true);

            var ok = await ReadJsonAsync(await admin.GetAsync("/admin/stats"));
            ok.GetProperty("maxDiskUsePercent").GetInt32().Should().Be(80);
            ok.GetProperty("diskUploadsPaused").GetBoolean().Should().BeFalse();
            ok.GetProperty("diskTotalBytes").GetInt64().Should().Be(disk.Total!.Value);

            disk.SetUsed(disk.Total.Value / 100 * 90);
            var paused = await ReadJsonAsync(await admin.GetAsync("/admin/stats"));
            paused.GetProperty("diskUploadsPaused").GetBoolean().Should().BeTrue();

            var offDisk = new FakeDiskSpace(0);
            var offAdmin = await NewUserAsync(WithDisk(offDisk));
            await TestDatabase.SetAdminAsync(Factory, UserId(offAdmin), true);
            offDisk.SetUsed(offDisk.Total!.Value);
            var off = await ReadJsonAsync(await offAdmin.GetAsync("/admin/stats"));
            off.GetProperty("maxDiskUsePercent").ValueKind.Should().Be(JsonValueKind.Null);
            off.GetProperty("diskUploadsPaused").GetBoolean().Should().BeFalse();
        }

        private static async Task PutChunkAsync(HttpClient client, string uploadId, byte[] data)
        {
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await client.PutAsync($"/uploads/{uploadId}/chunks/0", content)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // --- Share links need a confirmed email ---

        [Fact]
        public async Task CreatingAShareLink_NeedsAConfirmedEmail()
        {
            var user = await NewUserAsync();
            var fileId = await UploadFileAsync(user, "shared.txt", "hello");
            var id = UserId(user);

            (await user.PostAsJsonAsync("/shares", new { itemId = fileId })).StatusCode.Should().Be(HttpStatusCode.OK);

            await ExecuteSqlAsync("UPDATE Users SET EmailVerified = 0 WHERE Id = @Id", ("@Id", id));
            var refused = await user.PostAsJsonAsync("/shares", new { itemId = fileId });
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("confirm your email");

            await ExecuteSqlAsync("UPDATE Users SET EmailVerified = 1 WHERE Id = @Id", ("@Id", id));
            (await user.PostAsJsonAsync("/shares", new { itemId = fileId })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // --- Sign-up: per IP ---

        private async Task<HttpResponseMessage> RegisterRawAsync(HttpClient client, string? email = null) =>
            await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Sign", LastName = "Up", Email = email ?? NewEmail(), Password = Password });

        [Fact]
        public async Task Hosted_Signups_AreLimitedPerIp_WithTheDefaultOfFivePerHour()
        {
            var factory = WithSettings(("App:Mode", "hosted"));
            var client = Anonymous(factory);

            for (var i = 0; i < 5; i++)
            {
                var email = NewEmail();
                (await RegisterRawAsync(client, email)).StatusCode.Should().Be(HttpStatusCode.OK);
                TrackRegistered(email);
            }

            var sixth = await RegisterRawAsync(client);
            sixth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            sixth.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeGreaterThan(120);
            (await sixth.Content.ReadAsStringAsync()).Should().Contain("try again later").And.NotContain("a minute");
        }

        [Fact]
        public async Task Hosted_SignupLimit_IsConfigurable_AndSeparateFromLogin()
        {
            var factory = WithSettings(("App:Mode", "hosted"), ("RateLimiting:signup:PermitLimit", "2"), ("RateLimiting:auth:PermitLimit", "1000"));
            var client = Anonymous(factory);
            var first = NewEmail();
            (await RegisterRawAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.OK);
            var second = NewEmail();
            (await RegisterRawAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.OK);
            TrackRegistered(first);
            TrackRegistered(second);

            (await RegisterRawAsync(client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            // Signing in is not affected by the used-up sign-up allowance
            await TestDatabase.MarkEmailVerifiedAsync(Factory, first);
            (await client.PostAsJsonAsync("/user/login", new LoginModel { Email = first, Password = Password })).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        // Sets the connection's address from a header, as a real socket would, so the policy sees IPv6 clients
        private sealed class FakeClientIp : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, nxt) =>
                {
                    if (context.Request.Headers["X-Test-Client-Ip"].FirstOrDefault() is { } ip)
                        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                    return nxt();
                });
                next(app);
            };
        }

        [Fact]
        public async Task Hosted_SignupLimit_IsSharedAcrossAnIPv6Slash64_ButNotAcrossSlash64s()
        {
            var factory = WithHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:Mode"] = "hosted", ["RateLimiting:signup:PermitLimit"] = "1", ["RateLimiting:auth:PermitLimit"] = "1000"
                }));
                builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, FakeClientIp>());
            });
            var client = Anonymous(factory);
            Task<HttpResponseMessage> From(string ip)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/user/register")
                {
                    Content = JsonContent.Create(new UserModel { FirstName = "Sign", LastName = "Up", Email = NewEmail(), Password = Password })
                };
                request.Headers.Add("X-Test-Client-Ip", ip);
                return client.SendAsync(request);
            }

            (await From("2001:db8:1:2::1")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await From("2001:db8:1:2:aaaa:bbbb:cccc:dddd")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "same /64");
            (await From("2001:db8:1:3::1")).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "a different /64 has its own allowance");
        }

        [Fact]
        public async Task Hosted_Registers_DoNotUseUpTheLoginLimit()
        {
            var factory = WithSettings(("App:Mode", "hosted"), ("RateLimiting:signup:PermitLimit", "1000"), ("RateLimiting:auth:PermitLimit", "2"));
            var client = Anonymous(factory);
            string? last = null;
            for (var i = 0; i < 4; i++)
            {
                last = NewEmail();
                (await RegisterRawAsync(client, last)).StatusCode.Should().Be(HttpStatusCode.OK);
                TrackRegistered(last);
            }

            await TestDatabase.MarkEmailVerifiedAsync(Factory, last!);
            (await client.PostAsJsonAsync("/user/login", new LoginModel { Email = last!, Password = Password })).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task SelfHosted_Signups_StillFollowTheLoginLimitSettings()
        {
            var factory = WithSettings(("RateLimiting:auth:PermitLimit", "3"));
            var client = Anonymous(factory);
            for (var i = 0; i < 3; i++)
            {
                var email = NewEmail();
                (await RegisterRawAsync(client, email)).StatusCode.Should().Be(HttpStatusCode.OK);
                TrackRegistered(email);
            }

            var fourth = await RegisterRawAsync(client);
            fourth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (await fourth.Content.ReadAsStringAsync()).Should().Contain("wait a minute");
        }

        [Fact]
        public async Task SelfHosted_IgnoresTheSiteWideSignupLimits()
        {
            var factory = WithSettings(("RateLimiting:auth:PermitLimit", "1000"), ("Hosted:MaxSignupsPerHour", "1"), ("Hosted:MaxSignupsPerDay", "1"));
            var client = Anonymous(factory);
            for (var i = 0; i < 2; i++)
            {
                var email = NewEmail();
                (await RegisterRawAsync(client, email)).StatusCode.Should().Be(HttpStatusCode.OK);
                TrackRegistered(email);
            }
        }

        [Fact]
        public async Task Hosted_UnconfirmedAccounts_CannotLogIn()
        {
            var factory = Hosted();
            var email = NewEmail();
            await RegisterOnlyAsync(factory, email);

            var login = await Anonymous(factory).PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });

            login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadJsonAsync(login)).GetProperty("emailNotVerified").GetBoolean().Should().BeTrue();
        }

        // --- GET /site ---

        private static async Task<(HttpResponseMessage response, JsonElement body)> SiteAsync(HttpClient client)
        {
            var response = await client.GetAsync("/site");
            return (response, await ReadJsonAsync(response));
        }

        [Fact]
        public async Task Site_ReturnsNull_WhenNoContactIsSet()
        {
            var (response, body) = await SiteAsync(Anonymous(WithSettings(("App:AbuseContactEmail", ""))));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.GetProperty("abuseContactEmail").ValueKind.Should().Be(JsonValueKind.Null);
            body.EnumerateObject().Select(p => p.Name).Should().Equal("abuseContactEmail");
        }

        [Fact]
        public async Task Site_ReturnsTheContact_WithoutSignIn_AndAllowsCaching()
        {
            var (response, body) = await SiteAsync(Anonymous(WithSettings(("App:AbuseContactEmail", "abuse@example.test"))));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.GetProperty("abuseContactEmail").GetString().Should().Be("abuse@example.test");
            response.Headers.CacheControl!.Public.Should().BeTrue();
            response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromMinutes(5));
        }

        [Fact]
        public async Task Site_Hosted_FallsBackToTheHostedContact_ButExplicitWins()
        {
            var fallback = await SiteAsync(Anonymous(WithSettings(("App:Mode", "hosted"), ("App:AbuseContactEmail", ""), ("Hosted:ContactEmail", "owner@example.test"))));
            fallback.body.GetProperty("abuseContactEmail").GetString().Should().Be("owner@example.test");

            var explicitValue = await SiteAsync(Anonymous(WithSettings(("App:Mode", "hosted"), ("App:AbuseContactEmail", "abuse@example.test"), ("Hosted:ContactEmail", "owner@example.test"))));
            explicitValue.body.GetProperty("abuseContactEmail").GetString().Should().Be("abuse@example.test");

            var selfHosted = await SiteAsync(Anonymous(WithSettings(("App:AbuseContactEmail", ""), ("Hosted:ContactEmail", "owner@example.test"))));
            selfHosted.body.GetProperty("abuseContactEmail").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task Site_IsRateLimited()
        {
            var client = Anonymous(WithSettings(("RateLimiting:public:PermitLimit", "2")));

            (await client.GetAsync("/site")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/site")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/site")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
    }

    // Disk-limit cases that depend on every upload in progress, and the site-wide sign-up limits (which
    // count every account): each test has its own scratch database (see FreshDatabase).
    public class HostedAbuseLimitsFreshDatabaseTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly List<WebApplicationFactory<Program>> _factories = [];
        private FreshDatabase _db = null!;

        public HostedAbuseLimitsFreshDatabaseTests(WebApplicationFactory<Program> factory) => _baseFactory = factory;

        public async Task InitializeAsync() => _db = await FreshDatabase.CreateAsync();

        public async Task DisposeAsync()
        {
            foreach (var factory in _factories)
                await factory.DisposeAsync();
            await _db.DisposeAsync();
        }

        private WebApplicationFactory<Program> Api(FakeDiskSpace? disk = null, params (string key, string value)[] settings)
        {
            var factory = _db.CreateFactory(_baseFactory).WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(settings.ToDictionary(s => s.key, s => (string?)s.value)));
                if (disk != null)
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DiskSpace>();
                        services.AddSingleton<DiskSpace>(disk);
                    });
            });
            _factories.Add(factory);
            return factory;
        }

        private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
            await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Sign", LastName = "Up", Email = email, Password = TestAccounts.Password });

        private static string NewEmail() => $"fresh_{Guid.NewGuid():N}@example.test";

        private Task<int> UserCountAsync() => _db.ScalarAsync<int>("SELECT COUNT(*) FROM Users");

        // --- Disk limit with uploads in progress ---

        [Fact]
        public async Task DiskLimit_UploadsInProgress_OfOtherAccounts_ReserveNothingUntilChunksArrive()
        {
            var disk = new FakeDiskSpace(80, total: 1_000_000_000, used: 0); // limit: 800 MB
            var api = Api(disk);
            await TestAccounts.CreateAsync(api, "first"); // the first account is the administrator
            var a = await TestAccounts.CreateAsync(api, "a");
            var b = await TestAccounts.CreateAsync(api, "b");

            (await a.Client.PostAsJsonAsync("/uploads", new { name = "big.bin", size = 500_000_000 })).StatusCode.Should().Be(HttpStatusCode.OK);

            (await b.Client.PostAsJsonAsync("/uploads", new { name = "b.bin", size = 400_000_000 })).StatusCode
                .Should().Be(HttpStatusCode.OK, "a declared size with no chunks received is not on the drive");
        }

        [Fact]
        public async Task DiskLimit_Complete_DoesNotCountTheUploadsOwnReservationTwice()
        {
            var disk = new FakeDiskSpace(80, total: 1_000_000, used: 799_000); // room for exactly 1000 more bytes
            var api = Api(disk);
            await TestAccounts.CreateAsync(api, "first");
            var user = await TestAccounts.CreateAsync(api, "u");
            var data = new byte[1000];

            var started = await user.Client.PostAsJsonAsync("/uploads", new { name = "edge.bin", size = data.Length });
            started.StatusCode.Should().Be(HttpStatusCode.OK, "used + 1000 is exactly the limit");
            var uploadId = (await ReadAsync(started)).GetProperty("uploadId").GetString()!;
            var chunk = new ByteArrayContent(data);
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await user.Client.PutAsync($"/uploads/{uploadId}/chunks/0", chunk)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await user.Client.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        // --- Sign-up: site-wide limits ---

        [Fact]
        public async Task Signups_AreNotLimitedSiteWide_ByDefault()
        {
            var client = Api(settings: ("App:Mode", "hosted")).CreateClient();

            for (var i = 0; i < 4; i++)
                (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);

            (await UserCountAsync()).Should().Be(4);
        }

        [Fact]
        public async Task HourlyLimit_PausesSignups_AndInsertsNothing()
        {
            var api = Api(settings: [("App:Mode", "hosted"), ("Hosted:MaxSignupsPerHour", "2")]);
            var client = api.CreateClient();

            var first = NewEmail();
            (await RegisterAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            var paused = await RegisterAsync(client, NewEmail());

            paused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            paused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().Be(3600);
            (await paused.Content.ReadAsStringAsync()).Should().Contain("New sign-ups are paused");
            (await UserCountAsync()).Should().Be(2);
            (await _db.IsAdminAsync(first)).Should().BeTrue("the first account on an empty database is never refused and is the administrator");

            // Accounts created a while ago stop counting towards the hourly limit
            await _db.ScalarAsync<int>("UPDATE Users SET CreatedAt = DATEADD(hour, -2, GETUTCDATE()); SELECT 1");
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DailyLimit_StillCountsOlderAccounts()
        {
            var api = Api(settings: [("App:Mode", "hosted"), ("Hosted:MaxSignupsPerDay", "2")]);
            var client = api.CreateClient();

            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
            await _db.ScalarAsync<int>("UPDATE Users SET CreatedAt = DATEADD(hour, -5, GETUTCDATE()); SELECT 1");
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            await _db.ScalarAsync<int>("UPDATE Users SET CreatedAt = DATEADD(hour, -30, GETUTCDATE()); SELECT 1");
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task AdminCreatedAccounts_AreNeverLimited()
        {
            var api = Api(settings: [("App:Mode", "hosted"), ("Hosted:MaxSignupsPerHour", "1")]);
            var client = api.CreateClient();
            var adminEmail = NewEmail();
            (await RegisterAsync(client, adminEmail)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            await TestDatabase.MarkEmailVerifiedAsync(api, adminEmail);
            await TestAccounts.LoginAsync(client, adminEmail);
            var created = await client.PostAsJsonAsync("/admin/users",
                new { firstName = "Made", lastName = "ByAdmin", email = NewEmail(), password = TestAccounts.Password });

            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            (await UserCountAsync()).Should().Be(2);
        }

        [Fact]
        public async Task SimultaneousSignups_UnderALimitOfOne_CreateExactlyOneAccount()
        {
            var api = Api(settings: [("App:Mode", "hosted"), ("Hosted:MaxSignupsPerHour", "1")]);
            var client = api.CreateClient();

            var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RegisterAsync(client, NewEmail())));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).Should().Be(4);
            (await UserCountAsync()).Should().Be(1);
            (await _db.AdminCountAsync()).Should().Be(1);
        }

        [Fact]
        public async Task SelfHosted_FirstAccountStillBecomesAdmin_WithTheHostedLimitsSet()
        {
            var api = Api(settings: [("Hosted:MaxSignupsPerHour", "1")]);
            var client = api.CreateClient();
            var first = NewEmail();

            (await RegisterAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RegisterAsync(client, NewEmail())).StatusCode.Should().Be(HttpStatusCode.OK);

            (await _db.IsAdminAsync(first)).Should().BeTrue();
        }
    }
}
