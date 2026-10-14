using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text.Json;

namespace Backend.Test
{
    // GET /site: the public settings behind the privacy page. No accounts are needed (the endpoint is
    // anonymous), so these tests create no data.
    public class SiteTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public SiteTests(WebApplicationFactory<Program> factory) => _factory = factory;

        private WebApplicationFactory<Program> Hosted(string contact, string? cityDatabase = null) =>
            _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("App:Mode", "hosted");
                builder.UseSetting("Hosted:InactiveDays", "45");
                builder.UseSetting("Hosted:WarningDays", "10");
                builder.UseSetting("Hosted:ContactEmail", contact);
                builder.UseSetting("Storage:TrashRetentionDays", "14");
                builder.UseSetting("GeoIp:DatabasePath", "");
                builder.UseSetting("GeoIp:CityDatabasePath", cityDatabase ?? "");
                TestAccounts.WithNoopEmailServices(builder);
            });

        private static async Task<JsonElement> GetAsync(WebApplicationFactory<Program> factory)
        {
            var response = await factory.CreateClient().GetAsync("/site");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        [Fact]
        public async Task Hosted_ReturnsThePrivacySettings_WithoutSignIn()
        {
            var body = await GetAsync(Hosted("privacy@example.test"));

            var privacy = body.GetProperty("privacy");
            privacy.GetProperty("inactiveDays").GetInt32().Should().Be(45);
            privacy.GetProperty("warningDays").GetInt32().Should().Be(10);
            privacy.GetProperty("trashRetentionDays").GetInt32().Should().Be(14);
            privacy.GetProperty("contactEmail").GetString().Should().Be("privacy@example.test");
            privacy.GetProperty("loginLocation").GetBoolean().Should().BeFalse("no GeoIP database is installed");
        }

        [Fact]
        public async Task Hosted_WithoutContactEmail_OmitsIt()
        {
            var body = await GetAsync(Hosted(""));

            body.GetProperty("privacy").TryGetProperty("contactEmail", out _).Should().BeFalse();
        }

        [Fact]
        public async Task Hosted_WithAGeoIpDatabase_SaysLoginLocationIsOn()
        {
            var city = Path.Combine(AppContext.BaseDirectory, "TestData", "GeoIP2-City-Test.mmdb");

            var body = await GetAsync(Hosted("privacy@example.test", city));

            body.GetProperty("privacy").GetProperty("loginLocation").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task Hosted_ExposesNothingElse()
        {
            var body = await GetAsync(Hosted("privacy@example.test"));

            body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("privacy");
            body.GetProperty("privacy").EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo("inactiveDays", "warningDays", "trashRetentionDays", "loginLocation", "contactEmail");
        }

        [Fact]
        public async Task SelfHosted_ReturnsNullPrivacy()
        {
            var body = await GetAsync(_factory);

            body.GetProperty("privacy").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }
}
