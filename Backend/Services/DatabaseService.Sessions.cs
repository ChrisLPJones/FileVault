using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Active sessions: one per signed-in device, made up of a chain of refresh tokens
public partial class DatabaseServices
{
    public record SessionRecord(Guid Id, string Device, string? IpAddress, DateTime CreatedAt, DateTime LastUsedAt);

    // Start a session for a new login; returns its ID
    public async Task<Guid> CreateSessionAsync(string userId, string device, string? ipAddress)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            INSERT INTO Sessions (UserId, Device, IpAddress) OUTPUT inserted.Id
            VALUES (@UserId, @Device, @IpAddress)";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Device", device);
        command.Parameters.AddWithValue("@IpAddress", (object?)ipAddress ?? DBNull.Value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }



    // Record that the session was just used (on each refresh), from which address
    public async Task TouchSessionAsync(Guid sessionId, string? ipAddress)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            UPDATE Sessions SET LastUsedAt = SYSUTCDATETIME(), IpAddress = COALESCE(@IpAddress, IpAddress)
            WHERE Id = @SessionId";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@IpAddress", (object?)ipAddress ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }



    // The session a refresh token (by hash) belongs to, whatever its state; null if unknown
    public async Task<Guid?> GetSessionIdForTokenAsync(string tokenHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT SessionId FROM RefreshTokens WHERE TokenHash = @TokenHash", connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        return await command.ExecuteScalarAsync() is Guid id ? id : null;
    }



    // The user's sessions that still have a usable refresh token, most recently used first
    public async Task<List<SessionRecord>> GetActiveSessionsAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT s.Id, s.Device, s.IpAddress, s.CreatedAt, s.LastUsedAt
            FROM Sessions s
            WHERE s.UserId = @UserId AND EXISTS (
                SELECT 1 FROM RefreshTokens r
                WHERE r.SessionId = s.Id AND r.RevokedAt IS NULL AND r.ExpiresAt > SYSUTCDATETIME())
            ORDER BY s.LastUsedAt DESC";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);

        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sessions.Add(new SessionRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));

        return sessions;
    }



    // Sign one of the user's sessions out by revoking its refresh tokens.
    // False if it isn't theirs or is already signed out.
    // Like every other token writer this locks the user's Users row first (see
    // ConsumeAndReplaceRefreshTokenAsync); a bare UPDATE on RefreshTokens takes its locks in the
    // opposite order to a concurrent refresh or login and could be chosen as a deadlock victim.
    public Task<bool> RevokeSessionAsync(string userId, Guid sessionId) =>
        RetryOnDeadlockAsync(async () => await RevokeLockedAsync(@"
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND SessionId = @SessionId AND RevokedAt IS NULL",
            userId, cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId)) > 0);



    // Sign out every session except `keepSessionId` (every session if it's null),
    // including tokens from before sessions existed. Returns how many tokens were revoked.
    public Task<int> RevokeOtherSessionsAsync(string userId, Guid? keepSessionId) =>
        RetryOnDeadlockAsync(() => RevokeLockedAsync(@"
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND RevokedAt IS NULL
              AND (@KeepSessionId IS NULL OR SessionId IS NULL OR SessionId <> @KeepSessionId)",
            userId, cmd => cmd.Parameters.Add("@KeepSessionId", System.Data.SqlDbType.UniqueIdentifier).Value =
                (object?)keepSessionId ?? DBNull.Value));



    // Runs a token-revoking UPDATE in a transaction that first takes the user's Users row lock
    // (the same order as the refresh, login, password-change and suspension paths); returns the rows revoked
    private async Task<int> RevokeLockedAsync(string update, string userId, Action<SqlCommand> addParameters)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = new SqlCommand(@"
            DECLARE @L INT;
            SELECT @L = 1 FROM Users WITH (UPDLOCK, ROWLOCK) WHERE Id = @UserId;
            " + update + @";
            SELECT @@ROWCOUNT;", connection, transaction);
        command.Parameters.AddWithValue("@UserId", userId);
        addParameters(command);
        var revoked = Convert.ToInt32(await command.ExecuteScalarAsync());
        await transaction.CommitAsync();
        return revoked;
    }
}
