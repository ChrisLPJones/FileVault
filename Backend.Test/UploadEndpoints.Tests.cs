using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Backend.Test
{
    public class UploadEndpointsTests(WebApplicationFactory<Program> factory) : IntegrationTestBase(factory)
    {
        private const int Chunk = ChunkedUploadService.ChunkSize;

        private static byte[] RandomBytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static async Task<string> StartAsync(HttpClient client, string name, long size, string? parentId = null)
        {
            var response = await client.PostAsJsonAsync("/uploads", new { name, size, parentId, mimeType = "application/octet-stream" });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var json = await ReadJsonAsync(response);
            json.GetProperty("chunkSize").GetInt32().Should().Be(Chunk);
            return json.GetProperty("uploadId").GetString()!;
        }

        private static Task<HttpResponseMessage> PutChunkAsync(HttpClient client, string uploadId, int index, byte[] data, int offset, int length)
        {
            var content = new ByteArrayContent(data, offset, length);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return client.PutAsync($"/uploads/{uploadId}/chunks/{index}", content);
        }

        private static async Task SendChunksAsync(HttpClient client, string uploadId, byte[] data, params int[] indexes)
        {
            foreach (var i in indexes)
            {
                var offset = i * Chunk;
                var response = await PutChunkAsync(client, uploadId, i, data, offset, Math.Min(Chunk, data.Length - offset));
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            }
        }

        private static async Task<int[]> ReceivedAsync(HttpClient client, string uploadId)
        {
            var json = await ReadJsonAsync(await client.GetAsync($"/uploads/{uploadId}"));
            return json.GetProperty("receivedChunks").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        }

        private string UploadDir(HttpClient client, string uploadId)
        {
            var root = Factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("StorageRoot")!;
            return Directory.GetDirectories(Path.Combine(root, "tmp")).Select(d => Path.Combine(d, uploadId)).Single(Directory.Exists);
        }

        [Fact]
        public async Task ChunksInAnyOrder_ResumeAndComplete_IntoAnEncryptedFile()
        {
            var client = await NewUserAsync();
            var folderId = await CreateFolderAsync(client, "Big");
            var data = RandomBytes(2 * Chunk + 12_345, 1);

            var uploadId = await StartAsync(client, "video.bin", data.Length, folderId);
            (await ReadJsonAsync(await client.GetAsync($"/uploads/{uploadId}"))).GetProperty("chunkCount").GetInt32().Should().Be(3);

            // The last chunk first, then an interruption: only chunk 2 has arrived
            await SendChunksAsync(client, uploadId, data, 2);
            (await ReceivedAsync(client, uploadId)).Should().Equal(2);

            // Chunks wait encrypted on disk
            var tmp = UploadDir(client, uploadId);
            var stored = await File.ReadAllBytesAsync(Path.Combine(tmp, "2.part"));
            stored.AsSpan().IndexOf(data.AsSpan(2 * Chunk, 64)).Should().Be(-1);

            // Can't complete with chunks missing
            (await client.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Resume: send the rest; sending a chunk again just replaces it
            await SendChunksAsync(client, uploadId, data, 0, 1, 1);
            (await ReceivedAsync(client, uploadId)).Should().Equal(0, 1, 2);

            var complete = await client.PostAsync($"/uploads/{uploadId}/complete", null);
            complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
            var fileId = (await ReadJsonAsync(complete)).GetProperty("id").GetString()!;

            Paths(await ListFilesAsync(client)).Should().Contain("/Big/video.bin");
            (await client.GetByteArrayAsync($"/download/{fileId}")).Should().Equal(data);
            Encoding.ASCII.GetString((await File.ReadAllBytesAsync(StoredFilePath(fileId)))[..4]).Should().Be("FVE1");

            // The chunks and the upload are gone
            Directory.Exists(tmp).Should().BeFalse();
            (await client.GetAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Complete_GivesATakenNameANumber()
        {
            var client = await NewUserAsync();
            await UploadFileAsync(client, "notes.txt", "small");
            var data = RandomBytes(1000, 2);

            var uploadId = await StartAsync(client, "notes.txt", data.Length);
            await SendChunksAsync(client, uploadId, data, 0);
            var complete = await client.PostAsync($"/uploads/{uploadId}/complete", null);

            (await ReadJsonAsync(complete)).GetProperty("name").GetString().Should().Be("notes (1).txt");
            Paths(await ListFilesAsync(client)).Should().Contain(new[] { "/notes.txt", "/notes (1).txt" });
        }

        [Fact]
        public async Task Start_ChecksNameSizeFolderAndLimits()
        {
            var client = await NewUserAsync();
            var other = await NewUserAsync();
            var othersFolder = await CreateFolderAsync(other, "Theirs");

            async Task<HttpStatusCode> Start(object body) => (await client.PostAsJsonAsync("/uploads", body)).StatusCode;

            (await Start(new { name = "a/b.bin", size = 10 })).Should().Be(HttpStatusCode.BadRequest);
            (await Start(new { name = "", size = 10 })).Should().Be(HttpStatusCode.BadRequest);
            (await Start(new { name = "zero.bin", size = 0 })).Should().Be(HttpStatusCode.BadRequest);
            (await Start(new { name = "x.bin", size = 10, parentId = othersFolder })).Should().Be(HttpStatusCode.BadRequest);
            (await Start(new { name = "x.bin", size = 10, parentId = Guid.NewGuid().ToString() })).Should().Be(HttpStatusCode.BadRequest);
            (await Start(new { name = "huge.bin", size = 11L * 1024 * 1024 * 1024 })).Should().Be(HttpStatusCode.RequestEntityTooLarge);

            var usage = await ReadJsonAsync(await client.GetAsync("/user/usage"));
            usage.GetProperty("maxFileBytes").GetInt64().Should().Be(ChunkedUploadService.DefaultMaxFileBytes);
        }

        [Fact]
        public async Task UploadsInProgress_ReserveQuota()
        {
            var limited = WithSettings(("Storage:DefaultQuotaBytes", "1000000"), ("RateLimiting:auth:PermitLimit", "1000"));
            var client = await NewUserAsync(limited);

            (await client.PostAsJsonAsync("/uploads", new { name = "a.bin", size = 600_000 })).StatusCode.Should().Be(HttpStatusCode.OK);
            var second = await client.PostAsJsonAsync("/uploads", new { name = "b.bin", size = 600_000 });
            second.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await second.Content.ReadAsStringAsync()).Should().Contain("Not enough storage space");
        }

        [Fact]
        public async Task Chunks_MustHaveTheRightIndexAndLength()
        {
            var client = await NewUserAsync();
            var data = RandomBytes(Chunk + 100, 3);
            var uploadId = await StartAsync(client, "c.bin", data.Length);

            (await PutChunkAsync(client, uploadId, 0, data, 0, Chunk - 1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await PutChunkAsync(client, uploadId, 1, data, 0, 101)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await PutChunkAsync(client, uploadId, 2, data, 0, 100)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await PutChunkAsync(client, uploadId, -1, data, 0, 100)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReceivedAsync(client, uploadId)).Should().BeEmpty();

            (await PutChunkAsync(client, uploadId, 1, data, Chunk, 100)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReceivedAsync(client, uploadId)).Should().Equal(1);
        }

        [Fact]
        public async Task Uploads_NeedLogin_AndBelongToTheirOwner()
        {
            var owner = await NewUserAsync();
            var other = await NewUserAsync();
            var data = RandomBytes(500, 4);
            var uploadId = await StartAsync(owner, "mine.bin", data.Length);

            var anonymous = Anonymous();
            (await anonymous.PostAsJsonAsync("/uploads", new { name = "x", size = 1 })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await PutChunkAsync(anonymous, uploadId, 0, data, 0, data.Length)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.GetAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.DeleteAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            (await PutChunkAsync(other, uploadId, 0, data, 0, data.Length)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync("/uploads/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // Still intact for the owner
            await SendChunksAsync(owner, uploadId, data, 0);
            (await owner.PostAsync($"/uploads/{uploadId}/complete", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Cancel_DeletesTheChunks()
        {
            var client = await NewUserAsync();
            var data = RandomBytes(300, 5);
            var uploadId = await StartAsync(client, "cancel.bin", data.Length);
            await SendChunksAsync(client, uploadId, data, 0);
            var tmp = UploadDir(client, uploadId);

            (await client.DeleteAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            Directory.Exists(tmp).Should().BeFalse();
            (await client.GetAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.DeleteAsync($"/uploads/{uploadId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task CleanupService_RemovesUploadsAbandonedForADay()
        {
            var client = await NewUserAsync();
            var data = RandomBytes(300, 6);
            var stale = await StartAsync(client, "stale.bin", data.Length);
            var fresh = await StartAsync(client, "fresh.bin", data.Length);
            await SendChunksAsync(client, stale, data, 0);
            var staleDir = UploadDir(client, stale);

            await ExecuteSqlAsync("UPDATE Uploads SET CreatedAt = DATEADD(hour, -25, SYSUTCDATETIME()) WHERE Id = @Id", ("@Id", Guid.Parse(stale)));
            await Factory.Services.GetServices<IHostedService>().OfType<StorageCleanupService>().Single().RunOnceAsync();

            (await client.GetAsync($"/uploads/{stale}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            Directory.Exists(staleDir).Should().BeFalse();
            (await client.GetAsync($"/uploads/{fresh}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
