using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Share links (the Shares table)
public partial class DatabaseServices
{
    // DATETIME2 values come back with an unspecified kind; they are stored as UTC
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : AsUtc(reader.GetDateTime(ordinal));



    // Store a new link: the token's hash (for lookups), the password's BCrypt hash (for checks) and
    // both encrypted for showing the owner later. Returns its creation time.
    public async Task<DateTime> CreateShareAsync(
        Guid shareId, string userId, string itemId, string tokenHash, DateTime? expiresAtUtc, string? passwordHash,
        string tokenCipher, string? passwordCipher)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            INSERT INTO Shares (Id, TokenHash, ItemId, UserId, ExpiresAt, PasswordHash, TokenCipher, PasswordCipher)
            OUTPUT inserted.CreatedAt
            VALUES (@Id, @TokenHash, @ItemId, @UserId, @ExpiresAt, @PasswordHash, @TokenCipher, @PasswordCipher);";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", shareId);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@ItemId", itemId);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@ExpiresAt", (object?)expiresAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@PasswordHash", (object?)passwordHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@TokenCipher", tokenCipher);
        command.Parameters.AddWithValue("@PasswordCipher", (object?)passwordCipher ?? DBNull.Value);

        return AsUtc((DateTime)(await command.ExecuteScalarAsync())!);
    }



    private const string ShareListQuery = @"
            SELECT s.Id, s.ItemId, f.FileName, f.isDirectory, s.CreatedAt, s.ExpiresAt,
                   CAST(CASE WHEN s.PasswordHash IS NULL THEN 0 ELSE 1 END AS BIT), s.DownloadCount,
                   CAST(CASE WHEN f.DeletedAt IS NULL THEN 0 ELSE 1 END AS BIT),
                   s.TokenCipher, s.PasswordCipher
            FROM Shares s
            INNER JOIN Files f ON f.GUID = s.ItemId AND f.UserId = s.UserId
            WHERE s.UserId = @UserId AND s.RevokedAt IS NULL";

    private static ShareListRow ReadShareListRow(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetBoolean(3),
        AsUtc(reader.GetDateTime(4)),
        AsUtc(reader, 5),
        reader.GetBoolean(6),
        reader.GetInt32(7),
        reader.GetBoolean(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    // The user's links that haven't been revoked (expired ones included), newest first
    public async Task<List<ShareListRow>> GetSharesAsync(string userId)
    {
        var shares = new List<ShareListRow>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(ShareListQuery + " ORDER BY s.CreatedAt DESC", connection);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            shares.Add(ReadShareListRow(reader));

        return shares;
    }

    // One of the user's links (not revoked), or null
    public async Task<ShareListRow?> GetShareAsync(Guid shareId, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(ShareListQuery + " AND s.Id = @Id", connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Id", shareId);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadShareListRow(reader) : null;
    }



    // Revoke one of the user's links; false if it doesn't exist, isn't theirs or was already revoked
    public async Task<bool> RevokeShareAsync(Guid shareId, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Shares SET RevokedAt = SYSUTCDATETIME()
            WHERE Id = @Id AND UserId = @UserId AND RevokedAt IS NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", shareId);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }



    // An active link (not revoked, not expired by the database clock) by its token hash, or null
    public async Task<ShareRecord?> GetActiveShareAsync(string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT Id, ItemId, UserId, ExpiresAt, PasswordHash
            FROM Shares
            WHERE TokenHash = @TokenHash AND RevokedAt IS NULL
              AND (ExpiresAt IS NULL OR ExpiresAt > SYSUTCDATETIME())";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new ShareRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2).ToString(),
            AsUtc(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }



    // Count a download through a link
    public async Task IncrementShareDownloadsAsync(Guid shareId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Shares SET DownloadCount = DownloadCount + 1 WHERE Id = @Id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", shareId);
        await command.ExecuteNonQueryAsync();
    }
}
