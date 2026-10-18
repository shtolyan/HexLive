using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

// §71.11: шаг на волосок короче звена ставит тело на узел, не засчитав его.
// Кшиштоф на проде: 0.37495 wu/тик против звена 0.37499914 — хвост 5e-5 на
// каждом узле. Курс из нулевого вектора был 0°, и идущий на запад
// разворачивался кругом на месте, а потом кругом назад, 10 тиков на звено.
[NonParallelizable]
public sealed class NodeTailPivotTests
{
    private const float Tail = 0.00005f;

    [TestCase(30f)]
    [TestCase(90f)]
    [TestCase(150f)]
    [TestCase(-150f)]
    [TestCase(-90f)]
    [TestCase(-30f)]
    public void SubEpsilonTailBeforeANodeNeverTurnsTheBody(float heading)
    {
        var (world, npc, path) = CreateStraightRoute(heading);
        var direction = HexSpatialMath.Normalize(path[1].WorldPosition - path[0].WorldPosition);
        npc.Position = path[1].WorldPosition - direction * Tail;
        npc.RotationDegrees = heading;

        var movement = new MovementSystem();
        movement.Run(world);

        Assert.That(npc.Movement.Status, Is.Not.EqualTo(MovementStatus.Rotating),
            "Standing on the node is arrival, not a reason to pivot.");
        Assert.That(npc.Movement.PathIndex, Is.GreaterThanOrEqualTo(2),
            "The node under her feet must be credited this tick.");
        Assert.That(MathF.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, heading)), Is.LessThan(1f),
            "A zero-length delta has no heading; 0° from it turned her around.");

        for (var tick = 0; tick < 80 && npc.Movement.IsMoving; tick++)
        {
            world.Tick++;
            movement.Run(world);
            Assert.That(npc.Movement.Status, Is.Not.EqualTo(MovementStatus.Rotating),
                "A straight run has nothing to pivot for.");
            Assert.That(MathF.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, heading)), Is.LessThan(1f));
        }

        Assert.That(npc.Movement.Status, Is.EqualTo(MovementStatus.Arrived));
        Assert.That(npc.Position, Is.EqualTo(path[^1].WorldPosition));
    }

    // Центр тайла и три звена по прямой — всё внутри внутренней решётки r=3
    // (§20, HexPointLayout), одна высота, ни одного шва и прыжка.
    private static (WorldState world, NPCState npc, Junction[] path) CreateStraightRoute(float heading)
    {
        var world = TestWorld.CreateWorld();
        var tile = world.Tiles.Items.Keys.First(t =>
            world.Junctions.Items.Values.Any(j =>
                j.Tiles.Count == 1 && j.Tiles[0].Equals(t) &&
                HexSpatialMath.Distance(j.WorldPosition, HexSpatialMath.TileToWorld(t)) < .001f));
        world.Tiles.Items[tile].Flags = TileFlags.None;
        var center = world.Junctions.Items.Values.Single(j =>
            HexSpatialMath.Distance(j.WorldPosition, HexSpatialMath.TileToWorld(tile)) < .001f);

        var path = new List<Junction> { center };
        for (var step = 0; step < 3; step++)
        {
            var from = path[^1];
            path.Add(from.Neighbors.Select(id => world.Junctions.Items[id]).Single(j =>
                MathF.Abs(MathUtil.DeltaAngle(HexSpatialMath.AngleDegrees(
                    HexSpatialMath.Normalize(j.WorldPosition - from.WorldPosition)), heading)) < 1f));
        }

        foreach (var j in path)
        {
            Assert.That(j.Tiles, Is.EqualTo(new[] { tile }), "The route must stay inside one flat tile.");
            j.Blocked = false;
            j.Door = false;
        }

        foreach (var other in world.Entities.Npcs.Values)
        {
            other.CurrentJunction = null;
            other.Movement.IsMoving = false;
        }

        foreach (var mob in world.Mobs.Where(m => path.Any(j => j.Id.Equals(m.Junction)))) mob.Health = 0;

        var npc = world.Entities.Npcs.Values.First();
        npc.Tile = tile;
        npc.Position = path[0].WorldPosition;
        npc.CurrentJunction = path[0].Id;
        npc.Mind.WakeGraceUntilTick = 0;
        npc.Plan.Goal = GoalType.Dress;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = path[^1].Id;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.JunctionPath.AddRange(path.Select(j => j.Id));
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        return (world, npc, path.ToArray());
    }
}
