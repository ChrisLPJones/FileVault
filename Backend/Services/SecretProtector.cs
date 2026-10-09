using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Encrypts small secrets kept in the database (authenticator keys, share links' tokens and
// passwords) and hashes values that only ever need comparing (recovery codes).
//
// Every key is derived from Encryption:MasterKey by HKDF-SHA256 with its own info string, so each
// use has its own key and none is ever the same as the file-key wrapping. Values are AES-256-GCM,
// stored as base64(nonce | ciphertext | tag) and bound to a context (e.g. the user ID, or a share
// link's ID and which field it is) so a value can't be moved to another row or field. A copy of
// the database alone is no help in reading the secrets or guessing the codes.
public sealed class SecretProtector
{
    // What a value is for; each has its own key
    public enum Purpose
    {
        AccountSecrets,
        ShareLinks,
    }

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<Purpose, byte[]> _encryptionKeys;
    private readonly byte[] _hashKey;

    public SecretProtector(IConfiguration config)
    {
        var masterKey = FileEncryption.ParseMasterKey(config["Encryption:MasterKey"])
            ?? throw new InvalidOperationException("Encryption:MasterKey must be a base64-encoded 32-byte key.");

        // The info strings are part of the stored data's format: changing one makes existing values unreadable
        byte[] Derive(string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.UTF8.GetBytes(info));
        _encryptionKeys = new()
        {
            [Purpose.AccountSecrets] = Derive("FileVault account secrets v1"),
            [Purpose.ShareLinks] = Derive("FileVault share link secrets v1"),
        };
        _hashKey = Derive("FileVault recovery codes v1");
        CryptographicOperations.ZeroMemory(masterKey);
    }

    // AES-256-GCM, bound to `context` so a value can't be moved to another row
    public string Protect(byte[] plaintext, string context, Purpose purpose = Purpose.AccountSecrets)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));

        using var aes = new AesGcm(_encryptionKeys[purpose], TagSize);
        aes.Encrypt(output.AsSpan(0, NonceSize), plaintext, output.AsSpan(NonceSize, plaintext.Length),
            output.AsSpan(NonceSize + plaintext.Length, TagSize), Encoding.UTF8.GetBytes(context));

        return Convert.ToBase64String(output);
    }

    public string Protect(string plaintext, string context, Purpose purpose)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Protect(bytes, context, purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // Throws CryptographicException if the value was tampered with or belongs to another context
    public byte[] Unprotect(string protectedValue, string context, Purpose purpose = Purpose.AccountSecrets)
    {
        var bytes = Convert.FromBase64String(protectedValue);
        if (bytes.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid protected value.");

        var cipherLength = bytes.Length - NonceSize - TagSize;
        var plaintext = new byte[cipherLength];

        using var aes = new AesGcm(_encryptionKeys[purpose], TagSize);
        aes.Decrypt(
            bytes.AsSpan(0, NonceSize),
            bytes.AsSpan(NonceSize, cipherLength),
            bytes.AsSpan(NonceSize + cipherLength, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes(context));

        return plaintext;
    }

    // The text, or null if there is no value or it doesn't decrypt (tampered, other context, other key)
    public string? TryUnprotectString(string? protectedValue, string context, Purpose purpose)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;

        try
        {
            return Encoding.UTF8.GetString(Unprotect(protectedValue, context, purpose));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    // Keyed hash (HMAC-SHA256, hex) for values that only ever need to be compared
    public string Hash(string value, string context) =>
        Convert.ToHexString(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes($"{context}:{value}")));
}
