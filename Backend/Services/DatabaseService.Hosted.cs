using Microsoft.Data.SqlClient;

namespace Backend.Services;

// Hosted mode: the inactivity clock, the warning and removal of inactive accounts, and the
// first-login notice
public partial class DatabaseServices
{
    // The moment an account was last active. Accounts from before LastActiveAt existed fall back to
    // their last login, then to when they were created.
    private const string ActivityClock = "COALESCE(LastActiveAt, LastLogin, CreatedAt)";

    // Accounts the inactivity job never touches: administrators, permanent and suspended accounts
    private const string ExemptFromRemovalPredicate = "IsAdmin = 0 AND IsPermanent = 0 AND SuspendedAt IS NULL";

    // What an account must still satisfy at the moment it is deleted for inactivity: not exempt,
    // inactive for @InactiveDays, and warned at least @WarningDays ago
    internal const string DueForRemovalPredicate = ExemptFromRemovalPredicate + @"
        AND " + ActivityClock + @" < DATEADD(day, -@InactiveDays, SYSUTCDATETIME())
        AND InactivityWarnedAt IS NOT NULL AND InactivityWarnedAt < DATEADD(day, -@WarningDays, SYSUTCDATETIME())";

    // Record that the user signed in or is using the app, and cancel any removal warning. Written
    // at most about once an hour (the update does nothing when the last write is newer), so a
    // renewed access token costs no write most of the time.
    public async Task TouchActivityAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
            UPDATE Users SET LastActiveAt = SYSUTCDATETIME(), InactivityWarnedAt = NULL
            WHERE Id = @UserId
              AND (LastActiveAt IS NULL OR LastActiveAt < DATEADD(hour, -1, SYSUTCDATETIME()) OR InactivityWarnedAt IS NOT NULL)",
            connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }

    public record InactivityWarning(string UserId, string Email, string FirstName, bool EmailVerified,
        DateTime WarnedAt, DateTime LastActiveAt);

    // Phase one: mark up to maxCount accounts inactive for InactiveDays - WarningDays as warned, in
    // one statement, so two API instances can't both warn the same account. Returns the accounts
    // just warned. onlyUsers narrows it to those accounts (tests); null = everyone.
    public async Task<List<InactivityWarning>> ClaimInactivityWarningsAsync(HostedOptions hosted, int maxCount,
        IReadOnlyCollection<Guid>? onlyUsers = null)
    {
        var warned = new List<InactivityWarning>();
        if (onlyUsers is { Count: 0 })
            return warned;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand($@"
            UPDATE TOP (@Max) Users SET InactivityWarnedAt = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.Email, inserted.FirstName, inserted.EmailVerified,
                   inserted.InactivityWarnedAt, COALESCE(inserted.LastActiveAt, inserted.LastLogin, inserted.CreatedAt)
            WHERE InactivityWarnedAt IS NULL AND {ExemptFromRemovalPredicate}
              AND {ActivityClock} < DATEADD(day, -(@InactiveDays - @WarningDays), SYSUTCDATETIME())
              {OnlyUsersClause(onlyUsers)}", connection);
        command.Parameters.AddWithValue("@Max", maxCount);
        command.Parameters.AddWithValue("@InactiveDays", hosted.InactiveDays);
        command.Parameters.AddWithValue("@WarningDays", hosted.WarningDays);
        AddOnlyUsers(command, onlyUsers);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            warned.Add(new InactivityWarning(reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
        return warned;
    }

    // Phase two's candidates: up to maxCount accounts inactive for InactiveDays and warned at
    // least WarningDays ago. Each is checked again when it is deleted.
    public async Task<List<string>> GetAccountsDueForRemovalAsync(HostedOptions hosted, int maxCount,
        IReadOnlyCollection<Guid>? onlyUsers = null)
    {
        var ids = new List<string>();
        if (onlyUsers is { Count: 0 })
            return ids;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand($@"
            SELECT TOP (@Max) Id FROM Users
            WHERE {DueForRemovalPredicate} {OnlyUsersClause(onlyUsers)}
            ORDER BY {ActivityClock}", connection);
        command.Parameters.AddWithValue("@Max", maxCount);
        command.Parameters.AddWithValue("@InactiveDays", hosted.InactiveDays);
        command.Parameters.AddWithValue("@WarningDays", hosted.WarningDays);
        AddOnlyUsers(command, onlyUsers);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0).ToString());
        return ids;
    }

    private static string OnlyUsersClause(IReadOnlyCollection<Guid>? onlyUsers) =>
        onlyUsers == null ? "" : "AND Id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM STRING_SPLIT(@OnlyUsers, ','))";

    private static void AddOnlyUsers(SqlCommand command, IReadOnlyCollection<Guid>? onlyUsers)
    {
        if (onlyUsers != null)
            command.Parameters.AddWithValue("@OnlyUsers", string.Join(',', onlyUsers));
    }

    // When the account is due to be removed, or null when it is exempt (administrator, permanent or
    // suspended). Never earlier than InactiveDays after the last activity, nor than WarningDays
    // after the warning (an account not yet warned will be warned first).
    public static DateTime? RemovalDueAt(HostedOptions hosted, bool isAdmin, bool isPermanent, bool suspended,
        DateTime lastActiveAt, DateTime? warnedAt)
    {
        if (isAdmin || isPermanent || suspended)
            return null;

        var byInactivity = lastActiveAt.AddDays(hosted.InactiveDays);
        var warned = warnedAt ?? lastActiveAt.AddDays(hosted.InactiveDays - hosted.WarningDays);
        var byWarning = warned.AddDays(hosted.WarningDays);
        return byInactivity > byWarning ? byInactivity : byWarning;
    }

    // True if the first-login notice should show: not dismissed, and the account is neither an
    // administrator nor permanent. (Whether the install is hosted is the caller's check.)
    public async Task<bool> ShouldShowHostedNoticeAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
            SELECT CASE WHEN HostedNoticeDismissedAt IS NULL AND IsAdmin = 0 AND IsPermanent = 0
                        THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
            FROM Users WHERE Id = @UserId", connection);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteScalarAsync() is bool show && show;
    }

    // Remember that the user dismissed the notice (does nothing if they already had)
    public async Task DismissHostedNoticeAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "UPDATE Users SET HostedNoticeDismissedAt = SYSUTCDATETIME() WHERE Id = @UserId AND HostedNoticeDismissedAt IS NULL", connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }
}
