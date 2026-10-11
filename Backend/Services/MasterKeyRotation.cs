using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;

namespace Backend.Services;

public enum RotationStatus
{
    Rotated,
    DryRunOk,
    AlreadyRotated,
    // Nothing was changed because a precondition failed (wrong old key, API running, unreadable rows...)
    Refused,
    // Something went wrong part-way; the transaction was rolled back, nothing was changed
    Failed,
}

public sealed record RotationResult(RotationStatus Status, string Message);

public sealed record RotationOptions(bool DryRun = false, bool SkipUnreadable = false, bool ResetRecoveryCodes = false)
{
    // Called with "Table.Column" after each column's rows have been rewritten, before anything is
    // committed. For tests, to fail or kill the connection part-way through.
    public Func<string, Task>? AfterTable { get; init; }
}

// Re-wraps everything that is stored under ENCRYPTION_MASTER_KEY with a new key, in one SQL
// transaction, without touching file contents (they are encrypted with per-file data keys; only
// the wrapped data keys change). Either every value is rewritten and verified and the new key's
// fingerprint is stored, or nothing changes.
//
// The 8 stored values and how each is handled:
//   Files.WrappedKey, FileThumbnails.WrappedKey, Users.AvatarWrappedKey, Uploads.WrappedKey
//                              data keys wrapped by FileEncryption: unwrapped with the old key, wrapped with the new
//   Users.TotpSecret           SecretProtector, account secrets
//   Shares.TokenCipher, Shares.PasswordCipher
//                              SecretProtector, share link secrets
//   TotpRecoveryCodes.CodeHash keyed hashes of codes nobody stored; they can't be re-hashed, so the hashing
//                              key is carried over (protected under the new key in MasterKeyState), or with
//                              ResetRecoveryCodes the codes are deleted
// Each value is decrypted with the same context (AAD) the application uses, so a value moved to another
// row can't be rotated into place; one that does not decrypt is "unreadable".
public static class MasterKeyRotation
{
    private enum Kind { DataKey, AccountSecret, ShareSecret }

    // KeyExpr identifies the row in SQL and (as text) in the temp table; Context builds the AAD from it.
    private sealed record Spec(string Table, string Column, string KeyExpr, Kind Kind, Func<string, string> Context)
    {
        public string Name => $"{Table}.{Column}";
    }

    private const string GuidText = "LOWER(CONVERT(NVARCHAR(36), t.Id))";

    private static readonly Spec[] Specs =
    [
        new("Files", "WrappedKey", "t.GUID", Kind.DataKey, key => key),
        new("FileThumbnails", "WrappedKey", "t.FileGuid", Kind.DataKey, key => $"thumbnail:{key}"),
        new("Users", "AvatarWrappedKey", GuidText, Kind.DataKey, key => $"avatar:{key}"),
        new("Uploads", "WrappedKey", GuidText, Kind.DataKey, key => $"upload:{key}"),
        new("Users", "TotpSecret", GuidText, Kind.AccountSecret, key => $"totp:{key}"),
        new("Shares", "TokenCipher", GuidText, Kind.ShareSecret, key => $"share:{key}:token"),
        new("Shares", "PasswordCipher", GuidText, Kind.ShareSecret, key => $"share:{key}:password"),
    ];

    private sealed record Rewrite(Spec Spec, string RowKey, string OldValue, string NewValue);

    private sealed class Keys(byte[] key)
    {
        public readonly FileEncryption Files = new(key);
        public readonly SecretProtector Secrets = new(key);

        public byte[] Open(Spec spec, string rowKey, string value) => spec.Kind switch
        {
            Kind.DataKey => Files.UnwrapKey(value, spec.Context(rowKey)),
            Kind.AccountSecret => Secrets.Unprotect(value, spec.Context(rowKey), SecretProtector.Purpose.AccountSecrets),
            _ => Secrets.Unprotect(value, spec.Context(rowKey), SecretProtector.Purpose.ShareLinks),
        };

        public string Seal(Spec spec, string rowKey, byte[] plaintext) => spec.Kind switch
        {
            Kind.DataKey => Files.WrapKey(plaintext, spec.Context(rowKey)),
            Kind.AccountSecret => Secrets.Protect(plaintext, spec.Context(rowKey), SecretProtector.Purpose.AccountSecrets),
            _ => Secrets.Protect(plaintext, spec.Context(rowKey), SecretProtector.Purpose.ShareLinks),
        };
    }

    public const string ApplicationName = "FileVault rotate-master-key";

    // Progress and counts go to `log`; never key material, stored values or secrets
    public static async Task<RotationResult> RunAsync(
        string connectionString, byte[] oldKey, byte[] newKey, RotationOptions options, Action<string> log, CancellationToken ct = default)
    {
        var oldFingerprint = MasterKeyGuard.Fingerprint(oldKey);
        var newFingerprint = MasterKeyGuard.Fingerprint(newKey);
        if (oldFingerprint == newFingerprint)
            return new(RotationStatus.Refused, "The new key is the same as the current key.");

        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = ApplicationName };
        await using var connection = new SqlConnection(builder.ConnectionString);
        SqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(ct);

            if (!await TryTakeExclusiveLockAsync(connection, ct))
                return new(RotationStatus.Refused,
                    "The API is still running against this database (or another rotation is). Stop it first, e.g. docker compose stop api.");

            transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            var result = await RotateAsync(connection, transaction, oldKey, newKey, oldFingerprint, newFingerprint, options, log, ct);

            if (result.Status == RotationStatus.Rotated)
            {
                await transaction.CommitAsync(ct);
                transaction = null;
            }
            return result;
        }
        catch (SqlException ex)
        {
            return new(RotationStatus.Failed, ex.Number == 208
                ? "The MasterKeyState table is missing. Apply Backend/Docker/db/init.sql to the database first. Nothing was changed."
                : $"Database error {ex.Number} ({ex.GetType().Name}); the transaction was rolled back, nothing was changed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(RotationStatus.Failed, $"{ex.GetType().Name}: {ex.Message} The transaction was rolled back, nothing was changed.");
        }
        finally
        {
            if (transaction != null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception) { /* the connection is gone; SQL Server rolls back when the session ends */ }
                await transaction.DisposeAsync();
            }
        }
    }

    private static async Task<bool> TryTakeExclusiveLockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(@"
            DECLARE @Result INT;
            EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
            SELECT @Result;", connection);
        command.Parameters.AddWithValue("@Resource", MasterKeyGuard.LockResource);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) >= 0;
    }

    private static async Task<RotationResult> RotateAsync(
        SqlConnection connection, SqlTransaction tx, byte[] oldKey, byte[] newKey, string oldFingerprint, string newFingerprint,
        RotationOptions options, Action<string> log, CancellationToken ct)
    {
        // 1. Which key is the database under?
        string? carriedRecoveryKey;
        bool hasState;
        await using (var command = new SqlCommand(
            "SELECT KeyFingerprint, RecoveryHashKey FROM MasterKeyState WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1", connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            hasState = await reader.ReadAsync(ct);
            var stored = hasState ? reader.GetString(0).Trim() : null;
            carriedRecoveryKey = hasState && !reader.IsDBNull(1) ? reader.GetString(1) : null;

            if (hasState && stored == newFingerprint)
                return new(RotationStatus.AlreadyRotated, $"The database is already encrypted with the new key ({MasterKeyGuard.KeyId(newFingerprint)}). Nothing to do.");
            if (hasState && stored != oldFingerprint)
                return new(RotationStatus.Refused,
                    $"The current key (ENCRYPTION_MASTER_KEY, id {MasterKeyGuard.KeyId(oldFingerprint)}) is not the key this database is encrypted with. Nothing was changed.");
        }

        var oldKeys = new Keys(oldKey);
        var newKeys = new Keys(newKey);

        // 2. Read every value, decrypt with the old key and the row's context, encrypt with the new key,
        //    and check the new value decrypts to exactly the same bytes before it is queued
        var rewrites = new List<Rewrite>();
        var unreadable = new List<string>();
        foreach (var spec in Specs)
        {
            await using var command = new SqlCommand(
                $"SELECT {spec.KeyExpr}, t.{spec.Column} FROM {spec.Table} t WITH (TABLOCKX, HOLDLOCK) WHERE t.{spec.Column} IS NOT NULL", connection, tx)
            { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rowKey = reader.GetString(0);
                var value = reader.GetString(1);
                byte[]? plaintext = null;
                try
                {
                    plaintext = oldKeys.Open(spec, rowKey, value);
                    var sealedValue = newKeys.Seal(spec, rowKey, plaintext);
                    var check = newKeys.Open(spec, rowKey, sealedValue);
                    if (!CryptographicOperations.FixedTimeEquals(plaintext, check))
                        throw new CryptographicException("Re-encrypted value does not match.");
                    CryptographicOperations.ZeroMemory(check);
                    rewrites.Add(new(spec, rowKey, value, sealedValue));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    unreadable.Add($"{spec.Name} {rowKey}");
                }
                finally
                {
                    if (plaintext != null)
                        CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        log($"Found {rewrites.Count + unreadable.Count} protected values; {rewrites.Count} readable with the current key.");
        if (!hasState && rewrites.Count == 0 && unreadable.Count > 0)
            return new(RotationStatus.Refused,
                $"None of the {unreadable.Count} protected values opens with the current key (ENCRYPTION_MASTER_KEY, id {MasterKeyGuard.KeyId(oldFingerprint)}): it is the wrong key. Nothing was changed.");

        if (unreadable.Count > 0)
        {
            log($"{unreadable.Count} value(s) do not decrypt with the current key (already unusable; row IDs):");
            foreach (var item in unreadable.Take(100))
                log("  " + item);
            if (unreadable.Count > 100)
                log($"  ... and {unreadable.Count - 100} more");
            if (!options.SkipUnreadable)
                return new(RotationStatus.Refused,
                    "Rotation refused because of the unreadable values above. Nothing was changed. Run again with --skip-unreadable to rotate the rest and leave them as they are.");
            log("--skip-unreadable: leaving them as they are.");
        }

        // 3. Recovery codes
        string? newCarriedKey = null;
        int recoveryCodes;
        await using (var command = new SqlCommand("SELECT COUNT(*) FROM TotpRecoveryCodes WITH (TABLOCKX, HOLDLOCK)", connection, tx))
            recoveryCodes = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

        byte[]? hashKey = null;
        if (recoveryCodes > 0 && !options.ResetRecoveryCodes)
        {
            try
            {
                hashKey = carriedRecoveryKey != null
                    ? oldKeys.Secrets.Unprotect(carriedRecoveryKey, MasterKeyGuard.RecoveryKeyContext)
                    : SecretProtector.DeriveRecoveryHashKey(oldKey);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return new(RotationStatus.Refused, "The recovery-code hashing key stored in the database does not open with the current key. Nothing was changed.");
            }
            newCarriedKey = newKeys.Secrets.Protect(hashKey, MasterKeyGuard.RecoveryKeyContext, SecretProtector.Purpose.AccountSecrets);
            if (!CryptographicOperations.FixedTimeEquals(hashKey, newKeys.Secrets.Unprotect(newCarriedKey, MasterKeyGuard.RecoveryKeyContext)))
                throw new CryptographicException("Re-encrypted recovery key does not match.");
            CryptographicOperations.ZeroMemory(hashKey);
        }

        // 4. Write
        await using (var command = new SqlCommand(@"
            CREATE TABLE #Rewrap (
                Tbl NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL,
                Col NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL,
                RowKey NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL,
                OldValue NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL,
                NewValue NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL)", connection, tx))
            await command.ExecuteNonQueryAsync(ct);

        var table = new DataTable();
        foreach (var column in new[] { "Tbl", "Col", "RowKey", "OldValue", "NewValue" })
            table.Columns.Add(column, typeof(string));
        foreach (var rewrite in rewrites)
            table.Rows.Add(rewrite.Spec.Table, rewrite.Spec.Column, rewrite.RowKey, rewrite.OldValue, rewrite.NewValue);
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "#Rewrap", BulkCopyTimeout = 0 })
        {
            foreach (DataColumn column in table.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }

        foreach (var spec in Specs)
        {
            var queued = rewrites.Count(r => r.Spec == spec);
            int updated;
            await using (var command = new SqlCommand($@"
                UPDATE t SET t.{spec.Column} = r.NewValue
                FROM {spec.Table} t
                JOIN #Rewrap r ON r.Tbl = @Table AND r.Col = @Column AND r.RowKey = {spec.KeyExpr} COLLATE DATABASE_DEFAULT
                                  AND r.OldValue = t.{spec.Column} COLLATE DATABASE_DEFAULT", connection, tx) { CommandTimeout = 0 })
            {
                command.Parameters.AddWithValue("@Table", spec.Table);
                command.Parameters.AddWithValue("@Column", spec.Column);
                updated = await command.ExecuteNonQueryAsync(ct);
            }
            if (updated != queued)
                throw new InvalidOperationException($"{spec.Name}: expected to rewrite {queued} rows but {updated} changed.");

            log($"  {spec.Name}: {queued} re-wrapped");
            if (options.AfterTable != null)
                await options.AfterTable(spec.Name);
        }

        if (options.ResetRecoveryCodes)
        {
            await using var command = new SqlCommand("DELETE FROM TotpRecoveryCodes", connection, tx);
            var deleted = await command.ExecuteNonQueryAsync(ct);
            log($"  TotpRecoveryCodes: {deleted} deleted (--reset-recovery-codes); users with two-factor sign-in need new codes");
        }
        else
            log($"  TotpRecoveryCodes: {recoveryCodes} kept ({(newCarriedKey != null ? "hashing key carried over to the new key" : "none to carry")})");

        await using (var command = new SqlCommand(@"
            IF EXISTS (SELECT 1 FROM MasterKeyState WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1)
                UPDATE MasterKeyState SET KeyFingerprint = @Fingerprint, RecoveryHashKey = @RecoveryHashKey, RotatedAt = SYSUTCDATETIME() WHERE Id = 1
            ELSE
                INSERT INTO MasterKeyState (Id, KeyFingerprint, RecoveryHashKey, RotatedAt) VALUES (1, @Fingerprint, @RecoveryHashKey, SYSUTCDATETIME())", connection, tx))
        {
            command.Parameters.AddWithValue("@Fingerprint", newFingerprint);
            command.Parameters.AddWithValue("@RecoveryHashKey", (object?)newCarriedKey ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        // 5. Verify inside the transaction: everything that was rewritten opens with the new key alone
        var skipped = unreadable.ToHashSet();
        var verified = 0;
        foreach (var spec in Specs)
        {
            await using var command = new SqlCommand(
                $"SELECT {spec.KeyExpr}, t.{spec.Column} FROM {spec.Table} t WHERE t.{spec.Column} IS NOT NULL", connection, tx)
            { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rowKey = reader.GetString(0);
                if (skipped.Contains($"{spec.Name} {rowKey}"))
                    continue;
                CryptographicOperations.ZeroMemory(newKeys.Open(spec, rowKey, reader.GetString(1)));
                verified++;
            }
        }
        if (verified != rewrites.Count)
            throw new InvalidOperationException($"Verification read {verified} values, expected {rewrites.Count}.");
        log($"Verified {verified} re-wrapped values with the new key.");

        var ids = $"current key id {MasterKeyGuard.KeyId(oldFingerprint)}, new key id {MasterKeyGuard.KeyId(newFingerprint)}";
        if (options.DryRun)
        {
            await tx.RollbackAsync(ct);
            return new(RotationStatus.DryRunOk, $"Dry run: everything checks out and nothing was changed ({ids}).");
        }

        return new(RotationStatus.Rotated, $"Rotated ({ids}).");
    }
}
