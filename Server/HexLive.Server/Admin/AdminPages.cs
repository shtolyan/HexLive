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
        long pinnedCatalogRevision)
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
        string? query, string? type, string? state, string? notice)
    {
        query = query?.Trim() ?? string.Empty;
        type = type?.Trim() ?? string.Empty;
        state = state?.Trim() ?? string.Empty;
        var wearById = catalog.Wear.ToDictionary(value => value.Record.Id, StringComparer.Ordinal);
        var records = catalog.Records.Where(record =>
            (type.Length == 0 || record.Type == type) &&
            (state.Length == 0 || record.State == state) &&
            (query.Length == 0 || record.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (record.Type == "wear" && wearById.TryGetValue(record.Id, out var wear) &&
              (wear.Simulation?.DisplayName ?? string.Empty)
              .Contains(query, StringComparison.OrdinalIgnoreCase)))).ToArray();

        var body = new StringBuilder();
        body.Append("<div class='nav'><div><h1>Content catalog</h1><p class='sub' style='margin:0'>")
            .Append("Atomic server records and simulation garment tuning.</p></div>")
            .Append("<a href='/admin'>Back to dashboard</a></div>");
        if (!string.IsNullOrEmpty(notice))
        {
            body.Append("<div class='note'>").Append(Escape(notice)).Append("</div>");
        }

        var pending = catalog.RegistryRevision != pinnedRevision;
        body.Append("<div class='grid'>")
            .Append(Stat(catalog.RegistryRevision.ToString(CultureInfo.InvariantCulture), "live revision"))
            .Append(Stat(pinnedRevision.ToString(CultureInfo.InvariantCulture), "world revision"))
            .Append(Stat(catalog.Records.Count.ToString(CultureInfo.InvariantCulture), "records"))
            .Append(Stat(catalog.Wear.Count(value => value.Spawnable).ToString(CultureInfo.InvariantCulture), "spawnable wear"))
            .Append(Stat(catalog.Wear.Count(value => value.Record.State == "retired").ToString(CultureInfo.InvariantCulture), "retired wear"))
            .Append(Stat(catalog.UnconfiguredWear.ToString(CultureInfo.InvariantCulture), "unconfigured wear"))
            .Append("</div>");

        if (pending)
        {
            body.Append("<div class='note' style='margin-top:18px'><div class='row'><b>Catalog changes are pending.</b>")
                .Append("<span>Current players still use revision ").Append(pinnedRevision)
                .Append(". Applying saves and reloads the same world.</span>")
                .Append("<form method='post' action='/admin/catalog/apply' onsubmit=\"return confirm('Save and reload the current world with catalog revision ")
                .Append(catalog.RegistryRevision).Append("?')\">")
                .Append("<button class='primary'>Apply catalog to world</button></form></div></div>");
        }
        else
        {
            body.Append("<p class='ok'>The running world uses the current catalog revision.</p>");
        }

        body.Append("<h2>By content type</h2><div class='grid'>");
        foreach (var stat in catalog.Types)
        {
            body.Append("<div class='stat'><b>").Append(Escape(stat.Type)).Append("</b><span>")
                .Append(stat.Active).Append(" active · ").Append(stat.Retired).Append(" retired<br>")
                .Append(stat.Variants).Append(" variants · ").Append(Escape(HumanBytes(stat.Bytes)))
                .Append("</span></div>");
        }
        body.Append("</div>");

        body.Append("<h2>Platform coverage</h2><div class='card'><div class='scroll'><table><thead><tr>")
            .Append("<th>Platform / runtime profile</th><th>Covered</th><th>Missing active objects</th></tr></thead><tbody>");
        if (catalog.Coverage.Platforms.Count == 0)
        {
            body.Append("<tr><td colspan='3' class='dim'>No platform variants have been published.</td></tr>");
        }
        foreach (var platform in catalog.Coverage.Platforms)
        {
            body.Append("<tr><td>").Append(Escape(platform.Platform)).Append("<br><span class='dim'>")
                .Append(Escape(platform.RuntimeProfile)).Append("</span></td><td>")
                .Append(platform.Covered).Append(" / ").Append(catalog.Coverage.ActiveObjects)
                .Append("</td><td>");
            if (platform.Missing.Count == 0)
            {
                body.Append("<span class='ok'>complete</span>");
            }
            else
            {
                body.Append("<span class='bad'>").Append(platform.Missing.Count).Append(" missing</span><br><span class='mono dim'>")
                    .Append(Escape(string.Join(", ", platform.Missing.Take(20)
                        .Select(value => value.Type + "/" + value.Id))))
                    .Append(platform.Missing.Count > 20 ? ", …" : string.Empty).Append("</span>");
            }
            body.Append("</td></tr>");
        }
        body.Append("</tbody></table></div></div>");

        body.Append("<h2>Records</h2><div class='card'>")
            .Append("<form method='get' action='/admin/catalog' class='block'><div class='row'>")
            .Append("<input name='q' placeholder='id or display name' value='").Append(Escape(query)).Append("'>")
            .Append("<select name='type'><option value=''>all types</option>");
        foreach (var available in catalog.Types.Select(value => value.Type))
        {
            body.Append(Option(available, available, type));
        }
        body.Append("</select><select name='state'><option value=''>all states</option>")
            .Append(Option("active", "active", state))
            .Append(Option("retired", "retired", state))
            .Append("</select><button>Filter</button></div></form>")
            .Append("<p class='dim'>Showing ").Append(records.Length).Append(" of ")
            .Append(catalog.Records.Count).Append(" records.</p><div class='scroll'><table><thead><tr>")
            .Append("<th>Object</th><th>Name / configuration</th><th>State</th><th>Revision</th><th>Variants</th><th>Size</th>")
            .Append("</tr></thead><tbody>");
        foreach (var record in records)
        {
            GarmentCatalogEntry? wear = null;
            if (record.Type == "wear") wearById.TryGetValue(record.Id, out wear);
            var name = wear?.Simulation?.DisplayName ?? MetadataString(record, "displayName");
            var configuration = wear is null ? string.Empty : wear.ConfigurationSource;
            var size = record.Variants.Sum(value => value.Size + value.Attachments.Sum(item => item.Size));
            body.Append("<tr><td><a class='mono' href='/admin/catalog/")
                .Append(Path(record.Type)).Append('/').Append(Path(record.Id)).Append("'>")
                .Append(Escape(record.Type)).Append('/').Append(Escape(record.Id)).Append("</a></td><td>")
                .Append(Escape(name.Length == 0 ? "—" : name));
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
        body.Append("</tbody></table></div></div>");
        return Page("Content catalog", body.ToString());
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

        body.Append("<div class='card'><div class='row'><span class='badge ")
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
            .Append(record.State == "active" ? "Retire" : "Reactivate").Append("</button></form></div></div>");

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
