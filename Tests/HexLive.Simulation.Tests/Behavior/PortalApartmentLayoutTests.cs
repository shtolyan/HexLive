using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class PortalApartmentLayoutTests
{
    private static readonly TileCoord[] Directions =
    {
        new(1, 0), new(0, 1), new(-1, 1), new(-1, 0), new(0, -1), new(1, -1),
    };

    private static readonly (PortalApartmentRoom Room, string[] Fixtures)[] ExpectedRooms =
    {
        (PortalApartmentRoom.Living, new[]
        {
            "furniture.modern_sofa", "furniture.modern_tv", PortalWorld.ApartmentShelfDefinitionId,
        }),
        (PortalApartmentRoom.Kitchen, new[]
        {
            ContentIds.ModernFridge, "furniture.modern_table", "furniture.modern_chair",
        }),
        (PortalApartmentRoom.Bedroom, new[] { "furniture.modern_bed", "furniture.modern_wardrobe" }),
    };

    [Test]
    public void OpeningsAreRepeatableAndChangeTheUnrotatedFootprint()
    {
        var shapesBySize = new Dictionary<int, HashSet<string>>();
        var doorwayPatterns = new HashSet<string>();
        foreach (var seed in Enumerable.Range(-15, 32).Concat(new[] { int.MinValue, int.MaxValue }))
        {
            var previousSize = 0;
            for (var serial = 1; serial <= 12; serial++)
            {
                var layout = PortalApartmentLayout.Create(seed, serial);
                var replay = PortalApartmentLayout.Create(seed, serial);
                var context = $"seed={seed}, serial={serial}";
                Assert.That(layout.Tiles, Is.EqualTo(layout.Tiles.OrderBy(tile => tile.Q).ThenBy(tile => tile.R)), context);
                Assert.That(layout.Tiles, Is.EqualTo(replay.Tiles), context);
                Assert.That(layout.Tiles.Select(layout.RoomAt), Is.EqualTo(replay.Tiles.Select(replay.RoomAt)), context);
                Assert.That(Openings(layout), Is.EqualTo(Openings(replay)), context);
                Assert.That(layout.Furniture.Select(FurnitureKey), Is.EqualTo(replay.Furniture.Select(FurnitureKey)), context);
                Assert.That(layout.Tiles.Count, Is.Not.EqualTo(previousSize), context);
                previousSize = layout.Tiles.Count;

                if (!shapesBySize.TryGetValue(previousSize, out var shapes))
                    shapesBySize.Add(previousSize, shapes = new HashSet<string>());
                shapes.Add(WithoutRotation(layout.Tiles.Select(Local)));
                doorwayPatterns.Add(WithoutRotation(Openings(layout).SelectMany(edge =>
                    new[] { Local(edge.First), Local(edge.Second) })));
            }
        }
        Assert.That(shapesBySize.Keys.OrderBy(size => size), Is.EqualTo(new[] { 20, 21, 22, 23, 24 }));
        Assert.That(shapesBySize[24].Count, Is.GreaterThan(10));
        Assert.That(doorwayPatterns.Count, Is.GreaterThan(5));

        var previous = PortalApartmentLayout.Create(42, int.MaxValue - 1);
        var last = PortalApartmentLayout.Create(42, int.MaxValue);
        Assert.That(last.Tiles.Count, Is.Not.EqualTo(previous.Tiles.Count));
        Assert.That(() => PortalApartmentLayout.Create(42, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void RoomsRemainConnectedWithClearDoorwaysWindowsAndFurnitureAcrossManyVisits()
    {
        for (var seed = -32; seed < 64; seed++)
        for (var serial = 1; serial <= 16; serial++)
        {
            var layout = PortalApartmentLayout.Create(seed, serial);
            var context = $"seed={seed}, serial={serial}";
            Assert.That(layout.Tiles.All(tile =>
                HexSpatialMath.HexDistance(tile, PortalWorld.ApartmentCenter) <= PortalApartmentLayout.Radius), context);
            Assert.That(layout.Furniture.Count, Is.EqualTo(8), context);
            AssertExteriorIsContinuous(layout, context);
            var doors = Openings(layout);
            Assert.That(doors.Count, Is.EqualTo(2), context);
            foreach (var (first, second) in doors)
            {
                Assert.That(layout.HasWall(second, first), Is.False, context);
                Assert.That(layout.IsFurnitureTile(first) || layout.IsFurnitureTile(second), Is.False, context);
                Assert.That(HasJunctionInDoorway(first, second), Is.True, context);
            }

            var walkable = layout.Tiles.Where(tile => !layout.IsFurnitureTile(tile)).ToHashSet();
            Assert.That(Reachable(walkable, layout.HasWall), Is.EqualTo(walkable.Count), context);
            foreach (var (room, fixtures) in ExpectedRooms)
            {
                var roomTiles = layout.Tiles.Where(tile => layout.RoomAt(tile) == room).ToHashSet();
                Assert.That(Reachable(roomTiles, layout.HasWall), Is.EqualTo(roomTiles.Count), context);
                var roomWalkable = roomTiles.Where(walkable.Contains).ToHashSet();
                Assert.That(Reachable(roomWalkable, layout.HasWall), Is.EqualTo(roomWalkable.Count), context);
                Assert.That(roomWalkable.Count(tile => Neighbors(tile).Any(neighbor =>
                    layout.RoomAt(neighbor) == PortalApartmentRoom.None && layout.HasWall(tile, neighbor))),
                    Is.GreaterThanOrEqualTo(2), context);
                Assert.That(layout.Furniture.Where(piece => roomTiles.Contains(piece.Tile)).Select(piece => piece.DefinitionId),
                    Is.EquivalentTo(fixtures), context);
            }

            foreach (var piece in layout.Furniture)
            {
                Assert.That(layout.IsFurnitureTile(piece.Tile), context);
                Assert.That(HexSpatialMath.WorldToTile(piece.Position), Is.EqualTo(piece.Tile), context);
                Assert.That(Neighbors(piece.Tile).Any(neighbor =>
                    walkable.Contains(neighbor) && !layout.HasWall(piece.Tile, neighbor)), context);
            }

            AssertFurniturePlacement(layout, context);
        }
    }

    private static void AssertFurniturePlacement(PortalApartmentLayout layout, string context)
    {
        var table = Fixture(layout, "furniture.modern_table");
        var chair = Fixture(layout, "furniture.modern_chair");
        Assert.That(chair.Tile, Is.EqualTo(table.Tile), context);
        Assert.That(HexSpatialMath.Distance(chair.Position, table.Position), Is.InRange(.90f, 1.10f), context);
        Assert.That(NearestInteriorSlot(chair), Is.Not.EqualTo(NearestInteriorSlot(table)), context);
        AssertFaces(chair, table, 90f, context);
        var sofa = Fixture(layout, "furniture.modern_sofa");
        var tv = Fixture(layout, "furniture.modern_tv");
        AssertFaces(sofa, tv, 0f, context);
        AssertFaces(tv, sofa, 0f, context);
        foreach (var piece in layout.Furniture.Where(piece => piece.DefinitionId != chair.DefinitionId))
        {
            var fromCenter = piece.Position - HexSpatialMath.TileToWorld(piece.Tile);
            var direction = Directions.MaxBy(direction =>
                Dot(fromCenter, HexSpatialMath.TileToWorld(direction)));
            var outside = Add(piece.Tile, direction);
            Assert.That(layout.RoomAt(outside), Is.EqualTo(PortalApartmentRoom.None), context);
            Assert.That(layout.HasWall(piece.Tile, outside), context);
        }
        var wardrobe = Fixture(layout, "furniture.modern_wardrobe");
        var wardrobeOffset = wardrobe.Position - HexSpatialMath.TileToWorld(wardrobe.Tile);
        Assert.That(HexSpatialMath.Distance(wardrobeOffset, Float2.Zero),
            Is.EqualTo(HexSpatialMath.HexApothem - .210f - .10f).Within(.001f), context);
        Assert.That(Dot(Unit(wardrobe.RotationDegrees + 180f), HexSpatialMath.Normalize(wardrobeOffset)),
            Is.GreaterThan(.999f), context);
    }

    private static void AssertFaces(PortalApartmentLayout.FurniturePlacement seat,
        PortalApartmentLayout.FurniturePlacement target, float modelFacing, string context)
    {
        var facing = Unit(seat.RotationDegrees + modelFacing);
        Assert.That(Dot(facing, HexSpatialMath.Normalize(target.Position - seat.Position)),
            Is.GreaterThan(.999f), context);
    }

    private static PortalApartmentLayout.FurniturePlacement Fixture(PortalApartmentLayout layout, string id) =>
        layout.Furniture.Single(piece => piece.DefinitionId == id);

    private static int NearestInteriorSlot(PortalApartmentLayout.FurniturePlacement piece) =>
        HexPointLayout.GetInteriorTemplates().MinBy(template =>
            HexSpatialMath.Distance(HexSpatialMath.PointToWorld(piece.Tile, template.Offset), piece.Position)).Slot;

    private static (string, TileCoord, Float2, float) FurnitureKey(PortalApartmentLayout.FurniturePlacement piece) =>
        (piece.DefinitionId, piece.Tile, piece.Position, piece.RotationDegrees);

    private static List<(TileCoord First, TileCoord Second)> Openings(PortalApartmentLayout layout) =>
        (from tile in layout.Tiles
         from neighbor in Neighbors(tile)
         where (tile.Q < neighbor.Q || tile.Q == neighbor.Q && tile.R < neighbor.R) &&
               layout.RoomAt(neighbor) != PortalApartmentRoom.None &&
               layout.RoomAt(tile) != layout.RoomAt(neighbor) && !layout.HasWall(tile, neighbor)
         select (tile, neighbor)).ToList();

    private static bool HasJunctionInDoorway(TileCoord first, TileCoord second)
    {
        var templates = HexPointLayout.GetBoundaryTemplates();
        var keys = templates.Select(template => HexPointLayout.GetJunctionKeyPair(second, template.SubAxial)).ToHashSet();
        var midpoint = (HexSpatialMath.TileToWorld(first) + HexSpatialMath.TileToWorld(second)) * .5f;
        return templates.Any(template => keys.Contains(HexPointLayout.GetJunctionKeyPair(first, template.SubAxial)) &&
            HexSpatialMath.Distance(HexSpatialMath.PointToWorld(first, template.Offset), midpoint) <= .214f);
    }

    private static int Reachable(HashSet<TileCoord> tiles, Func<TileCoord, TileCoord, bool> hasWall)
    {
        var start = tiles.First();
        var pending = new Queue<TileCoord>();
        pending.Enqueue(start);
        var reached = new HashSet<TileCoord> { start };
        while (pending.Count > 0)
        {
            var tile = pending.Dequeue();
            foreach (var neighbor in Neighbors(tile))
                if (tiles.Contains(neighbor) && !hasWall(tile, neighbor) && reached.Add(neighbor))
                    pending.Enqueue(neighbor);
        }
        return reached.Count;
    }

    private static void AssertExteriorIsContinuous(PortalApartmentLayout layout, string context)
    {
        var center = PortalWorld.ApartmentCenter;
        const int radius = PortalApartmentLayout.Radius + 1;
        var exterior = (from q in Enumerable.Range(center.Q - radius, 2 * radius + 1)
                        from r in Enumerable.Range(center.R - radius, 2 * radius + 1)
                        let tile = new TileCoord(q, r)
                        where HexSpatialMath.HexDistance(tile, center) <= radius &&
                              layout.RoomAt(tile) == PortalApartmentRoom.None
                        select tile).ToHashSet();
        Assert.That(Reachable(exterior, (_, _) => false), Is.EqualTo(exterior.Count), context);
    }

    private static string WithoutRotation(IEnumerable<TileCoord> tiles)
    {
        var current = tiles.ToArray();
        var rotations = new string[Directions.Length];
        for (var turn = 0; turn < rotations.Length; turn++)
        {
            rotations[turn] = string.Join(";", current.OrderBy(tile => tile.Q).ThenBy(tile => tile.R));
            for (var i = 0; i < current.Length; i++)
                current[i] = new TileCoord(-current[i].R, current[i].Q + current[i].R);
        }
        return rotations.Min(StringComparer.Ordinal);
    }

    private static TileCoord Local(TileCoord tile) =>
        new(tile.Q - PortalWorld.ApartmentCenter.Q, tile.R - PortalWorld.ApartmentCenter.R);

    private static Float2 Unit(float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        return new Float2(MathF.Cos(radians), MathF.Sin(radians));
    }

    private static float Dot(Float2 a, Float2 b) => a.X * b.X + a.Y * b.Y;

    private static IEnumerable<TileCoord> Neighbors(TileCoord tile) =>
        Directions.Select(direction => Add(tile, direction));

    private static TileCoord Add(TileCoord a, TileCoord b) => new(a.Q + b.Q, a.R + b.R);
}

}
