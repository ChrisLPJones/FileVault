using System.Diagnostics.CodeAnalysis;

namespace Backend.Models
{
    // A row from the Files table (file or folder)
    public class FileRecord
    {
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
        public string Path { get; set; } = "";
        public string? ParentId { get; set; }
        public long Size { get; set; }
        public string? MimeType { get; set; }

        // Per-file data key encrypted with the master key (null for folders and legacy unencrypted files)
        public string? WrappedKey { get; set; }
    }

    // Storage used and allowed for a user, in bytes
    // MaxUploadBytes applies to POST /upload; MaxFileBytes to chunked uploads (POST /uploads)
    public record StorageUsage(long Used, long Quota, long MaxUploadBytes, long MaxFileBytes);

    // Either a value or an error, so callers can't use one without checking
    public record ServiceResult<T>(T? Value, HttpReturnResult? Error) where T : class
    {
        [MemberNotNullWhen(true, nameof(Value))]
        [MemberNotNullWhen(false, nameof(Error))]
        public bool Ok => Value is not null;

        public static ServiceResult<T> Success(T value) => new(value, null);

        public static implicit operator ServiceResult<T>(HttpReturnResult error) => new(null, error);
    }

    // An opened file ready to stream to the client
    public record DownloadFile(FileRecord File, Stream Stream);

    // A zip ready to stream to the client
    public record ZipDownload(Stream Stream, string FileName);

    public class RenameRequest
    {
        public string? Id { get; set; }
        public string? NewName { get; set; }
    }

    public class TransferRequest
    {
        public List<string>? SourceIds { get; set; }
        public string? DestinationId { get; set; }
    }

    public class ZipDownloadRequest
    {
        public List<string>? Ids { get; set; }
    }
}
