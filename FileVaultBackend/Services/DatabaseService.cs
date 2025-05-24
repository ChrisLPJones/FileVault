using FileVaultBackend.Models;
using Microsoft.Data.SqlClient;
using System.Security.Claims;

namespace FileVaultBackend.Services;

// Service responsible for interacting with the SQL database
public class DatabaseServices
{





    // Connection string for db access, injected through configuration
    private readonly string _connectionString;

    public DatabaseServices(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection");
    }






    // Checks if the database connection can be successfully opened
    public async Task CheckConnection()
    {
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
        }
    }






    // Adds a file record to the Files table with the provided filename and GUID
    public async Task AddFile(string fileName, string guid, string userId)
    {
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            string query = @"
            INSERT INTO Files (FileName, guid, UserId)
            VALUES (@FileName, @guid, @UserId);";

            using (SqlCommand command = new SqlCommand(query, connection))
            {
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
        }
    }






    // Deletes file metadata from the database by file name
    public async Task DeleteFileMetadata(string fileName, string userId)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            string query = @"DELETE FROM Files WHERE UserId = @UserId AND FileName = @FileName;";

            using (SqlCommand command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("FileName", fileName);
                command.Parameters.AddWithValue("userId", userId);

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
        }
    }






    // Retrieves all filenames from the Files table and returns them as a list
    public List<string> GetFilesFromDb(string userId)
    {
        string fileName = null;
        List<string> filesList = [];

        using SqlConnection connection = new SqlConnection(_connectionString);
        connection.Open();

        string query = "SELECT * FROM FILES WHERE FileName IS NOT NULL AND UserId = @UserId";

        using SqlCommand command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            fileName = reader["FileName"].ToString();
            filesList.Add(fileName);
        }

        return filesList;
    }






    // Retrieves the GUID associated with a given filename
    public async Task<string> GetFileGUIDAsync(string fileName, string userId)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT GUID from Files where UserId = @UserId AND FileName = @FileName";
        using SqlCommand command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@UserId", userId);

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return reader.GetString(0);
        }
        else
        {
            Console.WriteLine($"Error: File does not exist in db");
            return null;
        }
    }






    // Registers a new user in the Users table
    public async Task<HttpReturnResult> RegisterUser(string Username, string Email, string PasswordHash)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Users (Username, Email, PasswordHash) " +
            "VALUES (@Username, @Email, @PasswordHash)";
        using SqlCommand command = new SqlCommand(@query, connection);

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
            return new HttpReturnResult(false, $"User {Username} already exists.");
        }
        catch (SqlException ex)
        {
            Console.WriteLine(ex.Message);
            return new HttpReturnResult(false, "Database Error");
        }
    }






    // Retrieves user info from the Users table by username
    internal async Task<UserModel> GetUserByUsername(string username)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, Username, Email, PasswordHash FROM Users WHERE Username = @Username";
        using SqlCommand command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("username", username);

        using SqlDataReader reader = command.ExecuteReader();

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






    // Updates user info in the Users table; only updates fields that are not null/empty
    public async Task<HttpReturnResult> UpdateUser(string username, UserModel updateUser)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var updates = new List<string>();
        using var command = new SqlCommand();
        command.Connection = connection;

        var user = await GetUserByUsername(username);

        // Add parameters and update clauses conditionally
        if (!string.IsNullOrWhiteSpace(updateUser.Username) && user.Username != updateUser.Username)
        {
            updates.Add("Username = @NewUsername");
            command.Parameters.AddWithValue("@NewUsername", updateUser.Username);
        }

        if (!string.IsNullOrWhiteSpace(updateUser.Email) && user.Email != updateUser.Email)
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






        // Compose final SQL query dynamically based on which fields are being updated
        string setClause = string.Join(", ", updates);
        string query = $"UPDATE Users SET {setClause} Where Username = @Username";
        command.CommandText = query;
        command.Parameters.AddWithValue("@Username", username);

        try
        {
            int rowsAffected = await command.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
                return new HttpReturnResult(true, "Nothing changes where made");
            return new HttpReturnResult(true, "Updated user info");
        }
        catch (Exception ex)
        {
            return new HttpReturnResult(false, $"Error: {ex.Message}");
        }
    }






    // Deletes a user from the Users table based on username
    public void DeleteUser(string username)
    {
        SqlConnection connection = new SqlConnection(_connectionString);
        connection.OpenAsync();

        string query = "DELETE FROM Users WHERE Username = @Username";

        using SqlCommand command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Username", username);

        try
        {
            command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    internal async Task<UserModel> GetUserByUserId(string userId)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, Username, Email, PasswordHash FROM Users WHERE Id = @Id";
        using SqlCommand command = new SqlCommand(query, connection);

        command.Parameters.AddWithValue("Id", userId);

        using SqlDataReader reader = command.ExecuteReader();

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
