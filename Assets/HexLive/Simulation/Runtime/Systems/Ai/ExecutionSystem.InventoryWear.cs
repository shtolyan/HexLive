using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    // Spec 31A.5B: identify and REMOVE any worn item sharing (layer, part) with
    // the garment about to be worn — molly BodyBones.Equip semantics. §52.7: the
    // displaced pieces are NOT disposed of here — they are collected in
    // _displacedGarments so the caller can don the replacement + recompute the
    // pack capacity FIRST, then call StowDisplacedGarments. That order lets a
    // displaced garment's pocket items relocate INTO the new garment's slots
    // wherever they fit, spilling only the true remainder.
    private static readonly System.Collections.Generic.List<string> _conflictScratch = new();
    private static readonly System.Collections.Generic.List<ItemInstance> _displacedGarments = new();

    private static void ResolveWearConflicts(WorldState world, NPCState npc, string newItemId)
    {
        _displacedGarments.Clear();
        if (!world.Content.ObjectDefinitions.TryGetValue(newItemId, out var newDefinition))
        {
            return;
        }

        _conflictScratch.Clear();
        foreach (var wornId in npc.WornItems)
        {
            // §52.9: occupancy is (layer, SLOT) — one predicate, the same one
            // the prefab's BodyBones.Equip uses. Covers is far too coarse to
            // displace by (thigh holster, stockings and boots all read
            // "LegL+LegR"); WearSlotCatalog mirrors the prefab's fine slots and
            // falls back to Covers only for unauthored art.
            if (world.Content.ObjectDefinitions.TryGetValue(wornId, out var wornDefinition) &&
                WearSlotCatalog.Occupies(newDefinition, wornDefinition))
            {
                _conflictScratch.Add(wornId);
            }
        }

        foreach (var conflictId in _conflictScratch)
        {
            var conflictItem = npc.WornItems.Find(i => i.DefinitionId == (string)conflictId) ??
                new ItemInstance(conflictId);
            npc.WornItems.Remove(conflictItem);
            _displacedGarments.Add(conflictItem);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ItemReplaced",
                    $"{conflictId} taken off (layer conflict with {newItemId})");
            }
        }
    }

    // §52.9 r2: where a garment displaced by the most recent ResolveWearConflicts
    // GOES. Swapping one pair of panties for another must not litter the beach:
    // the old piece is FOLDED INTO THE PACK when a pocket is free, and only falls
    // to the ground when there is none (DropGarmentWithContents — the overflow
    // rides down inside it, lowest importance first, §52.3).
    //
    // Call AFTER WornItems.Add(new) + Recalculate, so the capacity being tested
    // is the live one — the replacement's pockets are already counted and the
    // displaced piece's are already gone. Items that still fit stay in the pack
    // ("moved into the new garment"); the true remainder spills at the end,
    // which is also what evicts the stowed piece itself if it never fitted.
    private static void StowDisplacedGarments(WorldState world, NPCState npc)
    {
        for (var i = 0; i < _displacedGarments.Count; i++)
        {
            var garment = _displacedGarments[i];
            if (npc.Inventory.HasSpace)
            {
                // No MakeRoomFor: a swapped-out shirt never outranks what is
                // already carried — food and tools are not shed to fold laundry.
                npc.Inventory.Items.Add(garment);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "GarmentStowed",
                        $"{garment.DefinitionId} folded into the pack " +
                        $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");
                }
            }
            else
            {
                if (DropGarmentWithContents(world, npc, garment) is null)
                    npc.Inventory.Items.Add(garment);
            }
        }

        _displacedGarments.Clear();
        // Taking the piece off took its pockets with it — whatever no longer
        // fits lands at her feet (the stowed garment included, if it is the
        // least important thing she carries).
        InventoryMath.SpillOverflow(world, npc);
    }

    // §35.5B: the rack holds up to SimBalance.RackCapacity garments — the
    // wearables at its junction, one per hanger slot on the assembled prefab.
    internal static bool RackIsFull(WorldState world, WorldObjectState rack)
    {
        if (rack.Junctions.Count == 0)
        {
            return true;
        }

        // §133: у гардероба своя ёмкость — считаем по той станции, о которой речь.
        var capacity = world.Content.ObjectDefinitions.TryGetValue(rack.DefinitionId, out var rackDef) &&
            rackDef.HasTag(ObjectTags.Wardrobe)
            ? Spec133.WardrobeCapacity
            : SimBalance.RackCapacity;

        var hung = 0;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Id.Value != rack.Id.Value && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(rack.Junctions[0]) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Layer is not null)
            {
                hung++;
                if (hung >= capacity)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static ItemInstance? FindWettestWornItem(NPCState npc)
    {
        ItemInstance? wettest = null;
        foreach (var item in npc.WornItems)
        {
            if (wettest is null || item.Wetness > wettest.Wetness)
            {
                wettest = item;
            }
        }

        return wettest;
    }

    // Spec 29G: crafted furniture lands on a free junction by the fire.
    private static void PlaceCraftedFurniture(WorldState world, NPCState npc, WorldObjectState campfire, string definitionId)
    {
        var spot = FindSpacedFurnitureSpot(world, campfire, definitionId) ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            return;
        }

        WorldObjectMutations.SpawnObject(world, definitionId, npc.Fragment, npc.Tile, junction);
    }

    // Spec 35.7 (iter 33): the camp is no longer a heap. Crafted furniture
    // still hugs the fire (keeping travel cheap — the economy is tight), but
    // never lands right on top of another bed/rack: prefer a fireside junction
    // that isn't within a tile of an existing piece, widening the search ring
    // only if the near ones are all taken.
    private static readonly string[] OtherFurnitureTags = { "Bed", "Rack" };

    private static JunctionId? FindSpacedFurnitureSpot(
        WorldState world, WorldObjectState campfire, string definitionId)
    {
        if (campfire.Junctions.Count == 0)
        {
            return null;
        }

        // §54.9A: the piece must PHYSICALLY fit — its footprint (ObstacleRadius
        // measured off the real prefab) may not cross boulders, palms, the
        // ember ring, or other furniture.
        var footprint = world.Content.ObjectDefinitions.TryGetValue(definitionId, out var placedDef)
            ? placedDef.ObstacleRadius
            : 0f;

        var anchor = campfire.Junctions[0];
        // Pass 1: a free junction on the standable rim just OUTSIDE the
        // fire's blocked ember ring (§47). GetPassableNeighbors would look
        // at the anchor's immediate neighbors — all inside the blocked ring
        // now — so we use the same beside-arrival BFS the planner uses:
        // furniture lands in the passable zone with a natural offset from
        // the flames, still fireside-close.
        SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch,
            96, float.MaxValue, campfire, InteractionReach.RimMode);
        var firePosition = world.Junctions.Items.TryGetValue(anchor, out var fireJunction)
            ? fireJunction.WorldPosition
            : HexSpatialMath.TileToWorld(campfire.Tile);
        foreach (var neighbor in _furnitureRimScratch)
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                world.Junctions.Items.TryGetValue(neighbor, out var j) && j.Tiles.Count > 0 &&
                // §47.1 r2: the ember disc shrank to 0.4R so the WORK ring hugs
                // the stones — furniture (rack/tent carry no footprint of their
                // own) must not: keep placements off the toe-to-stone ring.
                HexSpatialMath.Distance(j.WorldPosition, firePosition) >= 1.1f &&
                !IsNearOtherFurniture(world, j.Tiles[0]) &&
                SpatialQueries.FootprintClear(world, j, footprint))
            {
                return neighbor;
            }
        }

        // Pass 2: any junction 2 tiles out that's clear of other furniture.
        JunctionId? ring = null;
        // §158.4: «два тайла от костра» — это кольцо тайлов, а не весь граф.
        var ringSeen = world.Caches.LocalSearchSeenScratch;
        ringSeen.Clear();
        var ringJunctions = world.Caches.LocalSearchRingScratch;
        LocalSearch.CollectRing(world, campfire.Tile, 2, ringJunctions, ringSeen);
        foreach (var junction in ringJunctions)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Walkable) || tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            if (HexSpatialMath.HexDistance(junction.Tiles[0], campfire.Tile) == 2 &&
                !IsNearOtherFurniture(world, junction.Tiles[0]) &&
                SpatialQueries.FootprintClear(world, junction, footprint))
            {
                ring = junction.Id;
                break;
            }
        }

        // Pass 3: fall back to any free rim junction (heap beats nowhere).
        if (ring is null)
        {
            SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch,
            96, float.MaxValue, campfire, InteractionReach.RimMode);
            foreach (var neighbor in _furnitureRimScratch)
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor))
                {
                    return neighbor;
                }
            }
        }

        return ring;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _furnitureRimScratch = new();

    // Within 1 tile of an existing bed or rack (the campfire itself is fine
    // to sit beside — we only want to avoid stacking furniture on furniture).
    private static bool IsNearOtherFurniture(WorldState world, TileCoord tile)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            foreach (var tag in OtherFurnitureTags)
            {
                if (def.HasTag(tag) && HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Spec 35.5: the crafted rack goes onto a free junction next to the
    // campfire; when the fireside is crowded it lands at the crafter's feet.
    private static void PlaceRack(WorldState world, NPCState npc, WorldObjectState campfire)
    {
        // Spec 35.7: spaced away from the fire and the bed (no more heap).
        var spot = FindSpacedFurnitureSpot(world, campfire, ContentIds.DryingRack) ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            GiveOrDrop(world, npc, ContentIds.Stick);
            GiveOrDrop(world, npc, ContentIds.Stick);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed", "CraftRack: nowhere to place the rack");

            }
            return;
        }

        var rack = WorldObjectMutations.SpawnObject(
            world, ContentIds.DryingRack, npc.Fragment, npc.Tile, junction);
        Trace.Emit(world, npc.Id, "RackCrafted",
            $"Obj={rack.Id.Value} Junction={junction.Value}");
    }

    // Spec §52: take a garment off the body and lay it on the ground carrying
    // its pockets. Any pocket item that no longer fits the (now smaller) pack
    // rides down inside the dropped garment — the NPC still knows where its
    // bottle is and can fetch it later without dressing.
    internal static WorldObjectState DropGarmentWithContents(
        WorldState world, NPCState npc, ItemInstance garment)
    {
        // Spawn before mutating the pack.  A missing ground point must leave
        // the authoritative carried instances untouched rather than stranded
        // in the shared scratch list.
        var dropped = DropItemAtFeet(world, npc, garment, underFoot: true);
        if (dropped is null)
        {
            return null;
        }

        StashOverflowInDroppedGarment(world, npc, garment, dropped);
        OutfitMaintenanceMath.TrackGroundPiece(world, npc, garment, dropped);
        return dropped;
    }

    internal static void StashOverflowInDroppedGarment(
        WorldState world,
        NPCState npc,
        ItemInstance garment,
        WorldObjectState dropped)
    {
        _garmentSpillScratch.Clear();
        var inv = npc.Inventory;
        var guard = 0;
        while (inv.UsedSlots > inv.Capacity && guard++ < 64)
        {
            var victim = InventoryMath.LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            InventoryMath.RemoveReference(inv.Items, victim);
            _garmentSpillScratch.Add(victim);
        }

        if (_garmentSpillScratch.Count > 0)
        {
            dropped.Contents.AddRange(_garmentSpillScratch);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "StashedInGarment",
                    $"{garment.DefinitionId} holds [{string.Join(",", _garmentSpillScratch)}]");
            }
        }
    }

    /// <summary>
    /// §133: снять вещь и повесить её на гардероб/сушилку, если та указана и в
    /// ней ещё есть место; иначе — обычная куча под ноги. Карманы едут внутри
    /// вещи в обоих случаях (§52), владение сохраняется (§133).
    /// </summary>
    internal static WorldObjectState StowGarmentWithContents(
        WorldState world, NPCState npc, ItemInstance garment, ObjectId? stowObjectId)
    {
        if (stowObjectId is not { } stowId ||
            !world.Entities.Objects.TryGetValue(stowId, out var station) ||
            station.Junctions.Count == 0 || RackIsFull(world, station))
        {
            return DropGarmentWithContents(world, npc, garment);
        }

        _garmentSpillScratch.Clear();
        var inv = npc.Inventory;
        var guard = 0;
        while (inv.UsedSlots > inv.Capacity && guard++ < 64)
        {
            var victim = InventoryMath.LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            InventoryMath.RemoveReference(inv.Items, victim);
            _garmentSpillScratch.Add(victim);
        }

        // Рецепт тот же, что у CompleteHang: вещь становится объектом на
        // джанкшене станции и наследует её поворот (§66 — иначе висит мимо).
        var hung = WorldObjectMutations.SpawnObject(
            world, garment.DefinitionId, npc.Fragment, station.Tile, station.Junctions[0]);
        hung.RotationDegrees = station.RotationDegrees;
        hung.Wetness = garment.Wetness;
        hung.Durability = garment.Durability;
        hung.Dirtiness = garment.Dirtiness;
        hung.Bloodiness = garment.Bloodiness;
        hung.ResourceAmount = garment.ResourceAmount;
        hung.WaterKind = garment.WaterKind;
        hung.Owner = garment.OwnerId != 0 ? new EntityId(garment.OwnerId) : npc.Id;
        if (_garmentSpillScratch.Count > 0)
        {
            hung.Contents.AddRange(_garmentSpillScratch);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GarmentStowed",
                $"{garment.DefinitionId} onto Obj={station.Id.Value} " +
                $"({station.DefinitionId}) holds [{string.Join(",", _garmentSpillScratch)}]");
        }

        OutfitMaintenanceMath.TrackGroundPiece(world, npc, garment, hung);

        return hung;
    }

    private static readonly System.Collections.Generic.List<ItemInstance> _garmentSpillScratch = new();

    private static readonly System.Collections.Generic.List<ItemInstance> _dressPourScratch = new();

    private static readonly System.Collections.Generic.List<string> _stashRecoverScratch = new();

    // Spec §52: rifle a dropped garment's pockets for the tools the NPC lacks —
    // the tools come home, the garment (and any non-tool stash) stays on the
    // ground. This is how a knife left in an undressed jacket comes back
    // without re-dressing (seed 1104049673: both knives rode a doffed jacket
    // to the ground and their owner died of thirst two hexes away).
    private static void RecoverStashedTools(WorldState world, NPCState npc, WorldObjectState stash)
    {
        _stashRecoverScratch.Clear();
        for (var i = stash.Contents.Count - 1; i >= 0; i--)
        {
            var item = stash.Contents[i];
            if (npc.Inventory.Items.Contains(item.DefinitionId) ||
                !world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) ||
                !def.HasTag("Tool") ||
                !InventoryMath.MakeRoomFor(world, npc, item.DefinitionId))
            {
                continue;
            }

            stash.Contents.RemoveAt(i);
            npc.Inventory.Items.Add(item);
            _stashRecoverScratch.Add(item.DefinitionId);
        }

        stash.IsOccupied = false;
        stash.CurrentUser = null;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "StashRecovered",
                $"{stash.DefinitionId} pockets returned [{string.Join(",", _stashRecoverScratch)}] " +
                $"left [{string.Join(",", stash.Contents)}] " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
        }
    }

    /// <summary>Take one exact completed recipe output from a perceived stash.
    /// Unlike GatherTools this is definition-specific and therefore works for
    /// medicine, rope, splints, prostheses, and future item recipes alike.</summary>
    private static bool RecoverCraftOutputFromStash(
        WorldState world, NPCState npc, WorldObjectState stash, string outputId)
    {
        for (var i = 0; i < stash.Contents.Count; i++)
        {
            var item = stash.Contents[i];
            if (item.DefinitionId != outputId)
            {
                continue;
            }

            if (!InventoryMath.MakeRoomForGoal(world, npc, npc.Plan.Goal, outputId))
            {
                break;
            }

            stash.Contents.RemoveAt(i);
            npc.Inventory.Items.Add(item);
            stash.IsOccupied = false;
            stash.CurrentUser = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CraftOutputRecovered",
                    $"Goal={npc.Plan.Goal} Output={outputId} " +
                    $"Container={stash.Id.Value} left=[{string.Join(",", stash.Contents)}]");
            }
            return true;
        }

        stash.IsOccupied = false;
        stash.CurrentUser = null;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PickupBlocked",
                $"Goal={npc.Plan.Goal} Output={outputId} missing/full stash={stash.Id.Value}");
        }
        return false;
    }

    // Spec 31A.5A: take off a worn item in place; it drops to the world at
    // the NPC's feet, retrievable by anyone.
    // §Wardrobe-anim: 8 ticks = 2.0s at 0.25s/tick — matches the dress window.
    private const int UndressDurationTicks = 8;

    // §Wardrobe-anim: the fraction of a dress/undress window at which the
    // garment changes hands. Dressing: gather (before) -> don the piece (after).
    // Undressing: doff the piece (before) -> gather it up off the body (after),
    // so the garment leaves the body here and is only dropped at the very end.
    internal const float WardrobeHandoffFraction = 0.5f;

    private static void RunUndressItem(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Mind.OutfitLocked)
        {
            // The toggle may arrive after the visual handoff, when ordinary
            // Undress has already moved the authoritative instance from worn
            // to HeldGarment. Put that SAME instance back before cancelling;
            // clearing the hand here used to destroy the garment outright.
            if (npc.Execution.HeldGarment is { } held &&
                !InventoryMath.ContainsReference(npc.WornItems, held))
            {
                npc.WornItems.Add(held);
                EquipmentMath.Recalculate(world, npc);
            }

            npc.Plan.Status = PlanStatus.Failed;
            npc.Execution.HeldGarment = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "UndressBlocked", "Reason=OutfitLocked");
            }
            return;
        }

        var itemId = npc.Plan.TargetItemDefinitionId;

        // §133: если план вёл домой — раздеваемся, только дойдя до места.
        if (npc.Execution.Status == ExecutionStatus.None && step.TargetJunction is { } stand)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.CurrentJunction is not { } here || !here.Equals(stand))
            {
                // Дорога домой закрыта — не стоять же одетой в пекле: снимаем
                // здесь, как раньше (тот же «крайний случай»).
                if (npc.Movement.Status != MovementStatus.Blocked)
                {
                    return;
                }

                step.TargetJunction = null;
                step.TargetObject = null;
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "UndressAtHomeAborted",
                        "route home blocked; undressing where she stands");
                }
            }
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (itemId is null || !npc.WornItems.Contains(itemId))
            {
                npc.Plan.Status = PlanStatus.Failed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecFailed",
                        $"UndressItem: '{itemId ?? "-"}' is not worn");
                }
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.TargetObject = null;
            npc.Execution.HeldGarment = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"Undress -> {itemId} Duration={UndressDurationTicks}ticks");
            }
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            // §Wardrobe-anim beat 1 -> 2: at the handoff the piece comes OFF the
            // body and INTO the hand — warmth/armor drop here — but it isn't laid
            // on the floor until the gather beat finishes (see below).
            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var elapsed = world.Tick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)elapsed / total : 1f;
            if (npc.Execution.HeldGarment is null &&
                progress >= WardrobeHandoffFraction &&
                itemId is not null && npc.WornItems.Contains(itemId))
            {
                var doffed = npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                    new ItemInstance(itemId);
                InventoryMath.RemoveReference(npc.WornItems, doffed);
                EquipmentMath.Recalculate(world, npc);
                npc.Execution.HeldGarment = doffed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "GarmentInHand",
                        $"Undress {itemId} doffed to hand " +
                        $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                }
            }

            if (npc.Execution.EndTick - world.Tick > 0)
            {
                return;
            }

            // Beat 2 finished: the garment gathered up off the body lands on
            // the floor (preserving the doffed instance's wetness/durability).
            var wornItem = npc.Execution.HeldGarment ??
                npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                new ItemInstance(itemId);
            InventoryMath.RemoveReference(npc.WornItems, wornItem);
            npc.Execution.HeldGarment = null;
            EquipmentMath.Recalculate(world, npc);
            // Spec §52: the garment carries down whatever pocket items no longer
            // fit — they wait inside it on the ground, retrievable later.
            // §133: дошла до гардероба/сушилки — вещь вешается туда, а не
            // остаётся лежать под ногами.
            StowGarmentWithContents(world, npc, wornItem, step.TargetObject);

            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ItemUndressed",
                    $"{itemId} dropped at Tile={npc.Tile.Q},{npc.Tile.R} " +
                    $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
            }

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CycleReset",
                    "Goal->None Plan->Completed (undressed)");
            }
        }
    }

    private static void RunDropInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed", "DropInventoryItem: no target item");

            }
            return;
        }

        ItemInstance? item = null;
        foreach (var carried in npc.Inventory.Items)
        {
            if (carried.DefinitionId == itemId)
            {
                item = carried;
                break;
            }
        }

        if (item is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed", $"DropInventoryItem: '{itemId}' not in inventory");

            }
            return;
        }

        InventoryMath.RemoveReference(npc.Inventory.Items, item);
        var dropped = DropItemAtFeet(world, npc, item);
        if (dropped is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed", $"DropInventoryItem: no drop junction for '{itemId}'");

            }
            return;
        }

        npc.Plan.TargetObjectId = dropped.Id;
        npc.Plan.TargetTile = dropped.Tile;
        npc.Plan.TargetJunctionId = dropped.Junctions.Count > 0 ? dropped.Junctions[0] : npc.CurrentJunction;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.Steps.RemoveAt(0);
        npc.Plan.CurrentStepIndex = 0;

        foreach (var step in npc.Plan.Steps)
        {
            if (step.Type == PlanStepType.Interact)
            {
                step.TargetObject = dropped.Id;
                step.TargetJunction = npc.Plan.TargetJunctionId;
            }
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ItemDropped",
                $"{itemId} placed on ground Obj={dropped.Id.Value} for {npc.Plan.Goal}");
        }
    }

    // In-place consumption from inventory (spec 29B.3): no world object,
    // no junction reservation, executable wherever the NPC stands.
    private static void RunConsumeInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null ||
            !world.Content.ObjectDefinitions.TryGetValue(itemId, out var itemDefinition))
        {
            npc.Plan.Status = PlanStatus.Failed;
            npc.Execution.TargetInventoryItem = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    $"ConsumeInventoryItem: item '{itemId ?? "-"}' not in inventory or unknown definition");
            }
            return;
        }

        // §55: in-place consume now covers both Eat (food) and Drink (crack a
        // coconut). The verb comes from the plan step, so one handler serves
        // both; a Drink can carry Yields (the opened-coconut husk).
        var verb = npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Interaction.HasValue
            ? npc.Plan.Steps[0].Interaction.Value
            : InteractionType.Eat;
        var interaction = ResolveInteraction(world, npc, itemDefinition, verb);
        if (interaction is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            npc.Execution.TargetInventoryItem = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    $"ConsumeInventoryItem: '{itemId}' has no {verb} interaction");
            }
            return;
        }

        var starting = npc.Execution.Status == ExecutionStatus.None;
        var item = starting
            ? FindConsumableInventoryItem(npc, itemId, verb, itemDefinition)
            : npc.Execution.TargetInventoryItem;
        if (item is null || item.DefinitionId != itemId ||
            (!starting && !InventoryMath.ContainsReference(npc.Inventory.Items, item)) ||
            (IsPortableCoconutDrink(itemDefinition, verb) && item.ResourceAmount <= 0f))
        {
            npc.Plan.Status = PlanStatus.Failed;
            npc.Execution.TargetInventoryItem = null;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    $"ConsumeInventoryItem: '{itemId}' not available for {verb}");
            }
            return;
        }

        if (starting)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = verb;
            npc.Execution.TargetObject = null;
            // ItemInstance equality is deliberately definition-based. Keep
            // the exact physical coconut/food instance across the timed
            // interaction so an inventory reorder cannot redirect a sip to
            // another equal item with different ResourceAmount.
            npc.Execution.TargetInventoryItem = item;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + interaction.DurationTicks;

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"{verb} (inventory) -> {itemId} " +
                    $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                    $"EndTick={npc.Execution.EndTick} " +
                    $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
                    $"T={interaction.Effects.ThirstDelta:+0.00;-0.00} " +
                    $"C={interaction.Effects.ComfortDelta:+0.00;-0.00}]");
            }
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            if (remaining > 0)
            {
                var progress = total > 0 ? 1f - (float)remaining / total : 1f;
                // Spec 29C.9: hunger drops mouthful by mouthful, not in a jump.
                if (total > 0)
                {
                    ApplyEffectsScaled(npc, interaction.Effects, 1f / total);
                }

                if (SimTrace.Enabled)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "ExecProgress",
                            $"{verb} (inventory) Progress={progress:P0} " +
                            $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                    }
                }

                return;
            }

            var needsBefore = Trace.FormatNeeds(npc.Needs);
            ApplyEffectsScaled(npc, interaction.Effects, total > 0 ? 1f / total : 1f);
            if (IsPortableCoconutDrink(itemDefinition, verb))
            {
                item.ResourceAmount = System.MathF.Max(0f, item.ResourceAmount - 1f);

                // Jul 2026 (thirst-death class): a truly thirsty girl keeps
                // sipping the same coconut in place — each sip is the ordinary
                // interaction at the ordinary cost, without a full goal
                // re-auction round-trip between mouthfuls (which used to hand
                // the coconut to Eat and the girl to another errand while her
                // thirst stayed critical).
                // …but never sip past a STARVING stomach — with hunger at the
                // damage band the auction must rebalance toward Eat between
                // mouthfuls (iter-8: Marta died at Hunger 1.0 holding an open
                // coconut she never got to eat because the sips kept coming).
                if (npc.Needs.Thirst >= 0.4f && item.ResourceAmount > 0f &&
                    npc.Needs.Hunger < 0.85f)
                {
                    npc.Execution.Status = ExecutionStatus.InProgress;
                    npc.Execution.StartTick = world.Tick;
                    npc.Execution.EndTick = world.Tick + interaction.DurationTicks;
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "InteractionStarted",
                            $"{verb} (inventory, next sip) -> {itemId} " +
                            $"Duration={interaction.DurationTicks}ticks " +
                            $"WaterLeft={item.ResourceAmount:F0} Thirst={npc.Needs.Thirst:F2}");
                    }
                    return;
                }
            }
            else
            {
                InventoryMath.RemoveReference(npc.Inventory.Items, item);
                // §55: a consumed item may transform rather than vanish — cracking a
                // coconut (Drink) yields the opened husk straight into the hand.
                foreach (var yield in interaction.Yields)
                {
                    for (var n = 0; n < yield.Count; n++)
                    {
                        npc.Inventory.Items.Add(yield.DefinitionId);
                    }
                }
            }
            var needsAfter = Trace.FormatNeeds(npc.Needs);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ItemConsumed",
                    $"{itemId} {verb} from inventory " +
                    $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}] " +
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]" +
                    (IsPortableCoconutDrink(itemDefinition, verb)
                        ? $" CoconutWaterLeft={item.ResourceAmount:F0}"
                        : string.Empty));
            }

            // §54.17: the whole meat chain's finish line — hunt/butcher/cook
            // metrics read THIS, not ItemConsumed (which verbose-traces every
            // coconut). Player-visible: it closes the story MeatRoasted opens.
            if (verb == InteractionType.Eat && itemId == ContentIds.MeatCooked)
            {
                Trace.Emit(world, npc.Id, "MeatEaten",
                    $"{itemId} eaten NeedsAfter=[{needsAfter}]");
            }

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.TargetInventoryItem = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CycleReset",
                    "Goal->None Plan->Completed Execution->Cleared (ate from inventory)");
            }
        }
    }

    private static ItemInstance? FindConsumableInventoryItem(
        NPCState npc,
        string definitionId,
        InteractionType verb,
        ObjectDefinition definition)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId != definitionId)
            {
                continue;
            }

            if (IsPortableCoconutDrink(definition, verb) && item.ResourceAmount <= 0f)
            {
                continue;
            }

            return item;
        }

        return null;
    }

    private static bool IsPortableCoconutDrink(ObjectDefinition definition, InteractionType verb) =>
        verb == InteractionType.Drink && definition.HasTag("CoconutWater");

    // §55.4 (bug #317): перелить воду вскрытых кокосов инвентаря в личную
    // бутылку — на месте, небыстро (FillVesselDurationTicks, прогресс в
    // Execution). Кокосы теряют ResourceAmount, бутылка получает глотки 1:1;
    // пустая бутылка становится Coconut, непустая сохраняет свой вид воды.
    // Шаг снимает себя из плана: автономный план [FillVessel, DrinkBottle]
    // продолжается штатным питьём, ручной одношаговый — завершается.
    private static void RunFillVessel(WorldState world, NPCState npc)
    {
        var starting = npc.Execution.Status == ExecutionStatus.None;
        var hasPhysicalSelection = npc.Plan.Steps.Count > 0 &&
            npc.Plan.Steps[0].TimeoutEndTick.HasValue;
        ItemInstance targetBottle = null;
        if (starting && hasPhysicalSelection)
        {
            // Manual admission already resolved the clicked physical slot.
            // Do not resolve its index again here: another equal bottle can
            // occupy that index before the first execution tick.
            targetBottle = npc.Execution.TargetInventoryItem;
        }
        else if (starting)
        {
            targetBottle = BottleInventoryMath.FirstWithRoomFor(npc, WaterKind.Coconut);
        }
        else
        {
            // Once the delayed action has started, list position is no longer
            // identity: inventory management may insert or move equal bottles.
            // Continue only with the exact instance reserved at start.
            targetBottle = npc.Execution.TargetInventoryItem;
        }

        var targetPresent = targetBottle is not null &&
            targetBottle.DefinitionId == ContentIds.Bottle &&
            InventoryMath.ContainsReference(npc.Inventory.Items, targetBottle);
        if ((starting && hasPhysicalSelection && !targetPresent) ||
            (!starting && npc.Execution.Status == ExecutionStatus.InProgress && !targetPresent))
        {
            PlanInterruption.TryAbort(
                world, npc, InterruptionCause.ExecutionFailure,
                "FillVessel: selected physical bottle changed or disappeared");
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    "FillVessel: selected physical bottle changed or disappeared");
            }
            return;
        }

        if (starting)
        {
            if (!VesselTransferMath.CanFillBottle(npc, targetBottle))
            {
                npc.Plan.Status = PlanStatus.Failed;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecFailed",
                        "FillVessel: nothing to pour, or the bottle is full/lost");
                }
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.FillVessel;
            npc.Execution.TargetObject = null;
            npc.Execution.TargetInventoryItem = targetBottle;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.FillVesselDurationTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"FillVessel Duration={SimBalance.FillVesselDurationTicks}ticks " +
                    $"Charges={BottleInventoryMath.Charges(targetBottle)} " +
                    $"CoconutSips={VesselTransferMath.CoconutSips(npc)}");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        var moved = VesselTransferMath.FillBottleFromCoconuts(npc, targetBottle);
        if (moved > 0 && SimTrace.Enabled)
        {
            // Диагностика, не хроника: рядовой быт (как VesselPlaced/Taken).
            Trace.Debug(world, npc.Id, "VesselFilled",
                $"Poured {moved} sips into tool.bottle " +
                $"{targetBottle?.WaterKind} x{BottleInventoryMath.Charges(targetBottle)}");
        }

        npc.Plan.Steps.RemoveAt(0);
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetInventoryItem = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        if (npc.Plan.Steps.Count == 0)
        {
            npc.Plan.Status = moved > 0 ? PlanStatus.Completed : PlanStatus.Failed;
            npc.Mind.CurrentGoal = GoalType.None;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CycleReset",
                    "Goal->None Plan->Done Execution->Cleared (vessel fill)");
            }
        }
    }

    // Spec 29H: drink in place from the carried bottle — thirst quenched,
    // raw water carries the 30 % sickness roll, then the bottle empties.
    private static int DrinkBottleDurationTicks => SimBalance.DrinkBottleDurationTicks;

    private static void RunDrinkBottle(WorldState world, NPCState npc)
    {
        // §52 / bug #355: reserve one real filled bottle for the whole sip.
        // Inventory may be managed while AI is acting, so choosing "first"
        // again on every tick could start with Raw and finish from another
        // same-definition bottle. Reference identity is sufficient inside the
        // running world; after save/load the transient reservation is absent
        // and the interrupted step fails safely instead of drinking a guess.
        var starting = npc.Execution.Status == ExecutionStatus.None;
        var bottle = starting
            ? BottleInventoryMath.FirstDrinkable(npc)
            : npc.Execution.TargetInventoryItem;
        var bottlePresent = bottle is not null &&
            bottle.DefinitionId == ContentIds.Bottle &&
            BottleInventoryMath.Charges(bottle) > 0 &&
            InventoryMath.ContainsReference(npc.Inventory.Items, bottle);
        if (!bottlePresent)
        {
            PlanInterruption.TryAbort(
                world, npc, InterruptionCause.ExecutionFailure,
                "DrinkBottle: selected physical bottle is empty, moved or lost");
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ExecFailed",
                    "DrinkBottle: selected physical bottle is empty, moved or lost");

            }
            return;
        }

        if (starting)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Drink;
            npc.Execution.TargetObject = null;
            npc.Execution.TargetInventoryItem = bottle;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + DrinkBottleDurationTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"Drink (bottle:{bottle.WaterKind}) Duration={DrinkBottleDurationTicks}ticks");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // Bug #305: один глоток — 100 мл (литровая бутылка = 10 глотков), и
        // его облегчение (DrinkThirstRaw/Boiled) задано ЗА ГЛОТОК. Spec 29C.9:
        // the thirst drops gulp by gulp across the duration, not in one jump.
        // §54.15: RAIN water (the collector's leaf funnel, no ground contact)
        // is clean — boiled-grade thirst relief and NO sickness roll; only
        // the warm-drink comfort bonus stays boiled-only.
        // §55.4 / bug #347: COCONUT keeps the same data-driven effects as a
        // direct sip from food.coconut_pierced and never enters the Raw roll.
        var raw = bottle.WaterKind == WaterKind.Raw;
        var boiled = bottle.WaterKind == WaterKind.Boiled;
        var thirstTotal = raw ? SimBalance.DrinkThirstRaw : SimBalance.DrinkThirstBoiled;
        var comfortTotal = boiled ? SimBalance.DrinkComfortBoiled : 0f;
        if (bottle.WaterKind == WaterKind.Coconut &&
            world.Content.ObjectDefinitions.TryGetValue(
                ContentIds.CoconutPierced, out var coconutDefinition) &&
            ResolveInteraction(
                world, npc, coconutDefinition, InteractionType.Drink) is { } coconutDrink)
        {
            thirstTotal = System.MathF.Max(0f, -coconutDrink.Effects.ThirstDelta);
            comfortTotal = System.MathF.Max(0f, coconutDrink.Effects.ComfortDelta);
        }
        var share = 1f / DrinkBottleDurationTicks;
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst - thirstTotal * share);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfortTotal * share);
        npc.EffectImpacts.Record(
            NeedKind.Thirst,
            EffectKind.Drinking,
            EffectImpactDirection.Positive,
            EffectImpactCadence.Fast);
        if (comfortTotal > 0f)
        {
            npc.EffectImpacts.Record(
                NeedKind.Comfort,
                EffectKind.Drinking,
                EffectImpactDirection.Positive,
                EffectImpactCadence.Fast);
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Spec 29H: raw water is a gamble — sickness roll (moved here from
        // the old water-edge Drink now that filling and drinking are split).
        // §45 r4: eased 30%/-0.15 -> 15%/-0.08. The colony drinks raw
        // 70-105 times per 15 days (boiled is ~2% of drinks — the fire is
        // dead ~90% of the time), so the old odds ground through 3-5 full
        // torsos per soak: half of all deaths were "Torso destroyed by
        // sickness". The gamble stays (chronic cough), but expected damage
        // (~0.012/drink) now sits within fed-regen's budget instead of
        // being a guaranteed death sentence for a fireless colony.
        // §54.15: only RAW water gambles — rain (like boiled) is safe.
        if (raw)
        {
            var sickRoll = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 833);
            if (sickRoll < SimBalance.RawWaterSickChance && !Spec49.SickDoT)
            {
                // Baseline path (pre-§49): instant lump — torso -0.08 (floor 0.1,
                // only above 0.2) + comfort -0.2. Kept for harness A/B bisect.
                var beforeTorso = npc.Body.Parts[BodyPart.Torso];
                if (npc.Body.Parts[BodyPart.Torso] > 0.2f)
                {
                    npc.Body.Parts[BodyPart.Torso] =
                        System.Math.Max(0.1f, npc.Body.Parts[BodyPart.Torso] - 0.08f);
                }
                npc.Health = npc.Body.Mean();
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.2f);
                npc.EffectImpacts.Record(
                    NeedKind.Comfort,
                    EffectKind.Sick,
                    EffectImpactDirection.Negative,
                    EffectImpactCadence.Fast);
                // §105: через общую развилку (пол 0.1 не даёт болезни доломать
                // грудь — ветка живёт ради единственности ответа).
                MortalityHelpers.ResolveTrauma(
                    world, npc, beforeTorso - npc.Body.Parts[BodyPart.Torso], "sickness");
                DamageReactionSystemHelpers.GrantAdrenaline(
                    world, npc, beforeTorso - npc.Body.Parts[BodyPart.Torso], "Sickness");
                Trace.Emit(world, npc.Id, "GotSick", $"Raw water (Roll={sickRoll:F2}) instant");
            }
            else if (sickRoll < SimBalance.RawWaterSickChance)
            {
                // Spec §49: don't lump the harm here. Add a BOUNDED torso-damage
                // budget the DoT pays down over the next hours (visible 🤢), and
                // open the icon/malaise window. The budget cap is the key: a
                // thirsty colony drinking raw back-to-back opens overlapping
                // windows, and without the cap the DoT would grind continuously
                // (this exact bug wiped seed 12345). Capped, total harm ≈ the old
                // instant -0.08 model, so the §45/§46 balance holds.
                npc.Mind.SicknessDamageRemaining = System.Math.Min(
                    SicknessDamageBudgetCap, npc.Mind.SicknessDamageRemaining + SicknessDamagePerBout);
                npc.Mind.SickUntilTick =
                    System.Math.Max(npc.Mind.SickUntilTick, world.Tick) + SicknessDurationTicks;
                Trace.Emit(world, npc.Id, "GotSick",
                    $"Raw water (Roll={sickRoll:F2}) DmgBudget={npc.Mind.SicknessDamageRemaining:F2}");
            }
        }

        // Spec §52: spend one gulp; the bottle only empties when the last is gone.
        var consumedKind = bottle.WaterKind;
        BottleInventoryMath.ConsumeOne(bottle, out _);
        var remaining = BottleInventoryMath.Charges(bottle);
        var driedOut = remaining <= 0;
        Trace.Emit(world, npc.Id, "DrankBottle",
            $"{consumedKind} water Thirst={npc.Needs.Thirst:F2} Left={remaining}");

        // Bug #305: глоток теперь 100 мл, и одной жаждущей его мало — пьёт
        // следующий сразу, тем же правилом, что кокосовые глотки (без полного
        // пере-аукциона целей между глотками, но никогда мимо голодающего
        // желудка — урок iter-8).
        if (!driedOut && npc.Needs.Thirst >= 0.4f && npc.Needs.Hunger < 0.85f)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + DrinkBottleDurationTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"Drink (bottle, next sip) Left={remaining} Thirst={npc.Needs.Thirst:F2}");
            }
            return;
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetInventoryItem = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (drank from bottle)");
        }
    }
}

}
