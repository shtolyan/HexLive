using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Bug #278 / §52.5: подготовка копья к бою роняет ношу КИСТЕЙ, а не весь
/// инвентарь. Кисти в раскладке заполняются последними, поэтому граница —
/// UsedSlots относительно Capacity − handSlots.
/// </summary>
public sealed class SpearHandsTests
{
    private static (WorldState World, NPCState Npc, int Keep) Scene()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.HolsterSlotIds.Clear();
        var handSlots = System.Math.Min(npc.Body.IntactHands, SimBalance.HandSlots);
        var keep = npc.Inventory.Capacity - handSlots;
        Assert.That(keep, Is.GreaterThan(1),
            "Прекондиция: у колонистки нет карманов, тест не о том.");
        return (world, npc, keep);
    }

    private static void FillPockets(NPCState npc, int slots)
    {
        for (var i = 0; i < slots; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance("tool.hammer"));
        }
    }

    // До фикса ReadySpearHands вываливал ВСЕ брёвна/ветки/камни/листья,
    // сколько бы их ни было и где бы они ни лежали.
    [Test]
    public void FreeHandsDropNothing_Bug278()
    {
        var (world, npc, keep) = Scene();
        FillPockets(npc, keep - 1);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        var objectsBefore = world.Entities.Objects.Count;

        MobSystem.ReadySpearHands(world, npc);

        Assert.Multiple(() =>
        {
            Assert.That(
                npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Log),
                Is.EqualTo(2),
                "Bug #278: брёвна из карманов не смеют падать при свободных руках.");
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objectsBefore));
        });
    }

    [Test]
    public void OnlyTheHandLoadDrops_Bug278()
    {
        var (world, npc, keep) = Scene();
        // Брёвна подобраны РАНЬШЕ (лягут в карманы), листья — ПОЗЖЕ (кисть).
        FillPockets(npc, keep - 1);
        for (var i = 0; i < 5; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        }

        for (var i = 0; i < 48; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance("resource.palm_leaf"));
        }

        var objectsBefore = world.Entities.Objects.Count;

        MobSystem.ReadySpearHands(world, npc);

        var logsLeft = npc.Inventory.Items.Count(
            i => i.DefinitionId == ContentIds.Log);
        var leavesLeft = npc.Inventory.Items.Count(
            i => i.DefinitionId == "resource.palm_leaf");
        var gained = world.Entities.Objects.Count - objectsBefore;
        Assert.Multiple(() =>
        {
            Assert.That(logsLeft, Is.EqualTo(5),
                "Bug #278 r2: брёвна лежат в карманах — их не роняют; кисти " +
                "заполняются последними, снимать надо с конца списка.");
            Assert.That(leavesLeft, Is.EqualTo(0),
                "Охапка листьев в кисти — одна ячейка — падает целиком.");
            Assert.That(gained, Is.EqualTo(48),
                "Каждый уроненный лист — объект на земле; ничего не уничтожено.");
        });
    }

    // Переполненный сверх ёмкости пакет: бой освобождает не больше ЯЧЕЕК,
    // чем кистей, — перелив сверх Capacity лечит SpillOverflow, не драка.
    [Test]
    public void DropIsCappedAtHandCells_Bug278()
    {
        var (world, npc, keep) = Scene();
        FillPockets(npc, keep);
        for (var i = 0; i < 5; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        }

        for (var i = 0; i < 5; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        }

        for (var i = 0; i < 48; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance("resource.palm_leaf"));
        }

        MobSystem.ReadySpearHands(world, npc);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Count(
                    i => i.DefinitionId == ContentIds.Log),
                Is.EqualTo(5),
                "Освобождаются максимум handSlots ячеек — третий материал " +
                "(из кармана) не трогается.");
            Assert.That(npc.Inventory.Items.Count(
                    i => i.DefinitionId == "tool.hammer"),
                Is.EqualTo(keep), "Карманы не трогаются.");
        });
    }
}

}
