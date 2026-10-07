namespace Backend.Models
{
    // POST /user/login body. "Login" is an email or a username; "Email" is still accepted
    // for older clients. Fields are validated before use.
    public class LoginModel
    {
        public string Login { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";

        // The email or username to look up
        public string Identifier => (string.IsNullOrWhiteSpace(Login) ? Email : Login).Trim();
    }
}
