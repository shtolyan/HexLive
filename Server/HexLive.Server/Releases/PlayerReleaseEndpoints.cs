using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace HexLive.Server.Releases;

/// <summary>§156 read-only Windows Player release API.</summary>
public static class PlayerReleaseEndpoints
{
    private const string Root = "/api/releases/v1/windows";

    public static void Map(WebApplication app, PlayerReleaseStore store)
    {
        app.MapMethods(Root + "/installer", new[] { "GET", "HEAD" }, (HttpContext context) =>
        {
            if (!store.TryGetInstaller(out var path, out var size)) return Results.NotFound();
            var modified = File.GetLastWriteTimeUtc(path);
            var etag = $"\"installer-{size:x}-{modified.Ticks:x}\"";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.AcceptRanges = "bytes";
            if (Matches(context, etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.File(path, contentType: "application/vnd.microsoft.portable-executable",
                fileDownloadName: "HexLiveInstaller.exe", lastModified: modified,
                entityTag: new EntityTagHeaderValue(etag), enableRangeProcessing: true);
        });

        app.MapGet(Root + "/latest", (HttpContext context) =>
        {
            using var document = store.ReadLatest();
            if (document is null) return Results.NotFound();
            var root = document.RootElement;
            if (!root.TryGetProperty("playerRelease", out var release) ||
                !release.TryGetProperty("archiveSha256", out var shaNode))
                return Results.Problem("windows-latest.json has no playerRelease.archiveSha256", statusCode: 500);
            var sha = shaNode.GetString() ?? string.Empty;
            var etag = "\"release-" + sha + "\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "no-cache";
            if (Matches(context, etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(root), "application/json");
        });

        app.MapMethods(Root + "/blobs/{sha256}", new[] { "GET", "HEAD" },
            (HttpContext context, string sha256) =>
            {
                if (!PlayerReleaseStore.IsSha256(sha256))
                    return Results.BadRequest(new { error = "invalid SHA-256" });
                if (!store.TryGetBlob(sha256, out var path, out _)) return Results.NotFound();
                var etag = "\"" + sha256.ToLowerInvariant() + "\"";
                context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
                context.Response.Headers.ETag = etag;
                context.Response.Headers.AcceptRanges = "bytes";
                if (Matches(context, etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.File(
                    path,
                    contentType: "application/zip",
                    lastModified: File.GetLastWriteTimeUtc(path),
                    entityTag: new EntityTagHeaderValue(etag),
                    enableRangeProcessing: true);
            });
    }

    private static bool Matches(HttpContext context, string etag) =>
        context.Request.Headers.IfNoneMatch.ToString().Split(',').Any(value =>
            value.Trim() == "*" || string.Equals(value.Trim(), etag, StringComparison.Ordinal));
}
