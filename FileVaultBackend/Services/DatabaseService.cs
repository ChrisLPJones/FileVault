using FileVaultBackend.Models;
using Microsoft.Data.SqlClient;

namespace FileVaultBackend.Services;

public class DatabaseServices
{
    // Connection string for db access, injected through configuration
    private readonly string _connectionString;
    public DatabaseServices(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection");
    }





    // Check connection to 
    public async Task CheckConnection()
    {
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
        }
    }





    public async Task AddFile(string fileName, string guid)
    {
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            string query = @"
            INSERT INTO Files (FileName, guid)
            VALUES (@FileName, @guid);";

            using (SqlCommand command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@FileName", fileName);
                command.Parameters.AddWithValue("@guid", guid);

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





    public async Task DeleteFileMetadata(string fileName)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            string query = @"
            DELETE FROM Files 
            WHERE FileName = @FileName;";

            using (SqlCommand command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("FileName", fileName);

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





    public List<string> GetFilesFromDb(){
        string fileName = null;

        List<string> filesList = [];

        using SqlConnection connection = new SqlConnection(_connectionString);
        connection.Open();

        string query = "SELECT * FROM FILES WHERE FileName IS NOT NULL";

        using SqlCommand command = new SqlCommand(query, connection);

        using var reader = command.ExecuteReader();
        
        while (reader.Read())
        {
            fileName = reader["FileName"].ToString();
            filesList.Add(fileName);
        }
        
        return filesList;
    }





    public async Task<string> GetFileGUIDAsync(string fileName)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "SELECT GUID from Files where FileName = @FileName";
        using SqlCommand command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@FileName", fileName);

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






    public async Task<HttpReturnResult> RegisterUser(string Username, string Email, string PasswordHash)
    {
        using SqlConnection connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Users (Username, Email, PasswordHash) VALUES (@Username, @Email, @PasswordHash)";
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
            Console.WriteLine (ex.Message);
            return new HttpReturnResult(false, "Database Error");
        }
    }
}
