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
        FillPockets(npc, keep);
        for (var i = 0; i < 5; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        }

        var objectsBefore = world.Entities.Objects.Count;

        MobSystem.ReadySpearHands(world, npc);

        var logsLeft = npc.Inventory.Items.Count(
            i => i.DefinitionId == ContentIds.Log);
        var gained = world.Entities.Objects.Count - objectsBefore;
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.UsedSlots, Is.LessThanOrEqualTo(keep),
                "Руки обязаны освободиться под копьё.");
            Assert.That(
                npc.Inventory.Items.Count(i => i.DefinitionId == "tool.hammer"),
                Is.EqualTo(keep), "Карманы/рюкзак не трогаются.");
            Assert.That(gained, Is.EqualTo(5 - logsLeft),
                "Каждое уроненное бревно — объект на земле; ничего не уничтожено.");
            Assert.That(gained, Is.GreaterThan(0),
                "Ноша рук обязана лечь на землю.");
        });
    }
}

}
