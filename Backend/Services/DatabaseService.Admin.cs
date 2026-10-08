using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// The admin page: who is an administrator, every user's usage, quotas and totals
public partial class DatabaseServices
{
    // True if the user exists and is an administrator (checked on every admin request)
    public async Task<bool> IsAdminAsync(string userId)
    {
        if (!Guid.TryParse(userId, out _))
            return false;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "SELECT IsAdmin FROM Users WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteScalarAsync() is bool isAdmin && isAdmin;
    }



    // Make the listed emails administrators and everyone else not; with userId, only that user
    public async Task SyncAdminsAsync(IReadOnlyCollection<string> adminEmails, string? userId = null)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // The list goes in as JSON so any number of emails is one parameter
        // Only rows that change are written, so this doesn't lock every account
        const string query = @"
            WITH Target AS (
                SELECT Id, IsAdmin,
                       CASE WHEN LOWER(Email) IN (SELECT LOWER(value) FROM OPENJSON(@Emails)) THEN 1 ELSE 0 END AS ShouldBeAdmin
                FROM Users
                WHERE @UserId IS NULL OR Id = @UserId
            )
            UPDATE Target SET IsAdmin = ShouldBeAdmin WHERE IsAdmin <> ShouldBeAdmin";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Emails", System.Text.Json.JsonSerializer.Serialize(adminEmails));
        command.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }



    // Every account with its storage use and file count, oldest first
    public async Task<List<AdminUser>> GetUsersForAdminAsync(long defaultQuota)
    {
        var users = new List<AdminUser>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT u.Id, u.FirstName, u.LastName, u.Email, u.CreatedAt, u.LastLogin, u.StorageQuota, u.IsAdmin,
                   COALESCE(SUM(CASE WHEN f.IsDirectory = 0 THEN f.Size END), 0) AS BytesUsed,
                   COUNT(CASE WHEN f.IsDirectory = 0 THEN 1 END) AS FileCount
            FROM Users u
            LEFT JOIN Files f ON f.UserId = u.Id
            GROUP BY u.Id, u.FirstName, u.LastName, u.Email, u.CreatedAt, u.LastLogin, u.StorageQuota, u.IsAdmin
            ORDER BY u.CreatedAt, u.Email";

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var quotaOverride = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            users.Add(new AdminUser(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                reader.GetInt64(8),
                reader.GetInt32(9),
                quotaOverride ?? defaultQuota,
                quotaOverride,
                reader.GetBoolean(7)));
        }

        return users;
    }



    // Set a user's quota (null = the default); false if there's no such user
    public async Task<bool> SetStorageQuotaAsync(string userId, long? quotaBytes)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = "UPDATE Users SET StorageQuota = @Quota WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Quota", (object?)quotaBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }



    // Totals across all users: (users, admins, files, folders, bytes stored)
    public async Task<(int users, int admins, int files, int folders, long bytes)> GetAdminTotalsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT
                (SELECT COUNT(*) FROM Users),
                (SELECT COUNT(*) FROM Users WHERE IsAdmin = 1),
                (SELECT COUNT(*) FROM Files WHERE IsDirectory = 0),
                (SELECT COUNT(*) FROM Files WHERE IsDirectory = 1),
                (SELECT COALESCE(SUM(Size), 0) FROM Files WHERE IsDirectory = 0)";

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4));
    }
}
