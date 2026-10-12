using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Common;
using HexLive.VoiceBrain;
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

    public static VoiceBrainService? Map(WebApplication app, WorldSupervisor worlds, ControlLeases leases,
        AgentSessionRegistry agents, PlayerHttpIdentity identity, ILogger log)
    {
        if (!BrainOptions.Configured()) return null;
        var options = BrainOptions.FromEnvironment();
        var tools = new Mcp.McpTools(() => worlds.Host, () => worlds.CaptureViewerSession().WorldGeneration, leases, agents);
        bool Owns(string playerId, int npcId) => worlds.CaptureViewerSession().Assignments?.StillAssigned(playerId, npcId) == true;
        var voice = new VoiceBrainService(options, ticket => new InProcessWorldGateway(tools, ticket.OwnerUserId,
            npc => ticket.OwnerUserId.StartsWith("ws:", StringComparison.Ordinal) && Owns(ticket.OwnerUserId[3..], npc)), log);
        voice.MapCompletions(app, BrainPath);
        VoiceBrainService.MapClientScript(app, Root + "/client.js");
        var recent = new ConcurrentDictionary<string, (long Window, int Count)>();

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
            var ticket = voice.Tickets.Mint(owner, host.WorldId + ":" + request.NpcId, host.WorldId, request.NpcId);
            string token;
            try { token = await voice.ElevenLabs.ConversationTokenAsync(options.AgentId, c.RequestAborted); }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
            log.LogInformation("voice session player={Player} npc={Npc}", playerId[..8], request.NpcId);
            return Results.Json(new
            {
                conversationToken = token, ticket, name = facts.DisplayName,
                voiceId = voice.VoiceFor(null, facts.VoiceBank, facts.DisplayName),
            });
        });

        app.MapPost(Root + "/session/end", async (HttpContext c, VoiceSessionEnd request) =>
        {
            if (!identity.TryResolve(c, out var playerId) || !voice.Tickets.TryVerify(request.Ticket, out var ticket) ||
                ticket.OwnerUserId != "ws:" + playerId || request.ConversationId is not { Length: > 0 and <= 128 })
                return Results.NotFound();
            var (seconds, credits) = await voice.RecordSessionEndAsync(request.ConversationId, ticket, c.RequestAborted);
            return Results.Json(new { seconds, providerCredits = credits });
        });
        Console.WriteLine($"[server] voice brain     {BrainPath} (model {options.Model}, agent {options.AgentId[..Math.Min(10, options.AgentId.Length)]}…)");
        return voice;
    }
}

public sealed record VoiceSessionRequest(int NpcId);
public sealed record VoiceSessionEnd(string ConversationId, string Ticket);

/// <summary>The MCP tool table called in-process, as the ticket's owner, scoped to their own characters.</summary>
public sealed class InProcessWorldGateway(Mcp.McpTools tools, string owner, Func<int, bool> canAccessNpc) : IWorldGateway
{
    public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var text = tools.Call(tool, JsonSerializer.SerializeToElement(arguments), owner, out var isError, canAccessNpc);
        if (isError) throw new WorldToolRejectedException(text.Length > 300 ? text[..300] : text);
        using var doc = JsonDocument.Parse(text);
        return Task.FromResult(doc.RootElement.Clone());
    }
}
