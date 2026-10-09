using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    public class TrashEndpointsTests(WebApplicationFactory<Program> factory) : IntegrationTestBase(factory)
    {
        private static async Task<JsonElement[]> ListTrashAsync(HttpClient client)
        {
            var response = await client.GetAsync("/trash");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await ReadArrayAsync(response);
        }

        private static Task<HttpResponseMessage> RestoreAsync(HttpClient client, params string[] ids) =>
            client.PostAsJsonAsync("/trash/restore", new { ids });

        private static async Task<long> UsedBytesAsync(HttpClient client) =>
            (await ReadJsonAsync(await client.GetAsync("/user/usage"))).GetProperty("used").GetInt64();

        [Fact]
        public async Task Delete_MovesToBin_StillCountsTowardsQuota_AndRestoresToTheSameFolder()
        {
            var client = await NewUserAsync();
            var folderId = await CreateFolderAsync(client, "Work");
            var subId = await CreateFolderAsync(client, "Old", folderId);
            var fileId = await UploadFileAsync(client, "plan.txt", "twelve bytes", subId);
            var used = await UsedBytesAsync(client);

            (await client.DeleteAsync($"/delete/{subId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            Paths(await ListFilesAsync(client)).Should().Contain("/Work").And.NotContain(new[] { "/Work/Old", "/Work/Old/plan.txt" });
            (await client.GetAsync($"/download/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            File.Exists(StoredFilePath(fileId)).Should().BeTrue();
            (await UsedBytesAsync(client)).Should().Be(used);

            var entry = (await ListTrashAsync(client)).Single();
            entry.GetProperty("_id").GetString().Should().Be(subId);
            entry.GetProperty("name").GetString().Should().Be("Old");
            entry.GetProperty("isDirectory").GetBoolean().Should().BeTrue();
            entry.GetProperty("originalFolder").GetString().Should().Be("/Work");
            entry.GetProperty("size").GetInt64().Should().Be(12);
            (entry.GetProperty("purgeAt").GetDateTime() - entry.GetProperty("deletedAt").GetDateTime())
                .Should().Be(TimeSpan.FromDays(30));

            // Deleting it again (or something inside it) is a 404: it's no longer in the file list
            (await client.DeleteAsync($"/delete/{subId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.DeleteAsync($"/delete/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await RestoreAsync(client, subId)).StatusCode.Should().Be(HttpStatusCode.OK);
            Paths(await ListFilesAsync(client)).Should().Contain(new[] { "/Work/Old", "/Work/Old/plan.txt" });
            (await client.GetStringAsync($"/download/{fileId}")).Should().Be("twelve bytes");
            (await ListTrashAsync(client)).Should().BeEmpty();
        }

        [Fact]
        public async Task Restore_GoesToTheRoot_WhenTheFolderIsGone_AndRenamesOnAClash()
        {
            var client = await NewUserAsync();
            var folderId = await CreateFolderAsync(client, "Projects");
            var fileId = await UploadFileAsync(client, "notes.txt", "inside", folderId);

            // The file goes in the bin first, then its folder
            (await client.DeleteAsync($"/delete/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.DeleteAsync($"/delete/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ListTrashAsync(client)).Select(e => e.GetProperty("_id").GetString()).Should().BeEquivalentTo(fileId, folderId);

            // A new root item takes the name in the meantime
            await UploadFileAsync(client, "notes.txt", "newer");

            // Its folder is in the bin, so the file comes back in the root, numbered
            (await RestoreAsync(client, fileId)).StatusCode.Should().Be(HttpStatusCode.OK);
            var files = await ListFilesAsync(client);
            Paths(files).Should().Contain(new[] { "/notes.txt", "/notes (1).txt" });
            (await client.GetStringAsync($"/download/{fileId}")).Should().Be("inside");

            // Restoring the folder brings back only what was deleted with it
            (await RestoreAsync(client, folderId)).StatusCode.Should().Be(HttpStatusCode.OK);
            Paths(await ListFilesAsync(client)).Should().Contain("/Projects").And.NotContain("/Projects/notes.txt");
        }

        [Fact]
        public async Task DeletingAFolderForGood_KeepsItemsThatWentInTheBinSeparately()
        {
            var client = await NewUserAsync();
            var folderId = await CreateFolderAsync(client, "Box");
            var keptId = await UploadFileAsync(client, "kept.txt", "kept", folderId);
            var goneId = await UploadFileAsync(client, "gone.txt", "gone", folderId);

            (await client.DeleteAsync($"/delete/{keptId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.DeleteAsync($"/delete/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.DeleteAsync($"/trash/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(StoredFilePath(goneId)).Should().BeFalse();
            File.Exists(StoredFilePath(keptId)).Should().BeTrue();

            var entry = (await ListTrashAsync(client)).Single();
            entry.GetProperty("_id").GetString().Should().Be(keptId);
            entry.GetProperty("originalFolder").GetString().Should().Be("/Box");

            (await RestoreAsync(client, keptId)).StatusCode.Should().Be(HttpStatusCode.OK);
            Paths(await ListFilesAsync(client)).Should().Contain("/kept.txt");
        }

        [Fact]
        public async Task EmptyingTheBin_DeletesStoredFiles_AndFreesTheQuota()
        {
            var client = await NewUserAsync();
            var a = await UploadFileAsync(client, "a.txt", "aaaa");
            var b = await UploadFileAsync(client, "b.txt", "bbbb");
            var before = await UsedBytesAsync(client);

            (await DeleteWithBodyAsync(client, "/delete", new { ids = new[] { a, b } })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await UsedBytesAsync(client)).Should().Be(before);

            var empty = await client.DeleteAsync("/trash");
            empty.StatusCode.Should().Be(HttpStatusCode.OK);
            (await empty.Content.ReadAsStringAsync()).Should().Contain("Deleted 2 item(s)");

            (await ListTrashAsync(client)).Should().BeEmpty();
            File.Exists(StoredFilePath(a)).Should().BeFalse();
            File.Exists(StoredFilePath(b)).Should().BeFalse();
            (await UsedBytesAsync(client)).Should().Be(before - 8);
        }

        [Fact]
        public async Task BinnedFolders_CantBeUsedAsADestination()
        {
            var client = await NewUserAsync();
            var folderId = await CreateFolderAsync(client, "Binned");
            var fileId = await UploadFileAsync(client, "loose.txt", "x");
            (await client.DeleteAsync($"/delete/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await UploadAsync(client, "into.txt", "x", folderId)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.PostAsJsonAsync("/folder", new { name = "sub", parentId = folderId })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.PutAsJsonAsync("/move", new { sourceIds = new[] { fileId }, destinationId = folderId })).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // Its name is free again
            await CreateFolderAsync(client, "Binned");
        }

        [Fact]
        public async Task Delete_WhenSqlServerPicksItAsADeadlockVictim_IsRetriedAndStillSucceeds()
        {
            var client = await NewUserAsync();
            var rootId = await CreateFolderAsync(client, "Root");
            var childId = await CreateFolderAsync(client, "Child", rootId);

            // The bin update locks the root, then needs the child
            var response = await TestDatabase.RequestAsDeadlockVictimAsync(Factory, childId, rootId,
                () => client.DeleteAsync($"/delete/{rootId}"));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await ListTrashAsync(client)).Select(Id).Should().Equal(rootId);
        }

        [Fact]
        public async Task Restore_WhenSqlServerPicksItAsADeadlockVictim_IsRetriedAndStillSucceeds()
        {
            var client = await NewUserAsync();
            var rootId = await CreateFolderAsync(client, "Root");
            var childId = await CreateFolderAsync(client, "Child", rootId);
            (await client.DeleteAsync($"/delete/{rootId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            // The restore updates the paths below the folder, then the folder itself
            var response = await TestDatabase.RequestAsDeadlockVictimAsync(Factory, rootId, childId,
                () => RestoreAsync(client, rootId));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Paths(await ListFilesAsync(client)).Should().Contain(new[] { "/Root", "/Root/Child" });
            (await ListTrashAsync(client)).Should().BeEmpty();
        }

        [Fact]
        public async Task BinEndpoints_RequireLogin_ValidateInput_AndOnlyTouchYourOwnItems()
        {
            var owner = await NewUserAsync();
            var other = await NewUserAsync();
            var fileId = await UploadFileAsync(owner, "private.txt", "private");
            var liveId = await UploadFileAsync(owner, "live.txt", "live");
            (await owner.DeleteAsync($"/delete/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            var anonymous = Anonymous();
            (await anonymous.GetAsync("/trash")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await RestoreAsync(anonymous, fileId)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.DeleteAsync($"/trash/{fileId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.DeleteAsync("/trash")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            (await ListTrashAsync(other)).Should().BeEmpty();
            (await RestoreAsync(other, fileId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync($"/trash/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync($"/delete/{liveId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync("/trash")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ListTrashAsync(owner)).Should().ContainSingle();

            (await owner.PostAsJsonAsync("/trash/restore", new { ids = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await RestoreAsync(owner, "not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await RestoreAsync(owner, liveId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await RestoreAsync(owner, fileId, Guid.NewGuid().ToString())).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await owner.DeleteAsync($"/trash/{liveId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // Nothing changed by the rejected requests
            (await ListTrashAsync(owner)).Should().ContainSingle();
            Paths(await ListFilesAsync(owner)).Should().Contain("/live.txt").And.NotContain("/private.txt");
        }

        [Fact]
        public async Task CleanupService_PurgesEntriesOlderThanTheRetentionPeriod()
        {
            var client = await NewUserAsync();
            var oldId = await UploadFileAsync(client, "old.txt", "old");
            var recentId = await UploadFileAsync(client, "recent.txt", "recent");
            (await DeleteWithBodyAsync(client, "/delete", new { ids = new[] { oldId, recentId } })).StatusCode.Should().Be(HttpStatusCode.OK);

            // Simulate the old one having been in the bin for 31 days
            await ExecuteSqlAsync("UPDATE Files SET DeletedAt = DATEADD(day, -31, SYSUTCDATETIME()) WHERE TrashRootId = @Id", ("@Id", oldId));

            var cleanup = Factory.Services.GetServices<IHostedService>().OfType<StorageCleanupService>().Single();
            await cleanup.RunOnceAsync();

            (await ListTrashAsync(client)).Select(e => e.GetProperty("_id").GetString()).Should().BeEquivalentTo(recentId);
            File.Exists(StoredFilePath(oldId)).Should().BeFalse();
            File.Exists(StoredFilePath(recentId)).Should().BeTrue();
        }
    }
}
