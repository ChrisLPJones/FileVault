namespace Backend.Models
{
    // A row from the Files table (file or folder)
    public class FileRecord
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public string Path { get; set; }
        public string ParentId { get; set; }
        public long Size { get; set; }
        public string MimeType { get; set; }
    }

    public class RenameRequest
    {
        public string Id { get; set; }
        public string NewName { get; set; }
    }

    public class TransferRequest
    {
        public List<string> SourceIds { get; set; }
        public string DestinationId { get; set; }
    }

    public class ZipDownloadRequest
    {
        public List<string> Ids { get; set; }
    }
}
