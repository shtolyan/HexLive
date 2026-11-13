using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §121.13: перетаскивание в списке действий — это команда симуляции
/// <see cref="MovePlayerOrderCommand"/>, а не перестановка в клиенте. Очередь
/// остаётся единственным источником правды; двигается только ожидающий хвост,
/// приказы называются стабильными id, а устаревший drag отказывается.
/// </summary>
public sealed class PlayerOrderReorderTests
{
    private sealed class Fixture
    {
        public SimulationEngine Engine;
        public NPCState Npc;
        public NPCState Other;
        public int Walk, Sit, Home, Hug;
    }

    private static Fixture Queue()
    {
        var now = 0d;
        var engine = TestWorld.CreateEngine(clock: new SimulationClock(() => now));
        var world = engine.World;
        var colonists = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).ToList();
        var npc = colonists[0];
        var other = colonists[1];
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Step();
        var destination = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) is >= 2 and <= 4 &&
            npc.CurrentJunction is { } start && Connectivity.Reachable(world, start, j.Id));

        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new MoveToCommand(npc.Id, destination.WorldPosition))).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new SelfActionCommand(npc.Id, SelfActionKind.GroundSit))).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new SelfActionCommand(npc.Id, SelfActionKind.GoHome))).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new TalkToCommand(npc.Id, other.Id, TalkTopic.Hug))).Accepted, Is.True);
        new ManualOrderSystem().Run(world);

        var orders = Orders(engine, npc);
        Assert.That(orders.Select(o => (o.Action, o.Active)), Is.EqualTo(new[]
        {
            ("MoveTo", true), ("SelfAction", false), ("SelfAction", false), ("TalkTo", false),
        }));
        return new Fixture
        {
            Engine = engine, Npc = npc, Other = other,
            Walk = orders[0].Id, Sit = orders[1].Id, Home = orders[2].Id, Hug = orders[3].Id,
        };
    }

    private static System.Collections.Generic.List<PlayerOrderView> Orders(SimulationEngine engine, NPCState npc) =>
        Debug.WorldSnapshotExporter.Export(engine.World).Npcs.Single(n => n.Id == npc.Id).PlayerOrders;

    private static int[] Ids(Fixture f) => Orders(f.Engine, f.Npc).Select(o => o.Id).ToArray();

    [Test]
    public void DraggingMovesOnlyTheWaitingTailByStableIds()
    {
        var f = Queue();

        var admission = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Sit));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Hug, f.Sit, f.Home }));

        // 0 = «в конец».
        Assert.That(f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Sit, 0)).Accepted, Is.True);
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Hug, f.Home, f.Sit }));

        // На себя — пустой ход, не ошибка.
        Assert.That(f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Home, f.Home)).Accepted, Is.True);
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Hug, f.Home, f.Sit }));
    }

    [Test]
    public void QueuedOrdersCarryTheirKindAndTargetForTheTooltip()
    {
        var f = Queue();
        var hug = Orders(f.Engine, f.Npc).Single(o => o.Id == f.Hug);
        Assert.That(hug.Detail, Is.EqualTo("Hug"));
        Assert.That(hug.TargetNpcId, Is.EqualTo(f.Other.Id.Value));
        Assert.That(hug.TargetObjectId, Is.Null);
        var walk = Orders(f.Engine, f.Npc).Single(o => o.Id == f.Walk);
        Assert.That(walk.TargetNpcId, Is.Null);
        Assert.That(walk.TargetObjectId, Is.Null);
    }

    [Test]
    public void QueueOrderAndTargetsSurviveTheSnapshotWire()
    {
        var f = Queue();
        Assert.That(f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Sit)).Accepted, Is.True);
        var sent = Debug.WorldSnapshotExporter.Export(f.Engine.World);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            Wire.WorldSnapshotCodec.Write(sent, writer, includeDebugDetails: false);
        stream.Position = 0;
        var received = new Debug.WorldSnapshot();
        using (var reader = new System.IO.BinaryReader(stream))
            Wire.WorldSnapshotCodec.Read(reader, received);

        var expected = sent.Npcs.Single(n => n.Id == f.Npc.Id).PlayerOrders;
        var actual = received.Npcs.Single(n => n.Id == f.Npc.Id).PlayerOrders;
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual.Select(o => o.Id), Is.EqualTo(new[] { f.Walk, f.Hug, f.Sit, f.Home }));
        Assert.That(actual[1].TargetNpcId, Is.EqualTo(f.Other.Id.Value));
    }

    [Test]
    public void ReorderedOrderIsTheOneThatStartsNext()
    {
        var f = Queue();
        Assert.That(f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Sit, 0)).Accepted, Is.True);
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Home, f.Hug, f.Sit }));
        Assert.That(f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Home)).Accepted, Is.True);
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Hug, f.Home, f.Sit }));
        Assert.That(f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Sit, f.Hug)).Accepted, Is.True);
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Sit, f.Hug, f.Home }));

        // Отменяем бегущий «идти»: следующей стартует та, что теперь первая в хвосте.
        Assert.That(f.Engine.ApplyManualCommand(new CancelPlayerOrderCommand(f.Npc.Id, f.Walk)).Accepted, Is.True);
        new ManualOrderSystem().Run(f.Engine.World);
        Assert.That(f.Npc.Mind.PlayerOrders.Active?.Id, Is.EqualTo(f.Sit));
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Sit, f.Hug, f.Home }));
    }

    [Test]
    public void StaleDragIsRefusedAndChangesNothing()
    {
        var f = Queue();
        Assert.That(f.Engine.ApplyManualCommand(new CancelPlayerOrderCommand(f.Npc.Id, f.Home)).Accepted, Is.True);
        var before = Ids(f);

        var movedGone = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Home, f.Sit));
        Assert.That(movedGone.Accepted, Is.False);
        Assert.That(movedGone.Reason, Is.EqualTo("QueueChanged"));

        var anchorGone = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Home));
        Assert.That(anchorGone.Reason, Is.EqualTo("QueueChanged"));

        var unknown = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, 9999, 0));
        Assert.That(unknown.Reason, Is.EqualTo("QueueChanged"));

        Assert.That(Ids(f), Is.EqualTo(before));
    }

    [Test]
    public void RunningOrderIsNeitherMovedNorUsedAsAnAnchor()
    {
        var f = Queue();
        var goal = f.Npc.Mind.CurrentGoal;

        var moveRunning = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Walk, 0));
        Assert.That(moveRunning.Accepted, Is.False);
        Assert.That(moveRunning.Reason, Is.EqualTo("OrderRunning"));

        var inFrontOfRunning = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Walk));
        Assert.That(inFrontOfRunning.Reason, Is.EqualTo("OrderRunning"));

        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Sit, f.Home, f.Hug }));
        Assert.That(f.Npc.Mind.PlayerOrders.Active?.Id, Is.EqualTo(f.Walk));
        Assert.That(f.Npc.Mind.CurrentGoal, Is.EqualTo(goal));
    }

    [Test]
    public void AnotherPlayersCharacterIsRefused()
    {
        var f = Queue();
        var command = new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Sit);

        var stranger = f.Engine.ApplyManualCommand(command, "player:other", System.Array.Empty<int>());
        Assert.That(stranger.Reason, Is.EqualTo("NotAssigned"));
        var otherAssignment = f.Engine.ApplyManualCommand(command, "player:other", new[] { f.Other.Id.Value });
        Assert.That(otherAssignment.Reason, Is.EqualTo("NotAssigned"));
        Assert.That(Ids(f), Is.EqualTo(new[] { f.Walk, f.Sit, f.Home, f.Hug }));

        var owner = f.Engine.ApplyManualCommand(command, "player:owner", new[] { f.Npc.Id.Value });
        Assert.That(owner.Accepted, Is.True, owner.Reason);
    }

    [Test]
    public void CharacterReturnedToAiRefusesTheDrag()
    {
        var f = Queue();
        Assert.That(f.Engine.ApplyManualCommand(new SetManualControlCommand(f.Npc.Id, false)).Accepted, Is.True);
        var admission = f.Engine.ApplyManualCommand(new MovePlayerOrderCommand(f.Npc.Id, f.Hug, f.Sit));
        Assert.That(admission.Accepted, Is.False);
        Assert.That(admission.Reason, Is.EqualTo("NotManual"));
    }
}

}
