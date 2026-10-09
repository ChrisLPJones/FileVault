namespace Backend.Models
{
    // One device the user is logged in on (GET /user/sessions). Times are UTC.
    public record SessionResponse(Guid Id, string Device, string? IpAddress, DateTime CreatedAt, DateTime LastActiveAt, bool IsCurrent);
}
