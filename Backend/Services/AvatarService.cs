using Backend.Models;
using System.Security.Cryptography;

namespace Backend.Services;

// Profile pictures: validated by content, encrypted like stored files, kept in StorageRoot/avatars.
// They don't count towards the storage quota.
public class AvatarService(IConfiguration config, FileEncryption encryption, ILogger<AvatarService> logger)
{
    public const long MaxBytes = 2 * 1024 * 1024; // 2 MB (the frontend resizes to 256x256 first)

    private readonly string _avatarDirectory = Path.GetFullPath(Path.Combine(
        config.GetValue<string>("StorageRoot") ?? throw new InvalidOperationException("StorageRoot is not set."),
        "avatars"));

    public record AvatarImage(Stream Stream, string MimeType, DateTime UpdatedAt);

    // The encryption key is bound to this ID, so an avatar can't be swapped with a stored file
    private static string KeyId(string userId) => $"avatar:{userId}";

    private string AvatarPath(string userId)
    {
        if (!Guid.TryParse(userId, out var id))
            throw new ArgumentException("Invalid user ID.", nameof(userId));
        return Path.Combine(_avatarDirectory, id.ToString());
    }

    // Image type from the file's first bytes; the client's Content-Type isn't trusted
    public static string? DetectImageType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    public async Task<HttpReturnResult> SaveAsync(IFormFile file, DatabaseServices db, string userId)
    {
        if (file.Length == 0)
            return new HttpReturnResult(false, "No image uploaded");
        if (file.Length > MaxBytes)
            return new HttpReturnResult(false, "Profile pictures must be 2 MB or smaller") { StatusCode = 413 };

        var header = new byte[12];
        int headerLength;
        await using (var peek = file.OpenReadStream())
            headerLength = await peek.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);

        var mimeType = DetectImageType(header.AsSpan(0, headerLength));
        if (mimeType == null)
            return new HttpReturnResult(false, "Profile pictures must be PNG, JPEG or WebP images");

        Directory.CreateDirectory(_avatarDirectory);
        var path = AvatarPath(userId);
        var tempPath = path + ".uploading";
        var (dataKey, wrappedKey) = encryption.CreateDataKey(KeyId(userId));

        try
        {
            long size;
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                size = await FileEncryption.EncryptAsync(input, output, dataKey);

            // Replace the old image only once the new one is fully written
            File.Move(tempPath, path, overwrite: true);
            await db.SetAvatarAsync(userId, wrappedKey, size, mimeType);
            return new HttpReturnResult(true, "Profile picture updated");
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            logger.LogError(ex, "Saving avatar failed for user {UserId}", userId);
            return new HttpReturnResult(false, "Error saving profile picture") { StatusCode = 500 };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // The decrypted avatar, or null if the user has none
    public async Task<AvatarImage?> OpenAsync(DatabaseServices db, string userId)
    {
        var avatar = await db.GetAvatarAsync(userId);
        var path = AvatarPath(userId);
        if (avatar == null || !File.Exists(path))
            return null;

        var dataKey = encryption.UnwrapKey(avatar.WrappedKey, KeyId(userId));
        try
        {
            return new AvatarImage(FileEncryption.OpenDecryptedRead(path, dataKey, avatar.Size), avatar.MimeType, avatar.UpdatedAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task DeleteAsync(DatabaseServices db, string userId)
    {
        await db.SetAvatarAsync(userId, null, null, null);
        DeleteFile(userId);
    }

    // Remove the image file only (e.g. after the account row has been deleted)
    public void DeleteFile(string userId)
    {
        try
        {
            var path = AvatarPath(userId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete avatar file for user {UserId}", userId);
        }
    }
}
