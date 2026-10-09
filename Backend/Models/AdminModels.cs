namespace Backend.Models
{
    // GET /admin/me
    public record AdminStatusResponse(bool IsAdmin);

    // A row in GET /admin/users. Dates are UTC; Quota is the effective limit and QuotaOverride
    // the user's own limit (null = the server default).
    public record AdminUser(
        Guid Id,
        string FirstName,
        string LastName,
        string Email,
        DateTime? CreatedAt,
        DateTime? LastLogin,
        long BytesUsed,
        int FileCount,
        long Quota,
        long? QuotaOverride,
        bool IsAdmin,
        bool IsPermanent,
        DateTime? AvatarUpdatedAt,
        DateTime? SuspendedAt, // non-null = suspended (can't sign in; data kept)
        DateTime? LastActiveAt = null, // last sign-in or use of the app
        DateTime? RemovalDueAt = null); // hosted mode only: when the account will be removed for inactivity; null if exempt

    // POST /admin/users. Validated like /user/register; the email counts as confirmed.
    public class AdminCreateUserRequest
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
        public string? Password { get; set; }
        public bool? IsAdmin { get; set; }
        public bool? IsPermanent { get; set; }
    }

    // POST /admin/users
    public record AdminCreatedUserResponse(string Success, Guid Id);

    // PUT /admin/users/{id}/password
    public class AdminSetPasswordRequest
    {
        public string? Password { get; set; }
    }

    // PUT /admin/users/{id}/suspended
    public class AdminSuspendRequest
    {
        public bool? Suspended { get; set; }
    }

    // PUT /admin/users/{id}/permanent
    public class AdminPermanentRequest
    {
        public bool? IsPermanent { get; set; }
    }

    // PATCH /admin/users/{id}/quota. Null puts the user back on the default quota.
    public class QuotaUpdateRequest
    {
        public long? QuotaBytes { get; set; }
    }

    // PUT /admin/users/{id}/admin
    public class AdminUpdateRequest
    {
        public bool? IsAdmin { get; set; }
    }

    // GET /admin/stats. Disk figures are null if the server can't read them.
    public record AdminStats(
        int UserCount,
        int AdminCount,
        int FileCount,
        int FolderCount,
        long TotalStoredBytes,
        long DefaultQuotaBytes,
        long? StorageBytesOnDisk,
        long? DiskTotalBytes,
        long? DiskFreeBytes,
        Guid CurrentUserId, // the administrator asking, so the page can leave their own row's account actions out
        int SuspendedCount = 0,
        string Mode = "self-hosted"); // "self-hosted" or "hosted"
}
