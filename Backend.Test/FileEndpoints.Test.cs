using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Backend.Test
{
    [TestCaseOrderer("Backend.Test.PriorityOrderer", "Backend.Test")]
    public class FileEndpointsTest : IClassFixture<WebApplicationFactory<Program>>
    {
        // Unique per test run so a failed earlier run doesn't block registration
        private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];
        private static readonly string TestUsername = $"testuser_{RunId}";
        private static readonly string TestEmail = $"test_{RunId}@address.com";
        private const string TestPassword = "testpassword";

        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;
        private static string? _jwt;
        private static string? _fileId;

        public FileEndpointsTest(WebApplicationFactory<Program> factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        private async Task AuthenticateAsync()
        {
            if (string.IsNullOrEmpty(_jwt))
                _jwt = await LoginAsync(_client, TestEmail, TestPassword);

            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        }

        private static async Task RegisterAsync(HttpClient client, string username, string email, string password)
        {
            UserModel user = new() { Username = username, Email = email, Password = password };
            StringContent content = new(JsonSerializer.Serialize(user), Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/user/register", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        private static async Task<string> LoginAsync(HttpClient client, string email, string password)
        {
            LoginModel loginUser = new() { Email = email, Password = password };
            StringContent loginContent = new(JsonSerializer.Serialize(loginUser), Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/user/login", loginContent);
            var content = await response.Content.ReadAsStringAsync();

            using var jsonDoc = JsonDocument.Parse(content);
            return jsonDoc.RootElement.GetProperty("success").GetString()!;
        }

        private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, string text, string? parentId = null) =>
            UploadBytesAsync(client, fileName, Encoding.UTF8.GetBytes(text), "text/plain", parentId);

        private static async Task<HttpResponseMessage> UploadBytesAsync(HttpClient client, string fileName, byte[] bytes, string contentType, string? parentId = null)
        {
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

            var multipartContent = new MultipartFormDataContent();
            if (parentId != null)
                multipartContent.Add(new StringContent(parentId), "parentId");
            multipartContent.Add(fileContent, "file", fileName);

            return await client.PostAsync("/upload", multipartContent);
        }

        private static async Task<string> CreateFolderAsync(HttpClient client, string name, string? parentId = null)
        {
            var response = await client.PostAsJsonAsync("/folder", new { name, parentId });
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return jsonDoc.RootElement.GetProperty("_id").GetString()!;
        }

        private static async Task<JsonElement[]> ListFilesAsync(HttpClient client)
        {
            var response = await client.GetAsync("/files");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return jsonDoc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        private string StoredFilePath(string guid)
        {
            var storageRoot = _factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("StorageRoot")!;
            return Path.Combine(storageRoot, guid);
        }

        [Fact, TestPriority(1)]
        public async Task RegisterUser_ShouldSucceed()
        {
            UserModel registerUser = new()
            {
                Username = TestUsername,
                Email = TestEmail,
                Password = TestPassword
            };
            string json = JsonSerializer.Serialize(registerUser);
            StringContent registerContent = new(json, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/user/register", registerContent);
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be($"{{\"success\":\"User {TestUsername} registered\"}}");
        }

        [Fact, TestPriority(2)]
        public async Task LoginUser_ShouldReturnJwt()
        {
            await AuthenticateAsync();

            _jwt.Should().NotBeNullOrEmpty();
        }

        [Fact, TestPriority(3)]
        public async Task GetUser_ShouldReturnInfo()
        {
            await AuthenticateAsync();

            var response = await _client.GetAsync("/user/info");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be($"{{\"username\":\"{TestUsername}\",\"email\":\"{TestEmail}\"}}");
        }

        [Fact, TestPriority(4)]
        public async Task UpdateUser_ShouldSucceed()
        {
            await AuthenticateAsync();

            UserModel updateUser = new()
            {
                Username = $"user_{RunId}",
                Email = $"updated_{RunId}@example.com",
                Password = "asdasd"
            };
            string updateUserJson = JsonSerializer.Serialize(updateUser);
            StringContent updateUserContent = new(updateUserJson, Encoding.UTF8, "application/json");

            var response = await _client.PutAsync("/user", updateUserContent);
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"Updated user info\"}");
        }

        [Fact, TestPriority(5)]
        public async Task UploadFile_ShouldSucceed()
        {
            await AuthenticateAsync();

            var response = await UploadAsync(_client, "test.txt", "Dummy file content");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"success\":\"File Uploaded: test.txt\"}");
        }

        [Fact, TestPriority(6)]
        public async Task ListFiles_ShouldContainUploadedFile()
        {
            await AuthenticateAsync();

            var files = await ListFilesAsync(_client);
            var uploaded = files.Single(f => f.GetProperty("path").GetString() == "/test.txt");

            uploaded.GetProperty("name").GetString().Should().Be("test.txt");
            _fileId = uploaded.GetProperty("_id").GetString();
            _fileId.Should().NotBeNullOrEmpty();
        }

        [Fact, TestPriority(7)]
        public async Task DownloadFile_ShouldReturnContent()
        {
            await AuthenticateAsync();

            var response = await _client.GetAsync($"/download/{_fileId}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("Dummy file content");
        }

        [Fact, TestPriority(8)]
        public async Task DeleteFile_ShouldSucceed()
        {
            await AuthenticateAsync();

            var response = await _client.DeleteAsync($"/delete/{_fileId}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be($"{{\"success\":\"File deleted: {_fileId}\"}}");
            File.Exists(StoredFilePath(_fileId!)).Should().BeFalse();
        }

        [Fact, TestPriority(9)]
        public async Task UploadFile_WithoutFile_ReturnsBadRequest()
        {
            await AuthenticateAsync();

            var multipartContent = new MultipartFormDataContent
            {
                { new StringContent(""), "parentId" }
            };

            var response = await _client.PostAsync("/upload", multipartContent);
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            content.Should().Be("{\"error\":\"No file uploaded\"}");
        }

        [Fact, TestPriority(10)]
        public async Task DeleteFolderAndChild_RemovesEverythingIncludingStoredFiles()
        {
            await AuthenticateAsync();

            var folderId = await CreateFolderAsync(_client, "parent");
            var subFolderId = await CreateFolderAsync(_client, "child", folderId);
            (await UploadAsync(_client, "inner.txt", "inner", folderId)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await UploadAsync(_client, "nested.txt", "nested", subFolderId)).StatusCode.Should().Be(HttpStatusCode.OK);

            var files = await ListFilesAsync(_client);
            var innerId = files.Single(f => f.GetProperty("path").GetString() == "/parent/inner.txt").GetProperty("_id").GetString()!;
            var nestedId = files.Single(f => f.GetProperty("path").GetString() == "/parent/child/nested.txt").GetProperty("_id").GetString()!;
            File.Exists(StoredFilePath(innerId)).Should().BeTrue();
            File.Exists(StoredFilePath(nestedId)).Should().BeTrue();

            // Select the folder and a file inside it, like a multi-select in the UI
            var request = new HttpRequestMessage(HttpMethod.Delete, "/delete")
            {
                Content = JsonContent.Create(new { ids = new[] { folderId, innerId } })
            };
            var response = await _client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(StoredFilePath(innerId)).Should().BeFalse();
            File.Exists(StoredFilePath(nestedId)).Should().BeFalse();

            var remaining = await ListFilesAsync(_client);
            remaining.Select(f => f.GetProperty("_id").GetString())
                .Should().NotContain(new[] { folderId, subFolderId, innerId, nestedId });
        }

        [Fact, TestPriority(11)]
        public async Task UploadAndCreateFolder_IntoAnotherUsersFolder_AreRejected()
        {
            await AuthenticateAsync();

            // Second user owns a folder
            var otherClient = _factory.CreateClient();
            var otherEmail = $"other_{RunId}@address.com";
            await RegisterAsync(otherClient, $"other_{RunId}", otherEmail, TestPassword);
            var otherJwt = await LoginAsync(otherClient, otherEmail, TestPassword);
            otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherJwt);

            try
            {
                var otherFolderId = await CreateFolderAsync(otherClient, "private");

                var uploadResponse = await UploadAsync(_client, "intruder.txt", "x", otherFolderId);
                uploadResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

                var folderResponse = await _client.PostAsJsonAsync("/folder", new { name = "intruder", parentId = otherFolderId });
                folderResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

                var otherFiles = await ListFilesAsync(otherClient);
                otherFiles.Should().ContainSingle();
            }
            finally
            {
                await otherClient.DeleteAsync("/user");
            }
        }

        private static string IdAt(JsonElement[] files, string path) =>
            files.Single(f => f.GetProperty("path").GetString() == path).GetProperty("_id").GetString()!;

        private static string[] Paths(JsonElement[] files) =>
            files.Select(f => f.GetProperty("path").GetString()!).ToArray();

        [Fact, TestPriority(12)]
        public async Task CreateFolder_DuplicateOrInvalidName_IsRejected()
        {
            await AuthenticateAsync();

            await CreateFolderAsync(_client, "dupe");

            var duplicate = await _client.PostAsJsonAsync("/folder", new { name = "DUPE" });
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

            var invalid = await _client.PostAsJsonAsync("/folder", new { name = "a/b" });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact, TestPriority(13)]
        public async Task RenameFolder_UpdatesChildPaths_AndRejectsConflicts()
        {
            await AuthenticateAsync();

            var folderId = await CreateFolderAsync(_client, "docs");
            (await UploadAsync(_client, "a.txt", "a", folderId)).StatusCode.Should().Be(HttpStatusCode.OK);
            await CreateFolderAsync(_client, "taken");

            var rename = await _client.PatchAsJsonAsync("/rename", new { id = folderId, newName = "papers" });
            rename.StatusCode.Should().Be(HttpStatusCode.OK);

            var paths = Paths(await ListFilesAsync(_client));
            paths.Should().Contain(new[] { "/papers", "/papers/a.txt" });
            paths.Should().NotContain(new[] { "/docs", "/docs/a.txt" });

            var conflict = await _client.PatchAsJsonAsync("/rename", new { id = folderId, newName = "taken" });
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);

            var invalid = await _client.PatchAsJsonAsync("/rename", new { id = folderId, newName = "bad/name" });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var missing = await _client.PatchAsJsonAsync("/rename", new { id = Guid.NewGuid().ToString(), newName = "x" });
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact, TestPriority(14)]
        public async Task MoveFolder_UpdatesTree_AndRejectsMovingIntoItself()
        {
            await AuthenticateAsync();

            var srcId = await CreateFolderAsync(_client, "src");
            var innerId = await CreateFolderAsync(_client, "inner", srcId);
            (await UploadAsync(_client, "deep.txt", "deep", innerId)).StatusCode.Should().Be(HttpStatusCode.OK);
            var destId = await CreateFolderAsync(_client, "dest");

            var move = await _client.PutAsJsonAsync("/move", new { sourceIds = new[] { srcId }, destinationId = destId });
            move.StatusCode.Should().Be(HttpStatusCode.OK);

            var paths = Paths(await ListFilesAsync(_client));
            paths.Should().Contain(new[] { "/dest/src", "/dest/src/inner", "/dest/src/inner/deep.txt" });
            paths.Should().NotContain("/src");

            var intoChild = await _client.PutAsJsonAsync("/move", new { sourceIds = new[] { srcId }, destinationId = innerId });
            intoChild.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Back to the root (no destinationId)
            var toRoot = await _client.PutAsJsonAsync("/move", new { sourceIds = new[] { srcId } });
            toRoot.StatusCode.Should().Be(HttpStatusCode.OK);
            Paths(await ListFilesAsync(_client)).Should().Contain("/src/inner/deep.txt");

            // A root item with the same name blocks the move
            var otherSrc = await CreateFolderAsync(_client, "src", destId);
            var conflict = await _client.PutAsJsonAsync("/move", new { sourceIds = new[] { otherSrc } });
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact, TestPriority(15)]
        public async Task CopyFolder_DuplicatesTreeAndStoredFiles()
        {
            await AuthenticateAsync();

            var folderId = await CreateFolderAsync(_client, "album");
            (await UploadAsync(_client, "pic.txt", "picture", folderId)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Copy into the same place -> gets a unique name
            var copy = await _client.PostAsJsonAsync("/copy", new { sourceIds = new[] { folderId } });
            copy.StatusCode.Should().Be(HttpStatusCode.OK);

            var files = await ListFilesAsync(_client);
            var originalId = IdAt(files, "/album/pic.txt");
            var copyId = IdAt(files, "/album (1)/pic.txt");
            copyId.Should().NotBe(originalId);
            (await _client.GetStringAsync($"/download/{copyId}")).Should().Be("picture");

            // Deleting the copy leaves the original's stored file alone
            (await _client.DeleteAsync($"/delete/{IdAt(files, "/album (1)")}")).StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(StoredFilePath(copyId)).Should().BeFalse();
            File.Exists(StoredFilePath(originalId)).Should().BeTrue();

            var intoItself = await _client.PostAsJsonAsync("/copy", new { sourceIds = new[] { folderId }, destinationId = folderId });
            intoItself.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact, TestPriority(16)]
        public async Task DownloadFile_UsesRealName_AndSupportsRanges()
        {
            await AuthenticateAsync();

            (await UploadAsync(_client, "ranged.txt", "Hello range")).StatusCode.Should().Be(HttpStatusCode.OK);
            var fileId = IdAt(await ListFilesAsync(_client), "/ranged.txt");

            var full = await _client.GetAsync($"/download/{fileId}");
            full.StatusCode.Should().Be(HttpStatusCode.OK);
            full.Content.Headers.ContentDisposition!.FileName.Should().Be("ranged.txt");
            full.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");

            var request = new HttpRequestMessage(HttpMethod.Get, $"/download/{fileId}");
            request.Headers.Range = new RangeHeaderValue(0, 4);
            var partial = await _client.SendAsync(request);
            partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            (await partial.Content.ReadAsStringAsync()).Should().Be("Hello");

            var missing = await _client.GetAsync($"/download/{Guid.NewGuid()}");
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact, TestPriority(17)]
        public async Task DownloadZip_ContainsFilesAndFolders()
        {
            await AuthenticateAsync();

            var folderId = await CreateFolderAsync(_client, "zipme");
            var subId = await CreateFolderAsync(_client, "empty", folderId);
            (await UploadAsync(_client, "one.txt", "one", folderId)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await UploadAsync(_client, "loose.txt", "loose")).StatusCode.Should().Be(HttpStatusCode.OK);
            var looseId = IdAt(await ListFilesAsync(_client), "/loose.txt");

            var response = await _client.PostAsJsonAsync("/download/zip", new { ids = new[] { folderId, looseId } });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

            using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
                new[] { "zipme/", "zipme/empty/", "zipme/one.txt", "loose.txt" });

            using var reader = new StreamReader(zip.GetEntry("zipme/one.txt")!.Open());
            (await reader.ReadToEndAsync()).Should().Be("one");
        }

        [Fact, TestPriority(18)]
        public async Task StoredFile_IsEncryptedOnDisk_AndDownloadsAsPlaintext()
        {
            await AuthenticateAsync();

            const string secret = "Top secret contents that must not appear on disk";
            (await UploadAsync(_client, "secret.txt", secret)).StatusCode.Should().Be(HttpStatusCode.OK);
            var fileId = IdAt(await ListFilesAsync(_client), "/secret.txt");

            var stored = await File.ReadAllBytesAsync(StoredFilePath(fileId));
            Encoding.ASCII.GetString(stored, 0, 4).Should().Be("FVE1");
            Encoding.UTF8.GetString(stored).Should().NotContain("Top secret");

            (await _client.GetStringAsync($"/download/{fileId}")).Should().Be(secret);
        }

        [Theory, TestPriority(19)]
        [InlineData(1)]
        [InlineData(65_536)]      // exactly one chunk
        [InlineData(131_072)]     // exact multiple of the chunk size
        [InlineData(200_003)]     // several chunks with a partial last one
        public async Task EncryptedFile_RoundTrips_WithRangesAcrossChunks(int length)
        {
            await AuthenticateAsync();

            var data = new byte[length];
            new Random(length).NextBytes(data);
            var name = $"blob-{length}.bin";

            (await UploadBytesAsync(_client, name, data, "application/octet-stream")).StatusCode.Should().Be(HttpStatusCode.OK);
            var fileId = IdAt(await ListFilesAsync(_client), $"/{name}");

            (await _client.GetByteArrayAsync($"/download/{fileId}")).Should().Equal(data);

            // A range that crosses the first chunk boundary when the file is big enough
            var from = Math.Max(0, Math.Min(65_530, length - 20));
            var to = Math.Min(length - 1, from + 19);
            var request = new HttpRequestMessage(HttpMethod.Get, $"/download/{fileId}");
            request.Headers.Range = new RangeHeaderValue(from, to);
            var partial = await _client.SendAsync(request);

            var expected = data[from..(to + 1)];
            (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(expected);
        }

        [Fact, TestPriority(20)]
        public async Task TamperedStoredFile_IsNotServed()
        {
            await AuthenticateAsync();

            (await UploadAsync(_client, "tamper.txt", "original contents")).StatusCode.Should().Be(HttpStatusCode.OK);
            var fileId = IdAt(await ListFilesAsync(_client), "/tamper.txt");

            // Flip one ciphertext byte after the 8-byte header
            var path = StoredFilePath(fileId);
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[10] ^= 0xFF;
            await File.WriteAllBytesAsync(path, bytes);

            var download = async () =>
            {
                var response = await _client.GetAsync($"/download/{fileId}");
                response.EnsureSuccessStatusCode();
                await response.Content.ReadAsByteArrayAsync();
            };
            await download.Should().ThrowAsync<Exception>();
        }

        [Fact, TestPriority(30)]
        public async Task DeleteUser_ShouldSucceed()
        {
            await AuthenticateAsync();

            var response = await _client.DeleteAsync("/user");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("{\"message\":\"User's files and account deleted\"}");
        }
    }

    // xUnit Test Priority Attribute & Orderer
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class TestPriorityAttribute : Attribute
    {
        public int Priority { get; }
        public TestPriorityAttribute(int priority) => Priority = priority;
    }

    public class PriorityOrderer : ITestCaseOrderer
    {
        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(
            IEnumerable<TTestCase> testCases) where TTestCase : ITestCase
        {
            var sortedMethods = testCases.OrderBy(tc =>
            {
                var attr = tc.TestMethod.Method
                    .GetCustomAttributes(typeof(TestPriorityAttribute))
                    .FirstOrDefault();

                return attr == null
                    ? 0
                    : attr.GetNamedArgument<int>("Priority");
            });

            return sortedMethods;
        }
    }
}
