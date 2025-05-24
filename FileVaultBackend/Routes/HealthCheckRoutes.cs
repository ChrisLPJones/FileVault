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
            // Map the /ping route, which returns "Pong" to indicate the app is alive
            app.MapGet("/ping", (ClaimsPrincipal user) =>
            {
                var id = user.FindFirst(ClaimTypes.NameIdentifier).Value;

                return Results.Ok($"Pong from {id}");
            });


            // Map the /pingsql rout, which return "Pong" to indicate the Sql Server successfully connected
            app.MapGet("/pingsql", async (DatabaseServices db) =>
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
