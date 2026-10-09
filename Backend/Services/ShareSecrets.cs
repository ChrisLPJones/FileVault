using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Encrypts a share link's token and password so the owner can see them again later.
//
// AES-256-GCM with a key derived from Encryption:MasterKey by HKDF-SHA256 (its own info string, so
// it is never the same key as the file-key wrapping). Each value is bound to its share's Id and to
// what it is ("token" or "password") as associated data, so a value can't be copied to another
// link or swapped for the other one. Stored as base64(nonce | ciphertext | tag).
//
// Public access never decrypts anything: links are found by their SHA-256 hash and passwords are
// checked against their BCrypt hash. Decryption is only for showing the owner their own links.
public sealed class ShareSecrets
{
    public const string TokenPurpose = "token";
    public const string PasswordPurpose = "password";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] KeyInfo = "FileVault share link secrets v1"u8.ToArray();

    private readonly byte[] _key;

    public ShareSecrets(IConfiguration config)
    {
        var masterKey = FileEncryption.ParseMasterKey(config["Encryption:MasterKey"])
            ?? throw new InvalidOperationException("Encryption:MasterKey must be a base64-encoded 32-byte key.");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null, info: KeyInfo);
        CryptographicOperations.ZeroMemory(masterKey);
    }

    private static byte[] AssociatedData(Guid shareId, string purpose) => Encoding.UTF8.GetBytes($"share:{shareId:D}:{purpose}");

    public string Protect(string plaintext, Guid shareId, string purpose)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(output.AsSpan(0, NonceSize), plain, output.AsSpan(NonceSize, plain.Length),
            output.AsSpan(NonceSize + plain.Length, TagSize), AssociatedData(shareId, purpose));
        CryptographicOperations.ZeroMemory(plain);

        return Convert.ToBase64String(output);
    }

    // The plaintext, or null if there is no value or it doesn't decrypt (tampered, wrong share, other key)
    public string? Unprotect(string? protectedValue, Guid shareId, string purpose)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;

        try
        {
            var data = Convert.FromBase64String(protectedValue);
            if (data.Length < NonceSize + TagSize)
                return null;

            var length = data.Length - NonceSize - TagSize;
            var plain = new byte[length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, length), data.AsSpan(NonceSize + length, TagSize),
                plain, AssociatedData(shareId, purpose));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
