namespace Backend.Models
{
    // Shapes of the simple JSON responses, used to describe them in the OpenAPI docs
    public record SuccessResponse(string Success);

    public record ErrorResponse(string Error);

    public record TokenResponse(string Success);

    public record TokenUpdateResponse(string Success, string Token);

    public record UserInfoResponse(string FirstName, string LastName, string Email, DateTime? AvatarUpdatedAt, bool EmailVerified, string IconTheme = "default");

    // GET /user/info in hosted mode while the first-login notice is due: the usual fields plus
    // these two (the contact address is left out when none is set)
    public record HostedUserInfoResponse(string FirstName, string LastName, string Email, DateTime? AvatarUpdatedAt, bool EmailVerified, string IconTheme,
        bool HostedNotice,
        int HostedInactiveDays,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? HostedContactEmail)
        : UserInfoResponse(FirstName, LastName, Email, AvatarUpdatedAt, EmailVerified, IconTheme);
}
