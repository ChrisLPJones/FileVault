using System.Text.Json.Serialization;

namespace Backend.Models
{
    // A user row, and the /user/register request body (fields are validated before use)
    public class UserModel
    {
        public Guid Id { get; set; }

        [JsonPropertyName("Username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("Email")]
        public string Email { get; set; } = "";

        // Plain text in requests; the bcrypt hash when loaded from the database
        [JsonPropertyName("Password")]
        public string Password { get; set; } = "";
    }
}
