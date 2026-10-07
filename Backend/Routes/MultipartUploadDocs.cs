using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Backend.Routes
{
    // Marks endpoints that read the multipart form themselves (so they can turn an oversized
    // body into a 413), so the docs can still show a file picker for them.
    public sealed record MultipartUploadMetadata(
        string FieldName = "file",
        string Description = "The file to upload",
        bool IncludeParentId = true);

    public sealed class MultipartUploadOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var upload = context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<MultipartUploadMetadata>().FirstOrDefault();
            if (upload == null)
                return;

            var properties = new Dictionary<string, OpenApiSchema>
            {
                [upload.FieldName] = new() { Type = "string", Format = "binary", Description = upload.Description }
            };
            if (upload.IncludeParentId)
                properties["parentId"] = new() { Type = "string", Description = "Folder ID to upload into (omit for the root)" };

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content =
                {
                    ["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Required = new HashSet<string> { upload.FieldName },
                            Properties = properties
                        }
                    }
                }
            };
        }
    }
}
