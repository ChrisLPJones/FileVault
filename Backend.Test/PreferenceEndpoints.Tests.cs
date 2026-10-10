using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    public class PreferenceEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public PreferenceEndpointsTests(WebApplicationFactory<Program> factory) => _factory = TestAccounts.WithoutLoginLimit(factory);

        private static async Task<string> IconThemeAsync(HttpClient client)
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("/user/info"));
            return json.RootElement.GetProperty("iconTheme").GetString()!;
        }

        [Fact]
        public async Task IconTheme_DefaultsToDefault_PersistsAndIsPerUser()
        {
            var alice = await TestAccounts.CreateAsync(_factory, "icons_a");
            var bob = await TestAccounts.CreateAsync(_factory, "icons_b");
            try
            {
                (await IconThemeAsync(alice.Client)).Should().Be("default");

                (await alice.Client.PutAsJsonAsync("/user/icon-theme", new { iconTheme = "macos" })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await IconThemeAsync(alice.Client)).Should().Be("macos");
                (await IconThemeAsync(bob.Client)).Should().Be("default");

                // A new session still sees it (it's stored on the account, not the device)
                var again = await TestAccounts.LoginAsync(_factory.CreateClient(), alice.Email);
                (await IconThemeAsync(again)).Should().Be("macos");

                // Back to default clears it
                (await alice.Client.PutAsJsonAsync("/user/icon-theme", new { iconTheme = "default" })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await IconThemeAsync(alice.Client)).Should().Be("default");
            }
            finally
            {
                await alice.Client.DeleteAsync("/user");
                await bob.Client.DeleteAsync("/user");
            }
        }

        [Theory]
        [InlineData("beos")]
        [InlineData("")]
        [InlineData("Windows ")]
        [InlineData("windows'; DROP TABLE Users;--")]
        public async Task IconTheme_RejectsValuesOutsideTheAllowList(string value)
        {
            var account = await TestAccounts.CreateAsync(_factory, "icons_bad");
            try
            {
                await account.Client.PutAsJsonAsync("/user/icon-theme", new { iconTheme = "ubuntu" });

                (await account.Client.PutAsJsonAsync("/user/icon-theme", new { iconTheme = value })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await account.Client.PutAsJsonAsync("/user/icon-theme", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await IconThemeAsync(account.Client)).Should().Be("ubuntu"); // unchanged
            }
            finally
            {
                await account.Client.DeleteAsync("/user");
            }
        }

        [Fact]
        public async Task IconTheme_RequiresSignIn()
        {
            var response = await _factory.CreateClient().PutAsJsonAsync("/user/icon-theme", new { iconTheme = "windows" });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
