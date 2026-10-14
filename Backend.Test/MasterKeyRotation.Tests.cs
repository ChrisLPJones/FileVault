using Backend.Models;
using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Backend.Test
{
    // The rotate-master-key command and the start-up guard. Everything runs on its own FvTest_ database
    // (FreshDatabase): a rotation changes every protected value in the database it runs against, and the
    // guard's shared lock in every other test host would stop it anyway. The keys are random per test.
    public class MasterKeyRotationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
    {
        private const int Chunk = ChunkedUploadService.ChunkSize;

        private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        private static string Fp(string key) => MasterKeyGuard.Fingerprint(Convert.FromBase64String(key));

        // An app on this database with the given master key and a controllable authenticator clock
        private WebApplicationFactory<Program> App(FreshDatabase db, string masterKey, TestClock? clock = null) =>
            db.CreateFactory(factory, masterKey: masterKey).WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimiting:two-factor:PermitLimit", "1000");
                builder.UseSetting("RateLimiting:share-download:PermitLimit", "1000");
                builder.UseSetting("RateLimiting:share:PermitLimit", "1000");
                if (clock != null)
                    builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
            });

        // Run the command as the operator would: keys only from configuration
        private static async Task<(int exitCode, string output)> RotateAsync(
            FreshDatabase db, string oldKey, string? newKey, string[]? args = null, Func<string, Task>? afterTable = null)
        {
            TestDatabaseGuard.AssertTestDatabase(db.ConnectionString);
            // The command refuses while any other connection is open to the database; the test's own
            // pooled (idle) ones from earlier queries and stopped apps would count, so close them and
            // wait briefly until the server has ended those sessions (a test that keeps one open on purpose
            // still gets its refusal, after the wait)
            await WaitForNoOtherSessionsAsync(db, 3);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = db.ConnectionString,
                ["Encryption:MasterKey"] = oldKey,
                ["Encryption:NewMasterKey"] = newKey,
            }).Build();
            var output = new StringWriter();
            var exit = await MasterKeyCommand.RunAsync(args ?? [], config, output, afterTable);
            return (exit, output.ToString());
        }

        // Wait (bounded) until no session other than ours is left on the database. Closing a connection
        // returns before the server has ended its session, and the command refuses while any other session
        // is listed, so a run right after a stopped app or a test query could be refused for a moment.
        // Counts through a connection that isn't pooled, so the check leaves nothing behind itself.
        private static async Task WaitForNoOtherSessionsAsync(FreshDatabase db, int seconds = 10)
        {
            TestDatabaseGuard.AssertTestDatabase(db.ConnectionString);
            var unpooled = new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                SqlConnection.ClearAllPools();
                int others;
                await using (var connection = new SqlConnection(unpooled))
                {
                    await connection.OpenAsync();
                    await using var command = new SqlCommand(
                        "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND is_user_process = 1 AND session_id <> @@SPID", connection);
                    others = Convert.ToInt32(await command.ExecuteScalarAsync());
                }
                if (others == 0 || DateTime.UtcNow > deadline)
                    return;
                await Task.Delay(200);
            }
        }

        // Every protected value and the key state, in a comparable form: "Tag|rowKey|value[:more]"
        private static async Task<List<string>> SnapshotAsync(FreshDatabase db)
        {
            const string sql = @"
                SELECT v FROM (
                    SELECT CONCAT('Files|', GUID, '|', WrappedKey) v FROM Files WHERE WrappedKey IS NOT NULL
                    UNION ALL SELECT CONCAT('Thumb|', FileGuid, '|', WrappedKey) FROM FileThumbnails WHERE WrappedKey IS NOT NULL
                    UNION ALL SELECT CONCAT('Avatar|', Id, '|', AvatarWrappedKey) FROM Users WHERE AvatarWrappedKey IS NOT NULL
                    UNION ALL SELECT CONCAT('Upload|', Id, '|', WrappedKey) FROM Uploads
                    UNION ALL SELECT CONCAT('Totp|', Id, '|', TotpSecret) FROM Users WHERE TotpSecret IS NOT NULL
                    UNION ALL SELECT CONCAT('ShareToken|', Id, '|', TokenCipher) FROM Shares WHERE TokenCipher IS NOT NULL
                    UNION ALL SELECT CONCAT('SharePassword|', Id, '|', PasswordCipher) FROM Shares WHERE PasswordCipher IS NOT NULL
                    UNION ALL SELECT CONCAT('Recovery|', Id, '|', CodeHash, ':', UsedAt) FROM TotpRecoveryCodes
                    UNION ALL SELECT CONCAT('State|', Id, '|', KeyFingerprint, ':', RecoveryHashKey, ':', RotatedAt) FROM MasterKeyState
                ) s ORDER BY v";
            await using var connection = new SqlConnection(db.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
            return rows;
        }

        // The output may name rows and short key IDs, never a key, a stored value or an authenticator secret
        private static void AssertNoSecrets(string output, IEnumerable<string> snapshot, params string[] secrets)
        {
            foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
                output.Should().NotContain(secret);
            foreach (var row in snapshot.Where(r => r.StartsWith("State|") == false))
                output.Should().NotContain(row.Split('|')[2].Split(':')[0]);
        }

        private static byte[] Png(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.SteelBlue);
                using var paint = new SKPaint { Color = SKColors.Orange };
                canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }

        private static byte[] RandomBytes(int length)
        {
            var data = new byte[length];
            Random.Shared.NextBytes(data);
            return data;
        }

        private static byte[] FromBase32(string value)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var c in value.TrimEnd('=').ToUpperInvariant())
            {
                buffer = (buffer << 5) | alphabet.IndexOf(c);
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)(buffer >> bits));
                    buffer &= (1 << bits) - 1;
                }
            }
            return bytes.ToArray();
        }

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        // Everything a rotation has to carry over, created through the API on the old key
        private sealed class Data
        {
            public string Email = "", UserId = "", SimpleId = "", ChunkedId = "", BinnedId = "", ImageId = "", ShareId = "", ShareToken = "", UploadId = "";
            public byte[] Simple = [], Chunked = [], Binned = [], Image = [], Avatar = [], Thumbnail = [], TotpSecret = [], UploadBytes = [];
            public const string SharePassword = "open sesame";
            public List<string> RecoveryCodes = [];
        }

        private static string NextCode(Data data, TestClock clock)
        {
            clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
            return Totp.Code(data.TotpSecret, Totp.CurrentStep(clock.GetUtcNow()));
        }

        // A user with a plain file, a chunked file, a binned file, an image with a thumbnail, an avatar, a share
        // link with a password and two-factor sign-in on; with `inProgressUpload`, also a half-sent chunked upload
        private async Task<Data> SeedAsync(WebApplicationFactory<Program> app, TestClock clock, bool inProgressUpload = false)
        {
            var account = await TestAccounts.CreateAsync(app, "rot");
            var client = account.Client;
            var data = new Data { Email = account.Email, UserId = account.UserId };

            data.Simple = RandomBytes(5000);
            data.SimpleId = await TestAccounts.UploadAsync(client, "simple.bin", data.Simple);

            data.Chunked = RandomBytes(3000);
            data.ChunkedId = await CompleteUploadAsync(client, "chunked.bin", data.Chunked);

            data.Binned = RandomBytes(2000);
            data.BinnedId = await TestAccounts.UploadAsync(client, "binned.bin", data.Binned);
            (await client.DeleteAsync($"/delete/{data.BinnedId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            data.Image = Png(400, 300);
            data.ImageId = await TestAccounts.UploadAsync(client, "picture.png", data.Image, "image/png");
            var thumbnail = await client.GetAsync($"/files/{data.ImageId}/thumbnail");
            thumbnail.StatusCode.Should().Be(HttpStatusCode.OK);
            data.Thumbnail = await thumbnail.Content.ReadAsByteArrayAsync();

            data.Avatar = Png(64, 64);
            (await client.PutAsync("/user/avatar", new MultipartFormDataContent
            {
                { new ByteArrayContent(data.Avatar) { Headers = { ContentType = MediaTypeHeaderValue.Parse("image/png") } }, "avatar", "me.png" }
            })).StatusCode.Should().Be(HttpStatusCode.OK);

            var share = await client.PostAsJsonAsync("/shares", new { itemId = data.SimpleId, password = Data.SharePassword });
            share.StatusCode.Should().Be(HttpStatusCode.OK, await share.Content.ReadAsStringAsync());
            var shareJson = await JsonAsync(share);
            data.ShareId = shareJson.GetProperty("id").GetString()!;
            data.ShareToken = shareJson.GetProperty("token").GetString()!;

            if (inProgressUpload)
            {
                data.UploadBytes = RandomBytes(Chunk + 500);
                var started = await client.PostAsJsonAsync("/uploads", new { name = "big.bin", size = data.UploadBytes.Length, mimeType = "application/octet-stream" });
                started.StatusCode.Should().Be(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
                data.UploadId = (await JsonAsync(started)).GetProperty("uploadId").GetString()!;
                (await PutChunkAsync(client, data.UploadId, 0, data.UploadBytes.AsMemory(0, Chunk))).StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // Two-factor sign-in on
            var setup = await client.PostAsJsonAsync("/user/2fa/setup", new { password = TestAccounts.Password });
            setup.StatusCode.Should().Be(HttpStatusCode.OK);
            data.TotpSecret = FromBase32((await JsonAsync(setup)).GetProperty("secret").GetString()!);
            var enable = await client.PostAsJsonAsync("/user/2fa/enable", new { code = Totp.Code(data.TotpSecret, Totp.CurrentStep(clock.GetUtcNow())) });
            enable.StatusCode.Should().Be(HttpStatusCode.OK, await enable.Content.ReadAsStringAsync());
            data.RecoveryCodes = (await JsonAsync(enable)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
            return data;
        }

        private static Task<HttpResponseMessage> PutChunkAsync(HttpClient client, string uploadId, int index, ReadOnlyMemory<byte> bytes)
        {
            var content = new ReadOnlyMemoryContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return client.PutAsync($"/uploads/{uploadId}/chunks/{index}", content);
        }

        private static async Task<string> CompleteUploadAsync(HttpClient client, string name, byte[] bytes)
        {
            var started = await client.PostAsJsonAsync("/uploads", new { name, size = bytes.Length, mimeType = "application/octet-stream" });
            started.StatusCode.Should().Be(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
            var uploadId = (await JsonAsync(started)).GetProperty("uploadId").GetString()!;
            (await PutChunkAsync(client, uploadId, 0, bytes)).StatusCode.Should().Be(HttpStatusCode.OK);
            var complete = await client.PostAsync($"/uploads/{uploadId}/complete", null);
            complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
            return (await JsonAsync(complete)).GetProperty("id").GetString()!;
        }

        // The sign-in a user with two-factor on goes through; the client sends the access token when it worked
        private static async Task<(HttpResponseMessage response, HttpClient client)> SignInAsync(
            WebApplicationFactory<Program> app, Data data, string? code = null, string? recoveryCode = null)
        {
            var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = data.Email, Password = TestAccounts.Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var challenge = (await JsonAsync(login)).GetProperty("challengeToken").GetString();

            var response = await client.PostAsJsonAsync("/user/login/2fa", new { challengeToken = challenge, code, recoveryCode });
            if (response.StatusCode == HttpStatusCode.OK)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await JsonAsync(response)).GetProperty("success").GetString());
            return (response, client);
        }

        // Everything created by SeedAsync still works on `app`
        private static async Task VerifyWorksAsync(WebApplicationFactory<Program> app, TestClock clock, Data data, bool inProgressUpload = false)
        {
            // Two-factor sign-in with an authenticator code
            var (response, client) = await SignInAsync(app, data, code: NextCode(data, clock));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            // Files, byte for byte
            (await client.GetByteArrayAsync($"/download/{data.SimpleId}")).Should().Equal(data.Simple);
            (await client.GetByteArrayAsync($"/download/{data.ChunkedId}")).Should().Equal(data.Chunked);
            (await client.GetByteArrayAsync($"/download/{data.ImageId}")).Should().Equal(data.Image);

            // Binned file restores and downloads
            var stillBinned = (await JsonAsync(await client.GetAsync("/trash"))).EnumerateArray().Any(e => e.GetProperty("_id").GetString() == data.BinnedId);
            if (stillBinned)
                (await client.PostAsJsonAsync("/trash/restore", new { ids = new[] { data.BinnedId } })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetByteArrayAsync($"/download/{data.BinnedId}")).Should().Equal(data.Binned);

            // Avatar and thumbnail
            (await client.GetByteArrayAsync("/user/avatar")).Should().Equal(data.Avatar);
            (await client.GetByteArrayAsync($"/files/{data.ImageId}/thumbnail")).Should().Equal(data.Thumbnail);

            // The upload that was half-sent on the old key resumes and completes
            if (inProgressUpload)
            {
                var status = await JsonAsync(await client.GetAsync($"/uploads/{data.UploadId}"));
                status.GetProperty("receivedChunks").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
                (await PutChunkAsync(client, data.UploadId, 1, data.UploadBytes.AsMemory(Chunk))).StatusCode.Should().Be(HttpStatusCode.OK);
                var complete = await client.PostAsync($"/uploads/{data.UploadId}/complete", null);
                complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
                var uploadedId = (await JsonAsync(complete)).GetProperty("id").GetString()!;
                (await client.GetByteArrayAsync($"/download/{uploadedId}")).Should().Equal(data.UploadBytes);
            }

            // The share link: the owner still sees the same link and password, and a visitor can download with it
            var listed = (await JsonAsync(await client.GetAsync("/shares"))).EnumerateArray().Single(s => s.GetProperty("id").GetString() == data.ShareId);
            listed.GetProperty("token").GetString().Should().Be(data.ShareToken);
            (await JsonAsync(await client.GetAsync($"/shares/{data.ShareId}/password"))).GetProperty("password").GetString().Should().Be(Data.SharePassword);
            var visitor = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var download = await visitor.PostAsJsonAsync($"/s/{data.ShareToken}/download", new { password = Data.SharePassword });
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(data.Simple);
            (await visitor.PostAsJsonAsync($"/s/{data.ShareToken}/download", new { password = "not the password" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        private static string StartFailure(WebApplicationFactory<Program> app)
        {
            try
            {
                app.CreateClient().Dispose();
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
            throw new Xunit.Sdk.XunitException("The API started, but it should have refused to.");
        }

        private static async Task<string?> StoredFingerprintAsync(FreshDatabase db) =>
            (await SnapshotAsync(db)).Where(r => r.StartsWith("State|")).Select(r => r.Split('|')[2].Split(':')[0]).SingleOrDefault();

        [Fact]
        public async Task Rotation_KeepsEverythingWorking_UnderTheNewKey_AndTheOldKeyIsRefused()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            var clock = new TestClock();
            Data data;
            await using (var appA = App(db, keyA, clock))
                data = await SeedAsync(appA, clock, inProgressUpload: true);

            var before = await SnapshotAsync(db);
            before.Should().Contain(r => r.StartsWith("Files|")).And.Contain(r => r.StartsWith("Thumb|")).And.Contain(r => r.StartsWith("Avatar|"))
                .And.Contain(r => r.StartsWith("Upload|")).And.Contain(r => r.StartsWith("Totp|")).And.Contain(r => r.StartsWith("ShareToken|"))
                .And.Contain(r => r.StartsWith("SharePassword|")).And.Contain(r => r.StartsWith("Recovery|"));

            var (exit, output) = await RotateAsync(db, keyA, keyB);

            exit.Should().Be(0, output);
            output.Should().Contain("Rotated").And.Contain(MasterKeyGuard.KeyId(Fp(keyB)));
            AssertNoSecrets(output, before, keyA, keyB, Convert.ToBase64String(data.TotpSecret), Convert.ToHexString(data.TotpSecret));

            var after = await SnapshotAsync(db);
            after.Where(r => !r.StartsWith("Recovery|") && !r.StartsWith("State|")).Should().HaveSameCount(before.Where(r => !r.StartsWith("Recovery|") && !r.StartsWith("State|")));
            after.Where(r => r.StartsWith("Files|") || r.StartsWith("Totp|") || r.StartsWith("ShareToken|"))
                .Should().NotIntersectWith(before, "every wrapped value is new");
            (await StoredFingerprintAsync(db)).Should().Be(Fp(keyB));

            await using (var appB = App(db, keyB, clock))
                await VerifyWorksAsync(appB, clock, data, inProgressUpload: true);

            // The old key opens nothing in the live database any more
            await using var appOld = App(db, keyA, clock);
            StartFailure(appOld).Should().Contain("does not match the key this database is encrypted with");
        }

        [Fact]
        public async Task AnApiOnTheOldKey_RefusesToStart_AfterARotation()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            await using (var appA = App(db, keyA))
                (await TestAccounts.CreateAsync(appA, "guard")).Client.Dispose();

            (await RotateAsync(db, keyA, keyB)).exitCode.Should().Be(0);

            await using var appOld = App(db, keyA);
            StartFailure(appOld).Should().Contain("does not match the key this database is encrypted with");
            await using var appNew = App(db, keyB);
            appNew.CreateClient().Dispose();
        }

        [Fact]
        public async Task WrongCurrentKey_IsRefused_AndNothingChanges()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB, keyC) = (NewKey(), NewKey(), NewKey());
            var clock = new TestClock();
            await using (var appA = App(db, keyA, clock))
                await SeedAsync(appA, clock);
            var before = await SnapshotAsync(db);

            var (exit, output) = await RotateAsync(db, keyC, keyB);
            exit.Should().Be(1);
            output.Should().Contain("not the key this database is encrypted with");
            AssertNoSecrets(output, before, keyA, keyB, keyC);
            (await SnapshotAsync(db)).Should().Equal(before);

            // Also when the database has no key state yet (an API of this version never started): nothing opens with the key
            await db.ExecuteAsync("DELETE FROM MasterKeyState");
            before = await SnapshotAsync(db);
            (exit, output) = await RotateAsync(db, keyC, keyB);
            exit.Should().Be(1);
            output.Should().Contain("wrong key");
            (await SnapshotAsync(db)).Should().Equal(before);
        }

        [Fact]
        public async Task DryRun_ChangesNothing_AndWorksWithoutANewKey()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            var clock = new TestClock();
            await using (var appA = App(db, keyA, clock))
                await SeedAsync(appA, clock);
            var before = await SnapshotAsync(db);

            var (exit, output) = await RotateAsync(db, keyA, keyB, ["--dry-run"]);
            exit.Should().Be(0, output);
            output.Should().Contain("DRY RUN").And.Contain("Files.WrappedKey: 4 re-wrapped").And.Contain("Shares.TokenCipher: 1 re-wrapped").And.Contain("Dry run:");
            (await SnapshotAsync(db)).Should().Equal(before);

            (exit, output) = await RotateAsync(db, keyA, null, ["--dry-run"]);
            exit.Should().Be(0, output);
            output.Should().Contain("throwaway key");
            (await SnapshotAsync(db)).Should().Equal(before);

            // The dry run did not rotate: the real run still works
            (await RotateAsync(db, keyA, keyB)).exitCode.Should().Be(0);
        }

        [Fact]
        public async Task RunningAgainAfterSuccess_SaysAlreadyRotated_AndChangesNothing()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            var clock = new TestClock();
            await using (var appA = App(db, keyA, clock))
                await SeedAsync(appA, clock);

            (await RotateAsync(db, keyA, keyB)).exitCode.Should().Be(0);
            var rotated = await SnapshotAsync(db);

            var (exit, output) = await RotateAsync(db, keyA, keyB);
            exit.Should().Be(0, output);
            output.Should().Contain("already encrypted with the new key");
            (await SnapshotAsync(db)).Should().Equal(rotated);
        }

        [Fact]
        public async Task AnInterruptedRotation_ChangesNothing_AndRunningAgainCompletes()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            var clock = new TestClock();
            Data data;
            await using (var appA = App(db, keyA, clock))
                data = await SeedAsync(appA, clock);
            var before = await SnapshotAsync(db);

            // An error after the first table has been rewritten
            var (exit, output) = await RotateAsync(db, keyA, keyB, afterTable: table =>
                table == "Files.WrappedKey" ? throw new IOException("simulated failure") : Task.CompletedTask);
            exit.Should().Be(1);
            output.Should().Contain("simulated failure").And.Contain("nothing was changed");
            (await SnapshotAsync(db)).Should().Equal(before);

            await WaitForNoOtherSessionsAsync(db);

            // The session dies part-way (as if the process or the connection was killed)
            (exit, output) = await RotateAsync(db, keyA, keyB, afterTable: async table =>
            {
                if (table != "Files.WrappedKey")
                    return;
                var session = await db.ScalarAsync<int>(
                    "SELECT TOP 1 session_id FROM sys.dm_exec_sessions WHERE program_name = @Name AND database_id = DB_ID() ORDER BY login_time DESC",
                    ("@Name", MasterKeyRotation.ApplicationName));
                await db.ExecuteAsync($"KILL {session}");
            });
            exit.Should().Be(1, output);
            (await SnapshotAsync(db)).Should().Equal(before);

            await WaitForNoOtherSessionsAsync(db);

            // The API on the old key still works
            await using (var appA = App(db, keyA, clock))
                await VerifyWorksAsync(appA, clock, data);

            await WaitForNoOtherSessionsAsync(db);

            // Running it again completes
            (await RotateAsync(db, keyA, keyB)).exitCode.Should().Be(0);
            await using var appB = App(db, keyB, clock);
            await VerifyWorksAsync(appB, clock, data);
        }

        [Fact]
        public async Task Refusals_ChangeNothing_AndNeverEchoKeysOrArguments()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            await using (var appA = App(db, keyA))
                (await TestAccounts.CreateAsync(appA, "refuse")).Client.Dispose();
            var before = await SnapshotAsync(db);

            // New key the same as the current one
            var (exit, output) = await RotateAsync(db, keyA, keyA);
            exit.Should().Be(1);
            output.Should().Contain("same as the current key").And.NotContain(keyA);

            // New key missing, or not a key
            (exit, output) = await RotateAsync(db, keyA, null);
            exit.Should().Be(1);
            output.Should().Contain("ENCRYPTION_MASTER_KEY_NEW");
            (exit, output) = await RotateAsync(db, keyA, "not-a-key");
            exit.Should().Be(1);
            output.Should().NotContain("not-a-key");

            // An argument that looks like a pasted key: usage error that doesn't repeat it
            (exit, output) = await RotateAsync(db, keyA, keyB, [keyB]);
            exit.Should().Be(2);
            output.Should().Contain("Usage").And.NotContain(keyB);
            (exit, output) = await RotateAsync(db, keyA, keyB, [$"--new-key={keyB}"]);
            exit.Should().Be(2);
            output.Should().NotContain(keyB);

            // The API is running (a started host holds the shared lock)
            await using (var appRunning = App(db, keyA))
            {
                appRunning.CreateClient().Dispose();
                (exit, output) = await RotateAsync(db, keyA, keyB);
                exit.Should().Be(1);
                output.Should().Contain("still running");
            }

            (await SnapshotAsync(db)).Should().Equal(before);

            // Once the API is stopped it can rotate
            (await RotateAsync(db, keyA, keyB)).exitCode.Should().Be(0);
        }

        [Fact]
        public async Task AnyOtherConnectionToTheDatabase_BlocksTheCommand_EvenWithoutTheApplock()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            await using (var appA = App(db, keyA))
                (await TestAccounts.CreateAsync(appA, "other")).Client.Dispose();

            // Stands in for an API that holds no lock (an older version, or one whose guard connection dropped)
            var builder = new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false, ApplicationName = "pretend-old-api" };
            await using (var other = new SqlConnection(builder.ConnectionString))
            {
                await other.OpenAsync();
                var before = await SnapshotAsync(db);
                var (exit, output) = await RotateAsync(db, keyA, keyB);
                exit.Should().Be(1);
                output.Should().Contain("Other connections are open").And.Contain("pretend-old-api x1").And.NotContain(keyA).And.NotContain(keyB);
                (await SnapshotAsync(db)).Should().Equal(before);
            }

            (await RotateAsync(db, keyA, keyB)).exitCode.Should().Be(0);
        }

        [Fact]
        public async Task UnreadableValues_AreListedAndRefused_OrSkippedOnRequest()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            var clock = new TestClock();
            Data data;
            await using (var appA = App(db, keyA, clock))
                data = await SeedAsync(appA, clock);

            // A wrapped key that no longer opens (wrong tag)
            var tampered = Convert.ToBase64String(RandomNumberGenerator.GetBytes(60));
            await db.ExecuteAsync("UPDATE Files SET WrappedKey = @Value WHERE GUID = @Guid", ("@Value", tampered), ("@Guid", data.ChunkedId));
            var before = await SnapshotAsync(db);

            var (exit, output) = await RotateAsync(db, keyA, keyB);
            exit.Should().Be(1);
            output.Should().Contain($"Files.WrappedKey {data.ChunkedId}").And.Contain("--skip-unreadable").And.NotContain(tampered);
            (await SnapshotAsync(db)).Should().Equal(before);

            (exit, output) = await RotateAsync(db, keyA, keyB, ["--skip-unreadable"]);
            exit.Should().Be(0, output);
            output.Should().Contain($"Files.WrappedKey {data.ChunkedId}").And.Contain("leaving them as they are");

            var after = await SnapshotAsync(db);
            after.Should().Contain($"Files|{data.ChunkedId}|{tampered}", "the unreadable row is left exactly as it was");
            after.Should().NotContain(before.Where(r => r.StartsWith("Files|") && !r.Contains(data.ChunkedId)), "the others are rotated");

            await using var appB = App(db, keyB, clock);
            var (response, client) = await SignInAsync(appB, data, code: NextCode(data, clock));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetByteArrayAsync($"/download/{data.SimpleId}")).Should().Equal(data.Simple);
        }

        [Fact]
        public async Task RecoveryCodes_AreKept_AcrossRotations_ByDefault()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB, keyC) = (NewKey(), NewKey(), NewKey());
            var clock = new TestClock();
            Data data;
            await using (var appA = App(db, keyA, clock))
                data = await SeedAsync(appA, clock);
            var codesBefore = (await SnapshotAsync(db)).Where(r => r.StartsWith("Recovery|")).ToList();

            var (exit, output) = await RotateAsync(db, keyA, keyB);
            exit.Should().Be(0, output);
            output.Should().Contain("TotpRecoveryCodes: 10 kept");
            (await SnapshotAsync(db)).Where(r => r.StartsWith("Recovery|")).Should().Equal(codesBefore, "the hashes can't change, so the hashing key is carried");
            (await SnapshotAsync(db)).Single(r => r.StartsWith("State|")).Split(':')[1].Should().NotBeEmpty("the carried key is stored, protected");

            List<string> fresh;
            await using (var appB = App(db, keyB, clock))
            {
                // An old code still works (once)
                var (response, _) = await SignInAsync(appB, data, recoveryCode: data.RecoveryCodes[0]);
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                (await SignInAsync(appB, data, recoveryCode: data.RecoveryCodes[0])).response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // New codes made under the new key work too
                var (_, client) = await SignInAsync(appB, data, code: NextCode(data, clock));
                var regenerated = await client.PostAsJsonAsync("/user/2fa/recovery-codes", new { code = NextCode(data, clock) });
                regenerated.StatusCode.Should().Be(HttpStatusCode.OK, await regenerated.Content.ReadAsStringAsync());
                fresh = (await JsonAsync(regenerated)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
                (await SignInAsync(appB, data, recoveryCode: fresh[0])).response.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // A second rotation carries the same key on again
            (exit, output) = await RotateAsync(db, keyB, keyC);
            exit.Should().Be(0, output);
            await using var appC = App(db, keyC, clock);
            (await SignInAsync(appC, data, recoveryCode: fresh[1])).response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await SignInAsync(appC, data, recoveryCode: fresh[1])).response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await StoredFingerprintAsync(db)).Should().Be(Fp(keyC));
        }

        [Fact]
        public async Task ResetRecoveryCodes_DeletesThem_AndNewOnesWorkUnderTheNewKey()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyB) = (NewKey(), NewKey());
            var clock = new TestClock();
            Data data;
            await using (var appA = App(db, keyA, clock))
                data = await SeedAsync(appA, clock);

            var (exit, output) = await RotateAsync(db, keyA, keyB, ["--reset-recovery-codes"]);
            exit.Should().Be(0, output);
            output.Should().Contain("TotpRecoveryCodes: 10 deleted");
            (await SnapshotAsync(db)).Should().NotContain(r => r.StartsWith("Recovery|"));
            (await SnapshotAsync(db)).Single(r => r.StartsWith("State|")).Split(':')[1].Should().BeEmpty("nothing is carried");

            await using var appB = App(db, keyB, clock);
            (await SignInAsync(appB, data, recoveryCode: data.RecoveryCodes[0])).response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var (_, client) = await SignInAsync(appB, data, code: NextCode(data, clock));
            var regenerated = await client.PostAsJsonAsync("/user/2fa/recovery-codes", new { code = NextCode(data, clock) });
            regenerated.StatusCode.Should().Be(HttpStatusCode.OK);
            var fresh = (await JsonAsync(regenerated)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
            (await SignInAsync(appB, data, recoveryCode: fresh[0])).response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task TheGuard_HandlesADatabaseWithoutKeyState()
        {
            await using var db = await FreshDatabase.CreateAsync();
            var (keyA, keyC) = (NewKey(), NewKey());

            // Empty database: the first start records its key
            (await StoredFingerprintAsync(db)).Should().BeNull();
            await using (var empty = App(db, keyA))
                empty.CreateClient().Dispose();
            (await StoredFingerprintAsync(db)).Should().Be(Fp(keyA));

            await using (var appA = App(db, keyA))
            {
                // Some encrypted data, or there is nothing to tell the keys apart by
                var account = await TestAccounts.CreateAsync(appA, "state");
                await TestAccounts.UploadAsync(account.Client, "state.bin", RandomBytes(100));
            }

            // Data under key A and no state row (a database from before this version): another key is refused...
            await db.ExecuteAsync("DELETE FROM MasterKeyState");
            await using (var wrong = App(db, keyC))
                StartFailure(wrong).Should().Contain("does not match the key this database is encrypted with");
            (await StoredFingerprintAsync(db)).Should().BeNull("a refused start doesn't record anything");

            // ...and the matching key records its fingerprint
            await using (var right = App(db, keyA))
                right.CreateClient().Dispose();
            (await StoredFingerprintAsync(db)).Should().Be(Fp(keyA));

            // With the state present, another key is refused too
            await using var again = App(db, keyC);
            StartFailure(again).Should().Contain("does not match");
        }

        [Fact]
        public void Fingerprint_IsStable_AndDiffersPerKey()
        {
            var key = RandomNumberGenerator.GetBytes(32);
            MasterKeyGuard.Fingerprint(key).Should().Be(MasterKeyGuard.Fingerprint(key)).And.HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
            MasterKeyGuard.Fingerprint(key).Should().NotBe(MasterKeyGuard.Fingerprint(RandomNumberGenerator.GetBytes(32)));
        }
    }
}
