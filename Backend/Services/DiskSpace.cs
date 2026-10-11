using Backend.Models;

namespace Backend.Services;

// How full the drive holding the storage folder is, and the cap on it (Storage:MaxDiskUsePercent,
// default 80, 0 = off). Uploads that would push the drive past the cap are refused with 507. The
// cap applies to everyone, admins included, and in both modes: it protects the server (and the
// database if it shares the disk) from a full drive. A drive that can't be read fails open.
// Tests replace the reader (ReadCore) with fake figures.
public class DiskSpace
{
    public const int DefaultMaxPercent = 80;
    public const string FullMessage = "The server's storage is almost full, so uploads are paused. Please try again later.";

    private readonly string _storageRoot;
    private readonly ILogger _logger;
    private long _lastWarningTicks;

    public int MaxPercent { get; }

    public DiskSpace(IConfiguration config, ILogger<DiskSpace> logger)
    {
        _storageRoot = config.GetValue<string>("StorageRoot") ?? throw new InvalidOperationException("StorageRoot is not set.");
        _logger = logger;
        MaxPercent = ParsePercent(config["Storage:MaxDiskUsePercent"]);
    }

    // Unset or empty = 80; 0 = off; 1-99 = the cap; anything else stops the API starting
    public static int ParsePercent(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw))
            return DefaultMaxPercent;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 99)
            throw new InvalidOperationException(
                $"STORAGE_MAX_DISK_USE_PERCENT (Storage:MaxDiskUsePercent) must be a whole number from 1 to 99, or 0 to turn the limit off, but it is \"{raw}\".");
        return percent;
    }

    // Total and free bytes of the drive the storage folder is on, or null if it can't be read
    public (long total, long available)? Read()
    {
        try
        {
            var path = Path.GetFullPath(_storageRoot);
            // The folder is created at start-up; fall back to the nearest parent that exists
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent)
                path = parent;
            var read = ReadCore(path);
            return read is { total: > 0 } ? read : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    protected virtual (long total, long available)? ReadCore(string path)
    {
        var drive = new DriveInfo(path);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }

    // Used bytes include blocks reserved for root, which is the safe direction
    public static long LimitBytes(long total, int percent) => (long)(total / 100.0 * percent);

    // The drive's state for the admin page and startup log; null if the limit is off or it can't be read
    public async Task<DiskState?> GetStateAsync(DatabaseServices db)
    {
        if (MaxPercent == 0)
            return null;
        if (Read() is not var (total, available))
            return null;
        var used = total - available;
        var pending = await db.GetAllPendingUploadBytesAsync();
        return new DiskState(total, used, pending, used > LimitBytes(total, MaxPercent));
    }

    // Returns a 507 result if storing `incoming` more bytes would take the drive past the cap.
    // Chunk writes pass the chunk length; completing an upload passes its size (assembly briefly needs
    // about that much on top of the chunks already on disk).
    public Task<HttpReturnResult?> CheckAsync(long incoming) => Task.FromResult(Check(incoming));

    private HttpReturnResult? Check(long incoming)
    {
        if (MaxPercent == 0)
            return null;

        if (Read() is not var (total, available))
        {
            // Fail open: refusing every upload over a monitoring problem is worse; the OS still stops a full drive
            WarnUnreadable();
            return null;
        }

        var used = total - available;
        // Other users' pending uploads are not counted: their chunks are already in `used`, and the
        // unreceived rest is only a declared size that costs nothing until chunks arrive (each is checked).
        if (used + incoming <= LimitBytes(total, MaxPercent))
            return null;

        return new HttpReturnResult(false, FullMessage) { StatusCode = 507 };
    }

    private void WarnUnreadable()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (last != 0 && now - last < TimeSpan.FromHours(1).TotalMilliseconds)
            return;
        Interlocked.Exchange(ref _lastWarningTicks, now == 0 ? 1 : now);
        _logger.LogWarning("Could not read the disk space of the storage folder, so the disk limit (Storage:MaxDiskUsePercent) is not being applied");
    }
}

// used = bytes in use on the drive; pending = bytes of chunked uploads in progress (all users)
public record DiskState(long Total, long Used, long Pending, bool UploadsPaused);
