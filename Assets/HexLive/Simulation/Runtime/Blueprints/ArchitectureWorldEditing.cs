using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime.Blueprints
{
/// <summary>§120.9/#664: world tools edit an existing house in its actual
/// integer six-way frame; there is no duplicate preview-only house.</summary>
public static class ArchitectureWorldEditing
{
    public static BlueprintElementData ToWorld(BlueprintElementData source, BuildingBlueprintDraft plan,
        TileCoord destination, float yaw)
    {
        var steps = (int)MathF.Round(yaw / 60f);
        var originTile = BlueprintBuildingPlan.AnchorTile(plan);
        var origin = BlueprintGeometry.HexCenter(originTile);
        var target = BlueprintGeometry.HexCenter(destination);
        HexBuildNodeKey Node(HexBuildNodeKey node) => target + BlueprintGeometry.RotateNode(
            new HexBuildNodeKey(node.Q - origin.Q, node.R - origin.R), steps);
        TileCoord Hex(TileCoord tile)
        {
            var delta = BlueprintGeometry.RotateNode(new HexBuildNodeKey(tile.Q - originTile.Q, tile.R - originTile.R), steps);
            return new TileCoord(destination.Q + delta.Q, destination.R + delta.R);
        }
        var result = source.Clone();
        result.RoomId = 0; result.Origin = BlueprintElementOrigin.Manual;
        switch (result.Kind)
        {
            case BlueprintElementKind.Support: result.Node = Node(source.Node); break;
            case BlueprintElementKind.FloorSector:
                result.FloorSector = new FloorSectorKey(Hex(source.FloorSector.Hex), source.FloorSector.Sector - steps); break;
            case BlueprintElementKind.RoofSector:
                result.RoofSector = new RoofSectorKey(Hex(source.RoofSector.Hex), source.RoofSector.Sector - steps); break;
            default: result.Segment = new BuildSegmentKey(Node(source.Segment.A), Node(source.Segment.B)); break;
        }
        return result;
    }

    internal sealed class Revision
    {
        internal WorldObjectState Owner;
        internal BuildingBlueprintDraft Draft;
    }

    internal static bool Plan(WorldState world, IReadOnlyList<FreeArchitecturePlacementData> placements,
        IReadOnlyList<string> removals, IReadOnlyCollection<Faction> factions,
        out List<Revision> revisions, out HashSet<string> handled, out string error)
    {
        revisions = new List<Revision>(); handled = new HashSet<string>(StringComparer.Ordinal); error = string.Empty;
        var placementsBySlot = placements.Where(p => p != null).GroupBy(p => p.SlotKey)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var targets = new HashSet<string>(removals, StringComparer.Ordinal);
        targets.UnionWith(placementsBySlot.Keys);
        var owners = world.Entities.Objects.Values.Where(o => BuildingRules.EditablePlanFor(world, o) != null)
            .OrderBy(o => o.Id.Value).ToArray();
        foreach (var owner in owners)
        {
            var original = BuildingRules.EditablePlanFor(world, owner);
            var candidates = original.Elements.Select(e => e.Clone()).ToList();
            // Undo may restore a desired slot which is still physically queued.
            foreach (var piece in BuildingRules.ArchitectureObjects(world, owner))
            {
                var state = piece.ArchitectureElements[0];
                if (!state.DemolitionPlanned || candidates.Any(e => BlueprintBuildingPlan.SlotKey(e) == state.SlotKey)) continue;
                if (FreeArchitectureRules.TryDecode(state.DefinitionId, state.SlotKey, out var physical)) candidates.Add(physical);
            }
            BuildingBlueprintDraft revision = null;
            foreach (var authored in candidates)
            {
                var global = ToWorld(authored, original, owner.Tile, owner.RotationDegrees);
                var slot = BlueprintBuildingPlan.SlotKey(global);
                if (!targets.Contains(slot)) continue;
                if (!handled.Add(slot)) { error = "DuplicateArchitectureSlot"; return false; }
                if (factions != null && !ConstructionAccess.CanEditObject(world, factions, owner))
                { error = ConstructionAccess.ForeignReason; return false; }
                revision ??= original.Clone();
                var localSlot = BlueprintBuildingPlan.SlotKey(authored);
                revision.Elements.RemoveAll(e => BlueprintBuildingPlan.SlotKey(e) == localSlot);
                if (placementsBySlot.TryGetValue(slot, out var replacement))
                {
                    var next = authored.Clone(); next.Kind = replacement.Kind;
                    if (string.IsNullOrWhiteSpace(next.Id) || revision.Elements.Any(e => e.Id == next.Id))
                        next.Id = revision.AllocateElementId();
                    revision.Elements.Add(next);
                }
            }
            if (revision == null) continue;
            BlueprintEditorCommands.NormalizeRoomConnectivity(revision);
            revision.Normalize();
            var validation = BlueprintValidator.Validate(revision);
            if (!validation.IsValid) { error = "InvalidBlueprint"; return false; }
            revisions.Add(new Revision { Owner = owner, Draft = revision });
        }
        return true;
    }
}
}
