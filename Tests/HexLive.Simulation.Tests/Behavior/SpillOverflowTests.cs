using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §52.5 r2: перелив инвентаря меряется ЯЧЕЙКАМИ, как и сама ёмкость.
/// Гард считает освобождённые ячейки (неполный стак — одна ячейка = вся
/// охапка, это физика стака, не каскад), несброшенный предмет возвращается,
/// а не уничтожается.
/// </summary>
public sealed class SpillOverflowTests
{
    private static (WorldState World, NPCState Npc) Scene()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values
            .OrderBy(n => n.Id.Value).First();
        npc.Inventory.Items.Clear();
        npc.Inventory.HolsterSlotIds.Clear();
        return (world, npc);
    }

    [Test]
    public void OverflowFreesExactlyTheNeededCells()
    {
        var (world, npc) = Scene();
        npc.Inventory.Capacity = 2;
        npc.Inventory.Items.Add(new ItemInstance("item.bandage"));
        for (var i = 0; i < 5; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        }

        for (var i = 0; i < 48; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance("resource.palm_leaf"));
        }

        Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(3), "Прекондиция.");
        var objectsBefore = world.Entities.Objects.Count;

        InventoryMath.SpillOverflow(world, npc);

        var removed = 54 - npc.Inventory.Items.Count;
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(2),
                "Перелив — ровно до ёмкости, ни ячейкой больше.");
            Assert.That(npc.Inventory.Items.Any(
                    i => i.DefinitionId == "item.bandage"), Is.True,
                "Медицина важнее ресурсов — бинт остаётся.");
            Assert.That(npc.Inventory.Items.Count(
                        i => i.DefinitionId == ContentIds.Log) is 0 or 5,
                Is.True, "Освобождается ЦЕЛАЯ ячейка одного материала.");
            Assert.That(world.Entities.Objects.Count - objectsBefore,
                Is.EqualTo(removed),
                "Каждый сброшенный предмет — объект на земле, потерь нет.");
        });
    }

    // Прежний по-инстансный guard 64 истощался посреди неполного стака и
    // оставлял перелив: 66 предметов в двух ячейках требуют 66 сбросов.
    [Test]
    public void CellGuardSurvivesDeepPartialStacks()
    {
        var (world, npc) = Scene();
        npc.Inventory.Capacity = 0;
        for (var i = 0; i < 48; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance("resource.palm_leaf"));
        }

        for (var i = 0; i < 18; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        }

        InventoryMath.SpillOverflow(world, npc);

        Assert.That(npc.Inventory.UsedSlots, Is.Zero,
            "Гард считает ЯЧЕЙКИ: 66 предметов в двух ячейках высыпаются " +
            "полностью, по-инстансный guard 64 обрывался на полпути.");
    }

    // §50/§52: безрукой ёмкость ноль СОЗНАТЕЛЬНО — честный мир калек честен
    // и с рюкзаком. Высыпается всё, кроме любимого оружия §75A.
    [Test]
    public void ZeroCapacityDumpsEveryDroppable()
    {
        var (world, npc) = Scene();
        npc.Inventory.Capacity = 0;
        npc.Inventory.Items.Add(new ItemInstance("item.bandage"));
        for (var i = 0; i < 5; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        }

        InventoryMath.SpillOverflow(world, npc);

        Assert.That(npc.Inventory.UsedSlots, Is.Zero);
    }
}

}
