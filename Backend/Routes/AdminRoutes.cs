using Backend.Models;
using Backend.Services;
using System.Security.Claims;
using System.Text.Json;

namespace Backend.Routes
{
    // The admin page. Every /admin endpoint except /admin/me checks the database on each
    // request, so removing someone's admin rights takes effect straight away (not when
    // their access token expires).
    public static class AdminRoutes
    {
        public const long MaxQuotaBytes = 1L << 50; // 1 PB, a sanity limit

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static void MapAdminRoutes(this IEndpointRouteBuilder app)
        {
            // Whether the current user can use the admin page (for showing the link to it)
            app.MapGet("/admin/me", async (ClaimsPrincipal user, DatabaseServices db) =>
                Results.Ok(new AdminStatusResponse(await db.IsAdminAsync(user.GetUserId()))))
                .WithTags("Admin")
                .WithSummary("Whether the current user is an administrator")
                .Produces<AdminStatusResponse>().RequireAuthorization();

            var admin = app.MapGroup("/admin")
                .WithTags("Admin")
                .RequireAuthorization()
                .AddEndpointFilter(async (context, next) =>
                {
                    var db = context.HttpContext.RequestServices.GetRequiredService<DatabaseServices>();
                    return await db.IsAdminAsync(context.HttpContext.User.GetUserId())
                        ? await next(context)
                        : Results.Json(new { error = "Administrators only" }, statusCode: StatusCodes.Status403Forbidden);
                });

            // Every account with its usage and quota
            admin.MapGet("/users", async (DatabaseServices db, IConfiguration config, GeoIpService geoIp) =>
            {
                // The country is looked up now, not stored, so it always reflects the current database
                var users = await db.GetUsersForAdminAsync(DefaultQuota(config));
                return Results.Ok(users.Select(u => geoIp.TryCountry(u.LastLoginIp) is var (code, name)
                    ? u with { LastLoginCountryCode = code, LastLoginCountry = name }
                    : u).ToList());
            })
                .WithSummary("List users: name, email, created, last login (with address and country), bytes used, file count and quota (admins only)")
                .Produces<List<AdminUser>>()
                .Produces<ErrorResponse>(403);

            // Change one user's storage quota (null = back to the default)
            admin.MapPatch("/users/{userId}/quota", async (string userId, HttpRequest http, DatabaseServices db) =>
            {
                // Read the body here so malformed JSON is a 400, not an unhandled binding error
                QuotaUpdateRequest? request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync<QuotaUpdateRequest>(http.Body, JsonOptions);
                }
                catch (JsonException)
                {
                    request = null;
                }

                if (request == null)
                    return Results.BadRequest(new { error = "Invalid JSON" });
                if (request.QuotaBytes is < 0 or > MaxQuotaBytes)
                    return Results.BadRequest(new { error = "Quota must be between 0 bytes and 1 PB" });
                if (!Guid.TryParse(userId, out _) || !await db.SetStorageQuotaAsync(userId, request.QuotaBytes))
                    return Results.NotFound(new { error = "User not found" });

                Audit(http.HttpContext, "set-quota", userId);
                return Results.Ok(new { success = request.QuotaBytes == null ? "Quota reset to the default" : "Quota updated" });
            })
                .WithSummary("Set a user's storage quota in bytes (body: { quotaBytes }; null = the default) (admins only)")
                .Accepts<QuotaUpdateRequest>("application/json")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404);

            // Make a user an administrator or take it away (never from the last one)
            admin.MapPut("/users/{userId}/admin", async (string userId, HttpRequest http, DatabaseServices db) =>
            {
                AdminUpdateRequest? request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync<AdminUpdateRequest>(http.Body, JsonOptions);
                }
                catch (JsonException)
                {
                    request = null;
                }

                if (request?.IsAdmin == null)
                    return Results.BadRequest(new { error = "Invalid JSON" });
                if (!Guid.TryParse(userId, out _))
                    return Results.NotFound(new { error = "User not found" });

                var result = await db.SetAdminAsync(userId, request.IsAdmin.Value);
                if (result.Success)
                    Audit(http.HttpContext, request.IsAdmin.Value ? "grant-admin" : "remove-admin", userId);
                return result.Success
                    ? Results.Ok(new { success = result.Message })
                    : Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);
            })
                .WithSummary("Grant or remove administrator rights (body: { isAdmin }); the last administrator can't be removed (admins only)")
                .Accepts<AdminUpdateRequest>("application/json")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(409);

            // Create an account that works straight away: the administrator vouches for the email
            // address, so there is no confirmation email
            admin.MapPost("/users", async (HttpContext http, DatabaseServices db, AuthServices auth, FileServices fs) =>
            {
                var request = await ReadJsonAsync<AdminCreateUserRequest>(http.Request);
                if (request == null)
                    return Results.BadRequest(new { error = "Invalid JSON" });

                var validationError = AuthServices.ValidateAccount(request.FirstName, request.LastName, request.Email)
                    ?? AuthServices.ValidatePassword(request.Password);
                if (validationError != null)
                    return Results.BadRequest(new { error = validationError });

                var user = new UserModel
                {
                    FirstName = request.FirstName!.Trim(),
                    LastName = request.LastName?.Trim() ?? "",
                    Email = request.Email!.Trim(),
                    Password = auth.GeneratePasswordHash(request.Password!)
                };
                var id = await db.CreateUserByAdminAsync(user, request.IsAdmin == true, request.IsPermanent == true);
                if (id == null)
                    return Results.Json(new { error = "Email already exists" }, statusCode: StatusCodes.Status409Conflict);

                await fs.CreateDefaultFoldersAsync(db, id.Value.ToString());
                Audit(http, "create-user", id.Value.ToString());
                return Results.Ok(new AdminCreatedUserResponse($"Account created for {user.Email.ToLowerInvariant()}", id.Value));
            })
                .WithSummary("Create an account (body: { firstName, lastName, email, password, isAdmin?, isPermanent? }); " +
                    "its email counts as confirmed, so it can sign in straight away (admins only)")
                .Accepts<AdminCreateUserRequest>("application/json")
                .Produces<AdminCreatedUserResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(409);

            // Delete another user's account and every file they stored
            admin.MapDelete("/users/{userId}", async (string userId, HttpContext http, AccountDeletionService deletion) =>
            {
                if (!Guid.TryParse(userId, out var id))
                    return Results.NotFound(new { error = "User not found" });
                if (id.ToString() == http.User.GetUserId())
                    return Results.BadRequest(new { error = "Use Settings to delete your own account" });

                var result = await deletion.DeleteAsync(id.ToString());
                if (!result.Success)
                    return Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 500);

                Audit(http, "delete-user", id.ToString());
                return Results.Ok(new { success = "Account deleted" });
            })
                .WithSummary("Delete an account and all its files; not your own, and never the last administrator (admins only)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(409);

            // Set another user's password. They are signed out everywhere, and the access tokens
            // they hold stop working immediately.
            admin.MapPut("/users/{userId}/password", async (string userId, HttpContext http, DatabaseServices db,
                AuthServices auth, AccessTokenGate tokenGate) =>
            {
                var request = await ReadJsonAsync<AdminSetPasswordRequest>(http.Request);
                if (request == null)
                    return Results.BadRequest(new { error = "Invalid JSON" });
                if (!Guid.TryParse(userId, out var id))
                    return Results.NotFound(new { error = "User not found" });
                if (id.ToString() == http.User.GetUserId())
                    return Results.BadRequest(new { error = "Use Settings to change your own password" });

                var validationError = AuthServices.ValidatePassword(request.Password);
                if (validationError != null)
                    return Results.BadRequest(new { error = validationError });

                if (!await db.SetPasswordAndSignOutAsync(id.ToString(), auth.GeneratePasswordHash(request.Password!)))
                    return Results.NotFound(new { error = "User not found" });

                tokenGate.Evict(id.ToString());
                Audit(http, "set-password", id.ToString()); // never the password itself
                return Results.Ok(new { success = "Password set; the user has been signed out everywhere" });
            })
                .WithSummary("Set another user's password (body: { password }) and sign them out everywhere, " +
                    "including access tokens they already hold (admins only)")
                .Accepts<AdminSetPasswordRequest>("application/json")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404);

            // Mark an account permanent, so inactive-account removal (hosted mode) skips it
            admin.MapPut("/users/{userId}/permanent", async (string userId, HttpContext http, DatabaseServices db) =>
            {
                var request = await ReadJsonAsync<AdminPermanentRequest>(http.Request);
                if (request?.IsPermanent == null)
                    return Results.BadRequest(new { error = "Invalid JSON" });
                if (!Guid.TryParse(userId, out var id) || !await db.SetPermanentAsync(id.ToString(), request.IsPermanent.Value))
                    return Results.NotFound(new { error = "User not found" });

                Audit(http, request.IsPermanent.Value ? "mark-permanent" : "unmark-permanent", id.ToString());
                return Results.Ok(new { success = request.IsPermanent.Value ? "Account marked permanent" : "Account no longer permanent" });
            })
                .WithSummary("Mark an account permanent or not (body: { isPermanent }) (admins only)")
                .Accepts<AdminPermanentRequest>("application/json")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404);

            // Another user's profile picture, for the users list
            admin.MapGet("/users/{userId}/avatar", async (string userId, HttpContext http, DatabaseServices db, AvatarService avatars) =>
            {
                var avatar = Guid.TryParse(userId, out var id) ? await avatars.OpenAsync(db, id.ToString()) : null;
                if (avatar == null)
                    return Results.NotFound(new { error = "No profile picture" });

                http.Response.Headers.CacheControl = "private, no-cache";
                return Results.File(avatar.Stream, avatar.MimeType,
                    lastModified: new DateTimeOffset(DateTime.SpecifyKind(avatar.UpdatedAt, DateTimeKind.Utc)));
            })
                .WithSummary("A user's profile picture (admins only)")
                .Produces(200, contentType: "image/png")
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404);

            // Totals and disk space
            admin.MapGet("/stats", async (ClaimsPrincipal user, DatabaseServices db, IConfiguration config) =>
            {
                var (users, admins, files, folders, bytes) = await db.GetAdminTotalsAsync();
                var (onDisk, total, free) = DiskUsage(config.GetValue<string>("StorageRoot") ?? "");
                return Results.Ok(new AdminStats(users, admins, files, folders, bytes, DefaultQuota(config), onDisk, total, free,
                    Guid.Parse(user.GetUserId())));
            })
                .WithSummary("User and file counts, bytes stored, and disk use of the storage folder (admins only)")
                .Produces<AdminStats>()
                .Produces<ErrorResponse>(403);
        }

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

        // One log line per change an administrator makes to someone's account: who, to whom and
        // what, as IDs. Never passwords or email addresses.
        private static void Audit(HttpContext http, string action, string targetUserId) =>
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Backend.AdminAudit")
                .LogInformation("Admin {AdminId} did {Action} on user {TargetUserId}", http.User.GetUserId(), action, targetUserId);

        private static long DefaultQuota(IConfiguration config) =>
            config.GetValue("Storage:DefaultQuotaBytes", FileServices.DefaultQuotaBytes);

        // Bytes used by the storage folder (encrypted files, avatars and thumbnails, so a little
        // more than the files' sizes), and the size and free space of the disk it is on
        private static (long? onDisk, long? total, long? free) DiskUsage(string storageRoot)
        {
            long? onDisk = null, total = null, free = null;
            try
            {
                var root = new DirectoryInfo(Path.GetFullPath(storageRoot));
                onDisk = root.Exists ? root.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
                var drive = new DriveInfo(root.FullName);
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Leave whatever couldn't be read as null
            }
            return (onDisk, total, free);
        }
    }
}
