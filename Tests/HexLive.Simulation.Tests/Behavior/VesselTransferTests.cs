using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §55.4 (bug #317): перелив воды между ёмкостями. Пробитый кокос несёт свои
/// глотки в ResourceAmount инстанса, бутылка — в npc.BottleCharges; жаждущая
/// с неполной бутылкой и вскрытыми кокосами в карманах сначала переливает
/// (небыстрый шаг FillVessel с прогрессом), потом пьёт из бутылки штатно.
/// Ручной приказ «Наполнить» идёт FillVesselCommand через тот же шаг.
/// </summary>
public sealed class VesselTransferTests
{
    private static NPCState Girl(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value)
            .First();

    private static ItemInstance AddCoconut(NPCState npc, float sips)
    {
        var coconut = new ItemInstance(ContentIds.CoconutPierced)
        {
            ResourceAmount = sips
        };
        npc.Inventory.Items.Add(coconut);
        return coconut;
    }

    private static void KeepOnlyVessels(NPCState npc)
    {
        npc.Inventory.Items.RemoveAll(i => i.DefinitionId != ContentIds.Bottle);
        Assert.That(npc.Inventory.Items.Count, Is.EqualTo(1),
            "У колонистки прототипа должна быть ровно одна личная бутылка.");
    }

    [Test]
    public void ThirstyGirlPoursCoconutsIntoEmptyBottleThenDrinks()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var first = AddCoconut(npc, 4f);
        var second = AddCoconut(npc, 4f);

        npc.BottleWater = WaterKind.None;
        npc.BottleCharges = 0;
        npc.Needs.Thirst = 0.9f;
        npc.Needs.Hunger = 0f;
        npc.Needs.Energy = 1f;
        npc.Needs.Comfort = 1f;
        npc.Needs.Social = 1f;

        var filled = false;
        var thirstBefore = npc.Needs.Thirst;
        for (var i = 0; i < 600; i++)
        {
            engine.Step();
            if (!filled && npc.BottleCharges > 0)
            {
                filled = true;
                // 2 кокоса × 4 глотка = 8 Raw-глотков, 1:1 (§55.4).
                Assert.That(npc.BottleCharges, Is.EqualTo(8),
                    "Перелив обязан быть 1:1: два кокоса по 4 глотка = 8.");
                Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.Raw),
                    "Пустая бутылка после перелива несёт Raw-воду (§55.4).");
                Assert.That(first.ResourceAmount, Is.EqualTo(0f),
                    "Кокос-источник обязан потерять свой ResourceAmount.");
                Assert.That(second.ResourceAmount, Is.EqualTo(0f));
            }

            if (filled && npc.Needs.Thirst < 0.4f)
            {
                break;
            }
        }

        Assert.That(filled, Is.True,
            "Жаждущая с пустой бутылкой и кокосами обязана перелить их воду.");
        Assert.That(npc.Needs.Thirst, Is.LessThan(thirstBefore),
            "После перелива она пьёт из бутылки штатным DrinkBottle.");
        Assert.That(npc.BottleCharges, Is.LessThan(8),
            "Глотки после перелива тратятся штатным питьём.");
    }

    [Test]
    public void FillIsSlowAndShowsBothVesselsInSnapshot()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        AddCoconut(npc, 4f);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        engine.Step();

        var bottleIndex = npc.Inventory.Items.FindIndex(
            i => i.DefinitionId == ContentIds.Bottle);
        var admission = ManualCommandExecutor.Apply(world, new FillVesselCommand(
            npc.Id, new InventoryItemRef(
                InventoryItemSource.Carried, bottleIndex, ContentIds.Bottle)));
        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            $"«Наполнить» отклонён: {admission.Reason}");

        // Первый тик заводит исполнение; перелив НЕ мгновенный.
        engine.Step();
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress),
            "Перелив обязан идти с прогрессом в Execution, не мгновенно.");
        Assert.That(npc.Execution.CurrentInteraction,
            Is.EqualTo(InteractionType.FillVessel));
        Assert.That(npc.BottleCharges, Is.EqualTo(0),
            "Глотки приходят только по завершении процесса.");

        // §55.4: признак «переливает» + обе ёмкости для крафт-позы вида.
        var snapshot = WorldSnapshotExporter.Export(world).Npcs
            .Single(candidate => candidate.Id.Value == npc.Id.Value);
        Assert.That(snapshot.CurrentInteraction, Is.EqualTo("FillVessel"));
        Assert.That(snapshot.HeldItemId, Is.EqualTo(ContentIds.Bottle));
        Assert.That(snapshot.OffhandItemId, Is.EqualTo(ContentIds.CoconutPierced),
            "Вторая ёмкость (кокос-источник) обязана ехать в OffhandItemId.");

        for (var i = 0; i <= SimBalance.FillVesselDurationTicks + 2 &&
             npc.BottleCharges == 0; i++)
        {
            engine.Step();
        }

        Assert.That(npc.BottleCharges, Is.EqualTo(4),
            "Ручной перелив обязан перелить все 4 глотка кокоса.");
        Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.Raw));
        Assert.That(npc.Plan.Steps, Is.Empty,
            "Одношаговый ручной план завершается после перелива.");
    }

    [Test]
    public void FillOrderWithNothingToPourIsRejected()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true));
        engine.Step();

        var bottleIndex = npc.Inventory.Items.FindIndex(
            i => i.DefinitionId == ContentIds.Bottle);
        var admission = ManualCommandExecutor.Apply(world, new FillVesselCommand(
            npc.Id, new InventoryItemRef(
                InventoryItemSource.Carried, bottleIndex, ContentIds.Bottle)));

        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(admission.Reason, Is.EqualTo("NothingToPour"));
    }

    [Test]
    public void CoconutHalvesStackIntoOneSlotButPiercedOnesDoNot()
    {
        // §55.4 (bug #317): половинки кокоса — порции еды без инстансного
        // состояния, складываются в ячейку глубиной как мясо. Pierced-кокос
        // НЕ стакуется: его ResourceAmount различает инстансы.
        Assert.That(InventoryState.IsStackable(ContentIds.CoconutOpen), Is.True);
        Assert.That(InventoryState.StackSizeFor(ContentIds.CoconutOpen),
            Is.EqualTo(InventoryState.MeatStackSize));
        Assert.That(InventoryState.IsStackable(ContentIds.CoconutPierced), Is.False);

        var inventory = new InventoryState();
        for (var i = 0; i < 4; i++)
        {
            inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));
        }

        Assert.That(inventory.UsedSlots, Is.EqualTo(1),
            "Четыре одинаковые половинки занимают одну ячейку со счётчиком.");
    }

    [Test]
    public void PartialBottleKeepsItsWaterKindAndTopsUpOneToOne()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Girl(engine.World);
        KeepOnlyVessels(npc);
        var coconut = AddCoconut(npc, 4f);
        npc.BottleWater = WaterKind.Rain;
        npc.BottleCharges = 7;

        var moved = VesselTransferMath.FillBottleFromCoconuts(npc);

        // Места только 3 (ёмкость 10): переливается 3, кокос сохраняет 1.
        Assert.That(moved, Is.EqualTo(3));
        Assert.That(npc.BottleCharges, Is.EqualTo(SimBalance.BottleCapacity));
        Assert.That(coconut.ResourceAmount, Is.EqualTo(1f));
        Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.Rain),
            "Непустая бутылка сохраняет свой вид воды (§55.4).");
    }
}

}
