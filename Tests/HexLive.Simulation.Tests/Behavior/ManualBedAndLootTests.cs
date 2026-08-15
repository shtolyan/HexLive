using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §124.1 «Положить в кровать» и §128 «взял — обыскал»: две ручные функции,
/// которые доводят перенос человека до конца — несомого можно уложить в
/// выбранную кровать и обыскать прямо в руках.
/// </summary>
public sealed class ManualBedAndLootTests
{
    private static void Step(SimulationEngine engine, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            engine.Step();
        }
    }

    private static bool HasTrace(WorldState world, EntityId npc, string type, string contains)
    {
        foreach (var e in world.Events.Items)
        {
            if (e.Type == type && e.EntityId == npc.Value &&
                (contains.Length == 0 || (e.Message?.Contains(contains) ?? false)))
            {
                return true;
            }
        }

        return false;
    }

    // Носильщица с ручным режимом и лежащая без сознания соседка у неё на
    // руках. Нужды в ноль ДО включения режима — иначе авто-нужды §121.6
    // перехватят цель.
    private static (SimulationEngine Engine, NPCState Carrier, NPCState Patient)
        CarryScene()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var pair = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .Take(2)
            .ToList();
        Assert.That(pair, Has.Count.EqualTo(2), "В прототипе меньше двух колонисток.");
        var carrier = pair[0];
        var patient = pair[1];

        carrier.Needs.Hunger = 0f;
        carrier.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(carrier.Id, true));
        engine.Step();

        patient.Mind.FaintedUntilTick = world.Tick + 100000;
        Assert.That(patient.IsUnconscious(world.Tick), Is.True);

        engine.Commands.Enqueue(new CarryPersonCommand(carrier.Id, patient.Id));
        for (var i = 0; i < 600 && !carrier.IsCarryingPerson; i++)
        {
            engine.Step();
        }

        Assert.That(carrier.IsCarryingPerson, Is.True,
            "Прекондиция: подъём на руки не удался.");
        return (engine, carrier, patient);
    }

    private static WorldObjectState SpawnReachableBed(WorldState world, NPCState carrier)
    {
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (tile.Coord == carrier.Tile ||
                !StructurePlacement.HexFreeForBuild(world, tile.Coord) ||
                StructurePlacement.CenterJunction(world, tile.Coord) is not { } center ||
                carrier.CurrentJunction is not { } from ||
                !Connectivity.Reachable(world, from, center, carrier.Body.CanJump))
            {
                continue;
            }

            return WorldObjectMutations.SpawnObject(
                world, ContentIds.BedBasic, new FragmentId(1), tile.Coord, center);
        }

        Assert.Fail("Не нашлось свободного гекса под кровать.");
        return null;
    }

    [Test]
    public void PutPersonInBedCarriesToTheBedAndTucksIn()
    {
        var (engine, carrier, patient) = CarryScene();
        var world = engine.World;
        var bed = SpawnReachableBed(world, carrier);

        engine.Commands.Enqueue(new PutPersonInBedCommand(carrier.Id, bed.Id));
        engine.Step();
        Assert.That(carrier.Mind.CurrentGoal, Is.EqualTo(GoalType.Rescue),
            "Приказ §124.1 не принялся.");
        Assert.That(bed.CurrentUser, Is.EqualTo(patient.Id),
            "Кровать обязана быть забронирована за несомой сразу при приёме.");

        for (var i = 0; i < 2000 && carrier.IsCarryingPerson; i++)
        {
            engine.Step();
        }

        Assert.That(carrier.IsCarryingPerson, Is.False,
            "Носильщица так и не уложила несомую.");
        Assert.That(patient.Execution.TargetObject, Is.EqualTo(bed.Id),
            "Пациентка не в кровати — BedSleep.TryEnter не отработал.");
        Assert.That(bed.IsOccupied, Is.True);
        Assert.That(bed.CurrentUser, Is.EqualTo(patient.Id));
        Assert.That(carrier.Mind.CurrentGoal, Is.EqualTo(GoalType.None),
            "После укладки носильщица обязана стоять и ждать приказа.");
    }

    [Test]
    public void OccupiedBedRejectsThePutOrder()
    {
        var (engine, carrier, patient) = CarryScene();
        var world = engine.World;
        var bed = SpawnReachableBed(world, carrier);
        var third = world.Entities.Npcs.Values.First(n =>
            !n.Id.Equals(carrier.Id) && !n.Id.Equals(patient.Id));
        bed.IsOccupied = true;
        bed.CurrentUser = third.Id;

        engine.Commands.Enqueue(new PutPersonInBedCommand(carrier.Id, bed.Id));
        engine.Step();

        Assert.That(HasTrace(world, carrier.Id, "ManualOrderRejected", "Reason=Occupied"),
            Is.True, "Занятая кровать обязана отказать видимой причиной Occupied.");
        Assert.That(carrier.IsCarryingPerson, Is.True,
            "Отклонённый приказ не смеет ронять ношу.");
    }

    [Test]
    public void CarriedPersonCanBeLootedInTheCarriersArms()
    {
        var (engine, carrier, patient) = CarryScene();
        var world = engine.World;
        var loot = new ItemInstance("tool.hammer") { Durability = 0.4f };
        patient.Inventory.Items.Add(loot);

        engine.Commands.Enqueue(new TransferInventoryCommand(
            carrier.Id, patient.Id,
            new InventoryItemRef(InventoryItemSource.Carried,
                patient.Inventory.Items.Count - 1, loot.DefinitionId),
            1, InventoryTransferDirection.Take));
        Step(engine, 8);

        Assert.That(carrier.Inventory.Items.Any(i => ReferenceEquals(i, loot)), Is.True,
            "§128 «взял — обыскал»: вещь из карманов несомой не переехала.");
        Assert.That(patient.Inventory.Items.Any(i => ReferenceEquals(i, loot)), Is.False);
        Assert.That(carrier.IsCarryingPerson, Is.True,
            "Обыск в руках не смеет ронять несомую.");
        Assert.That(carrier.CarriedNpcId, Is.EqualTo(patient.Id));
    }
}

}
