using FileVaultBackend.Services;

namespace FileVaultBackend
{
    public partial class Program
    {
        private static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddScoped<FileServices>();
            builder.Services.AddScoped<DatabaseServices>();

            var app = builder.Build();

            // Map the /ping route, which returns "Pong" to indicate the app is alive
            app.MapGet("/ping", () =>
            {
                // await sql.CheckConnection();
                return Results.Ok("Pong");
            });

            // Map the /pingsql rout, which return "Pong" to indicate the Sql Server successfully connected
            app.MapGet("/pingsql", async (HttpRequest request, DatabaseServices database) =>
            {
                return await database.CheckConnection();
            });

            // Map the /upload route to handle file uploads from the client
            app.MapPost("/upload", async (HttpRequest request, FileServices file, DatabaseServices database) =>
            {
                return await file.UploadFile(request, database);
            });

            // Map the /download/{fileName} route to handle file downloads
            app.MapGet("/download/{fileName}", async (string fileName, HttpContext context, FileServices file, DatabaseServices database) =>
            {
                return await file.DownloadFile(fileName, context, database);
            });

            // Map the /delete/{fileName} route to handle file deletions
            app.MapDelete("/delete/{fileName}", (FileServices file, string fileName, DatabaseServices database) =>
            {
                return file.DeleteFile(fileName, database);
            });

            // Map the /files route to return a list of files in storage
            app.MapGet("/files", (FileServices file, DatabaseServices database) =>
            {
                return database.GetFilesFromDb();
            });


            // Ensure the storage directory exists
            var _storageRoot = builder.Configuration.GetValue<string>("StorageRoot");
            Directory.CreateDirectory(_storageRoot);

            // Start the web application
            app.Run();
        }
    }
}