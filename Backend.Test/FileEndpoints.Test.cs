using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Backend.Test
{
    public class FileEndpointsTest : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public FileEndpointsTest(WebApplicationFactory<Program> factory)
        {
            _client = factory.CreateClient();
        }






        [Fact]
        public async Task Upload_List_Download_Delete_WithValidRequest_ReturnsSuccess()
        {
            /* Upload */
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
            /* List */

            // Act
            response = await _client.GetAsync("/files");
            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("test.txt");

            /* Download */
            // Act
            response = await _client.GetAsync("download/test.txt");
            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content = await response.Content.ReadAsStringAsync();
            content.Should().Be("Dummy file content");

            /* Delete */
            // Act
            response = await _client.DeleteAsync("/delete/test.txt");
            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content = await response.Content.ReadAsStringAsync();
            content.Should().Be("\"File deleted: test.txt\"");
        }
    }
}