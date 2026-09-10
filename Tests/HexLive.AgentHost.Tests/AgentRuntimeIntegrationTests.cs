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
    public async Task WorldPauseControlsPaidWorkNotPlayerPresenceAndOffDetaches()
    {
        var root = FindRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "agent-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        using var stop = new CancellationTokenSource();
        using var host = new WorldHost(12345, GameMode.Feud, Path.Combine(temporary, "world.sav"),
            Path.Combine(root, "SimData/simdata.json"), false, companionProfile: "masha");
        host.EnableMcpEventLog();
        host.PauseAsOperator();
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
            // Cross the entire model-heartbeat interval with the world paused.
            await Task.Delay(TimeSpan.FromSeconds(31));
            Assert.Multiple(() =>
            {
                Assert.That(providers.Decisions, Is.Zero);
                Assert.That(providers.Syntheses, Is.Zero);
                Assert.That(registry.StatesFor(new[] { 901 })[0].Phase, Is.EqualTo(AgentPhase.Sleeping));
                Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.True,
                    "Attachment keeps effective manual control while the world is paused.");
                Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ExternalControl?.IsActive), Is.True);
            });

            host.ResumeAsOperator();
            // No player presence is needed for an explicitly started agent.
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
            handler.SyntheticHistory = true;
            var eventsBefore = handler.EventReads;
            await Until(() => handler.EventReads >= eventsBefore + 2);
            Assert.That(providers.Decisions, Is.EqualTo(3), "archive polling continues while the provider is blocked");
            var archive = new AgentMemorySearch(options.MemoryDirectory); archive.Refresh();
            // The scheduler also reads events. Its two polls do not prove the
            // archive writer completed epoch reconciliation and indexed the batch.
            await Until(() =>
            {
                archive.Refresh();
                return archive.Search("ARCHIVE_EVENT_FIXTURE").Hits.Any(h => h.Record.Source.StartsWith("server-ring:"));
            });
            Assert.That(archive.Search("Подожди").Hits.Any(h => h.Record.Kind == "player"), Is.True);
            Assert.That(archive.Search("ARCHIVE_EVENT_FIXTURE").Hits.Count(h => h.Record.Source.StartsWith("server-ring:")), Is.EqualTo(1), "Repeated batches are idempotent");
            Assert.That(archive.Search("",new MemoryFilter(Kind:"gap")).Hits,Is.Not.Empty,"Epoch switch creates an explicit gap");
            host.PauseAsOperator();
            await Until(() => providers.Cancelled);
            Assert.That(providers.Syntheses, Is.EqualTo(1), "Pausing must cancel before a second TTS call.");
        }
        finally
        {
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(registry.HasAttachment(901), Is.False, "off must detach without waiting for TTL");
            Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ExternalControl?.IsActive == true), Is.False,
                "Off removes the transient attachment control token.");
            Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.True,
                "Off preserves the player manual switch explicitly enabled before attachment.");
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    [Test]
    public async Task LostInboxAcknowledgmentsDoNotRepeatTurnsOrSkipInterleavedSpeakers()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "agent-inbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        using var stop = new CancellationTokenSource();
        using var host = new WorldHost(12345, GameMode.Feud, Path.Combine(temporary, "world.sav"),
            Path.Combine(FindRoot(), "SimData/simdata.json"), false, companionProfile: "masha");
        host.EnableMcpEventLog();
        host.PauseAsOperator();
        var registry = new AgentSessionRegistry();
        var tools = new McpTools(() => host, () => 1, new ControlLeases(45), registry, SpecLibrary.Discover(null));
        using var handler = new InProcessMcp(tools) { LoseAcknowledgments = true };
        var providers = new CountingProviders();
        var runtime = new AgentHostRuntime(new AgentHostOptions
        {
            McpUri = new Uri("http://localhost/mcp"), McpToken = "test-only", ProfileId = "masha",
            DisplayName = "Test", MemoryDirectory = Path.Combine(temporary, "memory"),
            StateDirectory = Path.Combine(temporary, "state"), WorldId = "fixture", FakeProviders = true,
            XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture"
        }, providers, handler);
        var running = runtime.RunAsync(stop.Token);
        try
        {
            await Until(() => registry.HasAttachment(901));
            var a = "11111111111111111111111111111111";
            var b = "22222222222222222222222222222222";
            registry.TryEnqueuePlayerText(901, "a1", "ru", new string('я', 3000), out _, a);
            registry.TryEnqueuePlayerText(901, "b1", "ru", "B1", out _, b);
            registry.TryEnqueuePlayerText(901, "a2", "ru", "A2", out _, a);
            var attachment = registry.StatesFor(new[] { 901 })[0].AttachmentId;
            host.ResumeAsOperator();
            await Until(() => handler.AckCalls >= 3);
            await Task.Delay(100);
            Assert.That(providers.Transcripts.ToArray(), Is.EqualTo(new[] { new string('я', 3000), "B1", "A2" }));
            Assert.That(providers.Decisions, Is.EqualTo(3));
            Assert.That(registry.TryReadInbox(attachment, "mcp:fixture", 1, 0, 16, out var inbox, out _), Is.True);
            Assert.That(inbox.Messages, Is.Empty);
            // The accepted-response retry uses the same sender/message ID after processing.
            registry.TryEnqueuePlayerText(901, "a1", "ru", "repeat", out _, a);
            registry.TryReadInbox(attachment, "mcp:fixture", 1, 0, 16, out inbox, out _);
            Assert.That(inbox.Messages, Is.Empty);
        }
        finally
        {
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(20));
            Directory.Delete(temporary, true);
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
        public bool LoseAcknowledgments;
        public volatile int AckCalls;
        public volatile int EventReads;
        public volatile bool SyntheticHistory;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            object result;
            if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                if (parameters.GetProperty("name").GetString() == "read_events") Interlocked.Increment(ref EventReads);
                var isAck = parameters.GetProperty("name").GetString() == "ack_agent_inbox";
                var ack = isAck ? Interlocked.Increment(ref AckCalls) : 0;
                if (LoseAcknowledgments && ack == 1) throw new HttpRequestException("lost request");
                var text = tools.Call(parameters.GetProperty("name").GetString()!,
                    parameters.GetProperty("arguments"), "mcp:fixture", out var error);
                if (LoseAcknowledgments && ack == 2) throw new HttpRequestException("lost response");
                if (SyntheticHistory && parameters.GetProperty("name").GetString() == "read_events")
                    text = """
                    {"events":[{"seq":7,"tick":10,"type":"Aided","message":"ARCHIVE_EVENT_FIXTURE NPC901 helped NPC902"}],
                    "watermark":7,"sessionEpoch":"fixture-new-epoch","gap":false,"truncated":false}
                    """;
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
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Transcripts = new();
        public async Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        {
            LastTranscript = transcript;
            Transcripts.Enqueue(transcript);
            Interlocked.Increment(ref Decisions);
            if (FailOnce)
            {
                FailOnce = false;
                return AgentProviders.ParseDecision("{\"speech\":\"broken", trigger);
            }
            if (BlockDecision)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return AgentProviders.ParseDecision("""
                {"speech":"Привет.","emotion":"warm","action":null,"reaction":"Neutral",
                "relationshipAssessment":{"learnedSomethingSignificant":false,"trust":"Unchanged",
                "sympathy":"Unchanged","seriousHarm":false,"reason":"Обычное приветствие.","voiceName":null,"namingReason":null},
                "intentSummary":"Слушаю голос.","memoryUpserts":[],"journalText":""}
                """, trigger);
        }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Syntheses);
            throw new HttpRequestException("synthetic provider outage");
        }
        public void Dispose() { }
    }
}
