using Backend.Services;
using System.Security.Claims;
using Backend.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Runtime.Intrinsics.Arm;

namespace Backend.Routes
{
    public static class FileRoutes
    {        // Maps all file-related API endpoints
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
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    if (file == null || file.Length == 0)
                        return Results.BadRequest(new { error = "No file uploaded" });

                    var result = await fs.UploadFile(file, db, userId);

                    return result.Success
                        ? Results.Ok(new { success = $"File Uploaded: {result.FileName}" })
                        : Results.BadRequest(new { error = $"File not saved: {result.Message}" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"Upload failed: {ex.Message}" });
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
                    : Results.BadRequest(new { error = $"Folder not saved: {result.Message}" });

                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"{ex.Message}" });
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






            // Downloads a specific file belonging to the authenticated user
            app.MapGet("/download/{fileName}", async (
                ClaimsPrincipal user,
                string fileName,
                FileServices fs,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var result = await fs.DownloadFile(fileName, db, userId);

                if (!result.Success || result.FileContent == null || result.FileName == null)
                    return Results.BadRequest(new { error = result.Message });

                return Results.File(
                    fileContents: result.FileContent,
                    contentType: "application/octet-stream",
                    fileDownloadName: result.FileName
                );
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

                if (request.ids.ValueKind == JsonValueKind.String)
                {
                    ids.Add(request.ids.GetString());
                }
                else if (request.ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in request.ids.EnumerateArray())
                    {
                        ids.Add(element.GetString());
                    }
                }
                else
                {
                    return Results.BadRequest("Invalid ids format");
                }
                foreach (var fileId in ids)
                {
                    var result = await fs.DeleteFile(fileId, db, userId);
                    if (!result.Success)
                        return Results.BadRequest(new { error = result.Message });
                }

                return Results.Ok(new { success = "Deleted Successfully" });
            }).RequireAuthorization();
        }
    }
}
