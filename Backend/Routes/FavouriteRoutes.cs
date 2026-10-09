using Backend.Models;
using Backend.Services;
using System.Security.Claims;

namespace Backend.Routes
{
    // Starring items and recording recently opened files. GET /files returns both
    // (isFavourite, lastOpenedAt), which the folder tree's Favourites and Recent use.
    public static class FavouriteRoutes
    {
        private static readonly IResult NotFound = Results.NotFound(new { error = "File not found." });

        public static void MapFavouriteRoutes(this IEndpointRouteBuilder app)
        {
            // Star a file or folder (doing it twice is fine)
            app.MapPut("/files/{fileId}/favourite", async (
                ClaimsPrincipal user,
                string fileId,
                DatabaseServices db) =>
            {
                return Guid.TryParse(fileId, out _) && await db.SetFavouriteAsync(fileId, user.GetUserId(), true)
                    ? Results.Ok(new { success = "Added to favourites" })
                    : NotFound;
            })
                .WithTags("Files")
                .WithSummary("Add a file or folder to favourites")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Unstar a file or folder
            app.MapDelete("/files/{fileId}/favourite", async (
                ClaimsPrincipal user,
                string fileId,
                DatabaseServices db) =>
            {
                return Guid.TryParse(fileId, out _) && await db.SetFavouriteAsync(fileId, user.GetUserId(), false)
                    ? Results.Ok(new { success = "Removed from favourites" })
                    : NotFound;
            })
                .WithTags("Files")
                .WithSummary("Remove a file or folder from favourites")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // The user previewed or downloaded a file: it goes to the top of Recent
            app.MapPost("/files/{fileId}/opened", async (
                ClaimsPrincipal user,
                string fileId,
                DatabaseServices db) =>
            {
                return Guid.TryParse(fileId, out _) && await db.MarkOpenedAsync(fileId, user.GetUserId())
                    ? Results.Ok(new { success = "Recorded" })
                    : NotFound;
            })
                .WithTags("Files")
                .WithSummary("Record that a file was opened (previewed or downloaded), for the Recent list")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();
        }
    }
}
