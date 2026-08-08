using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§113 r2: full-body ground placement on the 37-node sub-grid.</summary>
public sealed class LyingSpotTests
{
    private static NPCState Girl(WorldState world) => world.Entities.Npcs.Values.First();

    private static void MoveOtherNpcsAway(WorldState world, NPCState girl)
    {
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)))
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }
    }

    private static WorldObjectState SpawnAtCentre(
        WorldState world, NPCState girl, string definitionId)
    {
        var centre = StructurePlacement.CenterJunction(world, girl.Tile);
        Assert.That(centre, Is.Not.Null);
        return WorldObjectMutations.SpawnObject(
            world, definitionId, girl.Fragment, girl.Tile, centre.Value);
    }

    private static float ClearanceFromObject(
        WorldObjectState thing, WorldState world, NPCState girl)
    {
        Assert.That(LyingSpot.TryAnchor(world, thing, out var anchor), Is.True);
        var radius = LyingSpot.SolidRadius(world, thing);
        var d = anchor - girl.Position;
        var radians = girl.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        var outsideAlong = System.MathF.Max(
            System.MathF.Abs(d.X * forward.X + d.Y * forward.Y) -
            LyingSpot.BodyHalfLength, 0f);
        var outsideSide = System.MathF.Max(
            System.MathF.Abs(d.X * lateral.X + d.Y * lateral.Y) -
            LyingSpot.BodyHalfWidth, 0f);
        return System.MathF.Sqrt(outsideAlong * outsideAlong + outsideSide * outsideSide) - radius;
    }

    [Test]
    public void EmptyHex_UsesTheNearestInteriorGridNode()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var before = girl.Position;

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);

        var nearest = HexPointLayout.GetInteriorTemplates()
            .Select(t => HexSpatialMath.TileToWorld(girl.Tile) + t.Offset)
            .Min(p => HexSpatialMath.Distance(p, before));
        Assert.That(HexSpatialMath.Distance(girl.Position, before),
            Is.EqualTo(nearest).Within(0.001f));
        Assert.That(world.Events.Items.Any(e =>
            e.Type == "LieDownSpot" && e.Message.Contains("Fit=Clear")), Is.True);
    }

    [Test]
    public void Campfire_NoPartOfTheBodyEntersTheFire()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var fire = SpawnAtCentre(world, girl, ContentIds.Campfire);
        var tile = girl.Tile;

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);

        Assert.That(girl.Tile, Is.EqualTo(tile), "Choosing a pose is not movement to another tile.");
        Assert.That(ClearanceFromObject(fire, world, girl), Is.GreaterThanOrEqualTo(-0.001f));
    }

    [Test]
    public void Boulder_NoPartOfTheBodyEntersItsSolidRadius()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var boulder = SpawnAtCentre(world, girl, "rock.boulder");

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);
        Assert.That(ClearanceFromObject(boulder, world, girl), Is.GreaterThanOrEqualTo(-0.001f));
    }

    [Test]
    public void CliffEdge_MovesTheBodyToAWhollySupportedNode()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var eastCoord = new TileCoord(girl.Tile.Q + 1, girl.Tile.R);
        Assert.That(world.Tiles.Items.TryGetValue(eastCoord, out var east), Is.True);
        east.Elevation = world.Tiles.Items[girl.Tile].Elevation - 2;

        var eastMost = HexPointLayout.GetInteriorTemplates()
            .OrderByDescending(t => t.Offset.X)
            .ThenBy(t => t.Slot)
            .First();
        girl.Position = HexSpatialMath.TileToWorld(girl.Tile) + eastMost.Offset;
        girl.RotationDegrees = 0f;
        var unsafeX = girl.Position.X;

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);
        var radians = girl.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        Assert.That(LyingSpot.BodyClear(
            world, girl, girl.Tile, girl.Position, forward, lateral), Is.True,
            "The selected pose must keep the whole body on level support; a rim node is " +
            "valid when the solver turns the body parallel to the cliff.");
    }

    [Test]
    public void TwoBodies_AreSeparatedWithoutSharedHeadingOrBerthSlots()
    {
        var world = TestWorld.CreateWorld();
        var all = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList();
        var first = all[0];
        var second = all[1];
        foreach (var other in all.Skip(2))
        {
            other.Tile = new TileCoord(first.Tile.Q + 4, first.Tile.R);
        }

        second.Tile = first.Tile;
        first.Mind.FaintedUntilTick = world.Tick + 100;
        second.Mind.FaintedUntilTick = world.Tick + 100;
        Assert.That(ExecutionSystem.TryLieDownOnGround(world, first), Is.True);
        Assert.That(ExecutionSystem.TryLieDownOnGround(world, second), Is.True);

        var radians = second.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        Assert.That(LyingSpot.BodyClear(
            world, second, second.Tile, second.Position, forward, lateral), Is.True);
        Assert.That(HexSpatialMath.Distance(first.Position, second.Position), Is.GreaterThan(0.1f));
    }

    [Test]
    public void FullyBlockedHex_ReturnsNoSpaceInsteadOfStacking()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var before = girl.Position;
        foreach (var junctionId in world.Tiles.Items[girl.Tile].Junctions)
        {
            world.Junctions.Items[junctionId].Blocked = true;
        }

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.False);
        Assert.That(girl.Position, Is.EqualTo(before));
        Assert.That(world.Events.Items.Any(e =>
            e.Type == "LieDownSpot" && e.Message.Contains("Fit=NoSpace")), Is.True);
    }
}

}
