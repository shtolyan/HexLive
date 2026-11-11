using System.Collections.Generic;
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

// Spec §52: pack bookkeeping keyed on item importance. Water/food outrank
// resources, so a full pack sheds a stone before a coconut. Centralizes the
// "what do I drop / keep" decisions shared by overflow, haul-to-fire and stash.
internal static class InventoryMath
{
    /// <summary>
    /// ItemInstance equality intentionally groups equal definitions. Mutations
    /// that selected a physical slot must use reference identity instead.
    /// </summary>
    internal static bool ContainsReference(
        System.Collections.Generic.IReadOnlyList<ItemInstance> items,
        ItemInstance sought) => IndexOfReference(items, sought) >= 0;

    internal static int IndexOfReference(
        System.Collections.Generic.IReadOnlyList<ItemInstance> items,
        ItemInstance sought)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], sought)) return i;
        }

        return -1;
    }

    internal static bool RemoveReference(
        System.Collections.Generic.List<ItemInstance> items, ItemInstance sought)
    {
        var index = IndexOfReference(items, sought);
        if (index < 0) return false;
        items.RemoveAt(index);
        return true;
    }

    // Already-produced cargo must survive a saturated ground layout. This is
    // recovery ownership, not permission for another ordinary pickup.
    internal static void RetainOwnedItem(NPCState npc, ItemInstance item)
    {
        if (!ContainsReference(npc.Inventory.Items, item) && !ContainsReference(npc.WornItems, item))
            npc.Inventory.Items.Add(item);
    }

    internal static WorldObjectState TryDropAutomatic(WorldState world, NPCState npc, ItemInstance item)
    {
        if (world.Tick < npc.Inventory.NextGroundDropRetryTick ||
            !CampPropertyMath.CanDeposit(world, npc, npc.Tile)) return null;
        var dropped = ExecutionSystem.DropAutomaticItemAtFeet(world, npc, item);
        if (dropped == null)
            npc.Inventory.NextGroundDropRetryTick = world.Tick + System.Math.Max(1, world.SlowIntervalTicks);
        return dropped;
    }

    public static int Importance(WorldState world, string definitionId) =>
        world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def)
            ? ItemCatalog.Importance(def)
            : ItemCatalog.ImportanceById(definitionId);

    // Spec §52: importance follows the INSTANCE, not the label — a drained
    // pierced coconut is an empty shell (Resource-rank, first to drop), not
    // "water". Victim-side checks must use this overload or a dry shell
    // blocks the pack forever.
    public static int Importance(WorldState world, ItemInstance item) =>
        ItemCatalog.IsDrainedWaterShell(item.DefinitionId, item.ResourceAmount)
            ? ItemCatalog.Importance(ItemCategory.Resource)
            : Importance(world, item.DefinitionId);

    /// <summary>
    /// Content-authored per-NPC carry limit. This is deliberately independent
    /// of capacity: a second unique item does not become valid merely because
    /// another pocket is free. Definitions with the default zero are unlimited.
    /// </summary>
    public static bool CanAcquireAdditional(
        WorldState world, NPCState npc, string incomingDefinitionId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(
                incomingDefinitionId, out var definition) ||
            definition.MaxCarriedInstances <= 0)
        {
            return true;
        }

        var carried = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == incomingDefinitionId &&
                ++carried >= definition.MaxCarriedInstances)
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanMakeRoomFor(WorldState world, NPCState npc, string incomingDefinitionId) =>
        FitsWithoutEviction(world, npc, incomingDefinitionId);

    // §52.3 / #378: a lower-priority item is not free capacity until it can
    // actually leave the pack. Match TryDropAutomatic's backoff and the real
    // drop admission without spawning items or changing the retry timer.
    private static bool CanDropReplacement(WorldState world, NPCState npc, ItemInstance victim) =>
        victim is not null && world.Tick >= npc.Inventory.NextGroundDropRetryTick &&
        CampPropertyMath.CanDeposit(world, npc, npc.Tile) &&
        GroundItemPlacement.TryFind(world, npc, victim, out var tile, out _, out _) &&
        CampPropertyMath.CanDeposit(world, npc, tile);

    /// <summary>A physical instance can enter without displacing anything.
    /// Used by completion paths which must distinguish a truly full pack from
    /// free capacity inside an existing stack.</summary>
    internal static bool FitsWithoutEviction(
        WorldState world, NPCState npc, string incomingDefinitionId)
    {
        if (!CanAcquireAdditional(world, npc, incomingDefinitionId)) return false;
        return FitsAvailableCells(npc, incomingDefinitionId);
    }

    private static bool FitsAvailableCells(NPCState npc, string definitionId)
    {
        var used = npc.Inventory.UsedSlots;
        return used < npc.Inventory.Capacity ||
            used == npc.Inventory.Capacity && FitsExistingStack(npc, definitionId);
    }

    // §63: the emergency coconut-blade chain is allowed to sacrifice an
    // ordinary tool for its ONE stick and ONE stone.  The generic importance
    // order cannot express this: Tool(60) quite reasonably beats Resource(20)
    // in normal life, but that made a full pack of hammer/pickaxe/bottle/pill
    // fatal.  NeedsDecay would throw away the newly gathered stone every slow
    // tick and GatherStone picked it straight back up forever (seed 104729).
    //
    // Keep this goal-aware and emergency-only.  Ordinary hauling still never
    // swaps a useful tool for a pebble.
    public static bool CanMakeRoomForGoal(
        WorldState world, NPCState npc, GoalType goal, string incomingDefinitionId)
    {
        if (!CanAcquireAdditional(world, npc, incomingDefinitionId))
        {
            return false;
        }

        if (FitsAvailableCells(npc, incomingDefinitionId))
        {
            return true;
        }

        return CanDropReplacement(world, npc, SurvivalReplacementVictim(world, npc, goal, incomingDefinitionId));
    }

    public static bool MakeRoomFor(WorldState world, NPCState npc, string incomingDefinitionId) =>
        FitsWithoutEviction(world, npc, incomingDefinitionId);

    // §153.1: an offer never reserves another character's slot. Recheck the
    // whole bundle on arrival while both characters still own their items.
    internal static bool MakeRoomForGiftTransfer(
        WorldState world, NPCState npc, string incomingDefinitionId,
        System.Func<bool> transferFits)
    {
        return CanAcquireAdditional(world, npc, incomingDefinitionId) && transferFits();
    }

    public static bool MakeRoomForGoal(
        WorldState world, NPCState npc, GoalType goal, string incomingDefinitionId)
    {
        if (!CanAcquireAdditional(world, npc, incomingDefinitionId))
        {
            return false;
        }

        if (FitsAvailableCells(npc, incomingDefinitionId))
        {
            return true;
        }

        var victim = SurvivalReplacementVictim(world, npc, goal, incomingDefinitionId);
        if (!CanDropReplacement(world, npc, victim)) return false;
        using var events = world.Events.DeferPublication();
        if (TryDropAutomatic(world, npc, victim) == null) return false;
        RemoveReference(npc.Inventory.Items, victim);
        events.Commit();
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InventoryMadeRoom",
                $"Dropped {victim.DefinitionId} for emergency {goal} target {incomingDefinitionId}");
        }

        return true;
    }

    internal static bool NeedsEmergencyCoconutBlade(WorldState world, NPCState npc) =>
        npc.Body.HasUsableHand &&
        !DecisionSystem.HasCoconutBlade(npc) &&
        ((npc.Needs.Thirst >= 0.8f && !DecisionSystem.HasBottleWater(npc) &&
          !DecisionSystem.HasCoconutWater(npc, world)) ||
         (npc.Needs.Hunger >= 0.8f && !DecisionSystem.HasCoconutMeal(npc, world) &&
          npc.Inventory.FindFirstFood(world.Content) is null)) &&
        DecisionSystem.HasCoconutOpportunity(npc, world);

    // While the emergency knife is unfinished, every link in its stone →
    // sharp-stone → lashed-knife chain is survival cargo, not generic stockpile
    // junk. The goal-specific replacement cannot unload these inputs.
    internal static bool IsReservedEmergencyKnifeMaterial(
        WorldState world, NPCState npc, string definitionId)
    {
        if (!NeedsEmergencyCoconutBlade(world, npc))
        {
            return false;
        }

        var carriedSharpStones = 0;
        var carriedRope = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.SharpStone) carriedSharpStones++;
            if (item.DefinitionId == ContentIds.Rope) carriedRope++;
        }

        var required = definitionId switch
        {
            ContentIds.Stick => RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.Stick),
            ContentIds.SharpStone => RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.SharpStone),
            ContentIds.Rope => RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.Rope),
            ContentIds.Stone when carriedSharpStones < RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.SharpStone) => RecipeCatalog.InputCount(
                    GoalType.CraftSharpStone, ContentIds.Stone),
            ContentIds.Fiber when carriedRope < RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.Rope) => SimBalance.RopeFiberCost,
            _ => 0
        };
        if (required <= 0)
        {
            return false;
        }

        var carried = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == definitionId)
            {
                carried++;
            }
        }

        return carried <= required;
    }

    private static bool FitsExistingStack(NPCState npc, string definitionId)
    {
        if (!InventoryState.IsStackable(definitionId))
        {
            return false;
        }

        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == definitionId)
            {
                count++;
            }
        }

        return count > 0 && count % InventoryState.StackSizeFor(definitionId) != 0;
    }

    // Spec §52: does this ground object carry, in its pockets, a Tool the NPC
    // lacks and could make room for? Dropped garments keep their stash in
    // Contents — the undress overflow may have carried the knife down with
    // the jacket, and GatherTools must be able to see it there.
    public static bool StashHoldsWantedTool(WorldState world, NPCState npc, WorldObjectState container)
    {
        foreach (var stashed in container.Contents)
        {
            if (npc.Inventory.Items.Contains(stashed.DefinitionId) ||
                !CampPropertyMath.CanUseStoredItem(world, npc, stashed.OwnerId))
            {
                continue;
            }

            if (world.Content.ObjectDefinitions.TryGetValue(stashed.DefinitionId, out var def) &&
                def.HasTag("Tool") &&
                // §133: тот же счёт рук, что и на всех остальных путях к
                // инструменту (WeaponHands, не IntactHands) — иначе у калеки
                // заначка и земля отвечали по-разному на один вопрос.
                Content.GearCatalog.AddsValueOver(
                    npc.Inventory.Items, stashed.DefinitionId, npc.Body.WeaponHands) &&
                CanMakeRoomFor(world, npc, stashed.DefinitionId))
            {
                return true;
            }
        }

        return false;
    }

    // The least-wanted pocket item — the first to go when room is tight.
    // Personal effects (the bottle) are never candidates.
    public static ItemInstance LowestImportanceDroppable(WorldState world, NPCState npc) =>
        LowestImportanceDroppable(world, npc.Id.Value, npc.Inventory);

    internal static ItemInstance LowestImportanceDroppable(WorldState world, int npcId, InventoryState inventory)
    {
        ItemInstance worst = null;
        var worstImp = int.MaxValue;
        var favoriteWeapon = FavoriteWeaponInstance(npcId, inventory);
        foreach (var item in inventory.Items)
        {
            // §75A protects one physical favorite weapon. Identical copies
            // are ordinary inventory: protecting the definition id made every
            // knife in a knife-filled pack impossible to shed (bug #89).
            if (ReferenceEquals(item, favoriteWeapon))
            {
                continue;
            }

            if (InventoryState.IsPersonalEffect(item.DefinitionId))
            {
                continue;
            }

            // Spec §52.8: a holstered tool is strapped to the leg and costs no
            // pocket — shedding it frees nothing, so it is never the victim. It
            // costs a cell again when its holster is removed.
            if (inventory.IsHolstered(item))
            {
                continue;
            }

            var imp = Importance(world, item);
            if (imp < worstImp)
            {
                worstImp = imp;
                worst = item;
            }
        }

        return worst;
    }

    // Explicit worn-item drop keeps overflow inside the garment on the ground.
    internal static void MoveOverflowToGarment(WorldState world, int npcId,
        InventoryState inventory, System.Collections.Generic.List<ItemInstance> contents)
    {
        var guard = 0;
        while (inventory.UsedSlots > inventory.Capacity && guard++ < 64)
        {
            var victim = LowestImportanceDroppable(world, npcId, inventory);
            if (victim is null) break;
            RemoveReference(inventory.Items, victim);
            contents.Add(victim);
        }
    }

    // §52.2: plan the complete move before touching either physical store.
    // The garment can take its ordinary pockets and substitute whole cells
    // for important contents that stay with the character.
    internal static bool TryPlanGarmentStow(WorldState world, NPCState npc,
        ItemInstance garment, out List<ItemInstance> contents)
    {
        contents = new List<ItemInstance>();
        var wornIndex = IndexOfReference(npc.WornItems, garment);
        if (wornIndex < 0 || !world.Content.ObjectDefinitions.TryGetValue(garment.DefinitionId, out var definition) ||
            !InventoryLayoutBuilder.TryCollectOwnedContents(world, npc, wornIndex, out var pockets))
            return false;

        var worn = new List<ItemInstance>(npc.WornItems);
        worn.RemoveAt(wornIndex);
        var retained = PlayerInventoryMath.BuildProjectedInventory(world, npc, npc.Inventory.Items, worn);
        var packed = new InventoryState { Capacity = definition.InventoryCapacity };
        foreach (var slot in HolsterCatalog.SlotsFor(garment.DefinitionId))
            packed.HolsterSlotIds.Add(slot);
        var favorite = FavoriteWeaponInstance(npc.Id.Value, npc.Inventory);

        bool MustKeep(ItemInstance item) =>
            ReferenceEquals(item, favorite) || retained.IsHolstered(item) ||
            InventoryState.IsPersonalEffect(item.DefinitionId) ||
            Importance(world, item) >= ItemCatalog.Importance(ItemCategory.Tool);

        foreach (var pocket in pockets)
        {
            if (MustKeep(pocket)) continue;
            RemoveReference(retained.Items, pocket);
            packed.Items.Add(pocket);
        }
        if (packed.UsedSlots > packed.Capacity) return false;

        if (retained.UsedSlots > retained.Capacity)
        {
            var candidates = new List<(int importance, List<ItemInstance> items)>();
            foreach (var cell in InventoryLayoutBuilder.OccupiedCells(world, npc))
            {
                if (cell.Exists(item => MustKeep(item) || !ContainsReference(retained.Items, item))) continue;
                candidates.Add((Importance(world, cell[0]), cell));
            }
            // Selection scans the existing layout order for equal-importance
            // cells. A removed cell is no longer eligible on the next pass.
            while (retained.UsedSlots > retained.Capacity)
            {
                List<ItemInstance> selected = null;
                var bestImportance = int.MaxValue;
                foreach (var candidate in candidates)
                {
                    if (candidate.importance >= bestImportance ||
                        !ContainsReference(retained.Items, candidate.items[0])) continue;
                    packed.Items.AddRange(candidate.items);
                    var fits = packed.UsedSlots <= packed.Capacity;
                    packed.Items.RemoveRange(packed.Items.Count - candidate.items.Count, candidate.items.Count);
                    if (!fits) continue;
                    bestImportance = candidate.importance;
                    selected = candidate.items;
                }
                if (selected is null) return false;
                foreach (var item in selected) RemoveReference(retained.Items, item);
                packed.Items.AddRange(selected);
            }
        }

        contents = packed.Items;
        return true;
    }

    internal static bool FitsAfterWear(WorldState world, NPCState npc,
        WorldObjectState replacement) =>
        FitsAfterWear(world, npc, new ItemInstance(replacement.DefinitionId), replacement.Contents);

    internal static bool FitsAfterWear(WorldState world, NPCState npc,
        ItemInstance replacement, IReadOnlyList<ItemInstance> incomingContents = null)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(replacement.DefinitionId, out var definition) ||
            definition.Layer is null)
            return false;
        var carried = new List<ItemInstance>(npc.Inventory.Items);
        RemoveReference(carried, replacement);
        if (replacement.Contents is { Count: > 0 } stored) carried.AddRange(stored);
        if (incomingContents != null)
            foreach (var item in incomingContents)
                if (!ReferenceEquals(item, replacement)) carried.Add(item);
        var worn = new List<ItemInstance>();
        foreach (var item in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var current) &&
                WearSlotCatalog.Occupies(definition, current))
                carried.Add(item);
            else
                worn.Add(item);
        }
        worn.Add(replacement);
        return PlayerInventoryMath.FitsProjected(world, npc, carried, worn);
    }

    private static ItemInstance SurvivalReplacementVictim(
        WorldState world, NPCState npc, GoalType goal, string incomingDefinitionId)
    {
        var exactMaterial =
            goal == GoalType.GatherWood && incomingDefinitionId == ContentIds.Stick ||
            goal == GoalType.GatherStone && incomingDefinitionId == ContentIds.Stone ||
            goal == GoalType.GatherFiber && incomingDefinitionId == ContentIds.Fiber;
        var immediateMeal = (goal is GoalType.GetFood or GoalType.Eat) &&
            npc.Needs.Hunger >= 0.8f && npc.Body.HasUsableHand &&
            npc.Inventory.FindFirstFood(world.Content) is null &&
            !DecisionSystem.HasCoconutMeal(npc, world) &&
            world.Content.ObjectDefinitions.TryGetValue(incomingDefinitionId, out var definition) &&
            definition.Interactions.Exists(interaction => interaction.Type == InteractionType.Eat);
        if ((!exactMaterial || !NeedsEmergencyCoconutBlade(world, npc)) && !immediateMeal)
        {
            return null;
        }

        // One exact victim must open a whole cell. Never discard a stack one
        // item at a time while the character still cannot take the meal.
        if (npc.Inventory.UsedSlots != npc.Inventory.Capacity) return null;
        var favoriteWeapon = FavoriteWeaponInstance(npc.Id.Value, npc.Inventory);
        ItemInstance victim = null;
        var victimImportance = int.MaxValue;
        var victimWeaponPriority = int.MaxValue;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == incomingDefinitionId ||
                !CanFreePocket(npc, item, favoriteWeapon) ||
                !OpensCell(npc, item) ||
                IsReservedEmergencyKnifeMaterial(world, npc, item.DefinitionId))
            {
                continue;
            }

            // Food, water, medicine and weapons remain protected.  Resources,
            // clothing and ordinary tools may yield to the life-saving recipe.
            var importance = Importance(world, item);
            if (importance > ItemCatalog.Importance(ItemCategory.Tool))
            {
                continue;
            }

            var weaponPriority = Content.GearCatalog.For(item.DefinitionId).MeleePriority;
            if (importance < victimImportance ||
                importance == victimImportance && weaponPriority < victimWeaponPriority)
            {
                victim = item;
                victimImportance = importance;
                victimWeaponPriority = weaponPriority;
            }
        }

        return victim;
    }

    private static bool OpensCell(NPCState npc, ItemInstance item)
    {
        if (!InventoryState.IsStackable(item.DefinitionId)) return true;
        var count = 0;
        foreach (var carried in npc.Inventory.Items)
            if (carried.DefinitionId == item.DefinitionId && !npc.Inventory.IsHolstered(carried))
                count++;
        return count % InventoryState.StackSizeFor(item.DefinitionId) == 1;
    }

    private static ItemInstance FavoriteWeaponInstance(int npcId, InventoryState inventory)
    {
        var favoriteId = ItemAffinity.FavoriteWeapon(npcId, inventory.Items);
        foreach (var item in inventory.Items)
        {
            if (item.DefinitionId == favoriteId)
            {
                return item;
            }
        }

        return null;
    }

    private static bool CanFreePocket(
        NPCState npc, ItemInstance item, ItemInstance favoriteWeapon) =>
        !ReferenceEquals(item, favoriteWeapon) &&
        !InventoryState.IsPersonalEffect(item.DefinitionId) &&
        !npc.Inventory.IsHolstered(item);

    // Drop the lowest-importance pocket items when a physical loss (such as a
    // severed arm) directly requires a world spill. Items land
    // at the NPC's feet with their instance state (wetness/durability).
    //
    // §52.5 r2 (вслед за bug #278 r2): перелив меряется ЯЧЕЙКАМИ, как и сама
    // ёмкость. UsedSlots — ceil по стакам, поэтому: (а) гард считает
    // ОСВОБОЖДЁННЫЕ ЯЧЕЙКИ, не инстансы — прежний по-инстансный guard 64
    // истощался посреди неполного стака (охапка 48 листьев = одна ячейка =
    // 48 сбросов) и мог оборваться, оставив перелив; (б) предмет, которому
    // не нашлось точки сброса, возвращается в пакет, а не уничтожается.
    // Ячейка освобождается целиком — это не каскад, а физика стака: нельзя
    // держать пол-охапки. При Capacity = 0 (голая, обе руки потеряны — §50
    // сознательно оставляет безрукой ноль) высыпается всё, кроме любимого
    // оружия §75A: честный мир калек честен и с рюкзаком.
    public static void SpillOverflow(WorldState world, NPCState npc)
    {
        var inv = npc.Inventory;
        var freedCells = 0;
        var used = inv.UsedSlots;
        while (inv.UsedSlots > inv.Capacity && freedCells < 64)
        {
            var victim = LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            if (TryDropAutomatic(world, npc, victim) is null) break;
            RemoveReference(inv.Items, victim);

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ItemSpilled", $"{victim.DefinitionId} (no pocket room)");
            }

            if (inv.UsedSlots < used)
            {
                freedCells++;
                used = inv.UsedSlots;
            }
        }
    }
}

}
