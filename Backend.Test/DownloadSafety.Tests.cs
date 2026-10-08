using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Backend.Test
{
    // Any file type can be stored; downloads must never be something the browser renders
    public class DownloadSafetyTests(WebApplicationFactory<Program> factory)
        : IntegrationTestBase(factory, ("RateLimiting:share:PermitLimit", "1000"), ("RateLimiting:share-download:PermitLimit", "1000"))
    {
        private static async Task<HttpResponseMessage> UploadTypedAsync(HttpClient client, string name, string content, string contentType)
        {
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return await client.PostAsync("/upload", new MultipartFormDataContent { { file, "file", name } });
        }

        private static void ShouldBeSafeAttachment(HttpResponseMessage response, string fileName)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            response.Content.Headers.ContentDisposition.FileName.Should().Be(fileName);
            response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        }

        [Theory]
        [InlineData("page.html", "text/html", "<script>alert(1)</script>")]
        [InlineData("drawing.svg", "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
        [InlineData("setup.exe", "application/x-msdownload", "MZ")]
        [InlineData("archive.unknownext", "application/octet-stream", "data")]
        [InlineData("no-extension", "application/octet-stream", "data")]
        public async Task AnyFileType_Uploads_AndDownloadsAsANoSniffAttachment(string name, string contentType, string content)
        {
            var client = await NewUserAsync();
            (await UploadTypedAsync(client, name, content, contentType)).StatusCode.Should().Be(HttpStatusCode.OK);
            var fileId = Id((await ListFilesAsync(client)).Single(f => f.GetProperty("name").GetString() == name));

            var download = await client.GetAsync($"/download/{fileId}");
            ShouldBeSafeAttachment(download, name);
            (await download.Content.ReadAsStringAsync()).Should().Be(content);

            var zip = await client.PostAsJsonAsync("/download/zip", new { ids = new[] { fileId } });
            zip.StatusCode.Should().Be(HttpStatusCode.OK);
            zip.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            zip.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");

            // Through a share link: always octet-stream
            var share = await ReadJsonAsync(await client.PostAsJsonAsync("/shares", new { itemId = fileId }));
            var shared = await Anonymous().PostAsJsonAsync($"/s/{share.GetProperty("token").GetString()}/download", new { });
            ShouldBeSafeAttachment(shared, name);
            shared.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        }

        [Fact]
        public async Task ChunkedUploads_AcceptAnyFileType()
        {
            var client = await NewUserAsync();
            var data = Encoding.UTF8.GetBytes("<html></html>");
            var start = await ReadJsonAsync(await client.PostAsJsonAsync("/uploads", new { name = "index.html", size = data.Length, mimeType = "text/html" }));
            var uploadId = start.GetProperty("uploadId").GetString();

            var chunk = new ByteArrayContent(data);
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await client.PutAsync($"/uploads/{uploadId}/chunks/0", chunk)).StatusCode.Should().Be(HttpStatusCode.OK);
            var done = await ReadJsonAsync(await client.PostAsync($"/uploads/{uploadId}/complete", null));

            ShouldBeSafeAttachment(await client.GetAsync($"/download/{done.GetProperty("id").GetString()}"), "index.html");
        }
    }
}
