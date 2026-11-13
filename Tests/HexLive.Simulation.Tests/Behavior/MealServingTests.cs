using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.MealPreparationTest;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class MealServingTests
{
    [OneTimeSetUp]
    public void LoadShippedContent() => SimDataFile.Require(RepoPaths.SimData);

    [TestCase(ContentIds.WolfCarcassCooked, 900)]
    [TestCase(HumanMeatCatalog.TorsoCooked, 1800)]
    [TestCase(HumanMeatCatalog.MaleLegCooked, 900)]
    [TestCase(HumanMeatCatalog.ArmCooked, 600)]
    [TestCase(ContentIds.MeatRoasted, 300)]
    public void PreparationHasNoEatAndYieldsTheExpectedPortions(string id, int grams)
    {
        var world = TestWorld.CreateWorld();
        Assert.That(FoodMath.CanEat(world.Content.ObjectDefinitions[id]), Is.False);
        var preparation = new ItemInstance(id);
        Assert.That(MealServing.PortionGrams(preparation), Is.EqualTo(grams));
        Assert.That(MealServing.OutputFor(id), Is.EqualTo(ContentIds.MeatCooked));
    }

    [Test]
    public void LegacyHalfEatenLegCannotRegainConsumedMeat()
    {
        var part = new ItemInstance(HumanMeatCatalog.LegCooked) { ResourceAmount = 5000f };
        Assert.That(MealServing.PortionGrams(part), Is.EqualTo(400));
    }

    [TestCase(95, ContentIds.MeatRoasted)]
    [TestCase(WorldSaveSerializer.BlobVersion, ContentIds.MeatCooked)]
    public void SavedCookedMeatOnlyStaysEdibleIfAlreadyServedInTheNewFormat(int version, string expectedId)
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        actor.Inventory.Items.Clear();
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked) { ResourceAmount = 300 });
        var ground = WorldObjectMutations.SpawnObject(world, ContentIds.MeatCooked, actor.Fragment,
            actor.Tile, world.Tiles.Items[actor.Tile].Junctions[0]);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(bytes))
            WorldSaveSerializer.Read(restored, reader);

        Assert.That(restored.Entities.Npcs[actor.Id].Inventory.Items.Single().DefinitionId, Is.EqualTo(expectedId));
        Assert.That(restored.Entities.Objects[ground.Id].DefinitionId, Is.EqualTo(expectedId));
    }

    [Test]
    public void EmptyHandsIslandProvidesFoodAndWaterThroughTheAutonomousStarterChain()
    {
        var definition = MealPreparationTestWorld.CreateDefinition(emptyHands: true);
        var world = MealPreparationTestWorld.CreateWorld(definition, emptyHands: true);
        var actor = world.Entities.Npcs.Values.Single();
        actor.Needs.Hunger = .68f;
        actor.Needs.Thirst = .78f;
        var clock = new SimulationClock();
        clock.Resume();
        var engine = new SimulationEngine(world, new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval
        }, clock);
        DefinitionIdTable.Build(world.Content);
        SimulationSystemRegistry.RegisterDefaults(engine);
        SimTrace.EnableAll();
        Assert.That(actor.Inventory.Items, Is.Empty);
        Assert.That(world.Entities.Objects.Values.Any(obj => MealServing.IsSurface(obj.DefinitionId)), Is.False);
        var drank = false;
        var ate = false;
        var maxThirst = actor.Needs.Thirst;
        for (var i = 0; i < 5000 && actor.Health > 0f && !(drank && ate); i++)
        {
            engine.Step();
            maxThirst = Math.Max(maxThirst, actor.Needs.Thirst);
            foreach (var evt in world.Events.Items)
            {
                drank |= evt.Type == "CoconutDrank" ||
                    evt.Type == "ItemConsumed" && evt.Message.StartsWith(ContentIds.CoconutPierced + " Drink");
                ate |= evt.Type == "CoconutEaten" ||
                    evt.Type == "ItemConsumed" && evt.Message.StartsWith(ContentIds.CoconutOpen + " Eat");
            }
        }

        Assert.That(drank && ate, Is.True, () =>
            $"Food={ate} water={drank} peakThirst={maxThirst:F2} tick={world.Tick}; " +
            string.Join("\n", world.Events.Items.TakeLast(20).Select(evt => evt.Type + ": " + evt.Message)));
        Assert.That(maxThirst, Is.LessThan(1f));
    }

    [Test]
    public void WolfOccupiesTheEntireOutdoorSpitThroughRoasting()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        var outdoorTile = world.Fragments.Items[actor.Fragment].Tiles.Values.First(tile =>
            !ShelterMath.IsIndoor(world, tile.Coord) && tile.Junctions.Count > 0);
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire, actor.Fragment,
            outdoorTile.Coord, outdoorTile.Junctions[0]);
        for (var i = 0; i < SimBalance.CampfireBillSticks; i++)
            fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        for (var i = 0; i < SimBalance.CampfireBillRope; i++)
            fire.Contents.Add(new ItemInstance(ContentIds.Rope));
        fire.ResourceAmount = 10000;

        var wolf = new ItemInstance(ContentIds.WolfCarcassRaw);
        var meat = new ItemInstance(ContentIds.MeatRaw);
        Assert.That(BuildSiteMath.CampfireSpitComplete(fire), Is.True);
        Assert.That(BuildSiteMath.CanHangMeat(world, fire, wolf.DefinitionId), Is.True);
        Assert.That(ContainerLootMath.CanAccept(world, fire, new[] { wolf, meat }), Is.False);
        Assert.That(ContainerLootMath.CanAccept(world, fire, new[] { wolf }), Is.True);
        fire.Contents.Add(wolf);
        wolf.ResourceAmount = SimBalance.MeatRoastDurationTicks - 1;

        new FireSystem().Run(world);
        Assert.That(fire.Contents.Any(item => item.DefinitionId == ContentIds.WolfCarcassCooked), Is.True);
        Assert.That(BuildSiteMath.UsedSpitHooks(fire), Is.EqualTo(SimBalance.CampfireSpitCapacity));
        Assert.That(ContainerLootMath.CanAccept(world, fire, new[] { meat }), Is.False);
    }
}
