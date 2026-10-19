using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
/// <summary>§120.12: editing is ownership, not diplomacy. Check the complete
/// gesture before any registry, material, topology or object mutation.</summary>
public static class ConstructionAccess
{
    public const string ForeignReason = "ForeignConstruction";

    public static bool IsConstructionCommand(ISimulationCommand command) => command is
        PlaceBuildingPlanCommand or PlaceFurnitureSiteCommand or PlaceBuildingBlueprintCommand or
        UpdateBuildingBlueprintCommand or ApplyFreeArchitectureCommand or
        RotateBuildSiteCommand or CancelBuildSiteCommand;

    public static HashSet<Faction> Factions(WorldState world, IEnumerable<int> assigned) =>
        world.Entities.Npcs.Values.Where(npc => npc.Health > 0 &&
            PlayerAuthority.IsPlayerOwned(world, npc) &&
            (assigned == null || assigned.Contains(npc.Id.Value)))
            .Select(npc => npc.Faction).ToHashSet();

    public static bool CanEditTile(WorldState world, IReadOnlyCollection<Faction> factions, TileCoord tile)
    {
        if (factions.Count == 0) return false;
        foreach (var home in world.FactionHomes)
            if (!factions.Contains(home.Key) &&
                HexSpatialMath.HexDistance(tile, home.Value) <= TheftMath.PrivateGroundRadiusTiles) return false;
        return true;
    }

    public static bool CanEditTile(WorldSnapshot snapshot, IReadOnlyCollection<Faction> factions, TileCoord tile)
    {
        if (factions.Count == 0) return false;
        for (var i = 0; i < snapshot.CampHomes.Count; i++)
        {
            var home = snapshot.CampHomes[i];
            if (!factions.Contains(home.Faction) &&
                HexSpatialMath.HexDistance(tile, home.Tile) <= TheftMath.PrivateGroundRadiusTiles) return false;
        }
        return true;
    }

    public static bool CanEditObject(WorldState world, IReadOnlyCollection<Faction> factions, WorldObjectState obj) =>
        (obj.OwnerFaction is not { } owner || factions.Contains(owner)) &&
        (obj.Owner is not { } person || !world.Entities.Npcs.TryGetValue(person, out var npc) ||
         factions.Contains(npc.Faction)) && ObjectTiles(world, obj).All(tile => CanEditTile(world, factions, tile));

    public static Faction Builder(WorldState world, IReadOnlyCollection<Faction> factions, TileCoord tile) =>
        factions.OrderBy(faction => world.FactionHomes.TryGetValue(faction, out var home)
                ? HexSpatialMath.HexDistance(tile, home) : int.MaxValue)
            .ThenBy(faction => (int)faction).First();

    public static bool Validate(WorldState world, ISimulationCommand command,
        IReadOnlyCollection<Faction> factions, out string reason)
    {
        reason = string.Empty;
        if (!IsConstructionCommand(command)) return true;
        if (factions.Count == 0) { reason = "NotAssigned"; return false; }
        IEnumerable<TileCoord> tiles = Array.Empty<TileCoord>();
        WorldObjectState target = null;
        switch (command)
        {
            case PlaceFurnitureSiteCommand furniture:
                tiles = new[] { furniture.Tile };
                break;
            case PlaceBuildingPlanCommand hut:
                tiles = PlanTiles(CommittedBuildingPlans.PlayerHut, hut.Tile, hut.RotationDegrees);
                break;
            case PlaceBuildingBlueprintCommand blueprint:
                if (!TryPlan(blueprint.BlueprintJson, out var plan))
                { reason = "InvalidBlueprint"; return false; }
                tiles = PlanTiles(plan, blueprint.Tile, blueprint.RotationDegrees);
                break;
            case UpdateBuildingBlueprintCommand update:
                if (!TryPlan(update.BlueprintJson, out var revision))
                { reason = "InvalidBlueprint"; return false; }
                if (world.Entities.Objects.TryGetValue(update.Owner, out target))
                {
                    // ApplyBlueprintRevision pins the stored anchor, regardless
                    // of the client's anchor. Check that exact same geometry.
                    var previous = BuildingRules.EditablePlanFor(world, target);
                    if (previous != null)
                    {
                        var stableAnchor = BlueprintBuildingPlan.AnchorTile(previous);
                        revision.HasAnchor = true;
                        revision.AnchorQ = stableAnchor.Q;
                        revision.AnchorR = stableAnchor.R;
                        revision.Normalize();
                    }
                    tiles = PlanTiles(revision, target.Tile, target.RotationDegrees);
                }
                break;
            case RotateBuildSiteCommand rotate:
                if (world.Entities.Objects.TryGetValue(rotate.Site, out target))
                {
                    var rotatedPlan = BuildingRules.PlanFor(world, target);
                    tiles = rotatedPlan != null ? PlanTiles(rotatedPlan, target.Tile, rotate.RotationDegrees)
                        : new[] { target.Tile };
                }
                break;
            case CancelBuildSiteCommand cancel:
                world.Entities.Objects.TryGetValue(cancel.Site, out target);
                break;
            case ApplyFreeArchitectureCommand free:
                if (free.Placements.Count + free.RemovedSlotKeys.Count > FreeArchitectureRules.MaxGestureElements)
                { reason = "InvalidArchitectureGesture"; return false; }
                tiles = free.Placements.Where(p => p != null)
                    .SelectMany(p => ElementTiles(p.ToElement("access")).Append(p.AnchorTile));
                var editedSlots = new HashSet<string>(free.RemovedSlotKeys, StringComparer.Ordinal);
                foreach (var placement in free.Placements)
                    if (placement != null) editedSlots.Add(placement.SlotKey);
                foreach (var piece in FreeArchitectureRules.FreePieces(world))
                {
                    if (!editedSlots.Contains(piece.ArchitectureElements[0].SlotKey)) continue;
                    if (CanEditObject(world, factions, piece)) continue;
                    reason = ForeignReason; return false;
                }
                break;
        }
        if (target != null && !CanEditObject(world, factions, target) ||
            tiles.Any(tile => !CanEditTile(world, factions, tile)))
        { reason = ForeignReason; return false; }
        return true;
    }

    private static bool TryPlan(string json, out BuildingBlueprintDraft draft)
    {
        draft = null;
        return !string.IsNullOrEmpty(json) && json.Length <= 128 * 1024 &&
            BuildingBlueprintJson.TryDeserialize(json, out draft, out _);
    }

    public static IEnumerable<TileCoord> ObjectTiles(WorldState world, WorldObjectState obj)
    {
        yield return obj.Tile;
        var plan = BuildingRules.PlanFor(world, obj);
        if (plan != null)
            foreach (var tile in PlanTiles(plan, obj.Tile, obj.RotationDegrees)) yield return tile;
        if (FreeArchitectureRules.IsFreePiece(obj) && FreeArchitectureRules.TryDecode(
                obj.ArchitectureElements[0].DefinitionId, obj.ArchitectureElements[0].SlotKey, out var element))
            foreach (var tile in ElementTiles(element)) yield return tile;
    }

    public static IEnumerable<TileCoord> PlanTiles(BuildingBlueprintDraft plan, TileCoord anchor, float yaw)
    {
        var origin = BlueprintGeometry.HexCenter(BlueprintBuildingPlan.AnchorTile(plan));
        var destination = BlueprintGeometry.HexCenter(anchor);
        var originJunction = HexPointLayout.GetJunctionKeyPair(BlueprintBuildingPlan.AnchorTile(plan), default);
        var destinationJunction = HexPointLayout.GetJunctionKeyPair(anchor, default);
        var quantized = StructurePlacement.QuantizeHexSymmetryYaw(yaw);
        var steps = ((int)MathF.Round(quantized / 60f) % 6 + 6) % 6;
        HexBuildNodeKey Transform(HexBuildNodeKey node) => destination +
            BlueprintGeometry.RotateNode(new HexBuildNodeKey(node.Q - origin.Q, node.R - origin.R), steps);
        foreach (var tile in BuildingBootstrap.FootprintTiles(plan, anchor, quantized)) yield return tile;
        foreach (var element in plan.Elements)
        {
            foreach (var node in ElementNodes(element))
                foreach (var tile in PointTiles(BlueprintGeometry.ToWorld(Transform(node)))) yield return tile;
        }
        foreach (var furniture in plan.Furniture)
        {
            foreach (var tile in PointTiles(BlueprintGeometry.ToWorld(Transform(
                         BlueprintGeometry.HexCenter(new TileCoord(furniture.TileQ, furniture.TileR)))))) yield return tile;
            foreach (var junction in BlueprintFurnitureFootprints.OccupiedJunctions(furniture))
            {
                var offset = BlueprintGeometry.RotateJunctionOffset(new JunctionKey(
                    junction.XKey - originJunction.xKey, junction.YKey - originJunction.yKey), steps);
                foreach (var tile in PointTiles(BlueprintGeometry.JunctionToWorld(new JunctionKey(
                             destinationJunction.xKey + offset.XKey, destinationJunction.yKey + offset.YKey)))) yield return tile;
            }
        }
    }

    public static IEnumerable<TileCoord> ElementTiles(BlueprintElementData element) =>
        ElementNodes(element).SelectMany(node => PointTiles(BlueprintGeometry.ToWorld(node))).Distinct();

    public static IEnumerable<BlueprintElementData> ChangedElements(
        BuildingBlueprintDraft before, BuildingBlueprintDraft after)
    {
        var old = before.Elements.ToDictionary(BlueprintBuildingPlan.SlotKey, StringComparer.Ordinal);
        var next = after.Elements.ToDictionary(BlueprintBuildingPlan.SlotKey, StringComparer.Ordinal);
        foreach (var pair in old)
            if (!next.TryGetValue(pair.Key, out var element) || element.Kind != pair.Value.Kind) yield return pair.Value;
        foreach (var pair in next)
            if (!old.TryGetValue(pair.Key, out var element) || element.Kind != pair.Value.Kind) yield return pair.Value;
    }

    private static IEnumerable<HexBuildNodeKey> ElementNodes(BlueprintElementData element)
    {
        switch (element.Kind)
        {
            case BlueprintElementKind.Support: yield return element.Node; break;
            case BlueprintElementKind.FloorSector:
                yield return BlueprintGeometry.HexCenter(element.FloorSector.Hex);
                yield return BlueprintGeometry.HexCorner(element.FloorSector.Hex, element.FloorSector.Sector);
                yield return BlueprintGeometry.HexCorner(element.FloorSector.Hex, element.FloorSector.Sector + 1); break;
            case BlueprintElementKind.RoofSector:
                yield return BlueprintGeometry.HexCenter(element.RoofSector.Hex);
                yield return BlueprintGeometry.HexCorner(element.RoofSector.Hex, element.RoofSector.Sector);
                yield return BlueprintGeometry.HexCorner(element.RoofSector.Hex, element.RoofSector.Sector + 1); break;
            default: yield return element.Segment.A; yield return element.Segment.B; break;
        }
    }

    // Include every hex sharing an edge/corner, rather than WorldToTile's single
    // rounded winner: a wall on the boundary must not edit a neighbour's land.
    private static IEnumerable<TileCoord> PointTiles(Float2 point)
    {
        var center = HexSpatialMath.WorldToTile(point);
        for (var dq = -1; dq <= 1; dq++)
        for (var dr = -1; dr <= 1; dr++)
        {
            var tile = new TileCoord(center.Q + dq, center.R + dr);
            if (HexSpatialMath.HexDistance(center, tile) > 1) continue;
            var position = HexSpatialMath.TileToWorld(tile);
            var x = MathF.Abs(point.X - position.X);
            var y = MathF.Abs(point.Y - position.Y);
            const float epsilon = 0.0001f;
            if (x <= HexSpatialMath.HexRadius * HexSpatialMath.Sqrt3 * .5f + epsilon &&
                y + x / HexSpatialMath.Sqrt3 <= HexSpatialMath.HexRadius + epsilon) yield return tile;
        }
    }
}
}
