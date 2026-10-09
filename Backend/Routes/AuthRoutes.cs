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
        private static async Task<T?> ReadJsonAsync<T>(HttpRequest request) where T : class
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
                AuthServices auth,
                FileServices fs,
                AccountEmailService emails) =>
            {
                var user = await ReadJsonAsync<UserModel>(request);

                // Require all fields
                if (user is null ||
                    string.IsNullOrWhiteSpace(user.Email) ||
                    string.IsNullOrWhiteSpace(user.Password))
                    return Results.BadRequest(new { error = "Invalid JSON" });

                var validationError = AuthServices.ValidateAccount(user.FirstName, user.LastName, user.Email)
                    ?? AuthServices.ValidatePassword(user.Password);
                if (validationError != null)
                    return Results.BadRequest(new { error = validationError });

                if (await db.UserExistsByEmail(user.Email))
                    return Results.BadRequest(new { error = "Email already exists" });

                user.LastName = user.LastName?.Trim() ?? "";
                await auth.HashAndRegisterUser(user, db);

                // Starter folders (Documents, Pictures, Music, Videos) for the new account
                var created = await db.GetUserByEmail(user.Email.Trim());
                if (created != null)
                {
                    await fs.CreateDefaultFoldersAsync(db, created.Id.ToString());
                    // Ask them to confirm the address (they can use the app meanwhile)
                    await emails.SendVerificationAsync(created, db);
                }

                return Results.Ok(new { success = $"User {$"{user.FirstName.Trim()} {user.LastName}".Trim()} registered" });
            })
                .WithTags("Account")
                .WithSummary("Create an account")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(429).RequireRateLimiting("auth");

            // Logs in a user: returns an access token and sets the refresh token cookie
            app.MapPost("/user/login", async (
                HttpContext http,
                AuthServices auth,
                DatabaseServices db,
                TwoFactorService twoFactor) =>
            {
                var login = await ReadJsonAsync<LoginModel>(http.Request);

                if (login == null ||
                    string.IsNullOrWhiteSpace(login.Identifier) ||
                    string.IsNullOrWhiteSpace(login.Password))
                    return Results.BadRequest(new { error = "Invalid JSON" });

                var userRecord = await auth.ValidateUser(login, db);
                if (userRecord == null)
                    return Results.Json(new { error = "Invalid email or password" }, statusCode: 401);

                // Only after the password matched, so this doesn't reveal anything about other accounts
                if (!await db.IsEmailVerifiedAsync(userRecord.Id.ToString()))
                    return Results.Json(new { error = "Please confirm your email address first", emailNotVerified = true }, statusCode: 403);

                // Also only after the password matched. No challenge is issued and nothing is recorded.
                if ((await db.GetUserAuthStateAsync(userRecord.Id.ToString())).Suspended)
                    return Results.Json(new { error = AuthServices.SuspendedMessage, suspended = true }, statusCode: 403);

                // With two-factor on, the password only earns a short-lived challenge for POST /user/login/2fa
                if ((await db.GetTwoFactorStateAsync(userRecord.Id.ToString()))?.Enabled == true)
                    return Results.Ok(new TwoFactorChallengeResponse(true,
                        await twoFactor.CreateLoginChallengeAsync(userRecord.Id.ToString())));

                // Null means the token was refused: suspended after the check above
                if (await auth.IssueRefreshTokenAsync(userRecord.Id.ToString(), db, http) == null)
                    return Results.Json(new { error = AuthServices.SuspendedMessage, suspended = true }, statusCode: 403);

                return Results.Ok(new { Success = auth.GetJWTToken(userRecord) });
            })
                .WithTags("Account")
                .WithSummary("Log in with email and password: returns an access token and sets the refresh-token cookie " +
                    "(with two-factor authentication on, returns { twoFactorRequired, challengeToken } for POST /user/login/2fa instead)")
                .Produces<TokenResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(401)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(429).RequireRateLimiting("auth");

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
            })
                .WithTags("Account")
                .WithSummary("Get a new access token using the refresh-token cookie")
                .Produces<TokenResponse>()
                .Produces<ErrorResponse>(401)
                .Produces<ErrorResponse>(429).RequireRateLimiting("refresh");

            // Logs out: revokes the refresh token and clears its cookie
            app.MapPost("/user/logout", async (
                HttpContext http,
                AuthServices auth,
                DatabaseServices db) =>
            {
                await auth.RevokeRefreshCookieAsync(db, http);
                return Results.Ok(new { success = "Logged out" });
            })
                .WithTags("Account")
                .WithSummary("Log out: revokes the refresh token and clears its cookie")
                .Produces<SuccessResponse>();

            // Retrieves authenticated user's information
            app.MapGet("/user/info", async (
                ClaimsPrincipal user,
                DatabaseServices db) =>
            {
                var userId = user.GetUserId();
                var userInfo = await db.GetUserByUserId(userId);
                if (userInfo == null)
                    return Results.NotFound(new { error = "User not found" });

                var avatar = await db.GetAvatarAsync(userId);
                return Results.Ok(new UserInfoResponse(userInfo.FirstName, userInfo.LastName, userInfo.Email, avatar?.UpdatedAt,
                    await db.IsEmailVerifiedAsync(userId)));
            })
                .WithTags("Account")
                .WithSummary("Get the current user's name, email, whether it's confirmed and when their profile picture last changed")
                .Produces<UserInfoResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Updates the authenticated user's name and email
            app.MapPatch("/user/profile", async (
                ClaimsPrincipal user,
                ProfileUpdateRequest request,
                DatabaseServices db,
                AuthServices auth,
                AccountEmailService emails) =>
            {
                var firstName = request?.FirstName?.Trim();
                var lastName = request?.LastName?.Trim() ?? "";
                var email = request?.Email?.Trim().ToLowerInvariant();

                var validationError = AuthServices.ValidateAccount(firstName, lastName, email);
                if (validationError != null || firstName is null || email is null)
                    return Results.BadRequest(new { error = validationError ?? "Invalid JSON" });

                var userId = user.GetUserId();
                var account = await db.GetUserByUserId(userId);
                if (account == null)
                    return Results.NotFound(new { error = "User not found" });

                var conflict = await db.FindAccountConflictAsync(email, userId);
                if (conflict != null)
                    return Results.Conflict(new { error = conflict });

                await db.UpdateProfileAsync(userId, firstName, lastName, email);
                var emailChanged = !string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase);

                // New access token so the email claim is current
                account.FirstName = firstName;
                account.LastName = lastName;
                account.Email = email;

                // A new address needs confirming
                if (emailChanged)
                {
                    await db.SetEmailVerifiedAsync(userId, false);
                    await emails.SendVerificationAsync(account, db);
                }

                return Results.Ok(new { Success = "Profile updated", Token = auth.GetJWTToken(account) });
            })
                .WithTags("Account")
                .WithSummary("Change name and email (returns a new access token)")
                .Produces<TokenUpdateResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(409).RequireAuthorization();

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
                if (passwordError != null || request.NewPassword is null)
                    return Results.BadRequest(new { error = passwordError ?? "New password is required" });

                await db.UpdatePasswordHashAsync(userId, auth.GeneratePasswordHash(request.NewPassword));

                // End every other session, then start a fresh one for this client
                await db.RevokeAllRefreshTokensAsync(userId);
                await auth.IssueRefreshTokenAsync(userId, db, http);

                return Results.Ok(new { Success = "Password changed", Token = auth.GetJWTToken(account) });
            })
                .WithTags("Account")
                .WithSummary("Change password (ends other sessions, returns a new access token)")
                .Produces<TokenUpdateResponse>()
                .Produces<ErrorResponse>(400).RequireAuthorization();

            // Deletes the authenticated user's account and all their files (409 for the last administrator)
            app.MapDelete("/user", async (
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                AccountDeletionService deletion) =>
            {
                var userId = user.GetUserId();
                if (await db.GetUserByUserId(userId) == null)
                    return Results.NotFound(new { error = "User not found" });

                var response = await deletion.DeleteAsync(userId);
                if (!response.Success)
                    return Results.Json(new { error = response.Message }, statusCode: response.StatusCode ?? 500);

                // Refresh tokens are deleted with the user; clear the cookie too
                AuthServices.ClearRefreshCookie(http);

                return Results.Ok(new { response.Message });
            })
                .WithTags("Account")
                .WithSummary("Delete the account and every stored file")
                .Produces(200)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(409).RequireAuthorization();

            // Sets the profile picture (multipart field "avatar": PNG, JPEG or WebP, up to 2 MB)
            app.MapPut("/user/avatar", async (
                ClaimsPrincipal user,
                HttpRequest request,
                DatabaseServices db,
                AvatarService avatars) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected form-data content type" });

                IFormCollection form;
                try
                {
                    form = await request.ReadFormAsync();
                }
                catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
                {
                    return Results.Json(new { error = "Profile pictures must be 2 MB or smaller" }, statusCode: 413);
                }

                var file = form.Files.GetFile("avatar") ?? form.Files.FirstOrDefault();
                if (file == null)
                    return Results.BadRequest(new { error = "No image uploaded" });

                var result = await avatars.SaveAsync(file, db, user.GetUserId());
                return result.Success
                    ? Results.Ok(new { success = result.Message })
                    : Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);
            })
                .WithTags("Account")
                .WithSummary("Set the profile picture (multipart field \"avatar\": PNG, JPEG or WebP, up to 2 MB)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(413)
                .WithMetadata(new MultipartUploadMetadata("avatar", "PNG, JPEG or WebP image, up to 2 MB", IncludeParentId: false)).RequireAuthorization();

            // Returns the profile picture
            app.MapGet("/user/avatar", async (
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                AvatarService avatars) =>
            {
                var avatar = await avatars.OpenAsync(db, user.GetUserId());
                if (avatar == null)
                    return Results.NotFound(new { error = "No profile picture" });

                http.Response.Headers.CacheControl = "private, no-cache";
                return Results.File(avatar.Stream, avatar.MimeType,
                    lastModified: new DateTimeOffset(DateTime.SpecifyKind(avatar.UpdatedAt, DateTimeKind.Utc)));
            })
                .WithTags("Account")
                .WithSummary("Get the profile picture")
                .Produces(200, contentType: "image/png")
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Removes the profile picture
            app.MapDelete("/user/avatar", async (
                ClaimsPrincipal user,
                DatabaseServices db,
                AvatarService avatars) =>
            {
                await avatars.DeleteAsync(db, user.GetUserId());
                return Results.Ok(new { success = "Profile picture removed" });
            })
                .WithTags("Account")
                .WithSummary("Remove the profile picture")
                .Produces<SuccessResponse>().RequireAuthorization();
        }
    }
}
