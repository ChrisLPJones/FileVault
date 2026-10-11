using Backend.Models;
using Backend.Services;

namespace Backend.Routes
{
    // Public information about the site that the pages show without signing in
    public static class SiteRoutes
    {
        public static void MapSiteRoutes(this IEndpointRouteBuilder app)
        {
            // Only what the operator chose to publish: the mode, limits and totals are not exposed
            app.MapGet("/site", (HttpContext http, HostedOptions options) =>
            {
                http.Response.Headers.CacheControl = "public, max-age=300";
                return Results.Ok(new SiteInfo(options.AbuseContactEmail));
            })
                .WithTags("Site")
                .WithSummary("Public site information: the abuse contact address, or null if none is set")
                .Produces<SiteInfo>()
                .Produces<ErrorResponse>(429).RequireRateLimiting("public");
        }
    }

    // GET /site
    public record SiteInfo(string? AbuseContactEmail);
}
