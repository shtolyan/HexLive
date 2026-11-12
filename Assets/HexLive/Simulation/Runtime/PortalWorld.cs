using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public enum PortalSpawnMode { Disabled, Camp, Random }

public enum PortalDestinationKind { None, Apartment }

public enum PortalApartmentRoom { None, Living, Kitchen, Bedroom }

// §176. The visit is durable; doorway objects and room loot use their ordinary save paths.
public sealed class PortalVisitState
{
    public PortalSpawnMode Mode { get; set; } = PortalSpawnMode.Camp;
    public Faction Camp { get; set; } = Faction.Colony;
    public int LastOpeningDay { get; set; } = -1;
    public int VisitSerial { get; set; }
    public ObjectId? ExteriorDoor { get; set; }
    public ObjectId? InteriorDoor { get; set; }
    public EntityId? Visitor { get; set; }
    public EntityId? Resident { get; set; }
    public bool OperatorOpened { get; set; }
    public List<ObjectId> GeneratedObjects { get; } = new();
}

/// <summary>§176. A portal destination is a disconnected part of the same world.</summary>
public static class PortalWorld
{
    public const string DoorDefinitionId = "portal.modern_door";
    public const string TravelInteractionId = "portal.travel";
    public const string ResidentProfileId = "portal.apartment_resident";
    public const string PlacedFurnitureVariant = "modern.placed";
    public const string ApartmentFurnitureVariant = "modern.apartment";
    private const string VisitVariantPrefix = "visit.";
    public const string ApartmentShelfDefinitionId = "furniture.modern_shelf";
    public static readonly TileCoord ApartmentCenter = new(1024, 0);
    public static readonly FragmentId ApartmentFragment = new(176);

    [ThreadStatic]
    private static (int Seed, int Serial, PortalApartmentLayout Layout) _apartmentCache;

    private static readonly (int Q, int R, PortalApartmentRoom Room)[] Layout =
    {
        (0, 0, PortalApartmentRoom.Living), (-1, 0, PortalApartmentRoom.Living),
        (0, 1, PortalApartmentRoom.Living), (-1, 1, PortalApartmentRoom.Living),
        (-2, 1, PortalApartmentRoom.Living),
        (1, -1, PortalApartmentRoom.Kitchen), (0, -1, PortalApartmentRoom.Kitchen),
        (1, -2, PortalApartmentRoom.Kitchen),
        (1, 0, PortalApartmentRoom.Bedroom), (1, 1, PortalApartmentRoom.Bedroom),
        (2, 0, PortalApartmentRoom.Bedroom), (2, -1, PortalApartmentRoom.Bedroom),
    };

    private static readonly string[] HouseholdLoot =
    {
        "tool.kitchen_knife", "tool.baseball_bat", "food.apple", "food.banana",
        "item.dollar_bills", "item.kitchen_ladle", ContentIds.Bottle,
        "item.photo_frame", "item.ballpoint_pen", "item.alarm_clock",
        "item.ceramic_saucer", ContentIds.TeddyBear,
        ContentIds.SteelHatchet, ContentIds.Flashlight, ContentIds.DisposableLighter,
        ContentIds.WindproofLighter, ContentIds.ClawHammer, ContentIds.FoldingSaw,
    };

    // The backs and depths refer to the imported prefab basis (Blender +X/+Y
    // become Unity -X/-Z). Keep walls, furniture, and its interaction anchor
    // on one authored layout, then rotate the complete apartment as a unit.
    private const float WallClearance = .10f;
    private static readonly (string Id, int Q, int R, int DQ, int DR, float Offset, float BackAngle)[] Furnishings =
    {
        ("furniture.modern_table", 1, -1, 1, -1, HexSpatialMath.HexApothem - WallClearance - .38f, 180f),
        ("furniture.modern_chair", 1, -1, -1, 1, .18f, -90f),
        ("furniture.modern_sofa", -2, 1, -1, 0, HexSpatialMath.HexApothem - WallClearance - .405f, 180f),
        ("furniture.modern_tv", 0, 1, 1, 0, HexSpatialMath.HexApothem - WallClearance - .216f, 180f),
        ("furniture.modern_fridge", 0, -2, 0, -1, HexSpatialMath.HexApothem - WallClearance - .326f, -90f),
        ("furniture.modern_bed", 2, 0, 1, 0, HexSpatialMath.HexApothem - WallClearance - .904f, -90f),
        (ApartmentShelfDefinitionId, -1, 0, -1, 0, HexSpatialMath.HexApothem - WallClearance - .15f, 180f),
    };

    private static readonly TileCoord[] EdgeDirections =
    {
        new(1, 0), new(0, 1), new(-1, 1), new(-1, 0), new(0, -1), new(1, -1),
    };

    // The reserved grid is shared by all visits and never belongs to the island.
    public static bool IsApartmentTile(TileCoord tile) =>
        HexSpatialMath.HexDistance(tile, ApartmentCenter) <= PortalApartmentLayout.Radius;

    public static PortalDestinationKind DestinationAt(TileCoord tile) => IsApartmentTile(tile)
        ? PortalDestinationKind.Apartment : PortalDestinationKind.None;

    public static PortalApartmentLayout GeneratedApartment(int seed, int visitSerial)
    {
        if (visitSerial <= 0) return null;
        if (_apartmentCache.Layout == null || _apartmentCache.Seed != seed || _apartmentCache.Serial != visitSerial)
            _apartmentCache = (seed, visitSerial, PortalApartmentLayout.Create(seed, visitSerial));
        return _apartmentCache.Layout;
    }

    public static int DoorVisitSerial(string variant) =>
        TryReadVisit(variant, VisitVariantPrefix, out var serial) ? serial : 0;

    public static bool TryGetApartmentFurnitureVisit(string variant, out int serial)
    {
        if (variant == ApartmentFurnitureVariant)
        {
            serial = 0;
            return true;
        }
        return TryReadVisit(variant, ApartmentFurnitureVariant + ".", out serial);
    }

    private static bool TryReadVisit(string variant, string prefix, out int serial)
    {
        serial = 0;
        return variant != null && variant.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(variant.Substring(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out serial) && serial > 0;
    }

    public static PortalApartmentRoom RoomAt(TileCoord tile, int seed, int visitSerial = 0)
    {
        if (GeneratedApartment(seed, visitSerial) is { } generated) return generated.RoomAt(tile);
        if (!IsApartmentTile(tile)) return PortalApartmentRoom.None;
        var local = Rotate(tile.Q - ApartmentCenter.Q, tile.R - ApartmentCenter.R, (6 - Rotation(seed)) % 6);
        foreach (var cell in Layout)
            if (cell.Q == local.Q && cell.R == local.R) return cell.Room;
        if (tile == KitchenAlcove(seed)) return PortalApartmentRoom.Kitchen;
        return PortalApartmentRoom.None;
    }

    public static IEnumerable<TileCoord> ApartmentTiles(int seed, int visitSerial = 0)
    {
        if (GeneratedApartment(seed, visitSerial) is { } generated)
        {
            foreach (var tile in generated.Tiles) yield return tile;
            yield break;
        }
        foreach (var cell in Layout) yield return At(cell.Q, cell.R, seed);
        yield return KitchenAlcove(seed);
    }

    private static TileCoord KitchenAlcove(int seed) => ((uint)seed / 6 & 1) == 0
        ? At(0, -2, seed) : At(-1, -1, seed);

    public static bool HasWall(TileCoord tile, TileCoord neighbor, int seed, int visitSerial = 0)
    {
        if (GeneratedApartment(seed, visitSerial) is { } generated) return generated.HasWall(tile, neighbor);
        var room = RoomAt(tile, seed);
        if (room == PortalApartmentRoom.None || HexSpatialMath.HexDistance(tile, neighbor) != 1) return false;
        var other = RoomAt(neighbor, seed);
        if (other == room) return false;
        return !((tile == ApartmentCenter && IsInteriorDoorway(neighbor, seed)) ||
                 (neighbor == ApartmentCenter && IsInteriorDoorway(tile, seed)));
    }

    public static int WallpaperIndex(int seed, int openingDay, PortalApartmentRoom room)
    {
        if (room == PortalApartmentRoom.None) return 0;
        var permutation = Math.Min(5, (int)(MathUtil.Hash01(seed, openingDay, 0, 17601) * 6f));
        var step = permutation < 3 ? 1 : 2;
        return (permutation % 3 + ((int)room - 1) * step) % 3;
    }

    public static bool TryGetReturnDoorEdge(TileCoord doorTile, int seed, out TileCoord exteriorNeighbor,
        int visitSerial = 0)
    {
        exteriorNeighbor = default;
        if (RoomAt(doorTile, seed, visitSerial) == PortalApartmentRoom.None ||
            IsApartmentFurnitureTile(doorTile, seed, visitSerial)) return false;
        var start = Math.Min(5, (int)(MathUtil.Hash01(seed, doorTile.Q, doorTile.R, unchecked(17607 + visitSerial)) * 6f));
        for (var i = 0; i < EdgeDirections.Length; i++)
        {
            var direction = EdgeDirections[(i + start) % EdgeDirections.Length];
            var neighbor = new TileCoord(doorTile.Q + direction.Q, doorTile.R + direction.R);
            if (RoomAt(neighbor, seed, visitSerial) != PortalApartmentRoom.None) continue;
            exteriorNeighbor = neighbor;
            return true;
        }
        return false;
    }

    public static bool TryGetWindowEdge(TileCoord tile, int seed, TileCoord exitTile, out TileCoord exteriorNeighbor,
        int visitSerial = 0)
    {
        exteriorNeighbor = default;
        var room = RoomAt(tile, seed, visitSerial);
        if (room == PortalApartmentRoom.None || IsApartmentFurnitureTile(tile, seed, visitSerial)) return false;
        var hasExitEdge = TryGetReturnDoorEdge(exitTile, seed, out var exitNeighbor, visitSerial);
        foreach (var candidate in ApartmentTiles(seed, visitSerial))
        {
            if (RoomAt(candidate, seed, visitSerial) != room || IsApartmentFurnitureTile(candidate, seed, visitSerial)) continue;
            for (var i = 0; i < EdgeDirections.Length; i++)
            {
                var direction = EdgeDirections[(EdgeDirections.Length - i) % EdgeDirections.Length];
                var neighbor = new TileCoord(candidate.Q + direction.Q, candidate.R + direction.R);
                if (RoomAt(neighbor, seed, visitSerial) != PortalApartmentRoom.None ||
                    hasExitEdge && candidate == exitTile && neighbor == exitNeighbor) continue;
                if (candidate != tile) return false;
                exteriorNeighbor = neighbor;
                return true;
            }
        }
        return false;
    }

    public static bool TryGetApartmentFurniturePlacement(string definitionId, TileCoord tile, int seed,
        out Float2 position, out float rotationDegrees, int visitSerial = 0)
    {
        position = default;
        rotationDegrees = default;
        if (GeneratedApartment(seed, visitSerial) is { } generated)
        {
            foreach (var placement in generated.Furniture)
            {
                if (placement.DefinitionId != definitionId || placement.Tile != tile) continue;
                position = placement.Position;
                rotationDegrees = placement.RotationDegrees;
                return true;
            }
            return false;
        }
        foreach (var furniture in Furnishings)
        {
            if (furniture.Id != definitionId || FurnitureTile(furniture.Q, furniture.R, seed) != tile) continue;
            var delta = Rotate(furniture.DQ, furniture.DR, Rotation(seed));
            var normal = HexSpatialMath.Normalize(HexSpatialMath.TileToWorld(new TileCoord(delta.Q, delta.R)));
            position = HexSpatialMath.TileToWorld(tile) + normal * furniture.Offset;
            var axis = MathF.Round(HexSpatialMath.AngleDegrees(normal) / 60f) * 60f;
            rotationDegrees = (axis - furniture.BackAngle + 720f) % 360f;
            if (rotationDegrees == 0f) rotationDegrees = 360f;
            return true;
        }
        return false;
    }

    public static bool TryGetModernSeatFacing(string definitionId, float rotationDegrees, out float facing)
    {
        switch (definitionId)
        {
            case "furniture.modern_chair":
                facing = (rotationDegrees + 90f) % 360f;
                return true;
            case "furniture.modern_sofa":
                facing = rotationDegrees % 360f;
                return true;
            default:
                facing = default;
                return false;
        }
    }

    private static TileCoord FurnitureTile(int q, int r, int seed) =>
        q == 0 && r == -2 ? KitchenAlcove(seed) : At(q, r, seed);

    public static bool IsApartmentFurnitureTile(TileCoord tile, int seed, int visitSerial = 0) =>
        GeneratedApartment(seed, visitSerial) is { } generated
            ? generated.IsFurnitureTile(tile)
            : Furnishings.Any(furniture => FurnitureTile(furniture.Q, furniture.R, seed) == tile);

    private static bool IsInteriorDoorway(TileCoord tile, int seed) =>
        tile == At(0, -1, seed) || tile == At(1, 0, seed);

    private static int Rotation(int seed) => (int)((uint)seed % 6);
    private static (int Q, int R) Rotate(int q, int r, int steps)
    {
        for (var i = 0; i < steps; i++) (q, r) = (-r, q + r);
        return (q, r);
    }

    private static TileCoord At(int q, int r, int seed)
    {
        var rotated = Rotate(q, r, Rotation(seed));
        return new TileCoord(ApartmentCenter.Q + rotated.Q, ApartmentCenter.R + rotated.R);
    }

    internal static FragmentBootstrap CreateApartmentFragment(int seed) => new()
    {
        Id = ApartmentFragment.Value,
        Tiles = ReservedApartmentTiles(seed).Select(tile => new TileBootstrap
        {
            Q = tile.Q, R = tile.R, Indoor = true, Elevation = 1,
        }).ToList(),
    };

    private static IEnumerable<TileCoord> ReservedApartmentTiles(int seed)
    {
        // Append to the old list so all legacy junction ids retain their order.
        var seen = new HashSet<TileCoord>();
        foreach (var tile in ApartmentTiles(seed))
            if (seen.Add(tile)) yield return tile;
        for (var radius = 0; radius <= PortalApartmentLayout.Radius; radius++)
        foreach (var tile in Ring(ApartmentCenter, radius))
            if (seen.Add(tile)) yield return tile;
    }

    internal static void RestoreApartmentWalls(WorldState world, bool preserveLegacyJunctions = false) =>
        ApplyApartmentLayout(world, 0, preserveLegacyJunctions);

    private static void ApplyApartmentLayout(WorldState world, int visitSerial, bool preserveLegacyJunctions = false)
    {
        if (!world.Fragments.Items.TryGetValue(ApartmentFragment, out var fragment)) return;
        var active = new HashSet<TileCoord>(ApartmentTiles(world.Seed, visitSerial));
        const TileFlags roomFlags = TileFlags.Walkable | TileFlags.Indoor | TileFlags.Roofed;
        foreach (var tile in fragment.Tiles.Values)
        {
            var nextFlags = active.Contains(tile.Coord)
                ? (tile.Flags & ~TileFlags.Blocked) | roomFlags
                : (tile.Flags & ~(roomFlags | TileFlags.HasFloor)) | TileFlags.Blocked;
            if (tile.Flags == nextFlags) continue;
            tile.Flags = nextFlags;
            WorldTopology.NoteTile(world, tile.Coord);
        }

        var marked = new HashSet<JunctionId>();
        foreach (var tile in fragment.Tiles.Values)
        foreach (var id in tile.Junctions)
        {
            if (!marked.Add(id)) continue;
            var point = world.Junctions.Items[id];
            if (preserveLegacyJunctions && point.Tiles.Any(active.Contains)) continue;
            WorldTopology.SetBlocked(world, point, ApartmentJunctionBlocked(point, world.Seed, visitSerial));
        }
    }

    private static bool ApartmentJunctionBlocked(Junction point, int seed, int visitSerial)
    {
        if (point.Tiles.Any(tile => RoomAt(tile, seed, visitSerial) == PortalApartmentRoom.None) ||
            point.Tiles.Count == 1 && point.Neighbors.Count < 6) return true;

        for (var i = 0; i < point.Tiles.Count; i++)
        for (var j = i + 1; j < point.Tiles.Count; j++)
        {
            var first = point.Tiles[i];
            var second = point.Tiles[j];
            if (RoomAt(first, seed, visitSerial) == RoomAt(second, seed, visitSerial)) continue;
            var midpoint = (HexSpatialMath.TileToWorld(first) + HexSpatialMath.TileToWorld(second)) * .5f;
            const float halfDoorClearWidth = .213f;
            if (HasWall(first, second, seed, visitSerial) ||
                HexSpatialMath.Distance(point.WorldPosition, midpoint) > halfDoorClearWidth + .001f)
                return true;
        }
        return false;
    }

    internal static void AddDoorDefinition(WorldState world)
    {
        if (world.Content.ObjectDefinitions.ContainsKey(DoorDefinitionId)) return;
        var definition = new ObjectDefinition { Id = DoorDefinitionId, DisplayName = "Portal" };
        definition.Tags.AddRange(new[] { "Modern", "Portal" });
        definition.Interactions.Add(new InteractionDefinition
        {
            Id = TravelInteractionId, Type = InteractionType.TravelPortal, DurationTicks = 1,
        });
        world.Content.ObjectDefinitions.Add(definition.Id, definition);
    }

    internal static int OpeningDayAt(int tick)
    {
        var midnight = MidnightTick;
        return tick < midnight ? -1 : (tick - midnight) / WorldBalance.DayLengthTicks;
    }

    private static int MidnightTick => 3 * (WorldBalance.DayLengthTicks / 4) +
        3 * (WorldBalance.DayLengthTicks % 4) / 4;

    public static bool TryParseCamp(string value, out Faction camp)
    {
        value = value?.Trim();
        if (value is { Length: 1 } && value[0] is >= '1' and <= '6')
        {
            camp = value[0] == '1' ? Faction.Colony : (Faction)(value[0] - '0');
            return true;
        }
        return Enum.TryParse(value, true, out camp) && FactionRelations.IsGirlCamp(camp) &&
            string.Equals(value, camp.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFurniture(string definitionId) =>
        definitionId == ContentIds.ModernWardrobe ||
        definitionId != null && Array.Exists(Furnishings, furniture => furniture.Id == definitionId);

    private static bool AtCamp(TileCoord tile, TileCoord home) =>
        !IsApartmentTile(tile) && HexSpatialMath.HexDistance(tile, home) <= Spec72.MaxCampRadiusTiles;

    public static bool IsFurniturePlacementCampTile(WorldSnapshot snapshot, Faction faction, TileCoord tile)
    {
        var home = snapshot.CampHomes.FirstOrDefault(candidate => candidate.Faction == faction);
        return home != null && AtCamp(tile, home.Tile);
    }

    public static int AvailableFurnitureCount(WorldSnapshot snapshot, Faction faction, string definitionId)
    {
        if (!IsFurniture(definitionId)) return 0;
        var home = snapshot.CampHomes.FirstOrDefault(candidate => candidate.Faction == faction);
        if (home == null) return 0;
        var members = snapshot.Npcs.Where(npc => npc.Faction == faction && npc.Health > 0f)
            .Select(npc => npc.Id.Value).ToHashSet();
        var carried = 0;
        foreach (var npc in snapshot.Npcs)
        {
            if (!members.Contains(npc.Id.Value) || !AtCamp(npc.Tile, home.Tile)) continue;
            for (var i = 0; i < npc.InventoryItems.Count; i++)
                if (npc.InventoryItems[i] == definitionId &&
                    (i >= npc.InventoryOwnerIds.Count || npc.InventoryOwnerIds[i] == 0 ||
                     members.Contains(npc.InventoryOwnerIds[i]))) carried++;
        }
        return carried + snapshot.Objects.Count(obj => obj.DefinitionId == definitionId &&
            obj.Variant != PlacedFurnitureVariant && obj.OwnerNpcId == null && AtCamp(obj.Tile, home.Tile) &&
            (obj.PropertyOwnerFaction == null || obj.PropertyOwnerFaction == faction) &&
            (obj.PropertyOwnerNpcId == null || members.Contains(obj.PropertyOwnerNpcId.Value)));
    }

    internal static string PlaceFurniture(WorldState world, string definitionId, TileCoord tile, float rotation,
        IReadOnlyCollection<Faction> factions)
    {
        if (!world.Content.ObjectDefinitions.ContainsKey(definitionId)) return "UnknownProduct";
        if (float.IsNaN(rotation) || float.IsInfinity(rotation) ||
            !world.Tiles.Items.TryGetValue(tile, out var floor) ||
            (floor.Flags & (TileFlags.Water | TileFlags.Blocked)) != 0 ||
            !floor.Flags.HasFlag(TileFlags.HasFloor) ||
            StructurePlacement.CenterJunction(world, tile) is not { } anchor ||
            world.Junctions.Items[anchor].Blocked || !SpatialQueries.IsJunctionFree(world, anchor))
            return "PlacementBlocked";

        var faction = ConstructionAccess.Builder(world, factions, tile);
        if (!world.FactionHomes.TryGetValue(faction, out var home) || !AtCamp(tile, home)) return "ForeignConstruction";
        if (world.Caches.ObjectsByTile.TryGetValue(tile, out var occupants) && occupants.Any(id =>
                world.Entities.Objects.TryGetValue(id, out var obj) &&
                (obj.Variant == PlacedFurnitureVariant || BuildSiteMath.IsSite(obj) ||
                 !obj.IsArchitectureElement && world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                 def.HasTag("Furniture") && !IsFurniture(obj.DefinitionId)))) return "HexOccupied";

        return PlaceFurnitureFromStock(world, definitionId, tile, anchor,
            StructurePlacement.QuantizeHexYaw(rotation), faction, out _);
    }

    /// <summary>§120.13/§176.3: взять один экземпляр современной мебели из запаса
    /// лагеря (земля лагеря, затем рюкзаки его жителей) и поставить на уже
    /// проверенную точку сетки. Место проверяет вызывающий — здесь только запас.</summary>
    internal static string PlaceFurnitureFromStock(WorldState world, string definitionId, TileCoord tile,
        JunctionId anchor, float stampedRotation, Faction faction, out WorldObjectState placed)
    {
        placed = null;
        if (!world.FactionHomes.TryGetValue(faction, out var home) || !AtCamp(tile, home)) return "ForeignConstruction";
        bool Owns(int id) => id == 0 || world.Entities.Npcs.TryGetValue(new EntityId(id), out var owner) &&
            owner.Health > 0f && owner.Faction == faction;

        var ground = world.Entities.Objects.Values.Where(obj => obj.DefinitionId == definitionId &&
                obj.Variant != PlacedFurnitureVariant && !obj.IsOccupied && obj.CurrentUser == null &&
                AtCamp(obj.Tile, home) && (obj.OwnerFaction == null || obj.OwnerFaction == faction) &&
                Owns(obj.Owner?.Value ?? 0))
            .OrderBy(obj => HexSpatialMath.HexDistance(obj.Tile, tile)).ThenBy(obj => obj.Id.Value).FirstOrDefault();
        ItemInstance carriedItem = null;
        if (ground != null)
        {
            WorldObjectMutations.DespawnObject(world, ground.Id);
        }
        else
        {
            var carried = world.Entities.Npcs.Values.Where(npc => npc.Faction == faction && npc.Health > 0f && AtCamp(npc.Tile, home))
                .OrderBy(npc => npc.Id.Value).SelectMany(npc => npc.Inventory.Items
                    .Where(item => item.DefinitionId == definitionId && Owns(item.OwnerId)).Select(item => (Npc: npc, Item: item)))
                .FirstOrDefault();
            if (carried.Item == null) return "MissingFurnitureStock";
            InventoryMath.RemoveReference(carried.Npc.Inventory.Items, carried.Item);
            carriedItem = carried.Item;
        }

        placed = WorldObjectMutations.SpawnObject(world, definitionId, world.Junctions.Items[anchor].Fragment, tile, anchor);
        placed.Variant = PlacedFurnitureVariant;
        placed.OwnerFaction = faction;
        placed.RotationDegrees = stampedRotation;
        placed.Wetness = ground?.Wetness ?? carriedItem.Wetness;
        placed.Durability = ground?.Durability ?? carriedItem.Durability;
        placed.Dirtiness = ground?.Dirtiness ?? carriedItem.Dirtiness;
        placed.Bloodiness = ground?.Bloodiness ?? carriedItem.Bloodiness;
        return null;
    }

    internal static void Update(WorldState world)
    {
        if (!world.Fragments.Items.ContainsKey(ApartmentFragment)) return;
        var portal = world.Portal;
        var day = OpeningDayAt(world.Tick);
        if (portal.Visitor is { } visitor)
        {
            portal.LastOpeningDay = Math.Max(day, portal.LastOpeningDay);
            if (world.Entities.Npcs.TryGetValue(visitor, out var npc) && npc.Health > 0f &&
                npc.Fragment == ApartmentFragment)
                return;
            Close(world);
        }
        if (portal.Mode == PortalSpawnMode.Disabled && !portal.OperatorOpened && portal.ExteriorDoor.HasValue) Close(world);
        if (day <= portal.LastOpeningDay) return;

        portal.LastOpeningDay = day;
        if (portal.Mode == PortalSpawnMode.Disabled || Open(world, day, portal.Mode, portal.Camp, operatorOpened: false) != null)
            Close(world);
    }

    public static string OpenImmediately(WorldState world, Faction camp)
    {
        if (!FactionRelations.IsGirlCamp(camp) || !world.FactionHomes.ContainsKey(camp)) return "InvalidCamp";
        if (!world.Fragments.Items.ContainsKey(ApartmentFragment)) return "PortalDestinationBlocked";
        if (world.Portal.Visitor.HasValue) return "PortalOccupied";
        var day = OpeningDayAt(world.Tick);
        var result = Open(world, Math.Max(0, day), PortalSpawnMode.Camp, camp, operatorOpened: true);
        if (result == null) world.Portal.LastOpeningDay = Math.Max(world.Portal.LastOpeningDay, day);
        return result;
    }

    private static string Open(WorldState world, int day, PortalSpawnMode mode, Faction camp, bool operatorOpened)
    {
        var portal = world.Portal;
        if (portal.VisitSerial == int.MaxValue) return "PortalDestinationBlocked";
        var serial = portal.VisitSerial + 1;
        if (!TryExterior(world, serial, mode, camp, out var exterior)) return "NoSafePortalLocation";
        var doorOptions = new List<PopulationArrivalMath.Landing>();
        foreach (var tile in ApartmentTiles(world.Seed, serial))
        {
            if (!TryGetReturnDoorEdge(tile, world.Seed, out var exteriorNeighbor, serial)) continue;
            var center = HexSpatialMath.TileToWorld(tile);
            var towardWall = HexSpatialMath.Normalize(HexSpatialMath.TileToWorld(exteriorNeighbor) - center);
            if (TryTilePoint(world, tile, out var point, near: center + towardWall * .85f, plannedVisit: serial))
                doorOptions.Add(point);
        }
        if (doorOptions.Count == 0) return "PortalDestinationBlocked";
        var interior = doorOptions[Math.Min(doorOptions.Count - 1,
            (int)(MathUtil.Hash01(world.Seed, serial, 0, 17602) * doorOptions.Count))];

        Close(world);
        portal.VisitSerial = serial;
        portal.OperatorOpened = operatorOpened;
        ApplyApartmentLayout(world, serial);
        portal.ExteriorDoor = SpawnDoor(world, exterior, serial);
        portal.InteriorDoor = SpawnDoor(world, interior, serial);
        PopulateApartment(world, serial, interior);
        Trace.EmitSystem(world, "PortalOpened", $"Day={day} Visit={serial} Tile={exterior.Tile} Operator={operatorOpened}");
        return null;
    }

    private static bool TryExterior(WorldState world, int visitSerial, PortalSpawnMode mode, Faction camp,
        out PopulationArrivalMath.Landing landing)
    {
        landing = default;
        var unsafeTiles = PopulationArrivalMath.UnsafeHumanLandingTiles(world);
        if (mode == PortalSpawnMode.Camp)
        {
            if (!world.FactionHomes.TryGetValue(camp, out var home)) return false;
            for (var radius = 0; radius <= Spec72.MaxCampRadiusTiles; radius++)
            foreach (var tile in Ring(home, radius))
            {
                if (unsafeTiles.Contains(tile)) continue;
                if (TryExteriorTile(world, tile, out landing, avoidFurniture: true)) return true;
            }
            return false;
        }

        var anchors = new List<JunctionId>();
        foreach (var home in world.FactionHomes)
        {
            if (!FactionRelations.IsGirlCamp(home.Key)) continue;
            for (var radius = 0; radius <= 2; radius++)
            {
                var found = false;
                foreach (var tile in Ring(home.Value, radius))
                    if (TryExteriorTile(world, tile, out var anchor))
                    { anchors.Add(anchor.Junction); found = true; break; }
                if (found) break;
            }
        }
        if (anchors.Count == 0) return false;

        // The worldgen tile order is fixed. Scan from a seeded offset and
        // accept a dry connected tile away from predator territory.
        var ordered = world.Tiles.Items.Keys.ToArray();
        var start = Math.Min(ordered.Length - 1, (int)(MathUtil.Hash01(world.Seed, visitSerial, ordered.Length, 17603) * ordered.Length));
        for (var i = 0; i < ordered.Length; i++)
        {
            var tile = ordered[(start + i) % ordered.Length];
            if (unsafeTiles.Contains(tile) || !TryExteriorTile(world, tile, out var point, avoidFurniture: true) ||
                !anchors.Any(anchor => Connectivity.Reachable(world, anchor, point.Junction, true))) continue;
            landing = point;
            return true;
        }
        return false;
    }

    private static IEnumerable<TileCoord> Ring(TileCoord center, int radius)
    {
        for (var q = -radius; q <= radius; q++)
        for (var r = Math.Max(-radius, -q - radius); r <= Math.Min(radius, -q + radius); r++)
            if (Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(q + r))) == radius)
                yield return new TileCoord(center.Q + q, center.R + r);
    }

    private static bool TryExteriorTile(WorldState world, TileCoord tile, out PopulationArrivalMath.Landing landing,
        bool avoidFurniture = false, EntityId? ignoreNpc = null)
    {
        landing = default;
        if (IsApartmentTile(tile) || !world.Tiles.Items.TryGetValue(tile, out var state) ||
            (state.Flags & (TileFlags.Water | TileFlags.Blocked)) != 0) return false;
        if (avoidFurniture && world.Caches.ObjectsByTile.TryGetValue(tile, out var objects) &&
            objects.Any(id => world.Entities.Objects.TryGetValue(id, out var obj) &&
                (obj.IsArchitectureElement || BuildSiteMath.IsSite(obj) ||
                 world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                 (def.HasTag("Furniture") || def.HasTag("Campfire")))))
            return false;
        return TryTilePoint(world, tile, out landing, ignoreNpc);
    }

    private static bool TryTilePoint(WorldState world, TileCoord tile, out PopulationArrivalMath.Landing landing,
        EntityId? ignoreNpc = null, Float2? near = null, int plannedVisit = 0)
    {
        landing = default;
        if (!world.Tiles.Items.TryGetValue(tile, out var state) ||
            (plannedVisit > 0 ? RoomAt(tile, world.Seed, plannedVisit) == PortalApartmentRoom.None :
                !state.Flags.HasFlag(TileFlags.Walkable)))
            return false;

        var center = near ?? HexSpatialMath.TileToWorld(tile);
        Junction best = null;
        var distance = float.MaxValue;
        foreach (var id in state.Junctions)
        {
            var point = world.Junctions.Items[id];
            if (point.Tiles.Count != 1) continue;
            if (plannedVisit > 0)
            {
                if (ApartmentJunctionBlocked(point, world.Seed, plannedVisit)) continue;
            }
            else
            {
                if (point.Blocked || point.Door) continue;
                if (world.Occupancy.JunctionOwner.TryGetValue(id, out var occupier) &&
                    occupier.HasValue && occupier != ignoreNpc) continue;
                if (world.Reservations.Junctions.TryGetValue(id, out var reservation) &&
                    reservation.Owner != ignoreNpc && reservation.EndTick >= world.Tick) continue;
            }
            var offset = center - point.WorldPosition;
            var pointDistance = offset.X * offset.X + offset.Y * offset.Y;
            if (pointDistance >= distance) continue;
            best = point;
            distance = pointDistance;
        }
        if (best == null) return false;
        landing = new PopulationArrivalMath.Landing(tile, best.Id, best.Fragment, best.WorldPosition);
        return true;
    }

    private static ObjectId SpawnDoor(WorldState world, PopulationArrivalMath.Landing point, int serial)
    {
        var door = WorldObjectMutations.SpawnObject(world, DoorDefinitionId, point.Fragment, point.Tile, point.Junction);
        door.Variant = VisitVariantPrefix + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return door.Id;
    }

    internal static string TravelRefusal(WorldState world, NPCState npc, WorldObjectState door)
    {
        var portal = world.Portal;
        if (door == null || door.DefinitionId != DoorDefinitionId || door.Fragment != npc.Fragment)
            return "PortalUnavailable";
        if (door.Id == portal.InteriorDoor)
            return portal.Visitor == npc.Id ? null : "PortalUnavailable";
        if (door.Id != portal.ExteriorDoor || portal.Mode == PortalSpawnMode.Disabled && !portal.OperatorOpened)
            return "PortalUnavailable";
        return portal.Visitor.HasValue ? "PortalOccupied" : null;
    }

    internal static string Travel(WorldState world, NPCState npc, WorldObjectState door)
    {
        var failure = TravelRefusal(world, npc, door);
        if (failure != null) return failure;
        if (npc.CurrentJunction is not { } from || door.Junctions.Count != 1 ||
            from != door.Junctions[0]) return "Unreachable";
        var portal = world.Portal;
        var returning = door.Id == portal.InteriorDoor;
        PopulationArrivalMath.Landing destination = default;
        if (returning)
        {
            if (!world.Entities.Objects.TryGetValue(portal.ExteriorDoor.GetValueOrDefault(), out var exit))
                return "PortalUnavailable";
            var found = false;
            for (var radius = 0; radius <= 3 && !found; radius++)
            foreach (var tile in Ring(exit.Tile, radius))
                if (TryExteriorTile(world, tile, out destination))
                { found = true; break; }
            if (!found) return "PortalExitBlocked";
        }
        else
        {
            if (!world.Entities.Objects.TryGetValue(portal.InteriorDoor.GetValueOrDefault(), out var exit) ||
                exit.Junctions.Count != 1) return "PortalUnavailable";
            var options = ApartmentLandings(world, portal.VisitSerial, exit.Tile, exit.Junctions[0], npc.Body.CanJump);
            if (options.Count == 0) return "PortalExitBlocked";
            destination = options[Math.Min(options.Count - 1,
                (int)(MathUtil.Hash01(world.Seed, portal.VisitSerial, npc.Id.Value, 17604) * options.Count))];
        }

        if (!SpatialMutations.TryOccupyJunction(world, destination.Junction, npc.Id)) return "PortalExitBlocked";
        SpatialMutations.FreeJunction(world, from, npc.Id);
        SpatialMutations.ReleaseJunctionReservation(world, from, npc.Id);
        MoveAcross(world, npc, destination);
        if (npc.CarriedNpcId is { } carriedId &&
            KenshiRescueMath.TryGetPerson(world, carriedId, out var carried, out var dead) &&
            carried.CarriedByNpcId == npc.Id)
        {
            MoveAcross(world, carried, destination, carried: true, dead: dead);
            if (returning && !dead && portal.Resident == carriedId && FactionRelations.IsGirlCamp(npc.Faction))
            {
                carried.Faction = npc.Faction;
                carried.ProfileId = string.Empty;
                carried.Needs.Stress = Math.Max(.65f, carried.Needs.Stress);
                portal.Resident = null;
                Trace.Emit(world, carriedId, "PortalResidentJoined", $"Camp={npc.Faction}");
            }
        }

        if (returning)
        {
            Close(world);
            Trace.Emit(world, npc.Id, "PortalReturned", $"Tile={destination.Tile}");
        }
        else
        {
            portal.Visitor = npc.Id;
            Trace.Emit(world, npc.Id, "PortalEntered", $"Tile={destination.Tile}");
            if (portal.Resident is { } resident && world.Entities.Npcs.TryGetValue(resident, out var local))
            {
                local.Needs.Stress = Math.Max(local.Needs.Stress, .8f);
                Trace.Emit(world, resident, "PortalResidentStartled", "portal.resident.question");
            }
        }
        return null;
    }

    private static void MoveAcross(WorldState world, NPCState npc, PopulationArrivalMath.Landing point,
        bool carried = false, bool dead = false)
    {
        if (!dead)
        {
            SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, point.Tile);
            if (world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var old)) old.Remove(npc.Id);
            if (!world.Caches.EntitiesByFragment.TryGetValue(point.Fragment, out var destination))
            {
                destination = new List<EntityId>();
                world.Caches.EntitiesByFragment.Add(point.Fragment, destination);
            }
            if (!destination.Contains(npc.Id)) destination.Add(npc.Id);
        }
        npc.Tile = point.Tile;
        npc.Fragment = point.Fragment;
        npc.CurrentJunction = carried ? null : point.Junction;
        npc.Position = point.Position;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.SetStatus(MovementStatus.Idle);
        npc.Movement.IsMoving = false;
    }

    private static void Close(WorldState world)
    {
        var portal = world.Portal;
        if (portal.ExteriorDoor is { } exterior) WorldObjectMutations.DespawnObject(world, exterior);
        if (portal.InteriorDoor is { } interior) WorldObjectMutations.DespawnObject(world, interior);
        portal.ExteriorDoor = null;
        portal.InteriorDoor = null;
        portal.Visitor = null;
        portal.OperatorOpened = false;
        ClearApartment(world);
    }

    internal static void ClearClosedApartmentOnLoad(WorldState world)
    {
        if (!world.Portal.InteriorDoor.HasValue && world.Fragments.Items.ContainsKey(ApartmentFragment))
            ClearApartment(world);
    }

    private static void ClearApartment(WorldState world)
    {
        var portal = world.Portal;
        if (portal.Resident is { } resident && world.Entities.Npcs.TryGetValue(resident, out var npc) &&
            npc.Fragment == ApartmentFragment)
            AdminWorldCommands.Execute(world, new AdminCommand { Kind = "delete_npc", NpcId = resident.Value });
        portal.Resident = null;
        portal.GeneratedObjects.Clear();

        var abandoned = world.Entities.Objects.Values.Where(obj => obj.Fragment == ApartmentFragment)
            .Select(obj => obj.Id).ToArray();
        foreach (var id in abandoned) WorldObjectMutations.DespawnObject(world, id);
        var bodies = world.Entities.Corpses.Values.Where(body => body.Fragment == ApartmentFragment)
            .Select(body => body.Id).ToArray();
        foreach (var id in bodies) world.Entities.Corpses.Remove(id);
    }

    private static List<PopulationArrivalMath.Landing> ApartmentLandings(WorldState world, int serial,
        TileCoord exitTile, JunctionId exit, bool canJump)
    {
        var landings = new List<PopulationArrivalMath.Landing>();
        foreach (var tile in ApartmentTiles(world.Seed, serial))
        {
            if (tile == exitTile || IsApartmentFurnitureTile(tile, world.Seed, serial) ||
                !TryTilePoint(world, tile, out var point) ||
                !Connectivity.Reachable(world, point.Junction, exit, canJump)) continue;
            landings.Add(point);
        }
        return landings;
    }

    private static void PopulateApartment(WorldState world, int serial, PopulationArrivalMath.Landing door)
    {
        WorldObjectState table = null;
        WorldObjectState shelf = null;
        foreach (var furniture in GeneratedApartment(world.Seed, serial).Furniture)
        {
            if (!world.Content.ObjectDefinitions.ContainsKey(furniture.DefinitionId) ||
                !TryTilePoint(world, furniture.Tile, out var anchor, near: furniture.Position)) continue;
            var piece = WorldObjectMutations.SpawnObject(world, furniture.DefinitionId, ApartmentFragment,
                furniture.Tile, anchor.Junction);
            piece.Variant = ApartmentFurnitureVariant + "." + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
            piece.RotationDegrees = furniture.RotationDegrees;
            world.Portal.GeneratedObjects.Add(piece.Id);
            if (furniture.DefinitionId == "furniture.modern_table") table = piece;
            if (furniture.DefinitionId == ApartmentShelfDefinitionId) shelf = piece;
            if (furniture.DefinitionId == ContentIds.ModernFridge) ModernFoodCatalog.StockRefrigerator(world, piece, serial);
            if (furniture.DefinitionId == ContentIds.ModernWardrobe)
                PortalApartmentPopulation.StockWardrobe(world, piece, serial);
        }

        var landings = ApartmentLandings(world, serial, door.Tile, door.Junction, canJump: false);
        PortalApartmentPopulation.TrySpawnResident(world, serial, landings);

        var eligible = HouseholdLoot.Where(world.Content.ObjectDefinitions.ContainsKey).ToList();
        var lootPoints = landings.Where(point => SpatialQueries.IsJunctionFree(world, point.Junction))
            .OrderBy(point => MathUtil.Hash01(world.Seed, serial, point.Junction.Value, 17611)).Take(5).ToArray();
        var tools = eligible.Where(ModernLootCatalog.IsApartmentTool).ToArray();
        var nextPoint = 0;
        if (tools.Length > 0 && lootPoints.Length > 0)
        {
            var tool = Math.Min(tools.Length - 1, (int)(MathUtil.Hash01(world.Seed, serial, 0, 17610) * tools.Length));
            var point = lootPoints[nextPoint++];
            SpawnLoot(tools[tool], point.Tile, point.Junction);
        }
        PopulateSurface(table, slots: 2, maxItems: 2, salt: 17608);
        PopulateSurface(shelf, slots: 4, maxItems: 3, salt: 17609);
        for (var i = nextPoint; i < lootPoints.Length && eligible.Count > 0; i++)
        {
            var point = lootPoints[i];
            var pick = Math.Min(eligible.Count - 1, (int)(MathUtil.Hash01(world.Seed, serial, i, 17605) * eligible.Count));
            SpawnLoot(eligible[pick], point.Tile, point.Junction);
        }

        void PopulateSurface(WorldObjectState support, int slots, int maxItems, int salt)
        {
            if (support == null) return;
            var count = 1 + Math.Min(maxItems - 1, (int)(MathUtil.Hash01(world.Seed, serial, 0, salt) * maxItems));
            var empty = Enumerable.Range(0, slots).ToList();
            if (support == table && world.Content.ObjectDefinitions.ContainsKey(ContentIds.DrinkingGlass))
            {
                var glass = SpawnLoot(ContentIds.DrinkingGlass, support.Tile, support.Junctions[0]);
                glass.ResourceAmount = WaterVesselCatalog.Capacity(ContentIds.DrinkingGlass);
                glass.WaterKind = WaterKind.Boiled;
                glass.LastAddedWaterKind = WaterKind.Boiled;
                SurfacePlacementMath.TryPlace(world, glass, support, 0);
                empty.RemoveAt(0);
            }
            for (var i = slots - empty.Count; i < count; i++)
            {
                var small = eligible.Where(id => !ModernLootCatalog.IsBulkyTool(id)).ToArray();
                if (small.Length == 0) return;
                var itemPick = Math.Min(small.Length - 1, (int)(MathUtil.Hash01(world.Seed, serial, 2 * i + 1, salt) * small.Length));
                var slotPick = Math.Min(empty.Count - 1, (int)(MathUtil.Hash01(world.Seed, serial, 2 * i + 2, salt) * empty.Count));
                var loot = SpawnLoot(small[itemPick], support.Tile, support.Junctions[0]);
                SurfacePlacementMath.TryPlace(world, loot, support, (byte)empty[slotPick]);
                empty.RemoveAt(slotPick);
            }
        }

        WorldObjectState SpawnLoot(string definitionId, TileCoord tile, JunctionId anchor)
        {
            eligible.Remove(definitionId);
            var loot = WorldObjectMutations.SpawnObject(world, definitionId, ApartmentFragment, tile, anchor);
            world.Portal.GeneratedObjects.Add(loot.Id);
            if (definitionId == ContentIds.Bottle) FillCola(loot);
            return loot;
        }
    }

    private static void FillCola(WorldObjectState bottle)
    {
        bottle.ResourceAmount = 5f;
        bottle.WaterKind = WaterKind.Cola;
        bottle.LastAddedWaterKind = WaterKind.Cola;
    }
}

}

