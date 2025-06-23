using Backend.Services;
using Microsoft.Data.SqlClient;

namespace Backend.Routes
{
    public static class HealthCheckRoutes
    {
        public static void MapHealthCheckRoutes(this IEndpointRouteBuilder app)
        {





            // Health check endpoint to verify the API is running
            app.MapGet("/ping", () => Results.Ok(new { success = "Pong" }));






            // Health check endpoint to verify the SQL Server connection is working
            app.MapGet("/pingsql", async (DatabaseServices db) =>
            {
                try
                {
                    await db.CheckConnection();
                    return Results.Ok(new { success = "Connection successful" });
                }
                catch (SqlException)
                {
                    return Results.BadRequest(new { error = "Connection to SQL failed" });
                }
            });
        }
    }
}
