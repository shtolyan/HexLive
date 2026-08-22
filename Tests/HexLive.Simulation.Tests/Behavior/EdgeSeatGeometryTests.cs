using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class EdgeSeatGeometryTests
{
    [Test]
    public void LandSeatRequiresOneUnambiguousElevationStep_Bug192()
    {
        var world = TestWorld.CreateWorld();
        world.Tiles.Items.Clear();
        world.Junctions.Items.Clear();

        var high = new TileCoord(0, 0);
        var low = new TileCoord(1, 0);
        var thirdLevel = new TileCoord(0, 1);
        var junctionId = new JunctionId(900192);
        AddTile(world, high, elevation: 2, junctionId);
        AddTile(world, low, elevation: 1, junctionId);

        var highCenter = HexSpatialMath.TileToWorld(high);
        var lowCenter = HexSpatialMath.TileToWorld(low);
        var junction = new Junction
        {
            Id = junctionId,
            WorldPosition = (highCenter + lowCenter) * 0.5f
        };
        junction.Tiles.Add(high);
        junction.Tiles.Add(low);
        world.Junctions.Items[junctionId] = junction;

        Assert.That(PlanningSystem.TryGetEdgeSeatGeometry(
            world, junction, waterOnly: false, out var standTile, out var facing),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(standTile, Is.EqualTo(high));
            Assert.That(HexSpatialMath.Distance(facing, Float2.Zero), Is.GreaterThan(0f));
        });

        // A three-level vertex contains a tempting e2/e1 pair, but the same
        // physical point also touches e0. Starting Sit there used to leave the
        // first pose on the low side when PlaceAtEdge rejected the 2-step snap.
        AddTile(world, thirdLevel, elevation: 0, junctionId);
        junction.Tiles.Add(thirdLevel);

        Assert.That(PlanningSystem.TryGetEdgeSeatGeometry(
            world, junction, waterOnly: false, out _, out _), Is.False);
    }

    private static void AddTile(
        WorldState world, TileCoord coord, int elevation, JunctionId junction)
    {
        var tile = new Tile
        {
            Coord = coord,
            Elevation = elevation,
            Flags = TileFlags.Walkable
        };
        tile.Junctions.Add(junction);
        world.Tiles.Items[coord] = tile;
    }
}
