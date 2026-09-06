using System.Net;
using System.Text.Json;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

[NonParallelizable]
public sealed class AgentRuntimeIntegrationTests
{
    [Test]
    public void InboxBatchDoesNotDiscardMessagesBeyondOneThousandCharacters()
    {
        var texts = Enumerable.Range(0, 16).Select(i => $"{i}:" + new string('я', 220)).ToArray();
        using var inbox = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            messages = texts.Select(text => new { text }).ToArray(),
        }));
        var method = typeof(AgentHostRuntime).GetMethod("MergePlayerMessages",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.That(method.Invoke(null, new object[] { inbox.RootElement }),
            Is.EqualTo(string.Join(Environment.NewLine, texts)));
    }

    [Test]
    public async Task SleepWakeVoiceCancellationAndDetachUseRealMcpToolsWithoutPaidProviders()
    {
        var root = FindRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "agent-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        using var stop = new CancellationTokenSource();
        using var host = new WorldHost(12345, GameMode.Feud, Path.Combine(temporary, "world.sav"),
            Path.Combine(root, "SimData/simdata.json"), false, companionProfile: "masha");
        host.EnableMcpEventLog();
        var registry = new AgentSessionRegistry();
        var leases = new ControlLeases(45);
        leases.TryAcquire(901, "ws:previous-player-control", out _, out _);
        Assert.That(host.SubmitManualCommand(new HexLive.Simulation.Runtime.SetManualControlCommand(
            new EntityId(901), true)).Accepted, Is.True);
        var tools = new McpTools(() => host, () => 1, leases, registry, SpecLibrary.Discover(null));
        using var handler = new InProcessMcp(tools);
        var providers = new CountingProviders();
        var options = new AgentHostOptions
        {
            McpUri = new Uri("http://localhost/mcp"), McpToken = "test-only",
            ProfileId = "masha", DisplayName = "Test agent", MemoryDirectory = Path.Combine(temporary, "memory"),
            StateDirectory = Path.Combine(temporary, "state"), WorldId = "fixture",
            XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture",
            ElevenLabsVoiceId = "fixture", FakeProviders = true,
        };
        var runtime = new AgentHostRuntime(options, providers, handler);
        var running = runtime.RunAsync(stop.Token);
        try
        {
            await Until(() => registry.HasAttachment(901));
            Assert.That(leases.Snapshot(), Is.Empty, "attachment releases old player control without holding a lease itself");
            // Cross the entire model-heartbeat interval with no authorized viewer.
            await Task.Delay(TimeSpan.FromSeconds(31));
            Assert.Multiple(() =>
            {
                Assert.That(providers.Decisions, Is.Zero);
                Assert.That(providers.Syntheses, Is.Zero);
                Assert.That(registry.StatesFor(new[] { 901 })[0].Phase, Is.EqualTo(AgentPhase.Sleeping));
                Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
            });

            registry.SetViewerPresence("authorized-player", new[] { 901 }, true);
            handler.Unconscious = true;
            Assert.That(registry.TryEnqueuePlayerText(901, "voice1", "ru", "Привет!", out _), Is.True);
            await Until(() => registry.StatesFor(new[] { 901 })[0].IntentSummary.Contains("Без сознания"));
            Assert.That(providers.Decisions, Is.Zero, "Unconsciousness must not generate repeating paid replies");
            Assert.That(providers.Syntheses, Is.Zero);
            providers.FailOnce = true;
            handler.Unconscious = false;
            await Until(() => registry.LatestSpeechSequence > 0);
            Assert.Multiple(() =>
            {
                Assert.That(providers.Decisions, Is.EqualTo(2), "One failed request followed by one successful retry");
                Assert.That(providers.Syntheses, Is.EqualTo(1));
                Assert.That(providers.LastTranscript, Is.EqualTo("Привет!"));
                Assert.That(registry.UtterancesAfter(0, new HashSet<int> { 901 })[0].Metadata.Text, Is.EqualTo("Привет."));
                Assert.That(registry.UtterancesAfter(0, new HashSet<int> { 901 })[0].Bytes, Is.Empty,
                    "TTS failure still delivers a subtitle.");
            });
            await Task.Delay(1000);
            Assert.That(providers.Decisions, Is.EqualTo(2), "Completed voice is not read again");

            providers.BlockDecision = true;
            registry.TryEnqueuePlayerText(901, "voice2", "ru", "Подожди", out _);
            await Until(() => providers.Decisions == 3);
            registry.SetViewerPresence("authorized-player", Array.Empty<int>(), false);
            await Until(() => providers.Cancelled);
            Assert.That(providers.Syntheses, Is.EqualTo(1), "Leaving must cancel before a second TTS call.");
        }
        finally
        {
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(registry.HasAttachment(901), Is.False, "off must detach without waiting for TTL");
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }

    private static string FindRoot()
    {
        for (var path = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "SimData/simdata.json"))) return path.FullName;
        throw new DirectoryNotFoundException("HexLive source root");
    }

    private sealed class InProcessMcp(McpTools tools) : HttpMessageHandler
    {
        public volatile bool Unconscious;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            object result;
            if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var text = tools.Call(parameters.GetProperty("name").GetString()!,
                    parameters.GetProperty("arguments"), "mcp:fixture", out var error);
                if (Unconscious && parameters.GetProperty("name").GetString() == "describe_colonist")
                    text = "{\"stateSummary\":\"health=1; unconscious=true\"}";
                result = new { isError = error, content = new[] { new { type = "text", text } } };
            }
            else if (method == "tools/list") result = new { tools = McpTools.Catalog.Select(
                tool => new { name = tool.Name, inputSchema = tool.InputSchema }) };
            else result = new { protocolVersion = "2025-06-18" };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }

    private sealed class CountingProviders : IAgentProviders
    {
        public volatile int Decisions;
        public volatile int Syntheses;
        public volatile bool BlockDecision;
        public volatile bool Cancelled;
        public bool FailOnce;
        public string LastTranscript = "";
        public async Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        {
            LastTranscript = transcript;
            Interlocked.Increment(ref Decisions);
            if (FailOnce)
            {
                FailOnce = false;
                throw new InvalidDataException("synthetic invalid model decision");
            }
            if (BlockDecision)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new CompanionDecision { Speech = "Привет.", Emotion = "warm", Reaction = "Neutral", IntentSummary = "Слушаю голос." };
        }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Syntheses);
            throw new HttpRequestException("synthetic provider outage");
        }
        public void Dispose() { }
    }
}
