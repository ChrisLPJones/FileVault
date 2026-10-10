using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Test
{
    // Interleavings between an administrator setting a password (or deleting an account) and
    // requests that were already in flight
    public class AccountSignOutRacesTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public AccountSignOutRacesTests(WebApplicationFactory<Program> factory) =>
            _factory = TestAccounts.WithoutLoginLimit(factory);

        [Fact]
        public async Task StoreRefreshToken_IsRefused_WhenTokensValidAfterChangedSinceItWasRead()
        {
            var account = await TestAccounts.CreateAsync(_factory, "race");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();
                var sessionId = await db.CreateSessionAsync(account.UserId, "test", "127.0.0.1");
                var expires = DateTime.UtcNow.AddDays(1);

                // Unchanged since it was read (null): stored
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, "race-ok-" + Guid.NewGuid(), expires, true, null))
                    .Should().BeTrue();

                // The administrator sets a password between the consume and the issue
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET TokensValidAfter = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", account.UserId));

                var hash = "race-late-" + Guid.NewGuid();
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, hash, expires, true, null))
                    .Should().BeFalse();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H", ("@H", hash)))
                    .Should().Be(0);

                // Seeing the current value is fine, and an unguarded store (login) is unaffected
                var current = (await db.GetUserAuthStateAsync(account.UserId)).TokensValidAfter;
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, "race-cur-" + Guid.NewGuid(), expires, true, current))
                    .Should().BeTrue();
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, "race-open-" + Guid.NewGuid(), expires))
                    .Should().BeTrue();
            }
            finally
            {
                (await account.Client.DeleteAsync("/user")).Dispose();
            }
        }

        [Fact]
        public async Task Gate_DoesNotCacheAStateReadBeforeAnEvict()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var gate = new AccessTokenGate(cache, new ConfigurationBuilder().Build());
            var user = Guid.NewGuid().ToString();
            var stale = new DatabaseServices.UserAuthState(true, null);
            var fresh = new DatabaseServices.UserAuthState(false, null);

            // The read is in flight when the user is evicted (deleted / password set)
            var result = await gate.GetStateAsync(user, () =>
            {
                gate.Evict(user);
                return Task.FromResult(stale);
            });
            result.Should().Be(stale);

            // The stale answer was not kept: the next call reads again
            (await gate.GetStateAsync(user, () => Task.FromResult(fresh))).Should().Be(fresh);
            // ...and a read with no Evict meanwhile is cached
            (await gate.GetStateAsync(user, () => Task.FromResult(stale))).Should().Be(fresh);
        }
    }
}
