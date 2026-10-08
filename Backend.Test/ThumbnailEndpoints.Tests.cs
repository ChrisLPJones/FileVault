using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Backend.Test
{
    public class ThumbnailEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public ThumbnailEndpointsTests(WebApplicationFactory<Program> factory) => _factory = TestAccounts.WithoutLoginLimit(factory);

        private Task<TestAccounts.Account> NewUserAsync() => TestAccounts.CreateAsync(_factory.CreateClient(), "thumb");

        // A width x height image with a few shapes on it, encoded as PNG, JPEG or WebP
        private static byte[] MakeImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.SteelBlue);
                using var paint = new SKPaint { Color = SKColors.Orange };
                canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, 90);
            return data.ToArray();
        }

        // Skia can't encode BMPs, so build a 24-bit one by hand
        private static byte[] MakeBmp(int width, int height)
        {
            var rowSize = (width * 3 + 3) / 4 * 4;
            var pixels = rowSize * height;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("BM"u8.ToArray());
            writer.Write(54 + pixels); writer.Write(0); writer.Write(54);              // file header
            writer.Write(40); writer.Write(width); writer.Write(height);                // info header
            writer.Write((short)1); writer.Write((short)24); writer.Write(0); writer.Write(pixels);
            writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
            writer.Write(Enumerable.Repeat((byte)0x80, pixels).ToArray());
            return stream.ToArray();
        }

        // A JPEG with an EXIF orientation tag (6 = the camera was turned; show it rotated 90° clockwise)
        private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
        {
            byte[] tiff = [
                (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF header, IFD at 8
                0x01, 0x00,                                                // one entry
                0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,            // Orientation, SHORT, count 1
                (byte)orientation, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00];                                   // no next IFD
            var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
            var length = payload.Length + 2;
            byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
            return [.. jpeg[..2], .. app1, .. jpeg[2..]];
        }

        private static (int width, int height) Dimensions(byte[] image)
        {
            using var codec = SKCodec.Create(new MemoryStream(image));
            codec.Should().NotBeNull("the thumbnail should be a readable image");
            return (codec!.Info.Width, codec.Info.Height);
        }

        private string ThumbnailPath(string fileId) =>
            ThumbnailService.StoredPath(_factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("StorageRoot")!, fileId);

        // How many of these files still have a FileThumbnails row
        private async Task<int> ThumbnailRowsAsync(params string[] fileGuids)
        {
            var connectionString = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM FileThumbnails WHERE FileGuid IN (SELECT value FROM OPENJSON(@Guids))", connection);
            command.Parameters.AddWithValue("@Guids", JsonSerializer.Serialize(fileGuids));
            return (int)(await command.ExecuteScalarAsync())!;
        }

        [Fact]
        public async Task Thumbnail_IsMadeOnFirstRequest_Scaled_Encrypted_AndCacheable()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var original = MakeImage(1200, 600);
                var id = await TestAccounts.UploadAsync(client, "wide.png", original, "image/png");

                var response = await client.GetAsync($"/files/{id}/thumbnail");
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
                response.Headers.CacheControl!.Private.Should().BeTrue();
                response.Headers.CacheControl.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);
                response.Headers.ETag.Should().NotBeNull();

                var thumbnail = await response.Content.ReadAsByteArrayAsync();
                Dimensions(thumbnail).Should().Be((256, 128));
                thumbnail.Length.Should().BeLessThan(original.Length);

                // Stored encrypted, like the files themselves
                var stored = await File.ReadAllBytesAsync(ThumbnailPath(id));
                Encoding.ASCII.GetString(stored, 0, 4).Should().Be("FVE1");

                // The same thumbnail again, and a 304 when the browser already has it
                (await client.GetByteArrayAsync($"/files/{id}/thumbnail")).Should().Equal(thumbnail);
                var conditional = new HttpRequestMessage(HttpMethod.Get, $"/files/{id}/thumbnail");
                conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
                (await client.SendAsync(conditional)).StatusCode.Should().Be(HttpStatusCode.NotModified);

                // Thumbnails don't count towards the quota
                using var usage = JsonDocument.Parse(await client.GetStringAsync("/user/usage"));
                usage.RootElement.GetProperty("used").GetInt64().Should().Be(original.Length);
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Thumbnail_WorksForEachSupportedType_AndNeverEnlarges()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var cases = new (string name, byte[] bytes, (int, int) expected)[]
                {
                    ("photo.jpg", MakeImage(600, 900, SKEncodedImageFormat.Jpeg), (171, 256)),
                    ("picture.webp", MakeImage(512, 512, SKEncodedImageFormat.Webp), (256, 256)),
                    ("small.png", MakeImage(100, 40), (100, 40)),
                    ("bitmap.bmp", MakeBmp(300, 150), (256, 128)),
                    // A 1x1 GIF
                    ("pixel.gif", Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw=="), (1, 1)),
                };

                foreach (var (name, bytes, expected) in cases)
                {
                    var id = await TestAccounts.UploadAsync(client, name, bytes);
                    var response = await client.GetAsync($"/files/{id}/thumbnail");
                    response.StatusCode.Should().Be(HttpStatusCode.OK, name);
                    Dimensions(await response.Content.ReadAsByteArrayAsync()).Should().Be(expected, name);
                }
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Thumbnail_OfALargePng_IsComplete()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                // Noise doesn't compress, so the file spans many 64 KB encryption chunks;
                // the bottom half is solid red and must still be there in the thumbnail
                using var bitmap = new SKBitmap(800, 800);
                var random = new Random(7);
                for (var y = 0; y < 800; y++)
                    for (var x = 0; x < 800; x++)
                        bitmap.SetPixel(x, y, y < 400 ? new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)) : SKColors.Red);
                using var image = SKImage.FromBitmap(bitmap);
                var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                png.Length.Should().BeGreaterThan(5 * FileEncryption.ChunkSize);

                var id = await TestAccounts.UploadAsync(client, "noise.png", png, "image/png");
                var thumbnail = await client.GetByteArrayAsync($"/files/{id}/thumbnail");

                using var decoded = SKBitmap.Decode(thumbnail);
                decoded.Width.Should().Be(256);
                var bottom = decoded.GetPixel(128, 250);
                bottom.Red.Should().BeGreaterThan(200);
                bottom.Green.Should().BeLessThan(60);
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Thumbnail_FollowsExifOrientation()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                // Stored landscape, but tagged to be shown rotated, so the thumbnail is portrait
                var jpeg = WithExifOrientation(MakeImage(400, 200, SKEncodedImageFormat.Jpeg), 6);
                var id = await TestAccounts.UploadAsync(client, "sideways.jpg", jpeg, "image/jpeg");

                var thumbnail = await client.GetByteArrayAsync($"/files/{id}/thumbnail");
                Dimensions(thumbnail).Should().Be((128, 256));
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Thumbnail_IsNotFound_ForOtherItems()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var textId = await TestAccounts.UploadAsync(client, "notes.txt", "hello"u8.ToArray(), "text/plain");
                var folderId = await TestAccounts.CreateFolderAsync(client, "Holiday.png"); // a folder, whatever its name
                var brokenId = await TestAccounts.UploadAsync(client, "broken.png", "not really a png"u8.ToArray(), "image/png");

                (await client.GetAsync($"/files/{textId}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await client.GetAsync($"/files/{folderId}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await client.GetAsync($"/files/{Guid.NewGuid()}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await client.GetAsync("/files/not-a-guid/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);

                // An image that can't be decoded has no thumbnail (and the failure is remembered)
                (await client.GetAsync($"/files/{brokenId}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await client.GetAsync($"/files/{brokenId}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                File.Exists(ThumbnailPath(brokenId)).Should().BeFalse();
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Thumbnail_RequiresLogin_AndOnlyServesTheOwnersFiles()
        {
            var owner = await NewUserAsync();
            var other = await NewUserAsync();
            try
            {
                var id = await TestAccounts.UploadAsync(owner.Client, "mine.png", MakeImage(300, 300), "image/png");

                (await _factory.CreateClient().GetAsync($"/files/{id}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // Not even after the owner has made it
                (await other.Client.GetAsync($"/files/{id}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await owner.Client.GetAsync($"/files/{id}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await other.Client.GetAsync($"/files/{id}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            }
            finally
            {
                await owner.Client.DeleteAsync("/user");
                await other.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Thumbnail_IsDeletedWithItsFile_AndWithTheAccount()
        {
            var (client, _, _) = await NewUserAsync();

            var folderId = await TestAccounts.CreateFolderAsync(client, "Pictures 2");
            var inFolder = await TestAccounts.UploadAsync(client, "a.png", MakeImage(50, 50), "image/png", folderId);
            var single = await TestAccounts.UploadAsync(client, "b.png", MakeImage(50, 50), "image/png");
            var kept = await TestAccounts.UploadAsync(client, "c.png", MakeImage(50, 50), "image/png");
            foreach (var id in new[] { inFolder, single, kept })
                (await client.GetAsync($"/files/{id}/thumbnail")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.DeleteAsync($"/delete/{single}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.DeleteAsync($"/delete/{folderId}")).StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(ThumbnailPath(single)).Should().BeFalse();
            File.Exists(ThumbnailPath(inFolder)).Should().BeFalse();
            File.Exists(ThumbnailPath(kept)).Should().BeTrue();
            (await ThumbnailRowsAsync(single, inFolder, kept)).Should().Be(1);

            (await client.DeleteAsync("/user")).StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(ThumbnailPath(kept)).Should().BeFalse();
            (await ThumbnailRowsAsync(kept)).Should().Be(0);
        }

        [Fact]
        public async Task ConcurrentFirstRequests_AllGetTheThumbnail()
        {
            var (client, _, _) = await NewUserAsync();
            try
            {
                var id = await TestAccounts.UploadAsync(client, "busy.jpg", MakeImage(2000, 1500, SKEncodedImageFormat.Jpeg), "image/jpeg");

                var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetAsync($"/files/{id}/thumbnail")));
                responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
                var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsByteArrayAsync()));
                bodies.Should().OnlyContain(b => b.SequenceEqual(bodies[0]));
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }
    }
}
