using Backend.Models;
using Backend.Services;
using Microsoft.Net.Http.Headers;
using System.Security.Claims;

namespace Backend.Routes
{
    public static class ThumbnailRoutes
    {
        public static void MapThumbnailRoutes(this IEndpointRouteBuilder app)
        {
            // Small WebP preview of one of the user's images (made on first request)
            app.MapGet("/files/{fileId}/thumbnail", async (
                HttpContext http,
                ClaimsPrincipal user,
                string fileId,
                ThumbnailService thumbnails,
                FileServices fs,
                DatabaseServices db) =>
            {
                var thumbnail = await thumbnails.GetAsync(fileId, fs, db, user.GetUserId());
                if (thumbnail == null)
                    return Results.NotFound(new { error = "No thumbnail" });

                // A file's content never changes, so its thumbnail can be cached; the ETag lets
                // the browser revalidate cheaply (304) once the cached copy is stale
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(thumbnail.Stream, thumbnail.MimeType,
                    entityTag: EntityTagHeaderValue.Parse(thumbnail.ETag));
            })
                .WithTags("Files")
                .WithSummary($"Get a thumbnail of an image file (JPEG, PNG, GIF, WebP or BMP), at most {ThumbnailService.MaxDimension}px on its longest side")
                .Produces(200, contentType: ThumbnailService.MimeType)
                .Produces(304)
                .Produces<ErrorResponse>(404).RequireAuthorization();
        }
    }
}
