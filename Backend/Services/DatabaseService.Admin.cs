using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// The admin-membership lock was lost as a deadlock victim; the whole action is safe to run again
// (RetryOnDeadlockAsync does)
public sealed class AppLockDeadlockException(string message) : Exception(message);

// The admin-membership lock couldn't be had in time; the API answers 503
public sealed class AdminLockTimeoutException(string message) : Exception(message);

// The admin page: who is an administrator, every user's usage, quotas and totals
public partial class DatabaseServices
{
    // True if the user exists, is an administrator and is not suspended (checked on every admin request)
    public async Task<bool> IsAdminAsync(string userId)
    {
        if (!Guid.TryParse(userId, out _))
            return false;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = $"SELECT CASE WHEN {EffectiveAdminPredicate} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END FROM Users WHERE Id = @UserId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteScalarAsync() is bool isAdmin && isAdmin;
    }



    // Every change that can add or remove an administrator (registering the first account, granting
    // or removing admin, deleting an account) takes this application lock first, in the same
    // transaction as its write, so two of them can never both act on a stale view of who the
    // administrators are. The lock is released when that transaction ends.
    private const string AdminMembershipLock = "fv-admin-membership";

    // What counts as an administrator for the "at least one" rule and for admin access: a
    // suspended admin doesn't count.
    private const string EffectiveAdminPredicate = "IsAdmin = 1 AND SuspendedAt IS NULL";

    public const string LastAdminMessage = "There must always be at least one administrator";

    // Wait for the admin-membership lock. SQL Server returns < 0 when it can't be had:
    // -1 timeout, -2 cancelled, -3 deadlock victim, -999 error. A deadlock victim throws
    // AppLockDeadlockException, which RetryOnDeadlockAsync retries like a SQL deadlock, so every
    // caller of this runs inside RetryOnDeadlockAsync. A timeout throws AdminLockTimeoutException
    // (the API answers 503).
    private async Task TakeAdminMembershipLockAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(@"
            DECLARE @Result INT;
            EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive',
                 @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @Result;", connection, transaction);
        command.Parameters.AddWithValue("@Resource", AdminMembershipLock);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (result >= 0)
            return;

        _logger.LogError("Could not take the admin membership lock (sp_getapplock returned {Result})", result);
        if (result != -3)
            await ReportAppLockFailureAsync(AdminMembershipLock, result, connection, transaction);
        // A -999 with the transaction already ended by the server means it was rolled back under us
        // (nothing committed): retry like a deadlock victim
        if (result == -999 && transaction.Connection == null)
            result = -3;
        throw result switch
        {
            -3 => new AppLockDeadlockException("Chosen as a deadlock victim waiting for the admin membership lock"),
            -1 => new AdminLockTimeoutException("Timed out waiting for the admin membership lock"),
            _ => new InvalidOperationException($"Could not take the admin membership lock (sp_getapplock returned {result})")
        };
    }

    // The INITIAL_ADMIN_EMAIL setting (Admin:InitialEmail), trimmed and lower-cased like emails
    // elsewhere; null when it isn't set. When set, the first account is NOT made admin at
    // registration; that email becomes admin when it is confirmed (see ConfirmEmailAsync).
    public string? InitialAdminEmail { get; }

    public static string? NormaliseInitialAdminEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    // Confirm an email address. With INITIAL_ADMIN_EMAIL set, if this is that account and no
    // effective administrator exists, it becomes one in the same transaction, under the admin
    // membership lock. Returns whether it was promoted.
    //
    // distrustPasswordIfPromoted is for the confirmation-link flow: whoever registered the account
    // chose its password, but the link went to the mailbox owner, who may not be them (anyone can
    // register someone else's address first). Opening the link proves the mailbox, not the
    // password, so a promoted account's password is replaced by an unusable one and the owner
    // chooses theirs with "Forgot password". Resetting a password proves both, so that flow
    // passes false. Neither flow ever promotes an account whose email was changed after
    // sign-up (Users.EmailChanged): someone signed in could have set it to the owner's address.
    // Emails are compared byte for byte (BIN2), not under the database's accent/width-folding
    // collation, so look-alike addresses (ss/ß, ae/æ) never match.
    public Task<bool> ConfirmEmailAsync(string userId, bool distrustPasswordIfPromoted) =>
        RetryOnDeadlockAsync(() => ConfirmEmailOnceAsync(userId, distrustPasswordIfPromoted));

    private async Task<bool> ConfirmEmailOnceAsync(string userId, bool distrustPasswordIfPromoted)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        if (InitialAdminEmail != null)
            await TakeAdminMembershipLockAsync(connection, transaction);

        await using (var confirm = new SqlCommand("UPDATE Users SET EmailVerified = 1 WHERE Id = @UserId", connection, transaction))
        {
            confirm.Parameters.AddWithValue("@UserId", userId);
            await confirm.ExecuteNonQueryAsync();
        }

        var promoted = InitialAdminEmail != null
            && await PromoteInitialAdminInTransactionAsync(connection, transaction, userId, distrustPasswordIfPromoted);

        await transaction.CommitAsync();
        return promoted;
    }

    // Make the INITIAL_ADMIN_EMAIL account an administrator if it is confirmed and there is no
    // effective administrator. Call with the admin membership lock held.
    private async Task<bool> PromoteInitialAdminInTransactionAsync(SqlConnection connection, SqlTransaction transaction,
        string userId, bool replacePassword)
    {
        await using var command = new SqlCommand($@"
            UPDATE Users SET IsAdmin = 1, IsPermanent = 1{(replacePassword ? ", PasswordHash = @UnusableHash" : "")}
            WHERE Id = @UserId AND Email COLLATE Latin1_General_BIN2 = @Email AND EmailVerified = 1 AND IsAdmin = 0
              AND EmailChanged = 0 AND SuspendedAt IS NULL
              AND NOT EXISTS (SELECT 1 FROM Users WHERE {EffectiveAdminPredicate})", connection, transaction);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Email", InitialAdminEmail!);
        if (replacePassword)
            command.Parameters.AddWithValue("@UnusableHash", UnusablePasswordHash());
        return await command.ExecuteNonQueryAsync() > 0;
    }

    // A password hash nobody knows the password for (the hash of a random value)
    private static string UnusablePasswordHash() =>
        BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    // Run when the API starts: an install that has accounts but no administrator gets one.
    // With INITIAL_ADMIN_EMAIL set, that account (once confirmed) is the only candidate;
    // otherwise it is the oldest account with a confirmed email. Nothing changes when an
    // administrator exists, so every start is safe. (This used to be in init.sql, which can't see
    // the setting.) Returns the email promoted, if any.
    public Task<string?> EnsureAdministratorAtStartupAsync() => RetryOnDeadlockAsync(async () =>
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await TakeAdminMembershipLockAsync(connection, transaction);

        await using var command = new SqlCommand($@"
            DECLARE @Id UNIQUEIDENTIFIER;
            IF NOT EXISTS (SELECT 1 FROM Users WHERE {EffectiveAdminPredicate})
                SELECT TOP 1 @Id = Id FROM Users
                WHERE EmailVerified = 1 AND SuspendedAt IS NULL AND (@InitialEmail IS NULL OR (Email COLLATE Latin1_General_BIN2 = @InitialEmail AND EmailChanged = 0))
                ORDER BY CASE WHEN CreatedAt IS NULL THEN 1 ELSE 0 END, CreatedAt, Email;
            IF @Id IS NOT NULL
                UPDATE Users SET IsAdmin = 1, IsPermanent = 1 OUTPUT inserted.Email WHERE Id = @Id;", connection, transaction);
        command.Parameters.AddWithValue("@InitialEmail", (object?)InitialAdminEmail ?? DBNull.Value);
        var email = await command.ExecuteScalarAsync() as string;

        await transaction.CommitAsync();
        return email;
    });

    // Grant or remove admin for a user. Removing it from the last administrator is refused.
    public Task<HttpReturnResult> SetAdminAsync(string userId, bool isAdmin) =>
        RetryOnDeadlockAsync(() => SetAdminOnceAsync(userId, isAdmin));

    private async Task<HttpReturnResult> SetAdminOnceAsync(string userId, bool isAdmin)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await TakeAdminMembershipLockAsync(connection, transaction);

        bool? current = null;
        var effective = false;
        await using (var read = new SqlCommand("SELECT IsAdmin, SuspendedAt FROM Users WHERE Id = @UserId", connection, transaction))
        {
            read.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await read.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                current = reader.GetBoolean(0);
                effective = current == true && reader.IsDBNull(1);
            }
        }
        if (current == null)
            return HttpReturnResult.NotFound("User not found");

        // Only an administrator who counts (not suspended) can be the last one
        if (!isAdmin && effective)
        {
            await using var others = new SqlCommand(
                $"SELECT COUNT(*) FROM Users WHERE {EffectiveAdminPredicate} AND Id <> @UserId", connection, transaction);
            others.Parameters.AddWithValue("@UserId", userId);
            if (Convert.ToInt32(await others.ExecuteScalarAsync()) == 0)
                return HttpReturnResult.Conflict(LastAdminMessage); // rolled back (nothing written) when disposed
        }

        await using (var update = new SqlCommand(@"
            UPDATE Users SET IsAdmin = @IsAdmin,
                IsPermanent = CASE WHEN @IsAdmin = 1 THEN 1 ELSE IsPermanent END,
                LastActiveAt = CASE WHEN @IsAdmin = 0 AND IsAdmin = 1 THEN SYSUTCDATETIME() ELSE LastActiveAt END,
                InactivityWarnedAt = CASE WHEN @IsAdmin = 0 AND IsAdmin = 1 THEN NULL ELSE InactivityWarnedAt END
            WHERE Id = @UserId", connection, transaction))
        {
            update.Parameters.AddWithValue("@IsAdmin", isAdmin);
            update.Parameters.AddWithValue("@UserId", userId);
            await update.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new HttpReturnResult(true, isAdmin ? "Administrator rights granted" : "Administrator rights removed");
    }



    // Every account with its storage use and file count, oldest first. Reads every user's files,
    // so it can be picked as a deadlock victim by someone's delete; it only reads, so it's retried.
    // In hosted mode each row also says when the account will be removed for inactivity.
    public Task<List<AdminUser>> GetUsersForAdminAsync(long defaultQuota, HostedOptions? hosted = null) =>
        RetryOnDeadlockAsync(() => ReadUsersForAdminAsync(defaultQuota, hosted));

    private async Task<List<AdminUser>> ReadUsersForAdminAsync(long defaultQuota, HostedOptions? hosted)
    {
        var users = new List<AdminUser>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
            SELECT u.Id, u.FirstName, u.LastName, u.Email, u.CreatedAt, u.LastLogin, u.StorageQuota, u.IsAdmin,
                   COALESCE(SUM(CASE WHEN f.IsDirectory = 0 THEN f.Size END), 0) AS BytesUsed,
                   COUNT(CASE WHEN f.IsDirectory = 0 THEN 1 END) AS FileCount,
                   u.IsPermanent, u.AvatarUpdatedAt, u.SuspendedAt, u.LastLoginIp,
                   COALESCE(u.LastActiveAt, u.LastLogin, u.CreatedAt), u.InactivityWarnedAt
            FROM Users u
            LEFT JOIN Files f ON f.UserId = u.Id
            GROUP BY u.Id, u.FirstName, u.LastName, u.Email, u.CreatedAt, u.LastLogin, u.StorageQuota, u.IsAdmin,
                     u.IsPermanent, u.AvatarUpdatedAt, u.SuspendedAt, u.LastLoginIp, u.LastActiveAt, u.InactivityWarnedAt, u.CreatedAt
            ORDER BY u.CreatedAt, u.Email";

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var quotaOverride = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            var lastActive = reader.IsDBNull(14) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc);
            var warnedAt = reader.IsDBNull(15) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc);
            var isAdmin = reader.GetBoolean(7);
            var isPermanent = reader.GetBoolean(10);
            var suspendedAt = reader.IsDBNull(12) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc);
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
                isAdmin,
                isPermanent,
                reader.IsDBNull(11) ? null : DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc),
                suspendedAt,
                reader.IsDBNull(13) ? null : reader.GetString(13),
                LastActiveAt: lastActive,
                RemovalDueAt: hosted is { IsHosted: true } && lastActive is { } active
                    ? RemovalDueAt(hosted, isAdmin, isPermanent, suspendedAt != null, active, warnedAt)
                    : null));
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



    // Totals across all users: (users, administrators who aren't suspended, files, folders, bytes stored, suspended accounts). Retried on a
    // deadlock, like GetUsersForAdminAsync.
    public Task<(int users, int admins, int files, int folders, long bytes, int suspended)> GetAdminTotalsAsync() =>
        RetryOnDeadlockAsync(ReadAdminTotalsAsync);

    private async Task<(int users, int admins, int files, int folders, long bytes, int suspended)> ReadAdminTotalsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = $@"
            SELECT
                (SELECT COUNT(*) FROM Users),
                (SELECT COUNT(*) FROM Users WHERE {EffectiveAdminPredicate}),
                (SELECT COUNT(*) FROM Files WHERE IsDirectory = 0),
                (SELECT COUNT(*) FROM Files WHERE IsDirectory = 1),
                (SELECT COALESCE(SUM(Size), 0) FROM Files WHERE IsDirectory = 0),
                (SELECT COUNT(*) FROM Users WHERE SuspendedAt IS NOT NULL)";

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt32(5));
    }
}
