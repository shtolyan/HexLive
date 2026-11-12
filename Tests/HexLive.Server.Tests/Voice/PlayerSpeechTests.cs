using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.AgentCore.Studio;
using HexLive.Server.Accounts;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Server.Tests.Accounts;
using HexLive.Server.Voice;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Wire;
using HexLive.VoiceBrain;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.Server.Tests.Voice;

[NonParallelizable]
public sealed class PlayerSpeechTests
{
    [Test]
    public async Task OwnedRecordingIsSpokenExactlyAndDeliveredToAnotherPlayerOnlyOnce()
    {
        var dir = Directory.CreateTempSubdirectory("player-speech-");
        try
        {
            var registry = new AssetRegistryStore(Path.Combine(dir.FullName, "assets"));
            using var worlds = new WorldSupervisor(12345, GameMode.Feud, Path.Combine(dir.FullName, "world.sav"),
                new AssetGarmentCatalog(registry, AccountHttpTests.RepoFile("SimData/simdata.json")),
                false, false, new LlmHostOptions(), null, CancellationToken.None, startPaused: true);
            var accounts = new AccountStore(Path.Combine(dir.FullName, "accounts.json"), "fixture");
            var owner = accounts.CreateSession(new("speaker", "password"));
            var listener = accounts.CreateSession(new("listener", "password"));
            worlds.Assignments!.AdminAssign(owner.PlayerId, 1);
            worlds.Assignments.AdminAssign(listener.PlayerId, 2);
            var relay = new PlayerSpeechRelay();
            using var upstream = new Provider();
            using var provider = new HttpClient(upstream, disposeHandler: false);
            using var deepgram = new DeepgramTokenBroker("test-stt", upstream);
            var usageDirectory = Path.Combine(dir.FullName, "usage");
            var voice = new VoiceBrainService(new BrainOptions
            {
                OpenAiKey = "test-openai", ElevenLabsKey = "test-eleven", BrainSecret = "test-brain",
                TicketKey = new byte[32], StateDirectory = dir.FullName, UsageDirectory = usageDirectory
            }, _ => throw new InvalidOperationException("This route has no LLM turn"), NullLogger.Instance, provider);

            await using var app = AccountHttpTests.BuildApp();
            PlayerSpeechEndpoints.Map(app, worlds, new PlayerHttpIdentity(null, accounts),
                relay, deepgram, voice, _ => true, NullLogger.Instance);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var listenerSocket = new SpeechSocket();
            using var anonymousSocket = new SpeechSocket();
            var session = worlds.CaptureViewerSession();
            using var viewing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var other = new ViewerConnection(session.Host, listenerSocket, session.SimData, false,
                "ws:" + listener.PlayerId, new ControlLeases(45), new[] { 2 },
                worldGeneration: session.WorldGeneration, playerSpeech: relay);
            var anonymous = new ViewerConnection(session.Host, anonymousSocket, session.SimData, false,
                worldGeneration: session.WorldGeneration, playerSpeech: relay);
            var receive = Task.WhenAll(other.RunAsync(viewing.Token), anonymous.RunAsync(viewing.Token));
            try
            {
                await Task.WhenAll(listenerSocket.Connected.Task, anonymousSocket.Connected.Task).WaitAsync(viewing.Token);
                var id = Guid.NewGuid();
                async Task<HttpStatusCode> Send(string? token, byte[] input, string? worldId = null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        PlayerSpeechEndpoints.Path + $"?npcId=1&requestId={id:N}&language=ru" +
                        "&worldId=" + Uri.EscapeDataString(worldId ?? session.Host.WorldId));
                    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Content = new ByteArrayContent(input);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    using var response = await client.SendAsync(request, viewing.Token);
                    return response.StatusCode;
                }
                Assert.That(await Send(null, Wave()), Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(await Send(listener.SessionToken, Wave()), Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(await Send(owner.SessionToken, Wave(), "stale-world"), Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(await Send(owner.SessionToken, new byte[64]), Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(upstream.Requests, Is.Zero);
                Assert.That(await Send(owner.SessionToken, Wave()), Is.EqualTo(HttpStatusCode.NoContent));
                var frame = await listenerSocket.Speech.Task.WaitAsync(viewing.Token);
                var wav = await listenerSocket.Wav.Task.WaitAsync(viewing.Token);
                Assert.Multiple(() =>
                {
                    Assert.That(frame.Delivery, Is.EqualTo(AgentSpeechDelivery.PlayerBroadcast));
                    Assert.That(frame.Priority, Is.EqualTo(AgentSpeechPriority.Talk));
                    Assert.That(frame.NpcId, Is.EqualTo(1), "listener is assigned only NPC2");
                    Assert.That(frame.Text, Is.EqualTo(Provider.Phrase));
                    Assert.That(frame.TotalBytes, Is.GreaterThan(44));
                    Assert.That(wav, Has.Length.EqualTo(frame.TotalBytes));
                    Assert.That(Convert.ToHexString(SHA256.HashData(wav)), Is.EqualTo(frame.Sha256).IgnoreCase);
                    Assert.That(relay.After(0, session.WorldGeneration + 1), Is.Empty);
                    Assert.That(upstream.Synthesized, Is.EqualTo(Provider.Phrase));
                    Assert.That(upstream.Model, Is.EqualTo("eleven_flash_v2_5"));
                });
                Assert.That(await Send(owner.SessionToken, Wave()), Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(upstream.Requests, Is.EqualTo(2));
                Assert.That(relay.After(0, session.WorldGeneration), Has.Length.EqualTo(1));
                Assert.That(anonymousSocket.Speech.Task.IsCompleted, Is.False);
                var usage = JsonlUsageLedger.ReadAll(usageDirectory);
                Assert.That(usage.Select(x => x.Kind), Is.EquivalentTo(new[] { UsageKind.Stt, UsageKind.Tts }));
                Assert.That(usage.All(x => x.NpcId == 1 && x.OwnerUserId == "ws:" + owner.PlayerId), Is.True);
            }
            finally { viewing.Cancel(); await receive; await app.StopAsync(); }
        }
        finally { dir.Delete(true); }
    }

    private static byte[] Wave()
    {
        const int bytes = 16000;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(16000); writer.Write(32000);
        writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(bytes);
        writer.Write(new byte[bytes]);
        return output.ToArray();
    }

    private sealed class Provider : HttpMessageHandler
    {
        internal const string Phrase = "Идём к костру. Я вас слышу!";
        public int Requests;
        public string? Synthesized;
        public string? Model;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.RequestUri!.Host == "api.deepgram.com")
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                    new { results = new { channels = new[] { new { alternatives = new[] { new { transcript = Phrase } } } } } })) };
            Assert.That(request.RequestUri.AbsolutePath, Does.StartWith("/v1/text-to-speech/"));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Synthesized = document.RootElement.GetProperty("text").GetString();
            Model = document.RootElement.GetProperty("model_id").GetString();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[48_000]) };
        }
    }

    private sealed class SpeechSocket : WebSocket
    {
        public readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<AgentSpeechBeginFrame> Speech = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<byte[]> Wav = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _chunks = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() => _chunks.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken token)
        {
            var payload = buffer.AsSpan();
            if (payload[0] == (byte)FrameKind.Handshake) Connected.TrySetResult();
            if (payload[0] == (byte)FrameKind.AgentSpeechBegin)
                Speech.TrySetResult(AgentWire.ReadAgentSpeechBegin(payload[1..].ToArray()));
            if (payload[0] == (byte)FrameKind.AgentSpeechChunk)
                _chunks.Write(AgentWire.ReadAgentSpeechChunk(payload[1..].ToArray()).Bytes);
            if (payload[0] == (byte)FrameKind.AgentSpeechEnd) Wav.TrySetResult(_chunks.ToArray());
            return Task.CompletedTask;
        }
    }
}
