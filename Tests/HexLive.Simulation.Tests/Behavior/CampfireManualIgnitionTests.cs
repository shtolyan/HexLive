using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>Bug #228: stocking a cold campfire is not ignition.</summary>
public sealed class CampfireManualIgnitionTests
{
    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static WorldObjectState SpawnColdFire(WorldState world, NPCState npc)
    {
        var start = npc.CurrentJunction!.Value;
        var junction = world.Junctions.Items.Values.First(candidate =>
            !candidate.Blocked &&
            candidate.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, candidate.Tiles[0]) is >= 2 and <= 4 &&
            Connectivity.Reachable(world, start, candidate.Id)).Id;
        var tile = world.Junctions.Items[junction].Tiles[0];
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, tile, junction);
        fire.ResourceAmount = 0f;
        return fire;
    }

    private static void StepUntil(
        SimulationEngine engine, System.Func<bool> condition, int maxTicks = 600)
    {
        for (var i = 0; i < maxTicks && !condition(); i++)
        {
            engine.Step();
        }
    }

    [TestCase(ContentIds.Stick, ContainerLootMath.FuelTicksPerStick)]
    [TestCase(ContentIds.Board, ContainerLootMath.FuelTicksPerStick)]
    [TestCase(ContentIds.Log, ContainerLootMath.FuelTicksPerStick * 4f)]
    public void ManualFuelStocksAnyWoodAndIgniteNeedsLighter(
        string fuelId, float expectedFuel)
    {
        var engine = TestWorld.CreateEngine(22801);
        var world = engine.World;
        var npc = Colonist(world);
        engine.Step(); // bootstrap places NPCs onto their first junction
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        var control = ManualCommandExecutor.Apply(
            world, new SetManualControlCommand(npc.Id, true));
        Assert.That(control.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(fuelId));
        var fire = SpawnColdFire(world, npc);

        var stock = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, fire.Id, InteractionType.Fuel));
        Assert.That(stock.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            stock.Reason);

        StepUntil(engine, () =>
            ContainerLootMath.HasQueuedCampfireFuel(world, fire));

        Assert.Multiple(() =>
        {
            Assert.That(fire.ResourceAmount, Is.Zero,
                "«Подбросить» не должно зажигать холодный костёр.");
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.True);
            Assert.That(npc.Inventory.Items.Contains(fuelId), Is.False);
        });

        var withoutLighter = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, fire.Id, InteractionType.Ignite));
        Assert.Multiple(() =>
        {
            Assert.That(withoutLighter.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(withoutLighter.Reason, Is.EqualTo("MissingTool"));
            Assert.That(fire.ResourceAmount, Is.Zero);
        });

        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Lighter));
        var ignite = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, fire.Id, InteractionType.Ignite));
        Assert.That(ignite.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted));

        StepUntil(engine, () => fire.ResourceAmount > 0f);

        Assert.Multiple(() =>
        {
            Assert.That(fire.ResourceAmount,
                Is.InRange(expectedFuel - 16f, expectedFuel),
                "Между розжигом и наблюдением проходит один slow tick горения.");
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.False,
                "Розжиг должен перевести первую вещь из буфера в активное топливо.");
            Assert.That(npc.Inventory.Items.Contains(GearCatalog.Lighter), Is.True,
                "Зажигалка — многоразовый инструмент и не расходуется.");
        });
    }

    [Test]
    public void QueuedWoodCountsAsAutonomousFireFuelWithoutCarriedStick()
    {
        var world = TestWorld.CreateWorld(274);
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, npc.Tile,
            npc.CurrentJunction ?? world.Junctions.Items.Keys.First());
        fire.ResourceAmount = 0f;

        var log = ContainerLootMath.FindCarriedCampfireFuel(world, npc);
        ContainerLootMath.GiveToContainer(world, fire, npc, new[] { log! });

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items, Is.Empty);
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.True);
            Assert.That(
                ContainerLootMath.HasCampfireFuelAvailable(world, npc, fire),
                Is.True,
                "AI должен видеть уже загруженный холодный костёр как готовый к розжигу.");
        });
    }

    [Test]
    public void RaisedCampfireWithOpenUpgradeBillStillAcceptsFuelOrder()
    {
        var engine = TestWorld.CreateEngine(27402);
        var world = engine.World;
        var npc = Colonist(world);
        engine.Step();
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        ManualCommandExecutor.Apply(
            world, new SetManualControlCommand(npc.Id, true));
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));

        var fire = SpawnColdFire(world, npc);
        fire.BuildProduct = ContentIds.Campfire;
        fire.BillSticks = BuildSiteMath.CampfireStage1Sticks + 3;
        fire.BillRope = 2;
        fire.BillStones = 18;

        Assert.Multiple(() =>
        {
            Assert.That(BuildSiteMath.IsSite(fire), Is.True,
                "Открытый счёт улучшений по-прежнему должен быть виден строителям.");
            Assert.That(BuildSiteMath.UsesGenericSiteInteractions(fire), Is.False,
                "Поднятый костёр уже обязан сохранять собственные действия.");
        });

        var admission = ManualCommandExecutor.Apply(
            world, new InteractCommand(
                npc.Id, fire.Id, InteractionType.Fuel, "fuel.fire"));

        Assert.That(admission.Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);

        StepUntil(engine, () =>
            ContainerLootMath.HasQueuedCampfireFuel(world, fire));

        Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.True,
            "Исполнение тоже не должно повторно подменять костёр стройплощадкой.");
    }
}

}
