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

    [Test]
    public void ManualFuelStocksColdPitAndIgniteNeedsLighter()
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
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
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
            Assert.That(npc.Inventory.Items.Contains(ContentIds.Stick), Is.False);
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
            Assert.That(fire.ResourceAmount, Is.GreaterThan(0f));
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.False,
                "Розжиг должен перевести первую вещь из буфера в активное топливо.");
            Assert.That(npc.Inventory.Items.Contains(GearCatalog.Lighter), Is.True,
                "Зажигалка — многоразовый инструмент и не расходуется.");
        });
    }
}

}
