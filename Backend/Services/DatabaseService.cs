using Backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Backend.Services;

public class DatabaseServices
{



    // Store database connection string from configuration
    private readonly string _connectionString;




    // Store root directory path for file storage from configuration
    private readonly string _storageRoot;
    public DatabaseServices(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection");
        _storageRoot = config.GetValue<string>("StorageRoot");
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
        command.Parameters.AddWithValue("@MimeType", (object)mimeType ?? DBNull.Value);
        command.Parameters.AddWithValue("@WrappedKey", (object)wrappedKey ?? DBNull.Value);

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
    public async Task<List<string>> GetFileGuidsInTreeAsync(string guid, string userId)
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

        await command.ExecuteNonQueryAsync();
    }


    private static FileRecord ReadFileRecord(SqlDataReader reader) => new()
    {
        Guid = reader["GUID"].ToString(),
        Name = reader["FileName"].ToString(),
        IsDirectory = Convert.ToBoolean(reader["isDirectory"]),
        Path = reader["FilePath"].ToString(),
        ParentId = string.IsNullOrEmpty(reader["ParentId"]?.ToString()) ? null : reader["ParentId"].ToString(),
        Size = Convert.ToInt64(reader["Size"]),
        MimeType = reader["MimeType"] == DBNull.Value ? null : reader["MimeType"].ToString(),
        WrappedKey = reader["WrappedKey"] == DBNull.Value ? null : reader["WrappedKey"].ToString()
    };



    // Get a single file or folder owned by the user, or null if it doesn't exist
    public async Task<FileRecord> GetItemAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT GUID, FileName, isDirectory, FilePath, ParentId, Size, MimeType, WrappedKey
            FROM Files WHERE GUID = @GUID AND UserId = @UserId";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadFileRecord(reader) : null;
    }



    // Get an item and everything below it, parents before children
    public async Task<List<FileRecord>> GetTreeAsync(string guid, string userId)
    {
        var items = new List<FileRecord>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            WITH Tree AS (
                SELECT GUID, 0 AS Depth FROM Files WHERE GUID = @GUID AND UserId = @UserId
                UNION ALL
                SELECT f.GUID, t.Depth + 1 FROM Files f
                INNER JOIN Tree t ON f.ParentId = t.GUID
                WHERE f.UserId = @UserId
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
    public async Task<HashSet<string>> GetNamesInFolderAsync(string parentId, string userId, string excludeGuid = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Older rows may store root as '' instead of NULL
        const string query = @"
            SELECT FileName FROM Files
            WHERE UserId = @UserId
              AND ((@ParentId IS NULL AND (ParentId IS NULL OR ParentId = '')) OR ParentId = @ParentId)
              AND (@ExcludeGuid IS NULL OR GUID <> @ExcludeGuid)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@ParentId", (object)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExcludeGuid", (object)excludeGuid ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        return names;
    }



    // Rename and/or move an item, rewriting the stored path of everything below it
    public async Task RelocateAsync(FileRecord item, string newParentId, string newName, string newPath, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            const string updateItem = @"
                UPDATE Files
                SET FileName = @Name, FilePath = @Path, ParentId = @ParentId, UpdatedAt = GETDATE()
                WHERE GUID = @GUID AND UserId = @UserId";

            await using (var command = new SqlCommand(updateItem, connection, transaction))
            {
                command.Parameters.AddWithValue("@Name", newName);
                command.Parameters.AddWithValue("@Path", newPath);
                command.Parameters.AddWithValue("@ParentId", (object)newParentId ?? DBNull.Value);
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
    public async Task InsertItemsAsync(List<FileRecord> items, string userId)
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
                command.Parameters.AddWithValue("@ParentId", (object)item.ParentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@MimeType", (object)item.MimeType ?? DBNull.Value);
                command.Parameters.AddWithValue("@WrappedKey", (object)item.WrappedKey ?? DBNull.Value);
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
    public async Task<FolderModel> GetFolderById(string folderId, string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, FileName, FilePath, GUID, UserId, isDirectory FROM Files WHERE GUID = @GUID AND UserId = @UserId AND isDirectory = 1";
        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", folderId);
        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new FolderModel
            {
                _id = reader["GUID"].ToString(),
                Name = reader["FileName"].ToString(),
                Path = reader["FilePath"].ToString(),
                IsDirectory = Convert.ToBoolean(reader["isDirectory"]),
                UserId = reader["UserId"].ToString()
            };
        }

        return null;
    }




    // Retrieve all filenames that belong to a specific user
    public List<FileModel> GetFilesFromDb(string userId)
    {
        var filesList = new List<FileModel>();

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        string query = "SELECT Id, FileName, FilePath, UpdatedAt, GUID, isDirectory, Size FROM Files WHERE FileName IS NOT NULL AND UserId = @UserId";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            filesList.Add(new FileModel
            {
                _id = reader["GUID"].ToString(),
                Name = reader["FileName"].ToString(),
                Path = reader["FilePath"].ToString(),
                UpdatedAt = Convert.ToDateTime(reader["UpdatedAt"]),
                Size = Convert.ToInt64(reader["Size"]),
                IsDirectory = Convert.ToBoolean(reader["isDirectory"])
            });
        }

        return filesList;
    }








    // Register a new user in the database, handling duplicate username or email errors
    public async Task RegisterUser(UserModel user)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Users (Username, Email, PasswordHash) VALUES (@Username, @Email, @PasswordHash)";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Username", user.Username.Trim());
        command.Parameters.AddWithValue("@Email", user.Email.Trim().ToLower());
        command.Parameters.AddWithValue("@PasswordHash", user.Password);

        await command.ExecuteNonQueryAsync();

    }




    // Retrieve a user's information from the database by username
    public async Task<UserModel> GetUserByEmail(string email)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, Username, Email, PasswordHash FROM Users WHERE Email = @Email";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Email", email);

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return new UserModel
            {
                Id = reader.GetGuid(0),
                Username = reader.GetString(1),
                Email = reader.GetString(2),
                Password = reader.GetString(3),
            };
        }
        return null;
    }




    // Update user data only for fields that have been changed
    public async Task<HttpReturnResult> UpdateUser(UserModel oldUser, UserModel updateUser, string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var updates = new List<string>();
        using var command = new SqlCommand { Connection = connection };

        if (!string.IsNullOrWhiteSpace(updateUser.Username) && oldUser.Username != updateUser.Username)
        {
            updates.Add("Username = @NewUsername");
            command.Parameters.AddWithValue("@NewUsername", updateUser.Username);
        }

        if (!string.IsNullOrWhiteSpace(updateUser.Email) && oldUser.Email != updateUser.Email)
        {
            updates.Add("Email = @Email");
            command.Parameters.AddWithValue("@Email", updateUser.Email);
        }

        if (!string.IsNullOrEmpty(updateUser.Password))
        {
            updates.Add("PasswordHash = @Password");
            command.Parameters.AddWithValue("@Password", updateUser.Password);
        }

        if (updates.Count == 0)
            return new HttpReturnResult(false, "Nothing to update");

        string setClause = string.Join(", ", updates);
        command.CommandText = $"UPDATE Users SET {setClause} WHERE Id = @UserId";
        command.Parameters.AddWithValue("@UserId", userId);

        try
        {
            int rowsAffected = await command.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
                return new HttpReturnResult(true, "Nothing changes were made");
            return new HttpReturnResult(true, "Updated user info");
        }
        catch (Exception ex)
        {
            return new HttpReturnResult(false, $"Error: {ex.Message}");
        }
    }



    // Remove user and all file metadata from database and delete files from storage
    public async Task<HttpReturnResult> DeleteUserAndFilesById(string userId, FileServices fs)
    {
        var files = new List<string>();

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();

        try
        {
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
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"SQL error: {ex.Message}");
            return new HttpReturnResult(false, "Error deleting user and files");
        }

        // Remove all user's files from storage after successful DB transaction
        await fs.DeleteAllFilesFromUser(files);

        return new HttpReturnResult(true, "User's files and account deleted");
    }




    // Retrieve user details by their user ID
    public async Task<UserModel> GetUserByUserId(string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, Username, Email, PasswordHash FROM Users WHERE Id = @Id";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Id", userId);

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return new UserModel
            {
                Id = reader.GetGuid(0),
                Username = reader.GetString(1),
                Email = reader.GetString(2),
                Password = reader.GetString(3),
            };
        }

        return null;
    }




    // 
    public async Task UpdateUserLastLogin(LoginModel user)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "Update Users SET LastLogin = @LastLogin WHERE Email = @Email";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@LastLogin", DateTime.UtcNow);
        command.Parameters.AddWithValue("@Email", user.Email);

        await command.ExecuteNonQueryAsync();
    }




    public async Task<bool> UserExistsByUsername(string username)
    {
        using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT COUNT(1) FROM Users WHERE Username = @Username";

        using SqlCommand command = new(query, connection);
        command.Parameters.AddWithValue("@Username", username);

        int count = (int)await command.ExecuteScalarAsync();

        return count > 0;
    }




    public async Task<bool> UserExistsByEmail(string email)
    {
        using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT COUNT(1) FROM Users WHERE Email = @Email";

        using SqlCommand command = new(query, connection);
        command.Parameters.AddWithValue("@Email", email);

        int count = (int)await command.ExecuteScalarAsync();

        return count > 0;
    }



    // Store a new refresh token (hash only) and drop this user's expired ones
    public async Task StoreRefreshTokenAsync(string userId, string tokenHash, DateTime expiresAtUtc)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            DELETE FROM RefreshTokens WHERE UserId = @UserId AND ExpiresAt < SYSUTCDATETIME();
            INSERT INTO RefreshTokens (UserId, TokenHash, ExpiresAt) VALUES (@UserId, @TokenHash, @ExpiresAt);";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@ExpiresAt", expiresAtUtc);
        await command.ExecuteNonQueryAsync();
    }



    public record RefreshTokenUse(string UserId, bool Valid, DateTime? RevokedAt);

    // Atomically revoke a refresh token so it can only be used once.
    // Returns null if the token doesn't exist. Valid is true only if this call revoked an active, unexpired token.
    public async Task<RefreshTokenUse> ConsumeRefreshTokenAsync(string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string consume = @"
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME()
            OUTPUT inserted.UserId, inserted.ExpiresAt
            WHERE TokenHash = @TokenHash AND RevokedAt IS NULL;";

        await using (var command = new SqlCommand(consume, connection))
        {
            command.Parameters.AddWithValue("@TokenHash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var expiresAt = reader.GetDateTime(1);
                return new RefreshTokenUse(reader.GetGuid(0).ToString(), expiresAt > DateTime.UtcNow, null);
            }
        }

        // Not active: either unknown or already used/revoked
        const string lookup = "SELECT UserId, RevokedAt FROM RefreshTokens WHERE TokenHash = @TokenHash";
        await using (var command = new SqlCommand(lookup, connection))
        {
            command.Parameters.AddWithValue("@TokenHash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return new RefreshTokenUse(reader.GetGuid(0).ToString(), false, reader.GetDateTime(1));
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
}
