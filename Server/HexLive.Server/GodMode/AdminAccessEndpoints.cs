using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using HexLive.Server.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.GodMode;

public static class AdminAccessEndpoints
{
    public static void Map(WebApplication app, AdminAccess access, AdminSessions sessions, AdminCommandBus bus, WorldSupervisor worlds)
    {
        bool Signed(HttpContext c) => sessions.IsSignedIn(c.Request.Cookies["hexlive_admin"] ?? "");
        bool SameOrigin(HttpContext c) => Uri.TryCreate(c.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            && origin.Authority == c.Request.Host.Value;
        string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        string Page()
        {
            var html = new StringBuilder("<!doctype html><html lang='ru'><meta charset='utf-8'><title>Права администратора</title><style>body{background:#141d24;color:#dae2e8;font:16px sans-serif;max-width:1000px;margin:32px auto}a{color:#63ccc4}article{padding:20px;border:1px solid #435662;border-radius:10px;margin:14px 0}button,input,select{padding:8px;margin:6px}code{overflow-wrap:anywhere}</style><a href='/admin'>Сервер</a> · <a href='/admin/voice/history'>История команд</a><h1>Права администратора</h1><p>В игре нажать «Запросить права администратора». Здесь одобрить запрос на срок или бессрочно.</p>");
            foreach (var value in access.Requests())
            {
                var r = JsonSerializer.SerializeToElement(value); var client = r.GetProperty("client").GetString()!;
                var state = r.GetProperty("state").GetString()!; var id = r.GetProperty("id").GetString()!;
                var ids = worlds.Assignments?.AdminAssignedIds(client) ?? Array.Empty<int>();
                var names = worlds.Host.Read(w => ids.Select(n => w.Entities.Npcs.TryGetValue(new HexLive.Simulation.Common.EntityId(n), out var npc) ? npc.DisplayName : "#" + n).ToArray());
                var label = state switch { "pending" => "Ожидает решения", "approved" => "Одобрено", "rejected" => "Отклонено", "expired" => "Срок истёк", _ => "Отозвано" };
                html.Append("<article><strong>").Append(label).Append("</strong><p>Клиент: <code>").Append(E(client)).Append("</code><br>Персонажи: ").Append(E(string.Join(", ", names)))
                    .Append("<br>Запрос: ").Append(E(r.GetProperty("createdUtc").ToString())).Append("<br>").Append(r.GetProperty("connected").GetBoolean() ? "На связи" : "Не на связи")
                    .Append("<br>Доступ до: ").Append(r.GetProperty("expiresUtc").ValueKind == JsonValueKind.Null ? (state == "approved" ? "Бессрочно" : "—") : E(r.GetProperty("expiresUtc").ToString())).Append("</p>");
                if (state == "pending") html.Append("<form method='post' action='/admin/voice/decide'><input type='hidden' name='id' value='").Append(E(id))
                    .Append("'><label>Срок <input name='amount' type='number' min='0.01' step='any' value='1'></label><select name='unit'><option value='forever'>Бессрочно</option><option value='minutes'>Минут</option><option value='hours'>Часов</option><option value='days'>Дней</option></select><button name='decision' value='approve'>Одобрить</button><button name='decision' value='reject'>Отклонить</button></form>");
                if (state is "approved" or "pending") html.Append("<form method='post' action='/admin/voice/revoke'><input type='hidden' name='clientId' value='").Append(E(client)).Append("'><button>Отозвать права клиента</button></form>");
                html.Append("</article>");
            }
            foreach (var client in access.Clients) html.Append("<form method='post' action='/admin/voice/revoke'><code>").Append(E(client)).Append("</code><input type='hidden' name='clientId' value='").Append(E(client)).Append("'><button>Отозвать действующий доступ</button></form>");
            return html.Append("</html>").ToString();
        }
        app.MapGet("/admin/voice", (HttpContext c) => { c.Response.Headers.CacheControl = "no-store"; return Signed(c) ? Results.Content(Page(), "text/html; charset=utf-8") : Results.Redirect("/admin"); });
        app.MapGet("/admin/voice/history", (HttpContext c) => { c.Response.Headers.CacheControl = "no-store"; return Signed(c) ? Results.Json(bus.RecentHistory(), AdminCommandBus.Json) : Results.Unauthorized(); });
        app.MapPost("/admin/voice/decide", async (HttpContext c) =>
        {
            if (!Signed(c) || !SameOrigin(c)) return Results.Unauthorized();
            var form = await c.Request.ReadFormAsync();
            try
            {
                var approve = form["decision"] == "approve";
                if (!approve && form["decision"] != "reject") return Results.BadRequest("Invalid decision");
                TimeSpan? duration = null;
                if (approve && form["unit"] != "forever")
                {
                    var scale = form["unit"].ToString() switch { "minutes" => 60d, "hours" => 3600d, "days" => 86400d, _ => 0d };
                    if (scale == 0 || !double.TryParse(form["amount"], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n <= 0 || n * scale >= TimeSpan.MaxValue.TotalSeconds) return Results.BadRequest("Invalid duration");
                    duration = TimeSpan.FromSeconds(n * scale);
                }
                access.Decide(form["id"].ToString(), approve, duration); return Results.Redirect("/admin/voice");
            }
            catch (Exception e) when (e is ArgumentException or OverflowException) { return Results.BadRequest("Запрос уже обработан или срок некорректен"); }
        });
        app.MapPost("/admin/voice/revoke", async (HttpContext c) =>
        { if (!Signed(c) || !SameOrigin(c)) return Results.Unauthorized(); var form = await c.Request.ReadFormAsync(); access.Revoke(form["clientId"].ToString()); return Results.Redirect("/admin/voice"); });
    }
}
