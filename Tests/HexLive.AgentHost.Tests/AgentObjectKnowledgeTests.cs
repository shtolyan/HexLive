using System.Net;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentObjectKnowledgeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task QueryFeedsFinalDecisionWithoutSpeakingProvisionalTextOrReplacingWalking()
    {
        await using var fixture = new Fixture();
        fixture.Providers.Decisions.Enqueue(new CompanionDecision { Action = new CompanionAction
            { Tool = "move_to", Arguments = JsonSerializer.SerializeToElement(new { x = 10, y = 10 }) } });
        Assert.That(await fixture.Turn(), Is.True);
        fixture.Providers.Decisions.Enqueue(Query());
        fixture.Providers.Decisions.Enqueue(new CompanionDecision { IntentSummary = "final-query-informed-intent" });
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(fixture.Transport.Queries, Is.EqualTo(1));
        Assert.That(fixture.Transport.QueryNpc, Is.EqualTo(901), "Attachment actor overrides forged model npcId");
        Assert.That(fixture.Providers.States.Last(), Does.Contain("knownObjectQuery").And.Contain("food.coconut"));
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "move_to" }));
        Assert.That(fixture.Transport.Acquires, Is.EqualTo(1));
        Assert.That(fixture.Transport.Releases, Is.Zero);
        Assert.That(fixture.Transport.CommitBodies.All(text => !text.Contains("PROVISIONAL-SENTINEL")), Is.True);
        Assert.That(fixture.MemoryFiles().All(text => !text.Contains("PROVISIONAL-SENTINEL")), Is.True);
        Assert.That(fixture.Transport.CommitBodies.Last(), Does.Contain("final-query-informed-intent"));
    }

    [Test]
    public async Task QueryPreservesReferencesAndResolvesSubsequentReadRequestsBeforeCommit()
    {
        await using var fixture = new Fixture();
        fixture.Providers.Decisions.Enqueue(new CompanionDecision { MemoryRequests =
            [new() { Operation = "skills.read", Arguments = JsonSerializer.SerializeToElement(new { id = "build-bed" }) }] });
        fixture.Providers.Decisions.Enqueue(Query());
        fixture.Providers.Decisions.Enqueue(new CompanionDecision { MemoryRequests =
            [new() { Operation = "skills.read", Arguments = JsonSerializer.SerializeToElement(new { id = "recover-and-resume" }) }] });
        fixture.Providers.Decisions.Enqueue(new CompanionDecision { IntentSummary = "grounded-final" });
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(fixture.Transport.Queries, Is.EqualTo(1));
        Assert.That(fixture.Providers.Contexts[2], Does.Contain("skill:build-bed:"));
        Assert.That(fixture.Providers.Contexts.Last(), Does.Contain("skill:recover-and-resume:"));
        Assert.That(fixture.Providers.States.Last(), Does.Contain("food.coconut"));
        Assert.That(fixture.Transport.CommitBodies, Has.Count.EqualTo(1));
        Assert.That(fixture.Transport.CommitBodies[0], Does.Contain("grounded-final"));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
    }

    [Test]
    public async Task RepeatedQueryIsBoundedToOneReadAndCannotBecomePhysicalAction()
    {
        await using var fixture = new Fixture();
        fixture.Providers.Decisions.Enqueue(Query());
        fixture.Providers.Decisions.Enqueue(Query());
        Assert.That(await fixture.Turn(), Is.False);
        Assert.That(fixture.Transport.Queries, Is.EqualTo(1));
        Assert.That(fixture.Providers.States.Count, Is.EqualTo(2));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
        Assert.That(fixture.Transport.Commands, Is.Empty);
        Assert.That(fixture.MemoryFiles().All(text => !text.Contains("PROVISIONAL-SENTINEL")), Is.True);
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task QueryAuthorizationErrorsEscapeTurnInsteadOfBecomingOrdinaryReadData(HttpStatusCode status)
    {
        await using var fixture = new Fixture();
        fixture.Transport.QueryStatus = status;
        fixture.Providers.Decisions.Enqueue(Query());
        var error = Assert.CatchAsync(async () => await fixture.Turn());
        Assert.That(error, status == HttpStatusCode.Unauthorized
            ? Is.TypeOf<McpSessionExpiredException>() : Is.TypeOf<HttpRequestException>());
        Assert.That(fixture.Providers.States.Count, Is.EqualTo(1));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
        Assert.That(fixture.Transport.CommitBodies, Is.Empty);
    }

    [Test]
    public async Task Query403IsTerminalAtRuntimeBoundaryWithoutAnErrorRetryLoop()
    {
        await using var fixture = new Fixture();
        fixture.Transport.QueryStatus = HttpStatusCode.Forbidden;
        fixture.Providers.Fallback = Query;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Runtime.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(9));
        Assert.That(fixture.Runtime.TerminalErrorCode, Is.EqualTo(nameof(HttpRequestException)));
        Assert.That(fixture.Transport.Queries, Is.EqualTo(1));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
        Assert.That(fixture.Transport.Commands, Is.Empty);
    }

    [Test]
    public async Task CancelledQueryCannotPublishOrAcquireControl()
    {
        await using var fixture = new Fixture();
        fixture.Transport.WaitInQuery = true;
        fixture.Providers.Decisions.Enqueue(Query());
        using var stop = new CancellationTokenSource();
        var task = fixture.Turn(stop.Token);
        await fixture.Transport.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await task);
        Assert.That(fixture.Providers.States.Count, Is.EqualTo(1));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
        Assert.That(fixture.Transport.CommitBodies, Is.Empty);
    }

    [Test]
    public async Task WrongWorldResultCannotReachASecondModelRequest()
    {
        await using var fixture = new Fixture();
        fixture.Transport.QueryWorldId = "changed-world";
        fixture.Providers.Decisions.Enqueue(Query());
        Assert.ThrowsAsync<AgentTargetChangedException>(async () => await fixture.Turn());
        Assert.That(fixture.Providers.States.Count, Is.EqualTo(1));
        Assert.That(fixture.Transport.CommitBodies, Is.Empty);
        Assert.That(fixture.Transport.Acquires, Is.Zero);
    }

    [TestCase(QueryFailure.Timeout, "KnowledgeReadTimeout")]
    [TestCase(QueryFailure.Unavailable, "KnowledgeReadUnavailable")]
    [TestCase(QueryFailure.TooManyRows, "KnowledgeReadUnavailable")]
    [TestCase(QueryFailure.TooManyBytes, "KnowledgeReadUnavailable")]
    public async Task FailedOrOversizedQueryMakesOneSanitizedFollowupWithoutControlWrites(
        QueryFailure failure, string expectedCode)
    {
        await using var fixture = new Fixture();
        fixture.Transport.Failure = failure;
        fixture.Providers.Decisions.Enqueue(Query());
        fixture.Providers.Decisions.Enqueue(new CompanionDecision { IntentSummary = "final-with-unavailable-knowledge" });
        Assert.That(await fixture.Turn(), Is.True);
        Assert.That(fixture.Transport.Queries, Is.EqualTo(1));
        Assert.That(fixture.Providers.States.Count, Is.EqualTo(2));
        using var state = JsonDocument.Parse(fixture.Providers.States.Last());
        var answer = state.RootElement.GetProperty("knownObjectQuery");
        Assert.That(answer.GetProperty("error").GetString(), Is.EqualTo(expectedCode));
        Assert.That(fixture.Providers.States.Last(), Does.Not.Contain("PRIVATE-QUERY-ERROR"));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
        Assert.That(fixture.Transport.Releases, Is.Zero);
        Assert.That(fixture.Transport.Commands, Is.Empty);
        Assert.That(fixture.Transport.CommitBodies.All(text => !text.Contains("PROVISIONAL-SENTINEL")), Is.True);
        Assert.That(fixture.MemoryFiles().All(text => !text.Contains("PROVISIONAL-SENTINEL")), Is.True);
        if (failure == QueryFailure.TooManyBytes)
        {
            Assert.That(fixture.Transport.QueryPayloadChars, Is.LessThan(65536), "Must exercise bytes, not character count");
            Assert.That(fixture.Transport.QueryPayloadBytes, Is.GreaterThan(65536));
        }
    }

    [TestCase("_attachmentVersion")]
    [TestCase("_controlVersion")]
    public async Task QueryCompletedAfterReattachOrControlHandoffCannotReachModelOrCommit(string versionField)
    {
        await using var fixture = new Fixture();
        fixture.Transport.WaitInQuery = true;
        fixture.Providers.Decisions.Enqueue(Query());
        using var stop = new CancellationTokenSource();
        var task = fixture.ScheduledTurn(stop);
        await fixture.Transport.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.AdvanceVersion(versionField);
        fixture.Transport.QueryRelease.TrySetResult();
        Assert.CatchAsync<OperationCanceledException>(async () => await task);
        Assert.That(stop.IsCancellationRequested, Is.False, "Version fence must work independently of cancellation");
        Assert.That(fixture.Providers.States.Count, Is.EqualTo(1));
        Assert.That(fixture.Transport.Queries, Is.EqualTo(1));
        Assert.That(fixture.Transport.Acquires, Is.Zero);
        Assert.That(fixture.Transport.Releases, Is.Zero);
        Assert.That(fixture.Transport.Commands, Is.Empty);
        Assert.That(fixture.Transport.CommitBodies, Is.Empty);
        Assert.That(fixture.MemoryFiles().All(text => !text.Contains("PROVISIONAL-SENTINEL")), Is.True);
    }

    public enum QueryFailure { None, Timeout, Unavailable, TooManyRows, TooManyBytes }

    private static CompanionDecision Query() => new()
    {
        Speech = "PROVISIONAL-SENTINEL", IntentSummary = "PROVISIONAL-SENTINEL",
        MemoryUpserts = [new() { Key = "PROVISIONAL-SENTINEL", Value = "must-not-persist", Importance = 1 }],
        Action = new CompanionAction { Tool = "query_known_objects", Arguments = JsonSerializer.SerializeToElement(
            new { npcId = 999, definitionPrefix = "food.coconut", limit = 16 }) }
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("agent-object-query-").FullName;
        public readonly Transport Transport = new();
        public readonly Providers Providers = new();
        public AgentHostRuntime Runtime { get; }
        private McpClient Mcp { get; }
        public Fixture()
        {
            var options = new AgentHostOptions
            {
                McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture", ProfileId = "masha",
                DisplayName = "fixture", WorldId = "fixture", XaiKey = "", ElevenLabsKey = "",
                XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
                MemoryDirectory = Path.Combine(_directory, "memory"), StateDirectory = Path.Combine(_directory, "state"),
                FakeProviders = true, HeartbeatSeconds = 5, NpcId = 901
            };
            Runtime = new AgentHostRuntime(options, Providers, Transport);
            Mcp = new McpClient(options.ProviderOptions, Transport);
        }
        public Task<bool> Turn(CancellationToken token = default) =>
            (Task<bool>)typeof(AgentHostRuntime).GetMethod("ProcessSafelyAsync", Private)!.Invoke(Runtime,
                new object?[] { Mcp, "attachment", 901, "voice", "fixture", token, token, null, "",
                    new[] { Guid.NewGuid().ToString("N") } })!;
        public Task<bool> ScheduledTurn(CancellationTokenSource stop)
        {
            var type = typeof(AgentHostRuntime).GetNestedType("ScheduledTurn", BindingFlags.NonPublic)!;
            var turn = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetField("IsReply")!.SetValue(turn, false);
            type.GetField("AttachmentVersion")!.SetValue(turn, 0L);
            type.GetField("ControlVersion")!.SetValue(turn, 0L);
            type.GetField("InboxWatermark")!.SetValue(turn, 0L);
            type.GetField("TurnId")!.SetValue(turn, "query-fence");
            type.GetField("Stop")!.SetValue(turn, stop);
            return (Task<bool>)typeof(AgentHostRuntime).GetMethod("ProcessLaneAsync", Private)!.Invoke(Runtime,
                new object?[] { Mcp, "attachment", 901, "heartbeat", "", stop.Token, stop.Token,
                    "query-fence", "", Array.Empty<string>(), turn })!;
        }
        public void AdvanceVersion(string name) => typeof(AgentHostRuntime).GetField(name, Private)!.SetValue(Runtime, 1L);
        public string[] MemoryFiles() => Directory.Exists(Path.Combine(_directory, "memory"))
            ? Directory.GetFiles(Path.Combine(_directory, "memory"), "*.json", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray()
            : [];
        public async ValueTask DisposeAsync()
        {
            await (Task)typeof(AgentHostRuntime).GetMethod("StopActionAsync", Private)!.Invoke(Runtime, new object[] { false })!;
            Mcp.Dispose(); Transport.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Providers : IAgentProviders
    {
        public readonly Queue<CompanionDecision> Decisions = new();
        public readonly List<string> States = new();
        public readonly List<string> Contexts = new();
        public Func<CompanionDecision> Fallback = () => new CompanionDecision();
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string context,
            string transcript, IReadOnlyList<string> recent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            States.Add(stateJson);
            Contexts.Add(context);
            return Task.FromResult(Decisions.Count > 0 ? Decisions.Dequeue() : Fallback());
        }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken token) =>
            throw new AssertionException("Provisional speech must not reach TTS");
        public void Dispose() { }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public readonly List<string> Commands = new();
        public readonly List<string> CommitBodies = new();
        public readonly TaskCompletionSource QueryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource QueryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public QueryFailure Failure;
        public HttpStatusCode QueryStatus = HttpStatusCode.OK;
        public string QueryWorldId = "fixture";
        public bool WaitInQuery;
        public int Queries, QueryNpc, Acquires, Releases;
        public int QueryPayloadChars, QueryPayloadBytes;
        public bool Active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = doc.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            var method = root.GetProperty("method").GetString();
            if (method == "tools/list") result = new { tools = HexLive.Server.Mcp.McpTools.Catalog.Select(t => new
                { name = t.Name, inputSchema = t.InputSchema }) };
            if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                var arguments = parameters.GetProperty("arguments");
                if (name == "query_known_objects")
                {
                    Queries++; QueryNpc = arguments.GetProperty("npcId").GetInt32(); QueryEntered.TrySetResult();
                    if (WaitInQuery) await QueryRelease.Task.WaitAsync(token);
                    if (Failure == QueryFailure.Timeout) throw new TaskCanceledException("PRIVATE-QUERY-ERROR");
                    if (Failure == QueryFailure.Unavailable) return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        { Content = new StringContent("PRIVATE-QUERY-ERROR") };
                    if (QueryStatus != HttpStatusCode.OK) return new HttpResponseMessage(QueryStatus);
                }
                if (name == "acquire_npc_control") Acquires++;
                if (name is "move_to" or "interact" or "stop") { Commands.Add(name); Active = true; }
                if (name == "release_control") { Releases++; Active = false; }
                if (name == "commit_agent_turn") CommitBodies.Add(arguments.GetRawText());
                object payload = name switch
                {
                    "world_status" => new { worldId = "fixture", tick = 100, seed = 388, paused = false },
                    "list_colonists" => new { colonists = new[] { new { npcId = 901, health = 1f, planStatus = Active ? "Active" : "Completed" } } },
                    "attach_agent" => new { attachmentId = "attachment", playerPresent = false, eventWatermark = 0L },
                    "describe_colonist" => new { npcId = 901, stateSummary = "health=1; unconscious=false" },
                    "read_agent_inbox" => new { watermark = 0L, messages = Array.Empty<object>() },
                    "read_events" => new { watermark = 0L, events = Array.Empty<object>() },
                    "query_known_objects" => new { npcId = 901, worldId = QueryWorldId, tick = 100, source = "personalMemory",
                        objects = Enumerable.Range(0, Failure == QueryFailure.TooManyRows ? 65 : 1).Select(i =>
                            new { objectId = 7 + i, definitionId = "food.coconut", lastSeenTick = 90,
                                ageTicks = 10, provenance = "observedMemory", lastKnownTile = new { q = 3, r = 4 } }).ToArray(),
                        totalMatches = 1, truncated = false,
                        padding = Failure == QueryFailure.TooManyBytes ? new string('ж', 40000) : "" },
                    _ => new { accepted = true }
                };
                var payloadText = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                if (name == "query_known_objects")
                {
                    QueryPayloadChars = payloadText.Length;
                    QueryPayloadBytes = System.Text.Encoding.UTF8.GetByteCount(payloadText);
                }
                result = new { isError = false, content = new[] { new { type = "text", text = payloadText } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }
}
