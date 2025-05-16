using FileVaultBackend.Services;
using System.Runtime.CompilerServices;

namespace FileVaultBackend.Routes
{
    public static class FileRoutes
    { 
        public static void MapFileRoutes(this IEndpointRouteBuilder app)
        {
            // Map the /upload route to handle file uploads from the client
            app.MapPost("/upload", async (HttpRequest request, FileServices file, DatabaseServices db) =>
            {
                return await file.UploadFile(request, db);
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