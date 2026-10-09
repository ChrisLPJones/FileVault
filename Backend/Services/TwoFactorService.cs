using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Authenticator-app (TOTP) two-factor authentication: setup, recovery codes, and the
// second step of logging in.
public class TwoFactorService
{
    public const int RecoveryCodeCount = 10;

    private readonly DatabaseServices _db;
    private readonly SecretProtector _protector;
    private readonly IConfiguration _config;
    private readonly TimeProvider _clock;

    public TwoFactorService(DatabaseServices db, SecretProtector protector, IConfiguration config, TimeProvider clock)
    {
        _db = db;
        _protector = protector;
        _config = config;
        _clock = clock;
    }

    private TimeSpan ChallengeLifetime => TimeSpan.FromSeconds(_config.GetValue("TwoFactor:ChallengeSeconds", 300));
    public int MaxChallengeAttempts => _config.GetValue("TwoFactor:MaxAttempts", 5);
    private string Issuer => _config.GetValue("TwoFactor:Issuer", "FileVault") ?? "FileVault";

    // The secret is bound to the user so it can't be copied to another account's row
    private static string SecretContext(string userId) => $"totp:{userId}";



    public record SetupInfo(string Secret, string OtpAuthUri);

    // Create a new secret for the user to add to their app. Null if 2FA is already on
    // (it has to be turned off first, so a stolen session can't silently swap the secret).
    public async Task<SetupInfo?> StartSetupAsync(string userId, string email)
    {
        var secret = Totp.NewSecret();
        if (!await _db.SetPendingTotpSecretAsync(userId, _protector.Protect(secret, SecretContext(userId))))
            return null;

        var base32 = Totp.ToBase32(secret);
        return new SetupInfo(base32, Totp.OtpAuthUri(base32, email, Issuer));
    }

    // Confirm setup with a code from the app. Returns the recovery codes, or null if the
    // code is wrong or setup wasn't started.
    public async Task<List<string>?> EnableAsync(string userId, string? code)
    {
        var state = await _db.GetTwoFactorStateAsync(userId);
        if (state?.ProtectedSecret == null || state.Enabled)
            return null;

        var step = Totp.Match(_protector.Unprotect(state.ProtectedSecret, SecretContext(userId)), code, _clock.GetUtcNow());
        if (step == null)
            return null;

        var codes = NewRecoveryCodes();
        if (!await _db.EnableTotpAsync(userId, step.Value, codes.Select(c => HashRecoveryCode(userId, c))))
            return null;

        return codes;
    }

    // Check a code from the app, or a recovery code. Each is accepted only once.
    public async Task<bool> VerifyAsync(string userId, string? code, string? recoveryCode)
    {
        var state = await _db.GetTwoFactorStateAsync(userId);
        if (state?.ProtectedSecret == null || !state.Enabled)
            return false;

        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var normalized = NormalizeRecoveryCode(recoveryCode);
            return normalized != null && await _db.TryUseRecoveryCodeAsync(userId, HashRecoveryCode(userId, normalized));
        }

        var step = Totp.Match(_protector.Unprotect(state.ProtectedSecret, SecretContext(userId)), code, _clock.GetUtcNow());
        // Only a step later than the last accepted one counts, so a code can't be replayed
        return step != null && await _db.TryUseTotpStepAsync(userId, step.Value);
    }

    public async Task<List<string>> RegenerateRecoveryCodesAsync(string userId)
    {
        var codes = NewRecoveryCodes();
        await _db.ReplaceRecoveryCodesAsync(userId, codes.Select(c => HashRecoveryCode(userId, c)));
        return codes;
    }

    public Task DisableAsync(string userId) => _db.DisableTotpAsync(userId);



    // Start the second login step: a random, single-use token the client sends back with the code
    public async Task<string> CreateLoginChallengeAsync(string userId)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await _db.StoreLoginChallengeAsync(userId, HashToken(token), ChallengeLifetime);
        return token;
    }

    public enum ChallengeOutcome { Success, Expired, WrongCode }

    // Finish logging in. Expired covers unknown, used, timed-out and out-of-attempts challenges,
    // all of which mean "log in with your password again".
    public async Task<(ChallengeOutcome outcome, string? userId)> RedeemLoginChallengeAsync(
        string? challengeToken, string? code, string? recoveryCode)
    {
        if (string.IsNullOrWhiteSpace(challengeToken) || challengeToken.Length > 100)
            return (ChallengeOutcome.Expired, null);

        var tokenHash = HashToken(challengeToken);
        var userId = await _db.StartLoginChallengeAttemptAsync(tokenHash, MaxChallengeAttempts);
        if (userId == null)
            return (ChallengeOutcome.Expired, null);

        if (!await VerifyAsync(userId, code, recoveryCode))
            return (ChallengeOutcome.WrongCode, null);

        // Two requests racing with the same challenge: only the first one wins
        return await _db.CompleteLoginChallengeAsync(tokenHash)
            ? (ChallengeOutcome.Success, userId)
            : (ChallengeOutcome.Expired, null);
    }



    // Recovery codes look like "k7d2-mq4x-p9ta": 12 base32 characters, 60 random bits
    private static List<string> NewRecoveryCodes() =>
        Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ =>
            {
                var chars = Totp.ToBase32(RandomNumberGenerator.GetBytes(8))[..12].ToLowerInvariant();
                return $"{chars[..4]}-{chars[4..8]}-{chars[8..]}";
            })
            .ToList();

    // Case, spaces and dashes don't matter when typing a recovery code
    private static string? NormalizeRecoveryCode(string code)
    {
        var chars = new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return chars.Length == 12 && chars.All(char.IsAsciiLetterOrDigit)
            ? $"{chars[..4]}-{chars[4..8]}-{chars[8..]}"
            : null;
    }

    private string HashRecoveryCode(string userId, string code) => _protector.Hash(code, $"recovery:{userId}");

    // Challenge tokens are 256-bit random values, so a plain SHA-256 is enough
    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
