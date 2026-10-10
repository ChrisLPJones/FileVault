using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Changing the email address of a signed-in account. The new address is not written to
// Users.Email until its owner confirms it: until then it is only the Email of the user's newest
// unused, unexpired 'change-email' row in AccountTokens (the token hash and expiry are on the
// same row). Nothing unique holds a pending address, so it can never block someone registering it.
public partial class DatabaseServices
{
    public record PendingEmail(string Email, DateTime ExpiresAt);

    // The address waiting to be confirmed, or null
    public async Task<PendingEmail?> GetPendingEmailAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT TOP 1 Email, ExpiresAt FROM AccountTokens
            WHERE UserId = @UserId AND Purpose = @Purpose AND UsedAt IS NULL
              AND ExpiresAt > SYSUTCDATETIME() AND Email IS NOT NULL
            ORDER BY CreatedAt DESC, Id DESC";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Purpose", AccountEmailService.ChangePurpose);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new PendingEmail(reader.GetString(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
            : null;
    }

    public enum EmailChangeStoreOutcome { Stored, DailyLimit, Cooldown, NotFound }

    // Check the send limits and store a new confirmation link as one step, so parallel requests
    // can't all pass the check: at most maxPerDay links since sinceUtc (the caller passes 24 hours
    // ago; rows are kept until they expire, which is longer than that), and one per cooldown. The user's row is locked for the transaction (the same Users-then-AccountTokens
    // order as ConfirmEmailChangeAsync), which serialises requests for one account only.
    // Storing works like StoreAccountTokenAsync: earlier unused links stop working.
    public Task<EmailChangeStoreOutcome> TryStoreEmailChangeTokenAsync(string userId, string tokenHash, string email,
        DateTime expiresAtUtc, DateTime sinceUtc, int maxPerDay, TimeSpan cooldown) =>
        RetryOnDeadlockAsync(() => TryStoreEmailChangeTokenOnceAsync(userId, tokenHash, email, expiresAtUtc, sinceUtc, maxPerDay, cooldown));

    private async Task<EmailChangeStoreOutcome> TryStoreEmailChangeTokenOnceAsync(string userId, string tokenHash, string email,
        DateTime expiresAtUtc, DateTime sinceUtc, int maxPerDay, TimeSpan cooldown)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var lockUser = new SqlCommand("SELECT 1 FROM Users WITH (UPDLOCK, HOLDLOCK) WHERE Id = @UserId", connection, transaction))
        {
            lockUser.Parameters.AddWithValue("@UserId", userId);
            // No row: the account was deleted meanwhile
            if (await lockUser.ExecuteScalarAsync() == null)
            {
                await transaction.RollbackAsync();
                return EmailChangeStoreOutcome.NotFound;
            }
        }

        int count;
        DateTime? last;
        await using (var stats = new SqlCommand(@"
            SELECT COUNT(*), MAX(CreatedAt) FROM AccountTokens
            WHERE UserId = @UserId AND Purpose = @Purpose AND CreatedAt > @Since", connection, transaction))
        {
            stats.Parameters.AddWithValue("@UserId", userId);
            stats.Parameters.AddWithValue("@Purpose", AccountEmailService.ChangePurpose);
            stats.Parameters.Add("@Since", System.Data.SqlDbType.DateTime2).Value = sinceUtc;
            await using var reader = await stats.ExecuteReaderAsync();
            await reader.ReadAsync();
            count = reader.GetInt32(0);
            last = reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
        }

        if (count >= maxPerDay)
        {
            await transaction.RollbackAsync();
            return EmailChangeStoreOutcome.DailyLimit;
        }
        if (cooldown > TimeSpan.Zero && last is { } sent && DateTime.UtcNow - sent < cooldown)
        {
            await transaction.RollbackAsync();
            return EmailChangeStoreOutcome.Cooldown;
        }

        await using (var store = new SqlCommand(@"
            DELETE FROM AccountTokens WHERE UserId = @UserId AND ExpiresAt < SYSUTCDATETIME();
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND Purpose = @Purpose AND UsedAt IS NULL;
            INSERT INTO AccountTokens (UserId, Purpose, TokenHash, Email, ExpiresAt)
            VALUES (@UserId, @Purpose, @TokenHash, @Email, @ExpiresAt);", connection, transaction))
        {
            store.Parameters.AddWithValue("@UserId", userId);
            store.Parameters.AddWithValue("@Purpose", AccountEmailService.ChangePurpose);
            store.Parameters.AddWithValue("@TokenHash", tokenHash);
            store.Parameters.AddWithValue("@Email", email);
            store.Parameters.AddWithValue("@ExpiresAt", expiresAtUtc);
            await store.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return EmailChangeStoreOutcome.Stored;
    }

    // Drop the pending change: both of its links stop working
    public async Task CancelPendingEmailChangeAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND UsedAt IS NULL AND Purpose IN (@Change, @Cancel)", connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Change", AccountEmailService.ChangePurpose);
        command.Parameters.AddWithValue("@Cancel", AccountEmailService.CancelChangePurpose);
        await command.ExecuteNonQueryAsync();
    }

    // "This wasn't me" link from the old address: drop the pending change and end every way the
    // account is signed in (refresh tokens, pending two-factor challenges, and access tokens
    // already issued, see AccessTokenGate), in one transaction.
    public async Task CancelEmailChangeAndSignOutAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = new SqlCommand(@"
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND UsedAt IS NULL AND Purpose IN (@Change, @Cancel);
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @UserId AND RevokedAt IS NULL;
            DELETE FROM LoginChallenges WHERE UserId = @UserId;
            UPDATE Users SET TokensValidAfter = @Now WHERE Id = @UserId;", connection, transaction);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Change", AccountEmailService.ChangePurpose);
        command.Parameters.AddWithValue("@Cancel", AccountEmailService.CancelChangePurpose);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    public enum EmailChangeOutcome { Invalid, Taken, Changed }

    public record EmailChangeResult(EmailChangeOutcome Outcome, string? OldEmail = null, string? NewEmail = null, bool OldEmailWasVerified = false);

    // Confirm a pending change with the token from the email sent to the new address. The token
    // must belong to this user, so another account's session can't use (or burn) the link.
    // The link is used up even when the address was taken meanwhile (409): the pending change is
    // over either way. On success the new address becomes the login, is confirmed, EmailChanged
    // is set (so it can never become the INITIAL_ADMIN_EMAIL administrator), and the user's other
    // verification, password-reset and "wasn't me" links stop working (they were sent to the old
    // mailbox). Sessions are kept.
    public Task<EmailChangeResult> ConfirmEmailChangeAsync(string userId, string tokenHash) =>
        RetryOnDeadlockAsync(() => ConfirmEmailChangeOnceAsync(userId, tokenHash));

    private async Task<EmailChangeResult> ConfirmEmailChangeOnceAsync(string userId, string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        string? newEmail;
        await using (var use = new SqlCommand(@"
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            OUTPUT inserted.Email
            WHERE TokenHash = @TokenHash AND Purpose = @Purpose AND UserId = @UserId
              AND UsedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();", connection, transaction))
        {
            use.Parameters.AddWithValue("@TokenHash", tokenHash);
            use.Parameters.AddWithValue("@Purpose", AccountEmailService.ChangePurpose);
            use.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await use.ExecuteReaderAsync();
            newEmail = await reader.ReadAsync() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
        }
        if (newEmail == null)
        {
            await transaction.RollbackAsync();
            return new EmailChangeResult(EmailChangeOutcome.Invalid);
        }

        // Two accounts confirming the same address would otherwise take overlapping range locks on
        // the email index and deadlock; one at a time per address, the second then finds it taken.
        // Released when the transaction ends. A deadlock victim here is retried like a SQL one.
        await using (var addressLock = new SqlCommand(@"
            DECLARE @Result INT;
            EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive',
                 @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @Result;", connection, transaction))
        {
            addressLock.Parameters.AddWithValue("@Resource", "fv-email-change:" + newEmail.ToLowerInvariant());
            var result = Convert.ToInt32(await addressLock.ExecuteScalarAsync());
            if (result < 0 && result != -3)
                await ReportAppLockFailureAsync("fv-email-change:<address>", result, connection, transaction);
            if (result < 0)
                throw result == -3
                    ? new AppLockDeadlockException("Chosen as a deadlock victim waiting for the email address lock")
                    : new InvalidOperationException($"Could not take the email address lock (sp_getapplock returned {result})");
        }

        string? oldEmail = null;
        var oldVerified = false;
        await using (var read = new SqlCommand("SELECT Email, EmailVerified FROM Users WITH (UPDLOCK) WHERE Id = @UserId", connection, transaction))
        {
            read.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await read.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                oldEmail = reader.GetString(0);
                oldVerified = reader.GetBoolean(1);
            }
        }
        if (oldEmail == null)
        {
            await transaction.RollbackAsync();
            return new EmailChangeResult(EmailChangeOutcome.Invalid);
        }

        // The locked re-check and the unique constraint both guard against the address having been
        // registered (or confirmed by another account) since the request
        int changed;
        try
        {
            await using var update = new SqlCommand(@"
                UPDATE Users SET Email = @NewEmail, EmailVerified = 1, EmailChanged = 1
                WHERE Id = @UserId
                  AND NOT EXISTS (SELECT 1 FROM Users WITH (UPDLOCK, HOLDLOCK) WHERE Email = @NewEmail AND Id <> @UserId)", connection, transaction);
            update.Parameters.AddWithValue("@NewEmail", newEmail);
            update.Parameters.AddWithValue("@UserId", userId);
            changed = await update.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            changed = 0;
        }

        if (changed == 0)
        {
            if (transaction.Connection != null)
                await transaction.CommitAsync(); // keep the link used up
            else
            {
                // The server ended the transaction with the failed statement, taking the used-up
                // link with it: mark it used again on its own
                await using var burn = new SqlCommand(@"
                    UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
                    WHERE TokenHash = @TokenHash AND Purpose = @Purpose AND UserId = @UserId AND UsedAt IS NULL", connection);
                burn.Parameters.AddWithValue("@TokenHash", tokenHash);
                burn.Parameters.AddWithValue("@Purpose", AccountEmailService.ChangePurpose);
                burn.Parameters.AddWithValue("@UserId", userId);
                await burn.ExecuteNonQueryAsync();
            }
            return new EmailChangeResult(EmailChangeOutcome.Taken);
        }

        await using (var clear = new SqlCommand(@"
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND UsedAt IS NULL AND Purpose IN (@Verify, @Reset, @Cancel)", connection, transaction))
        {
            clear.Parameters.AddWithValue("@UserId", userId);
            clear.Parameters.AddWithValue("@Verify", AccountEmailService.VerifyPurpose);
            clear.Parameters.AddWithValue("@Reset", AccountEmailService.ResetPurpose);
            clear.Parameters.AddWithValue("@Cancel", AccountEmailService.CancelChangePurpose);
            await clear.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new EmailChangeResult(EmailChangeOutcome.Changed, oldEmail, newEmail, oldVerified);
    }
}
