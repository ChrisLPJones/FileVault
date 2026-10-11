using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Keeps the API from running against a database encrypted with a different master key, and keeps a
// master key rotation from running while the API is up.
//
// At start (before the API takes requests; registered ahead of the other hosted services) it:
//   1. takes a shared application lock, "fv-api-running", on a connection it keeps open for the life
//      of the app. The rotate-master-key command needs the same lock exclusively, so it refuses to
//      run while an API is up, and an API that starts during a rotation waits for it to finish;
//   2. compares the configured key's fingerprint with the one stored in MasterKeyState and refuses
//      to start on a mismatch. A database that has no row yet (first start after upgrading) gets one
//      if the configured key opens some of its data (or it has no encrypted data yet);
//   3. if a rotation carried the recovery-code hashing key over, hands it to SecretProtector.
// Every minute it checks again, and stops the app if the database has been moved to another key.
// The configured key is not the one the database is encrypted with (or a rotation never finished):
// the only failure that stops a running API
public sealed class MasterKeyMismatchException(string message) : InvalidOperationException(message);

public sealed class MasterKeyGuard(
    IConfiguration config,
    FileEncryption encryption,
    SecretProtector protector,
    IHostApplicationLifetime lifetime,
    ILogger<MasterKeyGuard> logger) : IHostedService, IDisposable
{
    public const string LockResource = "fv-api-running";
    public const string RecoveryKeyContext = "recovery-hash-key";

    private const int MaxAttempts = 3;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxLockWait = TimeSpan.FromMinutes(10);
    private const int SampleSize = 5;

    private readonly CancellationTokenSource _stop = new();
    private SqlConnection? _connection;
    private Task? _watcher;
    private string _fingerprint = "";
    private bool _carriedKeyApplied;

    // One-way fingerprint of a master key: HMAC-SHA256 under an HKDF-derived key, hex. Reveals nothing usable.
    public static string Fingerprint(byte[] masterKey)
    {
        var checkKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.UTF8.GetBytes("FileVault key check v1"));
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(checkKey, Encoding.UTF8.GetBytes("FileVault master key check"))).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(checkKey);
        }
    }

    // The short form shown to operators
    public static string KeyId(string fingerprint) => fingerprint[..8];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var masterKey = FileEncryption.ParseMasterKey(config["Encryption:MasterKey"])
            ?? throw new InvalidOperationException("Encryption:MasterKey must be a base64-encoded 32-byte key.");
        _fingerprint = Fingerprint(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);

        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ConnectAndCheckAsync(cancellationToken);
                break;
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                throw new InvalidOperationException(
                    "The MasterKeyState table is missing. Apply Backend/Docker/db/init.sql to the database (docker compose up -d runs it) and start again.", ex);
            }
            catch (SqlException ex) when (!IsUnreachable(ex))
            {
                // Login refused, permission denied, schema errors...: not "the database is down", so don't run unguarded
                throw new InvalidOperationException(
                    $"The master key check failed with a database error ({ex.Number}); the API did not start. Check the database login's permissions.", ex);
            }
            catch (SqlException ex)
            {
                // Error 0 is also what SqlClient reports for a TLS/certificate problem, so say what it was
                logger.LogWarning("Master key check: database not reachable (SQL error {Number}: {Message})", ex.Number, ex.Message);
                await CloseConnectionAsync();
                if (attempt >= MaxAttempts)
                {
                    // Like the other start-up checks the API still starts when the database is unreachable
                    // (nothing can be read or written then anyway); the check keeps trying and stops the app on a mismatch
                    logger.LogError(ex, "Could not check the master key against the database after {Attempts} attempts; " +
                        "trying again every {Seconds}s until it is reachable", MaxAttempts, (int)ReconnectInterval.TotalSeconds);
                    break;
                }
                logger.LogWarning(ex, "Could not check the master key against the database (attempt {Attempt} of {Max}); retrying in {Delay}s",
                    attempt, MaxAttempts, (int)delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
            }
        }

        _watcher = Task.Run(() => WatchAsync(_stop.Token));
    }

    // Errors that mean the server can't be reached (or the database isn't there yet), as opposed to
    // errors from a database that answered (0 is what SqlClient reports when the connection could not be established)
    private static readonly HashSet<int> UnreachableErrors = [0, -2, -1, 2, 20, 40, 53, 64, 233, 258, 1231, 4060, 10053, 10054, 10060, 10061, 11001];

    private static bool IsUnreachable(SqlException ex) => ex.Errors.Cast<SqlError>().Any(e => UnreachableErrors.Contains(e.Number));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The host may already have disposed this service (disposal and StopAsync can run in either order)
        try { await _stop.CancelAsync(); }
        catch (ObjectDisposedException) { }
        if (_watcher != null)
        {
            try { await _watcher.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (Exception) { /* stopping anyway */ }
        }
        await CloseConnectionAsync();
    }

    public void Dispose()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
        _stop.Dispose();
        _connection?.Dispose();
    }



    private string ConnectionString() => new SqlConnectionStringBuilder(
        config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set."))
    {
        // Not pooled: closing the connection must end the session so its application lock is released
        Pooling = false,
        ApplicationName = "FileVault API (master key guard)",
    }.ConnectionString;

    private async Task CloseConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection != null)
            await connection.DisposeAsync();
    }

    private async Task ConnectAndCheckAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString());
        try
        {
            await connection.OpenAsync(ct);
            await TakeSharedLockAsync(connection, ct);
            await CheckKeyAsync(connection, ct);
            await WarnIfPrivilegedLoginAsync(connection, ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await CloseConnectionAsync();
        _connection = connection;
    }

    // The Docker install connects as a login that can only read and write the data. A login that is sysadmin or
    // db_owner (sa, as in development) makes a SQL injection or code-execution bug far worse, so say so once.
    public static string? PrivilegedLoginWarning(bool sysadmin, bool dbOwner) =>
        sysadmin || dbOwner
            ? $"The database login is {(sysadmin ? "a sysadmin" : "db_owner")}. In production, connect as a login that can only read and write the data " +
              "(docker compose creates one from MSSQL_APP_PASSWORD; see docs/DEPLOYMENT.md, \"Database login\")."
            : null;

    private bool _privilegeChecked;

    private async Task WarnIfPrivilegedLoginAsync(SqlConnection connection, CancellationToken ct)
    {
        if (_privilegeChecked)
            return;
        try
        {
            await using var command = new SqlCommand("SELECT COALESCE(IS_SRVROLEMEMBER('sysadmin'), 0), COALESCE(IS_ROLEMEMBER('db_owner'), 0)", connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && PrivilegedLoginWarning(reader.GetInt32(0) == 1, reader.GetInt32(1) == 1) is { } warning)
                logger.LogWarning("{Warning}", warning);
            _privilegeChecked = true;
        }
        catch (SqlException)
        {
            // Only a hint: never stop the API for it
        }
    }

    private async Task TakeSharedLockAsync(SqlConnection connection, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var logged = false;
        while (true)
        {
            await using var command = new SqlCommand(@"
                DECLARE @Result INT;
                EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Shared', @LockOwner = 'Session', @LockTimeout = 5000;
                SELECT @Result;", connection) { CommandTimeout = 30 };
            command.Parameters.AddWithValue("@Resource", LockResource);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) >= 0)
                return;

            if (DateTime.UtcNow - started > MaxLockWait)
                throw new MasterKeyMismatchException("A master key rotation has been running for more than 10 minutes; the API did not start.");
            if (!logged)
            {
                logger.LogWarning("Waiting for a master key rotation to finish before starting");
                logged = true;
            }
        }
    }

    private async Task CheckKeyAsync(SqlConnection connection, CancellationToken ct)
    {
        var state = await ReadStateAsync(connection, ct);
        if (state == null)
        {
            if (!await SampleMatchesAsync(connection, ct))
                throw Mismatch();

            // Race-safe: another API starting at the same moment may insert it first; read it back and compare
            await using (var insert = new SqlCommand(@"
                INSERT INTO MasterKeyState (Id, KeyFingerprint)
                SELECT 1, @Fingerprint WHERE NOT EXISTS (SELECT 1 FROM MasterKeyState WITH (UPDLOCK, HOLDLOCK));", connection))
            {
                insert.Parameters.AddWithValue("@Fingerprint", _fingerprint);
                await insert.ExecuteNonQueryAsync(ct);
            }
            state = await ReadStateAsync(connection, ct) ?? throw new InvalidOperationException("The master key state row could not be created.");
        }

        if (!string.Equals(state.Value.Fingerprint, _fingerprint, StringComparison.Ordinal))
            throw Mismatch();

        if (!_carriedKeyApplied && state.Value.RecoveryHashKey != null)
        {
            var hashKey = protector.Unprotect(state.Value.RecoveryHashKey, RecoveryKeyContext);
            protector.UseRecoveryHashKey(hashKey);
            CryptographicOperations.ZeroMemory(hashKey);
        }
        _carriedKeyApplied = true;
    }

    private static MasterKeyMismatchException Mismatch() => new(
        "ENCRYPTION_MASTER_KEY does not match the key this database is encrypted with. " +
        "If you just rotated the master key, set the new key (see docs/DEPLOYMENT.md, \"Rotating the master key\").");

    private static async Task<(string Fingerprint, string? RecoveryHashKey)?> ReadStateAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT KeyFingerprint, RecoveryHashKey FROM MasterKeyState WHERE Id = 1", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetString(0).Trim(), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    // With no state row: true when the database has nothing encrypted yet, or the configured key opens
    // at least one of a sample of its values. False (refuse to start) when it has values and none open.
    private async Task<bool> SampleMatchesAsync(SqlConnection connection, CancellationToken ct)
    {
        var queries = new (string Sql, Func<string, string, bool> Opens)[]
        {
            ($"SELECT TOP ({SampleSize}) GUID, WrappedKey FROM Files WHERE WrappedKey IS NOT NULL",
                (id, value) => Try(() => encryption.UnwrapKey(value, id))),
            ($"SELECT TOP ({SampleSize}) CONVERT(NVARCHAR(36), Id), AvatarWrappedKey FROM Users WHERE AvatarWrappedKey IS NOT NULL",
                (id, value) => Try(() => encryption.UnwrapKey(value, $"avatar:{Guid.Parse(id).ToString()}"))),
            ($"SELECT TOP ({SampleSize}) CONVERT(NVARCHAR(36), Id), TotpSecret FROM Users WHERE TotpSecret IS NOT NULL",
                (id, value) => Try(() => protector.Unprotect(value, $"totp:{Guid.Parse(id).ToString()}"))),
            ($"SELECT TOP ({SampleSize}) CONVERT(NVARCHAR(36), Id), TokenCipher FROM Shares WHERE TokenCipher IS NOT NULL",
                (id, value) => Try(() => protector.Unprotect(value, $"share:{Guid.Parse(id):D}:token", SecretProtector.Purpose.ShareLinks))),
        };

        var found = 0;
        foreach (var (sql, opens) in queries)
        {
            var rows = new List<(string Id, string Value)>();
            await using (var command = new SqlCommand(sql, connection))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetString(0), reader.GetString(1)));

            foreach (var (id, value) in rows)
            {
                found++;
                if (opens(id, value))
                    return true;
            }
        }
        return found == 0;
    }

    private static bool Try(Action decrypt)
    {
        try
        {
            decrypt();
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }



    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_connection == null ? ReconnectInterval : CheckInterval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                await RecheckAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (MasterKeyMismatchException ex)
            {
                logger.LogCritical("{Message} Stopping the API.", ex.Message);
                lifetime.StopApplication();
                return;
            }
            catch (Exception ex)
            {
                // Database trouble: the connection is replaced on the next round
                logger.LogWarning(ex, "Could not re-check the master key; will try again");
            }
        }
    }

    private async Task RecheckAsync(CancellationToken ct)
    {
        var connection = _connection;
        if (connection is { State: ConnectionState.Open })
        {
            try
            {
                var state = await ReadStateAsync(connection, ct);
                if (state != null && !string.Equals(state.Value.Fingerprint, _fingerprint, StringComparison.Ordinal))
                    throw Mismatch();
                return;
            }
            catch (SqlException)
            {
                // The connection (and with it the lock) is gone: take a new one below
            }
        }

        await ConnectAndCheckAsync(ct);
    }
}
