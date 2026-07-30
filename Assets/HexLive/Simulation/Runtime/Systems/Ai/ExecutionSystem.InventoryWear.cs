using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    // Spec 31A.5B: identify and REMOVE any worn item sharing (layer, part) with
    // the garment about to be worn — molly BodyBones.Equip semantics. §52.7: the
    // displaced pieces are NOT dropped here — they are collected in
    // _displacedGarments so the caller can don the replacement + recompute the
    // pack capacity FIRST, then call DropDisplacedGarments. That order lets a
    // displaced garment's pocket items relocate INTO the new garment's slots
    // wherever they fit, spilling only the true remainder to the ground.
    private static readonly System.Collections.Generic.List<string> _conflictScratch = new();
    private static readonly System.Collections.Generic.List<ItemInstance> _displacedGarments = new();

    private static void ResolveWearConflicts(WorldState world, NPCState npc, string newItemId)
    {
        _displacedGarments.Clear();
        if (!world.Content.ObjectDefinitions.TryGetValue(newItemId, out var newDefinition) ||
            newDefinition.Layer is not { } newLayer)
        {
            return;
        }

        _conflictScratch.Clear();
        foreach (var wornId in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(wornId, out var wornDefinition) ||
                wornDefinition.Layer != newLayer)
            {
                continue;
            }

            foreach (var part in newDefinition.Covers)
            {
                if (wornDefinition.Covers.Contains(part))
                {
                    _conflictScratch.Add(wornId);
                    break;
                }
            }
        }

        foreach (var conflictId in _conflictScratch)
        {
            var conflictItem = npc.WornItems.Find(i => i.DefinitionId == (string)conflictId) ??
                new ItemInstance(conflictId);
            npc.WornItems.Remove(conflictItem);
            _displacedGarments.Add(conflictItem);
            Trace.Emit(world, npc.Id, "ItemReplaced",
                $"{conflictId} taken off (layer conflict with {newItemId})");
        }
    }

    // §52.7: lay every garment displaced by the most recent ResolveWearConflicts
    // on the ground, each carrying down whatever pack overflow no longer fits now
    // that the replacement's capacity is live (DropGarmentWithContents — lowest
    // importance first, §52.3). Call AFTER WornItems.Add(new) + Recalculate, so
    // items that STILL fit stay in the pack ("moved into the new garment") and
    // only the true remainder rides down inside the dropped piece.
    private static void DropDisplacedGarments(WorldState world, NPCState npc)
    {
        for (var i = 0; i < _displacedGarments.Count; i++)
        {
            DropGarmentWithContents(world, npc, _displacedGarments[i]);
        }

        _displacedGarments.Clear();
    }

    // §35.5B: the rack holds up to SimBalance.RackCapacity garments — the
    // wearables at its junction, one per hanger slot on the assembled prefab.
    internal static bool RackIsFull(WorldState world, WorldObjectState rack)
    {
        if (rack.Junctions.Count == 0)
        {
            return true;
        }

        var hung = 0;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Id.Value != rack.Id.Value && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(rack.Junctions[0]) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Layer is not null)
            {
                hung++;
                if (hung >= SimBalance.RackCapacity)
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
        SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch);
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
        foreach (var junction in world.Junctions.Items.Values)
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
            SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch);
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
                if (def.Tags.Contains(tag) && HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
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
        var spot = FindSpacedFurnitureSpot(world, campfire, "station.drying_rack") ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            GiveOrDrop(world, npc, "resource.stick");
            GiveOrDrop(world, npc, "resource.stick");
            Trace.Emit(world, npc.Id, "ExecFailed", "CraftRack: nowhere to place the rack");
            return;
        }

        var rack = WorldObjectMutations.SpawnObject(
            world, "station.drying_rack", npc.Fragment, npc.Tile, junction);
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

            inv.Items.Remove(victim);
            _garmentSpillScratch.Add(victim);
        }

        // §31A.5A: a doffed garment lands in the SAME cell she stands in, right
        // under her — not scattered a cell over (user request).
        var dropped = DropItemAtFeet(world, npc, garment, underFoot: true);
        if (dropped != null && _garmentSpillScratch.Count > 0)
        {
            dropped.Contents.AddRange(_garmentSpillScratch);
            Trace.Emit(world, npc.Id, "StashedInGarment",
                $"{garment.DefinitionId} holds [{string.Join(",", _garmentSpillScratch)}]");
        }

        return dropped;
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
                !def.Tags.Contains("Tool") ||
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
        Trace.Emit(world, npc.Id, "StashRecovered",
            $"{stash.DefinitionId} pockets returned [{string.Join(",", _stashRecoverScratch)}] " +
            $"left [{string.Join(",", stash.Contents)}] " +
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
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

    private static void RunUndressItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (itemId is null || !npc.WornItems.Contains(itemId))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"UndressItem: '{itemId ?? "-"}' is not worn");
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.TargetObject = null;
            npc.Execution.HeldGarment = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Undress -> {itemId} Duration={UndressDurationTicks}ticks");
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
                npc.WornItems.Remove(doffed);
                EquipmentMath.Recalculate(world, npc);
                npc.Execution.HeldGarment = doffed;
                Trace.Emit(world, npc.Id, "GarmentInHand",
                    $"Undress {itemId} doffed to hand " +
                    $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
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
            npc.WornItems.Remove(wornItem);
            npc.Execution.HeldGarment = null;
            EquipmentMath.Recalculate(world, npc);
            // Spec §52: the garment carries down whatever pocket items no longer
            // fit — they wait inside it on the ground, retrievable later.
            DropGarmentWithContents(world, npc, wornItem);

            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            Trace.Emit(world, npc.Id, "ItemUndressed",
                $"{itemId} dropped at Tile={npc.Tile.Q},{npc.Tile.R} " +
                $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed (undressed)");
        }
    }

    private static void RunDropInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "DropInventoryItem: no target item");
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
            Trace.Emit(world, npc.Id, "ExecFailed", $"DropInventoryItem: '{itemId}' not in inventory");
            return;
        }

        npc.Inventory.Items.Remove(item);
        var dropped = DropItemAtFeet(world, npc, item);
        if (dropped is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"DropInventoryItem: no drop junction for '{itemId}'");
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

        Trace.Emit(world, npc.Id, "ItemDropped",
            $"{itemId} placed on ground Obj={dropped.Id.Value} for {npc.Plan.Goal}");
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
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: item '{itemId ?? "-"}' not in inventory or unknown definition");
            return;
        }

        // §55: in-place consume now covers both Eat (food) and Drink (crack a
        // coconut). The verb comes from the plan step, so one handler serves
        // both; a Drink can carry Yields (the opened-coconut husk).
        var verb = npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Interaction.HasValue
            ? npc.Plan.Steps[0].Interaction.Value
            : InteractionType.Eat;
        var interaction = ResolveInteraction(itemDefinition, verb);
        if (interaction is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' has no {verb} interaction");
            return;
        }

        var item = FindConsumableInventoryItem(npc, itemId, verb, itemDefinition);
        if (item is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' not available for {verb}");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = verb;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + interaction.DurationTicks;

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{verb} (inventory) -> {itemId} " +
                $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                $"EndTick={npc.Execution.EndTick} " +
                $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
                $"T={interaction.Effects.ThirstDelta:+0.00;-0.00} " +
                $"C={interaction.Effects.ComfortDelta:+0.00;-0.00}]");
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

                if (SimTrace.Verbose)
                {
                    Trace.Emit(world, npc.Id, "ExecProgress",
                        $"{verb} (inventory) Progress={progress:P0} " +
                        $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
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
                    Trace.Emit(world, npc.Id, "InteractionStarted",
                        $"{verb} (inventory, next sip) -> {itemId} " +
                        $"Duration={interaction.DurationTicks}ticks " +
                        $"WaterLeft={item.ResourceAmount:F0} Thirst={npc.Needs.Thirst:F2}");
                    return;
                }
            }
            else
            {
                npc.Inventory.Items.Remove(item);
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

            Trace.Emit(world, npc.Id, "ItemConsumed",
                $"{itemId} {verb} from inventory " +
                $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}] " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]" +
                (IsPortableCoconutDrink(itemDefinition, verb)
                    ? $" CoconutWaterLeft={item.ResourceAmount:F0}"
                    : string.Empty));

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (ate from inventory)");
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
        verb == InteractionType.Drink && definition.Tags.Contains("CoconutWater");

    // Spec 29H: drink in place from the carried bottle — thirst quenched,
    // raw water carries the 30 % sickness roll, then the bottle empties.
    private static int DrinkBottleDurationTicks => SimBalance.DrinkBottleDurationTicks;

    private static void RunDrinkBottle(WorldState world, NPCState npc)
    {
        if (npc.BottleWater == WaterKind.None)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "DrinkBottle: the bottle is empty");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Drink;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + DrinkBottleDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Drink (bottle:{npc.BottleWater}) Duration={DrinkBottleDurationTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // A bottleful is a real drink: relief raw 0.7 / boiled 0.85 (so the
        // two-step chain matches the old single drink, 29H). Spec 29C.9: the
        // thirst drops gulp by gulp across the duration, not in one jump.
        // §54.15: RAIN water (the collector's leaf funnel, no ground contact)
        // is clean — boiled-grade thirst relief and NO sickness roll; only
        // the warm-drink comfort bonus stays boiled-only.
        var raw = npc.BottleWater == WaterKind.Raw;
        var boiled = npc.BottleWater == WaterKind.Boiled;
        var thirstTotal = raw ? SimBalance.DrinkThirstRaw : SimBalance.DrinkThirstBoiled;
        var comfortTotal = boiled ? SimBalance.DrinkComfortBoiled : 0f;
        var share = 1f / DrinkBottleDurationTicks;
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst - thirstTotal * share);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfortTotal * share);

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
                if (npc.Body.VitalDestroyed(out var sickVital0))
                {
                    npc.Health = 0f;
                    Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"{sickVital0} destroyed by sickness");
                }
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
        npc.BottleCharges--;
        var driedOut = npc.BottleCharges <= 0;
        Trace.Emit(world, npc.Id, "DrankBottle",
            $"{(boiled ? "Boiled" : "Raw")} water Thirst={npc.Needs.Thirst:F2} Left={System.Math.Max(0, npc.BottleCharges)}");
        if (driedOut)
        {
            npc.BottleWater = WaterKind.None;
            npc.BottleCharges = 0;
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (drank from bottle)");
    }
}

}
