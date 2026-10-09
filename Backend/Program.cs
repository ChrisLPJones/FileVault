using Backend.Routes;
using Backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Threading.RateLimiting;

namespace Backend
{
    public partial class Program
    {
        private static void Main(string[] args)
        {


            var builder = WebApplication.CreateBuilder(args);
            var jwtConfig = builder.Configuration.GetSection("Jwt");

            // Secrets are not stored in appsettings.json; fail fast with a clear message if they're missing
            var jwtKey = jwtConfig["Key"];
            if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
                throw new InvalidOperationException(
                    "Jwt:Key must be set to at least 32 bytes. Copy appsettings.Development.example.json " +
                    "to appsettings.Development.json, or set the Jwt__Key environment variable.");

            if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DefaultConnection")))
                throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is not set. Copy appsettings.Development.example.json " +
                    "to appsettings.Development.json, or set the ConnectionStrings__DefaultConnection environment variable.");

            if (FileEncryption.ParseMasterKey(builder.Configuration["Encryption:MasterKey"]) == null)
                throw new InvalidOperationException(
                    "Encryption:MasterKey must be a base64-encoded 32-byte key (e.g. openssl rand -base64 32). " +
                    "Set it in appsettings.Development.json or the Encryption__MasterKey environment variable. " +
                    "Keep it safe: stored files can't be decrypted without it.");

            // Allow request bodies up to the configured upload limit (plus room for the multipart wrapper)
            var maxRequestBytes = FileServices.MaxUploadBytes(builder.Configuration) + 1024 * 1024;
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxRequestBytes);
            builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(form =>
                form.MultipartBodyLengthLimit = maxRequestBytes);

            // Inject Services
            builder.Services.AddCors(options => {
                options.AddPolicy("AllowFrontend", policy =>
                {
                    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
                    policy.WithOrigins(origins is { Length: > 0 } ? origins : ["http://localhost:5173"])
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials(); // the refresh token cookie
                    });
                    });

            // Per-IP limits on endpoints that check passwords or issue tokens.
            // Limits are read per request so they can be changed in config (and in tests).
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = async (context, ct) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                        context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();

                    await context.HttpContext.Response.WriteAsJsonAsync(
                        new { error = "Too many attempts. Please wait a minute and try again." }, ct);
                };

                foreach (var (policy, defaultLimit) in new[] { ("auth", 10), ("refresh", 30), ("two-factor", 10) })
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
            // Public share links: viewing, and password attempts/downloads
            builder.Services.AddFixedWindowRateLimits(("share", 60), ("share-download", 20));
            // Endpoints that send email (forgot password, resend verification)
            builder.Services.AddFixedWindowRateLimits(("email", 5));
            // OpenAPI document (/swagger/v1/swagger.json) and interactive docs (/swagger)
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "FileVault API",
                    Version = "v1",
                    Description =
                        "Encrypted file storage API. Log in with POST /user/login, then click Authorize and " +
                        "paste the returned access token. The refresh token is an httpOnly cookie, so " +
                        "/user/refresh and /user/logout only work from a browser on the frontend's origin."
                });

                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Access token from POST /user/login (valid for 15 minutes)"
                });
                options.OperationFilter<BearerAuthOperationFilter>();
                options.OperationFilter<MultipartUploadOperationFilter>();
            });

            builder.Services.AddSingleton<FileEncryption>();
            builder.Services.AddScoped<FileServices>();
            builder.Services.AddScoped<AvatarService>();
            builder.Services.AddSingleton<ThumbnailService>();
            builder.Services.AddScoped<DatabaseServices>();
            builder.Services.AddScoped<AuthServices>();
            builder.Services.AddScoped<ShareService>();
            builder.Services.AddScoped<TrashService>();
            builder.Services.AddScoped<ChunkedUploadService>();
            builder.Services.AddHostedService<StorageCleanupService>();
            builder.Services.AddHostedService<AdminBootstrapOnStartup>();
            builder.Services.AddEmail(builder.Configuration);
            builder.Services.AddSingleton<SecretProtector>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<TwoFactorService>();
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(option =>
                {
                    option.TokenValidationParameters = new TokenValidationParameters
                    {
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                        ValidIssuer = jwtConfig["Issuer"],
                        ValidAudience = jwtConfig["Audience"],
                        ValidateIssuerSigningKey = true,
                        ValidateLifetime = true,
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        // Access tokens are short-lived; don't add the default 5 minutes of leeway
                        ClockSkew = TimeSpan.FromSeconds(30)
                    };

                    option.Events = new JwtBearerEvents
                    {
                        OnChallenge = context =>
                        {
                            // Skip the default response
                            context.HandleResponse();

                            context.Response.StatusCode = 401;
                            context.Response.ContentType = "application/json";
                            var result = System.Text.Json.JsonSerializer.Serialize(new { error = "Invalid token" });

                            return context.Response.WriteAsync(result);
                        }
                    };
                });

            var app = builder.Build();

            // Administrators are managed on the admin page now; the old setting does nothing
            var legacyAdminEmails = builder.Configuration.GetSection("Admin:Emails");
            if (legacyAdminEmails.Value is { Length: > 0 } || legacyAdminEmails.GetChildren().Any())
                app.Logger.LogWarning("Admin:Emails (ADMIN_EMAILS) is no longer used and is ignored. " +
                    "Without INITIAL_ADMIN_EMAIL the first account created is the administrator; with it set, " +
                    "that email's account becomes the administrator when confirmed. " +
                    "Administrators are managed on the admin page.");

            // Behind an HTTPS reverse proxy: take the client's IP and the original scheme from the
            // X-Forwarded-* headers (for rate limits, the sessions list and the Secure cookie flag).
            // Only turn this on when the API can't be reached except through the proxy. See docs/DEPLOYMENT.md.
            if (app.Configuration.GetValue("ForwardedHeaders:Enabled", false))
            {
                var forwarded = new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                };
                // By default only loopback proxies are trusted; the proxy is usually another container
                forwarded.KnownNetworks.Clear();
                forwarded.KnownProxies.Clear();
                foreach (var proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
                    forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
                app.UseForwardedHeaders(forwarded);
            }

            app.UseSecurityHeaders();
            app.UseCors("AllowFrontend");

            // Unexpected errors: log them and return a JSON 500 instead of an HTML page or stack trace.
            // (After UseCors so the browser can still read the error.)
            app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
            {
                var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                if (error is AdminLockTimeoutException)
                {
                    app.Logger.LogError(error, "Timed out on {Method} {Path}", context.Request.Method, context.Request.Path);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "5";
                    await context.Response.WriteAsJsonAsync(new { error = "The server is busy. Please try again in a moment." });
                    return;
                }
                app.Logger.LogError(error, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "An internal error has occurred" });
            }));

            // API docs only in Development, or when Swagger:Enabled is true (off by default in Docker)
            if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
            {
                app.UseSwagger();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/swagger/v1/swagger.json", "FileVault API v1");
                    options.DocumentTitle = "FileVault API";
                });
            }

            // Map Routes
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapFileRoutes();
            app.MapHealthCheckRoutes();
            app.MapAuthRoutes();
            app.MapThumbnailRoutes();
            app.MapFavouriteRoutes();
            app.MapAdminRoutes();
            app.MapShareRoutes();
            app.MapTrashRoutes();
            app.MapAccountEmailRoutes();
            app.MapUploadRoutes();
            app.MapTwoFactorRoutes();
            app.MapSessionRoutes();

            // Create storage folder if !exists
            var _storageRoot = builder.Configuration.GetValue<string>("StorageRoot");
            Directory.CreateDirectory(_storageRoot ?? throw new InvalidOperationException("StorageRoot is not set."));

            // Start the web application
            app.Run();
        }
    }
}
