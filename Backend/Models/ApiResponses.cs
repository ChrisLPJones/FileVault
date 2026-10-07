namespace Backend.Models
{
    // Shapes of the simple JSON responses, used to describe them in the OpenAPI docs
    public record SuccessResponse(string Success);

    public record ErrorResponse(string Error);

    public record TokenResponse(string Success);

    public record TokenUpdateResponse(string Success, string Token);

    public record UserInfoResponse(string FirstName, string LastName, string Email, DateTime? AvatarUpdatedAt);
}
