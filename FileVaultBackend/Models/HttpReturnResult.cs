namespace FileVaultBackend.Models
{
    #nullable enable
    public record HttpReturnResult(bool Success, string? Message, string? FileName, byte[]? FileContent);
}
