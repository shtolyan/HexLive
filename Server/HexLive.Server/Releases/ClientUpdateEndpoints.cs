using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace HexLive.Server.Releases;

// §166: serves only archives already verified and promoted by the Drive synchronizer.
public static class ClientUpdateEndpoints
{
    public static void Map(WebApplication app, string root)
    {
        const string route = "/api/releases/v2/client/{platform}/{architecture}/stable";
        app.MapGet(route + "/latest", (HttpContext context, string platform, string architecture) =>
        {
            if (!Supported(platform, architecture)) return Results.NotFound();
            var directory = Path.Combine(root, platform, architecture);
            var path = Path.Combine(directory, "latest.json");
            if (!File.Exists(path)) return Results.NotFound();
            // Renames by the synchronizer ensure readers never observe a partial JSON document.
            var bytes = File.ReadAllBytes(path);
            var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\"";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = etag;
            if (context.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            return Results.Bytes(bytes, "application/json");
        });
        app.MapMethods(route + "/blobs/{sha256}", new[] { "GET", "HEAD" },
            (HttpContext context, string platform, string architecture, string sha256) =>
        {
            if (!Supported(platform, architecture) || !PlayerReleaseStore.IsSha256(sha256)) return Results.NotFound();
            var path = Path.Combine(root, platform, architecture, "blobs", sha256.ToLowerInvariant());
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return Results.NotFound();
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            return Results.File(path, "application/zip", entityTag: new EntityTagHeaderValue("\"" + sha256.ToLowerInvariant() + "\""),
                enableRangeProcessing: true);
        });
    }
    public static bool Supported(string platform, string architecture) =>
        platform == "windows" && architecture == "x64" || platform == "macos" && (architecture == "arm64" || architecture == "x64");
}
