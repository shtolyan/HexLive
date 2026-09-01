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
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Failed));
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
