using System.Threading.RateLimiting;

namespace Backend.Routes
{
    public static class RateLimitPolicies
    {
        // Adds per-IP fixed-window rate limit policies, like the "auth" and "refresh" ones in Program.cs.
        // Limits are read per request from RateLimiting:<policy>:PermitLimit/WindowSeconds, so they
        // can be changed in config (and in tests). Rejections use the handler set up in Program.cs.
        public static IServiceCollection AddFixedWindowRateLimits(
            this IServiceCollection services, params (string policy, int defaultLimit)[] policies) =>
            services.AddRateLimiter(options =>
            {
                foreach (var (policy, defaultLimit) in policies)
                {
                    options.AddPolicy(policy, http =>
                    {
                        var config = http.RequestServices.GetRequiredService<IConfiguration>();
                        var permitLimit = config.GetValue($"RateLimiting:{policy}:PermitLimit", defaultLimit);
                        var window = TimeSpan.FromSeconds(config.GetValue($"RateLimiting:{policy}:WindowSeconds", 60));

                        return RateLimitPartition.GetFixedWindowLimiter(
                            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window });
                    });
                }
            });
    }
}
