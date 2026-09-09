using System.Net;
using System.Reflection;
using System.Text.Json;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

[NonParallelizable]
public sealed class AgentActionOutcomeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(false, "PlanCompleted")]
    [TestCase(true, "PlanInvalid")]
    public async Task RealCommandOutcomeSurvivesCompletionOrSweepWithDiagnosticsDisabled(bool interrupt, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("agent-action-outcome-").FullName;
        var tracing = SimTrace.Enabled;
        SimTrace.Enabled = false;
        try
        {
            using var host = new WorldHost(12345, GameMode.Feud, Path.Combine(directory, "world.sav"),
                Path.Combine(FindRoot(), "SimData/simdata.json"), false, companionProfile: "masha");
            host.EnableMcpEventLog();
            var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", Private)!.GetValue(host)!;
            host.Read(_ => { engine.Step(); return true; });
            var tools = new McpTools(host, new ControlLeases(45));
            const string owner = "mcp:outcome";
            bool Call(string tool, object arguments) { tools.Call(tool, JsonSerializer.SerializeToElement(arguments), owner, out var error); return !error; }
            Assert.That(Call("acquire_npc_control", new { npcId = 901 }), Is.True);
            // Real earlier admission failure must not become the next command's outcome.
            Assert.That(Call("interact", new { npcId = 901, objectId = int.MaxValue, interaction = "PickUp" }), Is.False);
            var candidates = host.Read(w =>
            {
                var npc = w.Entities.Npcs[new EntityId(901)];
                return w.Junctions.Items.Values.Where(j => !j.Blocked && SpatialQueries.IsJunctionFree(w, j.Id))
                    .OrderByDescending(j => Math.Abs(j.WorldPosition.X - npc.Position.X) + Math.Abs(j.WorldPosition.Y - npc.Position.Y)).ToArray();
            });
            var destination = candidates.First(j => Call("move_to", new { npcId = 901, x = j.WorldPosition.X, y = j.WorldPosition.Y }));
            Assert.That(Call("stop", new { npcId = 901 }), Is.True);
            using var handler = new Transport(tools);
            var options = new AgentHostOptions
            {
                McpUri = new Uri("http://fixture/mcp"), McpToken = "fixture", ProfileId = "masha", DisplayName = "fixture", WorldId = "fixture",
                XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
                MemoryDirectory = Path.Combine(directory, "memory"), StateDirectory = Path.Combine(directory, "state"), FakeProviders = true
            };
            var runtime = new AgentHostRuntime(options);
            using var mcp = new McpClient(options.ProviderOptions, handler);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var action = new CompanionAction { Tool = "move_to", Arguments = JsonSerializer.SerializeToElement(new
                { x = destination.WorldPosition.X, y = destination.WorldPosition.Y, run = false }) };
            var running = (Task)typeof(AgentHostRuntime).GetMethod("PerformActionSafelyAsync", Private)!
                .Invoke(runtime, new object[] { mcp, 901, action, timeout.Token, "actual-route" })!;
            try
            {
                while (!handler.CommandAccepted) await Task.Delay(10, timeout.Token);
                host.Read(world =>
                {
                    var npc = world.Entities.Npcs[new EntityId(901)];
                    Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
                    if (interrupt)
                    {
                        Assert.That(PlanInterruption.TryAbort(world, npc, InterruptionCause.PathFailure, "fixture path failure"), Is.True);
                        new ManualOrderSystem().Run(world);
                    }
                    else
                    {
                        for (var tick = 0; tick < 1200 && npc.Plan.Status == PlanStatus.Active; tick++) engine.Step();
                        Assert.That(npc.CurrentJunction, Is.EqualTo(destination.Id));
                    }
                    Assert.That(npc.Plan.Status, Is.EqualTo(interrupt ? PlanStatus.None : PlanStatus.Completed),
                        "MoveOnly retains Completed; the real interruption sweep erases Invalid before the poll");
                    return true;
                });
                await running.WaitAsync(timeout.Token);
                var feedback = (string)typeof(AgentHostRuntime).GetField("_actionFeedback", Private)!.GetValue(runtime)!;
                Assert.That(feedback, Does.Contain("result=" + expected));
                Assert.That(feedback, Does.Not.Contain("fixture path failure"));
                Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
            }
            finally { timeout.Cancel(); await running; }
        }
        finally { SimTrace.Enabled = tracing; Directory.Delete(directory, true); }
    }

    [Test]
    public void RealPickupPublishesCompletedAfterTheSweepWithoutDiagnostics()
    {
        var directory = Directory.CreateTempSubdirectory("agent-pickup-outcome-").FullName;
        var tracing = SimTrace.Enabled;
        SimTrace.Enabled = false;
        try
        {
            using var host = new WorldHost(12345, GameMode.Feud, Path.Combine(directory, "world.sav"),
                Path.Combine(FindRoot(), "SimData/simdata.json"), false, companionProfile: "masha");
            host.EnableMcpEventLog();
            var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", Private)!.GetValue(host)!;
            host.Read(_ => { engine.Step(); return true; });
            var tools = new McpTools(host, new ControlLeases(45));
            JsonElement Call(string tool, object arguments)
            {
                var text = tools.Call(tool, JsonSerializer.SerializeToElement(arguments), "mcp:pickup", out var error);
                Assert.That(error, Is.False, text);
                return JsonSerializer.Deserialize<JsonElement>(text);
            }
            var personId = host.Read(w => w.Entities.Npcs.Values.First(n => n.Id.Value != 901 &&
                FactionRelations.AreAllies(w.Entities.Npcs[new EntityId(901)], n)).Id.Value);
            Call("acquire_npc_control", new { npcId = 901 });
            Call("acquire_npc_control", new { npcId = personId }); // Keep the consenting ally still.
            // Retain real earlier history so the test also respects event-gap semantics.
            tools.Call("interact", JsonSerializer.SerializeToElement(new
                { npcId = 901, objectId = int.MaxValue, interaction = "PickUp" }), "mcp:pickup", out _);
            var outcome = new AgentActionOutcome(901, Call("read_events", new { npcId = 901 }));
            Call("carry_person", new { npcId = 901, targetNpcId = personId });
            host.Read(world =>
            {
                var carrier = world.Entities.Npcs[new EntityId(901)];
                for (var tick = 0; tick < 1200 && carrier.Plan.Status != PlanStatus.None; tick++) engine.Step();
                Assert.That(carrier.CarriedNpcId?.Value, Is.EqualTo(personId));
                Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.None), "Real pickup completion was swept");
                return true;
            });
            outcome.Observe(Call("read_events", new { npcId = 901, sinceSeq = outcome.Watermark, limit = 500 }));
            Assert.That(outcome.Result, Is.EqualTo("PlanCompleted"));
            Call("put_down_person", new { npcId = 901 });
            Call("release_control", new { npcId = 901 });
            Call("release_control", new { npcId = personId });
        }
        finally { SimTrace.Enabled = tracing; Directory.Delete(directory, true); }
    }

    [Test]
    public void OldOtherActorAndUnknownPayloadsDoNotSupplyAnOutcome()
    {
        var tracker = new AgentActionOutcome(901, JsonSerializer.SerializeToElement(new { watermark = 10, sessionEpoch = "a" }));
        tracker.Observe(Batch("a", new[] { Event(9, 901, "Completed"), Event(11, 902, "Completed"), Event(12, 901, "private-secret") }));
        Assert.That(tracker.Result, Is.Null);
        tracker.Observe(Batch("a", new[] { Event(14, 901, "Failed") }));
        Assert.That(tracker.Result, Is.EqualTo("PlanFailed"));
    }

    [TestCase(true, "a")]
    [TestCase(false, "different-world")]
    public void LostHistoryOrChangedWorldCannotBeCalledSuccess(bool gap, string epoch)
    {
        var tracker = new AgentActionOutcome(901, JsonSerializer.SerializeToElement(new { watermark = 10, sessionEpoch = "a" }));
        tracker.Observe(Batch(epoch, new[] { Event(12, 901, "Completed") }, gap));
        Assert.That(tracker.Result, Is.EqualTo("ActionOutcomeUnavailable"));
    }

    private static object Event(long seq, int entityId, string outcome) => new { seq, entityId, type = "ManualOrderFinished", message = "Order=PlayerOrder Outcome=" + outcome };
    private static JsonElement Batch(string epoch, object[] events, bool gap = false) => JsonSerializer.SerializeToElement(new
        { sessionEpoch = epoch, events, watermark = events.Select(e => JsonSerializer.SerializeToElement(e).GetProperty("seq").GetInt64()).DefaultIfEmpty(10).Max(), gap, sessionReset = false, truncated = false });
    private static string FindRoot()
    {
        for (var path = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "SimData/simdata.json"))) return path.FullName;
        throw new DirectoryNotFoundException("HexLive source root");
    }

    private sealed class Transport(McpTools tools) : HttpMessageHandler
    {
        public bool CommandAccepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/list")
                result = new { tools = McpTools.Catalog.Select(t => new { name = t.Name, inputSchema = t.InputSchema }) };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString()!;
                var text = tools.Call(name, parameters.GetProperty("arguments"), "mcp:outcome", out var error);
                if (name == "move_to" && !error) CommandAccepted = true;
                result = new { isError = error, content = new[] { new { type = "text", text } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture");
            return response;
        }
    }
}
