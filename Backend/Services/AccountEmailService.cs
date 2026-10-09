using Backend.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Backend.Services;

// Email verification and password reset links.
//
// Each link carries a random 32-byte token; only its SHA-256 hash is stored, it works once, and
// verification links last 24 hours, reset links 1 hour. Links point at the frontend
// (App:FrontendUrl, or the first Cors:AllowedOrigins entry), whose pages call the API.
public partial class AccountEmailService(IConfiguration config, EmailQueue queue)
{
    public const string VerifyPurpose = "verify-email";
    public const string ResetPurpose = "reset-password";
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenFormat();

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string FrontendUrl =>
        new[] { config["App:FrontendUrl"], config.GetSection("Cors:AllowedOrigins").Get<string[]>()?.FirstOrDefault() }
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))?.TrimEnd('/')
        ?? "http://localhost:5173";

    // Email the user a link to confirm their (current) address
    public async Task SendVerificationAsync(UserModel user, DatabaseServices db)
    {
        var token = NewToken();
        await db.StoreAccountTokenAsync(user.Id.ToString(), VerifyPurpose, HashToken(token), user.Email, DateTime.UtcNow + VerifyLifetime);

        queue.Enqueue(new EmailMessage(user.Email, "Confirm your FileVault email address",
            $"Hi {user.FirstName},\n\n" +
            "Please confirm this is your email address by opening this link:\n\n" +
            $"{FrontendUrl}/verify-email?token={token}\n\n" +
            "The link works once and expires in 24 hours. If you didn't create a FileVault account " +
            "or change your email address, you can ignore this email."));
    }

    // Email the user a link to choose a new password
    public async Task SendPasswordResetAsync(UserModel user, DatabaseServices db)
    {
        var token = NewToken();
        await db.StoreAccountTokenAsync(user.Id.ToString(), ResetPurpose, HashToken(token), user.Email, DateTime.UtcNow + ResetLifetime);

        queue.Enqueue(new EmailMessage(user.Email, "Reset your FileVault password",
            $"Hi {user.FirstName},\n\n" +
            "Someone (hopefully you) asked to reset the password for your FileVault account. " +
            "To choose a new password, open this link:\n\n" +
            $"{FrontendUrl}/reset-password?token={token}\n\n" +
            "The link works once and expires in 1 hour. If you didn't ask for this, you can ignore " +
            "this email; your password hasn't changed."));
    }

    // Tell a user their account will be removed for inactivity (hosted mode). The address is only
    // emailed if it was confirmed; the caller checks.
    public void SendInactivityWarning(string email, string firstName, DateTime removalDueAt, int inactiveDays, string? contactEmail)
    {
        var date = removalDueAt.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var contact = string.IsNullOrWhiteSpace(contactEmail) ? "" : $"To ask for a permanent account, email {contactEmail}.\n\n";
        queue.Enqueue(new EmailMessage(email, "Your FileVault account will be removed soon",
            $"Hi {firstName},\n\n" +
            "You haven't used your FileVault account for a while. FileVault is a free service with limited hosting, " +
            $"so accounts that aren't used for {inactiveDays} days are removed along with their files.\n\n" +
            $"Your account will be removed on or after {date} (UTC).\n\n" +
            "To keep it, sign in to FileVault before then.\n\n" +
            contact +
            $"{FrontendUrl}/login"));
    }

    // Use a token: the user (and the email it was sent to), or null if it's unknown, used or expired
    public static async Task<DatabaseServices.AccountTokenUse?> ConsumeAsync(string? token, string purpose, DatabaseServices db) =>
        token != null && TokenFormat().IsMatch(token)
            ? await db.ConsumeAccountTokenAsync(purpose, HashToken(token))
            : null;
}
