using Backend.Routes;
using Backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
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

            // Inject Services

            builder.Services.AddScoped<FileServices>();
            builder.Services.AddScoped<DatabaseServices>();
            builder.Services.AddScoped<AuthServices>();
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(option =>
                {
                    option.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                    {
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig["Key"])),
                        ValidIssuer = jwtConfig["Issuer"],
                        ValidAudience = jwtConfig["Audience"],
                        ValidateIssuerSigningKey = true,
                        ValidateLifetime = true,
                        ValidateIssuer = true,
                        ValidateAudience = true
                    };

                    option.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
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