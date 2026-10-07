namespace Backend.Models
{
    public class ProfileUpdateRequest
    {
        public string? Username { get; set; }
        public string? Email { get; set; }
    }

    public class PasswordChangeRequest
    {
        public string? CurrentPassword { get; set; }
        public string? NewPassword { get; set; }
    }
}
