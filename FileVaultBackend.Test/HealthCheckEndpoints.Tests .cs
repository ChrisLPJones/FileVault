using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net;

namespace FileVaultBackend.Test
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
            content.Should().Be("\"Pong\"");
        }






        [Fact]
        public async Task PingSQL_ReturnConnectionSuccessful()
        {
            // Act
            var response = await _client.GetAsync("/pingsql");

            // Assert 
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("\"Connection successful\"");
        }
    }

}