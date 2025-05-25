using FileVaultBackend.Models;
using System.Security.Claims;

namespace FileVaultBackend.Services;

public class FileServices(IConfiguration config)
{
    // Root directory for file storage, injected through configuration
    private readonly string _storageRoot = config.GetValue<string>("StorageRoot");





    // Method to upload a file to the storage
    public async Task<HttpReturnResult> UploadFile(
        IFormFile file, 
        DatabaseServices db, 
        string userId)
    {
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
            await db.AddFile(fileName, guid, userId);

            // Return a success response with the file's path
            return new HttpReturnResult(true, null, fileName);
        }
        catch (Exception ex)
        {
            if (File.Exists(fullFilePath))
                File.Delete(fullFilePath);

            return new HttpReturnResult(false, $"Error: {ex.Message}");
        }
    }





    // Method to download a file from the storage
    internal async Task<HttpReturnResult> DownloadFile(
        string fileName, 
        DatabaseServices db, 
        string userId)
    {
        var sanitizedFilename = Path.GetFileName(fileName);
        await db.CheckConnection();

        var fileGUID = await db.GetFileGUIDAsync(sanitizedFilename, userId);
        if (fileGUID == null)
            return new HttpReturnResult(false, "File not found.");

        var fullFilePath = Path.Combine(_storageRoot, fileGUID);
        if (!File.Exists(fullFilePath))
            return new HttpReturnResult(false, "File not found.");

        // Read file into memory
        var fileBytes = await File.ReadAllBytesAsync(fullFilePath);
        return new HttpReturnResult(true, null, sanitizedFilename, fileBytes);
    }





    // Method to delete a file from the storage
    public async Task<HttpReturnResult> DeleteFile(
        string fileName, 
        DatabaseServices db,
        string userId)
    {
        // Sanitize the filename and get the full path to the file
        var sanitizedFilename = Path.GetFileName(fileName);

        string fileGuid = await db.GetFileGUIDAsync(sanitizedFilename, userId);
        if (fileGuid == null)
            return new HttpReturnResult(false, "Error: File not found.");

        var fullFilePath = Path.Combine(_storageRoot, fileGuid);

        // If the file doesn't exist, return a bad request error
        if (!File.Exists(fullFilePath))
            return new HttpReturnResult(false, "Error: File doesn't exist.");

        try
        {
            // Delete the file from the storage
            await db.DeleteFileMetadata(fileName, userId);
            File.Delete(fullFilePath);
            return new HttpReturnResult(true, $"File deleted: {fileName}");
        }
        catch (Exception)
        {
            return new HttpReturnResult(false, "Error: File delete failed.");
        }
    }

    internal void DeleteAllFilesFromUser(string userId)
    {
        // Delete All Users Files
    }
}




