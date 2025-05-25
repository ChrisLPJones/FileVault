using FileVaultBackend.Models;
using Microsoft.Data.SqlClient;
using System.Security.Claims;

namespace FileVaultBackend.Services;

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
    public async Task AddFile(string fileName, string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = @"
            INSERT INTO Files (FileName, guid, UserId)
            VALUES (@FileName, @guid, @UserId);";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@guid", guid);
        command.Parameters.AddWithValue("@UserId", userId);

        try
        {
            await command.ExecuteNonQueryAsync();
            Console.WriteLine("File metadata added to db");
        }
        catch (SqlException ex)
        {
            Console.WriteLine($"Error: {ex}");
        }
    }

    // Remove file metadata from the database for the given user and filename
    public async Task DeleteFileMetadata(string fileName, string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = @"DELETE FROM Files WHERE UserId = @UserId AND FileName = @FileName;";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@UserId", userId);

        try
        {
            await command.ExecuteNonQueryAsync();
            Console.WriteLine("File metadata deleted from db");
        }
        catch (SqlException)
        {
            Console.WriteLine("Error: metadata not found ");
        }
    }

    // Retrieve all filenames that belong to a specific user
    public List<string> GetFilesFromDb(string userId)
    {
        var filesList = new List<string>();

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        string query = "SELECT FileName FROM Files WHERE FileName IS NOT NULL AND UserId = @UserId";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            filesList.Add(reader["FileName"].ToString());
        }

        return filesList;
    }

    // Get the unique identifier (GUID) for a specific file owned by the user
    public async Task<string> GetFileGUIDAsync(string fileName, string userId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT GUID FROM Files WHERE UserId = @UserId AND FileName = @FileName";

        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return reader.GetString(0);
        }
        else
        {
            Console.WriteLine("Error: File does not exist in db");
            return null;
        }
    }

    // Register a new user in the database, handling duplicate username or email errors
    public async Task<HttpReturnResult> RegisterUser(string Username, string Email, string PasswordHash)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Users (Username, Email, PasswordHash) VALUES (@Username, @Email, @PasswordHash)";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Username", Username.Trim());
        command.Parameters.AddWithValue("@Email", Email.Trim().ToLower());
        command.Parameters.AddWithValue("@PasswordHash", PasswordHash);

        try
        {
            await command.ExecuteNonQueryAsync();
            return new HttpReturnResult(true, $"Successfully added user {Username}");
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            // Duplicate username or email detected
            return new HttpReturnResult(false, $"User {Username} already exists.");
        }
        catch (SqlException ex)
        {
            Console.WriteLine(ex.Message);
            return new HttpReturnResult(false, "Database Error");
        }
    }

    // Retrieve a user's information from the database by username
    internal async Task<UserModel> GetUserByUsername(string username)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, Username, Email, PasswordHash FROM Users WHERE Username = @Username";
        using var command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@Username", username);

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

    // Remove user and all their files metadata from database and delete files from storage
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
    internal async Task<UserModel> GetUserByUserId(string userId)
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
}
