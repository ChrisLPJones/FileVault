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
    public Task<Guid?> CreateUserByAdminAsync(UserModel user, bool isAdmin, bool isPermanent) =>
        RetryOnDeadlockAsync(() => CreateUserByAdminOnceAsync(user, isAdmin, isPermanent));

    private async Task<Guid?> CreateUserByAdminOnceAsync(UserModel user, bool isAdmin, bool isPermanent)
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
                INSERT INTO Users (FirstName, LastName, Email, PasswordHash, IsAdmin, IsPermanent, EmailVerified)
                OUTPUT inserted.Id
                VALUES (@FirstName, @LastName, @Email, @PasswordHash, @IsAdmin, @IsPermanent, 1)", connection, transaction);
            insert.Parameters.AddWithValue("@FirstName", user.FirstName.Trim());
            insert.Parameters.AddWithValue("@LastName", user.LastName.Trim());
            insert.Parameters.AddWithValue("@Email", email);
            insert.Parameters.AddWithValue("@PasswordHash", user.Password);
            insert.Parameters.AddWithValue("@IsAdmin", isAdmin);
            insert.Parameters.AddWithValue("@IsPermanent", isPermanent);
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
    // working now (see AccessTokenGate). Returns false if there's no such user.
    public Task<bool> SetPasswordAndSignOutAsync(string userId, string passwordHash) =>
        RetryOnDeadlockAsync(() => SetPasswordAndSignOutOnceAsync(userId, passwordHash));

    private async Task<bool> SetPasswordAndSignOutOnceAsync(string userId, string passwordHash)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = new SqlCommand(@"
            UPDATE Users SET PasswordHash = @PasswordHash, TokensValidAfter = @Now WHERE Id = @UserId;
            IF @@ROWCOUNT = 0
            BEGIN
                SELECT CAST(0 AS BIT);
                RETURN;
            END
            UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @UserId AND RevokedAt IS NULL;
            DELETE FROM LoginChallenges WHERE UserId = @UserId;
            UPDATE AccountTokens SET UsedAt = SYSUTCDATETIME()
            WHERE UserId = @UserId AND Purpose = @ResetPurpose AND UsedAt IS NULL;
            SELECT CAST(1 AS BIT);", connection, transaction);
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@ResetPurpose", AccountEmailService.ResetPurpose);
        var found = (bool)(await command.ExecuteScalarAsync())!;

        await transaction.CommitAsync();
        return found;
    }



    // Mark an account permanent (or not); false if there's no such user
    public async Task<bool> SetPermanentAsync(string userId, bool isPermanent)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("UPDATE Users SET IsPermanent = @IsPermanent WHERE Id = @UserId", connection);
        command.Parameters.AddWithValue("@IsPermanent", isPermanent);
        command.Parameters.AddWithValue("@UserId", userId);
        return await command.ExecuteNonQueryAsync() > 0;
    }



    // Suspend or unsuspend an account. Suspending ends every way the user is signed in, in one
    // transaction: SuspendedAt, TokensValidAfter (their access tokens stop working now), all refresh
    // tokens, pending two-factor login challenges and unused password-reset links. Their files,
    // shares and quota are left alone. Suspending the last effective administrator is refused
    // (409). Takes the admin-membership lock because it can remove an administrator.
    public Task<HttpReturnResult> SetSuspendedAsync(string userId, bool suspended) =>
        RetryOnDeadlockAsync(() => SetSuspendedOnceAsync(userId, suspended));

    private async Task<HttpReturnResult> SetSuspendedOnceAsync(string userId, bool suspended)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await TakeAdminMembershipLockAsync(connection, transaction);

        var found = false;
        var effectiveAdmin = false;
        await using (var read = new SqlCommand("SELECT IsAdmin, SuspendedAt FROM Users WHERE Id = @UserId", connection, transaction))
        {
            read.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await read.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                found = true;
                effectiveAdmin = reader.GetBoolean(0) && reader.IsDBNull(1);
            }
        }
        if (!found)
            return HttpReturnResult.NotFound("User not found");

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
            await using var command = new SqlCommand("UPDATE Users SET SuspendedAt = NULL WHERE Id = @UserId", connection, transaction);
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
