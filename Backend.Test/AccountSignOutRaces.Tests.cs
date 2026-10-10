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
        public async Task StoreRefreshToken_IsRefused_ForASuspendedUser_EvenWithoutTheGuard()
        {
            var account = await TestAccounts.CreateAsync(_factory, "racesusp");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();
                var sessionId = await db.CreateSessionAsync(account.UserId, "test", "127.0.0.1");
                var expires = DateTime.UtcNow.AddDays(1);

                // A login suspended between its check and the issue
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", account.UserId));

                var hash = "race-susp-" + Guid.NewGuid();
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, hash, expires)).Should().BeFalse();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H", ("@H", hash)))
                    .Should().Be(0);

                // Unsuspended: stored again
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = NULL WHERE Id = @Id", ("@Id", account.UserId));
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, "race-unsusp-" + Guid.NewGuid(), expires)).Should().BeTrue();
            }
            finally
            {
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = NULL WHERE Id = @Id", ("@Id", account.UserId));
                (await account.Client.DeleteAsync("/user")).Dispose();
            }
        }

        [Fact]
        public async Task ReplaceSessionTokenWithinGrace_IsRefused_WhenSignedOutSuspendedOrPasswordChanged()
        {
            var account = await TestAccounts.CreateAsync(_factory, "racegrace");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();
                var sessionId = await db.CreateSessionAsync(account.UserId, "test", "127.0.0.1");
                var expires = DateTime.UtcNow.AddDays(1);
                var seen = (await db.GetUserAuthStateAsync(account.UserId)).TokensValidAfter;

                // No live token in the session (signed out): refused, nothing stored
                var none = "grace-none-" + Guid.NewGuid();
                (await db.ReplaceSessionTokenWithinGraceAsync(account.UserId, sessionId, none, expires, seen)).Should().BeFalse();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H", ("@H", none))).Should().Be(0);

                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, "grace-live-" + Guid.NewGuid(), expires)).Should().BeTrue();

                // Suspended: refused, and the live token is left as it was (the transaction rolled back)
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", account.UserId));
                (await db.ReplaceSessionTokenWithinGraceAsync(account.UserId, sessionId, "grace-susp-" + Guid.NewGuid(), expires, seen)).Should().BeFalse();
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = NULL WHERE Id = @Id", ("@Id", account.UserId));
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE SessionId = @S AND RevokedAt IS NULL", ("@S", sessionId))).Should().Be(1);

                // A password change since it was read: refused
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET TokensValidAfter = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", account.UserId));
                (await db.ReplaceSessionTokenWithinGraceAsync(account.UserId, sessionId, "grace-late-" + Guid.NewGuid(), expires, seen)).Should().BeFalse();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE SessionId = @S AND RevokedAt IS NULL", ("@S", sessionId))).Should().Be(1);

                // Seeing the current value: the live token is replaced by exactly one new one
                var current = (await db.GetUserAuthStateAsync(account.UserId)).TokensValidAfter;
                var fresh = "grace-ok-" + Guid.NewGuid();
                (await db.ReplaceSessionTokenWithinGraceAsync(account.UserId, sessionId, fresh, expires, current)).Should().BeTrue();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE SessionId = @S AND RevokedAt IS NULL", ("@S", sessionId))).Should().Be(1);
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H AND RevokedAt IS NULL", ("@H", fresh))).Should().Be(1);
            }
            finally
            {
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = NULL WHERE Id = @Id", ("@Id", account.UserId));
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
