namespace Backend.Models
{
    public record HttpReturnResult
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
        public string? FileName { get; init; }
        public FolderModel? Folder { get; init; }

        // HTTP status to use when Success is false (defaults to 400 in the routes)
        public int? StatusCode { get; init; }

        public static HttpReturnResult NotFound(string message) => new(false, message) { StatusCode = 404 };
        public static HttpReturnResult Conflict(string message) => new(false, message) { StatusCode = 409 };

        public HttpReturnResult(bool success)
        {
            this.Success = success;
        }

        public HttpReturnResult(bool success, string? message)
            : this(success)
        {
            this.Message = message;
        }

        public HttpReturnResult(bool success, string? message, string? fileName)
            : this(success, message)
        {
            this.FileName = fileName;
        }

        public HttpReturnResult(bool success, string? message, FolderModel folder)
            : this(success, message)
        {
            this.Folder = folder;
        }
    }
}
