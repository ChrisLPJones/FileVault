using Backend.Models;
using Microsoft.Data.SqlClient;

namespace Backend.Services;

// What administrators do to other people's accounts: create, set a password, mark permanent
public partial class DatabaseServices
{
    private const int DuplicateKeyErrorNumber = 2627;
    private const int DuplicateIndexErrorNumber = 2601;

    // Create an account for an administrator. The email address counts as confirmed (the
    // administrator vouches for it), so the account can sign in straight away. Returns the new
    // user's ID, or null if the email is already used. Takes the admin-membership lock because it
    // can add an administrator.
    public Task<Guid?> CreateUserByAdminAsync(UserModel user, bool isAdmin, bool isPermanent, long? quotaBytes = null) =>
        RetryOnDeadlockAsync(() => CreateUserByAdminOnceAsync(user, isAdmin, isPermanent, quotaBytes));

    private async Task<Guid?> CreateUserByAdminOnceAsync(UserModel user, bool isAdmin, bool isPermanent, long? quotaBytes)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await TakeAdminMembershipLockAsync(connection, transaction);

        var email = user.Email.Trim().ToLowerInvariant();
        await using (var exists = new SqlCommand("SELECT COUNT(1) FROM Users WHERE Email = @Email", connection, transaction))
        {
            exists.Parameters.AddWithValue("@Email", email);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync()) > 0)
                return null;
        }

        try
        {
            await using var insert = new SqlCommand(@"
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, IsAdmin, IsPermanent, StorageQuota, EmailVerified)
                OUTPUT inserted.Id
                VALUES (@FirstName, @LastName, @Email, @PasswordHash, @IsAdmin, @IsPermanent, @Quota, 1)", connection, transaction);
            insert.Parameters.AddWithValue("@FirstName", user.FirstName.Trim());
            insert.Parameters.AddWithValue("@LastName", user.LastName.Trim());
            insert.Parameters.AddWithValue("@Email", email);
            insert.Parameters.AddWithValue("@PasswordHash", user.Password);
            insert.Parameters.AddWithValue("@IsAdmin", isAdmin);
            insert.Parameters.AddWithValue("@IsPermanent", isAdmin || isPermanent); // administrators are always permanent
            insert.Parameters.AddWithValue("@Quota", (object?)quotaBytes ?? DBNull.Value);
            var id = (Guid)(await insert.ExecuteScalarAsync())!;
            await transaction.CommitAsync();
            return id;
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyErrorNumber or DuplicateIndexErrorNumber)
        {
            return null;
        }
    }



    // Set a user's password and end every way they were signed in, in one transaction: all
    // refresh tokens (so every session), pending two-factor login challenges and unused
    // password-reset links, and TokensValidAfter, which makes the access tokens they hold stop
    // working now (see AccessTokenGate). Not found is 404; with refuseAdmin an administrator is
    // refused (400), decided by the same UPDATE that would write, so it can't race a promotion.
    public Task<HttpReturnResult> SetPasswordAndSignOutAsync(string userId, string passwordHash, bool refuseAdmin = false) =>
        RetryOnDeadlockAsync(() => SetPasswordAndSignOutOnceAsync(userId, passwordHash, refuseAdmin));

    private async Task<HttpReturnResult> SetPasswordAndSignOutOnceAsync(string userId, string passwordHash, bool refuseAdmin)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = new SqlCommand(@"
            UPDATE Users SET PasswordHash = @PasswordHash, TokensValidAfter = @Now
            WHERE Id = @UserId AND (@RefuseAdmin = 0 OR IsAdmin = 0);
            IF @@ROWCOUNT = 0
            BEGIN
                SELECT CASE WHEN EXISTS (SELECT 1 FROM Users WHERE Id = @UserId) THEN 2 ELSE 0 END;
                RETURN;
            END
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @UserId AND RevokedAt IS NULL;
            DELETE FROM LoginChallenges WHERE UserId = @UserId;
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND Purpose = @ResetPurpose AND UsedAt IS NULL;
            SELECT 1;", connection, transaction);
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@RefuseAdmin", refuseAdmin);
        command.Parameters.AddWithValue("@ResetPurpose", AccountEmailService.ResetPurpose);
        var outcome = Convert.ToInt32(await command.ExecuteScalarAsync());

        await transaction.CommitAsync();
        return outcome switch
        {
            1 => new HttpReturnResult(true),
            2 => new HttpReturnResult(false, AdminAccountsLockedMessage),
            _ => HttpReturnResult.NotFound("User not found")
        };
    }

    public const string AdminAccountsLockedMessage = "Administrator accounts can't be changed from the admin page";



    // Mark an account permanent (or not). Not found is 404. An administrator is refused (400):
    // administrators are always permanent, and the check is the same UPDATE that would write.
    public async Task<HttpReturnResult> SetPermanentAsync(string userId, bool isPermanent)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Losing permanent status restarts the inactivity clock so the account isn't removed straight away
        await using var command = new SqlCommand(@"
            UPDATE Users SET IsPermanent = @IsPermanent,
                LastActiveAt = CASE WHEN @IsPermanent = 0 AND IsPermanent = 1 THEN SYSUTCDATETIME() ELSE LastActiveAt END,
                InactivityWarnedAt = CASE WHEN @IsPermanent = 0 AND IsPermanent = 1 THEN NULL ELSE InactivityWarnedAt END
            WHERE Id = @UserId AND IsAdmin = 0;
            IF @@ROWCOUNT = 0
                SELECT CASE WHEN EXISTS (SELECT 1 FROM Users WHERE Id = @UserId) THEN 2 ELSE 0 END;
            ELSE
                SELECT 1;", connection);
        command.Parameters.AddWithValue("@IsPermanent", isPermanent);
        command.Parameters.AddWithValue("@UserId", userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) switch
        {
            1 => new HttpReturnResult(true),
            2 => new HttpReturnResult(false, isPermanent ? AdminAccountsLockedMessage : "Administrator accounts are always permanent"),
            _ => HttpReturnResult.NotFound("User not found")
        };
    }



    // Suspend or unsuspend an account. Suspending ends every way the user is signed in, in one
    // transaction: SuspendedAt, TokensValidAfter (their access tokens stop working now), all refresh
    // tokens, pending two-factor login challenges and unused password-reset links. Their files,
    // shares and quota are left alone. Suspending the last effective administrator is refused
    // (409). Takes the admin-membership lock because it can remove an administrator.
    // With refuseAdmin any administrator is refused (400), checked under the admin-membership lock.
    public Task<HttpReturnResult> SetSuspendedAsync(string userId, bool suspended, bool refuseAdmin = false) =>
        RetryOnDeadlockAsync(() => SetSuspendedOnceAsync(userId, suspended, refuseAdmin));

    private async Task<HttpReturnResult> SetSuspendedOnceAsync(string userId, bool suspended, bool refuseAdmin)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await TakeAdminMembershipLockAsync(connection, transaction);

        var found = false;
        var isAdmin = false;
        var effectiveAdmin = false;
        await using (var read = new SqlCommand("SELECT IsAdmin, SuspendedAt FROM Users WHERE Id = @UserId", connection, transaction))
        {
            read.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await read.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                found = true;
                isAdmin = reader.GetBoolean(0);
                effectiveAdmin = isAdmin && reader.IsDBNull(1);
            }
        }
        if (!found)
            return HttpReturnResult.NotFound("User not found");
        if (refuseAdmin && isAdmin)
            return new HttpReturnResult(false, AdminAccountsLockedMessage);

        if (suspended && effectiveAdmin)
        {
            await using var others = new SqlCommand(
                $"SELECT COUNT(*) FROM Users WHERE {EffectiveAdminPredicate} AND Id <> @UserId", connection, transaction);
            others.Parameters.AddWithValue("@UserId", userId);
            if (Convert.ToInt32(await others.ExecuteScalarAsync()) == 0)
                return HttpReturnResult.Conflict(LastAdminMessage); // rolled back (nothing written) when disposed
        }

        if (suspended)
        {
            await using var command = new SqlCommand(@"
                UPDATE Users SET SuspendedAt = COALESCE(SuspendedAt, SYSUTCDATETIME()), TokensValidAfter = @Now
                WHERE Id = @UserId;
                UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @UserId AND RevokedAt IS NULL;
                DELETE FROM LoginChallenges WHERE UserId = @UserId;
                UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
                WHERE UserId = @UserId AND Purpose = @ResetPurpose AND UsedAt IS NULL;", connection, transaction);
            command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@ResetPurpose", AccountEmailService.ResetPurpose);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await using var command = new SqlCommand(
                // Restart the inactivity clock so the account isn't warned or removed straight away
                "UPDATE Users SET SuspendedAt = NULL, LastActiveAt = SYSUTCDATETIME(), InactivityWarnedAt = NULL WHERE Id = @UserId",
                connection, transaction);
            command.Parameters.AddWithValue("@UserId", userId);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new HttpReturnResult(true, suspended ? "Account suspended" : "Account unsuspended");
    }



    // What the access-token check needs to know about a user: whether they still exist, the
    // moment before which their access tokens are refused (null = none refused), and whether the
    // account is suspended
    public record UserAuthState(bool Exists, DateTime? TokensValidAfter, bool Suspended = false);

    public async Task<UserAuthState> GetUserAuthStateAsync(string userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT TokensValidAfter, SuspendedAt FROM Users WHERE Id = @UserId", connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return new UserAuthState(false, null);
        return new UserAuthState(true, reader.IsDBNull(0) ? null : DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), !reader.IsDBNull(1));
    }
}
