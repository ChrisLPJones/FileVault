using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Per-user display preferences (Users.IconTheme)
public partial class DatabaseServices
{
    public static readonly string[] IconThemes = ["default", "windows", "macos", "ubuntu"];

    // NULL in the database means "default"
    public async Task<string> GetIconThemeAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "SELECT IconTheme FROM Users WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteScalarAsync() is string theme && IconThemes.Contains(theme) ? theme : "default";
    }

    public async Task SetIconThemeAsync(string userId, string theme)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Users SET IconTheme = @Theme WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Theme", theme == "default" ? DBNull.Value : theme);
        await command.ExecuteNonQueryAsync();
    }
}
