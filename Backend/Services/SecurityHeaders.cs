namespace Backend.Services;

// Security headers on every API response (the frontend's are set by nginx, see Frontend/nginx/).
public static class SecurityHeaders
{
    // The API only serves JSON and files, never pages that load anything: nothing may run or
    // embed it. Swagger UI is a real page, so it only gets the framing rule.
    private const string ApiPolicy = "default-src 'none'; frame-ancestors 'none'";
    private const string SwaggerPolicy = "frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy =
                    context.Request.Path.StartsWithSegments("/swagger") ? SwaggerPolicy : ApiPolicy;

                // Account and auth responses (tokens, profile, sessions, 2FA secrets) must never be
                // cached. Endpoints that set their own caching (e.g. the avatar) keep it.
                if (context.Request.Path.StartsWithSegments("/user") && string.IsNullOrEmpty(headers.CacheControl))
                    headers.CacheControl = "no-store";

                return Task.CompletedTask;
            });

            return next(context);
        });
}
