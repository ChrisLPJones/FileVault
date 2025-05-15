using FileVaultBackend.Services;

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
            app.MapGet("/pingsql", async (HttpRequest request, DatabaseServices database) =>
            {
                return await database.CheckConnection();
            });
        }
    }
}
