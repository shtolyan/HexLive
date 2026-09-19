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
        string Csrf(HttpContext c) => "<input type='hidden' name='__RequestVerificationToken' value='" +
            AdminView.E(c.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(c).RequestToken!) + "'>";
        async Task<bool> ValidPost(HttpContext c)
        {
            try { await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c); return true; }
            catch (AntiforgeryValidationException) { return false; }
        }
        IResult LoginPage(HttpContext c, string? error = null, int statusCode = 200) => AdminView.Login(Csrf(c), error, statusCode);
        app.MapGet("/login", (HttpContext c) => Results.Redirect("/"));

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/api/identity/v1/validate", (HttpContext c) => {
            var header = c.Request.Headers.Authorization.ToString();
            var identity = header.StartsWith("Bearer ", StringComparison.Ordinal) ? keys.Authenticate(header[7..]) : null;
            return identity == null ? Results.Unauthorized() : Results.Ok(new { accountId = identity.Id, name = identity.Name, permissions = identity.Permissions, revision = identity.Revision, subjectType = identity.SubjectType });
        }).RequireRateLimiting("identity");

        app.MapGet("/", (HttpContext c) => {
            var csrf = Csrf(c);
            if (!Signed(c)) return LoginPage(c);
            var notice = c.Request.Query["done"].ToString() switch {
                "permissions" => "Права сохранены. Участник продолжает пользоваться тем же ключом.",
                "revoke" => "Ключ отозван. Доступ по нему закрыт.", _ => ""
            };
            return AdminView.Dashboard(keys.List(), csrf, Subject(c)!.Id, c.Request.Query["q"].ToString(), notice);
        });
        app.MapGet("/audit", (HttpContext c) => Signed(c) ? Results.Json(keys.History()) : Results.Unauthorized());
        app.MapPost("/login", async (HttpContext c) => {
            if (!await ValidPost(c)) return LoginPage(c, "Форма входа устарела. Повторите ввод ключа.", 400);
            var source = c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (sessions.LockoutSeconds(source) > 0) return LoginPage(c, "Слишком много попыток. Подождите и попробуйте снова.", 429);
            var form = await c.Request.ReadFormAsync();
            var identity = keys.Authenticate(form["key"].ToString().Trim());
            if (identity?.Permissions?.Contains("keys.manage") != true) {
                sessions.RecordFailure(source);
                return LoginPage(c, identity == null
                    ? "Ключ не найден или отозван. Скопируйте выданный ключ целиком и попробуйте снова."
                    : "У этого ключа нет права keys.manage. Владелец должен выдать право управления ключами.", 401);
            }
            sessions.RecordSuccess(source);
            var session = sessions.CreateSession(); subjects[session] = identity.KeyHash;
            c.Response.Cookies.Append("__Host-hexlive-identity", session, new CookieOptions {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12) });
            return Results.Redirect("/");
        });
        foreach (var action in new[] { "issue", "permissions", "revoke" })
        {
            app.MapPost("/" + action, async (HttpContext c) => {
                if (!Signed(c)) return LoginPage(c, "Сессия завершена. Войдите снова, чтобы управлять ключами.", 401);
                if (!await ValidPost(c)) return AdminView.Error("Форма устарела. Вернитесь к списку и повторите действие.", 401);
                var form = await c.Request.ReadFormAsync();
                var actor = Subject(c);
                if (actor?.Permissions?.Contains("keys.manage") != true) return LoginPage(c, "Право управления ключами больше недоступно.", 401);
                try {
                    var id = form["id"].ToString();
                    long? revision = long.TryParse(form["expectedRevision"], out var r) ? r : null;
                    if (id.Length > 0 && revision == null) return AdminView.Error("Форма устарела или заполнена неверно. Вернитесь к списку и повторите действие.", 400);
                    if (action == "issue") {
                        var issued = keys.Issue(form["name"].ToString(), id.Length == 0 ? null : id,
                            id.Length == 0 ? form["permissions"].Select(p => p!).ToArray() : null, revision, actor.Id,
                            form["subjectType"].ToString() is "agent" ? "agent" : "player");
                        return AdminView.Issued(issued.Account.Name, issued.Key);
                    }
                    if (revision == null) return AdminView.Error("Форма устарела или заполнена неверно. Вернитесь к списку и повторите действие.", 400);
                    if (action == "permissions") keys.SetPermissions(id, form["permissions"].Select(p => p!).ToArray(), revision.Value, actor.Id);
                    else keys.Revoke(id, revision, actor.Id);
                    return Results.Redirect("/?done=" + action);
                } catch (KeyStore.ConflictException) { return AdminView.Error("Права этого участника уже изменились. Вернитесь к списку и проверьте актуальные настройки.", 409); }
                catch (ArgumentException e) { return AdminView.Error(e.Message == "Cannot remove the last key manager"
                    ? "Нельзя отключить последнего администратора ключей. Сначала назначьте другого владельца."
                    : "Не удалось сохранить изменения. Проверьте имя, права и актуальность карточки.", 400); }
            });
        }
        app.MapPost("/logout", async (HttpContext c) => {
            if (!await ValidPost(c)) return AdminView.Error("Форма устарела или заполнена неверно. Вернитесь к списку и повторите действие.", 400);
            var token = c.Request.Cookies["__Host-hexlive-identity"];
            if (token != null) subjects.TryRemove(token, out _);
            sessions.SignOut(token);
            c.Response.Cookies.Delete("__Host-hexlive-identity", new CookieOptions { Secure = true, Path = "/" });
            return Results.Redirect("/");
        });
        await app.RunAsync();
    }
}
