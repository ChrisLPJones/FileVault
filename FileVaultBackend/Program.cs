using FileVaultBackend.Routes;
using FileVaultBackend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace FileVaultBackend
{
    public partial class Program
    {
        private static void Main(string[] args)
        {


            var builder = WebApplication.CreateBuilder(args);
            var jwtConfig = builder.Configuration.GetSection("Jwt");
            
            // Inject Services

            builder.Services.AddScoped<FileServices>();
            builder.Services.AddScoped<DatabaseServices>();
            builder.Services.AddScoped<AuthServices>();
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(x =>
                {
                    x.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                    {
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig["Key"])),
                        ValidIssuer = jwtConfig["Issuer"],
                        ValidAudience = jwtConfig["Audience"],
                        ValidateIssuerSigningKey = true,
                        ValidateLifetime = true,
                        ValidateIssuer = true,
                        ValidateAudience = true
                    };
                });

            var app = builder.Build();

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