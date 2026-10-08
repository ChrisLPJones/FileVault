namespace Backend.Models
{
    // POST /shares body
    public class CreateShareRequest
    {
        public string? ItemId { get; set; }

        // Omit for a link that never expires
        public DateTimeOffset? ExpiresAt { get; set; }

        // Omit (or leave empty) for a link that needs no password
        public string? Password { get; set; }
    }

    // POST /s/{token} and POST /s/{token}/download body (JSON or form)
    public class SharePasswordRequest
    {
        public string? Password { get; set; }
    }

    // One of the user's links, as listed in GET /shares. Token is null for links created before
    // links could be shown again (only their hash was kept). The password itself is only sent by
    // GET /shares/{id}/password; PasswordViewable says whether that can show it.
    public record ShareSummary(
        Guid Id,
        string ItemId,
        string Name,
        bool IsDirectory,
        DateTime CreatedAt,
        DateTime? ExpiresAt,
        bool HasPassword,
        int DownloadCount,
        bool ItemInBin, // the link doesn't work while its item is in the recycle bin
        string? Token,
        string? Path,
        bool PasswordViewable);

    // A Shares row for the owner's list, with the encrypted token and password
    public record ShareListRow(
        Guid Id,
        string ItemId,
        string Name,
        bool IsDirectory,
        DateTime CreatedAt,
        DateTime? ExpiresAt,
        bool HasPassword,
        int DownloadCount,
        bool ItemInBin,
        string? TokenCipher,
        string? PasswordCipher);

    // GET /shares/{id}/password response
    public record SharePassword(string Password);

    // POST /shares response. The token is only ever returned here.
    public record CreatedShare(
        Guid Id,
        string Token,
        string Path,
        string ItemId,
        string Name,
        bool IsDirectory,
        DateTime CreatedAt,
        DateTime? ExpiresAt,
        bool HasPassword);

    // A file or folder inside a shared folder (Path is relative to the shared folder)
    public record SharedEntry(string Path, long Size, bool IsDirectory);

    // GET /s/{token}: what an anonymous visitor sees. For a password-protected link only
    // PasswordRequired is set until the password is given (POST /s/{token}).
    public record PublicShareInfo(
        bool PasswordRequired,
        string? Name = null,
        long? Size = null,
        bool? IsDirectory = null,
        DateTime? ExpiresAt = null,
        List<SharedEntry>? Files = null);

    // A shared file or zipped folder ready to stream
    public record SharedDownload(Stream Stream, string FileName, string ContentType);

    // An active (not revoked, not expired) link found by its token
    public record ShareRecord(Guid Id, string ItemId, string UserId, DateTime? ExpiresAt, string? PasswordHash);
}
