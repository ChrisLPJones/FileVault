using System.Security.Cryptography;

namespace Backend.Services;

// dotnet Backend.dll rotate-master-key [--dry-run] [--skip-unreadable] [--reset-recovery-codes]
//
// The current key is the normal Encryption:MasterKey (ENCRYPTION_MASTER_KEY); the new one is
// Encryption:NewMasterKey (ENCRYPTION_MASTER_KEY_NEW). Keys are only ever read from configuration,
// never from arguments, and never printed. An unknown argument gets a usage error that does not repeat it
// (it might be a pasted key).
//
// Exit codes: 0 rotated / dry run passed / already rotated; 1 refused or failed (nothing changed); 2 usage error.
public static class MasterKeyCommand
{
    public const string Usage = "Usage: rotate-master-key [--dry-run] [--skip-unreadable] [--reset-recovery-codes]\n" +
        "Keys are read from ENCRYPTION_MASTER_KEY (current) and ENCRYPTION_MASTER_KEY_NEW (new), never from arguments.";

    public static int Run(string[] args) =>
        RunAsync(args, WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }).Configuration, Console.Out)
            .GetAwaiter().GetResult();

    public static async Task<int> RunAsync(
        string[] args, IConfiguration config, TextWriter output, Func<string, Task>? afterTable = null, CancellationToken ct = default)
    {
        bool dryRun = false, skipUnreadable = false, resetRecoveryCodes = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--dry-run": dryRun = true; break;
                case "--skip-unreadable": skipUnreadable = true; break;
                case "--reset-recovery-codes": resetRecoveryCodes = true; break;
                default:
                    // Deliberately not echoing the argument
                    await output.WriteLineAsync("Unknown argument.");
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }

        var oldKey = FileEncryption.ParseMasterKey(config["Encryption:MasterKey"]);
        if (oldKey == null)
            return await Refuse(output, "Encryption:MasterKey (ENCRYPTION_MASTER_KEY) is missing or not a base64-encoded 32-byte key.");

        var newKeyText = config["Encryption:NewMasterKey"];
        byte[]? newKey;
        if (string.IsNullOrWhiteSpace(newKeyText))
        {
            if (!dryRun)
                return await Refuse(output, "Encryption:NewMasterKey (ENCRYPTION_MASTER_KEY_NEW) is not set. Generate one with: openssl rand -base64 32");
            // A dry run still checks the current key and every stored value
            newKey = RandomNumberGenerator.GetBytes(32);
            await output.WriteLineAsync("No new key is set; the dry run uses a throwaway key.");
        }
        else
        {
            newKey = FileEncryption.ParseMasterKey(newKeyText);
            if (newKey == null)
                return await Refuse(output, "Encryption:NewMasterKey (ENCRYPTION_MASTER_KEY_NEW) is not a base64-encoded 32-byte key.");
        }

        var connectionString = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return await Refuse(output, "ConnectionStrings:DefaultConnection is not set.");

        if (CryptographicOperations.FixedTimeEquals(oldKey, newKey))
            return await Refuse(output, "The new key is the same as the current key.");

        await output.WriteLineAsync(dryRun ? "Master key rotation: DRY RUN (nothing will be changed)" : "Master key rotation");
        await output.WriteLineAsync($"Current key id {MasterKeyGuard.KeyId(MasterKeyGuard.Fingerprint(oldKey))}, new key id {MasterKeyGuard.KeyId(MasterKeyGuard.Fingerprint(newKey))}");

        var options = new RotationOptions(dryRun, skipUnreadable, resetRecoveryCodes) { AfterTable = afterTable };
        RotationResult result;
        try
        {
            result = await MasterKeyRotation.RunAsync(connectionString, oldKey, newKey, options, line => output.WriteLine(line), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
            CryptographicOperations.ZeroMemory(newKey);
        }

        await output.WriteLineAsync(result.Message);
        switch (result.Status)
        {
            case RotationStatus.Rotated:
                await output.WriteLineAsync("Next: set ENCRYPTION_MASTER_KEY to the new key, remove ENCRYPTION_MASTER_KEY_NEW, then start the API.");
                await output.WriteLineAsync("Keep the old key only for backups made before this rotation.");
                break;
            case RotationStatus.AlreadyRotated:
                await output.WriteLineAsync("If the API will not start, ENCRYPTION_MASTER_KEY must be set to the new key.");
                break;
        }

        return result.Status is RotationStatus.Rotated or RotationStatus.DryRunOk or RotationStatus.AlreadyRotated ? 0 : 1;
    }

    private static async Task<int> Refuse(TextWriter output, string message)
    {
        await output.WriteLineAsync("Refused: " + message);
        await output.WriteLineAsync("Nothing was changed.");
        return 1;
    }
}
