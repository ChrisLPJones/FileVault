using FileVaultBackend.Services;
using System.Security.Claims;

namespace FileVaultBackend.Routes
{
    public static class FileRoutes
    {
        // Extension method to map all file-related API routes
        public static void MapFileRoutes(this IEndpointRouteBuilder app)
        {





            // POST /upload - Handles file uploads from the client
            app.MapPost("/upload", async (
                ClaimsPrincipal user, 
                HttpRequest request, 
                FileServices fs, 
                DatabaseServices db) =>
            {
            try
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                    // Ensure request has form content type
                    if (!request.HasFormContentType)
                        return Results.BadRequest("Error: Expected Form Data.");

                    // Read form data
                    var form = await request.ReadFormAsync();
                    // Retrieve the first file from the form
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    // Validate file
                    if (file == null || file.Length == 0)
                        return Results.BadRequest("Error: No file uploaded.");

                    // Call service to upload file and store metadata
                    var result = await fs.UploadFile(file, db, userId);

                    // Return success or failure result
                    if (result.Success)
                    {
                        return Results.Ok($"File Uploaded: {result.FileName}");
                    }
                    else
                    {
                        return Results.BadRequest($"Error: File not saved: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    // Return any caught exception as a bad request
                    return Results.BadRequest(ex.Message);
                }
            }).RequireAuthorization();






            // GET /files - Returns list of all stored files from the database
            app.MapGet("/files", (
                DatabaseServices db, 
                ClaimsPrincipal user) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                return Results.Ok(db.GetFilesFromDb(userId));
            }).RequireAuthorization();






            // GET /download/{fileName} - Downloads a file by name
            app.MapGet("/download/{fileName}", async (
                ClaimsPrincipal user, 
                string fileName, 
                FileServices fs, 
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                // Attempt to retrieve file content and metadata
                var result = await fs.DownloadFile(fileName, db, userId);

                // Check if file retrieval was successful
                if (!result.Success || result.FileContent == null || result.FileName == null)
                {
                    return Results.BadRequest(result.Message);
                }

                // Return file as a download with appropriate headers
                return Results.File(
                    fileContents: result.FileContent,
                    contentType: "application/octet-stream",
                    fileDownloadName: result.FileName
                );
            }).RequireAuthorization();






            // DELETE /delete/{fileName} - Deletes a file and its metadata
            app.MapDelete("/delete/{fileName}", async (
                ClaimsPrincipal user,
                FileServices fs, 
                string fileName, 
                DatabaseServices db) =>
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                // Attempt to delete the file
                var result = await fs.DeleteFile(fileName, db, userId);

                // Return result of delete operation
                if (!result.Success)
                {
                    return Results.BadRequest(result.Message);
                }
                else
                {
                    return Results.Ok(result.Message);
                }
            }).RequireAuthorization();
        }
    }
}
