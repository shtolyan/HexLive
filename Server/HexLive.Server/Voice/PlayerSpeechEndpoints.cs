using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HexLive.AgentCore.Studio;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Wire;
using HexLive.VoiceBrain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace HexLive.Server.Voice;

internal static class PlayerSpeechEndpoints
{
    internal const string Path = VoiceEndpoints.Root + "/speak";
    private const int CaptureMs = 16_000;
    private const int CaptureRate = 16_000;
    private const int MaxBytes = 44 + CaptureMs * CaptureRate * 2 / 1000;
    private static readonly TimeSpan ProcessingBudget = TimeSpan.FromSeconds(20);

    internal static void Map(WebApplication app, WorldSupervisor worlds, PlayerHttpIdentity identity,
        PlayerSpeechRelay relay, DeepgramTokenBroker deepgram, VoiceBrainService voice, Func<string, bool> canSpend,
        ILogger log)
    {
        if (!deepgram.Available) return;
        app.MapPost(Path, async (HttpContext context, int npcId, Guid requestId, string language, string worldId) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!identity.TryResolve(context, out var player)) return Results.Unauthorized();
            if (npcId <= 0 || requestId == Guid.Empty || language is not ("ru" or "en")) return Results.BadRequest();
            var session = worlds.CaptureViewerSession();
            if (worldId != session.Host.WorldId) return Results.StatusCode(409);
            if (session.Assignments?.StillAssigned(player, npcId) != true) return Results.StatusCode(403);
            var speaker = session.Host.Read(world =>
                world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc) && CanSpeak(npc, world.Tick)
                    ? voice.PlayerSpeechVoiceFor(npc.VoiceBank, npc.DisplayName, npc.ActorMesh, npc.Sex == GarmentSex.Male)
                    : "");
            if (speaker.Length == 0) return Results.StatusCode(409);
            if (context.Request.ContentType != "audio/wav" ||
                context.Request.ContentLength is < 44 or > MaxBytes) return Results.BadRequest();
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = MaxBytes;

            var owner = "ws:" + player;
            var admission = relay.Begin(owner, session.WorldGeneration, requestId, out var attempt);
            if (admission == PlayerSpeechRelay.Admission.Duplicate) return Results.NoContent();
            if (attempt == null) return Results.StatusCode(429);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Lifetime);
            lifetime.CancelAfter(ProcessingBudget);
            var usage = new UsageContext(owner, session.Host.WorldId + ":" + npcId, session.Host.WorldId,
                npcId, "speak-" + requestId.ToString("N"));
            try
            {
                if (!canSpend(owner)) return Results.StatusCode(402);
                using var input = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, lifetime.Token)) > 0)
                {
                    if (input.Length + read > MaxBytes) return Results.StatusCode(413);
                    input.Write(buffer, 0, read);
                }
                var recorded = input.ToArray();
                if (!WavePcm16Mono44100.TryValidateAtRate(recorded, CaptureRate, CaptureMs, out var elapsedMs, out _) ||
                    elapsedMs < 250) return Results.BadRequest();

                string transcript;
                try
                {
                    transcript = await deepgram.TranscribePlayerSpeechAsync(recorded, language, lifetime.Token);
                    voice.RecordPlayerSpeechRecognition(usage, elapsedMs / 1000d, "ok", "");
                }
                catch (Exception ex)
                {
                    voice.RecordPlayerSpeechRecognition(usage, elapsedMs / 1000d,
                        lifetime.IsCancellationRequested ? "cancelled" : "error", ex.GetType().Name);
                    throw;
                }
                if (transcript.Length == 0 || transcript.Length > AgentWire.MaxSpeechCharacters ||
                    transcript.Any(char.IsControl)) return Results.StatusCode(422);
                var wav = await voice.SynthesizePlayerSpeechAsync(usage, speaker, transcript, lifetime.Token);
                if (!WavePcm16Mono44100.TryValidateAtRate(wav, 44100, 30_000, out var durationMs, out _))
                    return Results.StatusCode(502);
                var current = worlds.CaptureViewerSession();
                if (current.WorldGeneration != session.WorldGeneration || current.Assignments?.StillAssigned(player, npcId) != true ||
                    !current.Host.Read(world => world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc) &&
                        CanSpeak(npc, world.Tick))) return Results.StatusCode(409);
                lifetime.Token.ThrowIfCancellationRequested();
                relay.Complete(attempt, npcId, transcript, language, wav, durationMs);
                return Results.NoContent();
            }
            catch (BadHttpRequestException ex) { return Results.StatusCode(ex.StatusCode); }
            catch (OperationCanceledException) { return Results.StatusCode(504); }
            catch (Exception ex)
            {
                log.LogWarning("Player speech failed: {Type}", ex.GetType().Name);
                return Results.StatusCode(502);
            }
            finally { relay.Abort(attempt); }
        });
    }

    private static bool CanSpeak(NPCState npc, int tick) => npc.Health > 0f && !npc.IsUnconscious(tick) &&
        npc.Execution.CurrentInteraction is not (InteractionType.Sleep or InteractionType.Romance);
}
