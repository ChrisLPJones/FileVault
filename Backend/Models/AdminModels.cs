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
        string? LastLoginIp = null,
        string? LastLoginCountryCode = null,
        string? LastLoginCountry = null);

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
        long? DiskFreeBytes);
}
