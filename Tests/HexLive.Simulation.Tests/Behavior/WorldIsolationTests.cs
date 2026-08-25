using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>Derived AI caches must never retain entities from another world.</summary>
public sealed class WorldIsolationTests
{
    [Test]
    public void NearestJunctionReturnsNullWhenWorldHasNoNavigationGraph()
    {
        var world = new WorldState();

        Assert.That(
            SpatialQueries.FindNearestJunction(world, new Float2(100f, 100f)),
            Is.Null);
    }

    [Test]
    public void LedgeCacheIsOwnedByWorldNotSharedTopologyNumber()
    {
        var first = TestWorld.CreateWorld(209759);
        var second = TestWorld.CreateWorld(509687);
        Assert.That(first.TopologyVersion, Is.EqualTo(second.TopologyVersion),
            "Fresh islands must exercise the equal-version cache collision.");

        var firstNpc = first.Entities.Npcs.Values.First();
        firstNpc.CurrentJunction ??=
            SpatialQueries.FindNearestJunction(first, firstNpc.Position);
        Assert.That(firstNpc.CurrentJunction, Is.Not.Null);
        DecisionSystem.AnyLedgeNear(first, firstNpc, float.MaxValue);
        var expectedFirst = first.Junctions.Items.Values
            .Where(j => PlanningSystem.IsLedge(first, j))
            .Select(j => j.Id).ToHashSet();

        var secondNpc = second.Entities.Npcs.Values.First();
        secondNpc.CurrentJunction ??=
            SpatialQueries.FindNearestJunction(second, secondNpc.Position);
        Assert.That(secondNpc.CurrentJunction, Is.Not.Null);
        DecisionSystem.AnyLedgeNear(second, secondNpc, float.MaxValue);
        var expectedSecond = second.Junctions.Items.Values
            .Where(j => PlanningSystem.IsLedge(second, j))
            .Select(j => j.Id).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(first.Caches.LedgeJunctions, Is.EquivalentTo(expectedFirst));
            Assert.That(second.Caches.LedgeJunctions, Is.EquivalentTo(expectedSecond));
            Assert.That(ReferenceEquals(
                first.Caches.LedgeJunctions, second.Caches.LedgeJunctions), Is.False,
                "Equal topology versions must still own independent cache storage.");
        });
    }

    [Test]
    public void StuckSampleUsesLiveWatchdogExclusions()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;

        npc.Mind.CurrentGoal = GoalType.Idle;
        Assert.That(StuckDiagnosticSystem.CountsAsIdleWithGoal(world, npc), Is.False);

        npc.Mind.CurrentGoal = GoalType.Sleep;
        npc.Mind.ComaCause = ComaCause.Exhaustion;
        Assert.That(StuckDiagnosticSystem.CountsAsIdleWithGoal(world, npc), Is.False);

        npc.Mind.ComaCause = ComaCause.None;
        npc.Mind.CurrentGoal = GoalType.GatherWood;
        Assert.That(StuckDiagnosticSystem.CountsAsIdleWithGoal(world, npc), Is.True);
    }

    [Test]
    public void DangerRingTickCacheIsOwnedByItsWorld()
    {
        var withMobs = TestWorld.CreateWorld(209759);
        var empty = TestWorld.CreateWorld(509687);
        var mobJunction = withMobs.Junctions.Items.Values
            .First(j => !j.Blocked && j.Tiles.Count > 0);
        withMobs.Mobs.Add(new MobState
        {
            Id = int.MaxValue - 5,
            Junction = mobJunction.Id,
            Tile = mobJunction.Tiles[0],
            Position = mobJunction.WorldPosition,
            Health = 1f
        });
        empty.Mobs.Clear();
        Assert.That(withMobs.Tick, Is.EqualTo(empty.Tick));

        var firstRing = PathfindingSystem.DangerRing(withMobs);
        var emptyRing = PathfindingSystem.DangerRing(empty);

        Assert.That(firstRing, Is.Not.Empty);
        Assert.That(emptyRing, Is.Empty,
            "An equal tick must not reuse another world's mob danger ring.");
        Assert.That(ReferenceEquals(firstRing, emptyRing), Is.False);
    }
}

}
