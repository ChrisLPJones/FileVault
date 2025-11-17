using System.Text.Json.Serialization;

namespace Backend.Models
{
    public class UserModel
    {
        public Guid Id { get; set; }

        [JsonPropertyName("Username")]
        public string Username { get; set; }

        [JsonPropertyName("Email")]
        public string Email { get; set; }

        [JsonPropertyName("Password")]
        public string Password { get; set; }
    }
}
