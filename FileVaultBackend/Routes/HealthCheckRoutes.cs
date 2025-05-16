using FileVaultBackend.Services;
using Microsoft.Data.SqlClient;

namespace FileVaultBackend.Routes
{
    public static class HealthCheckRoutes
    {
        public static void MapHealthCheckRoutes(this IEndpointRouteBuilder app)
        {
            // Map the /ping route, which returns "Pong" to indicate the app is alive
            app.MapGet("/ping", () =>
            {
                // await sql.CheckConnection();
                return Results.Ok("Pong");
            });


            // Map the /pingsql rout, which return "Pong" to indicate the Sql Server successfully connected
            app.MapGet("/pingsql", async (HttpRequest request, DatabaseServices db) =>
            {
                try
                {
                    await db.CheckConnection();
                    return Results.Ok("Connection successful");
                }
                catch (SqlException)
                {
                    return Results.BadRequest($"Connection to Sql failed");
                }
            });
        }
    }
}
