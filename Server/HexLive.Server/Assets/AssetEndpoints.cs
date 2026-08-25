using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace HexLive.Server.Assets
{

/// <summary>Read-only public §152 API. Publishing deliberately has no route.</summary>
public static class AssetEndpoints
{
    private const string ApiRoot = "/api/assets/v1";

    public static void Map(WebApplication app, AssetRegistryStore store)
    {
        app.MapGet(ApiRoot + "/index/{platform}/{runtimeProfile}",
            (HttpContext context, string platform, string runtimeProfile, long? after) =>
            {
                try
                {
                    var response = store.GetIndex(platform, runtimeProfile, after);
                    var etag = IndexEtag(platform, runtimeProfile, after, response.RegistryRevision);
                    if (Matches(context, etag))
                    {
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                    }

                    context.Response.Headers.ETag = etag;
                    context.Response.Headers.CacheControl = "no-cache";
                    return Results.Json(response);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        app.MapGet(ApiRoot + "/objects/{type}/{id}",
            (string type, string id, string? platform, string? profile) =>
            {
                if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(profile))
                {
                    return Results.BadRequest(new { error = "platform and profile are required" });
                }

                try
                {
                    var resolved = store.Resolve(type, id, platform, profile);
                    return resolved is null ? Results.NotFound() : Results.Json(resolved);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        app.MapPost(ApiRoot + "/resolve", (AssetResolveRequest request) =>
            {
                try
                {
                    return Results.Json(store.ResolveMany(request));
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        app.MapGet(ApiRoot + "/objects/{type}/{id}/history", (string type, string id) =>
            {
                try
                {
                    var history = store.ReadHistory(type, id);
                    return history.Count == 0 ? Results.NotFound() : Results.Json(history);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        app.MapMethods(ApiRoot + "/blobs/{sha256}", new[] { "GET", "HEAD" },
            (HttpContext context, string sha256) => ServeBlob(context, store, sha256));
    }

    private static IResult ServeBlob(
        HttpContext context, AssetRegistryStore store, string sha256)
    {
        if (!ContentIdentity.IsSha256(sha256))
        {
            return Results.BadRequest(new { error = "invalid SHA-256" });
        }

        if (!store.TryGetVerifiedBlob(sha256, out var path, out var size))
        {
            return Results.NotFound();
        }

        var etag = "\"" + sha256 + "\"";
        context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.AcceptRanges = "bytes";
        if (Matches(context, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // Results.File implements conditional/range responses and suppresses
        // the body for HEAD while preserving Content-Length.
        return Results.File(
            path,
            contentType: "application/octet-stream",
            lastModified: File.GetLastWriteTimeUtc(path),
            entityTag: new EntityTagHeaderValue(etag),
            enableRangeProcessing: true);
    }

    private static bool Matches(HttpContext context, string etag)
    {
        var value = context.Request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Split(',').Any(part =>
            part.Trim() == "*" || string.Equals(part.Trim(), etag, StringComparison.Ordinal));
    }

    private static string IndexEtag(
        string platform, string runtimeProfile, long? after, long registryRevision) =>
        $"\"assets-{platform}-{runtimeProfile}-{after?.ToString() ?? "full"}-{registryRevision}\"";
}

}
