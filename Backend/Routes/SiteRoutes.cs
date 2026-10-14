using Backend.Models;
using Backend.Services;

namespace Backend.Routes
{
    public static class SiteRoutes
    {
        public static void MapSiteRoutes(this IEndpointRouteBuilder app)
        {
            // Public: the privacy page and its links must work signed out. Only hosted mode has a
            // privacy page, so on a self-hosted install the answer is { "privacy": null }. Settings
            // only: nothing per-user and no secrets.
            app.MapGet("/site", (HostedOptions hosted, GeoIpService geoIp, TrashService trash) =>
                Results.Ok(new SiteInfoResponse(hosted.IsHosted
                    ? new PrivacySiteInfo(hosted.InactiveDays, hosted.WarningDays, (int)trash.Retention.TotalDays, geoIp.IsAvailable, hosted.ContactEmail)
                    : null)))
                .WithTags("Site")
                .WithSummary("Public site info for the privacy page: in hosted mode privacy has inactiveDays, warningDays, trashRetentionDays, loginLocation " +
                    "(whether a GeoIP database is installed) and contactEmail (left out when not set); self-hosted installs return privacy: null")
                .Produces<SiteInfoResponse>()
                .Produces<ErrorResponse>(429).RequireRateLimiting("site");
        }
    }
}
