using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server;

/// <summary>§114: reject stale clients without opening or creating any legacy storage.</summary>
public static class RetiredBugTracker
{
    public const string Url = "https://flashback.62-146-235-120.sslip.io";

    public static void Map(WebApplication app)
    {
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/bugs/v1") ||
                context.Request.Path.StartsWithSegments("/admin/bugs"))
            {
                context.Response.Headers.CacheControl = "no-store";
                await Results.Json(new
                {
                    error = "Legacy bug tracker retired. Use Flashback. Legacy Singapore report IDs may differ; find the report before editing.",
                    url = Url,
                    api = Url + "/api/bugs/v1",
                }, statusCode: StatusCodes.Status410Gone).ExecuteAsync(context);
                return;
            }
            await next(context);
        });
    }
}
