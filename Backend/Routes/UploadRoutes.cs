using Backend.Models;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Backend.Routes
{
    // Chunked, resumable uploads for big files (POST /upload remains for small ones)
    public static class UploadRoutes
    {
        private static IResult Error(HttpReturnResult result) =>
            Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);

        public static void MapUploadRoutes(this IEndpointRouteBuilder app)
        {
            // Starts an upload: checks the name, size limit, quota and folder up front
            app.MapPost("/uploads", async (
                ClaimsPrincipal user,
                StartUploadRequest request,
                ChunkedUploadService uploads,
                DatabaseServices db) =>
            {
                var result = await uploads.StartAsync(request, db, user.GetUserId());
                return result.Ok ? Results.Ok(result.Value) : Error(result.Error);
            })
                .WithTags("Uploads")
                .WithSummary("Start a chunked upload (body: { name, size, parentId?, mimeType? }); returns the upload ID and chunk size")
                .Produces<StartedUpload>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(413)
                .Produces<ErrorResponse>(429).RequireAuthorization();

            // Receives one chunk (raw bytes, exactly the chunk's length)
            app.MapPut("/uploads/{id}/chunks/{index}", async (
                ClaimsPrincipal user,
                HttpRequest request,
                string id,
                int index,
                ChunkedUploadService uploads,
                DatabaseServices db) =>
            {
                if (!Guid.TryParse(id, out var uploadId))
                    return Results.NotFound(new { error = "Upload not found" });

                try
                {
                    var result = await uploads.PutChunkAsync(uploadId, index, request.Body, db, user.GetUserId(), request.HttpContext.RequestAborted);
                    return result.Success ? Results.Ok(new { success = result.Message }) : Error(result);
                }
                catch (BadHttpRequestException)
                {
                    // Body larger than the request size limit
                    return Results.Json(new { error = "Chunk is too large" }, statusCode: 413);
                }
            })
                .WithTags("Uploads")
                .WithSummary("Send one chunk of an upload (body: the chunk's bytes, application/octet-stream)")
                .Accepts<byte[]>("application/octet-stream")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(413)
                .WithMetadata(new RequestSizeLimitAttribute(ChunkedUploadService.ChunkSize + 1024 * 1024))
                .RequireAuthorization();

            // Which chunks have arrived, so an interrupted upload can send only the rest
            app.MapGet("/uploads/{id}", async (
                ClaimsPrincipal user,
                string id,
                ChunkedUploadService uploads,
                DatabaseServices db) =>
            {
                if (!Guid.TryParse(id, out var uploadId))
                    return Results.NotFound(new { error = "Upload not found" });

                var result = await uploads.GetStatusAsync(uploadId, db, user.GetUserId());
                return result.Ok ? Results.Ok(result.Value) : Error(result.Error);
            })
                .WithTags("Uploads")
                .WithSummary("Get an upload's progress: the chunks received so far")
                .Produces<UploadStatus>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Assembles the chunks into the stored, encrypted file
            app.MapPost("/uploads/{id}/complete", async (
                ClaimsPrincipal user,
                string id,
                ChunkedUploadService uploads,
                DatabaseServices db) =>
            {
                if (!Guid.TryParse(id, out var uploadId))
                    return Results.NotFound(new { error = "Upload not found" });

                var result = await uploads.CompleteAsync(uploadId, db, user.GetUserId());
                return result.Ok ? Results.Ok(result.Value) : Error(result.Error);
            })
                .WithTags("Uploads")
                .WithSummary("Finish an upload once every chunk has arrived; a taken name gets a number")
                .Produces<CompletedUpload>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(409)
                .Produces<ErrorResponse>(413).RequireAuthorization();

            // Cancels an upload and deletes its chunks
            app.MapDelete("/uploads/{id}", async (
                ClaimsPrincipal user,
                string id,
                ChunkedUploadService uploads,
                DatabaseServices db) =>
            {
                if (!Guid.TryParse(id, out var uploadId))
                    return Results.NotFound(new { error = "Upload not found" });

                var result = await uploads.CancelAsync(uploadId, db, user.GetUserId());
                return result.Success ? Results.Ok(new { success = result.Message }) : Error(result);
            })
                .WithTags("Uploads")
                .WithSummary("Cancel an upload and delete the chunks received so far")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();
        }
    }
}
