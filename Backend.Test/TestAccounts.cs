using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    // Helpers for tests that need a throwaway account with some files. Delete the account
    // (DELETE /user) when the test is done.
    public static class TestAccounts
    {
        public const string Password = "TestPassw0rd";

        public record Account(HttpClient Client, string UserId, string Email);

        // The app with a login rate limit high enough for tests that create many accounts
        public static WebApplicationFactory<Program> WithoutLoginLimit(WebApplicationFactory<Program> factory) =>
            factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:auth:PermitLimit", "1000"));

        // Register and log in a new user; the client sends their access token
        public static async Task<Account> CreateAsync(HttpClient client, string prefix, string? email = null)
        {
            email ??= $"{prefix}_{Guid.NewGuid():N}@example.test";
            (await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = prefix, LastName = "User", Email = email, Password = Password }))
                .EnsureSuccessStatusCode();

            await LoginAsync(client, email);
            var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
            return new Account(client, new JwtSecurityTokenHandler().ReadJwtToken(token).Subject, email);
        }

        // Log an existing test account in; the client then sends its access token
        public static async Task<HttpClient> LoginAsync(HttpClient client, string email)
        {
            var login = await client.PostAsJsonAsync("/user/login", new LoginModel { Email = email, Password = Password });
            login.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("success").GetString()!;

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        // Upload a file and return its ID
        public static async Task<string> UploadAsync(HttpClient client, string fileName, byte[] bytes,
            string contentType = "application/octet-stream", string? parentId = null)
        {
            var content = new MultipartFormDataContent();
            if (parentId != null)
                content.Add(new StringContent(parentId), "parentId");
            content.Add(new ByteArrayContent(bytes) { Headers = { ContentType = MediaTypeHeaderValue.Parse(contentType) } }, "file", fileName);

            var response = await client.PostAsync("/upload", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var files = await ListAsync(client);
            var path = parentId == null ? $"/{fileName}" : null;
            return files.Last(f => f.GetProperty("name").GetString() == fileName &&
                (path == null || f.GetProperty("path").GetString() == path)).GetProperty("_id").GetString()!;
        }

        public static async Task<string> CreateFolderAsync(HttpClient client, string name, string? parentId = null)
        {
            var response = await client.PostAsJsonAsync("/folder", new { name, parentId });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("_id").GetString()!;
        }

        public static async Task<JsonElement[]> ListAsync(HttpClient client)
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("/files"));
            return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        public static async Task<JsonElement> GetItemAsync(HttpClient client, string id) =>
            (await ListAsync(client)).Single(f => f.GetProperty("_id").GetString() == id);
    }
}
