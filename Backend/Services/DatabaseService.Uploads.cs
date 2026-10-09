using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Chunked uploads in progress (the Uploads table)
public partial class DatabaseServices
{
    public async Task CreateUploadAsync(UploadRecord upload)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            INSERT INTO Uploads (Id, UserId, FileName, Size, ParentId, MimeType, ChunkSize, WrappedKey)
            VALUES (@Id, @UserId, @FileName, @Size, @ParentId, @MimeType, @ChunkSize, @WrappedKey);";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", upload.Id);
        command.Parameters.AddWithValue("@UserId", upload.UserId);
        command.Parameters.AddWithValue("@FileName", upload.FileName);
        command.Parameters.AddWithValue("@Size", upload.Size);
        command.Parameters.AddWithValue("@ParentId", (object?)upload.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@MimeType", (object?)upload.MimeType ?? DBNull.Value);
        command.Parameters.AddWithValue("@ChunkSize", upload.ChunkSize);
        command.Parameters.AddWithValue("@WrappedKey", upload.WrappedKey);
        await command.ExecuteNonQueryAsync();
    }

    private static UploadRecord ReadUpload(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1).ToString(),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6),
        reader.GetString(7),
        AsUtc(reader.GetDateTime(8)));

    private const string UploadColumns = "Id, UserId, FileName, Size, ParentId, MimeType, ChunkSize, WrappedKey, CreatedAt";

    // One of the user's uploads, or null
    public async Task<UploadRecord?> GetUploadAsync(Guid uploadId, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = $"SELECT {UploadColumns} FROM Uploads WHERE Id = @Id AND UserId = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", uploadId);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadUpload(reader) : null;
    }

    // Total size and number of the user's uploads in progress (they reserve quota)
    public async Task<(long bytes, int count)> GetPendingUploadsAsync(string userId, Guid? excludeId = null)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT COALESCE(SUM(Size), 0), COUNT(*) FROM Uploads
            WHERE UserId = @UserId AND (@ExcludeId IS NULL OR Id <> @ExcludeId)";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@ExcludeId", (object?)excludeId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt32(1));
    }

    // Mark an upload as being completed; false if it already is (a second /complete at the same time)
    public async Task<bool> ClaimUploadAsync(Guid uploadId, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Uploads SET CompletingAt = SYSUTCDATETIME()
            WHERE Id = @Id AND UserId = @UserId
              AND (CompletingAt IS NULL OR CompletingAt < DATEADD(hour, -1, SYSUTCDATETIME()))";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", uploadId);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    // Let a failed completion be retried
    public async Task ReleaseUploadAsync(Guid uploadId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Uploads SET CompletingAt = NULL WHERE Id = @Id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", uploadId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteUploadAsync(Guid uploadId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "DELETE FROM Uploads WHERE Id = @Id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Id", uploadId);
        await command.ExecuteNonQueryAsync();
    }

    // Uploads (any user) started before the cutoff
    public async Task<List<UploadRecord>> GetStaleUploadsAsync(DateTime cutoffUtc)
    {
        var uploads = new List<UploadRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = $"SELECT {UploadColumns} FROM Uploads WHERE CreatedAt < @Cutoff";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Cutoff", cutoffUtc);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            uploads.Add(ReadUpload(reader));

        return uploads;
    }
}
