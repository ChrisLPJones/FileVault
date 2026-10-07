using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Backend.Test
{
    public class AvatarEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private const string Password = "TestPassw0rd";
        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        private readonly WebApplicationFactory<Program> _factory;

        public AvatarEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

        private async Task<(HttpClient client, string userId)> NewUserAsync()
        {
            var client = _factory.CreateClient();
            var email = $"avatar_{Guid.NewGuid():N}@example.test";
            (await client.PostAsJsonAsync("/user/register",
                new UserModel { Username = $"a_{Guid.NewGuid():N}"[..20], Email = email, Password = Password }))
                .EnsureSuccessStatusCode();

            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("success").GetString()!;

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (client, new JwtSecurityTokenHandler().ReadJwtToken(token).Subject);
        }

        private static Task<HttpResponseMessage> UploadAvatarAsync(HttpClient client, byte[] bytes, string contentType = "image/png") =>
            client.PutAsync("/user/avatar", new MultipartFormDataContent
            {
                { new ByteArrayContent(bytes) { Headers = { ContentType = MediaTypeHeaderValue.Parse(contentType) } }, "avatar", "me.png" }
            });

        private static byte[] FakePng(int length)
        {
            var bytes = new byte[length];
            new Random(length).NextBytes(bytes);
            PngSignature.CopyTo(bytes, 0);
            return bytes;
        }

        private string AvatarPath(string userId)
        {
            var storageRoot = _factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("StorageRoot")!;
            return Path.Combine(storageRoot, "avatars", userId);
        }

        private static async Task<JsonElement> InfoAsync(HttpClient client)
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("/user/info"));
            return json.RootElement.Clone();
        }

        [Fact]
        public async Task Avatar_RoundTrips_IsEncryptedOnDisk_AndCanBeRemoved()
        {
            var (client, userId) = await NewUserAsync();
            try
            {
                (await InfoAsync(client)).GetProperty("avatarUpdatedAt").ValueKind.Should().Be(JsonValueKind.Null);
                (await client.GetAsync("/user/avatar")).StatusCode.Should().Be(HttpStatusCode.NotFound);

                var image = FakePng(5000);
                (await UploadAvatarAsync(client, image)).StatusCode.Should().Be(HttpStatusCode.OK);

                (await InfoAsync(client)).GetProperty("avatarUpdatedAt").ValueKind.Should().Be(JsonValueKind.String);

                var get = await client.GetAsync("/user/avatar");
                get.StatusCode.Should().Be(HttpStatusCode.OK);
                get.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
                (await get.Content.ReadAsByteArrayAsync()).Should().Equal(image);

                var stored = await File.ReadAllBytesAsync(AvatarPath(userId));
                Encoding.ASCII.GetString(stored, 0, 4).Should().Be("FVE1");

                (await client.DeleteAsync("/user/avatar")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await client.GetAsync("/user/avatar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
                File.Exists(AvatarPath(userId)).Should().BeFalse();
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task Avatar_RejectsNonImages_AndOversizedFiles_AndDoesNotUseQuota()
        {
            var (client, _) = await NewUserAsync();
            try
            {
                // Named and labelled as a PNG, but the bytes aren't an image
                var notImage = await UploadAvatarAsync(client, Encoding.UTF8.GetBytes("definitely not an image"));
                notImage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await notImage.Content.ReadAsStringAsync()).Should().Contain("PNG, JPEG or WebP");

                var tooBig = await UploadAvatarAsync(client, FakePng(2 * 1024 * 1024 + 1));
                tooBig.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

                // A JPEG is accepted and doesn't count towards storage used
                var jpeg = new byte[3000];
                jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF;
                (await UploadAvatarAsync(client, jpeg, "image/jpeg")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await client.GetAsync("/user/avatar")).Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");

                using var usage = JsonDocument.Parse(await client.GetStringAsync("/user/usage"));
                usage.RootElement.GetProperty("used").GetInt64().Should().Be(0);
            }
            finally
            {
                await client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task DeletingAccount_RemovesAvatarFile()
        {
            var (client, userId) = await NewUserAsync();

            (await UploadAvatarAsync(client, FakePng(1000))).StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(AvatarPath(userId)).Should().BeTrue();

            (await client.DeleteAsync("/user")).StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(AvatarPath(userId)).Should().BeFalse();
        }
    }
}
