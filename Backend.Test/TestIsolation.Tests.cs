using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Test
{
    // The safety controls that keep `dotnet test` away from the dev database
    public class TestIsolationTests
    {
        private static string ConnectionStringFor(string database) =>
            $"Server=localhost;Database={database};User Id=sa;Password=x;TrustServerCertificate=True";

        [Fact]
        public void Guard_RejectsTheDevDatabase()
        {
            var act = () => TestDatabaseGuard.AssertTestDatabase(ConnectionStringFor("SecureVaultDb"));
            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Guard_RejectsAMissingDatabaseName()
        {
            var act = () => TestDatabaseGuard.AssertTestDatabase("Server=localhost;User Id=sa;Password=x");
            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Guard_AcceptsAnFvTestDatabase() =>
            TestDatabaseGuard.AssertTestDatabase(ConnectionStringFor("FvTest_x"));

        [Fact]
        public async Task BackgroundCleanupFalse_ExecuteAsyncReturnsWithoutRunningCleanup()
        {
            var scopes = new RecordingScopeFactory();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:BackgroundCleanup"] = "false" })
                .Build();
            using var service = new StorageCleanupService(scopes, config, NullLogger<StorageCleanupService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            scopes.CreateScopeCalls.Should().Be(0);
        }

        [Fact]
        public void TheResolvedTestConnectionString_IsAnFvTestDatabase()
        {
            new SqlConnectionStringBuilder(TestEnvironment.ConnectionString).InitialCatalog
                .Should().StartWith(TestDatabaseGuard.Prefix);
            new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")).InitialCatalog
                .Should().StartWith(TestDatabaseGuard.Prefix);
        }

        private sealed class RecordingScopeFactory : IServiceScopeFactory
        {
            public int CreateScopeCalls { get; private set; }

            public IServiceScope CreateScope()
            {
                CreateScopeCalls++;
                throw new InvalidOperationException("Cleanup must not run");
            }
        }
    }

    // A test host resolves the same per-run test database
    public class TestHostIsolationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
    {
        [Fact]
        public void AHost_UsesAnFvTestDatabase_AndDoesNotRunBackgroundCleanup()
        {
            var config = factory.Services.GetRequiredService<IConfiguration>();
            new SqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection")).InitialCatalog
                .Should().StartWith(TestDatabaseGuard.Prefix);
            config.GetValue("Storage:BackgroundCleanup", true).Should().BeFalse();
        }
    }
}
