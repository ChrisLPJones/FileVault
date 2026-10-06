using Backend.Models;
using System.IO.Compression;

namespace Backend.Services;

public class FileServices(IConfiguration config)
{
    private readonly string _storageRoot = config.GetValue<string>("StorageRoot");

    // Uploads a file, saves it to disk, and stores metadata in the database
    public async Task<HttpReturnResult> UploadFile(IFormFile file, DatabaseServices db, string userId, string parentId, string mimeType)
    {
        var fileName = Path.GetFileName(file.FileName); // Get original filename
        var guid = Guid.NewGuid().ToString(); // Generate unique ID for storage
        int isDirectory = 0;
        var filePath = $"/{fileName}";
        if (!string.IsNullOrEmpty(parentId))
        {
            var folder = await db.GetFolderById(parentId, userId);
            if (folder == null)
                return new HttpReturnResult(false, "Parent folder not found");

            filePath = $"{folder.Path}/{fileName}";
        }

        var fullFilePath = Path.Combine(_storageRoot, guid); // Path to save file

        try
        {
            // Save file to disk
            await using (var stream = new FileStream(fullFilePath, FileMode.Create))
                await file.CopyToAsync(stream);

            var fileInfo = new FileInfo(fullFilePath);

            long size = fileInfo.Length;

            // Add file metadata to database
            await db.AddFile(fileName, isDirectory, filePath, guid, userId, size, parentId, mimeType);

            return new HttpReturnResult(true, null, fileName); // Success
        }
        catch (Exception ex)
        {
            // If the save or the metadata insert failed, delete the stored file
            if (File.Exists(fullFilePath))
                File.Delete(fullFilePath);

            Console.WriteLine($"Upload error: {ex}");
            return new HttpReturnResult(false, "Error saving file"); // Failure
        }
    }


    public async Task<HttpReturnResult> CreateFolder(FolderModel request, DatabaseServices db, string userId)
    {
        request.Name = request.Name?.Trim();
        var nameError = ValidateName(request.Name);
        if (nameError != null)
            return new HttpReturnResult(false, nameError);

        request._id = Guid.NewGuid().ToString(); // Unique ID
        request.IsDirectory = true;
        request.UserId = userId;
        if (string.IsNullOrEmpty(request.ParentId))
            request.ParentId = null;

        string parentPath = "";
        if (request.ParentId != null)
        {
            // Get parent folder from DB
            var parentFolder = await db.GetFolderById(request.ParentId, userId);
            if (parentFolder == null)
                return new HttpReturnResult(false, "Parent folder not found");

            parentPath = parentFolder.Path;
        }

        if ((await db.GetNamesInFolderAsync(request.ParentId, userId)).Contains(request.Name))
            return HttpReturnResult.Conflict($"An item named \"{request.Name}\" already exists here");

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
            Console.WriteLine($"Create folder error: {ex}");
            return new HttpReturnResult(false, "Error creating folder");
        }
    }



    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    // Returns an error message if the name can't be used for a file or folder
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is required";
        if (name.Length > 255)
            return "Name must be 255 characters or fewer";
        if (name == "." || name == "..")
            return "Name is not allowed";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
            return "Name can't contain \\ / : * ? \" < > |";
        return null;
    }

    // Absolute path of a stored file on disk
    private string StoredPath(string guid) => Path.GetFullPath(Path.Combine(_storageRoot, guid));

    // Path of the parent folder for a stored item path ("/a/b.txt" -> "/a", "/b.txt" -> "")
    private static string ParentPath(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];

    // Resolve a destination folder; null/empty id means the root
    private static async Task<(HttpReturnResult error, string id, string path)> ResolveDestinationAsync(
        string destinationId, DatabaseServices db, string userId)
    {
        if (string.IsNullOrEmpty(destinationId))
            return (null, null, "");

        var folder = await db.GetFolderById(destinationId, userId);
        return folder == null
            ? (HttpReturnResult.NotFound("Destination folder not found"), null, null)
            : (null, folder._id, folder.Path);
    }

    // "report.pdf" -> "report (1).pdf" until the name is free
    private static string GetUniqueName(string name, bool isDirectory, HashSet<string> taken)
    {
        if (!taken.Contains(name))
            return name;

        var dot = isDirectory ? -1 : name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : "";

        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }



    // Get the on-disk path of a file for download
    public async Task<(HttpReturnResult error, FileRecord file, string fullPath)> GetDownloadAsync(
        string fileId, DatabaseServices db, string userId)
    {
        if (!Guid.TryParse(fileId, out _))
            return (HttpReturnResult.NotFound("File not found."), null, null);

        var file = await db.GetItemAsync(fileId, userId);
        if (file == null || file.IsDirectory)
            return (HttpReturnResult.NotFound("File not found."), null, null);

        var fullPath = StoredPath(file.Guid);
        if (!File.Exists(fullPath))
            return (HttpReturnResult.NotFound("File not found."), null, null);

        return (null, file, fullPath);
    }



    // Build a zip of the given files/folders in a temp file that is deleted when the stream closes
    public async Task<(HttpReturnResult error, Stream stream, string fileName)> CreateZipAsync(
        List<string> ids, DatabaseServices db, string userId)
    {
        if (ids == null || ids.Count == 0)
            return (new HttpReturnResult(false, "No items selected"), null, null);

        var trees = new List<List<FileRecord>>();
        foreach (var id in ids.Distinct())
        {
            var tree = Guid.TryParse(id, out _) ? await db.GetTreeAsync(id, userId) : [];
            if (tree.Count == 0)
                return (HttpReturnResult.NotFound("File not found."), null, null);
            trees.Add(tree);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"filevault-{Guid.NewGuid()}.zip");
        var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var topLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tree in trees)
                {
                    var root = tree[0];
                    var rootName = GetUniqueName(root.Name, root.IsDirectory, topLevelNames);
                    topLevelNames.Add(rootName);

                    foreach (var item in tree)
                    {
                        var entryName = item == root ? rootName : rootName + item.Path[root.Path.Length..];

                        if (item.IsDirectory)
                        {
                            zip.CreateEntry(entryName + "/");
                        }
                        else
                        {
                            var source = StoredPath(item.Guid);
                            if (!File.Exists(source))
                                throw new FileNotFoundException($"Stored file missing for {item.Guid}");

                            zip.CreateEntryFromFile(source, entryName, CompressionLevel.Fastest);
                        }
                    }
                }
            }

            stream.Position = 0;
            var fileName = trees.Count == 1 && trees[0][0].IsDirectory ? $"{trees[0][0].Name}.zip" : "FileVault.zip";
            return (null, stream, fileName);
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync();
            Console.WriteLine($"Zip error: {ex}");
            return (new HttpReturnResult(false, "Error creating zip") { StatusCode = 500 }, null, null);
        }
    }



    // Rename a file or folder; folder renames update the paths of everything inside
    public async Task<HttpReturnResult> Rename(RenameRequest request, DatabaseServices db, string userId)
    {
        var newName = request?.NewName?.Trim();
        var nameError = ValidateName(newName);
        if (nameError != null)
            return new HttpReturnResult(false, nameError);

        var item = Guid.TryParse(request.Id, out _) ? await db.GetItemAsync(request.Id, userId) : null;
        if (item == null)
            return HttpReturnResult.NotFound("File not found.");

        if (item.Name == newName)
            return new HttpReturnResult(true, "Name unchanged");

        if ((await db.GetNamesInFolderAsync(item.ParentId, userId, item.Guid)).Contains(newName))
            return HttpReturnResult.Conflict($"An item named \"{newName}\" already exists here");

        try
        {
            await db.RelocateAsync(item, item.ParentId, newName, $"{ParentPath(item.Path)}/{newName}", userId);
            return new HttpReturnResult(true, $"Renamed to {newName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Rename error: {ex}");
            return new HttpReturnResult(false, "Error renaming item") { StatusCode = 500 };
        }
    }



    // Move files/folders into a destination folder (null = root)
    public async Task<HttpReturnResult> Move(TransferRequest request, DatabaseServices db, string userId)
    {
        if (request?.SourceIds == null || request.SourceIds.Count == 0)
            return new HttpReturnResult(false, "No items selected");

        var (destError, destId, destPath) = await ResolveDestinationAsync(request.DestinationId, db, userId);
        if (destError != null)
            return destError;

        // Validate everything before changing anything
        var takenNames = await db.GetNamesInFolderAsync(destId, userId);
        var toMove = new List<FileRecord>();

        foreach (var id in request.SourceIds.Distinct())
        {
            var item = Guid.TryParse(id, out _) ? await db.GetItemAsync(id, userId) : null;
            if (item == null)
                return HttpReturnResult.NotFound("File not found.");

            if (item.ParentId == destId)
                continue; // already there

            if (item.IsDirectory && destId != null &&
                (await db.GetTreeAsync(item.Guid, userId)).Any(t => t.Guid == destId))
                return new HttpReturnResult(false, $"Can't move \"{item.Name}\" into itself");

            if (!takenNames.Add(item.Name))
                return HttpReturnResult.Conflict($"An item named \"{item.Name}\" already exists in the destination");

            toMove.Add(item);
        }

        try
        {
            foreach (var item in toMove)
                await db.RelocateAsync(item, destId, item.Name, $"{destPath}/{item.Name}", userId);

            return new HttpReturnResult(true, $"Moved {toMove.Count} item(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Move error: {ex}");
            return new HttpReturnResult(false, "Error moving items") { StatusCode = 500 };
        }
    }



    // Copy files/folders (including stored files) into a destination folder (null = root)
    public async Task<HttpReturnResult> Copy(TransferRequest request, DatabaseServices db, string userId)
    {
        if (request?.SourceIds == null || request.SourceIds.Count == 0)
            return new HttpReturnResult(false, "No items selected");

        var (destError, destId, destPath) = await ResolveDestinationAsync(request.DestinationId, db, userId);
        if (destError != null)
            return destError;

        // Validate everything before copying anything
        var trees = new List<List<FileRecord>>();
        foreach (var id in request.SourceIds.Distinct())
        {
            var tree = Guid.TryParse(id, out _) ? await db.GetTreeAsync(id, userId) : [];
            if (tree.Count == 0)
                return HttpReturnResult.NotFound("File not found.");

            if (destId != null && tree.Any(t => t.Guid == destId))
                return new HttpReturnResult(false, $"Can't copy \"{tree[0].Name}\" into itself");

            trees.Add(tree);
        }

        var takenNames = await db.GetNamesInFolderAsync(destId, userId);
        var copiedFiles = new List<string>();

        try
        {
            foreach (var tree in trees)
            {
                var root = tree[0];
                var rootName = GetUniqueName(root.Name, root.IsDirectory, takenNames);
                takenNames.Add(rootName);
                var rootPath = $"{destPath}/{rootName}";

                var newGuids = tree.ToDictionary(t => t.Guid, _ => Guid.NewGuid().ToString());
                var newItems = tree.Select(t => new FileRecord
                {
                    Guid = newGuids[t.Guid],
                    Name = t == root ? rootName : t.Name,
                    IsDirectory = t.IsDirectory,
                    Path = t == root ? rootPath : rootPath + t.Path[root.Path.Length..],
                    ParentId = t == root ? destId : newGuids[t.ParentId],
                    Size = t.Size,
                    MimeType = t.MimeType
                }).ToList();

                foreach (var source in tree.Where(t => !t.IsDirectory))
                {
                    var target = StoredPath(newGuids[source.Guid]);
                    File.Copy(StoredPath(source.Guid), target);
                    copiedFiles.Add(newGuids[source.Guid]);
                }

                await db.InsertItemsAsync(newItems, userId);
            }

            return new HttpReturnResult(true, $"Copied {trees.Count} item(s)");
        }
        catch (Exception ex)
        {
            // Remove stored copies whose metadata was never committed
            var committed = new HashSet<string>();
            foreach (var guid in copiedFiles)
                if (await db.IsFileAsync(guid, userId) != null)
                    committed.Add(guid);
            await DeleteAllFilesFromUser(copiedFiles.Where(g => !committed.Contains(g)).ToList());

            Console.WriteLine($"Copy error: {ex}");
            return new HttpReturnResult(false, "Error copying items") { StatusCode = 500 };
        }
    }

    // Deletes a file or folder (including everything inside it) for the given user
    public async Task<HttpReturnResult> DeleteFile(string fileId, DatabaseServices db, string userId)
    {
        if (!Guid.TryParse(fileId, out _))
            return new HttpReturnResult(false, "Error: File not found.");

        if (await db.IsFileAsync(fileId, userId) == null)
            return new HttpReturnResult(false, "Error: File not found.");

        try
        {
            // Collect stored files first; the delete trigger removes child rows
            var storedFiles = await db.GetFileGuidsInTreeAsync(fileId, userId);

            await db.DeleteFileMetadata(fileId, userId);

            // Metadata is gone, so remove the stored files (missing ones are ignored)
            await DeleteAllFilesFromUser(storedFiles);

            return new HttpReturnResult(true, $"File deleted: {fileId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Delete error: {ex}");
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
