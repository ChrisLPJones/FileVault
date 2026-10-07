namespace Backend.Models
{
    // POST /user/login body (fields are validated before use)
    public class LoginModel
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
