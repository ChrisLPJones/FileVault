namespace Backend.Models
{
    // POST /uploads body: the file about to be sent in chunks
    public class StartUploadRequest
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? ParentId { get; set; }
        public string? MimeType { get; set; }
    }

    // POST /uploads response
    public record StartedUpload(Guid UploadId, int ChunkSize, int ChunkCount);

    // GET /uploads/{id}: which chunks have arrived, so an interrupted upload can carry on
    public record UploadStatus(Guid UploadId, string Name, long Size, int ChunkSize, int ChunkCount, List<int> ReceivedChunks);

    // POST /uploads/{id}/complete response
    public record CompletedUpload(string Success, string Id, string Name);

    // An Uploads row
    public record UploadRecord(
        Guid Id,
        string UserId,
        string FileName,
        long Size,
        string? ParentId,
        string? MimeType,
        int ChunkSize,
        string WrappedKey,
        DateTime CreatedAt)
    {
        public int ChunkCount => (int)((Size + ChunkSize - 1) / ChunkSize);

        // Plaintext length of a chunk (the last one may be shorter)
        public int ChunkLength(int index) =>
            index == ChunkCount - 1 ? (int)(Size - (long)index * ChunkSize) : ChunkSize;
    }
}
