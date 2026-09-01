using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// §55.4 (bug #317): перелив воды между ёмкостями инвентаря. Архитектура
// «ёмкостей» едина: пробитый кокос несёт свои глотки в ResourceAmount
// ИНСТАНСА (StoredKind.Water в каталоге задаёт ёмкость), бутылка — в
// npc.BottleCharges/BottleWater (SimBalance.BottleCapacity).
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
        npc.Inventory.Items.Contains(new ItemInstance(ContentIds.Bottle));

    /// <summary>Свободные глотки бутылки (0 без самой бутылки-предмета).</summary>
    public static int BottleRoom(NPCState npc) =>
        HasBottleItem(npc)
            ? System.Math.Max(
                0, SimBalance.BottleCapacity - System.Math.Max(0, npc.BottleCharges))
            : 0;

    /// <summary>Один предикат на автономный шаг плана и ручную команду:
    /// бутылка есть, в ней есть место, в кокосах есть что переливать.</summary>
    public static bool CanFillBottle(NPCState npc) =>
        BottleRoom(npc) > 0 && CoconutSips(npc) > 0;

    /// <summary>Сам перелив: глоток за глоток, 1:1, до полной бутылки.
    /// Возвращает число перелитых глотков; кокосы теряют ResourceAmount,
    /// пустая бутылка получает вид Coconut.</summary>
    public static int FillBottleFromCoconuts(NPCState npc)
    {
        var room = BottleRoom(npc);
        if (room <= 0)
        {
            return 0;
        }

        var moved = 0;
        foreach (var item in npc.Inventory.Items)
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
            if (npc.BottleCharges <= 0 || npc.BottleWater == WaterKind.None)
            {
                npc.BottleWater = WaterKind.Coconut;
                npc.BottleCharges = 0;
            }

            npc.BottleCharges += moved;
        }

        return moved;
    }

    /// <summary>Что лежит во «второй руке» во время перелива — id ёмкости-
    /// источника для OffhandItemId снапшота (§55.4).</summary>
    public static string PourSourceId(NPCState npc) =>
        CoconutSips(npc) > 0 ? ContentIds.CoconutPierced : string.Empty;
}

}
