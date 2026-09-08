using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Server.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Bugs;

public static class BugApiEndpoints
{
    private const string AdminCookie = "hexlive_admin";

    public static void Map(WebApplication app, BugDatabase bugs, AccessTokenFile agentToken,
        AccessTokenFile? playerToken, AdminSessions sessions)
    {
        app.MapGet("/api/bugs/v1/reports", (HttpContext c) =>
        {
            var status = c.Request.Query["status"].ToString();
            var includeArchived = !string.Equals(c.Request.Query["archived"], "false", StringComparison.OrdinalIgnoreCase);
            return Results.Json(bugs.List(status, includeArchived));
        });

        app.MapGet("/api/bugs/v1/reports/{id:int}", (HttpContext c, int id) =>
        {
            var report = bugs.Get(id);
            return report == null ? Results.NotFound() : Results.Json(report);
        });

        // Filing is intentionally public: the game may report a connection bug
        // before it has authenticated for character control. Everything that
        // can alter somebody else's report remains authenticated.
        app.MapPost("/api/bugs/v1/reports", async (HttpContext c) =>
        {
            var request = await Read<CreateBugRequest>(c);
            return request == null
                ? Results.BadRequest(new { error = "invalid JSON" })
                : Try(() => Results.Json(bugs.Create(request), statusCode: StatusCodes.Status201Created));
        });

        app.MapPost("/api/bugs/v1/reports/{id:int}", async (HttpContext c, int id) =>
        {
            var role = Role(c, agentToken, playerToken, sessions);
            if (role == BugApiRole.None) return Results.Unauthorized();
            var request = await Read<UpdateBugRequest>(c);
            if (request == null) return Results.BadRequest(new { error = "invalid JSON" });
            if (role == BugApiRole.Player && !PlayerUpdateAllowed(bugs.Get(id), request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Try(() =>
            {
                var report = bugs.Update(id, request);
                return report == null ? Results.NotFound() : Results.Json(report);
            });
        });

        app.MapPost("/api/bugs/v1/reports/{id:int}/comments", async (HttpContext c, int id) =>
        {
            var role = Role(c, agentToken, playerToken, sessions);
            if (role == BugApiRole.None) return Results.Unauthorized();
            var request = await Read<AddBugCommentRequest>(c);
            if (request == null) return Results.BadRequest(new { error = "invalid JSON" });
            if (role == BugApiRole.Player) request.Author = "user";
            return Try(() =>
            {
                var report = bugs.AddComment(id, request);
                return report == null ? Results.NotFound() : Results.Json(report);
            });
        });

        app.MapPost("/api/bugs/v1/reports/{id:int}/comments/{ordinal:int}", async (HttpContext c, int id, int ordinal) =>
        {
            var role = Role(c, agentToken, playerToken, sessions);
            if (role == BugApiRole.None) return Results.Unauthorized();
            var request = await Read<AddBugCommentRequest>(c);
            if (request == null) return Results.BadRequest(new { error = "invalid JSON" });
            return Try(() =>
            {
                var report=bugs.EditComment(id,ordinal,request,role==BugApiRole.Player);
                return report==null?Results.NotFound():Results.Json(report);
            });
        });

        // §114.4c: fix-commit patches. The server owns no repository, so the
        // agent uploads `git show` of every fix commit from its checkout; the
        // card then shows files and the diff without leaving the tracker.
        app.MapGet("/api/bugs/v1/commits", () => Results.Json(bugs.ListCommitPatchShas()));

        app.MapGet("/api/bugs/v1/commits/{sha}", (string sha) =>
        {
            var patch = Try(() => bugs.GetCommitPatch(sha));
            return patch == null ? Results.NotFound() : Results.Json(patch);
        });

        app.MapPut("/api/bugs/v1/commits/{sha}", async (HttpContext c, string sha) =>
        {
            if (Role(c, agentToken, playerToken, sessions) != BugApiRole.Agent) return Results.Unauthorized();
            var request = await Read<BugCommitPatch>(c);
            if (request == null) return Results.BadRequest(new { error = "invalid JSON" });
            if (!string.IsNullOrWhiteSpace(request.Sha) &&
                !string.Equals(request.Sha.Trim(), sha.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "sha in the body differs from the route" });
            request.Sha = sha;
            return Try(() => Results.Json(bugs.PutCommitPatch(request)));
        });

        app.MapPost("/api/bugs/v1/reports/{id:int}/delete", (HttpContext c, int id) =>
        {
            if (!CanAdminister(c, agentToken, sessions)) return Results.Unauthorized();
            return bugs.Delete(id) ? Results.NoContent() : Results.NotFound();
        });

        app.MapDelete("/api/bugs/v1/reports/{id:int}", (HttpContext c, int id) =>
        {
            if (!CanAdminister(c, agentToken, sessions)) return Results.Unauthorized();
            return bugs.Delete(id) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static bool PlayerUpdateAllowed(BugReport? current, UpdateBugRequest request)
    {
        if (current == null) return true;
        if (request.AssignedAgent != null || request.AgentHandoff != null || request.FixCommits != null ||
            request.ReadyForTestInVersion != null || request.FixedInVersion != null)
            return false;
        if (request.Status == null) return true;
        return current.Status == BugStatuses.ReadyForTest &&
               request.Status is BugStatuses.Fixed or BugStatuses.Rework;
    }

    private static bool CanAdminister(HttpContext c, AccessTokenFile agent, AdminSessions sessions) =>
        sessions.IsSignedIn(c.Request.Cookies[AdminCookie] ?? string.Empty) || agent.Matches(Bearer(c));

    private static BugApiRole Role(HttpContext c, AccessTokenFile agent, AccessTokenFile? player,
        AdminSessions sessions)
    {
        if (sessions.IsSignedIn(c.Request.Cookies[AdminCookie] ?? string.Empty) || agent.Matches(Bearer(c)))
            return BugApiRole.Agent;
        return player != null && player.Matches(Bearer(c)) ? BugApiRole.Player : BugApiRole.None;
    }

    private static string Bearer(HttpContext c)
    {
        var value = c.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value.Substring(prefix.Length).Trim() : string.Empty;
    }

    private static async Task<T?> Read<T>(HttpContext context) where T : class
    {
        try { return await context.Request.ReadFromJsonAsync<T>(); }
        catch (JsonException) { return null; }
    }

    private static IResult Try(Func<IResult> action)
    {
        try { return action(); }
        catch (BugRevisionConflictException e) { return Results.Conflict(new { error=e.Message, actualRevision=e.ActualRevision }); }
        catch (InvalidDataException e) { return Results.BadRequest(new { error=e.Message }); }
    }

    /// <summary>A malformed SHA in a read is «not found», not a 500.</summary>
    private static BugCommitPatch? Try(Func<BugCommitPatch?> action)
    {
        try { return action(); }
        catch (InvalidDataException) { return null; }
    }

    private enum BugApiRole { None, Player, Agent }
}
