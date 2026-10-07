using Backend.Models;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;

namespace Backend.Routes
{
    // Unexpected errors are logged and turned into a 500 { error } response by
    // the exception handler in Program.cs.
    public static class FileRoutes
    {
        // Error response using the result's status code (400 by default)
        private static IResult Error(HttpReturnResult result) =>
            Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);

        private static IResult Success(HttpReturnResult result) =>
            result.Success ? Results.Ok(new { success = result.Message }) : Error(result);

        // Maps all file-related API endpoints
        public static void MapFileRoutes(this IEndpointRouteBuilder app)
        {
            // Uploads a file and stores metadata in the database
            app.MapPost("/upload", async (
                ClaimsPrincipal user,
                HttpRequest request,
                FileServices fs,
                DatabaseServices db) =>
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
                    // Body larger than the server's request size limit
                    return Results.Json(new { error = "File is too large to upload" }, statusCode: 413);
                }

                var parentId = form["parentId"].FirstOrDefault() ?? "";
                var file = form.Files.Count > 0 ? form.Files[0] : null;

                if (file == null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded" });

                var result = await fs.UploadFile(file, db, user.GetUserId(), parentId, file.ContentType);

                return result.Success
                    ? Results.Ok(new { success = $"File Uploaded: {result.FileName}" })
                    : Error(result);
            })
                .WithTags("Files")
                .WithSummary("Upload a file (multipart form: file, optional parentId)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(413)
                .WithMetadata(new MultipartUploadMetadata()).RequireAuthorization();

            // Create folder metadata in the database
            app.MapPost("/folder", async (
                ClaimsPrincipal user,
                [FromBody] FolderModel request,
                FileServices fs,
                DatabaseServices db) =>
            {
                if (string.IsNullOrWhiteSpace(request?.Name))
                    return Results.BadRequest(new { error = "Folder name is required" });

                var result = await fs.CreateFolder(request, db, user.GetUserId());
                return result.Success ? Results.Ok(result.Folder) : Error(result);
            })
                .WithTags("Files")
                .WithSummary("Create a folder")
                .Produces<FolderModel>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(409).RequireAuthorization();

            // Returns a list of all files stored for the authenticated user
            app.MapGet("/files", async (
                ClaimsPrincipal user,
                DatabaseServices db) =>
            {
                return Results.Ok(await db.GetFilesFromDb(user.GetUserId()));
            })
                .WithTags("Files")
                .WithSummary("List every file and folder the user owns")
                .Produces<List<FileModel>>().RequireAuthorization();

            // Storage used, quota and upload size limit for the authenticated user
            app.MapGet("/user/usage", async (
                ClaimsPrincipal user,
                FileServices fs,
                DatabaseServices db) =>
            {
                return Results.Ok(await fs.GetUsageAsync(db, user.GetUserId()));
            })
                .WithTags("Account")
                .WithSummary("Storage used, storage quota and the upload size limit (bytes)")
                .Produces<StorageUsage>().RequireAuthorization();

            // Streams a file belonging to the authenticated user (supports range requests)
            app.MapGet("/download/{fileId}", async (
                ClaimsPrincipal user,
                string fileId,
                FileServices fs,
                DatabaseServices db) =>
            {
                var download = await fs.GetDownloadAsync(fileId, db, user.GetUserId());
                if (!download.Ok)
                    return Error(download.Error);

                var (file, stream) = download.Value;
                return Results.File(
                    fileStream: stream,
                    contentType: string.IsNullOrEmpty(file.MimeType) ? "application/octet-stream" : file.MimeType,
                    fileDownloadName: file.Name,
                    enableRangeProcessing: true
                );
            })
                .WithTags("Files")
                .WithSummary("Download a file (supports Range requests)")
                .Produces(200, contentType: "application/octet-stream")
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Downloads several files and/or folders as a single zip
            app.MapPost("/download/zip", async (
                ClaimsPrincipal user,
                [FromBody] ZipDownloadRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                var zip = await fs.CreateZipAsync(request?.Ids, db, user.GetUserId());
                if (!zip.Ok)
                    return Error(zip.Error);

                return Results.File(zip.Value.Stream, "application/zip", zip.Value.FileName);
            })
                .WithTags("Files")
                .WithSummary("Download files and folders as one zip")
                .Produces(200, contentType: "application/zip")
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Renames a file or folder
            app.MapPatch("/rename", async (
                ClaimsPrincipal user,
                [FromBody] RenameRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                return Success(await fs.Rename(request, db, user.GetUserId()));
            })
                .WithTags("Files")
                .WithSummary("Rename a file or folder")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(409).RequireAuthorization();

            // Moves files/folders into another folder (destinationId null = root)
            app.MapPut("/move", async (
                ClaimsPrincipal user,
                [FromBody] TransferRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                return Success(await fs.Move(request, db, user.GetUserId()));
            })
                .WithTags("Files")
                .WithSummary("Move files/folders into a folder (omit destinationId for the root)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(409).RequireAuthorization();

            // Copies files/folders into another folder (destinationId null = root)
            app.MapPost("/copy", async (
                ClaimsPrincipal user,
                [FromBody] TransferRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                return Success(await fs.Copy(request, db, user.GetUserId()));
            })
                .WithTags("Files")
                .WithSummary("Copy files/folders into a folder (omit destinationId for the root)")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(404)
                .Produces<ErrorResponse>(413).RequireAuthorization();

            // Deletes a file or folder (and everything inside it) for the authenticated user
            app.MapDelete("/delete/{fileId}", async (
                ClaimsPrincipal user,
                string fileId,
                FileServices fs,
                DatabaseServices db) =>
            {
                return Success(await fs.DeleteFile(fileId, db, user.GetUserId()));
            })
                .WithTags("Files")
                .WithSummary("Delete a file or folder and everything inside it")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400).RequireAuthorization();

            // Deletes several files/folders for the authenticated user
            app.MapDelete("/delete", async (
                ClaimsPrincipal user,
                [FromBody] DeleteRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.GetUserId();
                List<string> ids = new();

                if (request?.ids.ValueKind == JsonValueKind.String)
                {
                    if (request.ids.GetString() is string id)
                        ids.Add(id);
                }
                else if (request?.ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in request.ids.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.String)
                            return Results.BadRequest(new { error = "Invalid ids format" });

                        ids.Add(element.GetString() ?? "");
                    }
                }
                else
                {
                    return Results.BadRequest(new { error = "Invalid ids format" });
                }

                var failed = new List<string>();
                foreach (var fileId in ids.Distinct())
                {
                    // Skip items already removed, e.g. a child of a folder deleted earlier in this request
                    if (await db.IsFileAsync(fileId, userId) == null)
                        continue;

                    var result = await fs.DeleteFile(fileId, db, userId);
                    if (!result.Success)
                        failed.Add(fileId);
                }

                if (failed.Count > 0)
                    return Results.Json(new { error = "Some items could not be deleted", failed }, statusCode: 500);

                return Results.Ok(new { success = "Deleted Successfully" });
            })
                .WithTags("Files")
                .WithSummary("Delete several files/folders (body: { ids: [...] })")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400)
                .Produces<ErrorResponse>(500).RequireAuthorization();
        }
    }
}
