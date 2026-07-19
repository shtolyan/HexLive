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

internal static class EquipmentMath
{
    public static float AverageDirtiness(NPCState npc)
    {
        if (npc.WornItems.Count == 0)
        {
            return 0f;
        }

        var total = 0f;
        foreach (var item in npc.WornItems)
        {
            // Contamination = dirt + blood (independent stain layers). The sum
            // may logically exceed 1 — the stat clamps at 1 ("full" bar).
            total += MathUtil.Clamp01(item.Dirtiness + item.Bloodiness);
        }

        return total / npc.WornItems.Count;
    }

    // Laundry (Jul 2026): the single dirtiest worn piece — the wash chain
    // cares about the worst offender (a filthy leather vest on a clean
    // outfit), which the average hides.
    public static float WorstDirtiness(NPCState npc)
    {
        var worst = 0f;
        foreach (var item in npc.WornItems)
        {
            worst = System.MathF.Max(worst, MathUtil.Clamp01(item.Dirtiness + item.Bloodiness));
        }

        return worst;
    }

    public static void Recalculate(WorldState world, NPCState npc)
    {
        var warmth = 0f;
        var armor = 0f;
        foreach (var item in npc.WornItems)
        {
            var (itemWarmth, itemArmor) = ItemValues(world, item.DefinitionId);
            // Spec 35.5: wet cloth loses insulation GRADUALLY — up to −90% at
            // fully soaked (was a hard cliff: 100% until 0.5, then zero; the
            // first minutes of rain changed nothing and the cutoff felt broken).
            warmth += itemWarmth * (1f - 0.9f * MathUtil.Clamp01(item.Wetness));

            armor = System.Math.Max(armor, itemArmor);
        }

        npc.EquippedWarmth = MathUtil.Clamp01(warmth);
        npc.EquippedArmor = armor;

        RecalculateCapacity(world, npc);
    }

    // Spec §52: the pack is only as big as what you wear. Base = the two hands;
    // each worn garment adds its own pockets. Recomputed whenever WornItems
    // changes (every Recalculate caller) and once at bootstrap. Naked ⇒ 2.
    public static void RecalculateCapacity(WorldState world, NPCState npc)
    {
        // Spec §52: hand slots = intact hands (arms severed via §50 remove them),
        // capped by the tunable HandSlots (normally 2 — the pair everyone has).
        var slots = System.Math.Min(npc.Body.IntactHands, SimBalance.HandSlots);
        foreach (var item in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def))
            {
                slots += def.InventoryCapacity;
            }
        }

        npc.Inventory.Capacity = slots;
    }

    // Spec 31A.5B: protection has anatomy — only garments covering the
    // bitten part absorb its damage.
    public static float ArmorForPart(WorldState world, NPCState npc, BodyPart part)
    {
        var best = 0f;
        foreach (var itemId in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(itemId, out var definition) ||
                !definition.Covers.Contains(part))
            {
                continue;
            }

            var (_, itemArmor) = ItemValues(world, itemId);
            best = System.Math.Max(best, itemArmor);
        }

        return best;
    }

    // Spec 35.4: is this body part covered by any worn garment?
    public static bool IsPartCovered(WorldState world, NPCState npc, BodyPart part)
    {
        foreach (var itemId in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(itemId, out var definition) &&
                definition.Covers.Contains(part))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.List<ItemInstance> _destroyedScratch = new();

    // Spec 35.6: durability loss on every garment covering the struck part;
    // at zero the item is rags — removed outright.
    public static void WearCoveringItems(WorldState world, NPCState npc, BodyPart part, float wear)
    {
        _destroyedScratch.Clear();
        foreach (var item in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
                definition.Covers.Contains(part))
            {
                item.Durability -= wear;
                if (item.Durability <= 0f)
                {
                    _destroyedScratch.Add(item);
                }
            }
        }

        DestroyWornItems(world, npc, _destroyedScratch);
    }

    public static void DestroyWornItems(
        WorldState world, NPCState npc,
        System.Collections.Generic.List<ItemInstance> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            npc.WornItems.Remove(item);
            npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.1f);
            Trace.Emit(world, npc.Id, "ItemDestroyed", $"{item.DefinitionId} fell apart");
        }

        Recalculate(world, npc);
        // Spec §52: rags destroy the garment, NOT the pockets' contents. Losing
        // the slots a torn piece provided spills whatever no longer fits onto the
        // ground at the NPC's feet — the bottle in the ripped panties just drops.
        InventoryMath.SpillOverflow(world, npc);
    }

    // Spec 35.5 / §49.7: soggy clothes drag. Only REAL garments (Wear/Outerwear)
    // count — a wet bra/panties/bikini (Underwear) is too light to slow you, so
    // a girl in just underwear (or naked) keeps full speed even soaked.
    public static float WetMovementFactor(WorldState world, NPCState npc)
    {
        var factor = 1f;
        foreach (var item in npc.WornItems)
        {
            if (item.Wetness <= 0.5f)
            {
                continue;
            }

            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) &&
                def.Layer == WearLayer.Underwear)
            {
                continue; // underwear doesn't drag
            }

            factor *= Spec49.WetDragPerGarment;
        }

        return System.MathF.Max(Spec49.WetDragFloor, factor);
    }

    public static (float Warmth, float Armor) ItemValues(WorldState world, string definitionId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition))
        {
            return (0f, 0f);
        }

        var warmth = 0f;
        var armor = 0f;
        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type != InteractionType.Dress)
            {
                continue;
            }

            warmth = System.Math.Max(warmth, interaction.Effects.WarmthDelta);
            armor = System.Math.Max(armor, interaction.Effects.ArmorDelta);
        }

        return (warmth, armor);
    }
}

}
