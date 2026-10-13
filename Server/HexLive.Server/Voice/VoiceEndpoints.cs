using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Common;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;
using HexLive.VoiceBrain;
using HexLive.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HexLive.Server.Voice;

/// <summary>
/// §171.8: the voice brain hosted inside the game server. A signed-in player asks
/// for a session for THEIR character; ElevenLabs then calls the brain, which reads
/// and commands the world in-process as that player — the same owner, lease and
/// assignment checks as their own clicks. Off unless every provider secret is set.
/// </summary>
public static class VoiceEndpoints
{
    public const string Root = "/api/voice/v1";
    /// <summary>ElevenLabs custom-LLM BASE url is <c>https://host/api/voice/v1/brain</c>; it appends /chat/completions.</summary>
    public const string BrainPath = Root + "/brain/chat/completions";
    public const int SessionsPerMinute = 6;
    public static readonly TimeSpan ProviderSessionTimeout = TimeSpan.FromSeconds(15);

    public static VoiceBrainService? Map(WebApplication app, WorldSupervisor worlds, ControlLeases leases,
        AgentSessionRegistry agents, PlayerHttpIdentity identity, ILogger log, AccountStore? accounts = null,
        HttpClient? providerHttp = null)
    {
        if (!BrainOptions.Configured()) return null;
        var options = BrainOptions.FromEnvironment();
        var tools = new Mcp.McpTools(() => worlds.Host, () => worlds.CaptureViewerSession().WorldGeneration, leases, agents);
        bool Owns(string playerId, int npcId) => worlds.CaptureViewerSession().Assignments?.StillAssigned(playerId, npcId) == true;
        VoiceBrainService? voice = null;
        bool CanSpend(string owner) => accounts == null || (owner.StartsWith("ws:", StringComparison.Ordinal) &&
            options.UsageDirectory.Length > 0 && voice!.Balance(owner, accounts.OpeningCreditUsd(owner[3..])).BalanceUsd > 0);
        voice = new VoiceBrainService(options, ticket => new InProcessWorldGateway(tools, ticket.OwnerUserId,
            npc => ticket.WorldId == worlds.Host.WorldId && ticket.OwnerUserId.StartsWith("ws:", StringComparison.Ordinal) &&
                Owns(ticket.OwnerUserId[3..], npc), () => worlds.Host), log, providerHttp, canSpend: CanSpend);
        voice.MapCompletions(app, BrainPath);
        VoiceBrainService.MapClientScript(app, Root + "/client.js");
        var recent = new ConcurrentDictionary<string, (long Window, int Count)>();
        if (accounts != null)
            app.MapGet(AccountEndpoints.Prefix + "/balance", (HttpContext c) =>
            {
                c.Response.Headers.CacheControl = "no-store";
                var account = accounts.Resolve(AccountEndpoints.Bearer(c));
                if (account == null) return Results.Unauthorized();
                if (options.UsageDirectory.Length == 0) return Results.StatusCode(503);
                return Results.Json(voice.Balance("ws:" + account.PlayerId, accounts.OpeningCreditUsd(account.PlayerId)));
            });

        app.MapGet(Root + "/relationship/{npcId:int}", (HttpContext c, int npcId) =>
        {
            if (!identity.TryResolve(c, out var playerId)) return Results.Unauthorized();
            if (!Owns(playerId, npcId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(voice.RelationshipView(worlds.Host.WorldId + ":" + npcId, "ws:" + playerId));
        });

        app.MapPost(Root + "/session", async (HttpContext c, VoiceSessionRequest request) =>
        {
            if (!identity.TryResolve(c, out var playerId)) return Results.Unauthorized();
            // §171: the agent spends tokens only for a player who is here and owns this body.
            if (!Owns(playerId, request.NpcId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var slot = recent.AddOrUpdate(playerId, _ => (window, 1), (_, s) => s.Window == window ? (window, s.Count + 1) : (window, 1));
            if (slot.Count > SessionsPerMinute) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var host = worlds.Host;
            var facts = host.Read(w => w.Entities.Npcs.TryGetValue(new EntityId(request.NpcId), out var npc)
                ? (Alive: true, npc.DisplayName, npc.VoiceBank) : (Alive: false, DisplayName: "", VoiceBank: ""));
            if (!facts.Alive) return Results.NotFound();
            var owner = "ws:" + playerId;
            if (!CanSpend(owner)) return Results.Json(new { error = "insufficient_credit" }, statusCode: 402);
            var ticket = voice.Tickets.Mint(owner, host.WorldId + ":" + request.NpcId, host.WorldId, request.NpcId);
            voice.Tickets.TryVerify(ticket, out var signed);
            string token;
            using var issuance = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
            issuance.CancelAfter(ProviderSessionTimeout);
            try { token = await voice.ElevenLabs.ConversationTokenAsync(options.AgentId, issuance.Token); }
            catch (OperationCanceledException) when (!c.RequestAborted.IsCancellationRequested)
            {
                log.LogWarning("voice session issuance timed out");
                return Results.Json(new { error = "voice_provider_timeout" }, statusCode: 504);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or System.IO.InvalidDataException)
            {
                log.LogWarning("voice session issuance failed: {Type}", ex.GetType().Name);
                return Results.Json(new { error = "voice_provider_unavailable" }, statusCode: 502);
            }
            voice.RecordSessionStarted(signed);
            log.LogInformation("voice session player={Player} npc={Npc}", playerId[..8], request.NpcId);
            return Results.Json(new
            {
                conversationToken = token, ticket, sessionId = signed.SessionId, name = facts.DisplayName,
                voiceId = voice.VoiceFor(null, facts.VoiceBank, facts.DisplayName),
            });
        });

        app.MapPost(Root + "/session/end", async (HttpContext c, VoiceSessionEnd request) =>
        {
            if (!identity.TryResolve(c, out var playerId) || !voice.Tickets.TryVerify(request.Ticket, out var ticket) ||
                ticket.OwnerUserId != "ws:" + playerId || request.ConversationId is not { Length: > 0 and <= 128 })
                return Results.NotFound();
            try
            {
                // The final charge must persist even if the browser goes away.
                var (seconds, credits) = await voice.RecordSessionEndAsync(request.ConversationId, ticket, CancellationToken.None);
                return Results.Json(new { seconds, providerCredits = credits });
            }
            catch (System.IO.InvalidDataException) { return Results.NotFound(); }
            catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException or OperationCanceledException)
            { return Results.StatusCode(503); }
        });
        Console.WriteLine($"[server] voice brain     {BrainPath} (model {options.Model}, agent {options.AgentId[..Math.Min(10, options.AgentId.Length)]}…)");
        return voice;
    }
}

public sealed record VoiceSessionRequest(int NpcId);
public sealed record VoiceSessionEnd(string ConversationId, string Ticket);

/// <summary>The MCP tool table called in-process, as the ticket's owner, scoped to their own characters.</summary>
public sealed class InProcessWorldGateway(Mcp.McpTools tools, string owner, Func<int, bool> canAccessNpc,
    Func<WorldHost>? currentHost = null) : IWorldGateway
{
    public Task RecordConversationAsync(int npcId, string turnId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!canAccessNpc(npcId)) throw new WorldToolRejectedException("NpcAccessDenied");
        if (currentHost == null) throw new WorldToolRejectedException("ConversationRecorderUnavailable");
        var result = currentHost().SubmitManualCommand(new RecordAgentSocialCommand(
            new EntityId(npcId), turnId, CompanionReaction.Neutral));
        if (!result.Accepted) throw new WorldToolRejectedException(result.Reason);
        return Task.CompletedTask;
    }

    public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var commandOwner = "voice:" + owner;
        var args = JsonSerializer.SerializeToElement(arguments);
        if (tool == "acquire_control")
        {
            var attach = new { npcId = args.GetProperty("npcId").GetInt32(), displayName = "Голосовой агент",
                capabilities = new[] { "worldActions", "relationView" }, ttlSeconds = 120 };
            tools.Call("attach_agent", JsonSerializer.SerializeToElement(attach), commandOwner, out var attachError, canAccessNpc);
            if (attachError) throw new WorldToolRejectedException("VoiceAttachmentRejected");
        }
        var text = tools.Call(tool, args, commandOwner, out var isError, canAccessNpc);
        if (isError) throw new WorldToolRejectedException(text.Length > 300 ? text[..300] : text);
        using var doc = JsonDocument.Parse(text);
        return Task.FromResult(doc.RootElement.Clone());
    }
}
