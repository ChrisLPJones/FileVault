using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;

namespace Backend.Test
{
    public class HealthCheckEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public HealthCheckEndpointsTests(WebApplicationFactory<Program> factory)
        {
            _client = factory.CreateClient();
        }






        [Fact]
        public async Task Ping_ReturnsPong()
        {
            // Act
            var response = await _client.GetAsync("/ping");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("{\"success\":\"Pong\"}");
        }






        [Fact]
        public async Task PingSQL_ReturnConnectionSuccessful()
        {
            // Act
            var response = await _client.GetAsync("/pingsql");

            // Assert 
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("{\"success\":\"Connection successful\"}");
        }

        [Fact]
        public async Task OpenApiDocument_DescribesEveryEndpoint()
        {
            var response = await _client.GetAsync("/swagger/v1/swagger.json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var paths = json.RootElement.GetProperty("paths");
            var documented = paths.EnumerateObject()
                .SelectMany(p => p.Value.EnumerateObject().Select(op => $"{op.Name.ToUpperInvariant()} {p.Name}"))
                .ToList();

            documented.Should().Contain(new[]
            {
                "POST /user/register", "POST /user/login", "POST /user/refresh", "POST /user/logout",
                "GET /user/info", "PATCH /user/profile", "POST /user/password", "DELETE /user", "GET /user/usage",
                "POST /upload", "POST /folder", "GET /files", "GET /download/{fileId}", "POST /download/zip",
                "PATCH /rename", "PUT /move", "POST /copy", "DELETE /delete/{fileId}", "DELETE /delete",
                "GET /ping", "GET /pingsql"
            });

            // Every operation has a summary and a tag, and the upload shows a file field
            foreach (var path in paths.EnumerateObject())
                foreach (var op in path.Value.EnumerateObject())
                {
                    op.Value.GetProperty("summary").GetString().Should().NotBeNullOrWhiteSpace($"{op.Name} {path.Name} needs a summary");
                    op.Value.GetProperty("tags").GetArrayLength().Should().BeGreaterThan(0);
                }

            paths.GetProperty("/upload").GetProperty("post").GetProperty("requestBody").GetProperty("content")
                .GetProperty("multipart/form-data").GetProperty("schema").GetProperty("properties")
                .GetProperty("file").GetProperty("format").GetString().Should().Be("binary");

            json.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _)
                .Should().BeTrue();

            // Only endpoints that require login are marked as needing the token
            bool NeedsToken(string path, string method) =>
                paths.GetProperty(path).GetProperty(method).TryGetProperty("security", out var security)
                && security.GetArrayLength() > 0;
            NeedsToken("/files", "get").Should().BeTrue();
            NeedsToken("/user/login", "post").Should().BeFalse();
            NeedsToken("/ping", "get").Should().BeFalse();
        }

        [Fact]
        public async Task SwaggerUi_IsServed()
        {
            var response = await _client.GetAsync("/swagger/index.html");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Contain("swagger-ui");
        }
    }

}