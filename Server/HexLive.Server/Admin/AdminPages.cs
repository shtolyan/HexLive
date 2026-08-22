using System;
using System.Net;
using System.Text;

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
.wrap{max-width:760px;margin:0 auto;padding:32px 20px 64px}
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
input{font:inherit;background:#0d1117;border:1px solid var(--line);color:var(--ink);
border-radius:9px;padding:9px 12px;width:100%;max-width:340px}
label{display:block;font-size:13px;color:var(--dim);margin:12px 0 5px}
form{display:inline}
.row{display:flex;gap:9px;flex-wrap:wrap;align-items:center}
.note{background:#1c1a10;border:1px solid #4d3c12;color:#f5d999;
border-radius:10px;padding:13px 16px;margin-bottom:18px;font-size:14px}
.alarm{background:#2a1315;border-color:#5d2220;color:#ff9d95}
.ok{color:var(--good)}.bad{color:var(--bad)}.dim{color:var(--dim)}
code{background:#0d1117;border:1px solid var(--line);border-radius:5px;padding:1px 6px;font-size:13px}
a{color:var(--gold)}
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
        string? notice, bool canSendMail)
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
