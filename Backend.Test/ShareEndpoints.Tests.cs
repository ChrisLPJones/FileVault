using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    public class ShareEndpointsTests(WebApplicationFactory<Program> factory)
        : IntegrationTestBase(factory, ("RateLimiting:share:PermitLimit", "1000"), ("RateLimiting:share-download:PermitLimit", "1000"))
    {
        private static async Task<(string id, string token)> CreateShareAsync(HttpClient client, object body)
        {
            var response = await client.PostAsJsonAsync("/shares", body);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var json = await ReadJsonAsync(response);

            var token = json.GetProperty("token").GetString()!;
            json.GetProperty("path").GetString().Should().Be($"/s/{token}");
            return (json.GetProperty("id").GetString()!, token);
        }

        private static async Task<JsonElement[]> ListSharesAsync(HttpClient client)
        {
            var response = await client.GetAsync("/shares");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await ReadArrayAsync(response);
        }

        private static Task<HttpResponseMessage> DownloadAsync(HttpClient client, string token, string? password = null) =>
            client.PostAsJsonAsync($"/s/{token}/download", new { password });

        [Fact]
        public async Task FileLink_ShowsDetails_AndDownloadsWithoutLogin()
        {
            var owner = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "report.txt", "shared contents");

            var (shareId, token) = await CreateShareAsync(owner, new { itemId = fileId });
            token.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");

            var visitor = Anonymous();
            var info = await ReadJsonAsync(await visitor.GetAsync($"/s/{token}"));
            info.GetProperty("name").GetString().Should().Be("report.txt");
            info.GetProperty("size").GetInt64().Should().Be("shared contents".Length);
            info.GetProperty("isDirectory").GetBoolean().Should().BeFalse();
            info.GetProperty("passwordRequired").GetBoolean().Should().BeFalse();

            var download = await DownloadAsync(visitor, token);
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            download.Content.Headers.ContentDisposition!.FileName.Should().Be("report.txt");
            (await download.Content.ReadAsStringAsync()).Should().Be("shared contents");

            // The owner's list shows the link with its item name and download count
            var listed = (await ListSharesAsync(owner)).Single();
            listed.GetProperty("id").GetString().Should().Be(shareId);
            listed.GetProperty("name").GetString().Should().Be("report.txt");
            listed.GetProperty("downloadCount").GetInt32().Should().Be(1);
            listed.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
            // The link can be copied again later
            listed.GetProperty("token").GetString().Should().Be(token);
            listed.GetProperty("path").GetString().Should().Be($"/s/{token}");
            listed.GetProperty("passwordViewable").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task FolderLink_ListsContents_AndDownloadsAsZip()
        {
            var owner = await NewUserAsync();
            var folderId = await CreateFolderAsync(owner, "Holiday");
            var subId = await CreateFolderAsync(owner, "Day 1", folderId);
            await UploadFileAsync(owner, "notes.txt", "notes", folderId);
            await UploadFileAsync(owner, "beach.txt", "sand", subId);

            var (_, token) = await CreateShareAsync(owner, new { itemId = folderId });

            var visitor = Anonymous();
            var info = await ReadJsonAsync(await visitor.GetAsync($"/s/{token}"));
            info.GetProperty("isDirectory").GetBoolean().Should().BeTrue();
            info.GetProperty("size").GetInt64().Should().Be(9);
            info.GetProperty("files").EnumerateArray()
                .Select(f => (f.GetProperty("path").GetString(), f.GetProperty("isDirectory").GetBoolean()))
                .Should().BeEquivalentTo(new[] { ("Day 1", true), ("Day 1/beach.txt", false), ("notes.txt", false) });

            var download = await DownloadAsync(visitor, token);
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
            using var zip = new ZipArchive(await download.Content.ReadAsStreamAsync());
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("Holiday/", "Holiday/Day 1/", "Holiday/Day 1/beach.txt", "Holiday/notes.txt");
        }

        [Fact]
        public async Task PasswordLink_HidesDetails_UntilThePasswordIsGiven()
        {
            var owner = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "secret.txt", "classified");
            var (_, token) = await CreateShareAsync(owner, new { itemId = fileId, password = "open sesame" });

            var visitor = Anonymous();
            var locked = await (await visitor.GetAsync($"/s/{token}")).Content.ReadAsStringAsync();
            locked.Should().Contain("\"passwordRequired\":true").And.NotContain("secret.txt");

            (await visitor.PostAsJsonAsync($"/s/{token}", new { password = "wrong one" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await visitor.PostAsJsonAsync($"/s/{token}", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var unlocked = await visitor.PostAsJsonAsync($"/s/{token}", new { password = "open sesame" });
            unlocked.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadJsonAsync(unlocked)).GetProperty("name").GetString().Should().Be("secret.txt");

            (await DownloadAsync(visitor, token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await DownloadAsync(visitor, token, "wrong one")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await (await DownloadAsync(visitor, token, "open sesame")).Content.ReadAsStringAsync()).Should().Be("classified");

            // The share page posts a plain form so the browser streams the download
            var form = await visitor.PostAsync($"/s/{token}/download",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = "open sesame" }));
            form.StatusCode.Should().Be(HttpStatusCode.OK);
            (await form.Content.ReadAsStringAsync()).Should().Be("classified");

            (await ListSharesAsync(owner)).Single().GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task ExpiredRevokedAndUnknownLinks_AllLookTheSame()
        {
            var owner = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "a.txt", "a");
            var (expiredId, expired) = await CreateShareAsync(owner, new { itemId = fileId, expiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            var (revokedId, revoked) = await CreateShareAsync(owner, new { itemId = fileId });

            // Simulate the expiry date passing
            await ExecuteSqlAsync("UPDATE Shares SET ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE Id = @Id", ("@Id", Guid.Parse(expiredId)));
            (await owner.DeleteAsync($"/shares/{revokedId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            var visitor = Anonymous();
            var unknown = new string('A', 43);
            var bodies = new List<string>();
            foreach (var token in new[] { expired, revoked, unknown, "not-a-token", "../../etc" })
            {
                var get = await visitor.GetAsync($"/s/{Uri.EscapeDataString(token)}");
                get.StatusCode.Should().Be(HttpStatusCode.NotFound);
                bodies.Add(await get.Content.ReadAsStringAsync());

                (await DownloadAsync(visitor, token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            bodies.Distinct().Should().ContainSingle();

            // Expired links stay listed (marked by their date); revoked ones don't
            var listed = await ListSharesAsync(owner);
            listed.Select(s => s.GetProperty("id").GetString()).Should().BeEquivalentTo(expiredId);
            (await owner.DeleteAsync($"/shares/{revokedId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task CreateShare_ValidatesInput()
        {
            var owner = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "b.txt", "b");

            (await owner.PostAsJsonAsync("/shares", new { itemId = Guid.NewGuid().ToString() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await owner.PostAsJsonAsync("/shares", new { itemId = "not-a-guid" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await owner.PostAsJsonAsync("/shares", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await owner.PostAsJsonAsync("/shares", new { itemId = fileId, expiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await owner.PostAsJsonAsync("/shares", new { itemId = fileId, password = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await owner.PostAsJsonAsync("/shares", new { itemId = fileId, password = new string('x', 73) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await owner.DeleteAsync("/shares/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // An empty password means no password
            var (_, token) = await CreateShareAsync(owner, new { itemId = fileId, password = "" });
            (await Anonymous().GetStringAsync($"/s/{token}")).Should().Contain("\"passwordRequired\":false");
        }

        [Fact]
        public async Task OwnerEndpoints_RequireLogin_AndOnlySeeYourOwnLinks()
        {
            var owner = await NewUserAsync();
            var other = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "mine.txt", "mine");
            var (shareId, token) = await CreateShareAsync(owner, new { itemId = fileId });

            var anonymous = Anonymous();
            (await anonymous.PostAsJsonAsync("/shares", new { itemId = fileId })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.GetAsync("/shares")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.DeleteAsync($"/shares/{shareId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Another user can't share, list or revoke the owner's items and links
            (await other.PostAsJsonAsync("/shares", new { itemId = fileId })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ListSharesAsync(other)).Should().BeEmpty();
            (await other.DeleteAsync($"/shares/{shareId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await anonymous.GetAsync($"/s/{token}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DeletingAnItem_RemovesLinksToItAndEverythingInside()
        {
            var owner = await NewUserAsync();
            var folderId = await CreateFolderAsync(owner, "Album");
            var innerId = await UploadFileAsync(owner, "photo.txt", "pixels", folderId);
            var keptId = await UploadFileAsync(owner, "kept.txt", "kept");

            var (_, folderToken) = await CreateShareAsync(owner, new { itemId = folderId });
            var (_, innerToken) = await CreateShareAsync(owner, new { itemId = innerId });
            var (keptShareId, keptToken) = await CreateShareAsync(owner, new { itemId = keptId });

            (await owner.DeleteAsync($"/delete/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            // In the bin: the links stop working but are kept, and work again after a restore
            var visitor = Anonymous();
            (await visitor.GetAsync($"/s/{folderToken}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await visitor.PostAsJsonAsync($"/s/{innerToken}/download", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ListSharesAsync(owner)).Count(s => s.GetProperty("itemInBin").GetBoolean()).Should().Be(2);

            (await owner.PostAsJsonAsync("/trash/restore", new { ids = new[] { folderId } })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await visitor.GetAsync($"/s/{innerToken}")).StatusCode.Should().Be(HttpStatusCode.OK);

            // Deleted for good: the links go too
            (await owner.DeleteAsync($"/delete/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await owner.DeleteAsync("/trash")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await visitor.GetAsync($"/s/{folderToken}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await visitor.GetAsync($"/s/{innerToken}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await visitor.GetAsync($"/s/{keptToken}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ListSharesAsync(owner)).Select(s => s.GetProperty("id").GetString()).Should().BeEquivalentTo(keptShareId);
        }

        [Fact]
        public async Task Password_IsShownOnlyToTheOwner_AndOnlyWhenAskedFor()
        {
            var owner = await NewUserAsync();
            var other = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "locked.txt", "locked");
            var (withPassword, token) = await CreateShareAsync(owner, new { itemId = fileId, password = "pässwörd 123" });
            var (withoutPassword, _) = await CreateShareAsync(owner, new { itemId = fileId });

            // The list says it can be shown but doesn't include it
            var list = await (await owner.GetAsync("/shares")).Content.ReadAsStringAsync();
            list.Should().NotContain("pässwörd");
            (await ListSharesAsync(owner)).Single(s => s.GetProperty("id").GetString() == withPassword)
                .GetProperty("passwordViewable").GetBoolean().Should().BeTrue();

            var shown = await owner.GetAsync($"/shares/{withPassword}/password");
            shown.StatusCode.Should().Be(HttpStatusCode.OK);
            shown.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await ReadJsonAsync(shown)).GetProperty("password").GetString().Should().Be("pässwörd 123");

            (await owner.GetAsync($"/shares/{withoutPassword}/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await owner.GetAsync($"/shares/{Guid.NewGuid()}/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await owner.GetAsync("/shares/not-a-guid/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync($"/shares/{withPassword}/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Anonymous().GetAsync($"/shares/{withPassword}/password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // The shown password is the one that opens the link
            (await Anonymous().PostAsJsonAsync($"/s/{token}", new { password = "pässwörd 123" })).StatusCode.Should().Be(HttpStatusCode.OK);

            (await owner.DeleteAsync($"/shares/{withPassword}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await owner.GetAsync($"/shares/{withPassword}/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task TokenAndPassword_AreStoredEncrypted_AndBoundToTheirLink()
        {
            var owner = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "bound.txt", "bound");
            var (firstId, firstToken) = await CreateShareAsync(owner, new { itemId = fileId, password = "first-secret" });
            var (secondId, _) = await CreateShareAsync(owner, new { itemId = fileId, password = "second-secret" });

            // Neither the token nor the password appears in the row
            var tokenCipher = (string?)await TestDatabase.ScalarAsync(Factory, "SELECT TokenCipher FROM Shares WHERE Id = @Id", ("@Id", Guid.Parse(firstId)));
            var passwordCipher = (string?)await TestDatabase.ScalarAsync(Factory, "SELECT PasswordCipher FROM Shares WHERE Id = @Id", ("@Id", Guid.Parse(firstId)));
            tokenCipher.Should().NotBeNullOrEmpty().And.NotContain(firstToken);
            passwordCipher.Should().NotBeNullOrEmpty().And.NotContain("first-secret");

            // Copied onto another link, the values don't decrypt (they're bound to the first link's Id)
            await ExecuteSqlAsync(@"UPDATE Shares SET TokenCipher = @Token, PasswordCipher = @Password WHERE Id = @Id",
                ("@Token", tokenCipher!), ("@Password", passwordCipher!), ("@Id", Guid.Parse(secondId)));
            var second = (await ListSharesAsync(owner)).Single(s => s.GetProperty("id").GetString() == secondId);
            second.GetProperty("token").ValueKind.Should().Be(JsonValueKind.Null);
            (await owner.GetAsync($"/shares/{secondId}/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task LinksFromBeforeTheChange_AreListedWithoutTheirLink_AndCanBeRevoked()
        {
            var owner = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "old.txt", "old");
            var (shareId, token) = await CreateShareAsync(owner, new { itemId = fileId, password = "old-password" });

            // As stored before tokens and passwords were kept encrypted
            await ExecuteSqlAsync("UPDATE Shares SET TokenCipher = NULL, PasswordCipher = NULL WHERE Id = @Id", ("@Id", Guid.Parse(shareId)));

            var listed = (await ListSharesAsync(owner)).Single();
            listed.GetProperty("token").ValueKind.Should().Be(JsonValueKind.Null);
            listed.GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
            listed.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
            listed.GetProperty("passwordViewable").GetBoolean().Should().BeFalse();
            (await owner.GetAsync($"/shares/{shareId}/password")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // The link itself still works, and can be revoked
            (await Anonymous().PostAsJsonAsync($"/s/{token}", new { password = "old-password" })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await owner.DeleteAsync($"/shares/{shareId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Anonymous().GetAsync($"/s/{token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task PublicEndpoints_AreRateLimited()
        {
            var limited = WithSettings(("RateLimiting:share:PermitLimit", "2"));
            var visitor = Anonymous(limited);
            var token = new string('B', 43);

            (await visitor.GetAsync($"/s/{token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await visitor.GetAsync($"/s/{token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var rejected = await visitor.GetAsync($"/s/{token}");
            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            rejected.Headers.Contains("Retry-After").Should().BeTrue();
        }
    }
}
