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
    public void LedgeQueryAnswersFromItsOwnWorldOnly()
    {
        // §158.4: списка уступов больше нет — окрестность перечисляется по
        // тайлам самого мира, так что чужой остров подмешаться не может по
        // построению. Проверяем ответ против полного обхода КАЖДОГО мира.
        var first = TestWorld.CreateWorld(209759);
        var second = TestWorld.CreateWorld(509687);

        foreach (var world in new[] { first, second })
        {
            var npc = world.Entities.Npcs.Values.First();
            npc.CurrentJunction ??= SpatialQueries.FindNearestJunction(world, npc.Position);
            Assert.That(npc.CurrentJunction, Is.Not.Null);
            var from = npc.CurrentJunction.Value;
            var radius = HexSpatialMath.HexRadius * 2f;
            var expected = world.Junctions.Items.Values.Any(j =>
                !j.Blocked &&
                PlanningSystem.IsLedge(world, j) &&
                HexSpatialMath.Distance(j.WorldPosition, npc.Position) < radius &&
                PlanningSystem.TryGetEdgeSeatGeometry(world, j, waterOnly: false, out _, out _) &&
                SpatialQueries.IsJunctionFree(world, j.Id) &&
                !PlanningSystem.IsBuildSiteJunction(world, j.Id) &&
                Connectivity.Reachable(world, from, j.Id, PlanningSystem.CanUseRoutineTraversal(npc)));
            Assert.That(DecisionSystem.AnyLedgeNear(world, npc, radius), Is.EqualTo(expected),
                $"мир {world.Seed}: локальный поиск уступа разошёлся с полным обходом");
        }
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
