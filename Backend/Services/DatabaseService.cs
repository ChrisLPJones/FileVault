using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

public partial class DatabaseServices
{
    // Store database connection string from configuration
    private readonly string _connectionString;
    private readonly ILogger<DatabaseServices> _logger;

    public DatabaseServices(IConfiguration config, ILogger<DatabaseServices> logger)
    {
        InitialAdminEmail = NormaliseInitialAdminEmail(config["Admin:InitialEmail"]);
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set.");
        _logger = logger;
    }




    // Check if the database connection can be opened successfully
    public async Task CheckConnection()
    {
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
        }
    }




    // Add a new file record to the database with filename, guid, and user ID
    public async Task AddFile(
        string fileName,
        int isDirectory,
        string filePath,
        string guid,
        string userId,
        long size,
        string parentId,
        string mimeType,
        string wrappedKey
        )
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = @"
            INSERT INTO Files (FileName, isDirectory, FilePath, guid, UserId, Size, parentId, MimeType, WrappedKey)
            VALUES (@FileName, @isDirectory, @filePath, @guid, @UserId, @size, @parentId, @MimeType, @WrappedKey);";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@isDirectory", isDirectory);
        command.Parameters.AddWithValue("@FilePath", filePath);
        command.Parameters.AddWithValue("@guid", guid);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Size", size);
        command.Parameters.AddWithValue("@parentId",
            string.IsNullOrEmpty(parentId) ? DBNull.Value : parentId);
        command.Parameters.AddWithValue("@MimeType", (object?)mimeType ?? DBNull.Value);
        command.Parameters.AddWithValue("@WrappedKey", (object?)wrappedKey ?? DBNull.Value);

        // Let failures propagate so the caller can clean up the stored file
        await command.ExecuteNonQueryAsync();
    }



    public async Task AddFolder(FolderModel response)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = @"
        INSERT INTO Files (FileName, isDirectory, FilePath, GUID, UserId, size, parentId, mimeType)
        VALUES (@FileName, @isDirectory, @FilePath, @GUID, @UserId, @size, @parentId, @mimeType);";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", response.UserId);
        command.Parameters.AddWithValue("@GUID", response._id);
        command.Parameters.AddWithValue("@FileName", response.Name);
        command.Parameters.AddWithValue("@isDirectory", response.IsDirectory);
        command.Parameters.AddWithValue("@FilePath", response.Path);
        command.Parameters.AddWithValue("@parentId",
            string.IsNullOrEmpty(response.ParentId) ? DBNull.Value : response.ParentId);
        command.Parameters.AddWithValue("@size", response.Size);
        command.Parameters.AddWithValue("@mimeType", response.MimeType);

        await command.ExecuteNonQueryAsync();
    }
    


    // Returns true for a file, false for a folder, or null if the item doesn't exist for this user
    public async Task<bool?> IsFileAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "SELECT isDirectory FROM Files WHERE GUID = @GUID AND UserId = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        var result = await command.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
            return null;

        bool isDirectory = Convert.ToBoolean(result);
        return !isDirectory;
    }



    // Get the storage GUIDs of every file (not folder) at or below the given item
    public Task<List<string>> GetFileGuidsInTreeAsync(string guid, string userId) =>
        RetryOnDeadlockAsync(() => GetFileGuidsInTreeOnceAsync(guid, userId));

    private async Task<List<string>> GetFileGuidsInTreeOnceAsync(string guid, string userId)
    {
        var guids = new List<string>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            WITH Tree AS (
                SELECT GUID, isDirectory FROM Files WHERE GUID = @GUID AND UserId = @UserId
                UNION ALL
                SELECT f.GUID, f.isDirectory FROM Files f
                INNER JOIN Tree t ON f.ParentId = t.GUID
                WHERE f.UserId = @UserId
            )
            SELECT GUID FROM Tree WHERE isDirectory = 0;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            guids.Add(reader.GetString(0));

        return guids;
    }
    


    // Remove file metadata from the database for the given user and filename
    public async Task DeleteFileMetadata(string fileId, string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = @"DELETE FROM Files WHERE UserId = @UserId AND GUID = @GUID;";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", fileId);
        command.Parameters.AddWithValue("@UserId", userId);

        await RetryOnDeadlockAsync(() => command.ExecuteNonQueryAsync());
    }


    private static FileRecord ReadFileRecord(SqlDataReader reader) => new()
    {
        Guid = (string)reader["GUID"],
        Name = (string)reader["FileName"],
        IsDirectory = Convert.ToBoolean(reader["isDirectory"]),
        Path = reader["FilePath"] as string ?? "",
        ParentId = reader["ParentId"] is string { Length: > 0 } parentId ? parentId : null,
        Size = Convert.ToInt64(reader["Size"]),
        MimeType = reader["MimeType"] as string,
        WrappedKey = reader["WrappedKey"] as string
    };



    // Get a single file or folder owned by the user, or null if it doesn't exist or is in the recycle bin
    public async Task<FileRecord?> GetItemAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT GUID, FileName, isDirectory, FilePath, ParentId, Size, MimeType, WrappedKey
            FROM Files WHERE GUID = @GUID AND UserId = @UserId AND DeletedAt IS NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadFileRecord(reader) : null;
    }



    // Get an item and everything below it, parents before children (skipping anything in the recycle bin)
    public Task<List<FileRecord>> GetTreeAsync(string guid, string userId) =>
        RetryOnDeadlockAsync(() => GetTreeOnceAsync(guid, userId));

    private async Task<List<FileRecord>> GetTreeOnceAsync(string guid, string userId)
    {
        var items = new List<FileRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            WITH Tree AS (
                SELECT GUID, 0 AS Depth FROM Files WHERE GUID = @GUID AND UserId = @UserId AND DeletedAt IS NULL
                UNION ALL
                SELECT f.GUID, t.Depth + 1 FROM Files f
                INNER JOIN Tree t ON f.ParentId = t.GUID
                WHERE f.UserId = @UserId AND f.DeletedAt IS NULL
            )
            SELECT f.GUID, f.FileName, f.isDirectory, f.FilePath, f.ParentId, f.Size, f.MimeType, f.WrappedKey
            FROM Files f INNER JOIN Tree t ON f.GUID = t.GUID
            ORDER BY t.Depth;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(ReadFileRecord(reader));

        return items;
    }



    // Get the names already used in a folder (null parentId = root), optionally ignoring one item
    public Task<HashSet<string>> GetNamesInFolderAsync(string? parentId, string userId, string? excludeGuid = null) =>
        RetryOnDeadlockAsync(() => GetNamesInFolderOnceAsync(parentId, userId, excludeGuid));

    private async Task<HashSet<string>> GetNamesInFolderOnceAsync(string? parentId, string userId, string? excludeGuid)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Older rows may store root as '' instead of NULL
        const string query = @"
            SELECT FileName FROM Files
            WHERE UserId = @UserId AND DeletedAt IS NULL
              AND ((@ParentId IS NULL AND (ParentId IS NULL OR ParentId = '')) OR ParentId = @ParentId)
              AND (@ExcludeGuid IS NULL OR GUID <> @ExcludeGuid)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@ParentId", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExcludeGuid", (object?)excludeGuid ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        return names;
    }



    // Rename and/or move an item, rewriting the stored path of everything below it
    public Task RelocateAsync(FileRecord item, string? newParentId, string newName, string newPath, string userId) =>
        RetryOnDeadlockAsync(() => RelocateOnceAsync(item, newParentId, newName, newPath, userId));

    private async Task RelocateOnceAsync(FileRecord item, string? newParentId, string newName, string newPath, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            const string updateItem = @"
                UPDATE Files
                SET FileName = @Name, FilePath = @Path, ParentId = @ParentId, UpdatedAt = SYSUTCDATETIME()
                WHERE GUID = @GUID AND UserId = @UserId";

            await using (var command = new SqlCommand(updateItem, connection, transaction))
            {
                command.Parameters.AddWithValue("@Name", newName);
                command.Parameters.AddWithValue("@Path", newPath);
                command.Parameters.AddWithValue("@ParentId", (object?)newParentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@GUID", item.Guid);
                command.Parameters.AddWithValue("@UserId", userId);
                await command.ExecuteNonQueryAsync();
            }

            if (item.IsDirectory)
            {
                // Replace the old path prefix of every descendant with the new one
                const string updateDescendants = @"
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
                    WHERE UserId = @UserId AND GUID IN (SELECT GUID FROM Tree)";

                await using var command = new SqlCommand(updateDescendants, connection, transaction);
                command.Parameters.AddWithValue("@NewPath", newPath);
                command.Parameters.AddWithValue("@OldPath", item.Path);
                command.Parameters.AddWithValue("@GUID", item.Guid);
                command.Parameters.AddWithValue("@UserId", userId);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }



    // Insert copied rows (parents before children) in one transaction
    public Task InsertItemsAsync(List<FileRecord> items, string userId) =>
        RetryOnDeadlockAsync(() => InsertItemsOnceAsync(items, userId));

    private async Task InsertItemsOnceAsync(List<FileRecord> items, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            const string query = @"
                INSERT INTO Files (FileName, isDirectory, FilePath, GUID, UserId, Size, ParentId, MimeType, WrappedKey)
                VALUES (@FileName, @isDirectory, @FilePath, @GUID, @UserId, @Size, @ParentId, @MimeType, @WrappedKey);";

            foreach (var item in items)
            {
                await using var command = new SqlCommand(query, connection, transaction);
                command.Parameters.AddWithValue("@FileName", item.Name);
                command.Parameters.AddWithValue("@isDirectory", item.IsDirectory);
                command.Parameters.AddWithValue("@FilePath", item.Path);
                command.Parameters.AddWithValue("@GUID", item.Guid);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Size", item.Size);
                command.Parameters.AddWithValue("@ParentId", (object?)item.ParentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@MimeType", (object?)item.MimeType ?? DBNull.Value);
                command.Parameters.AddWithValue("@WrappedKey", (object?)item.WrappedKey ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }



    // Get a folder owned by the given user, or null if it doesn't exist or isn't a folder
    public async Task<FolderModel?> GetFolderById(string folderId, string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, FileName, FilePath, GUID, UserId, isDirectory FROM Files WHERE GUID = @GUID AND UserId = @UserId AND isDirectory = 1 AND DeletedAt IS NULL";
        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", folderId);
        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new FolderModel
            {
                _id = (string)reader["GUID"],
                Name = (string)reader["FileName"],
                Path = reader["FilePath"] as string ?? "",
                IsDirectory = Convert.ToBoolean(reader["isDirectory"]),
                UserId = ((Guid)reader["UserId"]).ToString()
            };
        }

        return null;
    }




    // Retrieve all files and folders that belong to a specific user (not those in the recycle bin)
    public Task<List<FileModel>> GetFilesFromDb(string userId) =>
        RetryOnDeadlockAsync(() => GetFilesFromDbOnceAsync(userId));

    private async Task<List<FileModel>> GetFilesFromDbOnceAsync(string userId)
    {
        var filesList = new List<FileModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, FileName, FilePath, UpdatedAt, ISNULL(CreatedAt, UpdatedAt) AS CreatedAt, GUID, isDirectory, Size, Favourite, LastOpenedAt FROM Files WHERE FileName IS NOT NULL AND UserId = @UserId AND DeletedAt IS NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            filesList.Add(new FileModel
            {
                _id = (string)reader["GUID"],
                Name = (string)reader["FileName"],
                Path = reader["FilePath"] as string ?? "",
                // Stored in UTC; marked as such so the JSON carries a "Z" and browsers show local time
                UpdatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["UpdatedAt"]), DateTimeKind.Utc),
                CreatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["CreatedAt"]), DateTimeKind.Utc),
                Size = Convert.ToInt64(reader["Size"]),
                IsDirectory = Convert.ToBoolean(reader["isDirectory"]),
                IsFavourite = Convert.ToBoolean(reader["Favourite"]),
                LastOpenedAt = reader["LastOpenedAt"] is DateTime opened ? DateTime.SpecifyKind(opened, DateTimeKind.Utc) : null
            });
        }

        return filesList;
    }








    // Insert a new user (the caller has already checked the email is free). The first account on a
    // new install (no rows in Users) becomes an administrator, unless INITIAL_ADMIN_EMAIL is set:
    // then nobody is admin at registration (that email is promoted when it is confirmed). The
    // check and the insert share one transaction under the admin-membership lock, so two
    // simultaneous first registrations can't both be admin.
    public Task RegisterUser(UserModel user) => RetryOnDeadlockAsync(() => RegisterUserOnceAsync(user));

    private async Task RegisterUserOnceAsync(UserModel user)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await TakeAdminMembershipLockAsync(connection, transaction);

        const string query = @"
            INSERT INTO Users (FirstName, LastName, Email, PasswordHash, IsAdmin)
            SELECT @FirstName, @LastName, @Email, @PasswordHash,
                   CASE WHEN @FirstAccountIsAdmin = 1 AND NOT EXISTS (SELECT 1 FROM Users) THEN 1 ELSE 0 END";
        await using var command = new SqlCommand(query, connection, transaction);

        command.Parameters.AddWithValue("@FirstName", user.FirstName.Trim());
        command.Parameters.AddWithValue("@LastName", user.LastName.Trim());
        command.Parameters.AddWithValue("@Email", user.Email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("@PasswordHash", user.Password);
        command.Parameters.AddWithValue("@FirstAccountIsAdmin", InitialAdminEmail == null);

        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }




    // Look up a user by email address
    public async Task<UserModel?> GetUserByEmail(string email)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, FirstName, LastName, Email, PasswordHash FROM Users WHERE Email = @Email";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Email", email);

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return new UserModel
            {
                Id = reader.GetGuid(0),
                FirstName = reader.GetString(1),
                LastName = reader.GetString(2),
                Email = reader.GetString(3),
                Password = reader.GetString(4),
            };
        }
        return null;
    }




    // Returns an error if the email is already used by another account, or null if it is free
    public async Task<string?> FindAccountConflictAsync(string email, string excludeUserId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "SELECT COUNT(1) FROM Users WHERE Email = @Email AND Id <> @UserId";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Email", email);
        command.Parameters.AddWithValue("@UserId", excludeUserId);

        var count = (int)(await command.ExecuteScalarAsync() ?? 0);
        return count > 0 ? "Email already exists" : null;
    }



    // Update a user's name and email
    public async Task UpdateProfileAsync(string userId, string firstName, string lastName, string email)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Users SET FirstName = @FirstName, LastName = @LastName, EmailChanged = CASE WHEN Email COLLATE Latin1_General_BIN2 <> @Email THEN 1 ELSE EmailChanged END, Email = @Email WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FirstName", firstName);
        command.Parameters.AddWithValue("@LastName", lastName);
        command.Parameters.AddWithValue("@Email", email);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    // Replace a user's password hash
    public async Task UpdatePasswordHashAsync(string userId, string passwordHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    // Bytes stored by the user, and their quota override (null = use the default)
    public Task<(long used, long? quota)> GetStorageUsageAsync(string userId) =>
        RetryOnDeadlockAsync(() => GetStorageUsageOnceAsync(userId));

    private async Task<(long used, long? quota)> GetStorageUsageOnceAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT
                (SELECT COALESCE(SUM(Size), 0) FROM Files WHERE UserId = @UserId AND isDirectory = 0),
                (SELECT StorageQuota FROM Users WHERE Id = @UserId)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }



    // Remove user and all file metadata from database and delete files from storage
    // With onlyIfInactive (hosted-mode removal) the account is deleted only if it is still due for
    // removal inside the transaction (409 otherwise), so someone who signed in, or was made admin or
    // permanent, after the job picked them is left alone.
    public Task<HttpReturnResult> DeleteUserAndFilesById(string userId, FileServices fs, HostedOptions? onlyIfInactive = null) =>
        RetryOnDeadlockAsync(() => DeleteUserAndFilesOnceAsync(userId, fs, onlyIfInactive));

    private async Task<HttpReturnResult> DeleteUserAndFilesOnceAsync(string userId, FileServices fs, HostedOptions? onlyIfInactive)
    {
        var files = new List<string>();

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Serialised with every other change to who the administrators are, and taken before
            // touching Files so the lock order is always the same
            await TakeAdminMembershipLockAsync(connection, (SqlTransaction)transaction);

            await using (var exists = new SqlCommand("SELECT COUNT(1) FROM Users WHERE Id = @UserId", connection, (SqlTransaction)transaction))
            {
                exists.Parameters.AddWithValue("@UserId", userId);
                if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                {
                    await transaction.RollbackAsync();
                    return HttpReturnResult.NotFound("User not found");
                }
            }

            if (onlyIfInactive != null)
            {
                await using var due = new SqlCommand(
                    $"SELECT COUNT(1) FROM Users WITH (UPDLOCK, ROWLOCK) WHERE Id = @UserId AND {DueForRemovalPredicate}",
                    connection, (SqlTransaction)transaction);
                due.Parameters.AddWithValue("@UserId", userId);
                due.Parameters.AddWithValue("@InactiveDays", onlyIfInactive.InactiveDays);
                due.Parameters.AddWithValue("@WarningDays", onlyIfInactive.WarningDays);
                if (Convert.ToInt32(await due.ExecuteScalarAsync()) == 0)
                {
                    await transaction.RollbackAsync();
                    return HttpReturnResult.Conflict("Account is no longer due for removal");
                }
            }

            // Never delete the last administrator
            await using (var lastAdmin = new SqlCommand($@"
                SELECT CASE WHEN EXISTS (SELECT 1 FROM Users WHERE Id = @UserId AND {EffectiveAdminPredicate})
                             AND NOT EXISTS (SELECT 1 FROM Users WHERE {EffectiveAdminPredicate} AND Id <> @UserId)
                            THEN 1 ELSE 0 END", connection, (SqlTransaction)transaction))
            {
                lastAdmin.Parameters.AddWithValue("@UserId", userId);
                if (Convert.ToInt32(await lastAdmin.ExecuteScalarAsync()) == 1)
                {
                    await transaction.RollbackAsync();
                    return HttpReturnResult.Conflict(LastAdminMessage);
                }
            }

            // Get all GUIDs of files for this user
            var commandGetFiles = new SqlCommand("SELECT GUID FROM Files WHERE UserId = @UserId", connection, (SqlTransaction)transaction);
            commandGetFiles.Parameters.AddWithValue("@UserId", userId);

            using var reader = await commandGetFiles.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                files.Add(reader.GetString(0));
            }
            await reader.CloseAsync();

            // Delete file metadata entries for this user
            var commandDeleteFiles = new SqlCommand("DELETE FROM Files WHERE UserId = @UserId", connection, (SqlTransaction)transaction);
            commandDeleteFiles.Parameters.AddWithValue("@UserId", userId);
            await commandDeleteFiles.ExecuteNonQueryAsync();

            // Delete the user record
            var commandDeleteUser = new SqlCommand("DELETE FROM Users WHERE Id = @UserId", connection, (SqlTransaction)transaction);
            commandDeleteUser.Parameters.AddWithValue("@UserId", userId);
            await commandDeleteUser.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch (Exception ex) when (!IsDeadlock(ex) && ex is not AdminLockTimeoutException) // deadlocks are retried by the caller; a lock timeout becomes a 503
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to delete user {UserId} and their files", userId);
            return new HttpReturnResult(false, "Error deleting user and files");
        }

        // Remove all user's files from storage after successful DB transaction
        await DeleteThumbnailRecordsAsync(files);
        await fs.DeleteAllFilesFromUser(files);

        return new HttpReturnResult(true, "User's files and account deleted");
    }




    // Retrieve user details by their user ID
    public Task<UserModel?> GetUserByUserId(string userId) =>
        RetryOnDeadlockAsync(() => GetUserByUserIdOnceAsync(userId));

    private async Task<UserModel?> GetUserByUserIdOnceAsync(string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, FirstName, LastName, Email, PasswordHash FROM Users WHERE Id = @Id";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Id", userId);

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return new UserModel
            {
                Id = reader.GetGuid(0),
                FirstName = reader.GetString(1),
                LastName = reader.GetString(2),
                Email = reader.GetString(3),
                Password = reader.GetString(4),
            };
        }

        return null;
    }




    // 
    // Record a successful login
    public async Task UpdateUserLastLogin(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Users SET LastLogin = @LastLogin WHERE Id = @UserId AND SuspendedAt IS NULL";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@LastLogin", DateTime.UtcNow);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    public async Task<bool> UserExistsByEmail(string email)
    {
        using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT COUNT(1) FROM Users WHERE Email = @Email";

        using SqlCommand command = new(query, connection);
        command.Parameters.AddWithValue("@Email", email);

        int count = (int)(await command.ExecuteScalarAsync() ?? 0);

        return count > 0;
    }



    // Store a new refresh token (hash only) for a session, and drop this user's expired
    // tokens and the sessions left without any. When rotating a token, pass the user's
    // TokensValidAfter as read before the old token was consumed (RefreshTokenUse): the new
    // token is stored only if that value is unchanged, so a refresh that was in flight while an
    // administrator set the password can't leave a live session behind. Returns false if it was
    // refused for that reason, or because the user is suspended (checked on every insert so a login
    // in flight when a suspension commits can't leave a token behind), or the user is gone.
    public async Task<bool> StoreRefreshTokenAsync(string userId, Guid sessionId, string tokenHash, DateTime expiresAtUtc,
        bool requireUnchangedTokensValidAfter = false, DateTime? seenTokensValidAfter = null)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            DELETE FROM RefreshTokens WHERE UserId = @UserId AND ExpiresAt < SYSUTCDATETIME();
            INSERT INTO RefreshTokens (UserId, SessionId, TokenHash, ExpiresAt)
            SELECT @UserId, @SessionId, @TokenHash, @ExpiresAt
            WHERE EXISTS (
                SELECT 1 FROM Users WHERE Id = @UserId AND SuspendedAt IS NULL
                AND (@Guard = 0 OR (TokensValidAfter IS NULL AND @Seen IS NULL) OR TokensValidAfter = @Seen));
            DECLARE @Stored INT = @@ROWCOUNT;
            DELETE FROM Sessions WHERE UserId = @UserId
                AND NOT EXISTS (SELECT 1 FROM RefreshTokens r WHERE r.SessionId = Sessions.Id);
            SELECT @Stored;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@ExpiresAt", expiresAtUtc);
        command.Parameters.AddWithValue("@Guard", requireUnchangedTokensValidAfter);
        command.Parameters.Add("@Seen", System.Data.SqlDbType.DateTime2).Value = (object?)seenTokensValidAfter ?? DBNull.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }



    // SinceRevoked is measured by the database clock, so it isn't affected by clock differences with the API.
    // SessionId is null for tokens from before sessions existed. Replaced is true if the token was
    // rotated (a newer token exists in its session), as opposed to revoked by logging out or signing
    // the session out.
    public record RefreshTokenUse(string UserId, bool Valid, TimeSpan? SinceRevoked, Guid? SessionId = null, bool Replaced = false,
        DateTime? UserTokensValidAfter = null);

    // Atomically revoke a refresh token so it can only be used once.
    // Returns null if the token doesn't exist. Valid is true only if this call revoked an active, unexpired token.
    public async Task<RefreshTokenUse?> ConsumeRefreshTokenAsync(string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Read before consuming: if an administrator sets the password after this point, the
        // value changes and the replacement token is refused (see StoreRefreshTokenAsync). If the
        // change committed before, the token was revoked with the rest and the consume below fails.
        DateTime? tokensValidAfter = null;
        await using (var read = new SqlCommand(
            "SELECT u.TokensValidAfter FROM RefreshTokens t JOIN Users u ON u.Id = t.UserId WHERE t.TokenHash = @TokenHash", connection))
        {
            read.Parameters.AddWithValue("@TokenHash", tokenHash);
            if (await read.ExecuteScalarAsync() is DateTime seen)
                tokensValidAfter = seen;
        }

        const string consume = @"
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME()
            OUTPUT inserted.UserId, inserted.ExpiresAt, inserted.SessionId
            WHERE TokenHash = @TokenHash AND RevokedAt IS NULL;";

        await using (var command = new SqlCommand(consume, connection))
        {
            command.Parameters.AddWithValue("@TokenHash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var expiresAt = reader.GetDateTime(1);
                return new RefreshTokenUse(reader.GetGuid(0).ToString(), expiresAt > DateTime.UtcNow, null,
                    reader.IsDBNull(2) ? null : reader.GetGuid(2), false, tokensValidAfter);
            }
        }

        // Not active: either unknown or already used/revoked
        const string lookup = @"
            SELECT t.UserId, DATEDIFF_BIG(millisecond, t.RevokedAt, SYSUTCDATETIME()), t.SessionId,
                   CASE WHEN EXISTS (SELECT 1 FROM RefreshTokens n WHERE n.SessionId = t.SessionId AND n.Id > t.Id)
                        THEN 1 ELSE 0 END
            FROM RefreshTokens t WHERE t.TokenHash = @TokenHash";
        await using (var command = new SqlCommand(lookup, connection))
        {
            command.Parameters.AddWithValue("@TokenHash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return new RefreshTokenUse(
                    reader.GetGuid(0).ToString(),
                    false,
                    reader.IsDBNull(1) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetInt32(3) == 1);
        }

        return null;
    }



    // Revoke one refresh token (logout)
    public async Task RevokeRefreshTokenAsync(string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE TokenHash = @TokenHash AND RevokedAt IS NULL";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        await command.ExecuteNonQueryAsync();
    }



    // Revoke every refresh token for a user (password change, suspected token theft)
    public async Task RevokeAllRefreshTokensAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @UserId AND RevokedAt IS NULL";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }



    public record AvatarRecord(string WrappedKey, long Size, string MimeType, DateTime UpdatedAt);

    // The user's avatar metadata, or null if they haven't set one
    public async Task<AvatarRecord?> GetAvatarAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT AvatarWrappedKey, AvatarSize, AvatarMimeType, AvatarUpdatedAt
            FROM Users WHERE Id = @UserId AND AvatarWrappedKey IS NOT NULL";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new AvatarRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetDateTime(3));
    }



    // Save (or clear, with null) the user's avatar metadata
    public async Task SetAvatarAsync(string userId, string? wrappedKey, long? size, string? mimeType)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Users
            SET AvatarWrappedKey = @WrappedKey, AvatarSize = @Size, AvatarMimeType = @MimeType,
                AvatarUpdatedAt = CASE WHEN @WrappedKey IS NULL THEN NULL ELSE SYSUTCDATETIME() END
            WHERE Id = @UserId";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@WrappedKey", (object?)wrappedKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@Size", (object?)size ?? DBNull.Value);
        command.Parameters.AddWithValue("@MimeType", (object?)mimeType ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }
}
