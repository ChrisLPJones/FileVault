using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Backend.Routes
{
    // Marks the upload endpoint, which reads the multipart form itself (so it can turn an
    // oversized body into a 413), so the docs can still show a file picker for it.
    public sealed class MultipartUploadMetadata;

    public sealed class MultipartUploadOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<MultipartUploadMetadata>().Any())
                return;

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
                            Required = new HashSet<string> { "file" },
                            Properties =
                            {
                                ["file"] = new OpenApiSchema { Type = "string", Format = "binary", Description = "The file to upload" },
                                ["parentId"] = new OpenApiSchema { Type = "string", Description = "Folder ID to upload into (omit for the root)" }
                            }
                        }
                    }
                }
            };
        }
    }
}
