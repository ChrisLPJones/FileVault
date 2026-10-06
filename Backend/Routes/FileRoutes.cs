using Backend.Services;
using System.Security.Claims;
using Backend.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Backend.Routes
{
    public static class FileRoutes
    {
        // Error response using the result's status code (400 by default)
        private static IResult Error(HttpReturnResult result) =>
            Results.Json(new { error = result.Message }, statusCode: result.StatusCode ?? 400);

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
                try
                {
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    if (!request.HasFormContentType)
                        return Results.BadRequest(new { error = "Expected form-data content type" });

                    var form = await request.ReadFormAsync();
                    var parentId = form["parentId"].FirstOrDefault() ?? "";
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    if (file == null || file.Length == 0)
                        return Results.BadRequest(new { error = "No file uploaded" });

                    var mimeType = file.ContentType;

                    var result = await fs.UploadFile(file, db, userId, parentId, mimeType);

                    return result.Success
                        ? Results.Ok(new { success = $"File Uploaded: {result.FileName}" })
                        : Results.BadRequest(new { error = $"File not saved: {result.Message}" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Upload error: {ex}");
                    return Results.BadRequest(new { error = "Upload failed" });
                }
            }).RequireAuthorization();



            // Create folder metadata in the database
            app.MapPost("/folder", async (
                ClaimsPrincipal user,
                [FromBody] FolderModel request,
                FileServices fs,
                DatabaseServices db) =>
            {
                try
                {
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (userId == null) return Results.Unauthorized();

                    if (string.IsNullOrWhiteSpace(request.Name))
                        return Results.BadRequest(new { error = "Folder name is required" });

                    var result = await fs.CreateFolder(request, db, userId);

                    return result.Success
                    ? Results.Ok(result.Folder)
                    : Error(result);

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Create folder error: {ex}");
                    return Results.BadRequest(new { error = "Error creating folder" });
                }
            }).RequireAuthorization();



            // Returns a list of all files stored for the authenticated user
            app.MapGet("/files", (
                DatabaseServices db,
                ClaimsPrincipal user) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var files = db.GetFilesFromDb(userId);

                return Results.Ok(files);
            }).RequireAuthorization();






            // Streams a file belonging to the authenticated user (supports range requests)
            app.MapGet("/download/{fileId}", async (
                ClaimsPrincipal user,
                string fileId,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var (error, file, fullPath) = await fs.GetDownloadAsync(fileId, db, userId);
                if (error != null)
                    return Error(error);

                return Results.File(
                    path: fullPath,
                    contentType: string.IsNullOrEmpty(file.MimeType) ? "application/octet-stream" : file.MimeType,
                    fileDownloadName: file.Name,
                    enableRangeProcessing: true
                );
            }).RequireAuthorization();



            // Downloads several files and/or folders as a single zip
            app.MapPost("/download/zip", async (
                ClaimsPrincipal user,
                [FromBody] ZipDownloadRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var (error, stream, fileName) = await fs.CreateZipAsync(request?.Ids, db, userId);
                if (error != null)
                    return Error(error);

                return Results.File(stream, "application/zip", fileName);
            }).RequireAuthorization();



            // Renames a file or folder
            app.MapPatch("/rename", async (
                ClaimsPrincipal user,
                [FromBody] RenameRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var result = await fs.Rename(request, db, userId);
                return result.Success ? Results.Ok(new { success = result.Message }) : Error(result);
            }).RequireAuthorization();



            // Moves files/folders into another folder (destinationId null = root)
            app.MapPut("/move", async (
                ClaimsPrincipal user,
                [FromBody] TransferRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var result = await fs.Move(request, db, userId);
                return result.Success ? Results.Ok(new { success = result.Message }) : Error(result);
            }).RequireAuthorization();



            // Copies files/folders into another folder (destinationId null = root)
            app.MapPost("/copy", async (
                ClaimsPrincipal user,
                [FromBody] TransferRequest request,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var result = await fs.Copy(request, db, userId);
                return result.Success ? Results.Ok(new { success = result.Message }) : Error(result);
            }).RequireAuthorization();



            // Deletes a specific file and its metadata for the authenticated user
            app.MapDelete("/delete/{fileId}", async (
                ClaimsPrincipal user,
                FileServices fs,
                string fileId,
                DatabaseServices db) =>
                {
                    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    var result = await fs.DeleteFile(fileId, db, userId);

                    return result.Success
                        ? Results.Ok(new { success = result.Message })
                        : Results.BadRequest(new { error = result.Message });
                }).RequireAuthorization();

            // Delete multiple files and it metadata for the authenticated user
            app.MapDelete("/delete", async (
                ClaimsPrincipal user,
                FileServices fs,
                [FromBody] IsList request,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                List<string> ids = new();

                if (request?.ids.ValueKind == JsonValueKind.String)
                {
                    ids.Add(request.ids.GetString());
                }
                else if (request?.ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in request.ids.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.String)
                            return Results.BadRequest(new { error = "Invalid ids format" });

                        ids.Add(element.GetString());
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
            }).RequireAuthorization();
        }
    }
}
