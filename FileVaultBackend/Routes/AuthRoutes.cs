using FileVaultBackend.Models;
using FileVaultBackend.Services;
using System.Text.Json;

namespace FileVaultBackend.Routes
{
    public static class AuthRoutes
    {
        public static void MapAuthRoutes(this IEndpointRouteBuilder app)
        {
            // Get tokens

            // Create User
            app.MapPost("/register", async (HttpRequest request, DatabaseServices db) =>
            {
                string body;

                using (var reader = new StreamReader(request.Body))
                {
                    body = await reader.ReadToEndAsync();    
                }

                try
                {
                    var user = JsonSerializer.Deserialize<UserModel>(body);

                    return Results.Created("", $"User {user.UserName} created.");
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest("Invalid Json");
                }
            });


            // Read users info

            // Update User

            // Delete User

            // Refresh token

            // Logout
        }
    }
}