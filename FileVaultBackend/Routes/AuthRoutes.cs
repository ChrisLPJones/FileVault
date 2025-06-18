using FileVaultBackend.Models;
using FileVaultBackend.Services;
using System.Security.Claims;

namespace FileVaultBackend.Routes
{
    public static class AuthRoutes
    {
        public static void MapAuthRoutes(this IEndpointRouteBuilder app)
        {
            // Registers a new user with hashed password
            app.MapPost("/user/register", async (
                UserModel user,
                DatabaseServices db,
                AuthServices auth) =>
            {

                var checkUser = await db.GetUserByUsername(user.Username);
                if (checkUser != null)
                    return Results.BadRequest(new { error = "User already exists." });

                var result = await auth.HashAndRegisterUser(user, db);
                
                return Results.Created("", result.Message);
            });

            // Logs in a user and returns a success message or error
            app.MapPost("/user/login", async (
                LoginModel user,
                AuthServices auth,
                DatabaseServices db) =>
            {
                var result = await auth.ValidateUser(user, db, auth);
                if (!result.Success)
                    return Results.BadRequest(new { Error = result.Message });

                return Results.Ok(new { Success = result.Message });
            });

            // Retrieves authenticated user's information
            app.MapGet("/user/info", async (
                ClaimsPrincipal user,
                string username,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var userInfo = await db.GetUserByUserId(userId);

                if (userInfo == null)
                {
                    return Results.NotFound(new { Error = "User not found" });
                }

                return Results.Ok(new
                {
                    username = userInfo.Username,
                    email = userInfo.Email
                });
            }).RequireAuthorization();

            // Updates authenticated user's account information
            app.MapPut("/user", async (
                ClaimsPrincipal user,
                UserModel updateUser,
                DatabaseServices db,
                AuthServices auth) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (updateUser == null)
                    return Results.BadRequest(new { Error = "Request body is missing or invalid JSON" });

                if (!string.IsNullOrEmpty(updateUser.Password))
                    updateUser.Password = auth.GeneratePasswordHash(updateUser.Password);

                var oldUser = await db.GetUserByUserId(userId);

                if (oldUser == null)
                    return Results.NotFound(new { Error = "User not found" });

                var response = await db.UpdateUser(oldUser, updateUser, userId);
                if (!response.Success)
                    return Results.BadRequest(new { Error = response.Message });

                return Results.Ok(new { Success = "Updated user info" });
            }).RequireAuthorization();

            // Deletes the authenticated user's account and all their files
            app.MapDelete("/user", async (
                ClaimsPrincipal user,
                DatabaseServices db,
                FileServices fs) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var userModel = await db.GetUserByUserId(userId);

                if (userModel == null)
                    return Results.BadRequest(new { Error = "User not found" });

                try
                {
                    var response = await db.DeleteUserAndFilesById(userId, fs);
                    return Results.Ok(new { response.Message });
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return Results.BadRequest(new { Error = "SQL error occurred" });
                }
            }).RequireAuthorization();

            // TODO: Implement token refresh and logout endpoints
        }
    }
}
