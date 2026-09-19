using System.Net;
using System.Text;
using System.Collections.Concurrent;
using HexLive.Server.Admin;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;

namespace HexLive.Identity;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var root = Environment.GetEnvironmentVariable("HEXLIVE_IDENTITY_DATA")
            ?? throw new InvalidOperationException("HEXLIVE_IDENTITY_DATA must point to the persistent identity directory");
        Directory.CreateDirectory(root);
        using var writerLease = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var keys = new KeyStore(Path.Combine(root, "accounts.json"));
        if (args.Contains("--bootstrap-owner"))
        {
            if (keys.List().Length != 0) throw new InvalidOperationException("Bootstrap requires an empty registry");
            var output = Environment.GetEnvironmentVariable("HEXLIVE_BOOTSTRAP_OUTPUT") ?? throw new InvalidOperationException("Private output path required");
            // Create the delivery file first; no key is ever printed in service logs.
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var delivery = new StreamWriter(new FileStream(output, fileOptions));
            var owner = keys.Issue("Owner", permissions: KeyStore.Permissions, actor: "bootstrap",
                bootstrapId: Environment.GetEnvironmentVariable("HEXLIVE_BOOTSTRAP_ACCOUNT_ID"));
            delivery.Write(owner.Key); return;
        }
        var sessions = new AdminSessions();
        var subjects = new ConcurrentDictionary<string, string>();
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "protection-keys")));
        builder.Services.AddAntiforgery(o => {
            o.Cookie.Name = "__Host-hexlive-identity-csrf";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.Configure<ForwardedHeadersOptions>(o => {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Only the local Caddy proxy may assert the public TLS scheme/client IP.
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("identity", limiter => {
            limiter.PermitLimit = 120; limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
        }));
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.Use(async (c, next) => {
            c.Response.Headers.CacheControl = "no-store";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
            await next();
        });
        KeyStore.Account? Subject(HttpContext c)
        {
            var token = c.Request.Cookies["__Host-hexlive-identity"];
            return token != null && sessions.IsSignedIn(token) && subjects.TryGetValue(token, out var hash) ? keys.AuthenticateHash(hash) : null;
        }
        bool Signed(HttpContext c) => Subject(c)?.Permissions?.Contains("keys.manage") == true;
        string Checks(string[] selected) => string.Join("", KeyStore.Permissions.Select(p => "<label><input type='checkbox' name='permissions' value='" + E(p) + "'" + (selected.Contains(p) ? " checked" : "") + ">" + E(p) + "</label>"));
        static string E(string s) => WebUtility.HtmlEncode(s);
        string Csrf(HttpContext c) => "<input type='hidden' name='__RequestVerificationToken' value='" +
            E(c.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(c).RequestToken!) + "'>";
        async Task<bool> ValidPost(HttpContext c)
        {
            try { await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c); return true; }
            catch (AntiforgeryValidationException) { return false; }
        }
        static IResult Page(string body) => Results.Content("<!doctype html><html lang='ru'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>HexLive — ключи</title><style>body{background:#10171c;color:#edf2f4;font:17px system-ui;max-width:900px;margin:50px auto;padding:20px}input,button{font:inherit;padding:10px;margin:5px}button{cursor:pointer}article{border-top:1px solid #43535d;padding:15px 0}code{overflow-wrap:anywhere}a{color:#bcdf88}</style><h1>HexLive · ключи</h1>" + body + "</html>", "text/html; charset=utf-8");

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/api/identity/v1/validate", (HttpContext c) => {
            var header = c.Request.Headers.Authorization.ToString();
            var identity = header.StartsWith("Bearer ", StringComparison.Ordinal) ? keys.Authenticate(header[7..]) : null;
            return identity == null ? Results.Unauthorized() : Results.Ok(new { accountId = identity.Id, name = identity.Name, permissions = identity.Permissions, revision = identity.Revision, subjectType = identity.SubjectType });
        }).RequireRateLimiting("identity");

        app.MapGet("/", (HttpContext c) => {
            var csrf = Csrf(c);
            if (!Signed(c)) return Page("<form method='post' action='/login'>" + csrf +
                "<input name='key' type='password' autocomplete='current-password' placeholder='Ключ доступа' required><button>Войти</button></form>");
            var html = new StringBuilder("<form method='post' action='/issue'>" + csrf + "<input name='name' maxlength='100' placeholder='Имя' required>" + Checks(["game.play", "bugs.create"]) + "<select name='subjectType'><option value='player'>Игрок</option><option value='agent'>Агент</option></select><button>Выдать ключ</button></form>");
            foreach (var a in keys.List())
            {
                var hidden = csrf + "<input type='hidden' name='id' value='" + E(a.Id) + "'><input type='hidden' name='expectedRevision' value='" + a.Revision + "'>";
                html.Append("<article><strong>").Append(E(a.Name)).Append("</strong> · ").Append(a.Revoked ? "Отозван" : "Активен")
                    .Append("<form method='post' action='/permissions'>").Append(hidden).Append(Checks(a.Permissions!)).Append("<button>Сохранить права</button></form>")
                    .Append("<form method='post' action='/revoke'>").Append(hidden).Append("<button>Отозвать</button></form>")
                    .Append("<form method='post' action='/issue'>").Append(hidden).Append("<input type='hidden' name='name' value='").Append(E(a.Name)).Append("'><button>Заменить ключ</button></form></article>");
            }
            html.Append("<form method='post' action='/logout'>").Append(csrf).Append("<button>Выйти</button></form>");
            return Page(html.ToString());
        });
        app.MapGet("/audit", (HttpContext c) => Signed(c) ? Results.Json(keys.History()) : Results.Unauthorized());
        app.MapPost("/login", async (HttpContext c) => {
            if (!await ValidPost(c)) return Results.BadRequest();
            var source = c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (sessions.LockoutSeconds(source) > 0) return Results.StatusCode(429);
            var form = await c.Request.ReadFormAsync();
            var identity = keys.Authenticate(form["key"].ToString());
            if (identity?.Permissions?.Contains("keys.manage") != true) { sessions.RecordFailure(source); return Results.Unauthorized(); }
            sessions.RecordSuccess(source);
            var session = sessions.CreateSession(); subjects[session] = identity.KeyHash;
            c.Response.Cookies.Append("__Host-hexlive-identity", session, new CookieOptions {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12) });
            return Results.Redirect("/");
        });
        foreach (var action in new[] { "issue", "permissions", "revoke" })
        {
            app.MapPost("/" + action, async (HttpContext c) => {
                if (!Signed(c) || !await ValidPost(c)) return Results.Unauthorized();
                var form = await c.Request.ReadFormAsync();
                var actor = Subject(c);
                if (actor?.Permissions?.Contains("keys.manage") != true) return Results.Unauthorized();
                try {
                    var id = form["id"].ToString();
                    long? revision = long.TryParse(form["expectedRevision"], out var r) ? r : null;
                    if (id.Length > 0 && revision == null) return Results.BadRequest();
                    if (action == "issue") {
                        var issued = keys.Issue(form["name"].ToString(), id.Length == 0 ? null : id,
                            id.Length == 0 ? form["permissions"].Select(p => p!).ToArray() : null, revision, actor.Id,
                            form["subjectType"].ToString() is "agent" ? "agent" : "player");
                        return Page("<p>Скопируйте ключ для " + E(issued.Account.Name) + ". Он показывается один раз.</p><code>" + E(issued.Key) + "</code><p><a href='/'>К списку</a></p>");
                    }
                    if (revision == null) return Results.BadRequest();
                    if (action == "permissions") keys.SetPermissions(id, form["permissions"].Select(p => p!).ToArray(), revision.Value, actor.Id);
                    else keys.Revoke(id, revision, actor.Id);
                    return Results.Redirect("/");
                } catch (KeyStore.ConflictException) { return Results.Conflict(new { error = "Данные изменились. Обновите страницу." }); }
                catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            });
        }
        app.MapPost("/logout", async (HttpContext c) => {
            if (!await ValidPost(c)) return Results.BadRequest();
            var token = c.Request.Cookies["__Host-hexlive-identity"];
            if (token != null) subjects.TryRemove(token, out _);
            sessions.SignOut(token);
            c.Response.Cookies.Delete("__Host-hexlive-identity", new CookieOptions { Secure = true, Path = "/" });
            return Results.Redirect("/");
        });
        await app.RunAsync();
    }
}
