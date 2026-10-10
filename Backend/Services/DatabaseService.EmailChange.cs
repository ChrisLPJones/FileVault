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

    // How many confirmation links were sent for this user since the given time, and when the
    // latest was sent (rows are kept until they expire, which is longer than the daily window)
    public async Task<(int Count, DateTime? LastSentUtc)> GetEmailChangeSendStatsAsync(string userId, DateTime sinceUtc)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT COUNT(*), MAX(CreatedAt) FROM AccountTokens
            WHERE UserId = @UserId AND Purpose = @Purpose AND CreatedAt > @Since";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Purpose", AccountEmailService.ChangePurpose);
        command.Parameters.Add("@Since", System.Data.SqlDbType.DateTime2).Value = sinceUtc;

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
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
            await transaction.CommitAsync(); // keep the link used up
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
