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
            WHERE t.FileGuid = @GUID AND f.UserId = @UserId";

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



    // Record a new thumbnail (or, with a null key, that none can be made). Does nothing if
    // the file has been deleted meanwhile; returns false in that case.
    public async Task<bool> SaveThumbnailAsync(string fileGuid, string? wrappedKey, long? size, string? mimeType)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            MERGE FileThumbnails WITH (HOLDLOCK) AS target
            USING (SELECT GUID FROM Files WHERE GUID = @GUID) AS source
            ON target.FileGuid = source.GUID
            WHEN MATCHED THEN
                UPDATE SET WrappedKey = @WrappedKey, Size = @Size, MimeType = @MimeType, CreatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (FileGuid, WrappedKey, Size, MimeType) VALUES (source.GUID, @WrappedKey, @Size, @MimeType);";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", fileGuid);
        command.Parameters.AddWithValue("@WrappedKey", (object?)wrappedKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@Size", (object?)size ?? DBNull.Value);
        command.Parameters.AddWithValue("@MimeType", (object?)mimeType ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync() > 0;
    }
}
