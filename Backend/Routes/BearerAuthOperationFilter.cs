using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Backend.Routes
{
    // Marks only endpoints that call RequireAuthorization() as needing the bearer token in the docs
    public sealed class BearerAuthOperationFilter : IOperationFilter
    {
        private static readonly OpenApiSecurityScheme BearerScheme = new()
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        };

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
                return;

            operation.Security.Add(new OpenApiSecurityRequirement { [BearerScheme] = [] });
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing, invalid or expired access token" });
        }
    }
}
