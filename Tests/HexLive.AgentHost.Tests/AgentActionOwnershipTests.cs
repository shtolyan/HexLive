using System.Net;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentActionOwnershipTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task AutonomousHeartbeatCannotReplaceRouteButDialogueAndCriticalActionsCan()
    {
        await using var fixture = new Fixture();
        await fixture.Turn("voice", "move_to");
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "move_to" }));
        await fixture.Turn("heartbeat", "interact");
        Assert.That(fixture.Providers.Decisions, Is.EqualTo(1), "Do not pay for an ordinary replacement while a route is owned");
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "move_to" }), "Leaves cannot replace the accepted route");
        await fixture.Turn("voice", null);
        Assert.That(fixture.Providers.Decisions, Is.EqualTo(2), "Dialogue remains available while walking");
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "move_to" }));
        await fixture.Turn("voice", "self_action");
        await fixture.Turn("critical", "attack_mob");
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "move_to", "self_action", "attack_mob" }));
        Assert.That(fixture.Transport.Releases, Is.Zero, "Explicit replacements hand off the lease without returning to ordinary AI");
        fixture.Transport.Active = false;
        await fixture.ActionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Transport.Releases, Is.EqualTo(1));
        await fixture.Turn("heartbeat", "interact");
        Assert.That(fixture.Transport.Commands.Last(), Is.EqualTo("interact"), "Ordinary decisions resume once the route ends");
    }

    [Test]
    public async Task CompletedPickupAllowsHeartbeatContinuationWithoutDroppingThePatient()
    {
        await using var fixture = new Fixture();
        await fixture.Turn("voice", "carry_person");
        fixture.Transport.Active = false;
        await Until(() => (bool)typeof(AgentHostRuntime).GetField("_actionAwaitingContinuation", Private)!.GetValue(fixture.Runtime)!);
        await fixture.Turn("heartbeat", "move_to");
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "carry_person", "move_to" }));
        Assert.That(fixture.Transport.Carried, Is.True);
        Assert.That(fixture.Transport.Releases, Is.Zero);
        await fixture.Stop();
        Assert.That(fixture.Transport.Releases, Is.EqualTo(1));
        Assert.That(fixture.Transport.Carried, Is.False);
    }

    [TestCase("{}", "MissingRequiredArgument")]
    [TestCase("{\"kind\":\"private-secret\"}", "InvalidArgumentEnum")]
    public async Task InvalidReplacementDoesNotAcquireOrReleaseAndFeedbackReachesTheNextTurn(string arguments, string code)
    {
        await using var fixture = new Fixture();
        await fixture.Turn("voice", "move_to");
        var acquired = fixture.Transport.Acquires;
        fixture.Providers.Arguments = JsonSerializer.Deserialize<JsonElement>(arguments);
        await fixture.Turn("voice", "self_action");
        Assert.That(fixture.Transport.Acquires, Is.EqualTo(acquired));
        Assert.That(fixture.Transport.Releases, Is.Zero);
        Assert.That(fixture.Transport.Commands, Is.EqualTo(new[] { "move_to" }));
        await fixture.Turn("voice", null);
        Assert.That(fixture.Providers.Contexts.Last(), Does.Contain("result=" + code));
        Assert.That(fixture.Providers.Contexts.Last(), Does.Match("tool=self_action turn=[a-f0-9]{16}"));
    }

    [Test]
    public async Task RejectedActionFeedbackContainsOnlyTheSafeReasonAndItsCorrelation()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Reject = true;
        await fixture.Turn("voice", "self_action");
        await fixture.ActionTask;
        Assert.That(fixture.Transport.Releases, Is.EqualTo(1));
        await fixture.Turn("voice", null);
        Assert.That(fixture.Providers.Contexts.Last(), Does.Contain("result=NoSupplies"));
        Assert.That(fixture.Providers.Contexts.Last(), Does.Match("tool=self_action turn=[a-f0-9]{16}"));
        Assert.That(fixture.Providers.Contexts.Last(), Does.Not.Contain("private-secret"));
    }

    [TestCase("Completed", "PlanCompleted")]
    [TestCase("Failed", "PlanFailed")]
    [TestCase("Invalid", "PlanInvalid")]
    public async Task CompletionReportsTheActualPlanOutcome(string status, string code)
    {
        await using var fixture = new Fixture();
        fixture.Transport.PlanEndStatus = status;
        await fixture.Turn("voice", "move_to");
        fixture.Transport.Active = false;
        await fixture.ActionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Turn("voice", null);
        Assert.That(fixture.Providers.Contexts.Last(), Does.Contain("result=" + code));
    }

    [Test]
    public async Task SynchronousCompletionIsReportedImmediatelyAndReleasesControl()
    {
        await using var fixture = new Fixture();
        fixture.Transport.Complete = true;
        await fixture.Turn("voice", "self_action");
        await fixture.ActionTask;
        Assert.That(fixture.Transport.Releases, Is.EqualTo(1));
        Assert.That(fixture.Transport.PlanPolls, Is.Zero);
        await fixture.Turn("voice", null);
        Assert.That(fixture.Providers.Contexts.Last(), Does.Contain("result=Completed"));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("agent-action-owner-").FullName;
        public readonly Transport Transport = new();
        public readonly Providers Providers = new();
        public AgentHostRuntime Runtime { get; }
        private McpClient Mcp { get; }
        public Task ActionTask => (Task)typeof(AgentHostRuntime).GetField("_actionTask", Private)!.GetValue(Runtime)!;
        public Fixture()
        {
            var options = new AgentHostOptions
            {
                McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture", ProfileId = "masha",
                DisplayName = "fixture", WorldId = "fixture", XaiKey = "", ElevenLabsKey = "",
                XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
                MemoryDirectory = Path.Combine(_directory, "memory"), StateDirectory = Path.Combine(_directory, "state"),
                FakeProviders = true
            };
            Runtime = new AgentHostRuntime(options, Providers, Transport);
            Mcp = new McpClient(options.ProviderOptions, Transport);
        }
        public async Task Turn(string trigger, string? tool)
        {
            Providers.Tool = tool;
            var task = (Task<bool>)typeof(AgentHostRuntime).GetMethod("ProcessSafelyAsync", Private)!.Invoke(Runtime,
                new object?[] { Mcp, "attachment", 901, trigger, trigger == "voice" ? "fixture" : "",
                    CancellationToken.None, CancellationToken.None, null, "", new[] { Guid.NewGuid().ToString("N") } })!;
            Assert.That(await task, Is.True, "Turn must commit successfully");
        }
        public Task Stop() => (Task)typeof(AgentHostRuntime).GetMethod("StopActionAsync", Private)!.Invoke(Runtime, new object[] { false })!;
        public async ValueTask DisposeAsync()
        {
            await Stop();
            Mcp.Dispose();
            Transport.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Providers : IAgentProviders
    {
        public int Decisions;
        public string? Tool;
        public JsonElement? Arguments;
        public readonly List<string> Contexts = new();
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
            string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        {
            Decisions++;
            Contexts.Add(memoryContext);
            return Task.FromResult(new CompanionDecision
            {
                Action = Tool == null ? null : new CompanionAction { Tool = Tool, Arguments = Arguments ?? JsonSerializer.SerializeToElement(Tool switch
                {
                    "move_to" => (object)new { x = 10, y = 10 },
                    "interact" => new { objectId = 10, interaction = "PickUp" },
                    "self_action" => new { kind = "GroundSit" },
                    "attack_mob" => new { mobId = 10 },
                    "carry_person" => new { targetNpcId = 902 },
                    _ => new { }
                }) }
            });
        }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken) => throw new AssertionException("No speech requested");
        public void Dispose() { }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public readonly List<string> Commands = new();
        public volatile bool Active;
        public bool Carried;
        public int Releases;
        public int Acquires;
        public bool Reject;
        public bool Complete;
        public int PlanPolls;
        public string PlanEndStatus = "Completed";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/list")
                result = new { tools = HexLive.Server.Mcp.McpTools.Catalog.Select(t => new
                    { name = t.Name, inputSchema = t.InputSchema }) };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var name = root.GetProperty("params").GetProperty("name").GetString();
                if (name == "list_colonists") PlanPolls++;
                if (name == "acquire_npc_control") Acquires++;
                if (name is "move_to" or "interact" or "self_action" or "attack_mob" or "carry_person")
                {
                    Commands.Add(name);
                    Active = true;
                    if (name == "carry_person") Carried = true;
                }
                if (name == "release_control") { Releases++; Carried = false; Active = false; }
                object payload = name switch
                {
                    "world_status" => new { worldId = "fixture", tick = 100, seed = 12345, paused = false },
                    "describe_colonist" => new { stateSummary = "health=1; unconscious=false" },
                    "list_colonists" => new { colonists = new[] { new { npcId = 901, planStatus = Active ? "Active" : PlanEndStatus, carriedNpcId = Carried ? (int?)902 : null } } },
                    "self_action" when Complete => new { status = "Completed" },
                    _ => new { accepted = true }
                };
                var rejected = Reject && name == "self_action";
                result = new { isError = rejected, content = new[] { new { type = "text", text = rejected
                    ? "{\"reason\":\"NoSupplies\",\"detail\":\"private-secret\"}" : JsonSerializer.Serialize(payload) } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }
}
