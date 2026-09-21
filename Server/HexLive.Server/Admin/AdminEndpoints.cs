using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Assets;
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
        AdminSessions sessions, AdminMailer mailer, CancellationTokenSource lifetime,
        AssetRegistryStore assetRegistry, AssetGarmentCatalog assetCatalog, string? adminIconRoot)
    {
        app.MapGet("/admin", (HttpContext context) =>
        {
            if (!SignedIn(context, sessions))
            {
                return Html(AdminPages.Login(
                    context.Request.Query["error"], account.PasswordIsTemporary));
            }

            AssetCatalogOverview overview;
            var notice = context.Request.Query["notice"].ToString();
            try
            {
                overview = assetCatalog.ReadOverview();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidDataException or JsonException)
            {
                overview = new AssetCatalogOverview
                {
                    RegistryRevision = assetRegistry.RegistryRevision,
                };
                notice = "Catalog could not be read: " + ex.Message +
                         (notice.Length == 0 ? string.Empty : " · " + notice);
            }

            return Html(AdminPages.Dashboard(
                worlds.Host, account, IsInsecure(context), notice,
                mailer.CanSendMail, overview, worlds.CatalogRegistryRevision, context.Items[CentralAdminAccess.Marker] is true));
        });


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

        app.MapGet("/admin/catalog", (HttpContext context) =>
        {
            if (!SignedIn(context, sessions)) return Redirect("/admin");
            try
            {
                return Html(AdminPages.Catalog(
                    assetCatalog.ReadOverview(), worlds.CatalogRegistryRevision,
                    context.Request.Query["q"], context.Request.Query["type"],
                    context.Request.Query["state"], context.Request.Query["category"],
                    ParsePage(context.Request.Query["page"]), context.Request.Query["notice"]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidDataException or JsonException)
            {
                return Redirect(WithNotice("/admin", "Catalog could not be read: " + ex.Message));
            }
        });

        app.MapGet("/admin/icons/{id}", (HttpContext context, string id) =>
        {
            if (!SignedIn(context, sessions)) return Redirect("/admin");
            if (string.IsNullOrWhiteSpace(adminIconRoot) || !ContentIdentity.IsId(id))
            {
                return Results.NotFound();
            }

            var root = Path.GetFullPath(adminIconRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, id + ".png"));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(prefix, comparison) || !File.Exists(candidate))
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "private,no-cache";
            return Results.File(candidate, "image/png", enableRangeProcessing: false);
        });

        app.MapGet("/admin/catalog/{type}/{id}", (HttpContext context, string type, string id) =>
        {
            if (!SignedIn(context, sessions)) return Redirect("/admin");
            try
            {
                var record = assetRegistry.ReadCurrentRecord(type, id);
                if (record is null)
                {
                    return Redirect(WithNotice("/admin/catalog", $"Object {type}/{id} was not found."));
                }
                var wear = type == "wear" ? assetCatalog.ReadWear(id) : null;
                return Html(AdminPages.CatalogRecord(
                    record, assetRegistry.ReadHistory(type, id), wear,
                    assetRegistry.RegistryRevision, worlds.CatalogRegistryRevision,
                    context.Request.Query["notice"]));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                       InvalidDataException or JsonException)
            {
                return Redirect(WithNotice("/admin/catalog", "Object could not be read: " + ex.Message));
            }
        });

        app.MapPost("/admin/catalog/wear/{id}", async (HttpContext context, string id) =>
        {
            if (!SignedIn(context, sessions)) return Redirect("/admin");
            try
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var expected = RequireLong(form["expectedRevision"].ToString(), "expected revision");
                var record = assetRegistry.ReadCurrentRecord("wear", id)
                    ?? throw new InvalidOperationException($"Wear object wear/{id} no longer exists.");
                var covers = new List<string>();
                foreach (var cover in form["covers"])
                {
                    if (!string.IsNullOrWhiteSpace(cover)) covers.Add(cover.Trim());
                }
                var simulation = new GarmentSimulationMetadata
                {
                    DisplayName = form["displayName"].ToString().Trim(),
                    PrototypeId = form["prototypeId"].ToString().Trim(),
                    Layer = form["layer"].ToString().Trim(),
                    Sex = form["sex"].ToString().Trim(),
                    Warmth = RequireFloat(form["warmth"].ToString(), "warmth"),
                    Armor = RequireFloat(form["armor"].ToString(), "armor"),
                    ThermalDelta = RequireFloat(form["thermalDelta"].ToString(), "thermal delta"),
                    DressDurationTicks = RequireInt(
                        form["dressDurationTicks"].ToString(), "dress duration"),
                    Capacity = RequireInt(form["capacity"].ToString(), "capacity"),
                    Covers = covers,
                };
                var result = await assetRegistry.UpdateRecordAsync(
                    "wear", id, expected, record.State,
                    assetCatalog.WithSimulationMetadata(record, simulation),
                    context.RequestAborted);
                var message = result.Changed
                    ? $"Garment parameters saved as revision {result.Record.Revision}. Apply the pending catalog when ready."
                    : "No garment parameters changed.";
                return Redirect(WithNotice(ItemPath("wear", id), message));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                       InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
            {
                return Redirect(WithNotice(ItemPath("wear", id), "Could not save: " + ex.Message));
            }
        });

        app.MapPost("/admin/catalog/{type}/{id}/state",
            async (HttpContext context, string type, string id) =>
            {
                if (!SignedIn(context, sessions)) return Redirect("/admin");
                try
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    var expected = RequireLong(form["expectedRevision"].ToString(), "expected revision");
                    var state = form["state"].ToString();
                    var record = assetRegistry.ReadCurrentRecord(type, id)
                        ?? throw new InvalidOperationException($"Object {type}/{id} no longer exists.");
                    var result = await assetRegistry.UpdateRecordAsync(
                        type, id, expected, state, record.Metadata, context.RequestAborted);
                    var message = result.Changed
                        ? $"Object is now {result.Record.State} at revision {result.Record.Revision}."
                        : $"Object was already {result.Record.State}.";
                    return Redirect(WithNotice(ItemPath(type, id), message));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                           InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
                {
                    return Redirect(WithNotice(ItemPath(type, id), "Could not change state: " + ex.Message));
                }
            });

        app.MapPost("/admin/catalog/apply", (HttpContext context) =>
        {
            if (!SignedIn(context, sessions)) return Redirect("/admin");
            try
            {
                var changed = worlds.ReloadCatalog();
                return Redirect(WithNotice("/admin/catalog", changed
                    ? $"Catalog revision {worlds.CatalogRegistryRevision} is now active in the current world."
                    : "The current world already uses this catalog revision."));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or
                                       UnauthorizedAccessException or InvalidDataException)
            {
                return Redirect(WithNotice("/admin/catalog", "Catalog was not applied: " + ex.Message));
            }
        });

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

        Func<HttpContext, Task<IResult>> speedHandler = context => HandleSpeed(
            context, sessions, speed =>
            {
                // The operator may fast-forward; a viewer may not. That asymmetry is
                // the whole point of having a panel behind a password.
                worlds.Host.SetSpeedAsOperator(speed);
                Console.WriteLine($"[admin] speed set to {speed}x");
            });
        app.MapPost("/admin/speed", speedHandler);

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
        context.Items[CentralAdminAccess.Marker] is true || sessions.IsSignedIn(context.Request.Cookies[CookieName] ?? string.Empty);

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

    private static string ItemPath(string type, string id) =>
        "/admin/catalog/" + Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(id);

    private static string WithNotice(string path, string notice) =>
        path + (path.IndexOf('?') >= 0 ? "&" : "?") +
        "notice=" + Uri.EscapeDataString(notice);

    private static long RequireLong(string value, string field)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidDataException($"Invalid {field}.");
        }
        return parsed;
    }

    private static int ParsePage(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0
            ? page
            : 1;

    private static int RequireInt(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"Invalid {field}.");
        }
        return parsed;
    }

    private static float RequireFloat(string value, string field)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !float.IsFinite(parsed))
        {
            throw new InvalidDataException($"Invalid {field}.");
        }
        return parsed;
    }

    private static async Task<IResult> HandleSpeed(
        HttpContext context, AdminSessions sessions, Action<float> setSpeed)
    {
        if (!SignedIn(context, sessions))
        {
            return Redirect("/admin");
        }

        var form = await context.Request.ReadFormAsync();
        if (!float.TryParse(form["speed"].ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var speed) || !float.IsFinite(speed))
        {
            return Redirect("/admin");
        }

        setSpeed(speed);
        // #184 rework: this POST has exactly one stable destination. No notice,
        // locale, proxy or percent-decoding can turn it into a missing route;
        // Dashboard reads the authoritative speed and highlights its button.
        return Redirect("/admin");
    }
}

}
