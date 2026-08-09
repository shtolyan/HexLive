using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class FleeRecoveryTests
{
    [Test]
    public void TerminalPathCooldownPreventsImmediateReflee()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.Invalid;
        npc.Plan.Steps.Clear();
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = GoalType.Flee,
            EndTick = world.Tick + AiBalance.FailureCooldownTicks
        });

        Assert.That(MobSystem.TryStartFlee(world, npc, attackers: 1), Is.False,
            "Pathfinding already rejected this flee. The reactive combat pass " +
            "must honor that cooldown instead of recreating the same plan immediately.");
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(npc.Plan.Steps, Is.Empty);
    }

    [Test]
    public void NoJumpFleeSkipsCloserUnreachableRefuge()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();

        world.Tiles.Items.Clear();
        world.Junctions.Items.Clear();
        world.Occupancy.JunctionOwner.Clear();
        world.Reservations.Junctions.Clear();
        world.Mobs.Clear();
        npc.Mind.Cooldowns.Clear();
        npc.Body.Parts[BodyPart.LegL] = 0.5f;
        npc.Body.Parts[BodyPart.LegR] = 0.5f;
        Assert.That(npc.Body.CanJump, Is.False);

        var startTile = new TileCoord(0, 0);
        var nearTile = new TileCoord(1, 0);
        var middleTile = new TileCoord(0, 1);
        var farTile = new TileCoord(0, 2);
        var start = new JunctionId(900001);
        var near = new JunctionId(900002);
        var middle = new JunctionId(900003);
        var far = new JunctionId(900004);

        AddTile(world, startTile, start, indoor: false);
        AddTile(world, nearTile, near, indoor: true);
        AddTile(world, middleTile, middle, indoor: false);
        AddTile(world, farTile, far, indoor: true);

        AddJunction(world, start, startTile, new Float2(0f, 0f),
            new[] { near, middle }, new sbyte[] { 1, 0 });
        AddJunction(world, near, nearTile, new Float2(1f, 0f),
            new[] { start }, new sbyte[] { -1 });
        AddJunction(world, middle, middleTile, new Float2(0f, 2f),
            new[] { start, far }, new sbyte[] { 0, 0 });
        AddJunction(world, far, farTile, new Float2(0f, 3f),
            new[] { middle }, new sbyte[] { 0 });

        npc.Tile = startTile;
        npc.CurrentJunction = start;
        npc.Position = new Float2(0f, 0f);

        Assert.That(MobSystem.TryStartFlee(world, npc, attackers: 1), Is.True);
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(far),
            "The closest indoor point requires a jump. Flee must use the farther " +
            "flat route that this injured NPC can physically traverse.");
        Assert.That(world.Reservations.Junctions[far].Owner, Is.EqualTo(npc.Id));
    }

    private static void AddTile(
        WorldState world, TileCoord coord, JunctionId junction, bool indoor)
    {
        var tile = new Tile
        {
            Coord = coord,
            Elevation = 1,
            Flags = TileFlags.Walkable | (indoor ? TileFlags.Indoor : TileFlags.None)
        };
        tile.Junctions.Add(junction);
        world.Tiles.Items[coord] = tile;
    }

    private static void AddJunction(
        WorldState world,
        JunctionId id,
        TileCoord tile,
        Float2 position,
        JunctionId[] neighbors,
        sbyte[] stepDeltas)
    {
        var junction = new Junction
        {
            Id = id,
            WorldPosition = position,
            NeighborStepDelta = stepDeltas
        };
        junction.Tiles.Add(tile);
        junction.Neighbors.AddRange(neighbors);
        world.Junctions.Items[id] = junction;
    }
}

}
