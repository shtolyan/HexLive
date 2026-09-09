using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class RequestedTalkTopicTests
{
    [Test]
    public void RequestedTopicSurvivesApproachSaveRefreshAndThenClears()
    {
        var engine = Setup(out var actor, out var target);
        Accept(engine.World, actor, target, TalkTopic.Food);
        Assert.That(actor.Execution.CurrentInteraction, Is.Not.EqualTo(InteractionType.Talk));
        engine = Reload(engine.World, WorldSaveSerializer.BlobVersion);
        actor = engine.World.Entities.Npcs[actor.Id];
        target = engine.World.Entities.Npcs[target.Id];
        Assert.That(actor.Plan.RequestedTalkTopic, Is.EqualTo(TalkTopic.Food));
        StartTalk(engine, actor, target);
        Assert.That(actor.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.Food));
        var refresh = actor.Execution.StartTick + 30;
        while (engine.World.Tick < refresh) { Calm(actor); Calm(target); engine.Step(); }
        Assert.That(actor.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.Food), "Native refresh must keep the requested shared topic.");
        var resultTick = actor.Execution.LastTalkResultTick;
        for (var i = 0; i < 200 && actor.Mind.CurrentGoal != GoalType.None; i++) { Calm(actor); Calm(target); engine.Step(); }
        Assert.Multiple(() =>
        {
            Assert.That(actor.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(actor.Plan.RequestedTalkTopic, Is.Null);
            Assert.That(actor.Execution.LastTalkResultTick, Is.GreaterThan(resultTick), "Native social outcome must still run.");
        });
        Accept(engine.World, actor, target, null);
        Assert.That(actor.Plan.RequestedTalkTopic, Is.Null, "The next ordinary talk must not inherit Food.");
    }

    [Test]
    public void OlderSaveHasNoRequestedTopic()
    {
        var engine = Setup(out var actor, out var target);
        Accept(engine.World, actor, target, TalkTopic.Joke);
        var loaded = Reload(engine.World, 72);
        Assert.That(loaded.World.Entities.Npcs[actor.Id].Plan.RequestedTalkTopic, Is.Null);
    }

    [TestCase(TalkTopic.Hunger)]
    [TestCase(TalkTopic.Stranger)]
    [TestCase((TalkTopic)999)]
    public void InvalidTopicDoesNotReplaceAnAcceptedPlanAndStopClearsIt(TalkTopic topic)
    {
        var engine = Setup(out var actor, out var target);
        Accept(engine.World, actor, target, TalkTopic.Home);
        var admission = ManualCommandExecutor.Apply(engine.World, new TalkToCommand(actor.Id, target.Id, topic));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("InvalidTalkTopic"));
        Assert.That(actor.Plan.RequestedTalkTopic, Is.EqualTo(TalkTopic.Home));
        ManualCommandExecutor.Apply(engine.World, new StopCommand(actor.Id));
        Assert.That(actor.Plan.RequestedTalkTopic, Is.Null);
    }

    [Test]
    public void RequestedTopicDoesNotBypassTheListenersPersonalRefusal()
    {
        var engine = Setup(out var actor, out var target);
        target.Social.GetOrCreate(actor.Id).Affinity = -0.9f;
        target.Needs.Social = 1f;
        Accept(engine.World, actor, target, TalkTopic.Flirt);
        for (var i = 0; i < 120 && actor.Execution.LastTalkAffinityDelta >= 0f; i++) { Calm(actor); Calm(target); engine.Step(); }
        Assert.That(actor.Execution.LastTalkAffinityDelta, Is.LessThan(0f), "The native personal refusal and penalty must run.");
        Assert.That(actor.Plan.RequestedTalkTopic, Is.Null);
        Assert.That(actor.Execution.CurrentTalkTopic, Is.Null);
    }

    [Test]
    public void RealPersonalComplaintStillOverridesRequestedSharedTopic()
    {
        var engine = Setup(out var actor, out var target);
        Accept(engine.World, actor, target, TalkTopic.Food);
        StartTalk(engine, actor, target);
        actor.Needs.Thirst = 1f;
        var tick = actor.Execution.StartTick + 30;
        while (MathUtil.Hash01(engine.World.Seed, tick / 30, actor.Id.Value * 131 + target.Id.Value, 5507) >= 0.95f) tick += 30;
        engine.World.Tick = tick;
        actor.Execution.EndTick = tick + 60;
        new ExecutionSystem().Run(engine.World);
        Assert.That(actor.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.Thirst));
        Assert.That(target.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.Food));
        Assert.That(actor.Plan.RequestedTalkTopic, Is.EqualTo(TalkTopic.Food));
    }

    [Test]
    public void OldWireBytesRemainExactAndTypedTopicRoundTrips()
    {
        static byte[] Encode(TalkToCommand command)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            SimulationCommandCodec.Write(writer, command);
            return buffer.ToArray();
        }
        Assert.That(Encode(new TalkToCommand(new EntityId(21), new EntityId(112))),
            Is.EqualTo(new byte[] {11, 0, 21, 0, 0, 0, 112, 0, 0, 0}));
        using var bytes = new MemoryStream(Encode(new TalkToCommand(new EntityId(21), new EntityId(112), TalkTopic.Joke)));
        using var reader = new BinaryReader(bytes);
        var decoded = (TalkToCommand)SimulationCommandCodec.Read(reader);
        Assert.That(decoded.RequestedTopic, Is.EqualTo(TalkTopic.Joke));
        Assert.That(decoded.Npc.Value, Is.EqualTo(21));
        Assert.That(decoded.Target.Value, Is.EqualTo(112));
    }

    private static void Calm(NPCState npc)
    {
        npc.Needs.Hunger = 0f; npc.Needs.Thirst = 0f; npc.Needs.Energy = 1f; npc.Needs.ThermalComfort = 0f;
    }

    private static SimulationEngine Setup(out NPCState actor, out NPCState target)
    {
        var engine = TestWorld.CreateEngine();
        var pair = engine.World.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).OrderBy(n => n.Id.Value).Take(2).ToArray();
        actor = pair[0]; target = pair[1];
        foreach (var npc in pair) { Calm(npc); engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true)); }
        engine.Step(); // Acquire canonical initial junctions before placing the fixture.
        var world = engine.World;
        var id = SpatialQueries.GetPassableNeighbors(world, actor.CurrentJunction!.Value).First(j => SpatialQueries.IsJunctionFree(world, j));
        var junction = world.Junctions.Items[id];
        if (target.CurrentJunction is { } old) { SpatialMutations.FreeJunction(world, old, target.Id); SpatialMutations.ReleaseJunctionReservation(world, old, target.Id); }
        var oldTile = target.Tile;
        target.Tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : target.Tile;
        target.Fragment = junction.Fragment; target.Position = junction.WorldPosition; target.CurrentJunction = id;
        SpatialMutations.MoveEntityToTile(world, target.Id, oldTile, target.Tile);
        SpatialMutations.OccupyJunction(world, id, target.Id);
        return engine;
    }

    private static void Accept(WorldState world, NPCState actor, NPCState target, TalkTopic? topic)
    {
        var admission = ManualCommandExecutor.Apply(world, new TalkToCommand(actor.Id, target.Id, topic));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
    }

    private static void StartTalk(SimulationEngine engine, NPCState actor, NPCState target)
    {
        for (var i = 0; i < 120 && actor.Execution.CurrentInteraction != InteractionType.Talk; i++) { Calm(actor); Calm(target); engine.Step(); }
        Assert.That(actor.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Talk));
        Assert.That(actor.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
    }

    private static SimulationEngine Reload(WorldState world, int version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.WriteAtVersion(world, writer, version);
        buffer.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(loaded, reader);
        var clock = new SimulationClock(); clock.Resume();
        var engine = new SimulationEngine(loaded, new SimulationSettings(), clock);
        SimulationSystemRegistry.RegisterDefaults(engine);
        return engine;
    }
}
