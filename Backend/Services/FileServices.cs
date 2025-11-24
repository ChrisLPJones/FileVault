using Backend.Models;
using Microsoft.VisualBasic;

namespace Backend.Services;

public class FileServices(IConfiguration config)
{
    private readonly string _storageRoot = config.GetValue<string>("StorageRoot");

    // Uploads a file, saves it to disk, and stores metadata in the database
    public async Task<HttpReturnResult> UploadFile(IFormFile file, DatabaseServices db, string userId, string parentId)
    {
        var fileName = Path.GetFileName(file.FileName); // Get original filename
        var guid = Guid.NewGuid().ToString(); // Generate unique ID for storage
        int isDirectory = 0;
        var filePath = $"/{fileName}";
        if (!string.IsNullOrEmpty(parentId))
        {
            var folder = await db.GetFolderById(parentId);
            filePath = $"{folder.Path}/{fileName}"; 
        }

        var fullFilePath = Path.Combine(_storageRoot, guid); // Path to save file
        System.Console.WriteLine(file);

        try
        {
            // Save file to disk
            await using (var stream = new FileStream(fullFilePath, FileMode.Create))
                await file.CopyToAsync(stream);

            var fileInfo = new FileInfo(fullFilePath);

            long size = fileInfo.Length;

            // Add file metadata to database
            await db.AddFile(fileName, isDirectory, filePath, guid, userId, size, parentId);

            return new HttpReturnResult(true, null, fileName); // Success
        }
        catch (Exception ex)
        {
            // If save failed, delete partial file
            if (File.Exists(fullFilePath))
                File.Delete(fullFilePath);

            return new HttpReturnResult(false, $"Error: {ex.Message}"); // Failure
        }
    }


    public async Task<HttpReturnResult> CreateFolder(FolderModel request, DatabaseServices db, string userId)
    {
        request._id = Guid.NewGuid().ToString(); // Unique ID
        request.IsDirectory = true;
        request.UserId = userId;

        string parentPath = "";
        if (!string.IsNullOrEmpty(request.ParentId))
        {
            // Get parent folder from DB
            var parentFolder = await db.GetFolderById(request.ParentId);
            if (parentFolder == null)
                return new HttpReturnResult(false, "Parent folder not found");

            parentPath = parentFolder.Path;
        }

        request.Path = $"{parentPath}/{request.Name}".Replace("//", "/"); // construct full path
        request.Size = 0;
        request.MimeType = "";

        try
        {
            await db.AddFolder(request);
            return new HttpReturnResult(true, null, request);
        }
        catch (Exception ex)
        {
            return new HttpReturnResult(false, $"Error: {ex.Message}");
        }
    }



    // Downloads a file by filename for the specified user
    public async Task<HttpReturnResult> DownloadFile(string fileName, DatabaseServices db, string userId)
    {
        var sanitizedFilename = Path.GetFileName(fileName); // Sanitize input
        await db.CheckConnection();

        var fileGuid = await db.GetFileGUIDAsync(sanitizedFilename, userId); // Lookup GUID
        if (fileGuid == null)
            return new HttpReturnResult(false, "File not found.");

        var fullFilePath = Path.Combine(_storageRoot, fileGuid);
        if (!File.Exists(fullFilePath))
            return new HttpReturnResult(false, "File not found.");

        var fileBytes = await File.ReadAllBytesAsync(fullFilePath); // Read bytes
        return new HttpReturnResult(true, null, sanitizedFilename, fileBytes); // Return file content
    }

    // Deletes a file and its metadata for the given user
    public async Task<HttpReturnResult> DeleteFile(string fileId, DatabaseServices db, string userId)
    {
        var sanitizedFilename = Path.GetFileName(fileId); // Sanitize input

        string fileGuid = await db.GetFileGUIDAsync(sanitizedFilename, userId);
        if (fileGuid == null)
            return new HttpReturnResult(false, "Error: File not found.");

        var fullFilePath = Path.Combine(_storageRoot, fileGuid);

        if (!File.Exists(fullFilePath))
            return new HttpReturnResult(false, "Error: File doesn't exist.");

        try
        {
            // Remove metadata and delete file from storage
            await db.DeleteFileMetadata(fileId, userId);
            File.Delete(fullFilePath);
            return new HttpReturnResult(true, $"File deleted: {fileId}");
        }
        catch (Exception)
        {
            return new HttpReturnResult(false, "Error: File delete failed.");
        }
    }

    // Deletes all files from the user based on a list of file GUIDs
    public async Task DeleteAllFilesFromUser(List<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                var fullPath = Path.Combine(_storageRoot, file);

                if (File.Exists(fullPath))
                    await Task.Run(() => File.Delete(fullPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting file {file}: {ex.Message}");
            }
        }
    }
}
