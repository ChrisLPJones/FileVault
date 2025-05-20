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

                HttpReturnResult result = await auth.HashAndRegisterUserAsync(user, db);

                return Results.Created("", result.Message);
            });


            // Read users info

            // Update User

            // Delete User

            // Refresh token

            // Logout
        }
    }
}