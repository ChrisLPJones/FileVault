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
        public async Task StoreRefreshToken_IsRefused_ForASuspendedUser()
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
        public async Task ConsumeAndReplace_ConsumesButStoresNothing_ForASuspendedUserOrAnExpiredToken()
        {
            var account = await TestAccounts.CreateAsync(_factory, "raceswap");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();
                var sessionId = await db.CreateSessionAsync(account.UserId, "test", "127.0.0.1");
                var expires = DateTime.UtcNow.AddDays(1);
                var live = "swap-live-" + Guid.NewGuid();
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, live, expires)).Should().BeTrue();

                // Suspended: the old token is consumed, no replacement
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = SYSUTCDATETIME() WHERE Id = @Id", ("@Id", account.UserId));
                var suspendedHash = "swap-susp-" + Guid.NewGuid();
                var use = await db.ConsumeAndReplaceRefreshTokenAsync(live, suspendedHash, expires, "127.0.0.1", "test");
                use.Should().NotBeNull();
                use!.Valid.Should().BeTrue();
                use.Stored.Should().BeFalse();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H", ("@H", suspendedHash))).Should().Be(0);
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H AND RevokedAt IS NOT NULL", ("@H", live))).Should().Be(1);
                await TestDatabase.ExecuteAsync(_factory, "UPDATE Users SET SuspendedAt = NULL WHERE Id = @Id", ("@Id", account.UserId));

                // Expired: consumed, not valid, no replacement
                var expired = "swap-exp-" + Guid.NewGuid();
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, expired, expires)).Should().BeTrue();
                await TestDatabase.ExecuteAsync(_factory, "UPDATE RefreshTokens SET ExpiresAt = DATEADD(minute, -5, SYSUTCDATETIME()) WHERE TokenHash = @H", ("@H", expired));
                var expiredReplacement = "swap-exp2-" + Guid.NewGuid();
                use = await db.ConsumeAndReplaceRefreshTokenAsync(expired, expiredReplacement, expires, "127.0.0.1", "test");
                use!.Valid.Should().BeFalse();
                use.Stored.Should().BeFalse();
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE TokenHash = @H", ("@H", expiredReplacement))).Should().Be(0);

                // Unknown token: nothing
                (await db.ConsumeAndReplaceRefreshTokenAsync("swap-unknown-" + Guid.NewGuid(), "x", expires, null, "test")).Should().BeNull();

                // Normal case: old revoked, new stored in the same session, a second use is a rotated-away one
                var good = "swap-good-" + Guid.NewGuid();
                var next = "swap-next-" + Guid.NewGuid();
                (await db.StoreRefreshTokenAsync(account.UserId, sessionId, good, expires)).Should().BeTrue();
                use = await db.ConsumeAndReplaceRefreshTokenAsync(good, next, expires, "127.0.0.1", "test");
                use!.Valid.Should().BeTrue();
                use.Stored.Should().BeTrue();
                use.SessionId.Should().Be(sessionId);
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE SessionId = @S AND RevokedAt IS NULL", ("@S", sessionId))).Should().Be(1);
                use = await db.ConsumeAndReplaceRefreshTokenAsync(good, "swap-again-" + Guid.NewGuid(), expires, "127.0.0.1", "test");
                use!.Valid.Should().BeFalse();
                use.Replaced.Should().BeTrue();
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

        // Regression: a bare UPDATE on RefreshTokens (sign out a session) took its locks in the opposite
        // order to refresh/login (Users row first) and was chosen as a deadlock victim (HTTP 500)
        [Fact]
        public async Task RevokeSessionAndOthers_WaitForTheUsersRowLock_LikeEveryOtherTokenWriter()
        {
            var account = await TestAccounts.CreateAsync(_factory, "racerevlock");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();
                var sessionId = await db.CreateSessionAsync(account.UserId, "test", "127.0.0.1");
                await db.StoreRefreshTokenAsync(account.UserId, sessionId, "revlock-" + Guid.NewGuid(), DateTime.UtcNow.AddDays(1));

                var connectionString = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
                TestDatabaseGuard.AssertTestDatabase(connectionString!);
                await using var holder = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                await holder.OpenAsync();
                await using var tran = (Microsoft.Data.SqlClient.SqlTransaction)await holder.BeginTransactionAsync();
                await using (var lockUser = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT 1 FROM Users WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id", holder, tran))
                {
                    lockUser.Parameters.AddWithValue("@Id", account.UserId);
                    await lockUser.ExecuteScalarAsync();
                }

                var revoke = db.RevokeSessionAsync(account.UserId, sessionId);
                var revokeOthers = db.RevokeOtherSessionsAsync(account.UserId, null);
                await Task.Delay(500);
                revoke.IsCompleted.Should().BeFalse("it must queue behind the user's row lock before touching RefreshTokens");
                revokeOthers.IsCompleted.Should().BeFalse();

                await tran.CommitAsync();
                var done = await Task.WhenAll(revoke.ContinueWith(t => t.Result ? 1 : 0), revokeOthers);
                (done[0] + done[1]).Should().BeGreaterThan(0);
                (await TestDatabase.ScalarAsync(_factory, "SELECT COUNT(*) FROM RefreshTokens WHERE SessionId = @S AND RevokedAt IS NULL", ("@S", sessionId)))
                    .Should().Be(0);
            }
            finally
            {
                (await account.Client.DeleteAsync("/user")).Dispose();
            }
        }

        [Fact]
        public async Task RevokingSessions_WhileTheSameUserRefreshesAndLogsIn_NeverFails()
        {
            var account = await TestAccounts.CreateAsync(_factory, "racerevstress");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseServices>();
                var expires = DateTime.UtcNow.AddDays(1);

                var workers = Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
                {
                    for (var i = 0; i < 15; i++)
                    {
                        var session = await db.CreateSessionAsync(account.UserId, "test", "127.0.0.1");
                        var hash = $"stress-{w}-{i}-{Guid.NewGuid()}";
                        await db.StoreRefreshTokenAsync(account.UserId, session, hash, expires);
                        if (w % 3 == 0) await db.RevokeSessionAsync(account.UserId, session);
                        else if (w % 3 == 1) await db.ConsumeAndReplaceRefreshTokenAsync(hash, hash + "-next", expires, "127.0.0.1", "test");
                        else await db.RevokeOtherSessionsAsync(account.UserId, session);
                    }
                }));

                await Task.WhenAll(workers); // a deadlock victim would surface as a SqlException
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
