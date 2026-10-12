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
        return reader.ReadToEnd();
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
            "frame-src " + authOrigin + " https://accounts.google.com; img-src 'self' data:;";

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
