using Backend.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Backend.Services;

// Public links to a file or folder.
//
// A link is "/s/<token>", where the token is 32 random bytes in URL-safe base64. Links are looked
// up by a SHA-256 hash of the token and passwords checked against a BCrypt hash, so the public
// endpoints never need anything decrypted. So the owner can copy a link or see its password again,
// both are also kept encrypted (SecretProtector); without the master key the database alone reveals
// neither. Anything wrong with a link (unknown, revoked, expired, or its item deleted or in the
// recycle bin) gives the same "not found" answer, so visitors can't tell which it was.
public partial class ShareService(FileServices fs, SecretProtector secrets)
{
    public const int MinPasswordLength = 6;
    public const int MaxListedEntries = 1000;

    // A link's token and password are encrypted with their own key, each bound to its link and to
    // which field it is, so a value can't be copied to another link or swapped for the other one
    private const string TokenField = "token";
    private const string PasswordField = "password";

    private static string SecretContext(Guid shareId, string field) => $"share:{shareId:D}:{field}";

    private string ProtectSecret(string value, Guid shareId, string field) =>
        secrets.Protect(value, SecretContext(shareId, field), SecretProtector.Purpose.ShareLinks);

    private string? UnprotectSecret(string? value, Guid shareId, string field) =>
        secrets.TryUnprotectString(value, SecretContext(shareId, field), SecretProtector.Purpose.ShareLinks);

    public static readonly HttpReturnResult LinkNotFound = HttpReturnResult.NotFound("This link doesn't exist or has expired");
    public static readonly HttpReturnResult PasswordRejected = new(false, "The password is missing or incorrect") { StatusCode = 401 };

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenFormat();

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');



    // Create a link to one of the user's items
    public async Task<ServiceResult<CreatedShare>> CreateAsync(CreateShareRequest? request, DatabaseServices db, string userId)
    {
        var item = request?.ItemId is string id && Guid.TryParse(id, out _) ? await db.GetItemAsync(id, userId) : null;
        if (item == null)
            return HttpReturnResult.NotFound("File not found.");

        DateTime? expiresAt = request!.ExpiresAt?.UtcDateTime;
        if (expiresAt <= DateTime.UtcNow)
            return new HttpReturnResult(false, "The expiry date must be in the future");

        string? passwordHash = null;
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (request.Password.Length < MinPasswordLength)
                return new HttpReturnResult(false, $"Link passwords must be at least {MinPasswordLength} characters");
            // BCrypt only uses the first 72 bytes
            if (Encoding.UTF8.GetByteCount(request.Password) > 72)
                return new HttpReturnResult(false, "Link passwords must be 72 bytes or fewer");
            passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        }

        var token = NewToken();
        var shareId = Guid.NewGuid();
        var createdAt = await db.CreateShareAsync(shareId, userId, item.Guid, HashToken(token), expiresAt, passwordHash,
            ProtectSecret(token, shareId, TokenField),
            passwordHash == null ? null : ProtectSecret(request.Password!, shareId, PasswordField));

        return ServiceResult<CreatedShare>.Success(new CreatedShare(
            shareId, token, $"/s/{token}", item.Guid, item.Name, item.IsDirectory, createdAt, expiresAt, passwordHash != null));
    }



    // The user's links, each with its token decrypted so the link can be copied again
    // (null for links created before tokens were kept encrypted)
    public async Task<List<ShareSummary>> ListAsync(DatabaseServices db, string userId) =>
        (await db.GetSharesAsync(userId)).Select(row =>
        {
            var token = UnprotectSecret(row.TokenCipher, row.Id, TokenField);
            return new ShareSummary(row.Id, row.ItemId, row.Name, row.IsDirectory, row.CreatedAt, row.ExpiresAt,
                row.HasPassword, row.DownloadCount, row.ItemInBin, token, token == null ? null : $"/s/{token}",
                row.HasPassword && row.PasswordCipher != null);
        }).ToList();

    // A link's password, for its owner only. Not found for other users' links, links without a
    // password, and links created before passwords were kept encrypted.
    public async Task<ServiceResult<SharePassword>> GetPasswordAsync(Guid shareId, DatabaseServices db, string userId)
    {
        var row = await db.GetShareAsync(shareId, userId);
        if (row == null)
            return HttpReturnResult.NotFound("Link not found");
        if (!row.HasPassword)
            return HttpReturnResult.NotFound("This link has no password");

        var password = UnprotectSecret(row.PasswordCipher, row.Id, PasswordField);
        return password == null
            ? HttpReturnResult.NotFound("This link was created before passwords could be shown again")
            : ServiceResult<SharePassword>.Success(new SharePassword(password));
    }



    public record ResolvedShare(ShareRecord Share, FileRecord Item);

    // The active link for a token and the item it points to
    public static async Task<ServiceResult<ResolvedShare>> ResolveAsync(string? token, DatabaseServices db)
    {
        if (token == null || !TokenFormat().IsMatch(token))
            return LinkNotFound;

        var share = await db.GetActiveShareAsync(HashToken(token));
        if (share == null)
            return LinkNotFound;

        // GetItemAsync only finds items that are still there (not deleted or in the recycle bin)
        var item = await db.GetItemAsync(share.ItemId, share.UserId);
        return item == null ? LinkNotFound : ServiceResult<ResolvedShare>.Success(new ResolvedShare(share, item));
    }

    // True if the link has no password or the given one matches
    public static bool CheckPassword(ShareRecord share, string? password) =>
        share.PasswordHash == null ||
        (!string.IsNullOrEmpty(password) && BCrypt.Net.BCrypt.Verify(password, share.PasswordHash));



    // Name, size and (for a folder) its contents
    public static async Task<PublicShareInfo> DescribeAsync(ResolvedShare resolved, DatabaseServices db)
    {
        var (share, item) = resolved;
        if (!item.IsDirectory)
            return new PublicShareInfo(share.PasswordHash != null, item.Name, item.Size, false, share.ExpiresAt);

        var tree = await db.GetTreeAsync(item.Guid, share.UserId);
        var entries = tree
            .Skip(1) // the shared folder itself
            .Select(t => new SharedEntry(t.Path[(item.Path.Length + 1)..], t.IsDirectory ? 0 : t.Size, t.IsDirectory))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PublicShareInfo(
            share.PasswordHash != null,
            item.Name,
            entries.Where(e => !e.IsDirectory).Sum(e => e.Size),
            true,
            share.ExpiresAt,
            entries.Take(MaxListedEntries).ToList());
    }



    // A shared file as-is, or a shared folder as a zip. Files are always sent as a download
    // (octet-stream), never as something the browser might display on the API's origin.
    public async Task<ServiceResult<SharedDownload>> OpenDownloadAsync(ResolvedShare resolved, DatabaseServices db)
    {
        var (share, item) = resolved;

        if (item.IsDirectory)
        {
            var zip = await fs.CreateZipAsync([item.Guid], db, share.UserId);
            return zip.Ok
                ? ServiceResult<SharedDownload>.Success(new SharedDownload(zip.Value.Stream, zip.Value.FileName, "application/zip"))
                : zip.Error;
        }

        var download = await fs.GetDownloadAsync(item.Guid, db, share.UserId);
        return download.Ok
            ? ServiceResult<SharedDownload>.Success(new SharedDownload(download.Value.Stream, item.Name, "application/octet-stream"))
            : download.Error;
    }
}
