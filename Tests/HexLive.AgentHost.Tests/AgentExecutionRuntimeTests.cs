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
public sealed partial class AgentExecutionRuntimeTests
{
    private string _root = "";
    [SetUp] public void Setup() => _root = Directory.CreateTempSubdirectory("plan-runtime-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, true);

    [Test]
    public async Task TargetDisappearingBeforeDispatchStopsTheRemainingQueue()
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var objectId = host.Read(w => w.Entities.Objects.Values.First(o => o.DefinitionId == "tree.palm").Id);
        transport.BeforeToolCall = (name, _) =>
        {
            if (name == "execute_agent_command") host.Read(w => WorldObjectMutations.DespawnObject(w, objectId));
        };
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("harvest", "interact", JsonSerializer.SerializeToElement(new { objectId = objectId.Value, interaction = "Harvest" })),
             new("next", "stop", JsonSerializer.SerializeToElement(new { }))]);
        await Run(runtime, mcp, plan.Id, world, 15);
        var saved = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(saved.Status, Is.EqualTo("paused"));
        Assert.That(saved.Command!.Status, Is.EqualTo("failed"));
        Assert.That(saved.Command.Reason, Is.Not.Empty);
        Assert.That(saved.Cursor, Is.Zero);
        Assert.That(transport.Executions, Is.EqualTo(1));
        Assert.That(providers.Calls, Is.Zero);
        typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
        var maintain = typeof(AgentHostRuntime).GetMethod("MaintainPlanLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)maintain.Invoke(runtime, [mcp, 901, true, CancellationToken.None])!;
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.True,
            "A known failed step needs a new decision, not native AI taking over.");
        var archive = await store.SnapshotAsync(default);
        await store.CommitTurnAsync(world with { ObjectiveRevision = archive.Objective!.Revision,
            ExecutionPlanRevision = saved.Revision, ExecutionPlanId = saved.Id }, "pause", "voice",
            new CompanionDecision { ObjectiveUpdate = new() { Operation = "pause", Reason = "Fixture" } }, default);
        await (Task)maintain.Invoke(runtime, [mcp, 901, true, CancellationToken.None])!;
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
    }

    [Test]
    public async Task NativeInterruptionCauseReachesThePausedQueue()
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        host.Read(w => { w.Entities.Npcs[new EntityId(901)].Needs.Energy = .2f; return true; });
        var (store, world, plan) = await Install(runtime, mcp,
            [new("rest", "rest_until", JsonSerializer.SerializeToElement(new { need = "Energy", target = .8 })),
             new("next", "stop", JsonSerializer.SerializeToElement(new { }))]);
        var running = Run(runtime, mcp, plan.Id, world, 15);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (transport.Executions == 0 && !running.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.That(transport.Executions, Is.EqualTo(1));
        host.Read(w => PlanInterruption.TryAbort(w, w.Entities.Npcs[new EntityId(901)], InterruptionCause.Crying, "Fixture body interruption"));
        await running;
        var saved = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(saved.Status, Is.EqualTo("paused"));
        Assert.That(saved.Command!.Reason, Is.EqualTo("PlanInterrupted.Crying"));
        Assert.That(transport.Executions, Is.EqualTo(1), "No next step after a body interruption.");
    }

    [Test]
    public async Task BoundedCraftRepeatPaysEachRecipeAndProducesTwoRealRopes()
    {
        using var host = Host();
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        host.Read(w =>
        {
            foreach (var person in w.Entities.Npcs.Values) { person.Mind.ManualControl = true; person.Faction = Faction.Colony; }
            for (var i = 0; i < 64; i++) engine.Step();
            w.Mobs.Clear();
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Inventory.Items.Clear(); npc.Needs.Energy = npc.Needs.Stamina = 1;
            npc.Needs.Hunger = npc.Needs.Thirst = 0;
            npc.Perception.Hostiles.Clear(); npc.Perception.Mobs.Clear(); npc.Mind.AdrenalineUntilTick = 0;
            var bill = RecipeCatalog.InputCount(HexLive.Simulation.AI.GoalType.CraftRope, ContentIds.Fiber);
            for (var i = 0; i < 2 * bill; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Fiber));
            return true;
        });
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("make-rope", "craft_item", JsonSerializer.SerializeToElement(new { recipeGoal = "CraftRope" })) { Repeat = 2 }]);
        var running = Run(runtime, mcp, plan.Id, world, 30);
        while (!running.IsCompleted)
        {
            host.Read(w =>
            {
                for (var i = 0; i < 8 && w.Entities.Npcs[new EntityId(901)].Plan.Status == HexLive.Simulation.AI.PlanStatus.Active; i++) engine.Step();
                return true;
            });
            await Task.Delay(1);
        }
        await running;
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Status, Is.EqualTo("completed"));
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Inventory.Items.Count(i => i.DefinitionId == ContentIds.Rope)), Is.EqualTo(2));
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Inventory.Items.Count(i => i.DefinitionId == ContentIds.Fiber)), Is.Zero);
        Assert.That(transport.Executions, Is.EqualTo(2));
        Assert.That(providers.Calls, Is.Zero);
    }

    [Test]
    public async Task BoundedRepeatUsesDistinctReceiptsAndPersistsEachIterationWithoutModelCalls()
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("repeat-stop", "stop", JsonSerializer.SerializeToElement(new { })) { Repeat = 3 }]);
        await Run(runtime, mcp, plan.Id, world);
        var saved = await new MashaMemoryStore(options.MemoryDirectory).SnapshotAsync(default);
        Assert.That(saved.ExecutionPlan!.SchemaVersion, Is.EqualTo(3));
        Assert.That(saved.ExecutionPlan.Status, Is.EqualTo("completed"));
        Assert.That(transport.Executions, Is.EqualTo(3));
        Assert.That(saved.ExecutionProgress.Select(p => p.CommandId).Distinct().Count(), Is.EqualTo(3));
        Assert.That(saved.ExecutionProgress.Select(p => p.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
        Assert.That(providers.Calls, Is.Zero);
    }

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

    [TestCase("world-pause")]
    [TestCase("pause")]
    [TestCase("complete")]
    public async Task CompletedSegmentRetainsControlWhileSimulationAdvancesAndReleasesWhenFinished(string end)
    {
        using var host = Host();
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp);
        typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
        await Run(runtime, mcp, plan.Id, world);
        var maintain = typeof(AgentHostRuntime).GetMethod("MaintainPlanLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)maintain.Invoke(runtime, [mcp, 901, true, CancellationToken.None])!;
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        host.Read(w =>
        {
            for (var i = 0; i < 120; i++) engine.Step();
            Assert.That(w.Entities.Npcs[new EntityId(901)].Mind.ManualControl, Is.True,
                "Inference between completed segments must not hand the body to native AI.");
            return true;
        });
        Assert.That(providers.Calls, Is.Zero);
        Assert.That(transport.Executions, Is.EqualTo(2));
        if (end != "world-pause")
        {
            var state = await store.SnapshotAsync(default);
            await store.CommitTurnAsync(world with { ObjectiveRevision = state.Objective!.Revision,
                ExecutionPlanRevision = state.ExecutionPlan!.Revision, ExecutionPlanId = state.ExecutionPlan.Id },
                "end", "voice", new CompanionDecision { ObjectiveUpdate = new() { Operation = end, Reason = "Fixture" } }, default);
        }
        await (Task)maintain.Invoke(runtime, [mcp, 901, end != "world-pause", CancellationToken.None])!;
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
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

    [TestCase("Energy")]
    [TestCase("Stamina")]
    public async Task BoundedRecoveryContinuesSavedQueueWithoutAnotherModelDecision(string need)
    {
        using var host = Host();
        var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        host.Read(w =>
        {
            w.Mobs.Clear();
            foreach (var person in w.Entities.Npcs.Values)
            { person.Mind.ManualControl = true; person.Faction = Faction.Colony; }
            // Let the normal initial wildlife spawn finish before making this
            // isolated recovery fixture safe; do not suppress threats while resting.
            for (var i = 0; i < 64; i++) engine.Step();
            w.Mobs.Clear();
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Needs.Energy = need == "Energy" ? .2f : .8f;
            npc.Needs.Stamina = need == "Stamina" ? .2f : .8f;
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Mind.AdrenalineUntilTick = 0;
            npc.Perception.Hostiles.Clear();
            npc.Perception.Mobs.Clear();
            return true;
        });
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)));
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp,
            [new("rest", "rest_until", JsonSerializer.SerializeToElement(new { need, target = .4 }))
                { Condition = new("bodyNeeds." + need.ToLowerInvariant() + ".value", "lte", .3, "continue") },
             new("continue", "stop", JsonSerializer.SerializeToElement(new { }))]);
        var running = Run(runtime, mcp, plan.Id, world, 90);
        var sawRest = false;
        for (var batch = 0; !running.IsCompleted && batch < 2000; batch++)
        {
            host.Read(w =>
            {
                var npc = w.Entities.Npcs[new EntityId(901)];
                for (var i = 0; i < 32 && npc.Plan.Status == HexLive.Simulation.AI.PlanStatus.Active; i++)
                {
                    engine.Step();
                    sawRest |= need == "Energy" ? npc.Execution.CurrentInteraction == InteractionType.Sleep :
                        npc.Execution.CurrentInteraction is InteractionType.Sit or InteractionType.Rest;
                }
                return true;
            });
            await Task.Delay(1);
        }
        await running;
        var saved = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(saved.Status, Is.EqualTo("completed"), host.Read(w => JsonSerializer.Serialize(new
        { saved.Reason, ledger = w.AgentCommands.GetValueOrDefault(901),
            w.Tick, mobs = w.Mobs.Count,
            hostiles = w.Entities.Npcs[new EntityId(901)].Perception.Hostiles.Select(h => h.Id.Value),
            adrenaline = w.Entities.Npcs[new EntityId(901)].Mind.AdrenalineUntilTick,
            events = w.Events.Items.TakeLast(8).Select(e => new { e.Type, e.Message }) })));
        Assert.That(sawRest, Is.True, "Energy and stamina require their own form of recovery.");
        Assert.That(host.Read(w => need == "Energy" ? w.Entities.Npcs[new EntityId(901)].Needs.Energy :
            w.Entities.Npcs[new EntityId(901)].Needs.Stamina), Is.GreaterThanOrEqualTo(.4f));
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

    [Test]
    public async Task UnresolvableReceiptStopsAfterThreeReadsWithoutReplayingTheCommand()
    {
        using var host = Host();
        using var transport = new Transport(new McpTools(host, new ControlLeases(45)))
            { LoseNextResponse = true, UnknownCommandReads = true };
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (_, world, plan) = await Install(runtime, mcp);
        await Run(runtime, mcp, plan.Id, world);
        typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Advance the scheduler deadline; the inner receipt poll still runs normally.
            typeof(AgentHostRuntime).GetField("_nextExecutionRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(runtime, DateTimeOffset.MinValue);
            await (Task)typeof(AgentHostRuntime).GetMethod("TryStartExecutionPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(runtime, [mcp, 901, CancellationToken.None])!;
            await ((Task)typeof(AgentHostRuntime).GetField("_actionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(transport.CommandReads, Is.EqualTo(3));
        Assert.That(transport.Executions, Is.EqualTo(1));
        Assert.That(providers.Calls, Is.Zero);
        Assert.That(typeof(AgentHostRuntime).GetField("_planNeedsDecision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime), Is.EqualTo(1));
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

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task LostResponseAfterExecutionAutomaticallyReconcilesWithoutReplay(bool restart, bool lastCommand)
    {
        using var host = Host(); using var transport = new Transport(new McpTools(host, new ControlLeases(45))) { LoseNextResponse = true };
        var options = Options(); using var providers = new NoModel();
        var runtime = new AgentHostRuntime(options, providers);
        using var mcp = new McpClient(options.ProviderOptions, transport);
        var (store, world, plan) = await Install(runtime, mcp, lastCommand
            ? [new("last", "stop", JsonSerializer.SerializeToElement(new { }))] : null);
        await Run(runtime, mcp, plan.Id, world);
        plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Command!.Status, Is.EqualTo("unknown"));
        Assert.That(transport.Executions, Is.EqualTo(1));
        if (restart) runtime = new AgentHostRuntime(options, providers);
        store = Memory(runtime);
        typeof(AgentHostRuntime).GetField("_historyWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, world);
        if (!restart) await Task.Delay(2100);
        await (Task)typeof(AgentHostRuntime).GetMethod("TryStartExecutionPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [mcp, 901, CancellationToken.None])!;
        await ((Task)typeof(AgentHostRuntime).GetField("_actionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Status, Is.EqualTo("completed"));
        Assert.That(transport.Executions, Is.EqualTo(lastCommand ? 1 : 2));
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.True);
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
    private static async Task Run(AgentHostRuntime runtime, McpClient mcp, string planId, MashaWorldHandle world, int timeoutSeconds = 15, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await ((Task)typeof(AgentHostRuntime).GetMethod("RunExecutionPlanAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [mcp, 901, planId, world, timeout.Token])!);
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
        public Action<string, JsonElement>? BeforeToolCall;
        public bool LoseNextResponse;
        public bool UnknownCommandReads;
        public int CommandReads;
        public int Executions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            object result = new { protocolVersion = "2025-06-18" };
            if (root.GetProperty("method").GetString() == "tools/list")
                result = new { tools = McpTools.Catalog.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }) };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var parameters = root.GetProperty("params"); var name = parameters.GetProperty("name").GetString()!;
                BeforeToolCall?.Invoke(name, parameters.GetProperty("arguments"));
                var text = tools.Call(name, parameters.GetProperty("arguments"), "mcp:plans", out var error);
                if (name == "read_agent_command" && parameters.GetProperty("arguments").GetProperty("sequence").GetInt64() > 0)
                {
                    CommandReads++;
                    if (UnknownCommandReads)
                    {
                        var args = parameters.GetProperty("arguments");
                        text = JsonSerializer.Serialize(new { highestSequence = 1, sequence = args.GetProperty("sequence").GetInt64(),
                            commandId = args.GetProperty("commandId").GetString(), outcome = "unknown", reason = "FixtureUnavailable" });
                    }
                }
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
