using Backend.Services;
using System.Security.Claims;

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





            // Returns a list of all files stored for the authenticated user
            app.MapGet("/files", (
                DatabaseServices db,
                ClaimsPrincipal user) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return Results.Ok(new { success = db.GetFilesFromDb(userId) });
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
            app.MapDelete("/delete/{fileName}", async (
                ClaimsPrincipal user,
                FileServices fs,
                string fileName,
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var result = await fs.DeleteFile(fileName, db, userId);

                return result.Success
                    ? Results.Ok(new { success = result.Message })
                    : Results.BadRequest(new { error = result.Message });
            }).RequireAuthorization();
        }
    }
}
