using FileVaultBackend.Routes;
using FileVaultBackend.Services;

namespace FileVaultBackend
{
    public partial class Program
    {
        private static void Main(string[] args)
        {


            var builder = WebApplication.CreateBuilder(args);
            // Inject Services
            builder.Services.AddScoped<FileServices>();
            builder.Services.AddScoped<DatabaseServices>();
            builder.Services.AddScoped<AuthServices>();

            var app = builder.Build();

            // Map Routes
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