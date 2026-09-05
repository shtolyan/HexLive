using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §52 / bug #355: one source of truth for water held by physical bottle
/// instances. NPCState.BottleWater/BottleCharges remain only a legacy facade.
/// </summary>
public static class BottleInventoryMath
{
    public static bool IsBottle(ItemInstance item) =>
        item is not null && item.DefinitionId == ContentIds.Bottle;

    public static int Charges(ItemInstance item)
    {
        if (!IsBottle(item) || item.WaterKind == WaterKind.None ||
            item.ResourceAmount <= 0f)
        {
            return 0;
        }

        return System.Math.Min(
            SimBalance.BottleCapacity,
            System.Math.Max(0, (int)System.MathF.Floor(item.ResourceAmount + 1e-4f)));
    }

    public static void SetContents(ItemInstance bottle, WaterKind kind, int charges)
    {
        if (!IsBottle(bottle)) return;
        var clamped = System.Math.Min(SimBalance.BottleCapacity, System.Math.Max(0, charges));
        bottle.ResourceAmount = clamped;
        bottle.WaterKind = clamped > 0 && kind != WaterKind.None ? kind : WaterKind.None;
        if (bottle.WaterKind == WaterKind.None) bottle.ResourceAmount = 0f;
    }

    public static ItemInstance FirstBottle(NPCState npc)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (IsBottle(item)) return item;
        }

        return null;
    }

    public static ItemInstance FirstDrinkable(NPCState npc)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (Charges(item) > 0) return item;
        }

        return null;
    }

    public static ItemInstance FirstEmpty(NPCState npc)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (IsBottle(item) && Charges(item) == 0) return item;
        }

        return null;
    }

    public static ItemInstance FirstWithRoomFor(NPCState npc, WaterKind kind)
    {
        ItemInstance empty = null;
        foreach (var item in npc.Inventory.Items)
        {
            if (!IsBottle(item)) continue;
            var charges = Charges(item);
            if (charges == 0)
            {
                empty ??= item;
            }
            else if (charges < SimBalance.BottleCapacity && item.WaterKind == kind)
            {
                return item;
            }
        }

        return empty;
    }

    public static bool HasWater(NPCState npc) => FirstDrinkable(npc) is not null;

    public static int Add(ItemInstance bottle, WaterKind kind, int charges)
    {
        if (!IsBottle(bottle) || charges <= 0) return 0;
        var before = Charges(bottle);
        if (before > 0 && bottle.WaterKind != kind) return 0;
        var moved = System.Math.Min(charges, SimBalance.BottleCapacity - before);
        if (moved <= 0) return 0;
        SetContents(bottle, before == 0 ? kind : bottle.WaterKind, before + moved);
        return moved;
    }

    public static bool ConsumeOne(ItemInstance bottle, out WaterKind kind)
    {
        kind = bottle?.WaterKind ?? WaterKind.None;
        var charges = Charges(bottle);
        if (charges <= 0) return false;
        SetContents(bottle, kind, charges - 1);
        return true;
    }

    /// <summary>Migrate the v66/v67 NPC-global pair into the first bottle.</summary>
    public static void MigrateLegacy(NPCState npc, WaterKind kind, int charges)
    {
        var bottle = FirstBottle(npc);
        if (bottle is not null && Charges(bottle) == 0 &&
            kind != WaterKind.None && charges > 0)
        {
            SetContents(bottle, kind, charges);
        }
    }
}

}
