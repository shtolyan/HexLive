using HexLive.Simulation.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Releases;

public static class ClientCompatibilityEndpoints
{
    public static void Map(WebApplication app) =>
        app.MapGet("/api/client/v1/compatibility", (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { protocolVersion = Handshake.ProtocolVersion });
        });
}
