namespace Backend.Models
{
    public class FileModel
    {
        public string _id { get; set; }
        public string Name { get; set; }
        public bool IsDirectory { get; set; } = false;
        public string Path { get; set; }
        public DateTime UpdatedAt { get; set; }
        public long Size { get; set; }
    }
}