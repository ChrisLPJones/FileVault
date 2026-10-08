using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Email verification and password reset (Users.EmailVerified, the AccountTokens table)
public partial class DatabaseServices
{
    public async Task<bool> IsEmailVerifiedAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "SELECT EmailVerified FROM Users WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteScalarAsync() is bool verified && verified;
    }

    public async Task SetEmailVerifiedAsync(string userId, bool verified)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Users SET EmailVerified = @Verified WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Verified", verified);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    // Store a new single-use token (hash only). Earlier unused tokens for the same purpose stop
    // working, so only the most recent email's link can be used; expired ones are cleared out.
    public async Task StoreAccountTokenAsync(string userId, string purpose, string tokenHash, string? email, DateTime expiresAtUtc)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            DELETE FROM AccountTokens WHERE UserId = @UserId AND ExpiresAt < SYSUTCDATETIME();
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND Purpose = @Purpose AND UsedAt IS NULL;
            INSERT INTO AccountTokens (UserId, Purpose, TokenHash, Email, ExpiresAt)
            VALUES (@UserId, @Purpose, @TokenHash, @Email, @ExpiresAt);";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Purpose", purpose);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@Email", (object?)email ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExpiresAt", expiresAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    public record AccountTokenUse(string UserId, string? Email);

    // Atomically use a token: returns its user (and email) if it was unused and unexpired, else null
    public async Task<AccountTokenUse?> ConsumeAccountTokenAsync(string purpose, string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            OUTPUT inserted.UserId, inserted.Email
            WHERE TokenHash = @TokenHash AND Purpose = @Purpose
              AND UsedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@Purpose", purpose);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new AccountTokenUse(reader.GetGuid(0).ToString(), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
