using Backend.Models;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Large and resumable uploads: the client starts an upload, sends it in ~8 MB chunks (in any
// order, retrying any that fail), can ask which chunks have arrived, then completes it.
//
// Chunks wait under StorageRoot/tmp/<userId>/<uploadId>/, each encrypted with AES-GCM using a
// per-upload key (stored wrapped in the Uploads row), so nothing sits on disk in plaintext.
// Completing streams them through the normal file encryption into storage. Uploads in progress
// reserve quota; StorageCleanupService removes ones abandoned for 24 hours.
public class ChunkedUploadService(
    IConfiguration config, FileServices fs, FileEncryption encryption, ILogger<ChunkedUploadService> logger)
{
    public const int ChunkSize = 8 * 1024 * 1024;
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024; // 2 GB (Storage:MaxFileBytes)
    public const int MaxPendingUploads = 50;
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static long MaxFileBytes(IConfiguration config) =>
        config.GetValue("Storage:MaxFileBytes", DefaultMaxFileBytes);

    private readonly string _storageRoot = config.GetValue<string>("StorageRoot")
        ?? throw new InvalidOperationException("StorageRoot is not set.");

    private FileEncryption Encryption => encryption;

    private string TmpRoot => Path.GetFullPath(Path.Combine(_storageRoot, "tmp"));

    private string UploadDir(string userId, Guid uploadId) => Path.Combine(TmpRoot, userId, uploadId.ToString());

    private string ChunkPath(UploadRecord upload, int index) =>
        Path.Combine(UploadDir(upload.UserId, upload.Id), $"{index}.part");

    // Chunks are bound to their upload, position and length, so they can't be swapped or truncated
    private static byte[] ChunkAad(UploadRecord upload, int index) =>
        Encoding.UTF8.GetBytes($"{upload.Id}:{index}:{upload.ChunkLength(index)}");

    private static string KeyBinding(Guid uploadId) => $"upload:{uploadId}";

    private static HttpReturnResult UploadNotFound => HttpReturnResult.NotFound("Upload not found");



    public async Task<ServiceResult<StartedUpload>> StartAsync(StartUploadRequest? request, DatabaseServices db, string userId)
    {
        var name = request?.Name?.Trim();
        if (!FileServices.TryValidateName(name, out var nameError))
            return new HttpReturnResult(false, nameError);

        var maxFile = MaxFileBytes(config);
        if (request!.Size <= 0)
            return new HttpReturnResult(false, "Size must be greater than zero");
        if (request.Size > maxFile)
            return new HttpReturnResult(false, $"File is larger than the {FileServices.FormatBytes(maxFile)} limit") { StatusCode = 413 };

        var mimeType = string.IsNullOrWhiteSpace(request.MimeType) ? null : request.MimeType.Trim();
        if (mimeType?.Length > 255)
            return new HttpReturnResult(false, "MimeType is too long");

        var parentId = string.IsNullOrEmpty(request.ParentId) ? null : request.ParentId;
        if (parentId != null && await db.GetFolderById(parentId, userId) == null)
            return new HttpReturnResult(false, "Parent folder not found");

        // Uploads in progress count towards the quota, so several at once can't overfill it
        var (pendingBytes, pendingCount) = await db.GetPendingUploadsAsync(userId);
        if (pendingCount >= MaxPendingUploads)
            return new HttpReturnResult(false, "Too many uploads in progress. Finish or cancel some first.") { StatusCode = 429 };

        var usage = await fs.GetUsageAsync(db, userId);
        if (usage.Used + pendingBytes + request.Size > usage.Quota)
            return new HttpReturnResult(false,
                $"Not enough storage space: {FileServices.FormatBytes(usage.Quota - usage.Used - pendingBytes)} free of {FileServices.FormatBytes(usage.Quota)}")
            { StatusCode = 413 };

        var uploadId = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var upload = new UploadRecord(uploadId, userId, name, request.Size, parentId, mimeType, ChunkSize,
                encryption.WrapKey(key, KeyBinding(uploadId)), DateTime.UtcNow);
            Directory.CreateDirectory(UploadDir(userId, uploadId));
            await db.CreateUploadAsync(upload);
            return ServiceResult<StartedUpload>.Success(new StartedUpload(uploadId, ChunkSize, upload.ChunkCount));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }



    // Store one chunk. The body must be exactly the chunk's length. Sending a chunk again replaces it.
    public async Task<HttpReturnResult> PutChunkAsync(Guid uploadId, int index, Stream body, DatabaseServices db, string userId, CancellationToken ct)
    {
        var upload = await db.GetUploadAsync(uploadId, userId);
        if (upload == null)
            return UploadNotFound;
        if (index < 0 || index >= upload.ChunkCount)
            return new HttpReturnResult(false, $"Chunk index must be between 0 and {upload.ChunkCount - 1}");

        var expected = upload.ChunkLength(index);
        var plain = new byte[expected];
        try
        {
            var read = 0;
            while (read < expected)
            {
                var n = await body.ReadAsync(plain.AsMemory(read), ct);
                if (n == 0)
                    break;
                read += n;
            }
            if (read != expected || await body.ReadAsync(new byte[1], ct) != 0)
                return new HttpReturnResult(false, $"Chunk {index} must be exactly {expected} bytes");

            var key = encryption.UnwrapKey(upload.WrappedKey, KeyBinding(upload.Id));
            var output = new byte[NonceSize + TagSize + expected];
            try
            {
                RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(output.AsSpan(0, NonceSize), plain, output.AsSpan(NonceSize + TagSize),
                    output.AsSpan(NonceSize, TagSize), ChunkAad(upload, index));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            // Write then rename, so a half-written chunk is never counted as received
            var path = ChunkPath(upload, index);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, output, ct);
            File.Move(temp, path, overwrite: true);

            return new HttpReturnResult(true, $"Chunk {index} received");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }



    private List<int> ReceivedChunks(UploadRecord upload)
    {
        var received = new List<int>();
        for (var i = 0; i < upload.ChunkCount; i++)
        {
            var file = new FileInfo(ChunkPath(upload, i));
            if (file.Exists && file.Length == NonceSize + TagSize + upload.ChunkLength(i))
                received.Add(i);
        }
        return received;
    }

    public async Task<ServiceResult<UploadStatus>> GetStatusAsync(Guid uploadId, DatabaseServices db, string userId)
    {
        var upload = await db.GetUploadAsync(uploadId, userId);
        return upload == null
            ? UploadNotFound
            : ServiceResult<UploadStatus>.Success(new UploadStatus(
                upload.Id, upload.FileName, upload.Size, upload.ChunkSize, upload.ChunkCount, ReceivedChunks(upload)));
    }



    // Assemble the chunks into an encrypted stored file and add it to the user's files
    public async Task<ServiceResult<CompletedUpload>> CompleteAsync(Guid uploadId, DatabaseServices db, string userId)
    {
        var upload = await db.GetUploadAsync(uploadId, userId);
        if (upload == null)
            return UploadNotFound;

        var missing = upload.ChunkCount - ReceivedChunks(upload).Count;
        if (missing > 0)
            return new HttpReturnResult(false, $"{missing} chunk(s) haven't been received yet");

        if (!await db.ClaimUploadAsync(upload.Id, userId))
            return HttpReturnResult.Conflict("This upload is already being completed");

        var guid = Guid.NewGuid().ToString();
        var storedPath = Path.GetFullPath(Path.Combine(_storageRoot, guid));
        var (dataKey, wrappedKey) = encryption.CreateDataKey(guid);
        try
        {
            // The quota and the folder may have changed since the upload started
            var usage = await fs.GetUsageAsync(db, userId);
            if (usage.Used + upload.Size > usage.Quota)
            {
                await db.ReleaseUploadAsync(upload.Id);
                return new HttpReturnResult(false,
                    $"Not enough storage space: {FileServices.FormatBytes(usage.Quota - usage.Used)} free of {FileServices.FormatBytes(usage.Quota)}")
                { StatusCode = 413 };
            }

            var folderPath = "";
            if (upload.ParentId != null)
            {
                var folder = await db.GetFolderById(upload.ParentId, userId);
                if (folder == null)
                {
                    await db.ReleaseUploadAsync(upload.Id);
                    return new HttpReturnResult(false, "The folder this was being uploaded to no longer exists");
                }
                folderPath = folder.Path;
            }

            // A name already in the folder gets a number, like the normal upload
            var name = FileServices.GetUniqueName(upload.FileName, false, await db.GetNamesInFolderAsync(upload.ParentId, userId));

            long size;
            await using (var input = new ChunkReadStream(this, upload))
            await using (var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                size = await FileEncryption.EncryptAsync(input, output, dataKey);

            if (size != upload.Size)
                throw new InvalidOperationException($"Assembled {size} bytes, expected {upload.Size}.");

            await db.AddFile(name, 0, $"{folderPath}/{name}", guid, userId, size, upload.ParentId ?? "",
                upload.MimeType ?? "application/octet-stream", wrappedKey);

            await db.DeleteUploadAsync(upload.Id);
            DeleteDirectory(UploadDir(userId, upload.Id));

            return ServiceResult<CompletedUpload>.Success(new CompletedUpload($"File Uploaded: {name}", guid, name));
        }
        catch (Exception ex)
        {
            if (File.Exists(storedPath))
                File.Delete(storedPath);
            await db.ReleaseUploadAsync(upload.Id);

            logger.LogError(ex, "Completing upload {UploadId} failed for user {UserId}", upload.Id, userId);
            return new HttpReturnResult(false, "Error saving file") { StatusCode = 500 };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }



    public async Task<HttpReturnResult> CancelAsync(Guid uploadId, DatabaseServices db, string userId)
    {
        var upload = await db.GetUploadAsync(uploadId, userId);
        if (upload == null)
            return UploadNotFound;

        await db.DeleteUploadAsync(upload.Id);
        DeleteDirectory(UploadDir(userId, upload.Id));
        return new HttpReturnResult(true, "Upload cancelled");
    }

    // Remove uploads abandoned for 24 hours, and any leftover chunk folders; returns how many uploads
    public async Task<int> CleanupAbandonedAsync(DatabaseServices db)
    {
        var cutoff = DateTime.UtcNow - AbandonedAfter;
        var stale = await db.GetStaleUploadsAsync(cutoff);
        foreach (var upload in stale)
        {
            await db.DeleteUploadAsync(upload.Id);
            DeleteDirectory(UploadDir(upload.UserId, upload.Id));
        }

        // Folders with no upload row (e.g. the account was deleted)
        if (Directory.Exists(TmpRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(TmpRoot, "*", SearchOption.TopDirectoryOnly)
                         .SelectMany(userDir => Directory.EnumerateDirectories(userDir)))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    DeleteDirectory(dir);
            }
        }

        return stale.Count;
    }

    // Remove a deleted account's unfinished upload chunks (the Uploads rows go with the account)
    public void DeleteUserTempFiles(string userId)
    {
        if (Guid.TryParse(userId, out var id))
            DeleteDirectory(Path.Combine(TmpRoot, id.ToString()));
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete upload folder {Path}", path);
        }
    }



    // Read-only stream over an upload's decrypted chunks, in order
    private sealed class ChunkReadStream(ChunkedUploadService service, UploadRecord upload) : Stream
    {
        private readonly byte[] _key = service.Encryption.UnwrapKey(upload.WrappedKey, KeyBinding(upload.Id));
        private byte[] _plain = [];
        private int _offset;
        private int _next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset == _plain.Length)
            {
                if (_next == upload.ChunkCount)
                    return 0;
                await LoadAsync(_next++, ct);
            }

            var count = Math.Min(buffer.Length, _plain.Length - _offset);
            _plain.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        private async Task LoadAsync(int index, CancellationToken ct)
        {
            var data = await File.ReadAllBytesAsync(service.ChunkPath(upload, index), ct);
            var length = upload.ChunkLength(index);
            if (data.Length != NonceSize + TagSize + length)
                throw new CryptographicException($"Chunk {index} has the wrong length.");

            CryptographicOperations.ZeroMemory(_plain);
            _plain = new byte[length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize),
                _plain, ChunkAad(upload, index));
            _offset = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => upload.Size;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            CryptographicOperations.ZeroMemory(_plain);
            CryptographicOperations.ZeroMemory(_key);
            base.Dispose(disposing);
        }
    }
}
