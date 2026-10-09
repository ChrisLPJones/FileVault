using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Time-based one-time passwords (RFC 6238) as used by authenticator apps:
// HMAC-SHA1, 30-second steps, 6 digits. Small enough to implement here rather
// than take a dependency, and every part is covered by the RFC test vectors.
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20; // 160 bits, the size RFC 4226 recommends for HMAC-SHA1

    // How many steps either side of now are accepted, to allow for clock drift
    public const int AllowedDrift = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    // The code for one time step (RFC 4226 HOTP with the step as the counter)
    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        // Dynamic truncation
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                   | (hash[offset + 1] << 16)
                   | (hash[offset + 2] << 8)
                   | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6");
    }

    // The time step the code belongs to (within the allowed drift), or null if it doesn't match.
    // Every candidate is compared in constant time, and all of them are checked so the time
    // taken doesn't depend on which one matched.
    public static long? Match(byte[] secret, string? code, DateTimeOffset now)
    {
        var normalized = Normalize(code);
        if (normalized == null)
            return null;

        var given = Encoding.ASCII.GetBytes(normalized);
        var current = CurrentStep(now);
        long? matched = null;

        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(Code(secret, step))))
                matched ??= step;
        }

        return matched;
    }

    // Digits only (spaces and dashes allowed, as apps often show "123 456"), or null
    private static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var digits = new string(code.Where(c => c != ' ' && c != '-').ToArray());
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    // otpauth:// link that authenticator apps read from the QR code
    public static string OtpAuthUri(string base32Secret, string accountName, string issuer)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }



    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // RFC 4648 base32 without padding, the format authenticator apps expect for manual entry
    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;

        foreach (var b in data)
        {
            // Only the unread bits (fewer than 5) are kept, so this never overflows
            buffer = ((buffer & 0xff) << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);

        return output.ToString();
    }
}
