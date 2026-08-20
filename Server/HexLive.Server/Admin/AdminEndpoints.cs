using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Admin
{

/// <summary>
/// Wires the admin panel onto the web host.
/// <para>
/// Every mutating route is a POST from a form, never a GET — a link that pauses
/// the world would be followed by the first bot or link preview that finds it.
/// The session cookie is HttpOnly and SameSite=Strict, which is also what stops
/// another site from posting to these routes on your behalf.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    private const string CookieName = "hexlive_admin";

    public static void Map(WebApplication app, WorldSupervisor worlds, AdminAccount account,
        AdminSessions sessions, AdminMailer mailer, CancellationTokenSource lifetime)
    {
        app.MapGet("/admin", (HttpContext context) =>
            SignedIn(context, sessions)
                ? Html(AdminPages.Dashboard(worlds.Host, account, IsInsecure(context),
                    context.Request.Query["notice"], mailer.CanSendMail))
                : Html(AdminPages.Login(context.Request.Query["error"], account.PasswordIsTemporary)));

        app.MapPost("/admin/login", async (HttpContext context) =>
        {
            var source = Source(context);
            var lockout = sessions.LockoutSeconds(source);
            if (lockout > 0)
            {
                return Redirect($"/admin?error=Too many attempts. Try again in {lockout}s.");
            }

            var form = await context.Request.ReadFormAsync();
            if (!account.Verify(form["password"]))
            {
                sessions.RecordFailure(source);
                Console.WriteLine($"[admin] failed sign-in from {source}");
                // Same message whether or not anything matched — there is only
                // one account, so there is nothing to enumerate, and vagueness
                // here just confuses the legitimate operator.
                return Redirect("/admin?error=Wrong password.");
            }

            sessions.RecordSuccess(source);
            SetCookie(context, sessions.CreateSession());
            Console.WriteLine($"[admin] signed in from {source}");
            return Redirect("/admin");
        });

        app.MapPost("/admin/logout", (HttpContext context) =>
        {
            sessions.SignOut(context.Request.Cookies[CookieName] ?? string.Empty);
            context.Response.Cookies.Delete(CookieName);
            return Redirect("/admin");
        });

        // ── forgotten password ────────────────────────────────────────────

        app.MapGet("/admin/forgot", () =>
            Html(AdminPages.Forgot(null, account.Email.Length > 0, mailer.CanSendMail)));

        app.MapPost("/admin/forgot", (HttpContext context) =>
        {
            var token = sessions.CreateResetToken();
            var url = $"{context.Request.Scheme}://{context.Request.Host}/admin/reset?token={token}";
            var outcome = mailer.Send(account.Email, url);

            var message = outcome switch
            {
                "sent" => $"A reset link has been sent to {account.Email}. It expires in 30 minutes.",
                _ => "The reset link has been written to the server console (no working SMTP). " +
                     "Open the console on the machine running this server to collect it.",
            };

            return Html(AdminPages.Forgot(message, account.Email.Length > 0, mailer.CanSendMail));
        });

        app.MapGet("/admin/reset", (HttpContext context) =>
            Html(AdminPages.ResetForm(context.Request.Query["token"], null)));

        app.MapPost("/admin/reset", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            var token = form["token"].ToString();
            var password = form["password"].ToString();

            var problem = AdminAccount.ValidatePassword(password);
            if (problem.Length > 0)
            {
                // Do NOT redeem the token on a weak password — that would burn a
                // single-use link on a typo and lock the operator out.
                return Html(AdminPages.ResetForm(token, problem));
            }

            if (!sessions.RedeemResetToken(token))
            {
                return Html(AdminPages.ResetForm(token, "This link has expired or was already used."));
            }

            account.ChangePassword(password);
            sessions.SignOutEverywhere();
            Console.WriteLine("[admin] password reset via emailed link");
            return Redirect("/admin?error=Password changed. Sign in with the new one.");
        });

        // ── everything below requires a session ───────────────────────────

        app.MapPost("/admin/pause", (HttpContext context) => Guarded(context, sessions, () =>
        {
            worlds.Host.PauseAsOperator();
            Console.WriteLine("[admin] world paused");
            return Redirect("/admin?notice=World paused.");
        }));

        app.MapPost("/admin/resume", (HttpContext context) => Guarded(context, sessions, () =>
        {
            worlds.Host.ResumeAsOperator();
            Console.WriteLine("[admin] world resumed");
            return Redirect("/admin?notice=World resumed.");
        }));

        app.MapPost("/admin/speed", async (HttpContext context) =>
        {
            if (!SignedIn(context, sessions))
            {
                return Redirect("/admin");
            }

            var form = await context.Request.ReadFormAsync();
            if (!float.TryParse(form["speed"].ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var speed))
            {
                return Redirect("/admin");
            }

            // The operator may fast-forward; a viewer may not. That asymmetry is
            // the whole point of having a panel behind a password.
            worlds.Host.SetSpeedAsOperator(speed);
            Console.WriteLine($"[admin] speed set to {speed}x");
            return Redirect(SpeedRedirectLocation(speed));
        });

        app.MapPost("/admin/password", async (HttpContext context) =>
        {
            if (!SignedIn(context, sessions))
            {
                return Redirect("/admin");
            }

            var form = await context.Request.ReadFormAsync();
            var password = form["password"].ToString();
            var problem = AdminAccount.ValidatePassword(password);
            if (problem.Length > 0)
            {
                return Redirect("/admin?notice=" + problem);
            }

            account.ChangePassword(password);
            // Including this browser: if the password changed because it might
            // have leaked, leaving the old sessions alive defeats the change.
            sessions.SignOutEverywhere();
            context.Response.Cookies.Delete(CookieName);
            Console.WriteLine("[admin] password changed");
            return Redirect("/admin?error=Password updated. Sign in again.");
        });

        app.MapPost("/admin/email", async (HttpContext context) =>
        {
            if (!SignedIn(context, sessions))
            {
                return Redirect("/admin");
            }

            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString();
            if (!AdminAccount.LooksLikeEmail(email))
            {
                return Redirect("/admin?notice=That does not look like an email address.");
            }

            account.SetEmail(email);
            Console.WriteLine($"[admin] recovery email set to {email}");
            return Redirect("/admin?notice=Recovery email saved.");
        });

        app.MapPost("/admin/newworld", async (HttpContext context) =>
        {
            if (!SignedIn(context, sessions))
            {
                return Redirect("/admin");
            }

            var form = await context.Request.ReadFormAsync();
            var seed = int.TryParse(form["seed"], out var parsed)
                ? parsed
                : Environment.TickCount;
            // §146: an unrecognised value falls back to Feud — the select only
            // offers valid names, so this is belt for hand-crafted POSTs.
            ServerOptions.TryParseMode(form["mode"], out var mode);

            try
            {
                worlds.StartNewWorld(seed, mode);
            }
            catch (Exception ex)
            {
                return Redirect("/admin?notice=Could not start a new world: " + ex.Message);
            }

            return Redirect($"/admin?notice=New world started, seed {seed}, mode {mode}.");
        });

        app.MapPost("/admin/shutdown", (HttpContext context) => Guarded(context, sessions, () =>
        {
            Console.WriteLine("[admin] shutdown requested");
            // Let the response reach the browser before the process goes away,
            // otherwise the operator sees a connection error and cannot tell
            // whether the shutdown actually happened.
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                lifetime.Cancel();
            });

            return Html(
                "<!doctype html><meta charset='utf-8'><title>Shutting down</title>" +
                "<body style='background:#0d1117;color:#e6edf3;font:15px system-ui;padding:40px'>" +
                "<h1 style='font-size:20px'>Shutting down…</h1>" +
                "<p style='color:#8b949e'>The world was saved. Start the server again to continue it.</p>");
        }));
    }

    private static IResult Guarded(HttpContext context, AdminSessions sessions, Func<IResult> action) =>
        SignedIn(context, sessions) ? action() : Redirect("/admin");

    private static bool SignedIn(HttpContext context, AdminSessions sessions) =>
        sessions.IsSignedIn(context.Request.Cookies[CookieName] ?? string.Empty);

    private static void SetCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,                       // script cannot read it
            SameSite = SameSiteMode.Strict,        // another site cannot post with it
            Secure = context.Request.IsHttps,      // only promise HTTPS-only when it IS
            MaxAge = TimeSpan.FromHours(12),
        });

    /// <summary>
    /// True when the password would cross the network in the clear. Localhost is
    /// exempt: it never leaves the machine, and warning about it would train the
    /// operator to ignore the warning that matters.
    /// </summary>
    private static bool IsInsecure(HttpContext context) =>
        !context.Request.IsHttps && !IsLoopback(context);

    private static bool IsLoopback(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address != null && System.Net.IPAddress.IsLoopback(address);
    }

    private static string Source(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");

    private static IResult Redirect(string location) => Results.Redirect(location);

    private static string SpeedRedirectLocation(float speed)
    {
        // Location is an HTTP header and therefore must stay ASCII. Passing the
        // multiplication sign through verbatim makes Kestrel reject the response
        // after the speed was already changed, leaving the browser on an error page.
        var value = speed.ToString("0.##", CultureInfo.InvariantCulture);
        var notice = Uri.EscapeDataString($"Speed set to {value}×.");
        return "/admin?notice=" + notice;
    }
}

}
