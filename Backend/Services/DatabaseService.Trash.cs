using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Recycle bin (Files.DeletedAt / Files.TrashRootId)
public partial class DatabaseServices
{
    // Move an item and everything inside it that isn't already in the bin into the bin, as one
    // entry. Returns false if the item doesn't exist or is already in the bin.
    public Task<bool> MoveToTrashAsync(string guid, string userId) =>
        RetryOnDeadlockAsync(() => MoveToTrashOnceAsync(guid, userId));

    private async Task<bool> MoveToTrashOnceAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            WITH Tree AS (
                SELECT GUID FROM Files WHERE GUID = @GUID AND UserId = @UserId AND DeletedAt IS NULL
                UNION ALL
                SELECT f.GUID FROM Files f
                INNER JOIN Tree t ON f.ParentId = t.GUID
                WHERE f.UserId = @UserId AND f.DeletedAt IS NULL
            )
            UPDATE Files SET DeletedAt = SYSUTCDATETIME(), TrashRootId = @GUID
            WHERE UserId = @UserId AND GUID IN (SELECT GUID FROM Tree)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }



    // The user's bin entries (the items they deleted), most recently deleted first.
    // Size is the total of the files that went into the bin with the entry.
    public async Task<List<(FileRecord item, DateTime deletedAt, long size)>> GetTrashAsync(string userId)
    {
        var entries = new List<(FileRecord, DateTime, long)>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT r.GUID, r.FileName, r.isDirectory, r.FilePath, r.ParentId, r.Size, r.MimeType, r.WrappedKey, r.DeletedAt,
                   (SELECT COALESCE(SUM(c.Size), 0) FROM Files c
                    WHERE c.UserId = r.UserId AND c.TrashRootId = r.GUID AND c.isDirectory = 0) AS TotalSize
            FROM Files r
            WHERE r.UserId = @UserId AND r.TrashRootId = r.GUID
            ORDER BY r.DeletedAt DESC";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add((ReadFileRecord(reader), AsUtc(reader.GetDateTime(8)), reader.GetInt64(9)));

        return entries;
    }



    // One of the user's bin entries, or null if the ID isn't one
    public async Task<FileRecord?> GetTrashEntryAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT GUID, FileName, isDirectory, FilePath, ParentId, Size, MimeType, WrappedKey
            FROM Files WHERE GUID = @GUID AND UserId = @UserId AND TrashRootId = GUID";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadFileRecord(reader) : null;
    }



    // Take a bin entry out of the bin, under a (possibly new) parent and name. Everything below it
    // gets the new path prefix, like a move; only what went into the bin with it is restored.
    public Task RestoreFromTrashAsync(FileRecord entry, string? parentId, string name, string path, string userId) =>
        RetryOnDeadlockAsync(() => RestoreFromTrashOnceAsync(entry, parentId, name, path, userId));

    private async Task RestoreFromTrashOnceAsync(FileRecord entry, string? parentId, string name, string path, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            const string query = @"
                WITH Tree AS (
                    SELECT GUID FROM Files WHERE ParentId = @GUID AND UserId = @UserId
                    UNION ALL
                    SELECT f.GUID FROM Files f
                    INNER JOIN Tree t ON f.ParentId = t.GUID
                    WHERE f.UserId = @UserId
                )
                UPDATE Files
                SET FilePath = CAST(@NewPath AS NVARCHAR(MAX))
                    + SUBSTRING(FilePath, DATALENGTH(@OldPath) / 2 + 1, DATALENGTH(FilePath))
                WHERE UserId = @UserId AND GUID IN (SELECT GUID FROM Tree);

                UPDATE Files
                SET FileName = @Name, FilePath = @NewPath, ParentId = @ParentId, UpdatedAt = SYSUTCDATETIME()
                WHERE GUID = @GUID AND UserId = @UserId;

                UPDATE Files SET DeletedAt = NULL, TrashRootId = NULL
                WHERE UserId = @UserId AND TrashRootId = @GUID;";

            await using var command = new SqlCommand(query, connection, transaction);
            command.Parameters.AddWithValue("@GUID", entry.Guid);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Name", name);
            command.Parameters.AddWithValue("@NewPath", path);
            command.Parameters.AddWithValue("@OldPath", entry.Path);
            command.Parameters.AddWithValue("@ParentId", (object?)parentId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }



    // Before a bin entry is deleted for good: separate bin entries inside it (deleted earlier, on
    // their own) are moved to the root so the delete trigger doesn't take them too. They stay in
    // the bin and are restored to the root, since their folder is gone.
    public Task DetachTrashedChildrenAsync(string guid, string userId) =>
        RetryOnDeadlockAsync(() => DetachTrashedChildrenOnceAsync(guid, userId));

    private async Task DetachTrashedChildrenOnceAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Files SET ParentId = NULL
            WHERE UserId = @UserId
              AND TrashRootId IS NOT NULL AND TrashRootId <> @GUID
              AND ParentId IN (SELECT GUID FROM Files WHERE UserId = @UserId AND TrashRootId = @GUID)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    // Bin entries (any user) that went into the bin before the cutoff
    public async Task<List<(string guid, string userId)>> GetExpiredTrashAsync(DateTime cutoffUtc)
    {
        var entries = new List<(string, string)>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // READPAST: rows locked by someone else's delete are skipped (and picked up next run)
        // instead of the scan blocking on, or deadlocking with, that delete
        const string query = "SELECT GUID, UserId FROM Files WITH (READPAST) WHERE TrashRootId = GUID AND DeletedAt < @Cutoff";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Cutoff", cutoffUtc);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add((reader.GetString(0), reader.GetGuid(1).ToString()));

        return entries;
    }
}
