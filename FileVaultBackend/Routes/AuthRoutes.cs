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




            // Update User
            app.MapPut("/user", async (
                ClaimsPrincipal user,
                UserModel updateUser,
                DatabaseServices db,
                AuthServices auth) =>
            {

                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                if (updateUser == null)
                    return Results.BadRequest(new { Error = "Request body is missing or invalid JSON" });

                if (!string.IsNullOrEmpty(updateUser.Password))
                    updateUser.Password = auth.GeneratePasswordHash(updateUser.Password);

                var oldUser = await db.GetUserByUserId(userId);
                
                if (user == null)
                    return Results.NotFound(new
                    {
                        Error = "User not found"
                    });

                var response = await db.UpdateUser(oldUser, updateUser, userId);
                if (!response.Success)
                    return Results.BadRequest(new { Error = response.Message });

                return Results.Ok(new { Success = "Updated user info" });

            }).RequireAuthorization();



            // Need to add JWT to delete user 


            // Delete User
            app.MapDelete("/user", async (
                ClaimsPrincipal user,
                DatabaseServices db,
                FileServices fs) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;
                
                var userModel = await db.GetUserByUserId(userId);

                if (userModel == null)
                    return Results.BadRequest(new { Error = "Request body is missing or invalid JSON" });

                
                if (userModel == null)
                    return Results.BadRequest(new { Error = "User not found" });

                try
                {
                    await db.DeleteUserById(userId, fs);
                    return Results.Ok(new { Success = "User deleted" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return Results.BadRequest(new { Error = "sql error" });
                }
            }).RequireAuthorization();

            // Refresh token /auth/refresh


            // Logout /auth/logout


        }
    }
}