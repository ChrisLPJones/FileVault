using Backend.Models;
using Backend.Services;
using Microsoft.Data.SqlClient;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;

namespace Backend.Routes
{
    public static class AuthRoutes
    {
        public static void MapAuthRoutes(this IEndpointRouteBuilder app)
        {
            // Registers a new user
            app.MapPost("/user/register", async (
                HttpRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                try
                {
                    // Read request
                    using StreamReader reader = new(request.Body);
                    var body = await reader.ReadToEndAsync();

                    // Parse request into UserModel
                    UserModel user;
                    try
                    {
                        user = JsonSerializer.Deserialize<UserModel>(body);
                    }
                    catch (JsonException)
                    {
                        return Results.BadRequest(new { error = "Invalid JSON" });
                    }

                    // Require all fields
                    if (user is null ||
                    string.IsNullOrWhiteSpace(user.Username) ||
                    string.IsNullOrWhiteSpace(user.Email) ||
                    string.IsNullOrWhiteSpace(user.Password))
                        return Results.BadRequest(new { error = "Invalid JSON" });

                    // Check if User exists by email
                    if (await db.UserExistsByEmail(user.Email))
                        return Results.BadRequest(new { error = "Email already exists" });

                    // Check if user exists by username
                    if (await db.UserExistsByUsername(user.Username))
                        return Results.BadRequest(new { error = "Username already exists" });

                    // Hash and register user
                    await auth.HashAndRegisterUser(user, db);

                    // Return 200 OK 
                    return Results.Ok(new { success = $"User {user.Username} registered" });
                }
                catch (SqlException ex)
                {
                    Console.WriteLine($"Database error: {ex.Message}");
                    return Results.Json(new { error = "A database error has occurred" }, statusCode: 500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Internal error: {ex.Message}");
                    return Results.Json(new { error = "A internal error has occurred" }, statusCode: 500);
                }
            });

            // Logs in a user and returns a success message or error
            app.MapPost("/user/login", async (
                HttpRequest request,
                AuthServices auth,
                DatabaseServices db) =>
            {
                try
                {
                    StreamReader reader = new(request.Body);
                    string body = await reader.ReadToEndAsync();

                    LoginModel user;

                    try
                    {
                        user = JsonSerializer.Deserialize<LoginModel>(body);
                    }
                    catch (JsonException)
                    {
                        return Results.BadRequest(new { error = "Invalid JSON" });
                    }


                    if (user == null ||
                    string.IsNullOrWhiteSpace(user.Email) ||
                    string.IsNullOrWhiteSpace(user.Password))
                    {
                        return Results.BadRequest(new { Error = "Invalid JSON" });
                    }

                    var result = await auth.ValidateUser(user, db, auth);
                    if (!result.Success)
                        //return Results.BadRequest(new { Error = result.Message });
                        return Results.Unauthorized();

                    return Results.Ok(new { Success = result.Message });
                }
                catch (SqlException ex)
                {
                    Console.WriteLine($"Database error: {ex.Message}");
                    return Results.Json(new { error = "A database error has occurred" }, statusCode: 500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Internal error: {ex.Message}");
                    return Results.Json(new { error = "A internal error has occurred" }, statusCode: 500);
                }
            });

            // Retrieves authenticated user's information
            app.MapGet("/user/info", async (
                ClaimsPrincipal user,
                DatabaseServices db) =>
            {
                try
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
                }
                catch (SqlException ex)
                {
                    Console.WriteLine($"Database error: {ex.Message}");
                    return Results.Json(new { error = "A database error has occurred" }, statusCode: 500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Internal error: {ex.Message}");
                    return Results.Json(new { error = "A internal error has occurred" }, statusCode: 500);
                }
            }).RequireAuthorization();

            // Updates authenticated user's account information
            app.MapPut("/user", async (
                ClaimsPrincipal user,
                UserModel updateUser,
                DatabaseServices db,
                AuthServices auth) =>
            {
                try
                {
                    // Not sure if all fields should be filled in this Put request
                    if (updateUser == null ||
                    string.IsNullOrWhiteSpace(updateUser.Username) ||
                    string.IsNullOrWhiteSpace(updateUser.Email) ||
                    string.IsNullOrWhiteSpace(updateUser.Password))
                    {
                        return Results.BadRequest(new { error = "invalid JSON" });
                    }

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
                }
                catch (SqlException ex)
                {
                    Console.WriteLine($"Database error: {ex.Message}");
                    return Results.Json(new { error = "A database error has occurred" }, statusCode: 500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Internal error: {ex.Message}");
                    return Results.Json(new { error = "A internal error has occurred" }, statusCode: 500);
                }
            }).RequireAuthorization();

            // Deletes the authenticated user's account and all their files
            app.MapDelete("/user", async (
                ClaimsPrincipal user,
                DatabaseServices db,
                FileServices fs) =>
            {
                try
                {
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    var userModel = await db.GetUserByUserId(userId);

                    if (userModel == null)
                        return Results.BadRequest(new { error = "User not found" });

                    var response = await db.DeleteUserAndFilesById(userId, fs);

                    return Results.Ok(new { response.Message });
                }
                catch (SqlException ex)
                {
                    Console.WriteLine($"Database error: {ex.Message}");
                    return Results.Json(new { error = "A database error has occurred" }, statusCode: 500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Internal error: {ex.Message}");
                    return Results.Json(new { error = "An internal error has occurred" }, statusCode: 500);
                }

            }).RequireAuthorization();

            // TODO: Implement token refresh and logout endpoints
        }
    }
}
