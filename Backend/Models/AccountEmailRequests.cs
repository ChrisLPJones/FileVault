namespace Backend.Models
{
    // POST /user/forgot-password body
    public class ForgotPasswordRequest
    {
        public string? Email { get; set; }
    }

    // POST /user/reset-password body
    public class ResetPasswordRequest
    {
        public string? Token { get; set; }
        public string? NewPassword { get; set; }
    }

    // POST /user/verify-email body
    public class VerifyEmailRequest
    {
        public string? Token { get; set; }
    }
}
