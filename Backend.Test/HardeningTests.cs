using Backend.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Test
{
    // Production settings: security headers, Swagger switched off, the Secure cookie flag
    public class HardeningTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
    {
        private const string Password = "TestPassw0rd";

        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly List<WebApplicationFactory<Program>> _factories = new();
        private readonly List<(HttpClient client, string token)> _usersToDelete = new();

        public HardeningTests(WebApplicationFactory<Program> factory)
        {
            _baseFactory = factory;
        }

        // The app in another environment. The secrets normally come from appsettings.Development.json
        // (or environment variables in CI), so they're copied from the Development app's configuration.
        private HttpClient NewClient(string environment, params (string key, string value)[] settings)
        {
            var devConfig = _baseFactory.Services.GetRequiredService<IConfiguration>();
            var factory = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                foreach (var key in new[] { "ConnectionStrings:DefaultConnection", "Jwt:Key", "Encryption:MasterKey" })
                    builder.UseSetting(key, devConfig[key]);
                builder.UseSetting("RateLimiting:auth:PermitLimit", "1000");
                foreach (var (key, value) in settings)
                    builder.UseSetting(key, value);
            });
            _factories.Add(factory);
            return factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        }

        private async Task<HttpResponseMessage> RegisterAndLoginAsync(HttpClient client, Action<HttpRequestMessage>? configure = null)
        {
            var email = $"hard_{Guid.NewGuid():N}@example.test";
            (await client.PostAsJsonAsync("/user/register",
                new UserModel { FirstName = "Hard", LastName = "Ening", Email = email, Password = Password }))
                .EnsureSuccessStatusCode();
            await TestDatabase.MarkEmailVerifiedAsync(_baseFactory, email);

            var request = new HttpRequestMessage(HttpMethod.Post, "/user/login")
            {
                Content = JsonContent.Create(new LoginModel { Email = email, Password = Password })
            };
            configure?.Invoke(request);
            var login = await client.SendAsync(request);
            login.StatusCode.Should().Be(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            _usersToDelete.Add((client, json.RootElement.GetProperty("success").GetString()!));
            return login;
        }

        private static string RefreshSetCookie(HttpResponseMessage response) =>
            response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fv_refresh=")).ToLowerInvariant();

        private static void ShouldHaveSecurityHeaders(HttpResponseMessage response)
        {
            response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
            response.Headers.GetValues("Referrer-Policy").Should().ContainSingle("no-referrer");
            response.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
            response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
        }



        [Fact]
        public async Task EveryResponse_HasSecurityHeaders()
        {
            var client = NewClient("Production");

            var ping = await client.GetAsync("/ping");
            ShouldHaveSecurityHeaders(ping);
            ping.Headers.GetValues("Content-Security-Policy").Single().Should().Be("default-src 'none'; frame-ancestors 'none'");

            // Errors too
            ShouldHaveSecurityHeaders(await client.GetAsync("/user/info"));
            ShouldHaveSecurityHeaders(await client.GetAsync("/no-such-endpoint"));
            ShouldHaveSecurityHeaders(await client.PostAsJsonAsync("/user/login", new { email = "x@example.test", password = "Wr0ngPassword" }));
        }

        [Fact]
        public async Task AccountResponses_AreNotCached()
        {
            var client = NewClient("Production");
            var login = await RegisterAndLoginAsync(client);
            login.Headers.CacheControl!.NoStore.Should().BeTrue();

            var request = new HttpRequestMessage(HttpMethod.Get, "/user/info");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _usersToDelete[^1].token);
            var info = await client.SendAsync(request);
            info.StatusCode.Should().Be(HttpStatusCode.OK);
            info.Headers.CacheControl!.NoStore.Should().BeTrue();

            (await client.GetAsync("/user/sessions")).Headers.CacheControl!.NoStore.Should().BeTrue();
        }

        [Fact]
        public async Task Swagger_IsOffOutsideDevelopment_UnlessEnabled()
        {
            var production = NewClient("Production");
            (await production.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await production.GetAsync("/swagger/index.html")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            var enabled = NewClient("Production", ("Swagger:Enabled", "true"));
            (await enabled.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.OK);
            var ui = await enabled.GetAsync("/swagger/index.html");
            ui.StatusCode.Should().Be(HttpStatusCode.OK);
            // The UI page loads its own scripts, so it only gets the framing rule
            ui.Headers.GetValues("Content-Security-Policy").Single().Should().Be("frame-ancestors 'none'");

            // Development (the default for these tests) serves it without the setting
            (await _baseFactory.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task RefreshCookie_IsNotSecure_OverPlainHttp_ByDefault()
        {
            var client = NewClient("Production");
            RefreshSetCookie(await RegisterAndLoginAsync(client)).Should().NotContain("secure");
        }

        [Fact]
        public async Task RefreshCookie_IsSecure_WhenConfigured()
        {
            var client = NewClient("Production", ("Jwt:SecureRefreshCookie", "true"));
            RefreshSetCookie(await RegisterAndLoginAsync(client)).Should().Contain("secure");
        }

        [Fact]
        public async Task RefreshCookie_IsSecure_BehindAnHttpsProxy()
        {
            var client = NewClient("Production", ("ForwardedHeaders:Enabled", "true"));
            var login = await RegisterAndLoginAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"));
            RefreshSetCookie(login).Should().Contain("secure");
        }

        [Fact]
        public async Task ForwardedHeaders_AreIgnored_UnlessEnabled()
        {
            var client = NewClient("Production");
            var login = await RegisterAndLoginAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"));
            RefreshSetCookie(login).Should().NotContain("secure");
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var (client, token) in _usersToDelete)
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, "/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                await client.SendAsync(request);
            }

            foreach (var factory in _factories)
                await factory.DisposeAsync();
        }
    }
}
