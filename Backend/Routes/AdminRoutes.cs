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
            admin.MapGet("/users", async (DatabaseServices db, IConfiguration config) =>
                Results.Ok(await db.GetUsersForAdminAsync(DefaultQuota(config))))
                .WithSummary("List users: name, email, created, last login, bytes used, file count and quota (admins only)")
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

                return Results.Ok(new { success = request.QuotaBytes == null ? "Quota reset to the default" : "Quota updated" });
            })
                .WithSummary("Set a user's storage quota in bytes (body: { quotaBytes }; null = the default) (admins only)")
                .Accepts<QuotaUpdateRequest>("application/json")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(403)
                .Produces<ErrorResponse>(404);

            // Totals and disk space
            admin.MapGet("/stats", async (DatabaseServices db, IConfiguration config) =>
            {
                var (users, admins, files, folders, bytes) = await db.GetAdminTotalsAsync();
                var (onDisk, total, free) = DiskUsage(config.GetValue<string>("StorageRoot") ?? "");
                return Results.Ok(new AdminStats(users, admins, files, folders, bytes, DefaultQuota(config), onDisk, total, free));
            })
                .WithSummary("User and file counts, bytes stored, and disk use of the storage folder (admins only)")
                .Produces<AdminStats>()
                .Produces<ErrorResponse>(403);
        }

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
