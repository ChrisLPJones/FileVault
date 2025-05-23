namespace FileVaultBackend.Models
{
#nullable enable
    public record HttpReturnResult
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
        public string? FileName { get; init; }
        public byte[]? FileContent { get; init; }

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

        public HttpReturnResult(bool success, string? message, string? fileName, byte[]? fileContent)
            : this(success, message, fileName)
        {
            this.FileContent = fileContent;
        }

    }
}
