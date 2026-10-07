using Backend.Models;
using Backend.Services;
using Microsoft.Data.SqlClient;
using System.Security.Claims;
using System.Text.Json;

namespace Backend.Routes
{
    public static class AuthRoutes
    {
        // Accept both "Email" and "email" style property names
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
                        user = JsonSerializer.Deserialize<UserModel>(body, JsonOptions);
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

                    var validationError = AuthServices.ValidateAccount(user.Username, user.Email)
                        ?? AuthServices.ValidatePassword(user.Password);
                    if (validationError != null)
                        return Results.BadRequest(new { error = validationError });

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
            }).RequireRateLimiting("auth");

            // Logs in a user: returns an access token and sets the refresh token cookie
            app.MapPost("/user/login", async (
                HttpContext http,
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
                        user = JsonSerializer.Deserialize<LoginModel>(body, JsonOptions);
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

                    var userRecord = await auth.ValidateUser(user, db);
                    if (userRecord == null)
                        return Results.Json(new { error = "Invalid email or password" }, statusCode: 401);

                    await auth.IssueRefreshTokenAsync(userRecord.Id.ToString(), db, http);

                    return Results.Ok(new { Success = auth.GetJWTToken(userRecord) });
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
            }).RequireRateLimiting("auth");

            // Exchanges the refresh token cookie for a new access token (and a new refresh cookie)
            app.MapPost("/user/refresh", async (
                HttpContext http,
                AuthServices auth,
                DatabaseServices db) =>
            {
                var user = await auth.RotateRefreshTokenAsync(db, http);
                if (user == null)
                {
                    AuthServices.ClearRefreshCookie(http);
                    return Results.Json(new { error = "Session expired" }, statusCode: 401);
                }

                return Results.Ok(new { Success = auth.GetJWTToken(user) });
            }).RequireRateLimiting("refresh");

            // Logs out: revokes the refresh token and clears its cookie
            app.MapPost("/user/logout", async (
                HttpContext http,
                AuthServices auth,
                DatabaseServices db) =>
            {
                await auth.RevokeRefreshCookieAsync(db, http);
                return Results.Ok(new { success = "Logged out" });
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

            // Updates the authenticated user's username and email
            app.MapPatch("/user/profile", async (
                ClaimsPrincipal user,
                ProfileUpdateRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                try
                {
                    var username = request?.Username?.Trim();
                    var email = request?.Email?.Trim().ToLowerInvariant();

                    var validationError = AuthServices.ValidateAccount(username, email);
                    if (validationError != null)
                        return Results.BadRequest(new { error = validationError });

                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (await db.GetUserByUserId(userId) == null)
                        return Results.NotFound(new { error = "User not found" });

                    var conflict = await db.FindAccountConflictAsync(username, email, userId);
                    if (conflict != null)
                        return Results.Conflict(new { error = conflict });

                    await db.UpdateProfileAsync(userId, username, email);

                    // New access token so the email claim is current
                    var updated = await db.GetUserByUserId(userId);
                    return Results.Ok(new { Success = "Profile updated", Token = auth.GetJWTToken(updated) });
                }
                catch (SqlException ex)
                {
                    Console.WriteLine($"Database error: {ex.Message}");
                    return Results.Json(new { error = "A database error has occurred" }, statusCode: 500);
                }
            }).RequireAuthorization();

            // Changes the authenticated user's password (requires the current one)
            app.MapPost("/user/password", async (
                HttpContext http,
                ClaimsPrincipal user,
                PasswordChangeRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                try
                {
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var account = await db.GetUserByUserId(userId);
                    if (account == null)
                        return Results.NotFound(new { error = "User not found" });

                    if (string.IsNullOrEmpty(request?.CurrentPassword) ||
                        !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, account.Password))
                        return Results.BadRequest(new { error = "Current password is incorrect" });

                    var passwordError = AuthServices.ValidatePassword(request.NewPassword);
                    if (passwordError != null)
                        return Results.BadRequest(new { error = passwordError });

                    await db.UpdatePasswordHashAsync(userId, auth.GeneratePasswordHash(request.NewPassword));

                    // End every other session, then start a fresh one for this client
                    await db.RevokeAllRefreshTokensAsync(userId);
                    await auth.IssueRefreshTokenAsync(userId, db, http);

                    return Results.Ok(new { Success = "Password changed", Token = auth.GetJWTToken(account) });
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
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                FileServices fs) =>
            {
                try
                {
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    var userModel = await db.GetUserByUserId(userId);

                    if (userModel == null)
                        return Results.NotFound(new { error = "User not found" });

                    var response = await db.DeleteUserAndFilesById(userId, fs);
                    if (!response.Success)
                        return Results.Json(new { error = response.Message }, statusCode: 500);

                    // Refresh tokens are deleted with the user; clear the cookie too
                    AuthServices.ClearRefreshCookie(http);

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
        }
    }
}
