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

// #466: translated five-node route observed in Nika's saved camp. The route
// touches a three-tile corner, then enters the raised tile, never the third one.
[NonParallelizable]
public sealed class CornerHopRouteTests
{
    private static IEnumerable<TestCaseData> Crossings()
    {
        for (var yaw = 0; yaw < 6; yaw++)
        foreach (var up in new[] { true, false })
        foreach (var reverseMembership in new[] { false, true })
            yield return new TestCaseData(yaw, up, reverseMembership);
    }

    [TestCaseSource(nameof(Crossings))]
    public void CornerTouchKeepsTheActualTakeoffTile(int yaw, bool up, bool reverseMembership)
    {
        var (world, npc, near, far, third, path) = CreateRoute(yaw, up, reverseMembership);
        var start = npc.Position;
        new MovementSystem().Run(world);

        Assert.That(npc.Movement.HopArmed, Is.True, "The real elevation crossing must still arm.");
        Assert.That(npc.Movement.HopFromTile, Is.EqualTo(near), "The third tile is not on this route.");
        Assert.That(npc.Movement.HopTargetTile, Is.EqualTo(far));
        Assert.That(npc.Movement.HopUp, Is.EqualTo(up));
        var approach = HexSpatialMath.Normalize(npc.Movement.HopFrom - start);
        var flight = HexSpatialMath.Normalize(npc.Movement.HopTo - npc.Movement.HopFrom);
        Assert.That(approach.X * flight.X + approach.Y * flight.Y, Is.GreaterThan(0f),
            "A displaced third-tile takeoff makes the body pivot backwards before jumping.");
        var launches = 0;
        var movement = new MovementSystem();
        for (var tick = 0; tick < 80 && npc.Movement.IsMoving; tick++)
        {
            var wasFlying = npc.Movement.HopTimer > 0;
            world.Tick++;
            movement.Run(world);
            if (!wasFlying && npc.Movement.HopTimer > 0) launches++;
            Assert.That(npc.Tile, Is.Not.EqualTo(third));
            Assert.That(npc.Movement.JunctionPath, Is.EqualTo(path.Select(j => j.Id)));
        }
        Assert.That(launches, Is.EqualTo(1), "Exactly one real crossing, no rearmed third-tile hop.");
        Assert.That(npc.Movement.Status, Is.EqualTo(MovementStatus.Arrived));
        Assert.That(npc.Tile, Is.EqualTo(far));
        Assert.That(npc.Position, Is.EqualTo(path[^1].WorldPosition));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void FlatCornerRouteNeverEntersTheUnvisitedRaisedOrWaterTile(bool water, bool carrying)
    {
        var (world, npc, near, far, third, path) = CreateRoute(0, true, false);
        world.Tiles.Items[far].Elevation = world.Tiles.Items[near].Elevation;
        world.Tiles.Items[third].Elevation = water ? 0 : 2;
        world.Tiles.Items[third].Flags = water ? TileFlags.Water : TileFlags.None;
        var passenger = world.Entities.Npcs.Values.First(n => n != npc);
        if (carrying)
        {
            npc.CarriedNpcId = passenger.Id;
            passenger.CarriedByNpcId = npc.Id;
        }
        var movement = new MovementSystem();
        for (var tick = 0; tick < 80 && npc.Movement.IsMoving; tick++)
        {
            movement.Run(world);
            world.Tick++;
            Assert.That(npc.Movement.HopArmed, Is.False);
            Assert.That(npc.Movement.HopTimer, Is.Zero);
            Assert.That(npc.Movement.ClimbPauseTimer, Is.Zero, "No false swim entry at the corner.");
            Assert.That(npc.Tile, Is.Not.EqualTo(third), "Arrival and leftover steps must agree with lookahead.");
            if (carrying) Assert.That(npc.CarriedNpcId, Is.EqualTo(passenger.Id), "An unvisited water tile must not drop the passenger.");
        }
        Assert.That(npc.Movement.Status, Is.EqualTo(MovementStatus.Arrived));
        Assert.That(npc.Tile, Is.EqualTo(far));
        Assert.That(npc.Position, Is.EqualTo(path[^1].WorldPosition));
    }

    private static (WorldState world, NPCState npc, TileCoord near, TileCoord far, TileCoord third, Junction[] path)
        CreateRoute(int yaw, bool up, bool reverseMembership)
    {
        var world = TestWorld.CreateWorld();
        var farOffset = RotateTile(new TileCoord(-1, 0), yaw);
        var thirdOffset = RotateTile(new TileCoord(-1, 1), yaw);
        var near = world.Tiles.Items.Keys.First(t =>
            world.Tiles.Items.ContainsKey(Add(t, farOffset)) &&
            world.Tiles.Items.ContainsKey(Add(t, thirdOffset)));
        var far = Add(near, farOffset);
        var third = Add(near, thirdOffset);
        foreach (var tile in new[] { near, far, third })
        {
            world.Tiles.Items[tile].Flags = TileFlags.None;
            world.Tiles.Items[tile].Elevation = tile.Equals(far) ? (up ? 2 : 1) : (up ? 1 : 2);
        }
        var center = HexSpatialMath.TileToWorld(near);
        var points = new[]
        {
            HexPointLayout.ToLocalOffset(new AxialPoint(-3, 2)),
            HexPointLayout.ToLocalOffset(new AxialPoint(-3, 3)),
            HexPointLayout.ToLocalOffset(new AxialPoint(-4, 4)),
            HexSpatialMath.TileToWorld(new TileCoord(-1, 0)) + HexPointLayout.ToLocalOffset(new AxialPoint(3, 0)),
            HexSpatialMath.TileToWorld(new TileCoord(-1, 0)) + HexPointLayout.ToLocalOffset(new AxialPoint(3, -1))
        };
        var path = points.Select(p => world.Junctions.Items.Values.Single(j =>
            HexSpatialMath.Distance(j.WorldPosition, center + RotatePoint(p, yaw)) < .001f)).ToArray();
        Assert.That(path[2].Tiles, Does.Contain(near).And.Contain(far).And.Contain(third));
        for (var i = 1; i < path.Length; i++)
            Assert.That(path[i - 1].Neighbors, Does.Contain(path[i].Id), "Use actual adjacent graph nodes.");
        if (reverseMembership) path[2].Tiles.Reverse();
        foreach (var other in world.Entities.Npcs.Values)
        {
            other.CurrentJunction = null;
            other.Movement.IsMoving = false;
        }
        foreach (var mob in world.Mobs.Where(m => path.Any(j => j.Id.Equals(m.Junction)))) mob.Health = 0;
        foreach (var j in path) { j.Blocked = false; j.Door = false; }
        var npc = world.Entities.Npcs.Values.First();
        npc.Tile = near;
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
        Assert.That(npc.Body.CanJump, Is.True);

        return (world, npc, near, far, third, path);
    }

    private static TileCoord Add(TileCoord a, TileCoord b) => new(a.Q + b.Q, a.R + b.R);
    private static TileCoord RotateTile(TileCoord p, int yaw)
    {
        for (var i = 0; i < yaw; i++) p = new TileCoord(-p.R, p.Q + p.R);
        return p;
    }
    private static Float2 RotatePoint(Float2 p, int yaw)
    {
        var angle = yaw * MathF.PI / 3f;
        return new Float2(p.X * MathF.Cos(angle) - p.Y * MathF.Sin(angle),
            p.X * MathF.Sin(angle) + p.Y * MathF.Cos(angle));
    }
}
