using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §121.13 r2: a ground click and the action queue. A plain click
/// (<see cref="ReplaceMovementOrderCommand"/>) replaces walking only — like
/// Diablo/RTS, the last point wins and nothing accumulates. Any other action
/// in the queue is never wiped by it (QueueHasAction); «Идти сюда» and
/// Shift+click append a go order (<see cref="QueuePlayerOrderCommand"/>),
/// which run in order after whatever stands before them.
/// </summary>
public sealed class PlayerGoClickTests
{
    private double _now;

    private (SimulationEngine Engine, NPCState Npc, Junction[] Points) Setup()
    {
        _now = 0d;
        var engine = TestWorld.CreateEngine(clock: new SimulationClock(() => _now));
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Step();
        var points = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                        HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) is >= 2 and <= 3 &&
                        npc.CurrentJunction is { } start && Connectivity.Reachable(world, start, j.Id) &&
                        SpatialQueries.IsJunctionFree(world, j.Id))
            .GroupBy(j => j.Tiles[0])
            .Select(g => g.First())
            .Take(3)
            .ToArray();
        Assert.That(points, Has.Length.EqualTo(3), "three reachable free points on different tiles");
        return (engine, npc, points);
    }

    private static MoveToCommand Go(NPCState npc, Junction point) => new(npc.Id, point.WorldPosition);

    private static List<PlayerOrderView> Orders(SimulationEngine engine, NPCState npc) =>
        Debug.WorldSnapshotExporter.Export(engine.World).Npcs.Single(n => n.Id == npc.Id).PlayerOrders;

    /// <summary>Steps until the queue is empty, recording which order ran.</summary>
    private List<string> RunQueue(SimulationEngine engine, NPCState npc, int maxTicks = 2400)
    {
        var seen = new List<string>();
        var run = new ManualOrderSystem();
        for (var i = 0; i < maxTicks; i++)
        {
            _now += 0.25d;
            engine.Step();
            run.Run(engine.World);
            var active = npc.Mind.PlayerOrders.Active;
            if (active != null)
            {
                var label = active.Action + ":" + active.Id;
                if (seen.Count == 0 || seen[^1] != label) seen.Add(label);
            }
            if (active == null && npc.Mind.PlayerOrders.Pending.Count == 0 && i > 4)
                break;
        }
        return seen;
    }

    [Test]
    public void PlainGoReplacesTheRunningWalkAndDropsWaitingWalks()
    {
        var (engine, npc, points) = Setup();
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[0]))).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[1]))).Accepted, Is.True);
        new ManualOrderSystem().Run(engine.World);
        Assert.That(Orders(engine, npc).Select(o => (o.Action, o.Active)),
            Is.EqualTo(new[] { ("MoveTo", true), ("MoveTo", false) }));

        var admission = engine.ApplyManualCommand(new ReplaceMovementOrderCommand(Go(npc, points[2])));
        Assert.That(admission.Accepted, Is.True, admission.Reason);

        var orders = Orders(engine, npc);
        Assert.That(orders.Select(o => (o.Action, o.Active)), Is.EqualTo(new[] { ("MoveTo", true) }),
            "Nothing accumulates: one walk, to the newest point, running at once");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder));
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(points[2].Id));

        RunQueue(engine, npc);
        Assert.That(npc.CurrentJunction, Is.EqualTo(points[2].Id));
    }

    [Test]
    public void PlainGoFromAiTakesControlAndWalksAtOnce()
    {
        var (engine, npc, points) = Setup();
        Assert.That(npc.Mind.ManualControl, Is.False);
        Assert.That(engine.ApplyManualCommand(new ReplaceMovementOrderCommand(Go(npc, points[0]))).Accepted, Is.True);
        Assert.That(npc.Mind.ManualControl, Is.True);
        Assert.That(npc.Mind.PlayerOrders.Active?.Action, Is.EqualTo("MoveTo"));
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(points[0].Id));
    }

    [Test]
    public void PlainGoNeverWipesAWaitingNonMovementAction()
    {
        var (engine, npc, points) = Setup();
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[0]))).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new SelfActionCommand(npc.Id, SelfActionKind.GroundSit))).Accepted, Is.True);
        new ManualOrderSystem().Run(engine.World);
        var before = Orders(engine, npc).Select(o => o.Id).ToArray();
        var target = npc.Plan.TargetJunctionId;

        var admission = engine.ApplyManualCommand(new ReplaceMovementOrderCommand(Go(npc, points[1])));
        Assert.That(admission.Accepted, Is.False);
        Assert.That(admission.Reason, Is.EqualTo("QueueHasAction"));
        Assert.That(Orders(engine, npc).Select(o => o.Id), Is.EqualTo(before));
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(target), "The running walk was not re-planned either");
    }

    [Test]
    public void PlainGoNeverInterruptsAnActionRunningInPlace()
    {
        var (engine, npc, points) = Setup();
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new SelfActionCommand(npc.Id, SelfActionKind.GroundSit))).Accepted, Is.True);
        new ManualOrderSystem().Run(engine.World);
        Assert.That(npc.Mind.PlayerOrders.Active?.Action, Is.EqualTo("SelfAction"));
        var goal = npc.Mind.CurrentGoal;

        var admission = engine.ApplyManualCommand(new ReplaceMovementOrderCommand(Go(npc, points[0])));
        Assert.That(admission.Reason, Is.EqualTo("QueueHasAction"));
        Assert.That(npc.Mind.PlayerOrders.Active?.Action, Is.EqualTo("SelfAction"));
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(goal));
        Assert.That(npc.Mind.PlayerOrders.Pending, Is.Empty);
    }

    [Test]
    public void AppendedGoRunsAfterTheOtherActionCompletes()
    {
        var (engine, npc, points) = Setup();
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new SelfActionCommand(npc.Id, SelfActionKind.GroundSit))).Accepted, Is.True);
        new ManualOrderSystem().Run(engine.World);
        // «Идти сюда» from the menu: an ordinary queued go at the end.
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[0]))).Accepted, Is.True);

        var ran = RunQueue(engine, npc);
        Assert.That(ran.Select(r => r.Split(':')[0]), Is.EqualTo(new[] { "SelfAction", "MoveTo" }));
        Assert.That(npc.CurrentJunction, Is.EqualTo(points[0].Id));
    }

    [Test]
    public void SeveralAppendedGoesAreWaypointsInOrder()
    {
        var (engine, npc, points) = Setup();
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(
            new SelfActionCommand(npc.Id, SelfActionKind.GroundSit))).Accepted, Is.True);
        new ManualOrderSystem().Run(engine.World);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[0]))).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[1]))).Accepted, Is.True);
        var ids = Orders(engine, npc).Select(o => o.Id).ToArray();

        var reached = new List<JunctionId>();
        var run = new ManualOrderSystem();
        for (var i = 0; i < 2400 && (npc.Mind.PlayerOrders.Active != null || npc.Mind.PlayerOrders.Pending.Count > 0); i++)
        {
            _now += 0.25d;
            engine.Step();
            run.Run(engine.World);
            if (npc.CurrentJunction is { } here && (here == points[0].Id || here == points[1].Id) &&
                (reached.Count == 0 || reached[^1] != here))
                reached.Add(here);
        }

        Assert.That(reached, Is.EqualTo(new[] { points[0].Id, points[1].Id }));
        Assert.That(npc.Mind.PlayerOrders.Active, Is.Null);
        Assert.That(ids, Has.Length.EqualTo(3));
    }

    [Test]
    public void ShiftAppendWhileOnlyWalkingFinishesTheWalkFirst()
    {
        var (engine, npc, points) = Setup();
        Assert.That(engine.ApplyManualCommand(new ReplaceMovementOrderCommand(Go(npc, points[0]))).Accepted, Is.True);
        // Shift+click: append, never replace.
        Assert.That(engine.ApplyManualCommand(new QueuePlayerOrderCommand(Go(npc, points[1]))).Accepted, Is.True);
        Assert.That(Orders(engine, npc).Select(o => (o.Action, o.Active)),
            Is.EqualTo(new[] { ("MoveTo", true), ("MoveTo", false) }));
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(points[0].Id));

        var reached = new List<JunctionId>();
        var run = new ManualOrderSystem();
        for (var i = 0; i < 2400 && (npc.Mind.PlayerOrders.Active != null || npc.Mind.PlayerOrders.Pending.Count > 0); i++)
        {
            _now += 0.25d;
            engine.Step();
            run.Run(engine.World);
            if (npc.CurrentJunction is { } here && (here == points[0].Id || here == points[1].Id) &&
                (reached.Count == 0 || reached[^1] != here))
                reached.Add(here);
        }
        Assert.That(reached, Is.EqualTo(new[] { points[0].Id, points[1].Id }));
    }
}

}
