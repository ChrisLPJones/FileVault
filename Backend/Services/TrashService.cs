using Backend.Models;

namespace Backend.Services;

// The recycle bin. Deleting moves an item (and what's inside it) into the bin; it can be restored
// to where it was, or deleted for good, which removes the rows and the encrypted files on disk.
// Items in the bin still count towards the quota. StorageCleanupService empties out entries older
// than Storage:TrashRetentionDays.
public class TrashService(IConfiguration config, FileServices fs, ILogger<TrashService> logger)
{
    public const int DefaultRetentionDays = 30;

    public TimeSpan Retention => TimeSpan.FromDays(config.GetValue("Storage:TrashRetentionDays", DefaultRetentionDays));

    // Move one item to the bin
    public async Task<HttpReturnResult> MoveToTrashAsync(string fileId, DatabaseServices db, string userId)
    {
        if (!Guid.TryParse(fileId, out _) || !await db.MoveToTrashAsync(fileId, userId))
            return HttpReturnResult.NotFound("File not found.");

        return new HttpReturnResult(true, "Moved to the recycle bin");
    }

    public async Task<List<TrashItem>> ListAsync(DatabaseServices db, string userId)
    {
        var retention = Retention;
        return (await db.GetTrashAsync(userId))
            .Select(e => new TrashItem(
                e.item.Guid,
                e.item.Name,
                e.item.IsDirectory,
                e.item.Path[..Math.Max(0, e.item.Path.LastIndexOf('/'))],
                e.item.IsDirectory ? e.size : e.item.Size,
                e.deletedAt,
                e.deletedAt + retention))
            .ToList();
    }

    // Put bin entries back in their original folder, or the root if that folder is gone (deleted
    // or itself in the bin). A name that is taken there gets a number: "report (1).pdf".
    public async Task<HttpReturnResult> RestoreAsync(List<string>? ids, DatabaseServices db, string userId)
    {
        if (ids == null || ids.Count == 0)
            return new HttpReturnResult(false, "No items selected");

        // Check everything before changing anything
        var entries = new List<FileRecord>();
        foreach (var id in ids.Distinct())
        {
            var entry = Guid.TryParse(id, out _) ? await db.GetTrashEntryAsync(id, userId) : null;
            if (entry == null)
                return HttpReturnResult.NotFound("Item not found in the recycle bin.");
            entries.Add(entry);
        }

        // Names already used per destination folder, including ones given out in this request
        var takenByFolder = new Dictionary<string, HashSet<string>>();

        try
        {
            foreach (var entry in entries)
            {
                var parent = entry.ParentId == null ? null : await db.GetFolderById(entry.ParentId, userId);
                var key = parent?._id ?? "";
                if (!takenByFolder.TryGetValue(key, out var taken))
                    takenByFolder[key] = taken = await db.GetNamesInFolderAsync(parent?._id, userId);

                var name = FileServices.GetUniqueName(entry.Name, entry.IsDirectory, taken);
                taken.Add(name);

                await db.RestoreFromTrashAsync(entry, parent?._id, name, $"{parent?.Path ?? ""}/{name}", userId);
            }

            return new HttpReturnResult(true, $"Restored {entries.Count} item(s)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error restoring items") { StatusCode = 500 };
        }
    }

    // Delete one bin entry for good (rows and stored files)
    public async Task<HttpReturnResult> DeletePermanentlyAsync(string id, DatabaseServices db, string userId)
    {
        if (!Guid.TryParse(id, out _) || await db.GetTrashEntryAsync(id, userId) == null)
            return HttpReturnResult.NotFound("Item not found in the recycle bin.");

        await db.DetachTrashedChildrenAsync(id, userId);
        var result = await fs.DeleteFile(id, db, userId);
        return result.Success
            ? new HttpReturnResult(true, "Deleted permanently")
            : new HttpReturnResult(false, "Error deleting item") { StatusCode = 500 };
    }

    // Delete everything in the user's bin for good; returns how many entries were deleted
    public async Task<int> EmptyAsync(DatabaseServices db, string userId)
    {
        var count = 0;
        foreach (var (item, _, _) in await db.GetTrashAsync(userId))
        {
            if ((await DeletePermanentlyAsync(item.Guid, db, userId)).Success)
                count++;
        }
        return count;
    }

    // Delete bin entries (all users) older than the retention period; returns how many
    public async Task<int> PurgeExpiredAsync(DatabaseServices db)
    {
        var count = 0;
        foreach (var (guid, userId) in await db.GetExpiredTrashAsync(DateTime.UtcNow - Retention))
        {
            if ((await DeletePermanentlyAsync(guid, db, userId)).Success)
                count++;
            else
                logger.LogWarning("Could not purge bin entry {FileGuid} for user {UserId}", guid, userId);
        }
        return count;
    }
}
