namespace Backend.Models
{
    // Shapes of the simple JSON responses, used to describe them in the OpenAPI docs
    public record SuccessResponse(string Success);

    public record ErrorResponse(string Error);

    public record TokenResponse(string Success);

    public record TokenUpdateResponse(string Success, string Token);

    // PendingEmail / PendingEmailExpiresAt (UTC) are left out unless an email change is waiting to be confirmed
    public record UserInfoResponse(string FirstName, string LastName, string Email, DateTime? AvatarUpdatedAt, bool EmailVerified,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? PendingEmail = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        DateTime? PendingEmailExpiresAt = null);

    // POST /user/email/change: the address now waiting to be confirmed, and when its link expires (UTC)
    public record EmailChangeRequestedResponse(string Success, string PendingEmail, DateTime ExpiresAt);

    // POST /user/email/confirm: the account's new email and a new access token carrying it
    public record EmailChangeConfirmedResponse(string Success, string Email, string Token);

    // GET /user/info in hosted mode while the first-login notice is due: the usual fields plus
    // these two (the contact address is left out when none is set)
    public record HostedUserInfoResponse(string FirstName, string LastName, string Email, DateTime? AvatarUpdatedAt, bool EmailVerified,
        bool HostedNotice,
        int HostedInactiveDays,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? HostedContactEmail,
        string? PendingEmail = null,
        DateTime? PendingEmailExpiresAt = null)
        : UserInfoResponse(FirstName, LastName, Email, AvatarUpdatedAt, EmailVerified, PendingEmail, PendingEmailExpiresAt);
}
