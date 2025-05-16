using FileVaultBackend.Services;
using System.Runtime.CompilerServices;

namespace FileVaultBackend.Routes
{
    public static class FileRoutes
    { 
        public static void MapFileRoutes(this IEndpointRouteBuilder app)
        {
            // Map the /upload route to handle file uploads from the client
            app.MapPost("/upload", async (HttpRequest request, FileServices fs, DatabaseServices db) =>
            {
                try
                {
                    // Check if the request has the correct form content type
                    if (!request.HasFormContentType)
                        return Results.BadRequest("Error: Expected Form Data.");

                    // Read the form data from the request
                    var form = await request.ReadFormAsync();
                    // Get the first file from the form data
                    var file = form.Files.FirstOrDefault();

                    if (file == null || file.Length == 0)
                        return Results.BadRequest("Error: No file uploaded.");

                    var (success, message, originalFileName) = await fs.UploadFile(file, db);

                    return success
                        ? Results.Ok($"File Uploaded: {originalFileName}")
                        : Results.BadRequest($"Error: File not saved: {message}");
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(ex.Message);
                }
            });







            // Map the /download/{fileName} route to handle file downloads
            app.MapGet("/download/{fileName}", async (string fileName, HttpContext context, FileServices file, DatabaseServices db) =>
            {
                return await file.DownloadFile(fileName, context, db);
            });







            // Map the /delete/{fileName} route to handle file deletions
            app.MapDelete("/delete/{fileName}", (FileServices file, string fileName, DatabaseServices db) =>
            {
                return file.DeleteFile(fileName, db);
            });







            // Map the /files route to return a list of files in storage
            app.MapGet("/files", (FileServices file, DatabaseServices db) =>
            {
                return db.GetFilesFromDb();
            });
        }
    }
}