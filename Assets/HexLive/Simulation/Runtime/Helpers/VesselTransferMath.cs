using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// §55.4 (bug #317): перелив воды между ёмкостями инвентаря. Архитектура
// «ёмкостей» едина: и пробитый кокос, и бутылка несут глотки в
// ResourceAmount ИНСТАНСА; WaterKind сохраняет provenance бутылки.
//
// Приёмник — ТОЛЬКО бутылка, и это осознанно: у инстанса кокоса нет поля
// вида воды, так что перелив «в кокос» отмывал бы сырую воду от риска
// болезни (кокосовый глоток пьётся без броска). Кокос — источник.
// Вид воды в бутылке: Coconut, если бутылка была пуста; непустая сохраняет
// свой вид (правило §55.4, bug #347). Так перелив сохраняет provenance:
// кокосовая вода остаётся безопасной, а непустая Raw-бутылка не «отмывается».
public static class VesselTransferMath
{
    /// <summary>Сколько глотков ждут в пробитых кокосах инвентаря.</summary>
    public static int CoconutSips(NPCState npc)
    {
        var sips = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.CoconutPierced &&
                item.ResourceAmount > 0f)
            {
                sips += (int)item.ResourceAmount;
            }
        }

        return sips;
    }

    public static bool HasBottleItem(NPCState npc) =>
        BottleInventoryMath.FirstBottle(npc) is not null;

    /// <summary>Свободные глотки бутылки (0 без самой бутылки-предмета).</summary>
    public static int BottleRoom(NPCState npc) =>
        BottleRoom(BottleInventoryMath.FirstWithRoomFor(npc, WaterKind.Coconut));

    public static int BottleRoom(ItemInstance bottle) =>
        bottle is not null
            ? System.Math.Max(0, (int)System.MathF.Floor(BottleInventoryMath.Capacity(bottle) - BottleInventoryMath.Amount(bottle) + 1e-4f))
            : 0;

    /// <summary>Один предикат на автономный шаг плана и ручную команду:
    /// бутылка есть, в ней есть место, в кокосах есть что переливать.</summary>
    public static bool CanFillBottle(NPCState npc) =>
        CanFillBottle(npc, BottleInventoryMath.FirstWithRoomFor(npc, WaterKind.Coconut));

    public static bool CanFillBottle(NPCState npc, ItemInstance bottle) =>
        npc.Body.CanUseTwoHanded &&
        bottle is not null && InventoryMath.ContainsReference(npc.Inventory.Items, bottle) &&
        BottleRoom(bottle) > 0 &&
        (BottleInventoryMath.Amount(bottle) <= 0f || bottle.WaterKind == WaterKind.Coconut) &&
        CoconutSips(npc) > 0;

    /// <summary>Сам перелив: глоток за глоток, 1:1, до полной бутылки.
    /// Возвращает число перелитых глотков; кокосы теряют ResourceAmount,
    /// пустая бутылка получает вид Coconut.</summary>
    public static int FillBottleFromCoconuts(NPCState npc) =>
        FillBottleFromCoconuts(
            npc, BottleInventoryMath.FirstWithRoomFor(npc, WaterKind.Coconut));

    public static int FillBottleFromCoconuts(NPCState npc, ItemInstance bottle) =>
        FillBottleFromSources(bottle, npc.Inventory.Items);

    internal static int FillBottleFromSources(ItemInstance bottle,
        System.Collections.Generic.IEnumerable<ItemInstance> sources)
    {
        var room = BottleRoom(bottle);
        if (room <= 0 || bottle is null ||
            (BottleInventoryMath.Amount(bottle) > 0f && bottle.WaterKind != WaterKind.Coconut))
        {
            return 0;
        }

        var moved = 0;
        foreach (var item in sources)
        {
            if (room <= 0)
            {
                break;
            }

            if (item.DefinitionId != ContentIds.CoconutPierced ||
                item.ResourceAmount < 1f)
            {
                continue;
            }

            var take = System.Math.Min(room, (int)item.ResourceAmount);
            item.ResourceAmount -= take;
            room -= take;
            moved += take;
        }

        if (moved > 0)
        {
            BottleInventoryMath.Add(bottle, WaterKind.Coconut, moved);
        }

        return moved;
    }

    /// <summary>Manual consolidation preserves exact amounts, including collector fractions.</summary>
    public static float SourceSips(ItemInstance item) =>
        item is null ? 0f : BottleInventoryMath.IsBottle(item)
            ? BottleInventoryMath.Amount(item)
            : item.DefinitionId == ContentIds.CoconutPierced ? System.MathF.Max(0f, item.ResourceAmount) : 0f;

    public static bool CanConsolidateBottle(NPCState npc, ItemInstance bottle)
    {
        if (!npc.Body.CanUseTwoHanded || !BottleInventoryMath.IsBottle(bottle) ||
            !InventoryMath.ContainsReference(npc.Inventory.Items, bottle) ||
            SourceSips(bottle) >= BottleInventoryMath.Capacity(bottle)) return false;
        foreach (var source in npc.Inventory.Items)
            if (!ReferenceEquals(source, bottle) && SourceSips(source) > 0f) return true;
        return false;
    }

    internal static float ConsolidateBottleFromSources(ItemInstance bottle,
        System.Collections.Generic.IEnumerable<ItemInstance> sources)
    {
        if (!BottleInventoryMath.IsBottle(bottle)) return 0f;
        var before = SourceSips(bottle);
        var amount = before;
        var kind = amount > 0f ? bottle.WaterKind : WaterKind.None;
        foreach (var source in sources)
        {
            if (ReferenceEquals(source, bottle)) continue;
            var available = SourceSips(source);
            var take = System.MathF.Min(BottleInventoryMath.Capacity(bottle) - amount, available);
            if (take <= 0f) continue;
            var sourceKind = BottleInventoryMath.IsBottle(source) ? source.WaterKind : WaterKind.Coconut;
            // Adding raw water contaminates the mixture; safe water cannot launder it.
            kind = kind == WaterKind.Raw || sourceKind == WaterKind.Raw ? WaterKind.Raw
                : kind == WaterKind.None ? sourceKind : kind;
            source.ResourceAmount = System.MathF.Max(0f, available - take);
            if (BottleInventoryMath.IsBottle(source) && source.ResourceAmount <= 0f)
            {
                source.WaterKind = WaterKind.None;
                source.LastAddedWaterKind = WaterKind.None;
            }
            amount += take;
            bottle.LastAddedWaterKind = sourceKind;
            if (amount >= BottleInventoryMath.Capacity(bottle)) break;
        }
        if (amount > before)
        {
            bottle.ResourceAmount = amount;
            bottle.WaterKind = kind;
        }
        return amount - before;
    }

    /// <summary>Что лежит во «второй руке» во время перелива — id ёмкости-
    /// источника для OffhandItemId снапшота (§55.4).</summary>
    public static string PourSourceId(NPCState npc) =>
        CoconutSips(npc) > 0 ? ContentIds.CoconutPierced : string.Empty;
}

}
