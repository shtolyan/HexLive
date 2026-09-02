using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using HexLive.Server.Assets;
using HexLive.Simulation.Content;

namespace HexLive.Server.Admin
{

/// <summary>
/// The panel's HTML. One file, no framework, no CDN — a self-hosted game server
/// should not need the internet to render its own settings page, and an admin
/// panel that pulls scripts from elsewhere is exactly the thing you do not want
/// sitting on an open port.
/// </summary>
public static class AdminPages
{
    private const string Style = @"
:root{color-scheme:dark;--bg:#0d1117;--card:#161b22;--line:#30363d;--ink:#e6edf3;
--dim:#8b949e;--gold:#f0b429;--good:#3fb950;--bad:#f85149}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);
font:15px/1.5 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}
.wrap{max-width:1180px;margin:0 auto;padding:32px 20px 64px}
h1{font-size:22px;margin:0 0 4px;letter-spacing:.3px}
h2{font-size:13px;text-transform:uppercase;letter-spacing:1.2px;color:var(--dim);
margin:28px 0 10px;font-weight:600}
.sub{color:var(--dim);font-size:13px;margin:0 0 24px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:14px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:14px}
.stat{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
.stat b{display:block;font-size:22px;font-weight:600;margin-bottom:2px}
.stat span{color:var(--dim);font-size:12px;text-transform:uppercase;letter-spacing:.8px}
button{font:inherit;font-weight:600;border-radius:9px;border:1px solid var(--line);
background:#21262d;color:var(--ink);padding:9px 16px;cursor:pointer}
button:hover{background:#2d333b}
button.primary{background:var(--gold);border-color:var(--gold);color:#1a1205}
button.danger{border-color:#5d2220;color:#ff9d95}
button.danger:hover{background:#3d1512}
input,select,textarea{font:inherit;background:#0d1117;border:1px solid var(--line);color:var(--ink);
border-radius:9px;padding:9px 12px;width:100%;max-width:340px}
textarea{max-width:none;min-height:90px;resize:vertical}
label{display:block;font-size:13px;color:var(--dim);margin:12px 0 5px}
form{display:inline}
.block{display:block}.field-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:0 18px}
.row{display:flex;gap:9px;flex-wrap:wrap;align-items:center}
.note{background:#1c1a10;border:1px solid #4d3c12;color:#f5d999;
border-radius:10px;padding:13px 16px;margin-bottom:18px;font-size:14px}
.alarm{background:#2a1315;border-color:#5d2220;color:#ff9d95}
.ok{color:var(--good)}.bad{color:var(--bad)}.dim{color:var(--dim)}
code{background:#0d1117;border:1px solid var(--line);border-radius:5px;padding:1px 6px;font-size:13px}
a{color:var(--gold)}
table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;padding:9px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--dim);font-size:11px;text-transform:uppercase;letter-spacing:.6px}.scroll{overflow-x:auto}
.badge{display:inline-block;border:1px solid var(--line);border-radius:999px;padding:1px 8px;font-size:11px}.badge.active{color:var(--good)}.badge.retired{color:var(--bad)}
.checks{display:grid;grid-template-columns:repeat(auto-fit,minmax(125px,1fr));gap:5px 12px}.checks label{margin:0;color:var(--ink)}.checks input{width:auto;margin-right:6px}
.nav{display:flex;justify-content:space-between;gap:12px;align-items:center;margin-bottom:22px}.mono{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;word-break:break-all}
.thumb{position:relative;width:58px;height:58px;display:grid;place-items:center;overflow:hidden;border:1px solid var(--line);border-radius:10px;background:#0d1117;color:var(--dim);font-size:20px;flex:0 0 auto}
.thumb img{position:absolute;inset:0;width:100%;height:100%;object-fit:contain;background:#0d1117}.thumb.detail{width:132px;height:132px;border-radius:14px}
.object-cell{display:flex;gap:12px;align-items:center;min-width:270px}.record-head{display:flex;gap:18px;align-items:center}
.pager{display:flex;gap:6px;align-items:center;flex-wrap:wrap;margin-top:16px}.pager a,.pager span{display:inline-block;min-width:34px;text-align:center;border:1px solid var(--line);border-radius:8px;padding:6px 9px;text-decoration:none}.pager .current{background:var(--gold);border-color:var(--gold);color:#1a1205;font-weight:700}.pager .gap{border:0;color:var(--dim)}
";

    public static string Login(string? error, bool temporaryPassword)
    {
        var body = new StringBuilder();
        body.Append("<h1>HexLive server</h1><p class='sub'>Admin panel</p>");

        if (temporaryPassword)
        {
            body.Append("<div class='note'>First run — sign in with the one-time password " +
                        "printed in the server console.</div>");
        }

        if (!string.IsNullOrEmpty(error))
        {
            body.Append("<div class='note alarm'>").Append(Escape(error)).Append("</div>");
        }

        body.Append(@"<div class='card'>
<form method='post' action='/admin/login'>
<label>Password</label>
<input type='password' name='password' autofocus autocomplete='current-password'>
<div class='row' style='margin-top:16px'>
<button class='primary' type='submit'>Sign in</button>
<a href='/admin/forgot' class='dim' style='font-size:13px'>Forgot password?</a>
</div></form></div>");

        return Page("Sign in", body.ToString());
    }

    public static string Forgot(string? message, bool hasEmail, bool canSendMail)
    {
        var body = new StringBuilder();
        body.Append("<h1>Password reset</h1>");

        if (!string.IsNullOrEmpty(message))
        {
            body.Append("<div class='note'>").Append(Escape(message)).Append("</div>");
        }

        if (!hasEmail)
        {
            body.Append("<div class='note alarm'>No recovery email was ever set on this server, " +
                        "so a link cannot be mailed. It will be printed to the server console instead — " +
                        "you will need shell access to the machine.</div>");
        }
        else if (!canSendMail)
        {
            body.Append("<div class='note'>No SMTP is configured (<code>HEXLIVE_SMTP_HOST</code>), " +
                        "so the link will be printed to the server console rather than emailed.</div>");
        }

        body.Append(@"<div class='card'>
<p class='dim' style='margin-top:0'>A single-use link, valid for 30 minutes, will be sent to the
recovery address on file.</p>
<form method='post' action='/admin/forgot'>
<button class='primary' type='submit'>Send reset link</button>
<a href='/admin' class='dim' style='margin-left:12px;font-size:13px'>Back</a>
</form></div>");

        return Page("Password reset", body.ToString());
    }

    public static string ResetForm(string? token, string? error)
    {
        var body = new StringBuilder();
        body.Append("<h1>Choose a new password</h1>");
        if (!string.IsNullOrEmpty(error))
        {
            body.Append("<div class='note alarm'>").Append(Escape(error)).Append("</div>");
        }

        body.Append("<div class='card'><form method='post' action='/admin/reset'>")
            .Append("<input type='hidden' name='token' value='").Append(Escape(token)).Append("'>")
            .Append(@"<label>New password (10 characters or more)</label>
<input type='password' name='password' autofocus autocomplete='new-password'>
<div class='row' style='margin-top:16px'><button class='primary' type='submit'>Set password</button></div>
</form></div>");

        return Page("Reset password", body.ToString());
    }

    public static string Dashboard(WorldHost host, AdminAccount account, bool insecureTransport,
        string? notice, bool canSendMail, AssetCatalogOverview catalog,
        long pinnedCatalogRevision, int actionableBugs)
    {
        var census = host.Census();
        var body = new StringBuilder();

        body.Append("<h1>HexLive server</h1><p class='sub'>The colony runs here, with or without a viewer.</p>");

        if (!string.IsNullOrEmpty(notice))
        {
            body.Append("<div class='note'>").Append(Escape(notice)).Append("</div>");
        }

        if (insecureTransport)
        {
            // Said once, plainly, on the page where the password gets typed.
            body.Append("<div class='note alarm'><b>This page is served over plain HTTP.</b> " +
                        "Your password crosses the network in the clear. On anything but localhost, " +
                        "put this behind a reverse proxy with TLS (Caddy or nginx) before using it.</div>");
        }

        if (account.SetupIncomplete)
        {
            body.Append("<div class='note'><b>Finish setup.</b> ")
                .Append(account.PasswordIsTemporary ? "Change the temporary password" : string.Empty)
                .Append(account.PasswordIsTemporary && account.Email.Length == 0 ? " and add " : string.Empty)
                .Append(account.Email.Length == 0 ? "a recovery email" : string.Empty)
                .Append(" — otherwise you can be locked out of your own server.</div>");
        }

        var uptime = host.Uptime;
        body.Append("<div class='grid'>")
            .Append(Stat(host.Tick.ToString("N0"), "tick"))
            .Append(Stat($"{census.alive}/{census.total}", "colonists"))
            .Append(Stat(census.objects.ToString(), "objects"))
            .Append(Stat($"{host.MeasuredTicksPerSecond:0.00}", "ticks / sec"))
            .Append(Stat($"{host.AverageTickMs:0.0} ms", "per tick"))
            .Append(Stat($"{(int)uptime.TotalHours}h {uptime.Minutes:00}m", "uptime"))
            .Append("</div>");

        var pendingCatalog = catalog.RegistryRevision != pinnedCatalogRevision;
        body.Append("<h2>Content catalog</h2><div class='card'><div class='row'>")
            .Append("<a href='/admin/catalog'><button type='button' class='primary'>Open catalog</button></a>")
            .Append("<span class='dim'>live <code>").Append(catalog.RegistryRevision)
            .Append("</code> · world <code>").Append(pinnedCatalogRevision).Append("</code> · ")
            .Append(catalog.Records.Count).Append(" records · ")
            .Append(catalog.Wear.Count(value => value.Spawnable)).Append(" spawnable clothes")
            .Append(pendingCatalog
                ? " · <b class='bad'>changes pending apply</b>"
                : " · <b class='ok'>world is current</b>")
            .Append("</span></div></div>");

        body.Append("<h2>Bug tracker</h2><div class='card'><div class='row'>")
            .Append("<a href='/admin/bugs'><button type='button' class='primary'>Open bug tracker</button></a>")
            .Append("<span class='dim'>").Append(actionableBugs)
            .Append(" actionable reports · central SQLite store</span></div></div>");

        body.Append("<h2>World clock</h2><div class='card'><div class='row'>");
        body.Append(host.IsPaused
            ? "<form method='post' action='/admin/resume'><button class='primary'>Resume</button></form>"
            : "<form method='post' action='/admin/pause'><button>Pause</button></form>");
        body.Append("<span class='dim'>Now: ")
            .Append(host.IsPaused ? "<b class='bad'>paused</b>" : "<b class='ok'>running</b>")
            .Append(" at ").Append(host.SpeedMultiplier.ToString("0.##")).Append("×</span>");
        body.Append("</div>");

        body.Append("<label>Speed</label><div class='row'>")
            .Append(SpeedButton(host.SpeedMultiplier, 1f))
            .Append(SpeedButton(host.SpeedMultiplier, 4f))
            .Append(SpeedButton(host.SpeedMultiplier, 16f))
            .Append(SpeedButton(host.SpeedMultiplier, 50f))
            .Append(@"</div>
<p class='dim' style='margin-bottom:0;font-size:13px'>Fast-forward is an operator tool: it burns
through colony days for <em>everyone</em> watching and multiplies the stream every viewer receives.
Connected clients cannot do it — only this panel can.</p></div>");

        body.Append("<h2>Account</h2><div class='card'>");
        body.Append("<form method='post' action='/admin/password'>")
            .Append("<label>Change password</label>")
            .Append("<input type='password' name='password' autocomplete='new-password' placeholder='at least 10 characters'>")
            .Append("<div class='row' style='margin-top:12px'><button>Update password</button>")
            .Append("<span class='dim' style='font-size:13px'>Signs out every browser, including this one.</span>")
            .Append("</div></form>");

        body.Append("<form method='post' action='/admin/email' style='display:block;margin-top:18px'>")
            .Append("<label>Recovery email")
            .Append(account.Email.Length == 0
                ? " <span class='bad'>— not set</span>"
                : " <span class='ok'>— " + Escape(account.Email) + "</span>")
            .Append("</label>")
            .Append("<input type='email' name='email' placeholder='you@example.com' value='")
            .Append(Escape(account.Email)).Append("'>")
            .Append("<div class='row' style='margin-top:12px'><button>Save email</button>");

        if (!canSendMail)
        {
            body.Append("<span class='dim' style='font-size:13px'>No SMTP configured — reset links " +
                        "will be printed to the server console.</span>");
        }

        body.Append("</div></form></div>");

        body.Append("<h2>Danger zone</h2><div class='card'>");
        body.Append(@"<form method='post' action='/admin/newworld' onsubmit=""return confirm(
'Start a NEW colony? The current one stops immediately. Its save is archived next to the current one, but the running world is gone.')"">
<label>New world — seed (blank for random)</label>
<input type='text' name='seed' placeholder='e.g. 12345' inputmode='numeric'>
<label style='margin-top:8px'>Game mode</label>
<select name='mode'>
<option value='feud'>Feud with outsiders (classic island)</option>
<option value='bigisland'>Big island survival (3 camps)</option>
<option value='hugeisland'>Huge island survival (6 camps + outsiders)</option>
<option value='maniac'>Maniac (maxed armored woman + machete)</option>
<option value='islands'>Islands (six islands joined by fords, one castaway each)</option>
</select>
<div class='row' style='margin-top:12px'><button class='danger'>Start a new world</button>
<span class='dim' style='font-size:13px'>The existing save is archived, not deleted.</span></div></form>");

        body.Append(@"<form method='post' action='/admin/shutdown' style='display:block;margin-top:20px'
onsubmit=""return confirm('Shut the server down? The world is saved first, and nobody can reconnect until it is started again.')"">
<div class='row'><button class='danger'>Save and shut down</button>
<span class='dim' style='font-size:13px'>Saves before exiting.</span></div></form>");

        body.Append("</div>");

        body.Append("<h2>Session</h2><div class='card'><div class='row'>")
            .Append("<form method='post' action='/admin/logout'><button>Sign out</button></form>")
            .Append("<span class='dim' style='font-size:13px'>seed <code>")
            .Append(host.Seed).Append("</code> · mode <code>")
            .Append(host.Mode).Append("</code> · topology <code>0x")
            .Append(host.TopologyChecksum.ToString("X8")).Append("</code></span>")
            .Append("</div></div>");

        // The numbers go stale in seconds; refresh rather than making the
        // operator wonder whether the world is stuck. ⭐ Но НИКОГДА поверх
        // недописанной формы: слепой reload стирал пароль и email посреди
        // набора, и оператор вводил их через буфер обмена с третьей попытки.
        // Правило простое: тронул любое поле или держишь в нём фокус —
        // автообновление замирает, пока форма не отправлена (submit сам
        // уводит со страницы) или поля не потеряли и фокус, и правки.
        body.Append(@"<script>
(function(){
  var dirty = false;
  document.addEventListener('input', function(){ dirty = true; });
  function editing(){
    var a = document.activeElement;
    return a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA');
  }
  function tick(){
    if (dirty || editing()) { setTimeout(tick, 2000); return; }
    location.reload();
  }
  setTimeout(tick, 10000);
})();
</script>");

        return Page("Dashboard", body.ToString());
    }

    public static string Catalog(AssetCatalogOverview catalog, long pinnedRevision,
        string? query, string? type, string? state, string? category, int page, string? notice)
    {
        const int pageSize = 10;
        query = query?.Trim() ?? string.Empty;
        type = type?.Trim() ?? string.Empty;
        state = state?.Trim() ?? string.Empty;
        category = category?.Trim() ?? string.Empty;
        var wearById = catalog.Wear.ToDictionary(value => value.Record.Id, StringComparer.Ordinal);
        var filtered = catalog.Records.Where(record =>
            (type.Length == 0 || record.Type == type) &&
            (state.Length == 0 || record.State == state) &&
            (category.Length == 0 ||
             (record.Type == "wear" && wearById.TryGetValue(record.Id, out var categoryWear) &&
              string.Equals(categoryWear.Category.ToString(), category, StringComparison.OrdinalIgnoreCase))) &&
            MatchesSearch(record, wearById, query)).ToArray();
        var pageCount = Math.Max(1, (filtered.Length + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pageCount);
        var records = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        var from = filtered.Length == 0 ? 0 : (page - 1) * pageSize + 1;
        var to = Math.Min(page * pageSize, filtered.Length);

        var body = new StringBuilder();
        body.Append("<div class='nav'><div><h1>Каталог контента</h1><p class='sub' style='margin:0'>")
            .Append("Атомарные объекты сервера и параметры симуляции.</p></div>")
            .Append("<a href='/admin'>К панели сервера</a></div>");
        if (!string.IsNullOrEmpty(notice))
        {
            body.Append("<div class='note'>").Append(Escape(notice)).Append("</div>");
        }

        var pending = catalog.RegistryRevision != pinnedRevision;
        body.Append("<div class='grid'>")
            .Append(Stat(catalog.RegistryRevision.ToString(CultureInfo.InvariantCulture), "ревизия реестра"))
            .Append(Stat(pinnedRevision.ToString(CultureInfo.InvariantCulture), "ревизия мира"))
            .Append(Stat(catalog.Records.Count.ToString(CultureInfo.InvariantCulture), "всего объектов"))
            .Append(Stat(catalog.Wear.Count(value => value.Spawnable).ToString(CultureInfo.InvariantCulture), "доступно одежды"))
            .Append(Stat(catalog.Wear.Count(value => value.Record.State == "retired").ToString(CultureInfo.InvariantCulture), "снято одежды"))
            .Append(Stat(catalog.UnconfiguredWear.ToString(CultureInfo.InvariantCulture), "без параметров"))
            .Append("</div>");

        if (pending)
        {
            body.Append("<div class='note' style='margin-top:18px'><div class='row'><b>Есть неприменённые изменения.</b>")
                .Append("<span>Текущий мир пока использует ревизию ").Append(pinnedRevision)
                .Append(". Применение сохранит и перезагрузит этот же мир.</span>")
                .Append("<form method='post' action='/admin/catalog/apply' onsubmit=\"return confirm('Save and reload the current world with catalog revision ")
                .Append(catalog.RegistryRevision).Append("?')\">")
                .Append("<button class='primary'>Применить каталог к миру</button></form></div></div>");
        }
        else
        {
            body.Append("<p class='ok'>Мир использует актуальную ревизию каталога.</p>");
        }

        body.Append("<h2>По типам контента</h2><div class='grid'>");
        foreach (var stat in catalog.Types)
        {
            body.Append("<div class='stat'><b><a href='")
                .Append(Escape(CatalogUrl(string.Empty, stat.Type, string.Empty, string.Empty, 1)))
                .Append("'>").Append(Escape(TypeLabel(stat.Type))).Append("</a></b><span>")
                .Append(stat.Active).Append(" активно · ").Append(stat.Retired).Append(" снято<br>")
                .Append(stat.Variants).Append(" вариантов · ").Append(Escape(HumanBytes(stat.Bytes)))
                .Append("</span></div>");
        }
        body.Append("</div>");

        body.Append("<h2>Покрытие платформ</h2><div class='card'><div class='scroll'><table><thead><tr>")
            .Append("<th>Платформа / профиль</th><th>Опубликовано</th><th>Нет активных объектов</th></tr></thead><tbody>");
        if (catalog.Coverage.Platforms.Count == 0)
        {
            body.Append("<tr><td colspan='3' class='dim'>Платформенные варианты ещё не опубликованы.</td></tr>");
        }
        foreach (var platform in catalog.Coverage.Platforms)
        {
            body.Append("<tr><td>").Append(Escape(platform.Platform)).Append("<br><span class='dim'>")
                .Append(Escape(platform.RuntimeProfile)).Append("</span></td><td>")
                .Append(platform.Covered).Append(" / ").Append(catalog.Coverage.ActiveObjects)
                .Append("</td><td>");
            if (platform.Missing.Count == 0)
            {
                body.Append("<span class='ok'>полностью</span>");
            }
            else
            {
                body.Append("<span class='bad'>нет ").Append(platform.Missing.Count).Append("</span><br><span class='mono dim'>")
                    .Append(Escape(string.Join(", ", platform.Missing.Take(20)
                        .Select(value => value.Type + "/" + value.Id))))
                    .Append(platform.Missing.Count > 20 ? ", …" : string.Empty).Append("</span>");
            }
            body.Append("</td></tr>");
        }
        body.Append("</tbody></table></div></div>");

        body.Append("<h2>Объекты</h2><div class='card'>")
            .Append("<form method='get' action='/admin/catalog' class='block'><div class='row'>")
            .Append("<input name='q' placeholder='ID, название или русское слово' value='").Append(Escape(query)).Append("'>")
            .Append("<select name='type'><option value=''>Все типы</option>");
        foreach (var available in catalog.Types.Select(value => value.Type))
        {
            body.Append(Option(available, TypeLabel(available), type));
        }
        body.Append("</select><select name='category'><option value=''>Все категории одежды</option>");
        foreach (var available in catalog.Wear.Select(value => value.Category).Distinct().OrderBy(value => value))
        {
            body.Append(Option(available.ToString(), CategoryLabel(available), category));
        }
        body.Append("</select><select name='state'><option value=''>Все состояния</option>")
            .Append(Option("active", "Активные", state))
            .Append(Option("retired", "Снятые", state))
            .Append("</select><button>Найти</button><a href='/admin/catalog'>Сбросить</a></div></form>")
            .Append("<p class='dim'>Показано ").Append(from).Append("–").Append(to).Append(" из ")
            .Append(filtered.Length).Append(" найденных (всего ").Append(catalog.Records.Count)
            .Append("). По 10 на странице.</p><div class='scroll'><table><thead><tr>")
            .Append("<th>Объект</th><th>Название / категория</th><th>Состояние</th><th>Ревизия</th><th>Варианты</th><th>Размер</th>")
            .Append("</tr></thead><tbody>");
        foreach (var record in records)
        {
            GarmentCatalogEntry? wear = null;
            if (record.Type == "wear") wearById.TryGetValue(record.Id, out wear);
            var name = wear?.Simulation?.DisplayName ?? MetadataString(record, "displayName");
            var configuration = wear is null ? string.Empty : wear.ConfigurationSource;
            var size = record.Variants.Sum(value => value.Size + value.Attachments.Sum(item => item.Size));
            body.Append("<tr><td><div class='object-cell'>");
            AppendIcon(body, record.Id, detail: false);
            body.Append("<a class='mono' href='/admin/catalog/")
                .Append(Path(record.Type)).Append('/').Append(Path(record.Id)).Append("'>")
                .Append(Escape(record.Type)).Append('/').Append(Escape(record.Id)).Append("</a></div></td><td>")
                .Append(Escape(name.Length == 0 ? "—" : name));
            if (wear is not null)
            {
                body.Append("<br><span class='badge'>")
                    .Append(Escape(CategoryLabel(wear.Category))).Append("</span>");
            }
            if (configuration.Length > 0)
            {
                body.Append("<br><span class='")
                    .Append(wear?.Simulation is null ? "bad" : "dim").Append("'>")
                    .Append(Escape(configuration)).Append("</span>");
            }
            body.Append("</td><td><span class='badge ").Append(Escape(record.State)).Append("'>")
                .Append(Escape(record.State)).Append("</span></td><td>").Append(record.Revision)
                .Append("</td><td>").Append(record.Variants.Count).Append("</td><td>")
                .Append(Escape(HumanBytes(size))).Append("</td></tr>");
        }
        if (records.Length == 0)
        {
            body.Append("<tr><td colspan='6' class='dim'>По этим фильтрам ничего не найдено.</td></tr>");
        }
        body.Append("</tbody></table></div>");
        AppendPagination(body, query, type, state, category, page, pageCount);
        body.Append("</div>");
        return Page("Каталог контента", body.ToString());
    }

    public static string CatalogRecord(ContentObjectRecord record,
        IReadOnlyList<ContentObjectRecord> history, GarmentCatalogEntry? wear,
        long liveRevision, long pinnedRevision, string? notice)
    {
        var body = new StringBuilder();
        body.Append("<div class='nav'><div><h1 class='mono'>").Append(Escape(record.Type))
            .Append('/').Append(Escape(record.Id)).Append("</h1><p class='sub' style='margin:0'>revision ")
            .Append(record.Revision).Append(" · registry ").Append(liveRevision)
            .Append(" · world ").Append(pinnedRevision).Append("</p></div>")
            .Append("<a href='/admin/catalog'>Back to catalog</a></div>");
        if (!string.IsNullOrEmpty(notice))
        {
            body.Append("<div class='note'>").Append(Escape(notice)).Append("</div>");
        }

        body.Append("<div class='card'><div class='record-head'>");
        AppendIcon(body, record.Id, detail: true);
        body.Append("<div><div class='row'><span class='badge ")
            .Append(Escape(record.State)).Append("'>").Append(Escape(record.State)).Append("</span>")
            .Append("<span class='dim'>published ").Append(Escape(record.PublishedAtUtc.ToString("u")))
            .Append("</span><form method='post' action='/admin/catalog/")
            .Append(Path(record.Type)).Append('/').Append(Path(record.Id)).Append("/state' ")
            .Append("onsubmit=\"return confirm('")
            .Append(record.State == "active" ? "Retire this object? It will stop appearing in new spawns after Apply."
                : "Reactivate this object with its retained payload?")
            .Append("')\"><input type='hidden' name='expectedRevision' value='")
            .Append(record.Revision).Append("'><input type='hidden' name='state' value='")
            .Append(record.State == "active" ? "retired" : "active").Append("'><button class='danger'>")
            .Append(record.State == "active" ? "Retire" : "Reactivate")
            .Append("</button></form></div>");
        if (wear is not null)
        {
            body.Append("<p style='margin-bottom:0'><span class='badge'>")
                .Append(Escape(CategoryLabel(wear.Category))).Append("</span></p>");
        }
        body.Append("</div></div></div>");

        if (record.Type == "wear")
        {
            AppendWearForm(body, record, wear);
        }
        else
        {
            body.Append("<h2>Metadata</h2><div class='card'><pre class='mono'>")
                .Append(Escape(JsonSerializer.Serialize(record.Metadata, new JsonSerializerOptions { WriteIndented = true })))
                .Append("</pre><p class='dim'>This content type is currently read-only in the web admin.</p></div>");
        }

        body.Append("<h2>Payload variants</h2><div class='card'><div class='scroll'><table><thead><tr>")
            .Append("<th>Platform / profile</th><th>Payload</th><th>SHA-256</th><th>Size</th><th>Entry / icon</th></tr></thead><tbody>");
        foreach (var variant in record.Variants)
        {
            body.Append("<tr><td>").Append(Escape(variant.Platform)).Append("<br><span class='dim'>")
                .Append(Escape(variant.RuntimeProfile)).Append("</span></td><td>")
                .Append(Escape(variant.PayloadType)).Append("</td><td class='mono'>")
                .Append(Escape(variant.Sha256)).Append("</td><td>").Append(Escape(HumanBytes(variant.Size)))
                .Append("</td><td>").Append(Escape(variant.EntryAsset)).Append(" / ")
                .Append(Escape(variant.IconAsset ?? "—"));
            if (variant.Attachments.Count > 0)
            {
                body.Append("<br><span class='dim'>").Append(variant.Attachments.Count).Append(" attachment(s)</span>");
            }
            body.Append("</td></tr>");
        }
        body.Append("</tbody></table></div></div>");

        body.Append("<h2>Revision history</h2><div class='card'><div class='scroll'><table><thead><tr>")
            .Append("<th>Revision</th><th>State</th><th>Published UTC</th><th>Variants</th></tr></thead><tbody>");
        foreach (var item in history.OrderByDescending(value => value.Revision))
        {
            body.Append("<tr><td>").Append(item.Revision).Append("</td><td>")
                .Append(Escape(item.State)).Append("</td><td>")
                .Append(Escape(item.PublishedAtUtc.ToString("u"))).Append("</td><td>")
                .Append(item.Variants.Count).Append("</td></tr>");
        }
        body.Append("</tbody></table></div></div>");
        return Page(record.Type + "/" + record.Id, body.ToString());
    }

    private static void AppendWearForm(
        StringBuilder body, ContentObjectRecord record, GarmentCatalogEntry? entry)
    {
        var simulation = entry?.Simulation ?? new GarmentSimulationMetadata
        {
            DisplayName = MetadataString(record, "displayName") is { Length: > 0 } name ? name : record.Id,
            PrototypeId = record.Id,
        };
        body.Append("<h2>Simulation garment parameters</h2>");
        if (entry?.Simulation is null)
        {
            body.Append("<div class='note alarm'>This active wear record is not configured and is excluded from simulation spawns. Complete and save this form.</div>");
        }
        else if (entry.ConfigurationSource == "base SimData")
        {
            body.Append("<div class='note'>These values currently fall back to base SimData. Saving copies them into this atomic record.</div>");
        }
        body.Append("<div class='card'><form class='block' method='post' action='/admin/catalog/wear/")
            .Append(Path(record.Id)).Append("'><input type='hidden' name='expectedRevision' value='")
            .Append(record.Revision).Append("'><div class='field-grid'>")
            .Append(Field("Display name", "displayName", simulation.DisplayName))
            .Append(Field("Prototype id", "prototypeId", simulation.PrototypeId))
            .Append("<div><label>Layer</label><select name='layer'>");
        foreach (var value in Enum.GetNames(typeof(WearLayer)))
        {
            body.Append(Option(value, value, simulation.Layer));
        }
        body.Append("</select></div><div><label>Sex</label><select name='sex'>");
        foreach (var value in Enum.GetNames(typeof(GarmentSex)))
        {
            body.Append(Option(value, value, simulation.Sex));
        }
        body.Append("</select></div>")
            .Append(NumberField("Warmth", "warmth", simulation.Warmth, "0.01"))
            .Append(NumberField("Armor (0..1)", "armor", simulation.Armor, "0.01"))
            .Append(NumberField("Thermal delta", "thermalDelta", simulation.ThermalDelta, "0.01"))
            .Append(NumberField("Dress duration, ticks", "dressDurationTicks", simulation.DressDurationTicks, "1"))
            .Append(NumberField("Inventory capacity", "capacity", simulation.Capacity, "1"))
            .Append("</div><label>Covers body parts</label><div class='checks'>");
        var selected = simulation.Covers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in Enum.GetNames(typeof(BodyPart)))
        {
            body.Append("<label><input type='checkbox' name='covers' value='").Append(Escape(value)).Append("'")
                .Append(selected.Contains(value) ? " checked" : string.Empty).Append(">")
                .Append(Escape(value)).Append("</label>");
        }
        body.Append("</div><div class='row' style='margin-top:18px'><button class='primary'>Save garment parameters</button>")
            .Append("<span class='dim'>Creates one new object revision; payload hashes stay unchanged.</span></div></form></div>");
    }

    private static string Field(string label, string name, string value) =>
        "<div><label>" + Escape(label) + "</label><input required name='" + Escape(name) +
        "' value='" + Escape(value) + "'></div>";

    private static string NumberField(string label, string name, float value, string step) =>
        NumberField(label, name, value.ToString("0.######", CultureInfo.InvariantCulture), step);

    private static string NumberField(string label, string name, int value, string step) =>
        NumberField(label, name, value.ToString(CultureInfo.InvariantCulture), step);

    private static string NumberField(string label, string name, string value, string step) =>
        "<div><label>" + Escape(label) + "</label><input required type='number' step='" + step +
        "' name='" + Escape(name) + "' value='" + Escape(value) + "'></div>";

    private static string Option(string value, string label, string selected) =>
        "<option value='" + Escape(value) + "'" +
        (string.Equals(value, selected, StringComparison.Ordinal) ? " selected" : string.Empty) +
        ">" + Escape(label) + "</option>";

    private static bool MatchesSearch(ContentObjectRecord record,
        IReadOnlyDictionary<string, GarmentCatalogEntry> wearById, string query)
    {
        if (query.Length == 0) return true;
        wearById.TryGetValue(record.Id, out var wear);
        var name = wear?.Simulation?.DisplayName ?? MetadataString(record, "displayName");
        var category = wear?.Category ?? GarmentCategory.Unclassified;
        var searchable = string.Join(" ", new[]
        {
            record.Type,
            TypeLabel(record.Type),
            TypeAliases(record.Type),
            record.Id,
            name,
            category.ToString(),
            CategoryLabel(category),
            CategoryAliases(category),
        });
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string TypeLabel(string type) => type switch
    {
        "wear" => "Одежда",
        "actor" => "Персонажи",
        "hair" => "Волосы",
        "prosthetic" => "Протезы",
        "object" => "Предметы и инструменты",
        "building" => "Здания и мебель",
        "mob" => "Мобы",
        "ui" => "Интерфейс",
        "config" => "Настройки",
        "audio" => "Звуки",
        "vfx" => "Эффекты",
        _ => type,
    };

    private static string TypeAliases(string type) => type switch
    {
        "wear" => "вещи гардероб",
        "actor" => "актёр актер персонаж человек",
        "hair" => "причёска прическа волосы",
        "prosthetic" => "протез рука нога",
        "object" => "предмет ресурс инструмент мясо копьё копье",
        "building" => "постройка мебель кровать верстак",
        "mob" => "животное враг",
        "config" => "конфиг баланс simdata",
        "audio" => "звук музыка голос",
        "vfx" => "эффект частицы",
        _ => string.Empty,
    };

    private static string CategoryLabel(GarmentCategory category) => category switch
    {
        GarmentCategory.Underwear => "Бельё",
        GarmentCategory.Top => "Верх",
        GarmentCategory.Bottom => "Низ",
        GarmentCategory.Dress => "Платья",
        GarmentCategory.Outerwear => "Верхняя одежда",
        GarmentCategory.Footwear => "Обувь",
        GarmentCategory.Gloves => "Перчатки",
        GarmentCategory.Headwear => "Головные уборы",
        GarmentCategory.Neckwear => "Шея",
        GarmentCategory.Jewellery => "Украшения",
        GarmentCategory.Armwear => "Наручи",
        GarmentCategory.Legwear => "Чулки и носки",
        GarmentCategory.Belt => "Ремни",
        GarmentCategory.Bag => "Сумки и рюкзаки",
        GarmentCategory.Outfit => "Комплекты",
        GarmentCategory.Accessory => "Аксессуары",
        _ => "Без категории",
    };

    private static string CategoryAliases(GarmentCategory category) => category switch
    {
        GarmentCategory.Underwear => "белье бельё трусы бюстгальтер лифчик",
        GarmentCategory.Top => "топ рубашка блузка свитер корсет",
        GarmentCategory.Bottom => "юбка брюки штаны шорты легинсы",
        GarmentCategory.Dress => "платье сарафан",
        GarmentCategory.Outerwear => "куртка пальто жилет худи плащ",
        GarmentCategory.Footwear => "ботинки туфли сандалии кроссовки обувь",
        GarmentCategory.Gloves => "перчатки варежки",
        GarmentCategory.Headwear => "шапка кепка очки головной убор",
        GarmentCategory.Neckwear => "шарф воротник галстук ожерелье",
        GarmentCategory.Jewellery => "украшение серьги браслет кулон",
        GarmentCategory.Armwear => "нарукавники манжеты наручи",
        GarmentCategory.Legwear => "чулки носки колготки гетры",
        GarmentCategory.Belt => "ремень пояс",
        GarmentCategory.Bag => "сумка рюкзак кошелёк кошелек",
        GarmentCategory.Outfit => "комплект костюм комбинезон",
        GarmentCategory.Accessory => "аксессуар",
        _ => string.Empty,
    };

    private static void AppendIcon(StringBuilder body, string id, bool detail)
    {
        body.Append("<span class='thumb").Append(detail ? " detail" : string.Empty)
            .Append("' aria-hidden='true'>◇<img src='/admin/icons/")
            .Append(Path(id)).Append("' alt='' onerror=\"this.remove()\"></span>");
    }

    private static void AppendPagination(StringBuilder body, string query, string type,
        string state, string category, int page, int pageCount)
    {
        if (pageCount <= 1) return;
        body.Append("<nav class='pager' aria-label='Страницы'>");
        if (page > 1)
        {
            body.Append("<a href='").Append(Escape(CatalogUrl(query, type, state, category, page - 1)))
                .Append("'>←</a>");
        }

        var pages = new SortedSet<int> { 1, pageCount };
        for (var value = Math.Max(1, page - 2); value <= Math.Min(pageCount, page + 2); value++)
        {
            pages.Add(value);
        }

        var previous = 0;
        foreach (var value in pages)
        {
            if (previous > 0 && value - previous > 1) body.Append("<span class='gap'>…</span>");
            if (value == page)
            {
                body.Append("<span class='current' aria-current='page'>").Append(value).Append("</span>");
            }
            else
            {
                body.Append("<a href='").Append(Escape(CatalogUrl(query, type, state, category, value)))
                    .Append("'>").Append(value).Append("</a>");
            }
            previous = value;
        }

        if (page < pageCount)
        {
            body.Append("<a href='").Append(Escape(CatalogUrl(query, type, state, category, page + 1)))
                .Append("'>→</a>");
        }
        body.Append("</nav>");
    }

    private static string CatalogUrl(string query, string type, string state, string category, int page)
    {
        var values = new List<string>();
        if (query.Length > 0) values.Add("q=" + Uri.EscapeDataString(query));
        if (type.Length > 0) values.Add("type=" + Uri.EscapeDataString(type));
        if (state.Length > 0) values.Add("state=" + Uri.EscapeDataString(state));
        if (category.Length > 0) values.Add("category=" + Uri.EscapeDataString(category));
        if (page > 1) values.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        return values.Count == 0 ? "/admin/catalog" : "/admin/catalog?" + string.Join("&", values);
    }

    private static string MetadataString(ContentObjectRecord record, string key)
    {
        if (!record.Metadata.TryGetValue(key, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }
        return value.GetString() ?? string.Empty;
    }

    private static string HumanBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < suffix.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return value.ToString(index == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + suffix[index];
    }

    private static string Path(string value) => Uri.EscapeDataString(value);

    private static string Stat(string value, string label) =>
        $"<div class='stat'><b>{Escape(value)}</b><span>{Escape(label)}</span></div>";

    private static string SpeedButton(float currentSpeed, float speed)
    {
        var value = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var selected = Math.Abs(currentSpeed - speed) < 0.001f;
        var state = selected ? " class='primary' aria-current='true'" : string.Empty;
        return $"<form method='post' action='/admin/speed'>" +
               $"<input type='hidden' name='speed' value='{value}'>" +
               $"<button type='submit'{state}>{value}×</button></form>";
    }

    private static string Page(string title, string body) =>
        "<!doctype html><html lang='en'><head><meta charset='utf-8'>" +
        "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
        $"<title>{Escape(title)} · HexLive</title><style>{Style}</style></head>" +
        $"<body><div class='wrap'>{body}</div></body></html>";

    private static string Escape(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}

}
