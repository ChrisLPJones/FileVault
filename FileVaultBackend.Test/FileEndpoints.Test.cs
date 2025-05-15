using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FileVaultBackend.Test
{
    public class FileEndpointsTest : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public FileEndpointsTest(WebApplicationFactory<Program> factory)
        {
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task UploadFile_WithValidRequest_ReturnsSuccess()
        {
            // Arrange
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Dummy file content"));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

            var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(fileContent, name: "file", fileName: "test.txt");

            // Act
            var response = await _client.PostAsync("/upload", multipartContent);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("\"File Uploaded: test.txt\"");
        }

        [Fact]
        public async Task DownloadFile_WithValidRequest_ReturnsSuccess()
        {
            // Arrange
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Dummy file content"));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

            var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(fileContent, name: "file", fileName: "test.txt");

            // Act
            await _client.PostAsync("/upload", multipartContent);
            var response = await _client.GetAsync("download/test.txt");
            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("Dummy file content");

        }
    }

}