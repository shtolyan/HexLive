using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>Deterministic floor plan and furnishing positions for one portal opening.</summary>
public sealed class PortalApartmentLayout
{
    public const int Radius = 4;

    private const int SizeVariants = 5;
    private const int GridWidth = 2 * Radius + 1;
    private const int GridArea = GridWidth * GridWidth;
    private const float WallClearance = .10f;

    private static readonly TileCoord[] Directions =
    {
        new(1, 0), new(0, 1), new(-1, 1), new(-1, 0), new(0, -1), new(1, -1),
    };

    private static readonly (PortalApartmentRoom Room, TileCoord[] Tiles, int WallSlots)[] RoomSeeds =
    {
        (PortalApartmentRoom.Living, new TileCoord[]
        {
            new(0, 0), new(-1, 0), new(-2, 0), new(-2, 1),
            new(-1, 1), new(0, 1), new(-2, 2), new(-1, 2),
        }, 3),
        (PortalApartmentRoom.Kitchen, new TileCoord[]
        {
            new(0, -1), new(1, -1), new(0, -2), new(1, -2), new(0, -3), new(1, -3),
        }, 2),
        (PortalApartmentRoom.Bedroom, new TileCoord[]
        {
            new(1, 0), new(2, 0), new(1, 1), new(2, 1), new(3, 0), new(3, -1),
        }, 2),
    };

    // These cells prove the room seeds can always be furnished. Keep their
    // outside edges free during growth, and keep these cells out of doorways.
    // The seeded wall search may use other cells and edges for actual furniture.
    private static readonly (TileCoord Tile, TileCoord Outside)[] GuaranteedWallSlots =
    {
        (new(-2, 2), new(-3, 2)), (new(-1, 2), new(0, 2)), (new(-2, 0), new(-3, 0)),
        (new(0, -3), new(0, -4)), (new(1, -3), new(1, -4)),
        (new(3, 0), new(4, 0)), (new(2, 1), new(2, 2)),
    };

    // Two clear outside tiles per room even if every guaranteed slot is used.
    private static readonly HashSet<TileCoord> ReservedOutside = new(
        GuaranteedWallSlots.Select(slot => slot.Outside).Concat(new TileCoord[]
        {
            new(-3, 1), new(-1, -2), new(2, -3), new(1, 2), new(4, -1),
        }));

    private static readonly HashSet<TileCoord> GuaranteedFurnitureTiles =
        new(GuaranteedWallSlots.Select(slot => slot.Tile));

    private static readonly TileCoord[] LocalDisk = (
        from q in Enumerable.Range(-Radius, GridWidth)
        from r in Enumerable.Range(-Radius, GridWidth)
        let tile = new TileCoord(q, r)
        where HexSpatialMath.HexDistance(TileCoord.Zero, tile) <= Radius
        select tile).ToArray();

    private readonly Dictionary<TileCoord, PortalApartmentRoom> _rooms;
    private readonly HashSet<(TileCoord, TileCoord)> _doorways;
    private readonly HashSet<TileCoord> _furnitureTiles;

    private PortalApartmentLayout(Dictionary<TileCoord, PortalApartmentRoom> rooms,
        IEnumerable<(TileCoord, TileCoord)> doorways, List<FurniturePlacement> furniture)
    {
        _rooms = rooms;
        _doorways = new HashSet<(TileCoord, TileCoord)>(doorways);
        _furnitureTiles = new HashSet<TileCoord>(furniture.Select(piece => piece.Tile));
        Tiles = Array.AsReadOnly(rooms.Keys.OrderBy(tile => tile.Q).ThenBy(tile => tile.R).ToArray());
        Furniture = furniture.AsReadOnly();
    }

    public IReadOnlyList<TileCoord> Tiles { get; }
    public IReadOnlyList<FurniturePlacement> Furniture { get; }

    public PortalApartmentRoom RoomAt(TileCoord tile) =>
        _rooms.TryGetValue(tile, out var room) ? room : PortalApartmentRoom.None;

    public bool HasWall(TileCoord tile, TileCoord neighbor)
    {
        var room = RoomAt(tile);
        return room != PortalApartmentRoom.None && HexSpatialMath.HexDistance(tile, neighbor) == 1 &&
            RoomAt(neighbor) != room && !_doorways.Contains((tile, neighbor)) &&
            !_doorways.Contains((neighbor, tile));
    }

    public bool IsFurnitureTile(TileCoord tile) => _furnitureTiles.Contains(tile);

    internal static PortalApartmentLayout Create(int seed, int visitSerial)
    {
        if (visitSerial < 1) throw new ArgumentOutOfRangeException(nameof(visitSerial));

        var rooms = new Dictionary<TileCoord, PortalApartmentRoom>();
        foreach (var room in RoomSeeds)
        foreach (var tile in room.Tiles)
            rooms.Add(tile, room.Room);

        // Area cycles on the serial. The other rolls can never make consecutive
        // openings the same size, including openings in the same in-game day.
        var sizePhase = (int)(MathUtil.Hash01(seed, 0, 17620) * SizeVariants);
        var additions = ((visitSerial - 1) % SizeVariants + sizePhase) % SizeVariants;
        GrowRooms(rooms, seed, visitSerial, additions);

        var doors = new[]
        {
            ChooseDoor(rooms, PortalApartmentRoom.Kitchen, seed, visitSerial),
            ChooseDoor(rooms, PortalApartmentRoom.Bedroom, seed, visitSerial),
        };
        var doorTiles = new HashSet<TileCoord>(doors.SelectMany(door => new[] { door.Living, door.Other }));
        var rotation = (int)(MathUtil.Hash01(seed, visitSerial, 17621) * Directions.Length);
        var furniture = new List<FurniturePlacement>();
        foreach (var room in RoomSeeds)
        {
            var walls = ChooseFurnitureWalls(rooms, room.Room, room.WallSlots, doorTiles, seed, visitSerial);
            switch (room.Room)
            {
                case PortalApartmentRoom.Living:
                    furniture.Add(AgainstWall("furniture.modern_sofa", walls[0], .405f, 180f, rotation));
                    furniture.Add(AgainstWall("furniture.modern_tv", walls[1], .216f, 180f, rotation));
                    furniture.Add(AgainstWall(PortalWorld.ApartmentShelfDefinitionId, walls[2], .15f, 180f, rotation));
                    break;
                case PortalApartmentRoom.Kitchen:
                    furniture.Add(AgainstWall(ContentIds.ModernFridge, walls[0], .326f, -90f, rotation));
                    furniture.Add(AgainstWall("furniture.modern_table", walls[1], .38f, 180f, rotation));
                    var table = walls[1];
                    var normal = WallNormal(table.Direction, rotation);
                    var chairTile = InApartment(table.Tile, rotation);
                    furniture.Add(new FurniturePlacement("furniture.modern_chair", chairTile,
                        HexSpatialMath.TileToWorld(chairTile) - normal * .18f, Facing(normal, 90f)));
                    break;
                case PortalApartmentRoom.Bedroom:
                    furniture.Add(AgainstWall("furniture.modern_bed", walls[0], .904f, -90f, rotation));
                    // 0.826 wide and 0.420 deep. The prefab's back points along Unity -X.
                    furniture.Add(AgainstWall("furniture.modern_wardrobe", walls[1], .210f, 180f, rotation));
                    break;
            }
        }

        return new PortalApartmentLayout(
            rooms.ToDictionary(pair => InApartment(pair.Key, rotation), pair => pair.Value),
            doors.Select(door => (InApartment(door.Living, rotation), InApartment(door.Other, rotation))),
            furniture);
    }

    private static void GrowRooms(Dictionary<TileCoord, PortalApartmentRoom> rooms,
        int seed, int visitSerial, int additions)
    {
        for (var step = 0; step < additions; step++)
        {
            var candidates = new List<(TileCoord Tile, PortalApartmentRoom Room, float Score)>();
            foreach (var tile in LocalDisk)
            {
                if (rooms.ContainsKey(tile) || ReservedOutside.Contains(tile)) continue;
                foreach (var room in RoomSeeds)
                {
                    var shared = Neighbors(tile).Count(neighbor =>
                        !GuaranteedFurnitureTiles.Contains(neighbor) &&
                        rooms.TryGetValue(neighbor, out var neighborRoom) && neighborRoom == room.Room);
                    if (shared == 0) continue;
                    var score = MathUtil.Hash01(seed, visitSerial, TileKey(tile), 17630 + step * 3 + (int)room.Room) +
                        .15f * (shared - 1);
                    candidates.Add((tile, room.Room, score));
                }
            }
            var grew = false;
            foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
            {
                rooms.Add(candidate.Tile, candidate.Room);
                if (HasOpenExterior(rooms))
                {
                    grew = true;
                    break;
                }
                rooms.Remove(candidate.Tile);
            }
            if (!grew)
                throw new InvalidOperationException("No connected room growth remains inside the apartment radius.");
        }
    }

    private static bool HasOpenExterior(Dictionary<TileCoord, PortalApartmentRoom> rooms)
    {
        // Beyond the construction disk a full empty ring represents the
        // outside. A frontier cell may not seal a hole inside the apartment.
        const int outsideRadius = Radius + 1;
        const int cellCount = 3 * outsideRadius * (outsideRadius + 1) + 1;
        var outside = new TileCoord(outsideRadius, 0);
        var visited = new HashSet<TileCoord> { outside };
        var pending = new Queue<TileCoord>();
        pending.Enqueue(outside);
        while (pending.Count > 0)
        {
            foreach (var neighbor in Neighbors(pending.Dequeue()))
                if (HexSpatialMath.HexDistance(neighbor, TileCoord.Zero) <= outsideRadius &&
                    !rooms.ContainsKey(neighbor) && visited.Add(neighbor))
                    pending.Enqueue(neighbor);
        }
        return visited.Count == cellCount - rooms.Count;
    }

    private static (TileCoord Living, TileCoord Other) ChooseDoor(
        Dictionary<TileCoord, PortalApartmentRoom> rooms, PortalApartmentRoom otherRoom, int seed, int visitSerial)
    {
        return (
            from living in LocalDisk
            where IsFreeRoomCell(living, PortalApartmentRoom.Living)
            from other in Neighbors(living)
            where IsFreeRoomCell(other, otherRoom)
            orderby MathUtil.Hash01(seed, visitSerial,
                TileKey(living) * GridArea + TileKey(other), 17640 + (int)otherRoom)
            select (living, other)).First();

        bool IsFreeRoomCell(TileCoord tile, PortalApartmentRoom room) =>
            !GuaranteedFurnitureTiles.Contains(tile) && rooms.TryGetValue(tile, out var found) && found == room;
    }

    private static IReadOnlyList<(TileCoord Tile, TileCoord Direction)> ChooseFurnitureWalls(
        Dictionary<TileCoord, PortalApartmentRoom> rooms, PortalApartmentRoom room, int count,
        HashSet<TileCoord> doorTiles, int seed, int visitSerial)
    {
        var tiles = LocalDisk.Where(tile => rooms.TryGetValue(tile, out var found) && found == room).ToArray();
        var exteriorTiles = new HashSet<TileCoord>(tiles.Where(tile =>
            Neighbors(tile).Any(neighbor => !rooms.ContainsKey(neighbor))));
        var candidates = (
            from tile in tiles
            where !doorTiles.Contains(tile)
            from direction in Directions
            where !rooms.ContainsKey(Add(tile, direction))
            orderby MathUtil.Hash01(seed, visitSerial,
                TileKey(tile) * GridArea + TileKey(direction), 17650 + (int)room)
            select (Tile: tile, Direction: direction)).ToArray();
        var selected = new List<(TileCoord Tile, TileCoord Direction)>(count);
        var occupied = new HashSet<TileCoord>();
        if (!ChooseNext())
            throw new InvalidOperationException($"No usable furniture arrangement for the apartment {room}.");
        return selected;

        bool ChooseNext()
        {
            if (selected.Count == count) return HasRoomAccess(tiles, exteriorTiles, occupied);
            foreach (var candidate in candidates)
            {
                if (room == PortalApartmentRoom.Living && selected.Count == 1 &&
                    !FaceEachOther(selected[0], candidate)) continue;
                if (!occupied.Add(candidate.Tile)) continue;
                selected.Add(candidate);
                if (ChooseNext()) return true;
                selected.RemoveAt(selected.Count - 1);
                occupied.Remove(candidate.Tile);
            }
            return false;
        }
    }

    private static bool HasRoomAccess(TileCoord[] tiles, HashSet<TileCoord> exterior, HashSet<TileCoord> occupied)
    {
        var free = new HashSet<TileCoord>(tiles.Where(tile => !occupied.Contains(tile)));
        if (free.Count(exterior.Contains) < 2 ||
            occupied.Any(tile => !Neighbors(tile).Any(free.Contains))) return false;

        var pending = new Queue<TileCoord>();
        pending.Enqueue(free.First());
        free.Remove(pending.Peek());
        while (pending.Count > 0)
        {
            foreach (var neighbor in Neighbors(pending.Dequeue()))
                if (free.Remove(neighbor)) pending.Enqueue(neighbor);
        }
        return free.Count == 0;
    }

    private static bool FaceEachOther((TileCoord Tile, TileCoord Direction) first,
        (TileCoord Tile, TileCoord Direction) second)
    {
        var distance = HexSpatialMath.HexDistance(first.Tile, second.Tile);
        return Add(first.Direction, second.Direction) == TileCoord.Zero &&
            Add(first.Tile, new TileCoord(second.Direction.Q * distance, second.Direction.R * distance)) == second.Tile;
    }

    private static FurniturePlacement AgainstWall(string id, (TileCoord Tile, TileCoord Direction) wall,
        float halfDepth, float backAngle, int rotation)
    {
        var tile = InApartment(wall.Tile, rotation);
        var normal = WallNormal(wall.Direction, rotation);
        var position = HexSpatialMath.TileToWorld(tile) +
            normal * (HexSpatialMath.HexApothem - WallClearance - halfDepth);
        return new FurniturePlacement(id, tile, position, Facing(normal, backAngle));
    }

    private static float Facing(Float2 normal, float prefabAngle)
    {
        var axis = MathF.Round(HexSpatialMath.AngleDegrees(normal) / 60f) * 60f;
        var degrees = (axis - prefabAngle + 720f) % 360f;
        return degrees == 0f ? 360f : degrees;
    }

    private static Float2 WallNormal(TileCoord direction, int rotation) =>
        HexSpatialMath.Normalize(HexSpatialMath.TileToWorld(Rotate(direction, rotation)));

    private static TileCoord InApartment(TileCoord local, int rotation) =>
        Add(PortalWorld.ApartmentCenter, Rotate(local, rotation));

    private static TileCoord Rotate(TileCoord tile, int steps)
    {
        for (var i = 0; i < steps; i++) tile = new TileCoord(-tile.R, tile.Q + tile.R);
        return tile;
    }

    private static IEnumerable<TileCoord> Neighbors(TileCoord tile)
    {
        foreach (var direction in Directions) yield return Add(tile, direction);
    }

    private static TileCoord Add(TileCoord a, TileCoord b) => new(a.Q + b.Q, a.R + b.R);

    private static int TileKey(TileCoord tile) => (tile.Q + Radius) * GridWidth + tile.R + Radius;

    public readonly struct FurniturePlacement
    {
        internal FurniturePlacement(string definitionId, TileCoord tile, Float2 position, float rotationDegrees)
        {
            DefinitionId = definitionId;
            Tile = tile;
            Position = position;
            RotationDegrees = rotationDegrees;
        }

        public string DefinitionId { get; }
        public TileCoord Tile { get; }
        public Float2 Position { get; }
        public float RotationDegrees { get; }
    }
}

}
