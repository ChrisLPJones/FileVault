namespace Backend.Models
{
    // An entry in GET /trash: something the user deleted (a folder includes what was inside it).
    // OriginalFolder is where it was ("" = the root); PurgeAt is when it will be deleted for good.
    public record TrashItem(
        string _id,
        string Name,
        bool IsDirectory,
        string OriginalFolder,
        long Size,
        DateTime DeletedAt,
        DateTime PurgeAt);

    // POST /trash/restore body
    public class RestoreRequest
    {
        public List<string>? Ids { get; set; }
    }
}
