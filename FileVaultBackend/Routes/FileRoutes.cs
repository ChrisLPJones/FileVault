using FileVaultBackend.Services;
using System.Runtime.CompilerServices;

namespace FileVaultBackend.Routes
{

    public static class FileRoutes
    { 





        public record HttpReturnResult(bool Success, string? Message, string? FileName, byte[]? FileContent);





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
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    if (file == null || file.Length == 0)
                        return Results.BadRequest("Error: No file uploaded.");

                    var result = await fs.UploadFile(file, db);


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
                    return Results.BadRequest(ex.Message);
                }
            });





            // Map the /files route to return a list of files in storage
            app.MapGet("/files", (DatabaseServices db) =>
            {
                return db.GetFilesFromDb();
            });





            app.MapGet("/download/{fileName}", async (string fileName, FileServices fs, DatabaseServices db) =>
            {
                var result = await fs.DownloadFile(fileName, db);

                if (!result.Success || result.FileContent == null || result.FileName == null)
                {
                    return Results.BadRequest(result.Message);
                }

                return Results.File(
                    fileContents: result.FileContent,
                    contentType: "application/octet-stream",
                    fileDownloadName: result.FileName
                );
            });





            // Map the /delete/{fileName} route to handle file deletions
            app.MapDelete("/delete/{fileName}", (FileServices fs, string fileName, DatabaseServices db) =>
            {
                return fs.DeleteFile(fileName, db);
            });





        }
    }
}