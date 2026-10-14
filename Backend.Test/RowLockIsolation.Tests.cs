using Backend.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Backend.Test
{
    // The session, token and sign-in-challenge queries filter on one user (or one token), but with few rows
    // SQL Server used to pick a table scan, which waits for (and can deadlock with) the locked rows of every
    // OTHER user. These keep one user's rows locked in an open transaction and check that another user's
    // calls never wait for them. A fresh database: its tables are tiny, the case where the scan was chosen.
    public class RowLockIsolationTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _baseFactory;

        public RowLockIsolationTests(WebApplicationFactory<Program> factory) => _baseFactory = factory;

        private static string NewHash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        private static async Task<T> Promptly<T>(Task<T> call, string what)
        {
            if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10))) != call)
                throw new XunitException($"{what} waited for rows locked by another user");
            return await call;
        }

        private static Task Promptly(Task call, string what) => Promptly(call.ContinueWith(_ => true, TaskScheduler.Default), what);

        [Fact]
        public async Task AnotherUsersLockedRows_NeverBlockTokenSessionAndChallengeQueries()
        {
            await using var db = await FreshDatabase.CreateAsync();
            await using var factory = db.CreateFactory(_baseFactory);
            await TestAccounts.CreateAsync(factory, "admin"); // the first account is the administrator
            var other = await TestAccounts.CreateAsync(factory, "other");
            var blocker = await TestAccounts.CreateAsync(factory, "blocker");
            using var scope = factory.Services.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<DatabaseServices>();

            // The blocker has a session, refresh token, account token and login challenge
            await data.StoreAccountTokenAsync(blocker.UserId, "reset-password", NewHash(), null, DateTime.UtcNow.AddHours(1));
            await data.StoreLoginChallengeAsync(blocker.UserId, NewHash(), TimeSpan.FromMinutes(5));

            await using var holder = new SqlConnection(db.ConnectionString);
            await holder.OpenAsync();
            await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
            try
            {
                await using (var lockRows = new SqlCommand(@"
                    UPDATE RefreshTokens SET RevokedAt = RevokedAt WHERE UserId = @U;
                    UPDATE Sessions SET LastUsedAt = LastUsedAt WHERE UserId = @U;
                    UPDATE AccountTokens SET UsedAt = UsedAt WHERE UserId = @U;
                    UPDATE LoginChallenges SET Attempts = Attempts WHERE UserId = @U;", holder, transaction))
                {
                    lockRows.Parameters.AddWithValue("@U", Guid.Parse(blocker.UserId));
                    (await lockRows.ExecuteNonQueryAsync()).Should().BeGreaterThanOrEqualTo(4);
                }

                var user = other.UserId;
                var expires = DateTime.UtcNow.AddDays(1);

                // Sign-in and refresh
                var session = await Promptly(data.CreateSessionAsync(user, "test", "127.0.0.1"), "creating a session");
                var first = NewHash();
                (await Promptly(data.StoreRefreshTokenAsync(user, session, first, expires), "storing a refresh token")).Should().BeTrue();
                var second = NewHash();
                var rotated = await Promptly(data.ConsumeAndReplaceRefreshTokenAsync(first, second, expires, "127.0.0.1", "test"), "rotating a refresh token");
                rotated!.Valid.Should().BeTrue();
                var lookedUp = await Promptly(data.ConsumeAndReplaceRefreshTokenAsync(first, NewHash(), expires, "127.0.0.1", "test"), "looking up a used refresh token");
                lookedUp!.Valid.Should().BeFalse();
                (await Promptly(data.GetSessionIdForTokenAsync(second), "finding a token's session")).Should().Be(session);
                (await Promptly(data.GetActiveSessionsAsync(user), "listing sessions")).Should().NotBeEmpty();
                var seen = (DateTime?)null;
                (await Promptly(data.ReplaceSessionTokenWithinGraceAsync(user, session, NewHash(), expires, seen), "replacing a token within grace")).Should().BeTrue();

                // Signing out
                await Promptly(data.RevokeRefreshTokenAsync(second), "revoking a refresh token");
                await Promptly(data.RevokeOtherSessionsAsync(user, null), "signing out other sessions");
                await Promptly(data.RevokeAllRefreshTokensAsync(user), "revoking every refresh token");
                await Promptly(data.RevokeSessionAsync(user, session), "signing out a session");
                (await Promptly(data.StoreRefreshTokenAsync(user, session, NewHash(), expires), "storing another refresh token")).Should().BeTrue();

                // Emailed links
                var link = NewHash();
                await Promptly(data.StoreAccountTokenAsync(user, "reset-password", link, null, expires), "storing an account token");
                (await Promptly(data.ConsumeAccountTokenAsync("reset-password", link), "using an account token")).Should().NotBeNull();
                await Promptly(data.CancelPendingEmailChangeAsync(user), "cancelling an email change");
                await Promptly(data.CancelEmailChangeAndSignOutAsync(user), "cancelling an email change from the old address");
                (await Promptly(data.ConfirmEmailChangeAsync(user, NewHash()), "confirming an email change")).Outcome
                    .Should().Be(DatabaseServices.EmailChangeOutcome.Invalid);

                // Two-factor sign-in challenges
                var challenge = NewHash();
                await Promptly(data.StoreLoginChallengeAsync(user, challenge, TimeSpan.FromMinutes(5)), "storing a login challenge");
                (await Promptly(data.StartLoginChallengeAttemptAsync(challenge, 5), "counting a challenge attempt")).Should().Be(user);
                (await Promptly(data.CompleteLoginChallengeAsync(challenge), "completing a challenge")).Should().BeTrue();
                await Promptly(data.DisableTotpAsync(user), "turning two-factor off");

                // Password change and suspension: sign out everything of this user only
                (await Promptly(data.SetPasswordAndSignOutAsync(user, "not-a-real-hash"), "changing a password")).Success.Should().BeTrue();
                (await Promptly(data.SetSuspendedAsync(user, true), "suspending")).Success.Should().BeTrue();
                (await Promptly(data.SetSuspendedAsync(user, false), "unsuspending")).Success.Should().BeTrue();
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }
    }
}
