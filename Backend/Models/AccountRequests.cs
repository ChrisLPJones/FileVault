namespace Backend.Models
{
    public class ProfileUpdateRequest
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
    }

    public class PasswordChangeRequest
    {
        public string? CurrentPassword { get; set; }
        public string? NewPassword { get; set; }
    }

    // POST /user/email/change
    public class ChangeEmailRequest
    {
        public string? Email { get; set; }
        public string? CurrentPassword { get; set; }
    }

    // POST /user/email/confirm and /user/email/cancel
    public class EmailChangeTokenRequest
    {
        public string? Token { get; set; }
    }
}
