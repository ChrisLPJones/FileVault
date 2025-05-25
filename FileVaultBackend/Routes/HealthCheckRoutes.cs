using FileVaultBackend.Services;
using Microsoft.Data.SqlClient;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace FileVaultBackend.Routes
{
    public static class HealthCheckRoutes
    {
        public static void MapHealthCheckRoutes(this IEndpointRouteBuilder app)
        {
            // Health check endpoint to verify the API is running
            app.MapGet("/ping", () =>
            {
                return Results.Ok("Pong");
            });

            // Health check endpoint to verify the SQL Server connection is working
            app.MapGet("/pingsql", async (DatabaseServices db) =>
            {
                try
                {
                    await db.CheckConnection();
                    return Results.Ok("Connection successful");
                }
                catch (SqlException)
                {
                    return Results.BadRequest("Connection to SQL failed");
                }
            });
        }
    }
}
