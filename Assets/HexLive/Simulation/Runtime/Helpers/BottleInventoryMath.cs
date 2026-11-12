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
        item is not null && WaterVesselCatalog.IsPortable(item.DefinitionId);

    public static int Capacity(ItemInstance item) => item is null ? 0 : WaterVesselCatalog.Capacity(item.DefinitionId);
    public static int Charges(ItemInstance item) =>
        IsBottle(item) ? Charges(item.ResourceAmount, item.WaterKind, item.DefinitionId) : 0;

    public static int Charges(WorldObjectState vessel) =>
        Charges(vessel.ResourceAmount, vessel.WaterKind, vessel.DefinitionId);

    /// <summary>Shared physical-vessel rule for simulation and snapshot-based menus.</summary>
    public static int Charges(float amount, WaterKind kind, string definitionId = ContentIds.Bottle)
    {
        if (kind == WaterKind.None || (float.IsNaN(amount) || float.IsInfinity(amount)) || amount <= 0f) return 0;
        return System.Math.Max(0, (int)System.MathF.Floor(
            System.MathF.Min(WaterVesselCatalog.Capacity(definitionId), amount) + 1e-4f));
    }

    public static float Amount(ItemInstance item) =>
        IsBottle(item) && item.WaterKind != WaterKind.None &&
        !float.IsNaN(item.ResourceAmount) && !float.IsInfinity(item.ResourceAmount)
            ? System.Math.Clamp(item.ResourceAmount, 0f, Capacity(item)) : 0f;

    public static void SetContents(ItemInstance bottle, WaterKind kind, int charges) =>
        SetContents(bottle, kind, (float)charges);

    public static void SetContents(ItemInstance bottle, WaterKind kind, float amount)
    {
        if (!IsBottle(bottle)) return;
        var clamped = float.IsNaN(amount) || float.IsInfinity(amount) ? 0f
            : System.Math.Clamp(amount, 0f, Capacity(bottle));
        bottle.ResourceAmount = clamped;
        bottle.WaterKind = clamped > 0f && kind != WaterKind.None ? kind : WaterKind.None;
        if (bottle.WaterKind == WaterKind.None) bottle.ResourceAmount = 0f;
        bottle.LastAddedWaterKind = bottle.WaterKind;
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
            if (IsBottle(item) && Amount(item) <= 0f) return item;
        }

        return null;
    }

    public static ItemInstance FirstWithRoomFor(NPCState npc, WaterKind kind)
    {
        ItemInstance empty = null;
        foreach (var item in npc.Inventory.Items)
        {
            if (!IsBottle(item)) continue;
            var amount = Amount(item);
            if (amount <= 0f)
            {
                empty ??= item;
            }
            else if (amount + 1f <= Capacity(item) + 1e-4f && item.WaterKind == kind)
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
        var before = Amount(bottle);
        if (before > 0f && bottle.WaterKind != kind) return 0;
        var room = System.Math.Max(0, (int)System.MathF.Floor(Capacity(bottle) - before + 1e-4f));
        var moved = System.Math.Min(charges, room);
        if (moved <= 0) return 0;
        SetContents(bottle, before == 0 ? kind : bottle.WaterKind, before + moved);
        return moved;
    }

    public static bool ConsumeOne(ItemInstance bottle, out WaterKind kind)
    {
        kind = bottle?.WaterKind ?? WaterKind.None;
        var charges = Charges(bottle);
        if (charges <= 0) return false;
        var appearance = bottle.LastAddedWaterKind;
        SetContents(bottle, kind, Amount(bottle) - 1f);
        if (bottle.ResourceAmount > 0f) bottle.LastAddedWaterKind = appearance;
        return true;
    }

    /// <summary>Migrate the v66/v67 NPC-global pair into the first bottle.</summary>
    public static void MigrateLegacy(NPCState npc, WaterKind kind, int charges)
    {
        var bottle = FirstBottle(npc);
        if (bottle is not null && Amount(bottle) <= 0f &&
            kind != WaterKind.None && charges > 0)
        {
            SetContents(bottle, kind, charges);
        }
    }
}

}
