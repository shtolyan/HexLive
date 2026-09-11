using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentReplySchedulingTests
{
    [Test]
    public async Task CommittedActionStartsBeforeBlockedSpeechSynthesis()
    {
        await using var f = new Fixture(blockTts: true, replyAction: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.That(f.Provider.Release.Task.IsCompleted, Is.False);
        await Until(() => f.Transport.Actions.Count == 1);
        Assert.That(f.Transport.Actions, Is.EqualTo(new[] { 10 }));
        Assert.That(f.Transport.Delivered, Is.Empty, "The physical order must not wait for TTS.");
        Assert.That(f.Transport.Decisions, Is.EqualTo(new[] { "stale-autonomy" }));
        f.Provider.Release.TrySetResult();
        await Until(() => f.Transport.Delivered.Contains("ambient"));
        Assert.That(f.Transport.Actions, Is.EqualTo(new[] { 10 }), "Finishing speech must not submit the order twice.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReplyProgressesWhileAutonomyModelOrTtsIsBlocked(bool blockTts)
    {
        await using var f = new Fixture(blockTts);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await Until(() => f.Transport.Delivered.Contains("reply-1"));
        Assert.That(f.Provider.Release.Task.IsCompleted, Is.False);
        Assert.That(f.Transport.Delivered.Count(x => x == "reply-1"), Is.EqualTo(1));
        await Until(() => f.Transport.Acknowledged == 1);
        Assert.That(f.Provider.MaxReply, Is.EqualTo(1));
        Assert.That(f.Provider.MaxAutonomy, Is.EqualTo(1));
    }

    [Test]
    public async Task NewQuestionCancelsAutonomyBeforeEnteringTheSingleModelSlot()
    {
        await using var f = new Fixture(blockReply: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await f.Provider.ReplyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.That(f.Provider.Canceled, Is.EqualTo(1));
        Assert.That(f.Provider.ModelConcurrency, Is.All.EqualTo(1));
        f.Provider.Release.TrySetResult();
        await Until(() => f.Transport.Acknowledged == 1);
        Assert.That(f.Transport.Delivered, Is.EqualTo(new[] { "reply-1" }));
        Assert.That(f.Transport.Decisions, Is.EqualTo(new[] { "reply-1" }));
    }

    [Test]
    public async Task FailedAutonomyBackoffDoesNotDelayNewQuestion()
    {
        await using var f = new Fixture(failAutonomy: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await Until(() => f.Transport.ErrorTurns > 0);
        f.Transport.Question = 1;
        await Until(() => f.Transport.Delivered.Contains("reply-1"));
        Assert.That(f.Provider.AutonomyCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task TtsFailureDeliversTextAndReleasesReplySlotForNextQuestion()
    {
        await using var f = new Fixture(failReplyTts: true, startAutonomy: false);
        f.Transport.Question = 1;
        await Until(() => f.Transport.Acknowledged == 1);
        f.Transport.Question = 2;
        await Until(() => f.Transport.Acknowledged == 2);
        Assert.That(f.Transport.Delivered, Is.EqualTo(new[] { "reply-1", "reply-2" }));
        Assert.That(f.Transport.VoiceBytes, Is.All.Zero);
    }

    [Test]
    public async Task LostAcknowledgmentDoesNotRegenerateDeliveredSpeechOrAction()
    {
        await using var f = new Fixture(replyAction: true, startAutonomy: false);
        f.Transport.FailAcknowledgment = true;
        f.Transport.Question = 1;
        await Until(() => f.Transport.Acknowledged == 1);
        Assert.That(f.Provider.ReplyCalls, Is.EqualTo(1));
        Assert.That(f.Transport.Delivered, Is.EqualTo(new[] { "reply-1" }));
        Assert.That(f.Transport.Actions, Is.EqualTo(new[] { 20 }));
        Assert.That(f.Transport.Decisions, Is.EqualTo(new[] { "reply-1" }));
    }

    [Test]
    public async Task LateAutonomyCannotCommitOrActAfterReplyChangesGoal()
    {
        await using var f = new Fixture(ignoreAutonomyCancellation: true, replyAction: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await f.Provider.AutonomyCancellation.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.That(f.Provider.ReplyCalls, Is.Zero, "A non-cooperative provider still occupies the single slot");
        f.Provider.Release.TrySetResult();
        await Until(() => f.Transport.Actions.Count > 0);
        Assert.That(f.Provider.ModelConcurrency, Is.All.EqualTo(1));
        await Task.Delay(250);
        Assert.That(f.Transport.Actions, Is.EqualTo(new[] { 20 }));
        Assert.That(f.Transport.Delivered, Does.Not.Contain("ambient"));
        Assert.That(f.Transport.Decisions, Is.EqualTo(new[] { "reply-1" }));
    }

    [Test]
    public async Task WorldPauseCancelsBothLanesAndRetainsUnconsumedQuestion()
    {
        await using var f = new Fixture(blockReply: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await f.Provider.ReplyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Paused = true;
        await Until(() => f.Provider.Canceled == 2);
        await Task.Delay(150);
        Assert.That(f.Transport.Acknowledged, Is.Zero);
        Assert.That(f.Transport.Delivered, Is.Empty);
        Assert.That(f.Transport.Decisions, Is.Empty);
        Assert.That(f.Transport.Attachments.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task StopCancelsBothInferenceLanesWithoutLateWrites()
    {
        await using var f = new Fixture(blockReply: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await f.Provider.ReplyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Stop.Cancel();
        await f.Running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(f.Provider.Canceled, Is.EqualTo(2));
        Assert.That(f.Transport.Delivered, Is.Empty);
        Assert.That(f.Transport.Decisions, Is.Empty);
        Assert.That(f.Transport.Acknowledged, Is.Zero);
    }

    [Test]
    public async Task ExpiredHeartbeatCancelsBothLanesBeforeSameNpcReattachment()
    {
        await using var f = new Fixture(blockReply: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await f.Provider.ReplyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.ExpireHeartbeat = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (f.Transport.Attachments.Count < 2) await Task.Delay(20, deadline.Token);
        Assert.That(f.Provider.Canceled, Is.EqualTo(2));
        Assert.That(f.Transport.Attachments, Is.All.EqualTo(901));
        Assert.That(f.Transport.Delivered, Is.Empty);
        Assert.That(f.Transport.Decisions, Is.Empty);
        Assert.That(f.Transport.Acknowledged, Is.Zero);
    }

    [Test]
    public async Task IdlePollingRetainsTwoInboxRequestsPerSecond()
    {
        await using var f = new Fixture(startAutonomy: false);
        await Until(() => f.Transport.InboxReads > 0);
        var before = f.Transport.InboxReads;
        await Task.Delay(2100);
        Assert.That(f.Transport.InboxReads - before, Is.InRange(3, 5));
        Assert.That(f.Provider.AutonomyCalls, Is.Zero);
        Assert.That(f.Provider.ReplyCalls, Is.Zero);
    }

    [Test]
    public async Task ReplyObjectiveWithoutActionFencesLateAutonomyBeforeDurableCommit()
    {
        await using var f = new Fixture(ignoreAutonomyCancellation: true, replyObjective: true);
        await f.Provider.AutonomyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Transport.Question = 1;
        await f.Provider.AutonomyCancellation.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Provider.Release.TrySetResult();
        await Until(() => f.Transport.Acknowledged == 1);
        Assert.That(f.Provider.ModelConcurrency, Is.All.EqualTo(1));
        await Task.Delay(250);
        Assert.That(f.Transport.Actions, Is.Empty);
        Assert.That(f.Transport.Decisions, Is.EqualTo(new[] { "reply-1" }));
        var archive = await f.Memory.SnapshotAsync(default);
        Assert.That(archive.Objective!.Text, Is.EqualTo("Rescue friend"));
        Assert.That(archive.Objective.Revision, Is.EqualTo(1));
        Assert.That(archive.Worlds.SelectMany(w => w.Journal).Any(j => j.Text == "stale-autonomy"), Is.False);
    }

    [Test]
    public async Task StaleReplyObjectiveRetainsSameQuestionUntilFreshSnapshotRetry()
    {
        await using var f = new Fixture(blockReply: true, startAutonomy: false, replyObjective: true);
        f.Transport.Question = 1;
        await f.Provider.ReplyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var archive = await f.Memory.SnapshotAsync(default);
        var episode = archive.Worlds.Single();
        await f.Memory.CommitTurnAsync(new(episode.Id, episode.WorldKey, episode.LastTick, -1)
            { ObjectiveRevision = 0 }, "concurrent-goal", "heartbeat", new()
            { ObjectiveUpdate = new() { Operation = "set", Text = "Find water", Reason = "Immediate survival" } }, default);
        f.Provider.Release.TrySetResult();
        await Task.Delay(500);
        Assert.That(f.Transport.Acknowledged, Is.Zero);
        Assert.That(f.Transport.Delivered, Is.Empty);
        Assert.That(f.Transport.Decisions, Is.Empty);
        await Until(() => f.Transport.Acknowledged == 1, 9);
        Assert.That(f.Provider.ReplyCalls, Is.EqualTo(2));
        Assert.That(f.Transport.Delivered, Is.EqualTo(new[] { "reply-1" }));
        Assert.That((await f.Memory.SnapshotAsync(default)).Objective!.Revision, Is.EqualTo(2));
    }

    private static async Task Until(Func<bool> predicate, int seconds = 4)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("bug387-").FullName;
        public readonly CancellationTokenSource Stop = new();
        public readonly Provider Provider;
        public readonly Transport Transport;
        public readonly Task Running;
        public readonly MashaMemoryStore Memory;
        public Fixture(bool blockTts = false, bool failAutonomy = false, bool failReplyTts = false,
            bool ignoreAutonomyCancellation = false, bool replyAction = false, bool blockReply = false,
            bool startAutonomy = true, bool replyObjective = false)
        {
            Provider = new(blockTts, failAutonomy, failReplyTts, ignoreAutonomyCancellation, replyAction, blockReply, replyObjective);
            Transport = new(startAutonomy);
            var options = new AgentHostOptions
            {
                McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture-only", ProfileId = "fixture",
                DisplayName = "fixture", WorldId = "fixture", ExpectedWorldId = "fixture", NpcId = 901,
                XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture",
                ElevenLabsVoiceId = "fixture", FakeProviders = true,
                MemoryDirectory = Path.Combine(_path, "memory"), StateDirectory = Path.Combine(_path, "state"),
            };
            var runtime = new AgentHostRuntime(options, Provider, Transport);
            Memory = (MashaMemoryStore)typeof(AgentHostRuntime).GetField("_memory",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!;
            Running = runtime.RunAsync(Stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel(); Provider.Release.TrySetResult(); Transport.ReleaseEvents.TrySetResult();
            await Running.WaitAsync(TimeSpan.FromSeconds(3));
            Transport.Dispose(); Stop.Dispose(); Directory.Delete(_path, true);
        }
    }

    private sealed class Provider(bool blockTts, bool failAutonomy, bool failReplyTts,
        bool ignoreAutonomyCancellation, bool replyAction, bool blockReply, bool replyObjective) : IAgentProviders
    {
        public readonly TaskCompletionSource AutonomyBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReplyBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AutonomyCalls, ReplyCalls, Canceled, MaxReply, MaxAutonomy;
        public readonly TaskCompletionSource AutonomyCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<int> ModelConcurrency = new();
        private int _activeModels;
        private int _replyActive, _autonomyActive;
        public async Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        {
            var reply = trigger == "voice";
            ModelConcurrency.Enqueue(Interlocked.Increment(ref _activeModels));
            using var cancellation = cancellationToken.Register(() => { if (!reply) AutonomyCancellation.TrySetResult(); });
            if (reply) { Interlocked.Increment(ref ReplyCalls); MaxReply = Math.Max(MaxReply, Interlocked.Increment(ref _replyActive)); }
            else { Interlocked.Increment(ref AutonomyCalls); MaxAutonomy = Math.Max(MaxAutonomy, Interlocked.Increment(ref _autonomyActive)); }
            try
            {
                if (!reply && !blockTts)
                {
                    AutonomyBlocked.TrySetResult();
                    if (failAutonomy) throw new HttpRequestException("synthetic model failure");
                    await Release.Task.WaitAsync(ignoreAutonomyCancellation ? CancellationToken.None : cancellationToken);
                }
                if (reply && blockReply) { ReplyBlocked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
                return new CompanionDecision
                {
                    Speech = reply ? transcript : "ambient", IntentSummary = reply ? transcript : "stale-autonomy",
                    Action = replyAction || (!reply && replyObjective) ? new CompanionAction { Tool = "move_to", Arguments = JsonSerializer.SerializeToElement(new { x = reply ? 20 : 10, y = 10 }) } : null,
                    ObjectiveUpdate = reply && replyObjective ? new() { Operation = "set", Text = "Rescue friend", Reason = "New player request" } : null,
                };
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            finally { Interlocked.Decrement(ref _activeModels); if (reply) Interlocked.Decrement(ref _replyActive); else Interlocked.Decrement(ref _autonomyActive); }
        }
        public async Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            if (text == "ambient" && blockTts) { AutonomyBlocked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            if (text.StartsWith("reply-") && failReplyTts) throw new HttpRequestException("synthetic TTS failure");
            return new VoiceArtifact([]);
        }
        public void Dispose() { }
    }

    private sealed class Transport(bool startAutonomy) : HttpMessageHandler
    {
        public volatile int Question, Acknowledged;
        public volatile bool ExpireHeartbeat;
        public volatile bool Paused;
        public volatile bool FailAcknowledgment;
        public volatile bool HoldEvents;
        public readonly TaskCompletionSource EventsHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseEvents = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InboxReads, ErrorTurns;
        private int _events;
        private string _pending = "";
        public readonly ConcurrentQueue<string> Delivered = new(), Decisions = new();
        public readonly ConcurrentQueue<int> Actions = new(), VoiceBytes = new();
        public readonly ConcurrentQueue<int> Attachments = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            object result = new { protocolVersion = "2025-06-18" };
            if (method == "tools/list") result = new { tools = HexLive.Server.Mcp.McpTools.Catalog.Select(t => new { name = t.Name, inputSchema = t.InputSchema }) };
            if (method == "tools/call")
            {
                var p = root.GetProperty("params"); var name = p.GetProperty("name").GetString();
                var a = p.GetProperty("arguments");
                if (name == "read_events" && HoldEvents) { HoldEvents = false; EventsHeld.TrySetResult(); await ReleaseEvents.Task.WaitAsync(token); }
                if (name == "world_status" && ExpireHeartbeat) { ExpireHeartbeat = false; return new(HttpStatusCode.Unauthorized); }
                if (name == "attach_agent") Attachments.Enqueue(a.GetProperty("npcId").GetInt32());
                if (name == "read_agent_inbox") Interlocked.Increment(ref InboxReads);
                if (name == "ack_agent_inbox")
                {
                    if (FailAcknowledgment) { FailAcknowledgment = false; throw new HttpRequestException("synthetic lost acknowledgment"); }
                    Acknowledged = a.GetProperty("throughSeq").GetInt32();
                }
                if (name == "begin_agent_utterance") { _pending = a.GetProperty("text").GetString()!; VoiceBytes.Enqueue(a.GetProperty("totalBytes").GetInt32()); }
                if (name == "commit_agent_utterance") Delivered.Enqueue(_pending);
                if (name == "move_to") Actions.Enqueue(a.GetProperty("x").GetInt32());
                if (name == "commit_agent_turn")
                {
                    var id = a.GetProperty("turnId").GetString()!;
                    if (id.StartsWith("error-")) Interlocked.Increment(ref ErrorTurns);
                    else if (!id.StartsWith("attach-")) Decisions.Enqueue(a.GetProperty("intentSummary").GetString()!);
                }
                var question = Question;
                object payload = name switch
                {
                    "world_status" => new { worldId = "fixture", tick = 100, seed = 12345, paused = Paused },
                    "list_colonists" => new { colonists = new[] { new { npcId = 901, health = 1f, planStatus = "Completed" } } },
                    "attach_agent" => new { attachmentId = "attach-fixture", playerPresent = true, eventWatermark = 0L },
                    "agent_heartbeat" => new { playerPresent = true },
                    "describe_colonist" => new { stateSummary = "health=1; unconscious=false" },
                    "read_agent_inbox" => new { watermark = (long)question, messages = question > Acknowledged ? new[] { new { seq = (long)question, messageId = "message-" + question, text = "reply-" + question, senderId = "player", language = "en", createdUtc = DateTimeOffset.UtcNow } } : [] },
                    "read_events" => new { watermark = 1L, events = startAutonomy && Interlocked.Increment(ref _events) == 1 ? new[] { new { seq = 1L, tick = 100L, type = "Threat", message = "Threat nearby" } } : [] },
                    _ => new { accepted = true, status = "Completed" },
                };
                result = new { isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "session-fixture"); return response;
        }
    }
}
