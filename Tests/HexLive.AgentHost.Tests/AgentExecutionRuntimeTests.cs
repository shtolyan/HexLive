using System.Net;
using System.Reflection;
using System.Text.Json;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

[NonParallelizable]
public sealed class AgentExecutionRuntimeTests
{
    private string _root = "";
    [SetUp] public void Setup() => _root = Directory.CreateTempSubdirectory("plan-runtime-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, true);

    [Test]
    public async Task ExecutesNextStepWithoutModelOrSpeechAndPersistsCompletedCursor()
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp);
        await Run(runtime, mcp, plan.Id, world);
        var saved = (await new MashaMemoryStore(options.MemoryDirectory).SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(saved.Status, Is.EqualTo("completed"));
        Assert.That(saved.Cursor, Is.EqualTo(2));
        Assert.That(transport.Executions, Is.EqualTo(2));
        Assert.That(providers.Calls, Is.Zero);
        Assert.That(host.Read(w => w.AgentCommands[901].HighestSequence), Is.EqualTo(2));
    }

    [Test]
    public async Task NativeMovementCompletesBeforeNextCommandWithoutAnIntermediateModelTurn()
    {
        using var host = Host();
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        host.Read(w => { engine.Step(); return true; });
        var tools = new McpTools(host, new ControlLeases(45));
        using var transport = new Transport(tools);
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        await mcp.CallToolAsync("acquire_npc_control", new { npcId = 901 }, default);
        var destinations = host.Read(w => w.Junctions.Items.Values.Where(j => !j.Blocked && SpatialQueries.IsJunctionFree(w, j.Id))
            .OrderBy(j => Math.Abs(j.WorldPosition.X - w.Entities.Npcs[new EntityId(901)].Position.X) +
                Math.Abs(j.WorldPosition.Y - w.Entities.Npcs[new EntityId(901)].Position.Y)).Skip(8).ToArray());
        var destination = destinations.First(j => {
            tools.Call("move_to", JsonSerializer.SerializeToElement(new { npcId = 901, x = j.WorldPosition.X, y = j.WorldPosition.Y }), "mcp:plans", out var error);
            return !error;
        });
        await mcp.CallToolAsync("stop", new { npcId = 901 }, default);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("walk", "move_to", JsonSerializer.SerializeToElement(new { x = destination.WorldPosition.X, y = destination.WorldPosition.Y })),
             new("finish", "stop", JsonSerializer.SerializeToElement(new { }))]);
        var running = Run(runtime, mcp, plan.Id, world);
        for (var i = 0; !running.IsCompleted && i < 5000; i++)
        {
            host.Read(w => { engine.Step(); return true; });
            await Task.Delay(1);
        }
        await running;
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Status, Is.EqualTo("completed"));
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].CurrentJunction), Is.EqualTo(destination.Id));
        Assert.That(transport.Executions, Is.EqualTo(2));
        Assert.That(providers.Calls, Is.Zero);
    }

    [Test]
    public async Task CapacityConditionBranchesBeforeSendingTheGuardedCommand()
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        host.Read(w =>
        {
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Inventory.Items.Clear();
            for (var i = 0; i < npc.Inventory.Capacity; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Coconut));
            return true;
        });
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("collect", "stop", JsonSerializer.SerializeToElement(new { }))
                { Condition = new("inventorySummary.freeSlots", "gte", 1, "return") },
             new("return", "stop", JsonSerializer.SerializeToElement(new { }))]);
        await Run(runtime, mcp, plan.Id, world);
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Status, Is.EqualTo("completed"));
        Assert.That(transport.Executions, Is.EqualTo(1));
        Assert.That(host.Read(w => w.AgentCommands[901].HighestSequence), Is.EqualTo(1));
        Assert.That(providers.Calls, Is.Zero);
    }

    [Test]
    public async Task CoconutPickupReturnAndUnloadRunAsOneSavedQueue()
    {
        using var host = Host();
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var fixture = host.Read(w =>
        {
            foreach (var person in w.Entities.Npcs.Values) person.Mind.ManualControl = true;
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Inventory.Items.Clear();
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            engine.Step();
            var at = SpatialQueries.GetPassableNeighbors(w, npc.CurrentJunction!.Value).First();
            var coconut = WorldObjectMutations.SpawnObject(w, ContentIds.Coconut,
                npc.Fragment, w.Junctions.Items[at].Tiles[0], at);
            return (coconut.Id, Home: npc.Position);
        });
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("collect", "interact", JsonSerializer.SerializeToElement(new { objectId = fixture.Id.Value, interaction = "PickUp" })),
             new("return", "move_to", JsonSerializer.SerializeToElement(new { x = fixture.Home.X, y = fixture.Home.Y })),
             new("unload", "manage_inventory", JsonSerializer.SerializeToElement(new
             { source = "Carried", index = 0, expectedDefinitionId = "food.coconut", action = "Drop" }))]);
        var running = Run(runtime, mcp, plan.Id, world);
        var carried = false;
        for (var i = 0; !running.IsCompleted && i < 10000; i++)
        {
            host.Read(w =>
            {
                var npc = w.Entities.Npcs[new EntityId(901)];
                carried |= npc.Inventory.Items.Any(item => item.DefinitionId == "food.coconut");
                if (npc.Plan.Status == HexLive.Simulation.AI.PlanStatus.Active) engine.Step();
                return true;
            });
            await Task.Delay(1);
        }
        await running;
        var saved = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(saved.Status, Is.EqualTo("completed"), saved.Reason);
        Assert.That(carried, Is.True, "The coconut must pass through the real inventory.");
        host.Read(w =>
        {
            var npc = w.Entities.Npcs[new EntityId(901)];
            Assert.That(npc.Inventory.Items, Is.Empty);
            Assert.That(npc.Position.X, Is.EqualTo(fixture.Home.X).Within(.01f));
            Assert.That(npc.Position.Y, Is.EqualTo(fixture.Home.Y).Within(.01f));
            Assert.That(w.Entities.Objects.Values.Any(o => o.DefinitionId == "food.coconut" && o.Id != fixture.Id && o.ProduceOrigin == ProduceOrigin.Gathered && o.Junctions.Count > 0 &&
                Math.Abs(w.Junctions.Items[o.Junctions[0]].WorldPosition.X - npc.Position.X) < 2f && Math.Abs(w.Junctions.Items[o.Junctions[0]].WorldPosition.Y - npc.Position.Y) < 2f), Is.True);
            return true;
        });
        Assert.That(transport.Executions, Is.EqualTo(3));
        Assert.That(providers.Calls, Is.Zero);
    }

    [Test]
    public async Task GiftCommandTransfersThePhysicalItemAndRecordsRecipientReactionWithoutModel()
    {
        using var host = Host();
        var gift = new ItemInstance("food.coconut") { ResourceAmount = 1f };
        var receiverId = host.Read(w =>
        {
            var giver = w.Entities.Npcs[new EntityId(901)];
            var receiver = w.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony && n.Id != giver.Id);
            foreach (var person in w.Entities.Npcs.Values) person.Mind.ManualControl = true;
            giver.Inventory.Items.Clear();
            receiver.Inventory.Items.Clear();
            giver.Inventory.Items.Add(gift);
            receiver.Needs.Thirst = 1f;
            var from = w.Junctions.Items.Values.First(j => !j.Blocked &&
                SpatialQueries.IsJunctionFree(w, j.Id) && !SpatialQueries.IsAllWaterJunction(w, j.Id) &&
                j.Neighbors.Any(id => SpatialQueries.IsJunctionFree(w, id) &&
                    !SpatialQueries.IsAllWaterJunction(w, id) &&
                    SpatialQueries.CanTouchAcross(w, j.Id, id, HexSpatialMath.HexRadius * 1.3f)));
            var to = w.Junctions.Items[from.Neighbors.First(id => SpatialQueries.IsJunctionFree(w, id) &&
                !SpatialQueries.IsAllWaterJunction(w, id) &&
                SpatialQueries.CanTouchAcross(w, from.Id, id, HexSpatialMath.HexRadius * 1.3f))];
            Place(w, giver, from);
            Place(w, receiver, to);
            receiver.Social.GetOrCreate(giver.Id).Affinity = 0f;
            return receiver.Id;
        });
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("give", "transfer_inventory", JsonSerializer.SerializeToElement(new
            {
                otherNpcId = receiverId.Value, source = "Carried", index = 0,
                expectedDefinitionId = gift.DefinitionId, count = 1, direction = "Give"
            }))]);
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var running = Run(runtime, mcp, plan.Id, world);
        for (var i = 0; !running.IsCompleted && i < 5000; i++)
        {
            host.Read(w =>
            {
                if (!w.Events.Items.Any(e => e.Type == "GiftGiven")) engine.Step();
                return true;
            });
            await Task.Delay(1);
        }
        await running;
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Status, Is.EqualTo("completed"));
        host.Read(w =>
        {
            Assert.That(w.Entities.Npcs[new EntityId(901)].Inventory.Items, Does.Not.Contain(gift));
            Assert.That(w.Entities.Npcs[receiverId].Inventory.Items.Single(), Is.SameAs(gift));
            Assert.That(w.Entities.Npcs[receiverId].Social.GetOrCreate(new EntityId(901)).Affinity, Is.GreaterThan(0));
            Assert.That(w.Events.Items.Count(e => e.Type == "GiftGiven"), Is.EqualTo(1));
            return true;
        });
        Assert.That(transport.Executions, Is.EqualTo(1));
        Assert.That(providers.Calls, Is.Zero);
    }

    private static void Place(WorldState world, NPCState npc, Junction destination)
    {
        if (npc.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, npc.Id);
        }
        var oldTile = npc.Tile;
        npc.CurrentJunction = destination.Id;
        npc.Position = destination.WorldPosition;
        npc.Tile = destination.Tiles[0];
        npc.Fragment = destination.Fragment;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, destination.Id, npc.Id);
    }

    [Test]
    public async Task LostResponseAfterExecutionIsReconciledWithoutReplayAfterRestart()
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45))) { LoseNextResponse = true };
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp);
        await Run(runtime, mcp, plan.Id, world);
        plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Command!.Status, Is.EqualTo("unknown"));
        Assert.That(transport.Executions, Is.EqualTo(1));
        runtime = new AgentHostRuntime(options, providers);
        store = Memory(runtime);
        await Run(runtime, mcp, plan.Id, world);
        plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(plan.Cursor, Is.EqualTo(1));
        Assert.That(plan.Command, Is.Null);
        Assert.That(transport.Executions, Is.EqualTo(1));
        var prompt = await store.BuildPromptContextAsync(world, "", default);
        world = world with { ObjectiveRevision = prompt.ObjectiveRevision, ExecutionPlanId = prompt.ExecutionPlanId, ExecutionPlanRevision = prompt.ExecutionPlanRevision };
        await store.CommitTurnAsync(world, "resume", "heartbeat", new CompanionDecision
            { ExecutionPlanUpdate = new() { Operation = "resume", Reason = "ReceiptConfirmed" } }, default);
        await Run(runtime, mcp, plan.Id, world);
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Status, Is.EqualTo("completed"));
        Assert.That(transport.Executions, Is.EqualTo(2));
        Assert.That(providers.Calls, Is.Zero);
    }

    private static async Task<(MashaMemoryStore, MashaWorldHandle, AgentExecutionPlan)> Install(AgentHostRuntime runtime, McpClient mcp,
        AgentExecutionStep[]? steps = null)
    {
        var store = Memory(runtime);
        var world = await store.BindHexLiveWorldAsync(await mcp.CallToolAsync("world_status", new { }, default), 901, "fixture", default);
        world = world with { ObjectiveRevision = 0, ExecutionPlanRevision = 0, ExecutionPlanId = "" };
        await store.CommitTurnAsync(world, "install", "heartbeat", new CompanionDecision
        {
            ObjectiveUpdate = new() { Operation = "set", Text = "Two controlled commands", Reason = "fixture" },
            ExecutionPlanUpdate = new() { Operation = "replace", Reason = "Fixture", Steps = steps ??
                [new("first", "stop", JsonSerializer.SerializeToElement(new { })), new("second", "stop", JsonSerializer.SerializeToElement(new { }))] }
        }, default);
        return (store, world, (await store.SnapshotAsync(default)).ExecutionPlan!);
    }
    private static MashaMemoryStore Memory(AgentHostRuntime runtime) => (MashaMemoryStore)typeof(AgentHostRuntime)
        .GetField("_memory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
    private static async Task Run(AgentHostRuntime runtime, McpClient mcp, string planId, MashaWorldHandle world)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ((Task)typeof(AgentHostRuntime).GetMethod("RunExecutionPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [mcp, 901, planId, world, timeout.Token])!).WaitAsync(timeout.Token);
    }
    private AgentHostOptions Options() => new()
    {
        McpUri = new("http://fixture/mcp"), McpToken = "fixture", ProfileId = "masha", DisplayName = "fixture", WorldId = "fixture",
        XaiKey = "", ElevenLabsKey = "", XaiModel = "fixture", ElevenLabsModel = "fixture", ElevenLabsVoiceId = "fixture",
        MemoryDirectory = Path.Combine(_root, "memory"), StateDirectory = Path.Combine(_root, "state"), FakeProviders = true
    };
    private WorldHost Host() => new(12345, GameMode.Feud, Path.Combine(_root, "world.sav"), Path.Combine(FindRoot(), "SimData/simdata.json"), false, companionProfile: "masha");
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
    private sealed class NoModel : IAgentProviders
    {
        public int Calls;
        public Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext, string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("UnexpectedModelCall"); }
        public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("UnexpectedSpeechCall"); }
        public void Dispose() { }
    }
    private sealed class Transport(McpTools tools) : HttpMessageHandler
    {
        public bool LoseNextResponse;
        public int Executions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/list")
                result = new { tools = McpTools.Catalog.Select(t => new { name = t.Name, inputSchema = t.InputSchema }) };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var parameters = root.GetProperty("params"); var name = parameters.GetProperty("name").GetString()!;
                var text = tools.Call(name, parameters.GetProperty("arguments"), "mcp:plans", out var error);
                if (name == "execute_agent_command")
                {
                    Executions++;
                    if (LoseNextResponse) { LoseNextResponse = false; throw new HttpRequestException("Lost response fixture"); }
                }
                result = new { isError = error, content = new[] { new { type = "text", text } } };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result })) };
            response.Headers.Add("Mcp-Session-Id", "fixture"); return response;
        }
    }
}
