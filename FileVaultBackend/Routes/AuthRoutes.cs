using FileVaultBackend.Models;
using FileVaultBackend.Services;
using System.Security.Claims;

namespace FileVaultBackend.Routes
{
    public static class AuthRoutes
    {
        public static void MapAuthRoutes(this IEndpointRouteBuilder app)
        {
            // Create User
            app.MapPost("/user/register", async (
                UserModel user, 
                HttpRequest request, 
                DatabaseServices db, 
                AuthServices auth) =>
            {
                HttpReturnResult result = await auth.HashAndRegisterUser(user, db);

                return Results.Created("", result.Message);
            });





            // Login
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





            // Get users info
            app.MapGet("/user/info", async (
                ClaimsPrincipal user,
                string username, 
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                var userInfo = await db.GetUserByUserId(userId);

                if (user == null)
                {
                    return Results.NotFound(new
                    {
                        Error = $"User not found"
                    });
                }

                return Results.Ok(new
                {
                    username = userInfo.Username,
                    email = userInfo.Email
                });
            }).RequireAuthorization();


            // Need to add JWT to update user and delete user 


            // Update User
            app.MapPut("/user/{username}", async (
                string username,
                UserModel updateUser,
                DatabaseServices db,
                AuthServices auth) =>
            {
                if (updateUser == null)
                    return Results.BadRequest(new { Error = "Request body is missing or invalid JSON" });

                if (!string.IsNullOrEmpty(updateUser.Password))
                    updateUser.Password = auth.GeneratePasswordHash(updateUser.Password);

                var user = await db.GetUserByUsername(username);
                if (user == null)
                    return Results.NotFound(new
                    {
                        Error = "User not found"
                    });

                var response = await db.UpdateUser(username, updateUser);
                if (!response.Success)
                    return Results.BadRequest(new { Error = response.Message });

                return Results.Ok(new { Success = "Updated user info" });

            }).RequireAuthorization();





            // Delete User
            app.MapDelete("/user/{username}", async (
                string username,
                DatabaseServices db) =>
            {
                if (string.IsNullOrEmpty(username))
                    return Results.BadRequest(new { Error = "Request body is missing or invalid JSON" });

                var user = await db.GetUserByUsername(username);
                //Console.WriteLine(user.UserName);
                if (user == null)
                    return Results.BadRequest(new { Error = "User not found" });

                try
                {
                    db.DeleteUser(username);
                    return Results.Ok(new { Success = "User deleted" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return Results.BadRequest(new { Error = "sql error" });
                }
            }).RequireAuthorization();

            // Refresh token


            // Logout


        }
    }
}