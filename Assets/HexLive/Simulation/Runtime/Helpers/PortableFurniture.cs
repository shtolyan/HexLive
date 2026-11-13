using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§176. Moving standalone furniture transfers one physical item through inventory.</summary>
public static class PortableFurniture
{
    public const string BasicPlacedVariant = "portable.placed";
    public const float ReachWorldUnits = 2.25f;

    public static bool CanCarry(string definitionId) =>
        definitionId is ContentIds.BedBasic or ContentIds.StorageChest or "chair.basic" ||
        PortalWorld.IsFurniture(definitionId);

    /// <summary>
    /// #745: pieces that block their authored junction footprint wherever they
    /// stand — on the ground as well as indoors, like <c>bed.basic</c>. The
    /// modern table has no Obstacle tag and no radius, so outdoors it blocked
    /// nothing: the construction overlay drew its 11 footprint points, yet a
    /// route ran straight through them and under the tabletop.
    /// </summary>
    public static bool BlocksFootprintEverywhere(string definitionId) =>
        // #765: every modern piece is solid wherever it stands — the island, a
        // house floor, the apartment. Chair, fridge, shelf, wardrobe, TV and the
        // sofa blocked nothing before and were walked straight through.
        // The chest, the rain collector and the workbench are the same case:
        // an authored footprint (disc of 7, disc of 7, table's 11) that only an
        // indoor floor used to stamp. Outdoors the collector and the chest
        // blocked their single anchor point, the workbench a 0.62 wu disc.
        definitionId is ContentIds.BedBasic or ContentIds.StorageChest or ContentIds.WaterCollector
            or ContentIds.Workbench || PortalWorld.IsFurniture(definitionId);

    /// <summary>Stamp the authored footprint once the placement yaw is final
    /// (spawn happens at yaw 0, and the table's 4-3-4 rows and the beds' two
    /// rails turn with it). #755-757: the modern bed blocks the primitive
    /// bed's 14 points; a bed set down from a backpack now gets the same
    /// footprint the loader has always restamped, not the 1.25 wu spawn disc.</summary>
    internal static void ApplyPlacedFootprint(WorldState world, WorldObjectState placed)
    {
        if (placed != null && BlocksFootprintEverywhere(placed.DefinitionId))
            WorldObjectMutations.SetAuthoredFurnitureBlocking(world, placed, blocked: true);
    }

    public static bool IsBuiltInBed(string definitionId, string variant) =>
        definitionId == ContentIds.BedBasic && variant == ContentIds.HutBedVariant;

    public static bool CanShowPickup(ObjectSnapshot obj) => CanCarry(obj.DefinitionId) &&
        !obj.ArchitectureOwnerObjectId.HasValue && string.IsNullOrEmpty(obj.BuildProduct) &&
        !IsBuiltInBed(obj.DefinitionId, obj.Variant);

    public static float FootprintRadius(string definitionId) => definitionId switch
    {
        // #755-757: one bed footprint, one placement radius.
        ContentIds.BedBasic or ContentIds.ModernBed => 1.25f,
        ContentIds.StorageChest => StorageChest.FootprintRadius,
        "chair.basic" => .28f,
        "furniture.modern_sofa" => 1.04f,
        "furniture.modern_table" => .66f,
        "furniture.modern_tv" => .61f,
        "furniture.modern_fridge" => .49f,
        "furniture.modern_chair" => .34f,
        PortalWorld.ApartmentShelfDefinitionId => .39f,
        ContentIds.ModernWardrobe => ModernLootCatalog.WardrobeFootprintRadius,
        _ => 0f,
    };

    public static bool FitsInsideTile(string definitionId, TileCoord tile, Float2 position)
    {
        var radius = FootprintRadius(definitionId);
        if (radius <= 0f) return false;
        var offset = position - HexSpatialMath.TileToWorld(tile);
        var side = Math.Abs(offset.X * .5f) + Math.Abs(offset.Y) * HexSpatialMath.HexApothemFactor;
        return Math.Max(Math.Abs(offset.X), side) + radius <= HexSpatialMath.HexApothem;
    }

    public static bool CanShareTile(string definitionId, string otherId, Float2 center, Float2 otherCenter)
    {
        var otherRadius = FootprintRadius(otherId);
        return definitionId == ContentIds.StorageChest && otherRadius > 0f &&
            HexSpatialMath.Distance(center, otherCenter) >= StorageChest.FootprintRadius + otherRadius;
    }

    internal static string PickupRefusal(WorldState world, NPCState actor, WorldObjectState obj,
        bool allowOwnReservation = false)
    {
        if (!ManualControlMath.IsManual(actor)) return "ManualOnly";
        if (obj.ArchitectureOwnerId.HasValue || BuildSiteMath.IsSite(obj) || obj.IsCraftProject ||
            IsBuiltInBed(obj.DefinitionId, obj.Variant)) return "FurnitureIntegrated";
        if ((obj.IsOccupied || obj.CurrentUser.HasValue) && (!allowOwnReservation || obj.CurrentUser != actor.Id) ||
            obj.Contents.Count > 0) return "Occupied";
        return actor.Body.HasUsableHand ? null : "NeedsHand";
    }

    internal static string Place(WorldState world, NPCState actor, PlaceInventoryFurnitureCommand command)
    {
        var items = actor.Inventory.Items;
        var source = command.Item;
        if (source.Source != InventoryItemSource.Carried || source.Index < 0 || source.Index >= items.Count ||
            items[source.Index].DefinitionId != source.ExpectedDefinitionId) return "StaleItem";
        var item = items[source.Index];
        if (!CanCarry(item.DefinitionId) || !world.Content.ObjectDefinitions.ContainsKey(item.DefinitionId))
            return "NotFurniture";
        if (!actor.Body.HasUsableHand) return "NeedsHand";

        if (float.IsNaN(command.RotationDegrees) || float.IsInfinity(command.RotationDegrees) ||
            !world.Tiles.Items.TryGetValue(command.TargetTile, out var tile) || !tile.Flags.HasFlag(TileFlags.Walkable) ||
            (tile.Flags & (TileFlags.Water | TileFlags.Blocked)) != 0 ||
            !world.Junctions.Items.TryGetValue(command.TargetJunction, out var anchor) ||
            !tile.Junctions.Contains(anchor.Id) || anchor.Fragment != actor.Fragment || anchor.Blocked || anchor.Door ||
            !SpatialQueries.IsJunctionFree(world, anchor.Id) || !FitsInsideTile(item.DefinitionId, tile.Coord, anchor.WorldPosition))
            return "PlacementBlocked";
        if (!ConstructionAccess.CanEditTile(world, new[] { actor.Faction }, tile.Coord)) return "ForeignConstruction";
        if (!WithinReach(world, actor, anchor)) return "Unreachable";
        if (actor.Fragment == PortalWorld.ApartmentFragment && world.Portal.InteriorDoor is { } doorId &&
            world.Entities.Objects.TryGetValue(doorId, out var door) &&
            PortalWorld.TryGetWindowEdge(tile.Coord, world.Seed, door.Tile, out _)) return "PlacementBlocked";

        var radius = FootprintRadius(item.DefinitionId);
        if (world.Entities.Npcs.Values.Any(npc => npc.Health > 0f && npc.Fragment == actor.Fragment &&
                HexSpatialMath.Distance(npc.Position, anchor.WorldPosition) <= radius) ||
            tile.Junctions.Any(id => world.Junctions.Items.TryGetValue(id, out var point) &&
                (point.Blocked || !SpatialQueries.IsJunctionFree(world, id)) &&
                HexSpatialMath.Distance(point.WorldPosition, anchor.WorldPosition) <= radius) ||
            world.Caches.ObjectsByTile.TryGetValue(tile.Coord, out var objects) && objects.Any(id =>
                world.Entities.Objects.TryGetValue(id, out var other) &&
                BlocksPlacement(world, other, item.DefinitionId, anchor.WorldPosition)))
            return "PlacementBlocked";

        using var events = world.Events.DeferPublication();
        var placed = WorldObjectMutations.SpawnObject(world, item.DefinitionId, anchor.Fragment, tile.Coord, anchor.Id);
        placed.RotationDegrees = StructurePlacement.QuantizeHexYaw(command.RotationDegrees);
        placed.Variant = PortalWorld.IsFurniture(item.DefinitionId) ? PortalWorld.PlacedFurnitureVariant : BasicPlacedVariant;
        placed.OwnerFaction = actor.Faction;
        placed.Wetness = item.Wetness;
        placed.Durability = item.Durability;
        placed.ResourceAmount = item.ResourceAmount;
        placed.WaterKind = item.WaterKind;
        placed.LastAddedWaterKind = item.LastAddedWaterKind;
        placed.LeafDryingStartTick = item.LeafDryingStartTick;
        placed.Dirtiness = item.Dirtiness;
        placed.Bloodiness = item.Bloodiness;
        ApplyPlacedFootprint(world, placed);
        items.RemoveAt(source.Index);
        Trace.Emit(world, actor.Id, "FurniturePlaced", $"Def={item.DefinitionId} Obj={placed.Id.Value} Tile={tile.Coord}");
        events.Commit();
        return null;
    }

    private static bool BlocksPlacement(
        WorldState world, WorldObjectState obj, string definitionId, Float2 center)
    {
        if (BuildSiteMath.IsSite(obj) || obj.DefinitionId == PortalWorld.DoorDefinitionId) return true;
        if (obj.IsArchitectureElement) return false;
        if (definitionId == ContentIds.StorageChest && obj.Junctions.Count > 0 &&
            world.Junctions.Items.TryGetValue(obj.Junctions[0], out var other))
        {
            if (CanShareTile(definitionId, obj.DefinitionId, center, other.WorldPosition)) return false;
            if (HexSpatialMath.Distance(center, other.WorldPosition) <= StorageChest.FootprintRadius) return true;
        }
        return CanCarry(obj.DefinitionId) ||
            world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
            (def.HasTag("Furniture") || def.HasTag("Bed") || def.HasTag("Chair"));
    }

    private static bool WithinReach(WorldState world, NPCState actor, Junction anchor)
    {
        if (actor.CurrentJunction is not { } start ||
            HexSpatialMath.Distance(actor.Position, anchor.WorldPosition) > ReachWorldUnits) return false;
        var path = HexPathfinder.FindPath(world, start, anchor.Id, null, weightClimb: false,
            canJump: false, maxExpansions: 256);
        if (path == null || path.Count == 0) return false;
        var distance = 0f;
        for (var i = 1; i < path.Count; i++)
            distance += HexSpatialMath.Distance(world.Junctions.Items[path[i - 1]].WorldPosition,
                world.Junctions.Items[path[i]].WorldPosition);
        return distance <= ReachWorldUnits;
    }
}

}
