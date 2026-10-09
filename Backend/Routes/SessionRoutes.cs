using Backend.Models;
using Backend.Services;
using System.Security.Claims;

namespace Backend.Routes
{
    // Active sessions: the devices the user is logged in on. Each login starts a session, and
    // its refresh-token chain keeps it going; signing a session out revokes its refresh token,
    // so that device is logged out when its access token next needs renewing (within 15 minutes).
    // The refresh cookie (Path /user) is sent here too, which tells us which session is this one.
    public static class SessionRoutes
    {
        public static void MapSessionRoutes(this IEndpointRouteBuilder app)
        {
            // Lists the devices the user is logged in on
            app.MapGet("/user/sessions", async (
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                AuthServices auth) =>
            {
                var current = await auth.CurrentSessionIdAsync(db, http);
                var sessions = await db.GetActiveSessionsAsync(user.GetUserId());

                return Results.Ok(sessions
                    .Select(s => new SessionResponse(s.Id, s.Device, s.IpAddress, s.CreatedAt, s.LastUsedAt, s.Id == current))
                    .OrderByDescending(s => s.IsCurrent)
                    .ToList());
            })
                .WithTags("Sessions")
                .WithSummary("List the devices you're logged in on (isCurrent marks this one)")
                .Produces<List<SessionResponse>>().RequireAuthorization();

            // Signs one session out
            app.MapDelete("/user/sessions/{id}", async (
                string id,
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                AuthServices auth) =>
            {
                // Someone else's session looks the same as one that doesn't exist
                if (!Guid.TryParse(id, out var sessionId) || !await db.RevokeSessionAsync(user.GetUserId(), sessionId))
                    return Results.NotFound(new { error = "Session not found" });

                if (await auth.CurrentSessionIdAsync(db, http) == sessionId)
                    AuthServices.ClearRefreshCookie(http);

                return Results.Ok(new { success = "Signed out" });
            })
                .WithTags("Sessions")
                .WithSummary("Sign a session out: that device is logged out when its access token next needs renewing")
                .Produces<SuccessResponse>()
                .Produces<ErrorResponse>(404).RequireAuthorization();

            // Signs out every session except this one
            app.MapPost("/user/sessions/revoke-others", async (
                HttpContext http,
                ClaimsPrincipal user,
                DatabaseServices db,
                AuthServices auth) =>
            {
                // Without a refresh cookie we can't tell which session is this one, so all are signed out
                var current = await auth.CurrentSessionIdAsync(db, http);
                await db.RevokeOtherSessionsAsync(user.GetUserId(), current);

                return Results.Ok(new { success = "Signed out of all other sessions" });
            })
                .WithTags("Sessions")
                .WithSummary("Sign out of every other session")
                .Produces<SuccessResponse>().RequireAuthorization();
        }
    }
}
