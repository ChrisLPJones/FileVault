using Backend.Models;
using Backend.Services;
using System.Security.Claims;
using System.Text.Json;

namespace Backend.Routes
{
    // Share links: the owner's endpoints (/shares) need a login; the public ones (/s/{token})
    // don't, and are rate-limited per IP instead.
    public static class ShareRoutes
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private static IResult Error(HttpReturnResult result) =>
            Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);

        // The link password from a JSON body ({ "password": "..." }) or a form post (password=...).
        // The share page downloads with a plain form post so the browser streams the file to disk.
        private static async Task<string?> ReadPasswordAsync(HttpRequest request)
        {
            if (request.HasFormContentType)
                return (await request.ReadFormAsync())["password"].FirstOrDefault();

            try
            {
                return (await JsonSerializer.DeserializeAsync<SharePasswordRequest>(request.Body, JsonOptions))?.Password;
            }
            catch (JsonException)
            {
                return null; // no body or not JSON: treated as no password
            }
        }

        // Public responses are per-link and may be password-protected: never cache them
        private static void NoStore(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        }

        public static void MapShareRoutes(this IEndpointRouteBuilder app)
        {
            // Creates a link to one of the user's files or folders
            app.MapPost("/shares", async (
                ClaimsPrincipal user,
                CreateShareRequest request,
                ShareService shares,
                DatabaseServices db) =>
            {
                var result = await shares.CreateAsync(request, db, user.GetUserId());
                return result.Ok ? Results.Ok(result.Value) : Error(result.Error);
            })
                .WithTags("Shares")
                .WithSummary("Create a share link to a file or folder (optional expiresAt and password). The token is only returned here.")
                .Produces<CreatedShare>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Lists the user's links (not revoked; expired ones included)
            app.MapGet("/shares", async (
                ClaimsPrincipal user,
                DatabaseServices db) =>
            {
                return Results.Ok(await db.GetSharesAsync(user.GetUserId()));
            })
                .WithTags("Shares")
                .WithSummary("List your share links with the names of the shared items")
                .Produces<List<ShareSummary>>().RequireAuthorization();

            // Revokes one of the user's links
            app.MapDelete("/shares/{id}", async (
                ClaimsPrincipal user,
                string id,
                DatabaseServices db) =>
            {
                if (!Guid.TryParse(id, out var shareId) || !await db.RevokeShareAsync(shareId, user.GetUserId()))
                    return Results.NotFound(new { error = "Link not found" });

                return Results.Ok(new { success = "Link revoked" });
            })
                .WithTags("Shares")
                .WithSummary("Revoke a share link")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // What a link points to. A password-protected link only says that it needs a password.
            app.MapGet("/s/{token}", async (
                HttpContext http,
                string token,
                DatabaseServices db) =>
            {
                NoStore(http);
                var resolved = await ShareService.ResolveAsync(token, db);
                if (!resolved.Ok)
                    return Error(resolved.Error);

                if (resolved.Value.Share.PasswordHash != null)
                    return Results.Ok(new PublicShareInfo(PasswordRequired: true));

                return Results.Ok(await ShareService.DescribeAsync(resolved.Value, db));
            })
                .WithTags("Shares")
                .WithSummary("Public: name, size and folder contents of a share link (no login)")
                .Produces<PublicShareInfo>()
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(429).RequireRateLimiting("share");

            // Unlocks a password-protected link's details
            app.MapPost("/s/{token}", async (
                HttpContext http,
                string token,
                DatabaseServices db) =>
            {
                NoStore(http);
                var resolved = await ShareService.ResolveAsync(token, db);
                if (!resolved.Ok)
                    return Error(resolved.Error);

                if (!ShareService.CheckPassword(resolved.Value.Share, await ReadPasswordAsync(http.Request)))
                    return Error(ShareService.PasswordRejected);

                return Results.Ok(await ShareService.DescribeAsync(resolved.Value, db));
            })
                .WithTags("Shares")
                .WithSummary("Public: a share link's details, giving its password (body: { password })")
                .Produces<PublicShareInfo>()
                .Produces<ErrorResponse>(401)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(429).RequireRateLimiting("share-download");

            // Downloads the shared file, or the shared folder as a zip
            app.MapPost("/s/{token}/download", async (
                HttpContext http,
                string token,
                ShareService shares,
                DatabaseServices db) =>
            {
                NoStore(http);
                var resolved = await ShareService.ResolveAsync(token, db);
                if (!resolved.Ok)
                    return Error(resolved.Error);

                if (!ShareService.CheckPassword(resolved.Value.Share, await ReadPasswordAsync(http.Request)))
                    return Error(ShareService.PasswordRejected);

                var download = await shares.OpenDownloadAsync(resolved.Value, db);
                if (!download.Ok)
                    return Error(download.Error);

                await db.IncrementShareDownloadsAsync(resolved.Value.Share.Id);
                return Results.File(download.Value.Stream, download.Value.ContentType, download.Value.FileName);
            })
                .WithTags("Shares")
                .WithSummary("Public: download a shared file, or a shared folder as a zip (JSON or form body: password)")
                .Produces(200, contentType: "application/octet-stream")
                .Produces<ErrorResponse>(401)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(429).RequireRateLimiting("share-download");
        }
    }
}
