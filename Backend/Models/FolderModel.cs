using Microsoft.VisualBasic;

namespace Backend.Models
{
    public class FolderModel
    {
        public string _id { get; set; }
        public string UserId { get; set; }
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public string Path { get; set; }
        public string ParentId { get; set; }
        public long Size { get; set; }
        public string MimeType { get; set; }
        public DateAndTime CreatedAt { get; set; }
        public DateAndTime UpdatedAt { get; set; }
        public string __v { get; set; }
        

    }
}