using Backend.Models;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Backend.Services;

// Small previews of image files for the grid view. Made the first time one is asked for (so
// files uploaded before thumbnails existed get one too), then kept encrypted like avatars in
// StorageRoot/thumbnails. They don't count towards the quota and are deleted with the file.
public class ThumbnailService(IConfiguration config, FileEncryption encryption, ILogger<ThumbnailService> logger)
{
    public const int MaxDimension = 256;                         // longest side, in pixels
    public const long DefaultMaxSourceBytes = 50L * 1024 * 1024; // bigger images get no thumbnail
    private const long MaxSourcePixels = 100_000_000;            // guards against decompression bombs
    private const int WebpQuality = 80;
    public const string MimeType = "image/webp";

    private static readonly HashSet<string> Extensions =
        new(["jpg", "jpeg", "png", "gif", "webp", "bmp"], StringComparer.OrdinalIgnoreCase);

    // One generation per file at a time, and only a few at once (decoding is CPU and memory heavy)
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlight = new();
    private static readonly SemaphoreSlim Gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    private readonly string _storageRoot = config.GetValue<string>("StorageRoot")
        ?? throw new InvalidOperationException("StorageRoot is not set.");

    private long MaxSourceBytes => config.GetValue("Thumbnails:MaxSourceBytes", DefaultMaxSourceBytes);

    public record ThumbnailImage(Stream Stream, string MimeType, string ETag);

    // The encryption key is bound to this ID, so a thumbnail can't be swapped with a stored file
    private static string KeyId(string fileGuid) => $"thumbnail:{fileGuid}";

    // Where a file's thumbnail is kept (whether or not it exists)
    public static string StoredPath(string storageRoot, string fileGuid) =>
        Path.GetFullPath(Path.Combine(storageRoot, "thumbnails", fileGuid));

    // Only these image types are thumbnailed (by name; the content is checked when decoding)
    public static bool IsSupported(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(fileName[(dot + 1)..]);
    }

    // The thumbnail of one of the user's images, made now if it doesn't exist yet.
    // Null if the item isn't the user's, isn't a supported image, or can't be thumbnailed.
    public async Task<ThumbnailImage?> GetAsync(string fileId, FileServices fs, DatabaseServices db, string userId)
    {
        if (!Guid.TryParse(fileId, out _))
            return null;

        var record = await db.GetThumbnailAsync(fileId, userId);
        if (record == null)
        {
            var file = await db.GetItemAsync(fileId, userId);
            if (file == null || file.IsDirectory || !IsSupported(file.Name) || file.Size > MaxSourceBytes)
                return null;

            // Lazy, because GetOrAdd may run the factory for several racing callers
            var creation = InFlight.GetOrAdd(file.Guid, _ => new Lazy<Task>(() => CreateAsync(file, fs, db)));
            try
            {
                await creation.Value;
            }
            finally
            {
                InFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(file.Guid, creation));
            }

            record = await db.GetThumbnailAsync(fileId, userId);
        }

        var path = StoredPath(_storageRoot, fileId);
        if (record?.WrappedKey == null || record.Size == null || !File.Exists(path))
            return null;

        var dataKey = encryption.UnwrapKey(record.WrappedKey, KeyId(fileId));
        try
        {
            var etag = $"\"{fileId}-{record.CreatedAt.Ticks:x}\"";
            return new ThumbnailImage(FileEncryption.OpenDecryptedRead(path, dataKey, record.Size.Value), record.MimeType ?? MimeType, etag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // Decode the image, write its encrypted thumbnail and record it. An image that can't be
    // decoded is recorded as having no thumbnail; unexpected errors are logged and retried next time.
    private async Task CreateAsync(FileRecord file, FileServices fs, DatabaseServices db)
    {
        await Gate.WaitAsync();
        var path = StoredPath(_storageRoot, file.Guid);
        var tempPath = $"{path}.{Guid.NewGuid():N}.creating";
        try
        {
            // Another request may have made it while this one was waiting; a thumbnail is
            // never replaced, since requests may be reading it
            if (await db.HasThumbnailRecordAsync(file.Guid))
                return;

            // Decrypt into memory first: the decrypting stream returns at most one 64 KB chunk per
            // read, and some decoders (PNG) treat a short read as the end of the file
            using var plain = new MemoryStream((int)Math.Min(file.Size, int.MaxValue));
            await using (var source = fs.OpenStoredFile(file))
                await source.CopyToAsync(plain);
            plain.Position = 0;

            var thumbnail = await Task.Run(() => Render(plain, MaxDimension));

            if (thumbnail == null)
            {
                await db.SaveThumbnailAsync(file.Guid, null, null, null);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var (dataKey, wrappedKey) = encryption.CreateDataKey(KeyId(file.Guid));
            try
            {
                long size;
                await using (var input = new MemoryStream(thumbnail))
                await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    size = await FileEncryption.EncryptAsync(input, output, dataKey);

                File.Move(tempPath, path, overwrite: true); // replaces a leftover from a failed attempt
                if (!await db.SaveThumbnailAsync(file.Guid, wrappedKey, size, MimeType))
                    File.Delete(path); // the file was deleted while its thumbnail was being made
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating a thumbnail failed for {FileGuid}", file.Guid);
            TryDelete(tempPath);
        }
        finally
        {
            Gate.Release();
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    // Scale an image to fit in maxDimension x maxDimension (never enlarging it), upright
    // according to its EXIF orientation, as WebP. Null if it isn't an image Skia can decode.
    // Animated GIFs and WebPs use their first frame.
    public static byte[]? Render(Stream source, int maxDimension)
    {
        using var codec = SKCodec.Create(source);
        if (codec == null)
            return null;

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > MaxSourcePixels)
            return null;

        // Let the decoder shrink the image where it can (JPEG decodes at 1/2, 1/4 or 1/8 cheaply),
        // keeping at least twice the final size so the resize below still looks smooth
        var decodeSize = codec.GetScaledDimensions(Math.Min(1f, 2f * maxDimension / Math.Max(width, height)));
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            return null;

        var ratio = Math.Min(1.0, (double)maxDimension / Math.Max(decodeSize.Width, decodeSize.Height));
        var resizedInfo = new SKImageInfo(
            Math.Max(1, (int)Math.Round(decodeSize.Width * ratio)),
            Math.Max(1, (int)Math.Round(decodeSize.Height * ratio)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var resized = decoded.Resize(resizedInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized == null)
            return null;

        using var upright = Orient(resized, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(upright);
        using var data = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        return data?.ToArray();
    }

    // Copy of the bitmap turned the right way up (photos from phones are often stored sideways)
    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        float w = bitmap.Width, h = bitmap.Height;
        // x' = a*x + b*y + c, y' = d*x + e*y + f
        (float a, float b, float c, float d, float e, float f)? transform = origin switch
        {
            SKEncodedOrigin.TopRight => (-1, 0, w, 0, 1, 0),     // mirrored
            SKEncodedOrigin.BottomRight => (-1, 0, w, 0, -1, h), // upside down
            SKEncodedOrigin.BottomLeft => (1, 0, 0, 0, -1, h),   // mirrored vertically
            SKEncodedOrigin.LeftTop => (0, 1, 0, 1, 0, 0),       // transposed
            SKEncodedOrigin.RightTop => (0, -1, h, 1, 0, 0),     // needs 90° clockwise
            SKEncodedOrigin.RightBottom => (0, -1, h, -1, 0, w), // transverse
            SKEncodedOrigin.LeftBottom => (0, 1, 0, -1, 0, w),   // needs 90° anticlockwise
            _ => null
        };

        if (transform is not { } t)
            return bitmap.Copy();

        var swap = t.a == 0;
        var result = new SKBitmap(new SKImageInfo(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height,
            bitmap.ColorType, bitmap.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(new SKMatrix(t.a, t.b, t.c, t.d, t.e, t.f, 0, 0, 1));
        canvas.DrawBitmap(bitmap, 0, 0);
        return result;
    }
}
