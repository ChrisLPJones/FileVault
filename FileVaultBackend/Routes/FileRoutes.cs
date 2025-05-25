using FileVaultBackend.Services;
using System.Security.Claims;

namespace FileVaultBackend.Routes
{
    public static class FileRoutes
    {
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
                        return Results.BadRequest("Error: Expected form-data content type.");

                    var form = await request.ReadFormAsync();
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    if (file == null || file.Length == 0)
                        return Results.BadRequest("Error: No file uploaded.");

                    var result = await fs.UploadFile(file, db, userId);

                    return result.Success
                        ? Results.Ok($"File Uploaded: {result.FileName}")
                        : Results.BadRequest($"Error: File not saved: {result.Message}");
                }
                catch (Exception ex)
                {
                    return Results.BadRequest($"Upload failed: {ex.Message}");
                }
            }).RequireAuthorization();

            // Returns a list of all files stored for the authenticated user
            app.MapGet("/files", (
                DatabaseServices db,
                ClaimsPrincipal user) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return Results.Ok(db.GetFilesFromDb(userId));
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
                    return Results.BadRequest(result.Message);

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
                    ? Results.Ok(result.Message)
                    : Results.BadRequest(result.Message);
            }).RequireAuthorization();
        }
    }
}
