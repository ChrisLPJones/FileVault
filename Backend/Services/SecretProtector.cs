using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Encrypts small account secrets (authenticator keys) and hashes recovery codes,
// using keys derived from Encryption:MasterKey. Separate derived keys keep these
// uses apart from file encryption, and a copy of the database alone is no help in
// reading the secrets or guessing the codes.
public class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _hashKey;

    public SecretProtector(IConfiguration config)
    {
        var masterKey = FileEncryption.ParseMasterKey(config["Encryption:MasterKey"])
            ?? throw new InvalidOperationException("Encryption:MasterKey must be a base64-encoded 32-byte key.");

        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "FileVault account secrets v1"u8.ToArray());
        _hashKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "FileVault recovery codes v1"u8.ToArray());
    }

    // AES-256-GCM, bound to `context` (e.g. the user ID) so a value can't be moved to another row
    public string Protect(byte[] plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(context));

        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    // Throws CryptographicException if the value was tampered with or belongs to another context
    public byte[] Unprotect(string protectedValue, string context)
    {
        var bytes = Convert.FromBase64String(protectedValue);
        if (bytes.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid protected value.");

        var cipherLength = bytes.Length - NonceSize - TagSize;
        var plaintext = new byte[cipherLength];

        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(
            bytes.AsSpan(0, NonceSize),
            bytes.AsSpan(NonceSize, cipherLength),
            bytes.AsSpan(NonceSize + cipherLength, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes(context));

        return plaintext;
    }

    // Keyed hash (HMAC-SHA256, hex) for values that only ever need to be compared
    public string Hash(string value, string context) =>
        Convert.ToHexString(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes($"{context}:{value}")));
}
