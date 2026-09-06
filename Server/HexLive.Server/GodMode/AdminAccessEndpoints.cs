using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HexLive.Server.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.GodMode;

public static class AdminAccessEndpoints
{
    public static void Map(WebApplication app, AdminAccess access, AdminSessions sessions, AdminCommandBus bus)
    {
        bool Signed(HttpContext c) => sessions.IsSignedIn(c.Request.Cookies["hexlive_admin"] ?? "");
        bool SameOrigin(HttpContext c) => Uri.TryCreate(c.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            && origin.Authority == c.Request.Host.Value;
        string Page(string code = "") => "<!doctype html><html lang='ru'><meta charset='utf-8'><title>Голосовая админка</title>" +
            "<body><a href='/admin'>Сервер</a><h1>Голосовая админка</h1><p><a href=\"/admin/voice/history\">Последние 100 команд</a></p><p>Client ID показан в игровой панели «Админ».</p>" +
            "<form method='post'><input name='clientId' required maxlength='32' placeholder='Client ID'><button>Выдать код на 5 минут</button></form>" +
            (code.Length == 0 ? "" : "<p>Одноразовый код: <code>" + WebUtility.HtmlEncode(code) + "</code></p>") +
            string.Join("", access.Clients.Select(id => "<form method='post' action='/admin/voice/revoke'><input type='hidden' name='clientId' value='" +
                WebUtility.HtmlEncode(id) + "'><code>" + WebUtility.HtmlEncode(id) + "</code><button>Отозвать</button></form>")) + "</body></html>";
        app.MapGet("/admin/voice/history", (HttpContext c) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Signed(c) ? Results.Json(bus.RecentHistory(), AdminCommandBus.Json) : Results.Unauthorized();
        });
        app.MapGet("/admin/voice", (HttpContext c) => Signed(c) ? Results.Content(Page(), "text/html; charset=utf-8") : Results.Redirect("/admin"));
        app.MapPost("/admin/voice", async (HttpContext c) =>
        {
            if (!Signed(c) || !SameOrigin(c)) return Results.Unauthorized();
            c.Response.Headers.CacheControl = "no-store";
            var form = await c.Request.ReadFormAsync();
            try { return Results.Content(Page(access.Issue(form["clientId"].ToString())), "text/html; charset=utf-8"); }
            catch (ArgumentException) { return Results.BadRequest("Invalid client id"); }
        });
        app.MapPost("/admin/voice/revoke", async (HttpContext c) =>
        {
            if (!Signed(c) || !SameOrigin(c)) return Results.Unauthorized();
            var form = await c.Request.ReadFormAsync(); access.Revoke(form["clientId"].ToString()); return Results.Redirect("/admin/voice");
        });
    }
}
