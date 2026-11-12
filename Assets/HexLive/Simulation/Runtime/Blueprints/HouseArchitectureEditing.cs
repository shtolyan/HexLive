using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime.Blueprints
{

/// <summary>§120.9/#664: desired blueprint and still-standing paid LEGO are separate.
/// The existing persisted demolition fields are also valid on house-owned pieces.</summary>
public static class HouseArchitectureEditing
{
    internal static bool QueueOrRestore(WorldState world, WorldObjectState owner, WorldObjectState piece, string desiredDefinition)
    {
        var state = piece.ArchitectureElements[0];
        if (state.DefinitionId == desiredDefinition)
        {
            if (state.DemolitionPlanned)
            {
                // The work target has been withdrawn. Merely clearing its
                // flag strands an in-flight Build executor after its generic
                // site interaction disappears; retire its actual claims now.
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    var executing = npc.Execution.TargetObject == piece.Id &&
                        npc.Execution.CurrentInteraction == InteractionType.Build;
                    var approaching = npc.Plan.Status == PlanStatus.Active && npc.Plan.Steps
                        .Skip(Math.Max(0, npc.Plan.CurrentStepIndex)).Any(step =>
                            step.TargetObject == piece.Id && step.Interaction == InteractionType.Build);
                    if ((executing || approaching) && PlanInterruption.TryAbort(world, npc,
                            InterruptionCause.PlayerCommand, "Construction teardown cancelled"))
                        npc.Mind.CurrentGoal = GoalType.None;
                }
            }
            state.DemolitionPlanned = false;
            state.ReplacementDefinitionId = string.Empty;
            return false;
        }
        if (state.DeliveredTotal == 0 && state.WorkDone == 0) return false;
        state.DemolitionPlanned = true;
        piece.Owner = owner.Owner; piece.OwnerFaction = owner.OwnerFaction;
        AnchorForWork(world, owner, piece);
        Bootstrap.BuildingBootstrap.RememberPlanSite(world, piece);
        state.ReplacementDefinitionId = desiredDefinition ?? string.Empty;
        return true;
    }

    internal static void TrackPaidPrefix(WorldState world, WorldObjectState owner)
    {
        if (owner.InstalledBuildMaterials != null) return;
        if (!BuildingRules.IsCompletedBuilding(owner)) { BuildSiteMath.TrackWork(owner); return; }
        var elements = BuildingRules.Elements(world, owner).ToArray();
        var legacyDeltaBill = BuildSiteMath.AllMaterials.Any(material => BuildSiteMath.Bill(owner, material) > 0);
        var installed = new int[BuildSiteMath.AllMaterials.Length];
        var historical = new List<ItemInstance>();
        for (var i = 0; i < installed.Length; i++)
        {
            var material = BuildSiteMath.AllMaterials[i];
            int Paid(ArchitectureElementState e) => material == ContentIds.Stick ? e.DeliveredSticks :
                material == ContentIds.Board ? e.DeliveredBoards : material == ContentIds.Rope ? e.DeliveredRope :
                material == ContentIds.PalmLeaf ? e.DeliveredLeaves : 0;
            int Required(ArchitectureElementState e) => material == ContentIds.Stick ? e.RequiredSticks :
                material == ContentIds.Board ? e.RequiredBoards : material == ContentIds.Rope ? e.RequiredRope :
                material == ContentIds.PalmLeaf ? e.RequiredLeaves : 0;
            var paid = elements.Sum(Paid);
            var existing = owner.Contents.Count(item => item.DefinitionId == material);
            // Legacy completed houses may represent paid materials ONLY in
            // their LEGO counters. A legacy extension's bill/Contents holds
            // the new delta; keep that loose delivery in addition to the old
            // paid prefix, instead of stealing it during a later teardown.
            // RaiseFurnitureSite copies neither Bill nor Contents into a new
            // owner: bill=0 means counter-only payment, so existing items are
            // loose. In-place extensions retain their DELTA bill even after
            // completion; their existing Contents already represents that delta.
            var missing = legacyDeltaBill
                ? Math.Max(0, elements.Sum(Required) - BuildSiteMath.Bill(owner, material))
                : paid;
            missing = Math.Max(missing, paid - existing);
            for (var n = 0; n < missing; n++) historical.Add(new ItemInstance(material) {
                LeafDryingStartTick = owner.LeafDryingStartTick,
                OwnerId = owner.Owner?.Value ?? 0
            });
            installed[i] = paid;
        }
        owner.Contents.InsertRange(0, historical);
        owner.InstalledBuildMaterials = installed;
    }

    internal static BuildingBlueprintDraft PhysicalPlan(WorldState world, WorldObjectState owner, bool completedOnly = false)
    {
        var desired = BuildingRules.PlanFor(world, owner);
        if (desired == null) return null;
        var plan = desired.Clone();
        plan.Furniture.Clear(); plan.Elements.Clear();
        var bySlot = desired.Elements.ToDictionary(BlueprintBuildingPlan.SlotKey, StringComparer.Ordinal);
        foreach (var piece in BuildingRules.ArchitectureObjects(world, owner))
        {
            var state = piece.ArchitectureElements[0];
            if (completedOnly && !state.Complete) continue;
            if (!FreeArchitectureRules.TryDecode(state.DefinitionId, state.SlotKey, out var element)) continue;
            if (bySlot.TryGetValue(state.SlotKey, out var authored)) element = authored.Clone();
            // A replacement retains the OLD physical kind until actual work.
            if (FreeArchitectureRules.TryDecode(state.DefinitionId, state.SlotKey, out var physical))
                element.Kind = physical.Kind;
            element.Id = $"physical_{piece.Id.Value}";
            plan.Elements.Add(element);
        }
        return plan;
    }

    private static void AnchorForWork(WorldState world, WorldObjectState owner, WorldObjectState piece)
    {
        if (piece.DefinitionId == DoorTopology.DoorDefinitionId) return; // actual door throat is already canonical
        var state = piece.ArchitectureElements[0];
        var angle = owner.RotationDegrees * MathF.PI / 180f;
        var origin = HexSpatialMath.TileToWorld(owner.Tile);
        var target = origin + new Float2(state.LocalX * MathF.Cos(angle) - state.LocalZ * MathF.Sin(angle),
            state.LocalX * MathF.Sin(angle) + state.LocalZ * MathF.Cos(angle));
        var tile = HexSpatialMath.WorldToTile(target);
        if (!world.Tiles.Items.TryGetValue(tile, out var tileState)) return;
        var candidates = tileState.Junctions.Where(id => world.Junctions.Items.ContainsKey(id))
            .OrderBy(id => {
                var delta = world.Junctions.Items[id].WorldPosition - target;
                return delta.X * delta.X + delta.Y * delta.Y;
            }).ThenBy(id => id.Value).ToArray();
        if (candidates.Length == 0) return;
        piece.Junctions.Clear(); piece.Junctions.Add(candidates[0]);
    }

    internal static void RefreshFlags(WorldState world, WorldObjectState owner, IEnumerable<TileCoord> previousTiles = null)
    {
        var physical = PhysicalPlan(world, owner);
        if (physical == null) return;
        var touched = ConstructionAccess.PlanTiles(physical, owner.Tile, owner.RotationDegrees)
            .Concat(previousTiles ?? Array.Empty<TileCoord>()).Distinct();
        RefreshWorldFlags(world, touched);
    }

    /// <summary>
    /// §35.4 r3 (bug #750): a house under construction gets its per-hex
    /// <see cref="TileFlags.Roofed"/> the moment that hex's sixth roof sector
    /// is finished — not when the whole building completes. Only Roofed is
    /// touched: the sanctuary (Indoor/HasFloor) keeps its whole-building
    /// contract. Event-driven (one call per completed roof sector, and once
    /// per plan on load), never per tick or per query.
    /// </summary>
    internal static void RefreshRoofFlags(WorldState world, WorldObjectState owner)
    {
        var tiles = PlanTilesOf(world, owner);
        if (tiles != null) RefreshWorldFlags(world, tiles, roofOnly: true);
    }

    internal static IEnumerable<TileCoord> PlanTilesOf(WorldState world, WorldObjectState owner)
    {
        var physical = PhysicalPlan(world, owner);
        return physical == null
            ? null
            : ConstructionAccess.PlanTiles(physical, owner.Tile, owner.RotationDegrees);
    }

    // Floor/roof/enclosure are properties of the physical union, not of the
    // construction owner: five old house sectors plus one independent sector
    // still form one floor. Desired demolition remains physical until work.
    internal static void RefreshWorldFlags(WorldState world, IEnumerable<TileCoord> touched,
        bool roofOnly = false)
    {
        var physical = new BuildingBlueprintDraft { HasAnchor = true };
        var slots = new HashSet<string>(StringComparer.Ordinal);
        var canonical = new HashSet<TileCoord>();
        foreach (var owner in world.Entities.Objects.Values)
        {
            if (owner.DefinitionId == ContentIds.Hut1Hex) canonical.Add(owner.Tile);
            if (BuildingRules.PlanFor(world, owner) == null) continue;
            var local = PhysicalPlan(world, owner, completedOnly: true);
            foreach (var element in local.Elements)
            {
                var global = ArchitectureWorldEditing.ToWorld(element, local, owner.Tile, owner.RotationDegrees);
                if (!slots.Add(BlueprintBuildingPlan.SlotKey(global))) continue;
                global.Id = $"physical_{physical.Elements.Count}";
                physical.Elements.Add(global);
            }
        }
        foreach (var piece in FreeArchitectureRules.FreePieces(world))
        {
            var state = piece.ArchitectureElements[0];
            if (!state.Complete || !FreeArchitectureRules.TryDecode(state.DefinitionId, state.SlotKey, out var global) ||
                !slots.Add(state.SlotKey)) continue;
            global.Id = $"physical_{physical.Elements.Count}";
            physical.Elements.Add(global);
        }
        var roofsByTile = physical.Elements.Where(e => e.Kind == BlueprintElementKind.RoofSector)
            .GroupBy(e => e.RoofSector.Hex).ToDictionary(g => g.Key, g => g.Count());
        if (roofOnly)
        {
            // Grant-only: completion and load can only ADD a roof. Removal
            // (demolition, revision) runs the full pass below, so this path can
            // never strip a Roofed that another writer (portal flat, legacy
            // hut, CompleteHut footprint) legitimately set.
            foreach (var coord in touched.Distinct())
            {
                if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                    (tile.Flags & TileFlags.Roofed) != 0) continue;
                if (!canonical.Contains(coord) &&
                    !(roofsByTile.TryGetValue(coord, out var count) && count == 6)) continue;
                tile.Flags |= TileFlags.Roofed;
                WorldTopology.NoteTile(world, coord);
            }
            return;
        }
        var regions = BlueprintValidator.CalculateRoomRegions(physical);
        var floorsByTile = physical.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector)
            .GroupBy(e => e.FloorSector.Hex).ToDictionary(g => g.Key, g => g.Count());
        var indoor = new HashSet<TileCoord>(regions.Where(region => BlueprintValidator.IsIndoorRegion(physical, region))
            .SelectMany(region => region.Select(f => f.Hex)));
        foreach (var coord in touched.Distinct())
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile)) continue;
            var legacy = canonical.Contains(coord);
            var hasFloor = legacy || floorsByTile.TryGetValue(coord, out var floorCount) && floorCount == 6;
            SetFlag(tile, TileFlags.HasFloor, hasFloor);
            SetFlag(tile, TileFlags.Roofed, legacy || roofsByTile.TryGetValue(coord, out var roofCount) && roofCount == 6);
            SetFlag(tile, TileFlags.Indoor, legacy || hasFloor && indoor.Contains(coord));
            WorldTopology.NoteTile(world, coord);
        }
    }

    private static void SetFlag(Tile tile, TileFlags flag, bool value)
    {
        if (value) tile.Flags |= flag; else tile.Flags &= ~flag;
    }

    internal static void CompleteDemolition(WorldState world, WorldObjectState piece)
    {
        if (piece.ArchitectureOwnerId is not { } ownerId ||
            !world.Entities.Objects.TryGetValue(ownerId, out var owner)) return;
        var oldTiles = Bootstrap.BuildingBootstrap.FootprintTiles(PhysicalPlan(world, owner), owner.Tile, owner.RotationDegrees);
        var state = piece.ArchitectureElements[0];
        var delivered = new Dictionary<string, int> {
            [ContentIds.Stick] = state.DeliveredSticks, [ContentIds.Board] = state.DeliveredBoards,
            [ContentIds.Rope] = state.DeliveredRope, [ContentIds.PalmLeaf] = state.DeliveredLeaves
        };
        foreach (var pair in delivered)
        {
            var recover = pair.Value / 2;
            for (var i = 0; i < pair.Value; i++)
            {
                var index = owner.Contents.FindIndex(item => item.DefinitionId == pair.Key);
                var item = index >= 0 ? owner.Contents[index] : new ItemInstance(pair.Key);
                if (index >= 0) owner.Contents.RemoveAt(index);
                if (i >= recover || piece.Junctions.Count == 0) continue;
                var junction = world.Junctions.Items[piece.Junctions[0]];
                var anchorTile = HexSpatialMath.WorldToTile(junction.WorldPosition);
                if (!world.Tiles.Items.ContainsKey(anchorTile)) anchorTile = piece.Tile;
                var drop = WorldObjectMutations.SpawnObject(world, pair.Key, junction.Fragment, anchorTile, piece.Junctions[0]);
                drop.Wetness = item.Wetness; drop.Durability = item.Durability;
                drop.Dirtiness = item.Dirtiness; drop.Bloodiness = item.Bloodiness;
                drop.ResourceAmount = item.ResourceAmount; drop.LeafDryingStartTick = item.LeafDryingStartTick;
                drop.Owner = item.OwnerId != 0 ? new EntityId(item.OwnerId) : null;
            }
            var material = Array.IndexOf(BuildSiteMath.AllMaterials, pair.Key);
            if (owner.InstalledBuildMaterials != null && material >= 0)
                owner.InstalledBuildMaterials[material] = Math.Max(0, owner.InstalledBuildMaterials[material] - pair.Value);
        }
        WorldObjectMutations.DespawnObject(world, piece.Id);
        foreach (var npc in world.Entities.Npcs.Values)
            if (npc.Memory.KnownObjects.Remove(piece.Id)) npc.Memory.Version++;
        // Reconcile the already-authorized desired revision: replacements now
        // start EMPTY, while unrelated modules keep their identities/progress.
        var draft = BuildingRules.PlanFor(world, owner);
        if (!Bootstrap.BuildingBootstrap.ApplyBlueprintRevision(world, owner, draft, out var error))
            throw new InvalidOperationException("Authorized demolition could not reconcile its house: " + error);
        RefreshFlags(world, owner, oldTiles);
    }
}

}
