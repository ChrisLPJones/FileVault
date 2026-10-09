using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Image thumbnails (see ThumbnailService and the FileThumbnails table in init.sql)
public partial class DatabaseServices
{
    // WrappedKey is null when the image couldn't be thumbnailed
    public record ThumbnailRecord(string? WrappedKey, long? Size, string? MimeType, DateTime CreatedAt);

    // The thumbnail row for a file the user owns, or null if none has been made yet
    public async Task<ThumbnailRecord?> GetThumbnailAsync(string fileGuid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT t.WrappedKey, t.Size, t.MimeType, t.CreatedAt
            FROM FileThumbnails t
            INNER JOIN Files f ON f.GUID = t.FileGuid
            WHERE t.FileGuid = @GUID AND f.UserId = @UserId AND f.DeletedAt IS NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", fileGuid);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new ThumbnailRecord(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetDateTime(3));
    }



    // True if a thumbnail (or the fact that there can't be one) is already recorded for the file
    public async Task<bool> HasThumbnailRecordAsync(string fileGuid)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT COUNT(*) FROM FileThumbnails WHERE FileGuid = @GUID", connection);
        command.Parameters.AddWithValue("@GUID", fileGuid);
        return (int)(await command.ExecuteScalarAsync() ?? 0) > 0;
    }



    // Forget the thumbnails of deleted files. Seeks each row by key, so it never waits on
    // other users' rows. Failures are logged, not thrown: a leftover row is harmless.
    public async Task DeleteThumbnailRecordsAsync(IReadOnlyCollection<string> fileGuids)
    {
        if (fileGuids.Count == 0)
            return;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // A literal list of keys (in batches) so the delete seeks the primary key
            foreach (var batch in fileGuids.Chunk(500))
            {
                await using var command = new SqlCommand { Connection = connection };
                var names = batch.Select((guid, i) =>
                {
                    command.Parameters.AddWithValue($"@g{i}", guid);
                    return $"@g{i}";
                }).ToList();
                command.CommandText = $"DELETE FROM FileThumbnails WITH (ROWLOCK) WHERE FileGuid IN ({string.Join(", ", names)})";
                await RetryOnDeadlockAsync(() => command.ExecuteNonQueryAsync());
            }
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Could not remove thumbnail records of {Count} deleted files", fileGuids.Count);
        }
    }



    // Record a new thumbnail (or, with a null key, that none can be made). Does nothing if
    // the file has been deleted meanwhile; returns false in that case.
    public async Task<bool> SaveThumbnailAsync(string fileGuid, string? wrappedKey, long? size, string? mimeType)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Update, else insert. Not a serializable MERGE: its range locks on FileThumbnails
        // deadlocked with deletes cascading into the table. Two inserts of the same thumbnail
        // can't race, because ThumbnailService makes one per file at a time.
        const string query = @"
            UPDATE FileThumbnails
            SET WrappedKey = @WrappedKey, Size = @Size, MimeType = @MimeType, CreatedAt = SYSUTCDATETIME()
            WHERE FileGuid = @GUID;

            IF @@ROWCOUNT = 0
                INSERT INTO FileThumbnails (FileGuid, WrappedKey, Size, MimeType)
                SELECT GUID, @WrappedKey, @Size, @MimeType FROM Files WHERE GUID = @GUID;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", fileGuid);
        command.Parameters.AddWithValue("@WrappedKey", (object?)wrappedKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@Size", (object?)size ?? DBNull.Value);
        command.Parameters.AddWithValue("@MimeType", (object?)mimeType ?? DBNull.Value);
        return await RetryOnDeadlockAsync(() => command.ExecuteNonQueryAsync()) > 0;
    }
}
