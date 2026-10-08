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

    // One of the user's links, as listed in GET /shares
    public record ShareSummary(
        Guid Id,
        string ItemId,
        string Name,
        bool IsDirectory,
        DateTime CreatedAt,
        DateTime? ExpiresAt,
        bool HasPassword,
        int DownloadCount);

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
