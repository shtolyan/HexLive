using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Simulation.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Accounts;

/// <summary>§171.9: optional account API. Only a verified Firebase token can mint a game session.</summary>
public static class AccountEndpoints
{
    public const string Prefix = "/api/accounts/v1";

    public static void Map(WebApplication app, AccountStore? accounts, FirebaseTokenVerifier? verifier,
        WorldSupervisor worlds, SessionRateLimit? limiter = null)
    {
        if (accounts == null || verifier == null) return;
        CharacterEndpoints.Map(app, accounts, worlds);
        limiter ??= new SessionRateLimit();
        app.MapPost(Prefix + "/session", async (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            // Do not trust X-Forwarded-For from arbitrary clients. A proxy must be configured upstream.
            if (!limiter.Allow(context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown"))
                return Error("try_later", 429);
            SessionRequest? request;
            try
            {
                // Bound streamed bodies too (Content-Length is optional and attacker-controlled).
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
                {
                    if (body.Length + count > FirebaseTokenVerifier.MaxTokenLength + 1024) return Error("invalid_credentials", 401);
                    body.Write(buffer, 0, count);
                }
                request = JsonSerializer.Deserialize<SessionRequest>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException) { return Error("invalid_credentials", 401); }
            var result = await verifier.VerifyAsync(request?.IdToken, context.RequestAborted);
            if (result.Identity == null) return Error(result.Error, 401);
            try { return Results.Json(accounts.CreateSession(result.Identity)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Error("try_later", 503); }
        });

        app.MapDelete(Prefix + "/session", (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            var token = Bearer(context);
            if (accounts.Resolve(token) == null) return Error("invalid_credentials", 401);
            try { return accounts.Revoke(token) ? Results.NoContent() : Error("invalid_credentials", 401); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Error("try_later", 503); }
        });

        app.MapGet(Prefix + "/me", (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            var session = accounts.Resolve(Bearer(context));
            if (session == null) return Error("invalid_credentials", 401);
            var world = worlds.CaptureViewerSession();
            var assignments = world.Assignments!;
            var assigned = assignments.Reconcile(world.Host, session.PlayerId, 1, world.Lifetime,
                world.WorldGeneration, accountCharacter: true);
            var character = world.Host.Read(state => {
                var id = assigned.FirstOrDefault();
                return id > 0 && state.Entities.Npcs.TryGetValue(new EntityId(id), out var npc)
                    ? new AccountCharacter(id, npc.DisplayName, true) : null;
            });
            if (world.Lifetime.IsCancellationRequested) return Error("try_later", 503);
            var needsNewCharacter = assignments.NeedsNewCharacter(session.PlayerId);
            return Results.Json(new { session.PlayerId, session.Provider, character, needsNewCharacter,
                messageCode = needsNewCharacter ? "character_died_create_new" : null });
        });
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    internal static string? Bearer(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..].Trim() : null;
    }
    private sealed record SessionRequest(string? IdToken);
    private sealed record AccountCharacter(int NpcId, string Name, bool Alive);
}

/// <summary>§171.9: ten attempts per minute/IP, bounded memory, no attacker-provided identity keys.</summary>
public sealed class SessionRateLimit(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, int Count)> _windows = new();
    public bool Allow(string ip)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_windows.TryGetValue(ip, out var window) && window.Until > now)
            {
                if (window.Count >= 10) return false;
                _windows[ip] = (window.Until, window.Count + 1);
                return true;
            }
            if (_windows.Count >= 10000)
            {
                foreach (var key in _windows.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) _windows.Remove(key);
                if (_windows.Count >= 10000) return false;
            }
            _windows[ip] = (now.AddMinutes(1), 1);
            return true;
        }
    }
}
