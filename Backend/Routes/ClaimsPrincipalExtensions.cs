using System.Security.Claims;

namespace Backend.Routes
{
    public static class ClaimsPrincipalExtensions
    {
        // The authenticated user's ID (the JWT "sub" claim)
        public static string GetUserId(this ClaimsPrincipal user) =>
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
