using Backend.Routes;
using Backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

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

            // Inject Services
            builder.Services.AddCors(options => {
                options.AddPolicy("AllowFrontend", policy =>
                {
                    policy.WithOrigins("http://localhost:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod();
                    });
                    });
            builder.Services.AddSingleton<FileEncryption>();
            builder.Services.AddScoped<FileServices>();
            builder.Services.AddScoped<DatabaseServices>();
            builder.Services.AddScoped<AuthServices>();
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
                        ValidateAudience = true
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
            
            app.UseCors("AllowFrontend");

            // Map Routes
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapFileRoutes();
            app.MapHealthCheckRoutes();
            app.MapAuthRoutes();

            // Create storage folder if !exists
            var _storageRoot = builder.Configuration.GetValue<string>("StorageRoot");
            Directory.CreateDirectory(_storageRoot);

            // Start the web application
            app.Run();
        }
    }
}
