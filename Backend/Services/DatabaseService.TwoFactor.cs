using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Two-factor authentication: authenticator secrets, recovery codes and login challenges
public partial class DatabaseServices
{
    // ProtectedSecret is set while setup is pending as well as once 2FA is on
    public record TwoFactorState(string? ProtectedSecret, bool Enabled, int RecoveryCodesLeft);

    public async Task<TwoFactorState?> GetTwoFactorStateAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT TotpSecret, TotpEnabled,
                   (SELECT COUNT(*) FROM TotpRecoveryCodes WHERE UserId = @UserId AND UsedAt IS NULL)
            FROM Users WHERE Id = @UserId";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new TwoFactorState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetInt32(2));
    }



    // Store a new secret for setup. Does nothing (returns false) if 2FA is already on.
    public async Task<bool> SetPendingTotpSecretAsync(string userId, string protectedSecret)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Users SET TotpSecret = @Secret, TotpLastStep = NULL
            WHERE Id = @UserId AND TotpEnabled = 0";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Secret", protectedSecret);
        return await command.ExecuteNonQueryAsync() == 1;
    }



    // Turn 2FA on with the pending secret, record the step of the confirming code and
    // store the recovery codes. Returns false if setup wasn't pending.
    public async Task<bool> EnableTotpAsync(string userId, long acceptedStep, IEnumerable<string> recoveryCodeHashes)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        const string enable = @"
            UPDATE Users SET TotpEnabled = 1, TotpLastStep = @Step
            WHERE Id = @UserId AND TotpEnabled = 0 AND TotpSecret IS NOT NULL";

        await using (var command = new SqlCommand(enable, connection, transaction))
        {
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Step", acceptedStep);
            if (await command.ExecuteNonQueryAsync() != 1)
            {
                await transaction.RollbackAsync();
                return false;
            }
        }

        await ReplaceRecoveryCodesAsync(userId, recoveryCodeHashes, connection, transaction);
        await transaction.CommitAsync();
        return true;
    }



    // Accept a code's time step once: only succeeds if it's later than the last accepted step
    public async Task<bool> TryUseTotpStepAsync(string userId, long step)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Users SET TotpLastStep = @Step
            WHERE Id = @UserId AND TotpEnabled = 1 AND (TotpLastStep IS NULL OR TotpLastStep < @Step)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Step", step);
        return await command.ExecuteNonQueryAsync() == 1;
    }



    // Mark a recovery code as used; false if it doesn't exist or was already used
    public async Task<bool> TryUseRecoveryCodeAsync(string userId, string codeHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE TOP (1) TotpRecoveryCodes SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND CodeHash = @CodeHash AND UsedAt IS NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@CodeHash", codeHash);
        return await command.ExecuteNonQueryAsync() == 1;
    }



    // Replace every recovery code (old ones stop working)
    public async Task ReplaceRecoveryCodesAsync(string userId, IEnumerable<string> codeHashes)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await ReplaceRecoveryCodesAsync(userId, codeHashes, connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task ReplaceRecoveryCodesAsync(
        string userId, IEnumerable<string> codeHashes, SqlConnection connection, SqlTransaction transaction)
    {
        await using (var delete = new SqlCommand("DELETE FROM TotpRecoveryCodes WHERE UserId = @UserId", connection, transaction))
        {
            delete.Parameters.AddWithValue("@UserId", userId);
            await delete.ExecuteNonQueryAsync();
        }

        foreach (var hash in codeHashes)
        {
            await using var insert = new SqlCommand(
                "INSERT INTO TotpRecoveryCodes (UserId, CodeHash) VALUES (@UserId, @CodeHash)", connection, transaction);
            insert.Parameters.AddWithValue("@UserId", userId);
            insert.Parameters.AddWithValue("@CodeHash", hash);
            await insert.ExecuteNonQueryAsync();
        }
    }



    // Turn 2FA off: forget the secret, recovery codes and any pending login challenges
    public async Task DisableTotpAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Users SET TotpSecret = NULL, TotpEnabled = 0, TotpLastStep = NULL WHERE Id = @UserId;
            DELETE FROM TotpRecoveryCodes WHERE UserId = @UserId;
            DELETE FROM LoginChallenges WHERE UserId = @UserId;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    // Store a login challenge (hash only) and drop this user's finished or expired ones.
    // The expiry uses the database clock, like the checks against it.
    public async Task StoreLoginChallengeAsync(string userId, string tokenHash, TimeSpan lifetime)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            DELETE FROM LoginChallenges WHERE UserId = @UserId AND (ExpiresAt < SYSUTCDATETIME() OR UsedAt IS NOT NULL);
            INSERT INTO LoginChallenges (UserId, TokenHash, ExpiresAt) VALUES (@UserId, @TokenHash, DATEADD(second, @Seconds, SYSUTCDATETIME()));";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@Seconds", (int)lifetime.TotalSeconds);
        await command.ExecuteNonQueryAsync();
    }



    // Count an attempt against a challenge. Returns the user ID if the challenge is unused,
    // unexpired and still had attempts left; null otherwise. Atomic, so parallel guesses
    // can't get past the limit.
    public async Task<string?> StartLoginChallengeAttemptAsync(string tokenHash, int maxAttempts)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE LoginChallenges SET Attempts = Attempts + 1
            OUTPUT inserted.UserId
            WHERE TokenHash = @TokenHash AND UsedAt IS NULL
              AND ExpiresAt > SYSUTCDATETIME() AND Attempts < @MaxAttempts";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@MaxAttempts", maxAttempts);

        var userId = await command.ExecuteScalarAsync();
        return userId is Guid id ? id.ToString() : null;
    }



    // Use up a challenge after a correct code; false if it was already used
    public async Task<bool> CompleteLoginChallengeAsync(string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE LoginChallenges SET UsedAt = SYSUTCDATETIME()
            WHERE TokenHash = @TokenHash AND UsedAt IS NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        return await command.ExecuteNonQueryAsync() == 1;
    }
}
