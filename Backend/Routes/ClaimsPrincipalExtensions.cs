using System.Security.Claims;

namespace Backend.Routes
{
    public static class ClaimsPrincipalExtensions
    {
        // The authenticated user's ID (the JWT "sub" claim). Only call this on endpoints that
        // require authorization, where the claim is always present.
        public static string GetUserId(this ClaimsPrincipal user) =>
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Authenticated request has no user ID claim.");
    }
}
