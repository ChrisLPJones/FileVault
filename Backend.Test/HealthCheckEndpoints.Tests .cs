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
    }

}