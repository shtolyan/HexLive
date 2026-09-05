using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §55.4 (bug #317): перелив воды между ёмкостями. Пробитый кокос и каждая
/// физическая бутылка несут свои глотки в ResourceAmount и WaterKind; жаждущая
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

    private static void DrinkOneBottleSip(
        WorldState world, NPCState npc, WaterKind kind, int completionTick)
    {
        npc.BottleWater = kind;
        npc.BottleCharges = 1;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.DrinkBottle });
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        world.Tick = completionTick - SimBalance.DrinkBottleDurationTicks;

        var execution = new ExecutionSystem();
        execution.Run(world);
        while (world.Tick < completionTick)
        {
            world.Tick++;
            execution.Run(world);
        }
    }

    private static int RawSicknessCompletionTick(WorldState world, NPCState npc)
    {
        for (var tick = SimBalance.DrinkBottleDurationTicks; tick < 100_000; tick++)
        {
            if (MathUtil.Hash01(world.Seed, tick, npc.Id.Value, 833) <
                SimBalance.RawWaterSickChance)
            {
                return tick;
            }
        }

        Assert.Fail("Не найден детерминированный Raw sickness roll для теста.");
        return -1;
    }

    private static WorldState RoundTripSave(WorldState source, int version)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.WriteAtVersion(source, writer, version);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(source.Seed);
        using var reader = new BinaryReader(
            stream, System.Text.Encoding.UTF8, leaveOpen: true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
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
                // 2 кокоса × 4 глотка = 8 Coconut-глотков, 1:1 (§55.4).
                Assert.That(npc.BottleCharges, Is.EqualTo(8),
                    "Перелив обязан быть 1:1: два кокоса по 4 глотка = 8.");
                Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.Coconut),
                    "Пустая бутылка обязана сохранить кокосовый provenance (§55.4).");
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
        Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.Coconut));
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
    public void CoconutBottleTopsUpOneToOne()
    {
        var engine = TestWorld.CreateEngine();
        var npc = Girl(engine.World);
        KeepOnlyVessels(npc);
        var coconut = AddCoconut(npc, 4f);
        npc.BottleWater = WaterKind.Coconut;
        npc.BottleCharges = 7;

        var moved = VesselTransferMath.FillBottleFromCoconuts(npc);

        // Места только 3 (ёмкость 10): переливается 3, кокос сохраняет 1.
        Assert.That(moved, Is.EqualTo(3));
        Assert.That(npc.BottleCharges, Is.EqualTo(SimBalance.BottleCapacity));
        Assert.That(coconut.ResourceAmount, Is.EqualTo(1f));
        Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.Coconut));
    }

    [Test]
    public void CoconutWaterDoesNotMixIntoBottleWithAnotherProvenance()
    {
        var world = TestWorld.CreateWorld(35505);
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var bottle = BottleInventoryMath.FirstBottle(npc);
        BottleInventoryMath.SetContents(bottle, WaterKind.Rain, 7);
        var coconut = AddCoconut(npc, 4f);

        Assert.Multiple(() =>
        {
            Assert.That(VesselTransferMath.CanFillBottle(npc, bottle), Is.False);
            Assert.That(VesselTransferMath.FillBottleFromCoconuts(npc, bottle), Is.Zero);
            Assert.That(BottleInventoryMath.Charges(bottle), Is.EqualTo(7));
            Assert.That(bottle.WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(coconut.ResourceAmount, Is.EqualTo(4f));
        });
    }

    [Test]
    public void ManualFillFailsIfSelectedBottleDisappearsDuringDuration()
    {
        var engine = TestWorld.CreateEngine(35506);
        var world = engine.World;
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var selected = BottleInventoryMath.FirstBottle(npc);
        var other = new ItemInstance(ContentIds.Bottle);
        npc.Inventory.Items.Add(other);
        AddCoconut(npc, 4f);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Mind.ManualControl = true;

        var selectedIndex = npc.Inventory.Items.IndexOf(selected);
        var admission = ManualCommandExecutor.Apply(world, new FillVesselCommand(
            npc.Id, new InventoryItemRef(
                InventoryItemSource.Carried, selectedIndex, ContentIds.Bottle)));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        engine.Step();
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));

        npc.Inventory.Items.Remove(selected); // the other bottle shifts into the SAME index
        for (var i = 0; i <= SimBalance.FillVesselDurationTicks + 2 &&
             npc.Plan.Status == PlanStatus.Active; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(BottleInventoryMath.Charges(other), Is.Zero,
                "A stale manual selection must never fall back to another bottle.");
            Assert.That(VesselTransferMath.CoconutSips(npc), Is.EqualTo(4));
        });
    }

    [Test]
    public void ManualFillKeepsExactBottleWhenItsInventoryIndexChanges()
    {
        var engine = TestWorld.CreateEngine(35512);
        var world = engine.World;
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var selected = BottleInventoryMath.FirstBottle(npc);
        var coconut = AddCoconut(npc, 4f);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Mind.ManualControl = true;

        var selectedIndex = npc.Inventory.Items.IndexOf(selected);
        var admission = ManualCommandExecutor.Apply(world, new FillVesselCommand(
            npc.Id, new InventoryItemRef(
                InventoryItemSource.Carried, selectedIndex, ContentIds.Bottle)));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        engine.Step();
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));

        var inserted = new ItemInstance(ContentIds.Bottle);
        npc.Inventory.Items.Insert(0, inserted);
        for (var i = 0; i <= SimBalance.FillVesselDurationTicks + 2 &&
             npc.Plan.Status == PlanStatus.Active; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(BottleInventoryMath.Charges(selected), Is.EqualTo(4));
            Assert.That(selected.WaterKind, Is.EqualTo(WaterKind.Coconut));
            Assert.That(BottleInventoryMath.Charges(inserted), Is.Zero,
                "An inserted equal bottle must not replace the reserved instance.");
            Assert.That(coconut.ResourceAmount, Is.Zero);
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void ManualFillRejectsEqualReplacementBeforeFirstExecutionTick()
    {
        var engine = TestWorld.CreateEngine(35514);
        var world = engine.World;
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var selected = BottleInventoryMath.FirstBottle(npc);
        AddCoconut(npc, 4f);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Mind.ManualControl = true;

        var selectedIndex = npc.Inventory.Items.IndexOf(selected);
        var admission = ManualCommandExecutor.Apply(world, new FillVesselCommand(
            npc.Id, new InventoryItemRef(
                InventoryItemSource.Carried, selectedIndex, ContentIds.Bottle)));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));

        npc.Inventory.Items.RemoveAt(selectedIndex);
        var replacement = new ItemInstance(ContentIds.Bottle);
        npc.Inventory.Items.Insert(selectedIndex, replacement);
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Completed));
            Assert.That(BottleInventoryMath.Charges(replacement), Is.Zero);
            Assert.That(VesselTransferMath.CoconutSips(npc), Is.EqualTo(4));
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void AutonomousFillFailsIfReservedBottleIsReplacedByEqualInstance()
    {
        var world = TestWorld.CreateWorld(35513);
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var selected = BottleInventoryMath.FirstBottle(npc);
        var replacement = new ItemInstance(ContentIds.Bottle);
        npc.Inventory.Items.Add(replacement);
        AddCoconut(npc, 4f);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.FillVessel });
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;

        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));
        Assert.That(InventoryMath.RemoveReference(npc.Inventory.Items, selected), Is.True);

        world.Tick++;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(BottleInventoryMath.Charges(replacement), Is.Zero,
                "Autonomous fill must not retarget to another equal bottle.");
            Assert.That(VesselTransferMath.CoconutSips(npc), Is.EqualTo(4));
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void DrinkKeepsExactBottleWhenAnotherBottleIsInsertedDuringSip()
    {
        var world = TestWorld.CreateWorld(35507);
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var selected = BottleInventoryMath.FirstBottle(npc);
        BottleInventoryMath.SetContents(selected, WaterKind.Raw, 1);
        npc.Needs.Thirst = 0.6f;
        npc.Needs.Hunger = 0f;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.DrinkBottle });
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;

        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));

        var inserted = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(inserted, WaterKind.Boiled, 5);
        npc.Inventory.Items.Insert(0, inserted);
        for (var i = 0; i <= SimBalance.DrinkBottleDurationTicks + 2 &&
             npc.Plan.Status == PlanStatus.Active; i++)
        {
            world.Tick++;
            execution.Run(world);
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(BottleInventoryMath.Charges(selected), Is.Zero,
                "The physical bottle selected at sip start must be consumed.");
            Assert.That(selected.WaterKind, Is.EqualTo(WaterKind.None));
            Assert.That(BottleInventoryMath.Charges(inserted), Is.EqualTo(5),
                "A newly inserted same-definition bottle must not replace the selection.");
            Assert.That(inserted.WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void DrinkFailsIfReservedBottleIsRemovedButIdenticalBottleRemains()
    {
        var world = TestWorld.CreateWorld(35509);
        var npc = Girl(world);
        KeepOnlyVessels(npc);
        var selected = BottleInventoryMath.FirstBottle(npc);
        BottleInventoryMath.SetContents(selected, WaterKind.Raw, 2);
        var other = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(other, WaterKind.Rain, 4);
        npc.Inventory.Items.Add(other);
        npc.Needs.Thirst = 0.6f;
        npc.Needs.Hunger = 0f;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.DrinkBottle });
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;

        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));
        Assert.That(InventoryMath.RemoveReference(npc.Inventory.Items, selected), Is.True);

        world.Tick++;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(BottleInventoryMath.Charges(selected), Is.EqualTo(2),
                "A bottle moved out during the sip must not be consumed remotely.");
            Assert.That(BottleInventoryMath.Charges(other), Is.EqualTo(4),
                "An identical replacement must not inherit the active sip.");
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void CoconutSipKeepsExactInstanceWhenAnotherCoconutIsInserted()
    {
        var world = TestWorld.CreateWorld(35515);
        var npc = Girl(world);
        npc.Inventory.Items.RemoveAll(item =>
            item.DefinitionId == ContentIds.CoconutPierced);
        var selected = AddCoconut(npc, 4f);
        npc.Needs.Thirst = 0.6f;
        npc.Needs.Hunger = 0.9f;
        npc.Plan.TargetItemDefinitionId = ContentIds.CoconutPierced;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.ConsumeInventoryItem,
            Interaction = InteractionType.Drink
        });
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;

        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));

        var inserted = new ItemInstance(ContentIds.CoconutPierced)
        {
            ResourceAmount = 2f
        };
        npc.Inventory.Items.Insert(0, inserted);
        while (npc.Plan.Status == PlanStatus.Active &&
               world.Tick <= npc.Execution.EndTick + 1)
        {
            world.Tick++;
            execution.Run(world);
        }

        Assert.Multiple(() =>
        {
            Assert.That(selected.ResourceAmount, Is.EqualTo(3f),
                "The coconut reserved at sip start must lose the sip.");
            Assert.That(inserted.ResourceAmount, Is.EqualTo(2f),
                "An equal coconut inserted before it must stay untouched.");
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void CoconutSipFailsIfReservedInstanceIsRemoved()
    {
        var world = TestWorld.CreateWorld(35516);
        var npc = Girl(world);
        npc.Inventory.Items.RemoveAll(item =>
            item.DefinitionId == ContentIds.CoconutPierced);
        var selected = AddCoconut(npc, 4f);
        var other = AddCoconut(npc, 2f);
        npc.Needs.Thirst = 0.6f;
        npc.Needs.Hunger = 0.9f;
        npc.Plan.TargetItemDefinitionId = ContentIds.CoconutPierced;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.ConsumeInventoryItem,
            Interaction = InteractionType.Drink
        });
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;

        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.TargetInventoryItem, Is.SameAs(selected));
        Assert.That(InventoryMath.RemoveReference(npc.Inventory.Items, selected), Is.True);

        world.Tick++;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Failed));
            Assert.That(selected.ResourceAmount, Is.EqualTo(4f),
                "A coconut moved away during a sip must not drain remotely.");
            Assert.That(other.ResourceAmount, Is.EqualTo(2f),
                "An equal replacement must not inherit the active sip.");
            Assert.That(npc.Execution.TargetInventoryItem, Is.Null);
        });
    }

    [Test]
    public void CoconutBottleUsesDirectCoconutEffectsAndNeverRollsRawSickness()
    {
        const int seed = 347;
        var coconutWorld = TestWorld.CreateWorld(seed);
        var coconutNpc = Girl(coconutWorld);
        KeepOnlyVessels(coconutNpc);
        coconutNpc.Needs.Thirst = 0.9f;
        coconutNpc.Needs.Comfort = 0.2f;
        coconutNpc.Needs.Hunger = 0f;
        var drink = coconutWorld.Content.ObjectDefinitions[ContentIds.CoconutPierced]
            .Interactions.Single(interaction => interaction.Type == InteractionType.Drink);
        var completionTick = RawSicknessCompletionTick(coconutWorld, coconutNpc);
        var thirstBefore = coconutNpc.Needs.Thirst;
        var comfortBefore = coconutNpc.Needs.Comfort;
        var torsoBefore = coconutNpc.Body.Parts[BodyPart.Torso];

        DrinkOneBottleSip(
            coconutWorld, coconutNpc, WaterKind.Coconut, completionTick);

        var rawWorld = TestWorld.CreateWorld(seed);
        var rawNpc = Girl(rawWorld);
        KeepOnlyVessels(rawNpc);
        rawNpc.Needs.Thirst = 0.9f;
        rawNpc.Needs.Comfort = 0.2f;
        rawNpc.Needs.Hunger = 0f;
        var rawTorsoBefore = rawNpc.Body.Parts[BodyPart.Torso];
        DrinkOneBottleSip(rawWorld, rawNpc, WaterKind.Raw, completionTick);

        Assert.Multiple(() =>
        {
            Assert.That(coconutNpc.Needs.Thirst,
                Is.EqualTo(thirstBefore + drink.Effects.ThirstDelta).Within(0.00001f),
                "бутылка должна дать ровно эффект прямого глотка кокоса");
            Assert.That(coconutNpc.Needs.Comfort,
                Is.EqualTo(comfortBefore + drink.Effects.ComfortDelta).Within(0.00001f));
            Assert.That(coconutNpc.Mind.SicknessDamageRemaining, Is.Zero,
                "Coconut не входит в Raw sickness branch");
            Assert.That(coconutNpc.Body.Parts[BodyPart.Torso], Is.EqualTo(torsoBefore));
            Assert.That(
                rawNpc.Mind.SicknessDamageRemaining > 0f ||
                rawNpc.Body.Parts[BodyPart.Torso] < rawTorsoBefore,
                Is.True,
                "контрольный Raw-глоток на том же детерминированном roll обязан заболеть");
        });
    }

    [Test]
    public void ZeroChargeBottleCannotDrinkOrRollSickness()
    {
        var world = TestWorld.CreateWorld(348);
        var npc = Girl(world);
        npc.BottleWater = WaterKind.Raw;
        npc.BottleCharges = 0;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.DrinkBottle });
        npc.Plan.Status = PlanStatus.Active;

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(npc.BottleWater, Is.EqualTo(WaterKind.None));
            Assert.That(npc.BottleCharges, Is.Zero);
            Assert.That(npc.Mind.SicknessDamageRemaining, Is.Zero);
        });
    }

    [Test]
    public void CoconutBottleKindAndChargesSurviveCurrentSaveRoundTrip()
    {
        var world = TestWorld.CreateWorld(349);
        var npc = Girl(world);
        npc.BottleWater = WaterKind.Coconut;
        npc.BottleCharges = 4;

        var loaded = RoundTripSave(world, WorldSaveSerializer.BlobVersion);
        var reloaded = loaded.Entities.Npcs[npc.Id];

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.BottleWater, Is.EqualTo(WaterKind.Coconut));
            Assert.That(reloaded.BottleCharges, Is.EqualTo(4));
        });
    }

    // Тест «v64 без количества мигрирует в пустую бутылку» снят в §156: блоб
    // v66 оборвал совместимость (OldestReadable = BlobVersion), читать v64
    // больше нельзя и писать его тоже, так что проверять стало нечего. Сама
    // ветка миграции в WorldSaveSerializer осталась гаситься гейтом версии.
}

}
