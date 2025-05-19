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
            app.MapPost("/register", async (UserModel user, HttpRequest request, DatabaseServices db, AuthServices auth) =>
            {
                string body;

                Console.WriteLine($"Recived: {user}");

                await auth.HashAndRegisterUserAsync(user, db);

                return Results.Created("", $"User {user.UserName} created");
            });


            // Read users info

            // Update User

            // Delete User

            // Refresh token

            // Logout
        }
    }
}