using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Favourites (starred items) and recently opened files
public partial class DatabaseServices
{
    // Star or unstar one of the user's files or folders; false if it doesn't exist
    public async Task<bool> SetFavouriteAsync(string guid, string userId, bool favourite)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Files SET Favourite = @Favourite WHERE GUID = @GUID AND UserId = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Favourite", favourite);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }



    // Record that the user just opened (previewed or downloaded) one of their files; false if it doesn't exist
    public async Task<bool> MarkOpenedAsync(string guid, string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Files SET LastOpenedAt = SYSUTCDATETIME() WHERE GUID = @GUID AND UserId = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@GUID", guid);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }
}
