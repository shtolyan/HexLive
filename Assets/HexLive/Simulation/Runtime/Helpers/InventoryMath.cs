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

    public static bool CanMakeRoomFor(WorldState world, NPCState npc, string incomingDefinitionId)
    {
        if (!CanAcquireAdditional(world, npc, incomingDefinitionId))
        {
            return false;
        }

        if (FitsWithoutEviction(world, npc, incomingDefinitionId))
        {
            return true;
        }

        return ReplacementVictim(world, npc, incomingDefinitionId) is not null;
    }

    /// <summary>A physical instance can enter without displacing anything.
    /// Used by completion paths which must distinguish a truly full pack from
    /// free capacity inside an existing stack.</summary>
    internal static bool FitsWithoutEviction(
        WorldState world, NPCState npc, string incomingDefinitionId) =>
        CanAcquireAdditional(world, npc, incomingDefinitionId) &&
        (npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId));

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

        if (CanMakeRoomFor(world, npc, incomingDefinitionId))
        {
            return true;
        }

        return SurvivalKnifeMaterialVictim(
            world, npc, goal, incomingDefinitionId) is not null;
    }

    public static bool MakeRoomFor(WorldState world, NPCState npc, string incomingDefinitionId)
    {
        if (!CanAcquireAdditional(world, npc, incomingDefinitionId))
        {
            return false;
        }

        if (npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId))
        {
            return true;
        }

        var incomingImportance = Importance(world, incomingDefinitionId);
        var guard = 0;
        while (!npc.Inventory.HasSpace &&
               !FitsExistingStack(npc, incomingDefinitionId) &&
               guard++ < 64)
        {
            var victim = ReplacementVictim(world, npc, incomingDefinitionId);
            if (victim is null)
            {
                return false;
            }

            var victimImportance = Importance(world, victim);
            RemoveReference(npc.Inventory.Items, victim);
            ExecutionSystem.DropItemAtFeet(world, npc, victim);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InventoryMadeRoom",
                    $"Dropped {victim.DefinitionId}({victimImportance}) for " +
                    $"{incomingDefinitionId}({incomingImportance})");
            }
        }

        return npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId);
    }

    public static bool MakeRoomForGoal(
        WorldState world, NPCState npc, GoalType goal, string incomingDefinitionId)
    {
        if (!CanAcquireAdditional(world, npc, incomingDefinitionId))
        {
            return false;
        }

        if (npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId))
        {
            return true;
        }

        // Preserve the ordinary replacement order whenever it can solve the
        // pack.  The survival exception is only the final fallback.
        if (ReplacementVictim(world, npc, incomingDefinitionId) is not null)
        {
            return MakeRoomFor(world, npc, incomingDefinitionId);
        }

        var victim = SurvivalKnifeMaterialVictim(
            world, npc, goal, incomingDefinitionId);
        if (victim is null)
        {
            return false;
        }

        var victimImportance = Importance(world, victim);
        RemoveReference(npc.Inventory.Items, victim);
        ExecutionSystem.DropItemAtFeet(world, npc, victim);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InventoryMadeRoom",
                $"Dropped {victim.DefinitionId}({victimImportance}) for " +
                $"emergency knife material {incomingDefinitionId}");
        }

        return npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId);
    }

    /// <summary>
    /// Repairs older saves (and any legacy direct-add path) which already
    /// contain more instances than the definition permits. Extras are dropped,
    /// never deleted, so another NPC who actually lacks the item may recover one.
    /// </summary>
    public static void SpillCarriedLimitExcess(WorldState world, NPCState npc)
    {
        System.Collections.Generic.Dictionary<string, int>? counts = null;
        for (var i = 0; i < npc.Inventory.Items.Count;)
        {
            var item = npc.Inventory.Items[i];
            if (!world.Content.ObjectDefinitions.TryGetValue(
                    item.DefinitionId, out var definition) ||
                definition.MaxCarriedInstances <= 0)
            {
                i++;
                continue;
            }

            counts ??= new System.Collections.Generic.Dictionary<string, int>();
            counts.TryGetValue(item.DefinitionId, out var carried);
            if (carried < definition.MaxCarriedInstances)
            {
                counts[item.DefinitionId] = carried + 1;
                i++;
                continue;
            }

            npc.Inventory.Items.RemoveAt(i);
            ExecutionSystem.DropItemAtFeet(world, npc, item);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CarryLimitSpill",
                    $"Dropped excess {item.DefinitionId}; " +
                    $"limit={definition.MaxCarriedInstances}");
            }
        }
    }

    internal static bool NeedsEmergencyCoconutBlade(WorldState world, NPCState npc) =>
        npc.Body.HasUsableHand &&
        !DecisionSystem.HasCoconutBlade(npc) &&
        (npc.Needs.Thirst >= 0.8f || npc.Needs.Hunger >= 0.8f) &&
        DecisionSystem.HasCoconutOpportunity(npc, world);

    // While the emergency knife is unfinished, the material pair is survival
    // cargo, not generic stockpile junk.  NeedsDecay uses this when deciding
    // which resources it may unload from a full pack.
    internal static bool IsReservedEmergencyKnifeMaterial(
        WorldState world, NPCState npc, string definitionId)
    {
        if (!NeedsEmergencyCoconutBlade(world, npc))
        {
            return false;
        }

        var required = definitionId switch
        {
            ContentIds.Stick => RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.Stick),
            ContentIds.Stone => RecipeCatalog.InputCount(
                GoalType.CraftKnife, ContentIds.Stone),
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
            if (npc.Inventory.Items.Contains(stashed.DefinitionId))
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
    public static ItemInstance LowestImportanceDroppable(WorldState world, NPCState npc)
    {
        ItemInstance worst = null;
        var worstImp = int.MaxValue;
        var favoriteWeapon = FavoriteWeaponInstance(npc);
        foreach (var item in npc.Inventory.Items)
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
            // leaves the pack only when the holster itself comes off (the slots
            // vanish first, then it spills by the normal rules).
            if (npc.Inventory.IsHolstered(item))
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

    // Normal replacement follows category importance. A useful missing tool
    // gets one narrow exception: it may displace redundant managed gear when
    // the remaining inventory still provides every capability and at least as
    // good a weapon. This lets a hammer/pickaxe break a pack full of duplicate
    // knives without making the sole knife expendable.
    private static ItemInstance ReplacementVictim(
        WorldState world, NPCState npc, string incomingDefinitionId)
    {
        var incomingImportance = Importance(world, incomingDefinitionId);
        var normalVictim = LowestImportanceDroppable(world, npc);
        if (normalVictim is not null &&
            Importance(world, normalVictim) < incomingImportance)
        {
            return normalVictim;
        }

        if (!IsUsefulMissingTool(world, npc, incomingDefinitionId))
        {
            return null;
        }

        var favoriteWeapon = FavoriteWeaponInstance(npc);
        ItemInstance redundantVictim = null;
        var redundantImportance = int.MaxValue;
        foreach (var item in npc.Inventory.Items)
        {
            if (!CanFreePocket(npc, item, favoriteWeapon) ||
                !IsRedundantManagedGear(npc, item))
            {
                continue;
            }

            var importance = Importance(world, item);
            if (importance < redundantImportance)
            {
                redundantVictim = item;
                redundantImportance = importance;
            }
        }

        return redundantVictim;
    }

    private static ItemInstance SurvivalKnifeMaterialVictim(
        WorldState world, NPCState npc, GoalType goal, string incomingDefinitionId)
    {
        var exactMaterial =
            goal == GoalType.GatherWood && incomingDefinitionId == ContentIds.Stick ||
            goal == GoalType.GatherStone && incomingDefinitionId == ContentIds.Stone;
        if (!exactMaterial || !NeedsEmergencyCoconutBlade(world, npc))
        {
            return null;
        }

        ItemInstance victim = null;
        var victimImportance = int.MaxValue;
        var victimWeaponPriority = int.MaxValue;
        foreach (var item in npc.Inventory.Items)
        {
            if (npc.Inventory.IsHolstered(item) ||
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

    private static bool IsUsefulMissingTool(
        WorldState world, NPCState npc, string definitionId) =>
        !npc.Inventory.Items.Contains(definitionId) &&
        world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) &&
        definition.HasTag("Tool") &&
        Content.GearCatalog.AddsValueOver(
            npc.Inventory.Items, definitionId, npc.Body.WeaponHands);

    private static bool IsRedundantManagedGear(NPCState npc, ItemInstance candidate)
    {
        var stats = Content.GearCatalog.For(candidate.DefinitionId);
        return stats.Id == candidate.DefinitionId &&
            !Content.GearCatalog.AddsValueOver(
                ItemsExcept(npc.Inventory.Items, candidate),
                candidate.DefinitionId,
                npc.Body.WeaponHands);
    }

    private static System.Collections.Generic.IEnumerable<ItemInstance> ItemsExcept(
        System.Collections.Generic.IEnumerable<ItemInstance> items,
        ItemInstance excluded)
    {
        foreach (var item in items)
        {
            if (!ReferenceEquals(item, excluded))
            {
                yield return item;
            }
        }
    }

    private static ItemInstance FavoriteWeaponInstance(NPCState npc)
    {
        var favoriteId = ItemAffinity.FavoriteWeapon(npc.Id.Value, npc.Inventory.Items);
        foreach (var item in npc.Inventory.Items)
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

    // Drop the lowest-importance pocket items until the pack fits again. Used
    // whenever capacity shrinks under a full load (undress, arm severed). Items
    // land at the NPC's feet with their instance state (wetness/durability).
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

            RemoveReference(inv.Items, victim);
            if (ExecutionSystem.DropItemAtFeet(world, npc, victim) is null)
            {
                inv.Items.Add(victim);
                break;
            }

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
