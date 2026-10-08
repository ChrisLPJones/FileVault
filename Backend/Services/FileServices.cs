using Backend.Models;
using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Backend.Services;

public class FileServices(IConfiguration config, FileEncryption encryption, ILogger<FileServices> logger)
{
    private readonly string _storageRoot = config.GetValue<string>("StorageRoot")
        ?? throw new InvalidOperationException("StorageRoot is not set.");

    public const long DefaultQuotaBytes = 1L * 1024 * 1024 * 1024; // 1 GB
    public const long DefaultMaxUploadBytes = 100L * 1024 * 1024;  // 100 MB

    public static long MaxUploadBytes(IConfiguration config) =>
        config.GetValue("Storage:MaxUploadBytes", DefaultMaxUploadBytes);

    // How much the user has stored, their quota (per-user override or the default) and the upload size limit
    public async Task<StorageUsage> GetUsageAsync(DatabaseServices db, string userId)
    {
        var (used, quota) = await db.GetStorageUsageAsync(userId);
        return new StorageUsage(used, quota ?? config.GetValue("Storage:DefaultQuotaBytes", DefaultQuotaBytes), MaxUploadBytes(config));
    }

    public static readonly string[] DefaultFolderNames = ["Documents", "Pictures", "Music", "Videos"];

    // Give a new account the usual starter folders (Storage:DefaultFolders / Storage:CreateDefaultFolders).
    // Failures are logged, not thrown, so they never block registration.
    public async Task CreateDefaultFoldersAsync(DatabaseServices db, string userId)
    {
        if (!config.GetValue("Storage:CreateDefaultFolders", true))
            return;

        var names = config.GetSection("Storage:DefaultFolders").Get<string[]>() ?? DefaultFolderNames;
        try
        {
            var existing = await db.GetNamesInFolderAsync(null, userId);
            foreach (var name in names.Where(n => !existing.Contains(n)))
            {
                var result = await CreateFolder(new FolderModel { Name = name }, db, userId);
                if (!result.Success)
                    logger.LogWarning("Could not create default folder {Folder} for user {UserId}: {Error}", name, userId, result.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating default folders failed for user {UserId}", userId);
        }
    }

    // Returns an error if adding `bytes` would exceed the user's quota
    private async Task<HttpReturnResult?> CheckQuotaAsync(DatabaseServices db, string userId, long bytes)
    {
        var usage = await GetUsageAsync(db, userId);
        if (usage.Used + bytes <= usage.Quota)
            return null;

        return new HttpReturnResult(false,
            $"Not enough storage space: {FormatBytes(usage.Quota - usage.Used)} free of {FormatBytes(usage.Quota)}")
        { StatusCode = 413 };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    // Open a stored file as plaintext. Rows without a key predate encryption and are read as-is.
    private Stream OpenStoredFile(FileRecord file)
    {
        var path = StoredPath(file.Guid);
        if (file.WrappedKey == null)
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

        var dataKey = encryption.UnwrapKey(file.WrappedKey, file.Guid);
        try
        {
            return FileEncryption.OpenDecryptedRead(path, dataKey, file.Size);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // A copied file reuses the same ciphertext, so re-wrap its data key for the new GUID
    private string? RewrapKey(string? wrappedKey, string oldGuid, string newGuid)
    {
        if (wrappedKey == null)
            return null;

        var dataKey = encryption.UnwrapKey(wrappedKey, oldGuid);
        try
        {
            return encryption.WrapKey(dataKey, newGuid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // Uploads a file, saves it to disk, and stores metadata in the database
    public async Task<HttpReturnResult> UploadFile(IFormFile file, DatabaseServices db, string userId, string parentId, string mimeType)
    {
        var maxUpload = MaxUploadBytes(config);
        if (file.Length > maxUpload)
            return new HttpReturnResult(false, $"File is larger than the {FormatBytes(maxUpload)} upload limit") { StatusCode = 413 };

        var quotaError = await CheckQuotaAsync(db, userId, file.Length);
        if (quotaError != null)
            return quotaError;

        var guid = Guid.NewGuid().ToString(); // Generate unique ID for storage
        int isDirectory = 0;
        var folderPath = "";
        if (!string.IsNullOrEmpty(parentId))
        {
            var folder = await db.GetFolderById(parentId, userId);
            if (folder == null)
                return new HttpReturnResult(false, "Parent folder not found");

            folderPath = folder.Path;
        }

        // A name already in the folder gets a number, like paste: "report.pdf" -> "report (1).pdf"
        var taken = await db.GetNamesInFolderAsync(string.IsNullOrEmpty(parentId) ? null : parentId, userId);
        var fileName = GetUniqueName(Path.GetFileName(file.FileName), false, taken);
        var filePath = $"{folderPath}/{fileName}";

        var fullFilePath = Path.Combine(_storageRoot, guid); // Path to save file
        var (dataKey, wrappedKey) = encryption.CreateDataKey(guid);

        try
        {
            // Encrypt the upload straight to disk
            long size;
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(fullFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                size = await FileEncryption.EncryptAsync(input, output, dataKey);

            // Add file metadata (including the wrapped key) to database
            await db.AddFile(fileName, isDirectory, filePath, guid, userId, size, parentId, mimeType, wrappedKey);

            return new HttpReturnResult(true, null, fileName); // Success
        }
        catch (Exception ex)
        {
            // If the save or the metadata insert failed, delete the stored file
            if (File.Exists(fullFilePath))
                File.Delete(fullFilePath);

            logger.LogError(ex, "Upload failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error saving file"); // Failure
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }


    public async Task<HttpReturnResult> CreateFolder(FolderModel request, DatabaseServices db, string userId)
    {
        var name = request.Name?.Trim();
        if (!TryValidateName(name, out var nameError))
            return new HttpReturnResult(false, nameError);
        request.Name = name;

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

        if ((await db.GetNamesInFolderAsync(request.ParentId, userId)).Contains(name))
            return HttpReturnResult.Conflict($"An item named \"{name}\" already exists here");

        request.Path = $"{parentPath}/{name}".Replace("//", "/"); // construct full path
        request.Size = 0;
        request.MimeType = "";

        try
        {
            await db.AddFolder(request);
            return new HttpReturnResult(true, null, request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating a folder failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error creating folder");
        }
    }



    private static readonly char[] InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    // True if the name can be used for a file or folder; otherwise `error` says why
    public static bool TryValidateName([NotNullWhen(true)] string? name, [NotNullWhen(false)] out string? error)
    {
        error = string.IsNullOrWhiteSpace(name) ? "Name is required"
            : name.Length > 255 ? "Name must be 255 characters or fewer"
            : name == "." || name == ".." ? "Name is not allowed"
            : name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl) ? "Name can't contain \\ / : * ? \" < > |"
            : null;
        return error == null;
    }

    // Absolute path of a stored file on disk
    private string StoredPath(string guid) => Path.GetFullPath(Path.Combine(_storageRoot, guid));

    // Path of the parent folder for a stored item path ("/a/b.txt" -> "/a", "/b.txt" -> "")
    private static string ParentPath(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];

    // A folder to move/copy into: Id null means the root (Path "")
    private sealed record Destination(string? Id, string Path);

    // Resolve a destination folder; null/empty id means the root
    private static async Task<ServiceResult<Destination>> ResolveDestinationAsync(
        string? destinationId, DatabaseServices db, string userId)
    {
        if (string.IsNullOrEmpty(destinationId))
            return ServiceResult<Destination>.Success(new Destination(null, ""));

        var folder = await db.GetFolderById(destinationId, userId);
        return folder == null
            ? HttpReturnResult.NotFound("Destination folder not found")
            : ServiceResult<Destination>.Success(new Destination(folder._id, folder.Path));
    }

    // "report.pdf" -> "report (1).pdf" until the name is free
    public static string GetUniqueName(string name, bool isDirectory, HashSet<string> taken)
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



    // Open a file for download as a seekable plaintext stream
    public async Task<ServiceResult<DownloadFile>> GetDownloadAsync(
        string fileId, DatabaseServices db, string userId)
    {
        if (!Guid.TryParse(fileId, out _))
            return HttpReturnResult.NotFound("File not found.");

        var file = await db.GetItemAsync(fileId, userId);
        if (file == null || file.IsDirectory || !File.Exists(StoredPath(file.Guid)))
            return HttpReturnResult.NotFound("File not found.");

        try
        {
            return ServiceResult<DownloadFile>.Success(new DownloadFile(file, OpenStoredFile(file)));
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Could not decrypt stored file {FileGuid}", file.Guid);
            return new HttpReturnResult(false, "File could not be decrypted") { StatusCode = 500 };
        }
    }



    // Build a zip of the given files/folders in a temp file that is deleted when the stream closes
    public async Task<ServiceResult<ZipDownload>> CreateZipAsync(
        List<string>? ids, DatabaseServices db, string userId)
    {
        if (ids == null || ids.Count == 0)
            return new HttpReturnResult(false, "No items selected");

        var trees = new List<List<FileRecord>>();
        foreach (var id in ids.Distinct())
        {
            var tree = Guid.TryParse(id, out _) ? await db.GetTreeAsync(id, userId) : [];
            if (tree.Count == 0)
                return HttpReturnResult.NotFound("File not found.");
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
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                            await using var source = OpenStoredFile(item);
                            await using var entryStream = entry.Open();
                            await source.CopyToAsync(entryStream);
                        }
                    }
                }
            }

            stream.Position = 0;
            var fileName = trees.Count == 1 && trees[0][0].IsDirectory ? $"{trees[0][0].Name}.zip" : "FileVault.zip";
            return ServiceResult<ZipDownload>.Success(new ZipDownload(stream, fileName));
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync();
            logger.LogError(ex, "Creating a zip failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error creating zip") { StatusCode = 500 };
        }
    }



    // Rename a file or folder; folder renames update the paths of everything inside
    public async Task<HttpReturnResult> Rename(RenameRequest request, DatabaseServices db, string userId)
    {
        var newName = request?.NewName?.Trim();
        if (!TryValidateName(newName, out var nameError))
            return new HttpReturnResult(false, nameError);

        var item = request?.Id is string id && Guid.TryParse(id, out _) ? await db.GetItemAsync(id, userId) : null;
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
            logger.LogError(ex, "Rename failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error renaming item") { StatusCode = 500 };
        }
    }



    // Move files/folders into a destination folder (null = root)
    public async Task<HttpReturnResult> Move(TransferRequest request, DatabaseServices db, string userId)
    {
        if (request?.SourceIds == null || request.SourceIds.Count == 0)
            return new HttpReturnResult(false, "No items selected");

        var destination = await ResolveDestinationAsync(request.DestinationId, db, userId);
        if (!destination.Ok)
            return destination.Error;
        var (destId, destPath) = destination.Value;

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
            logger.LogError(ex, "Move failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error moving items") { StatusCode = 500 };
        }
    }



    // Copy files/folders (including stored files) into a destination folder (null = root)
    public async Task<HttpReturnResult> Copy(TransferRequest request, DatabaseServices db, string userId)
    {
        if (request?.SourceIds == null || request.SourceIds.Count == 0)
            return new HttpReturnResult(false, "No items selected");

        var destination = await ResolveDestinationAsync(request.DestinationId, db, userId);
        if (!destination.Ok)
            return destination.Error;
        var (destId, destPath) = destination.Value;

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

        var quotaError = await CheckQuotaAsync(db, userId, trees.SelectMany(t => t).Where(t => !t.IsDirectory).Sum(t => t.Size));
        if (quotaError != null)
            return quotaError;

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
                    ParentId = t == root || t.ParentId is null ? destId : newGuids[t.ParentId],
                    Size = t.Size,
                    MimeType = t.MimeType,
                    WrappedKey = RewrapKey(t.WrappedKey, t.Guid, newGuids[t.Guid])
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

            logger.LogError(ex, "Copy failed for user {UserId}", userId);
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
            logger.LogError(ex, "Delete failed for {FileId}", fileId);
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
                logger.LogWarning(ex, "Could not delete stored file {FileGuid}", file);
            }
        }
    }
}
