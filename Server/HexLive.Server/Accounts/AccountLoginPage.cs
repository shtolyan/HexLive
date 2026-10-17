using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Accounts;

/// <summary>§171.12: embedded, project-independent login/character page; public web configuration only.</summary>
public static class AccountLoginPage
{
    private static readonly Lazy<string> Html = new(() => {
        using var stream = typeof(AccountLoginPage).Assembly.GetManifestResourceStream("HexLive.Accounts.Login.html")
            ?? throw new InvalidOperationException("Missing embedded login page.");
        using var reader = new StreamReader(stream);
        // The CSP hashes below must equal what the browser hashes, and an HTML parser turns
        // CRLF into LF before hashing an inline block. A CRLF copy of the page (Singapore
        // build, 12.10.2026) blocked its own style and script: an unstyled, dead login page.
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace('\r', '\n');
    });

    public const string GooglePhotoOrigin = "https://lh3.googleusercontent.com";
    private const string ArtPrefix = "HexLive.Accounts.Site.";

    private static readonly Lazy<System.Collections.Generic.Dictionary<string, byte[]>> Art = new(() => {
        var assembly = typeof(AccountLoginPage).Assembly;
        var art = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ArtPrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            art[resource[ArtPrefix.Length..]] = copy.ToArray();
        }
        return art;
    });

    private static string Hashes(string tag) => string.Join(" ",
        Regex.Matches(Html.Value, $"<{tag}(?:\\s[^>]*)?>([\\s\\S]*?)</{tag}>")
            .Select(m => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value))) + "'"));

    public static void Map(WebApplication app, ServerOptions options)
    {
        if (options.FirebaseProject is null) return;
        var authOrigin = "https://" + options.FirebaseAuthDomain;
        var csp = "default-src 'none'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; " +
            "form-action 'self'; script-src " + Hashes("script") +
            " https://www.gstatic.com/firebasejs/12.3.0/ https://apis.google.com; " +
            "style-src " + Hashes("style") + "; " +
            "connect-src 'self' https://identitytoolkit.googleapis.com https://securetoken.googleapis.com " +
            "https://www.googleapis.com " + authOrigin +
            (options.FirebaseEmulator is { } emu ? " http://" + emu : "") + "; " +
            "frame-src " + authOrigin + " https://accounts.google.com; " +
            // The Google account photo on the profile card (§171.12).
            "img-src 'self' data: " + GooglePhotoOrigin + ";";

        // §171.12: the page's art, embedded like the page itself. Names are a closed set.
        app.MapGet("/login/art/{name}", (HttpContext context, string name) => {
            if (!Art.Value.TryGetValue(name, out var bytes)) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=86400";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(bytes, "image/webp");
        });

        app.MapGet(AccountEndpoints.Prefix + "/client-config", (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { projectId = options.FirebaseProject, apiKey = options.FirebaseApiKey,
                authDomain = options.FirebaseAuthDomain, providers = options.FirebaseProviders,
                emulator = options.FirebaseEmulator, requireVerifiedEmail = options.FirebaseRequireVerifiedEmail });
        });
        // ASP.NET routing matches both /login and /login/ with this single endpoint.
        app.MapGet("/login", (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = csp;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // Firebase popup must keep its opener even when /play/ uses cross-origin isolation.
            context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
            return Results.Content(Html.Value, "text/html; charset=utf-8");
        });
    }
}
