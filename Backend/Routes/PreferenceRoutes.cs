using Backend.Models;
using Backend.Services;
using System.Security.Claims;

namespace Backend.Routes
{
    // Per-user display preferences that follow the account across devices
    public static class PreferenceRoutes
    {
        public static void MapPreferenceRoutes(this IEndpointRouteBuilder app)
        {
            // Choose the style of folder and file icons
            app.MapPut("/user/icon-theme", async (
                ClaimsPrincipal user,
                IconThemeRequest request,
                DatabaseServices db) =>
            {
                var theme = request?.IconTheme;
                if (theme is null || !DatabaseServices.IconThemes.Contains(theme))
                    return Results.BadRequest(new { error = "Unknown icon theme" });

                await db.SetIconThemeAsync(user.GetUserId(), theme);
                return Results.Ok(new { success = "Icon theme saved" });
            })
                .WithTags("Account")
                .WithSummary("Set the icon theme: default, windows, macos or ubuntu")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(400).RequireAuthorization();
        }
    }
}
