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
        definitionId is ContentIds.BedBasic or "chair.basic" || PortalWorld.IsFurniture(definitionId);

    public static bool IsBuiltInBed(string definitionId, string variant, bool hasFloor) =>
        definitionId == ContentIds.BedBasic &&
        (variant == ContentIds.HutBedVariant || hasFloor && variant != BasicPlacedVariant);

    public static bool CanShowPickup(ObjectSnapshot obj, bool hasFloor) => CanCarry(obj.DefinitionId) &&
        !obj.ArchitectureOwnerObjectId.HasValue && string.IsNullOrEmpty(obj.BuildProduct) &&
        !IsBuiltInBed(obj.DefinitionId, obj.Variant, hasFloor);

    public static float FootprintRadius(string definitionId) => definitionId switch
    {
        ContentIds.BedBasic => 1.25f,
        "chair.basic" => .28f,
        "furniture.modern_bed" or "furniture.modern_sofa" => 1.04f,
        "furniture.modern_table" => .66f,
        "furniture.modern_tv" => .61f,
        "furniture.modern_fridge" => .49f,
        "furniture.modern_chair" => .34f,
        PortalWorld.ApartmentShelfDefinitionId => .39f,
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

    internal static string PickupRefusal(WorldState world, NPCState actor, WorldObjectState obj,
        bool allowOwnReservation = false)
    {
        if (!ManualControlMath.IsManual(actor)) return "ManualOnly";
        var hasFloor = world.Tiles.Items.TryGetValue(obj.Tile, out var tile) && tile.Flags.HasFlag(TileFlags.HasFloor);
        if (obj.ArchitectureOwnerId.HasValue || BuildSiteMath.IsSite(obj) || obj.IsCraftProject ||
            IsBuiltInBed(obj.DefinitionId, obj.Variant, hasFloor)) return "FurnitureIntegrated";
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
                world.Entities.Objects.TryGetValue(id, out var other) && BlocksPlacement(world, other)))
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
        items.RemoveAt(source.Index);
        Trace.Emit(world, actor.Id, "FurniturePlaced", $"Def={item.DefinitionId} Obj={placed.Id.Value} Tile={tile.Coord}");
        events.Commit();
        return null;
    }

    private static bool BlocksPlacement(WorldState world, WorldObjectState obj) =>
        BuildSiteMath.IsSite(obj) || obj.DefinitionId == PortalWorld.DoorDefinitionId ||
        !obj.IsArchitectureElement && (CanCarry(obj.DefinitionId) ||
            world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
            (def.HasTag("Furniture") || def.HasTag("Bed") || def.HasTag("Chair")));

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
