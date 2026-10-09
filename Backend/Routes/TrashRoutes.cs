using Backend.Models;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Backend.Routes
{
    // The recycle bin. DELETE /delete and /delete/{id} (FileRoutes) move items here.
    public static class TrashRoutes
    {
        private static IResult Result(HttpReturnResult result) =>
            result.Success
                ? Results.Ok(new { success = result.Message })
                : Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);

        public static void MapTrashRoutes(this IEndpointRouteBuilder app)
        {
            // Lists what's in the bin
            app.MapGet("/trash", async (
                ClaimsPrincipal user,
                TrashService trash,
                DatabaseServices db) =>
            {
                return Results.Ok(await trash.ListAsync(db, user.GetUserId()));
            })
                .WithTags("Recycle bin")
                .WithSummary("List the recycle bin: deleted items, where they were, and when they'll be deleted for good")
                .Produces<List<TrashItem>>().RequireAuthorization();

            // Puts items back where they were (or in the root if that folder is gone)
            app.MapPost("/trash/restore", async (
                ClaimsPrincipal user,
                [FromBody] RestoreRequest request,
                TrashService trash,
                DatabaseServices db) =>
            {
                return Result(await trash.RestoreAsync(request?.Ids, db, user.GetUserId()));
            })
                .WithTags("Recycle bin")
                .WithSummary("Restore items to their original folder (body: { ids: [...] }); taken names get a number")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Deletes one item in the bin for good
            app.MapDelete("/trash/{id}", async (
                ClaimsPrincipal user,
                string id,
                TrashService trash,
                DatabaseServices db) =>
            {
                return Result(await trash.DeletePermanentlyAsync(id, db, user.GetUserId()));
            })
                .WithTags("Recycle bin")
                .WithSummary("Permanently delete an item in the recycle bin")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Empties the bin
            app.MapDelete("/trash", async (
                ClaimsPrincipal user,
                TrashService trash,
                DatabaseServices db) =>
            {
                var count = await trash.EmptyAsync(db, user.GetUserId());
                return Results.Ok(new { success = $"Deleted {count} item(s) permanently" });
            })
                .WithTags("Recycle bin")
                .WithSummary("Empty the recycle bin")
                .Produces<SuccessResponse>().RequireAuthorization();
        }
    }
}
