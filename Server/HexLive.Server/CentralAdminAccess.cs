using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server;

/// <summary>Authority mode has no password/device-grant fallback. Every request revalidates rights.</summary>
public static class CentralAdminAccess
{
    public const string Marker = "HexLive.CentralAdmin";
    private const string Cookie = "__Host-hexlive-server";
    public static void Use(WebApplication app, IdentityClient identity)
    {
        var sessions = new ConcurrentDictionary<string, (string Key, DateTimeOffset Expires)>();
        app.Use(async (c, next) => {
            var path = c.Request.Path.Value ?? "";
            if (!path.StartsWith("/admin", StringComparison.Ordinal) && !path.StartsWith("/api/worlds/v1", StringComparison.Ordinal)) { await next(c); return; }
            c.Response.Headers.CacheControl = "no-store";
            c.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            var authorization = c.Request.Headers.Authorization.ToString();
            var key = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
            var cookie = c.Request.Cookies[Cookie];
            var cookieAuth = false;
            if (key.Length == 0 && cookie != null && sessions.TryGetValue(cookie, out var session))
            {
                if (session.Expires > DateTimeOffset.UtcNow) { key = session.Key; cookieAuth = true; }
                else sessions.TryRemove(cookie, out _);
            }
            bool SameOrigin() => Uri.TryCreate(c.Request.Headers.Origin, UriKind.Absolute, out var origin) && origin.Scheme == "https" && origin.Authority == c.Request.Host.Value;
            if (HttpMethods.IsPost(c.Request.Method) && (cookieAuth || path == "/admin/login") && !SameOrigin()) { c.Response.StatusCode = 403; return; }
            if (path is "/admin/forgot" or "/admin/reset" or "/admin/password" or "/admin/email" or "/admin/voice/decide" or "/admin/voice/revoke") { c.Response.StatusCode = 410; return; }
            if (path == "/admin/voice") { c.Response.Redirect("https://keys.62-146-235-120.sslip.io/"); return; }
            if (path == "/admin/logout")
            {
                if (cookie != null) sessions.TryRemove(cookie, out _);
                c.Response.Cookies.Delete(Cookie, new CookieOptions { Secure = true, Path = "/" }); c.Response.Redirect("/admin"); return;
            }
            if (path == "/admin/login" && HttpMethods.IsPost(c.Request.Method)) key = (await c.Request.ReadFormAsync())["key"].ToString();
            if (path == "/api/worlds/v1/login") { c.Response.StatusCode = 410; return; }
            IdentityClient.Identity? subject;
            try { subject = await identity.AuthenticateAsync(key, c.RequestAborted); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { c.Response.StatusCode = 503; return; }
            if (subject?.Permissions.Contains("server.admin") != true)
            {
                if (path == "/admin" && HttpMethods.IsGet(c.Request.Method))
                {
                    c.Response.ContentType = "text/html; charset=utf-8";
                    await c.Response.WriteAsync("<!doctype html><html lang='ru'><meta charset='utf-8'><title>HexLive</title><h1>Админка сервера</h1><form method='post' action='/admin/login'><input type='password' name='key' placeholder='Ключ доступа' required><button>Войти</button></form></html>"); return;
                }
                c.Response.StatusCode = subject == null ? 401 : 403; return;
            }
            if (path == "/admin/login")
            {
                foreach (var stale in sessions.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow)) sessions.TryRemove(stale.Key, out _);
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                sessions[token] = (key, DateTimeOffset.UtcNow.AddHours(2));
                c.Response.Cookies.Append(Cookie, token, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(2) });
                c.Response.Redirect("/admin"); return;
            }
            c.Items[Marker] = true; await next(c);
        });
    }
}
