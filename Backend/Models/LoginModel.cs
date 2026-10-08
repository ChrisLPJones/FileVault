namespace Backend.Models
{
    // POST /user/login body. Fields are validated before use.
    public class LoginModel
    {
        public string? Email { get; set; }
        public string Password { get; set; } = "";

        // Older clients (e.g. a tab opened before an update) send the address as "login"
        public string? Login { get; set; }

        // The email address to look up
        public string Identifier => (Email ?? Login ?? "").Trim();
    }
}
