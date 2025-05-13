using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FileVaultBackend.Models
{
    public class File
    {
        public DateTime UploadDate { get; set; }
        public string OriginalFileName { get; set; }
        public string StorageFileName { get; set; }
        public int FileSize { get; set; }
        public string MimeType { get; set; }
    }
}