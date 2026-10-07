using Backend.Models;
using Backend.Services;
using System.Security.Claims;
using System.Text.Json;

namespace Backend.Routes
{
    // Unexpected errors (e.g. the database being down) are logged and turned into
    // a 500 { error } response by the exception handler in Program.cs.
    public static class AuthRoutes
    {
        // Accept both "Email" and "email" style property names
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // Read a JSON body, or null if it's missing or malformed
        private static async Task<T> ReadJsonAsync<T>(HttpRequest request) where T : class
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static void MapAuthRoutes(this IEndpointRouteBuilder app)
        {
            // Registers a new user
            app.MapPost("/user/register", async (
                HttpRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                var user = await ReadJsonAsync<UserModel>(request);

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

                if (await db.UserExistsByEmail(user.Email))
                    return Results.BadRequest(new { error = "Email already exists" });

                if (await db.UserExistsByUsername(user.Username))
                    return Results.BadRequest(new { error = "Username already exists" });

                await auth.HashAndRegisterUser(user, db);

                return Results.Ok(new { success = $"User {user.Username} registered" });
            }).RequireRateLimiting("auth");

            // Logs in a user: returns an access token and sets the refresh token cookie
            app.MapPost("/user/login", async (
                HttpContext http,
                AuthServices auth,
                DatabaseServices db) =>
            {
                var login = await ReadJsonAsync<LoginModel>(http.Request);

                if (login == null ||
                    string.IsNullOrWhiteSpace(login.Email) ||
                    string.IsNullOrWhiteSpace(login.Password))
                    return Results.BadRequest(new { error = "Invalid JSON" });

                var userRecord = await auth.ValidateUser(login, db);
                if (userRecord == null)
                    return Results.Json(new { error = "Invalid email or password" }, statusCode: 401);

                await auth.IssueRefreshTokenAsync(userRecord.Id.ToString(), db, http);

                return Results.Ok(new { Success = auth.GetJWTToken(userRecord) });
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
                var userInfo = await db.GetUserByUserId(user.GetUserId());
                if (userInfo == null)
                    return Results.NotFound(new { error = "User not found" });

                return Results.Ok(new
                {
                    username = userInfo.Username,
                    email = userInfo.Email
                });
            }).RequireAuthorization();

            // Updates the authenticated user's username and email
            app.MapPatch("/user/profile", async (
                ClaimsPrincipal user,
                ProfileUpdateRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                var username = request?.Username?.Trim();
                var email = request?.Email?.Trim().ToLowerInvariant();

                var validationError = AuthServices.ValidateAccount(username, email);
                if (validationError != null)
                    return Results.BadRequest(new { error = validationError });

                var userId = user.GetUserId();
                if (await db.GetUserByUserId(userId) == null)
                    return Results.NotFound(new { error = "User not found" });

                var conflict = await db.FindAccountConflictAsync(username, email, userId);
                if (conflict != null)
                    return Results.Conflict(new { error = conflict });

                await db.UpdateProfileAsync(userId, username, email);

                // New access token so the email claim is current
                var updated = await db.GetUserByUserId(userId);
                return Results.Ok(new { Success = "Profile updated", Token = auth.GetJWTToken(updated) });
            }).RequireAuthorization();

            // Changes the authenticated user's password (requires the current one)
            app.MapPost("/user/password", async (
                HttpContext http,
                ClaimsPrincipal user,
                PasswordChangeRequest request,
                DatabaseServices db,
                AuthServices auth) =>
            {
                var userId = user.GetUserId();
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
            }).RequireAuthorization();

            // Deletes the authenticated user's account and all their files
            app.MapDelete("/user", async (
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                FileServices fs) =>
            {
                var userId = user.GetUserId();
                if (await db.GetUserByUserId(userId) == null)
                    return Results.NotFound(new { error = "User not found" });

                var response = await db.DeleteUserAndFilesById(userId, fs);
                if (!response.Success)
                    return Results.Json(new { error = response.Message }, statusCode: 500);

                // Refresh tokens are deleted with the user; clear the cookie too
                AuthServices.ClearRefreshCookie(http);

                return Results.Ok(new { response.Message });
            }).RequireAuthorization();
        }
    }
}
