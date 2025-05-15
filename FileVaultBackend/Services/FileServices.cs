namespace FileVaultBackend.Services;

public class FileServices
{
    // Root directory for file storage, injected through configuration
    private readonly string _storageRoot;

    // Constructor to initialize the FileService with the storage root path from the configuration
    public FileServices(IConfiguration config)
    {
        _storageRoot = config.GetValue<string>("StorageRoot");
    }





    // Method to download a file from the storage
    internal async Task<IResult> DownloadFile(string fileName, HttpContext context, DatabaseServices database)
    {
        // Sanitize the filename and get the full path to the file
        var sanitizedFilename = Path.GetFileName(fileName);
        await database.CheckConnection();
        var fileGUID = await database.GetFileGUIDAsync(sanitizedFilename);
        var fullFilePath = Path.Combine(_storageRoot, fileGUID);

        // If the file doesn't exist, return a 404 error
        if (!File.Exists(fullFilePath))
        {
            context.Response.StatusCode = 404;
            return Results.NotFound("File not found.");
        }

        // Set the response content type and headers for downloading the file
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename={sanitizedFilename}");

        // Send the file content to the response stream
        await context.Response.SendFileAsync(fullFilePath);
        return Results.Empty;
    }






    // Method to upload a file to the storage
    public async Task<IResult> UploadFile(HttpRequest request, DatabaseServices database)
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

        // Get the file name
        var fileName = Path.GetFileName(file.FileName);
        // Create guid for file
        var guid = Guid.NewGuid().ToString();

        // Generate a unique filename for the file and combine it with the storage root path
        var fullFilePath = Path.Combine(_storageRoot, guid);

        try
        {
            // Save the uploaded file to the local storage location
            using (var stream = new FileStream(fullFilePath, FileMode.Create))
                await file.CopyToAsync(stream);

            // Add metadata to Database
            await database.AddFile(fileName, guid);

            // Return a success response with the file's path
            return Results.Ok($"File Uploaded: {fileName}");
        }
        catch (Exception)
        {
            if (File.Exists(fullFilePath))
                File.Delete(fullFilePath);

            return Results.BadRequest($"Error: File not saved.");
        }
    }






    // Method to delete a file from the storage
    public async Task<IResult> DeleteFile(string fileName, DatabaseServices database)
    {
        // Sanitize the filename and get the full path to the file
        var sanitizedFilename = Path.GetFileName(fileName);

        string fileGuid = await database.GetFileGUIDAsync(sanitizedFilename);
        if (fileGuid == null)
        {
            return Results.NotFound("Error: File not found.");
        }

        var fullFilePath = Path.Combine(_storageRoot, fileGuid);


        // If the file doesn't exist, return a bad request error
        if (!File.Exists(fullFilePath))
            return Results.NotFound("Error: File doesn't exist.");

        try
        {
            // Delete the file from the storage
            await database.DeleteFileMetadata(fileName);
            File.Delete(fullFilePath);
            return Results.Ok($"File deleted: {fileName}");
        }
        catch (Exception)
        {
            return Results.BadRequest("Error: File delete failed.");
        }
    }






    // Method to get a list of all files in the storage
    public List<string> GetFileList()
    {
        // Get the list of all file names from the storage directory
        var files = Directory.GetFiles(_storageRoot).Select(Path.GetFileName).ToList();
        return files;
    }
}
