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

public sealed class PlantGrowthTests
{
    [SetUp]
    public void SetUp() => GearCatalog.ResetToDefaults();

    [TearDown]
    public void TearDown() => GearCatalog.ResetToDefaults();

    [Test]
    public void EveryRenewablePlantHasASeedAndThreeAuthoredStages()
    {
        var definitions = PrototypeContentCatalog.CreateDefaults();
        var renewable = definitions.Values.Where(d => d.HasTag("SeedPlant")).ToArray();

        Assert.That(renewable.Select(d => d.Id), Is.EquivalentTo(
            PlantGrowthCatalog.All.Select(p => p.MatureDefinitionId)));
        foreach (var profile in PlantGrowthCatalog.All)
        {
            Assert.Multiple(() =>
            {
                Assert.That(definitions.ContainsKey(profile.SeedDefinitionId), Is.True);
                Assert.That(definitions[profile.SeedDefinitionId].HasTag("Seed"), Is.True);
                Assert.That(definitions[profile.SeedDefinitionId].Interactions.Any(
                    interaction => interaction.Type == InteractionType.Plant), Is.True);
                Assert.That(profile.StageDefinitionIds, Has.Length.EqualTo(3));
                Assert.That(profile.StageDefinitionIds.All(id =>
                    definitions.TryGetValue(id, out var stage) && stage.HasTag("GrowthStage")), Is.True);
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(PlantGrowthCatalog.All.Single(p => p.SeedDefinitionId == ContentIds.Coconut)
                .TotalGrowthTicks, Is.EqualTo(WorldBalance.DayLengthTicks * 30));
            Assert.That(PlantGrowthCatalog.All.Single(p => p.SeedDefinitionId == ContentIds.HerbSeed)
                .TotalGrowthTicks, Is.EqualTo(WorldBalance.DayLengthTicks * 10));
            Assert.That(PlantGrowthCatalog.All.Single(p => p.SeedDefinitionId == ContentIds.YuccaSeed)
                .TotalGrowthTicks, Is.EqualTo(WorldBalance.DayLengthTicks * 10));
        });
    }

    [Test]
    public void CoconutPlantingConsumesTheNutAndCatchesUpThroughAllStages()
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 100;
        var (tile, anchor) = FreeAnchor(world);
        var coconut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Coconut, world.Junctions.Items[anchor].Fragment, tile, anchor);

        var sprout = PlantGrowthSystem.TryPlant(world, coconut);
        Assert.That(sprout, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(coconut.Id), Is.False);
            Assert.That(sprout.DefinitionId, Is.EqualTo(ContentIds.PalmSprout));
            Assert.That(sprout.SpawnTick, Is.EqualTo(100));
        });

        var growth = new PlantGrowthSystem();
        var plantedAt = sprout.SpawnTick;
        AssertStage(world, growth, anchor, plantedAt + WorldBalance.PalmGrowthStageTicks,
            ContentIds.PalmSapling, plantedAt);
        AssertStage(world, growth, anchor, plantedAt + 2 * WorldBalance.PalmGrowthStageTicks,
            ContentIds.PalmYoung, plantedAt);
        AssertStage(world, growth, anchor, plantedAt + 3 * WorldBalance.PalmGrowthStageTicks,
            ContentIds.Palm, plantedAt);
    }

    [Test]
    public void ManualPlantOrderRunsTheRealTimedExecutionPath()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var colonist in world.Entities.Npcs.Values)
        {
            engine.ApplyManualCommand(new SetManualControlCommand(colonist.Id, true));
        }
        npc.Needs.Stamina = 1f;
        engine.Step();
        var anchor = SpatialQueries.GetPassableNeighbors(world, npc.CurrentJunction!.Value)
            .First(id => world.Entities.Objects.Values.All(o => !o.Junctions.Contains(id)));
        var tile = world.Junctions.Items[anchor].Tiles[0];
        var coconut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Coconut, npc.Fragment, tile, anchor);

        var admission = engine.ApplyManualCommand(
            new InteractCommand(npc.Id, coconut.Id, InteractionType.Plant));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var i = 0; i < 400 && world.Entities.Objects.ContainsKey(coconut.Id); i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(coconut.Id), Is.False);
            Assert.That(world.Entities.Objects.Values.Any(o =>
                o.DefinitionId == ContentIds.PalmSprout && o.Junctions.Contains(anchor)), Is.True);
        });
    }

    [Test]
    public void StumpNeverRegrowsAndOnlyTheAxeCanRemoveItForTenSticksAndTenLogs()
    {
        var world = TestWorld.CreateWorld();
        var definitions = world.Content.ObjectDefinitions;
        var palm = definitions[ContentIds.Palm].Interactions.Single(i => i.Id == "chop.palm");
        var stumpDefinition = definitions[ContentIds.PalmStump];
        var removal = stumpDefinition.Interactions.Single(i => i.Id == "remove.stump");

        Assert.Multiple(() =>
        {
            Assert.That(removal.DurationTicks, Is.EqualTo(palm.DurationTicks * 5));
            Assert.That(removal.RequiredCapabilities,
                Is.EqualTo(new[] { GearCapability.RemoveStump }));
            Assert.That(removal.Yields.Single(y => y.DefinitionId == ContentIds.Stick).Count,
                Is.EqualTo(10));
            Assert.That(removal.Yields.Single(y => y.DefinitionId == ContentIds.Log).Count,
                Is.EqualTo(10));
            Assert.That(GearCatalog.For(GearCatalog.Axe).Has(GearCapability.RemoveStump), Is.True);
            Assert.That(GearCatalog.For(GearCatalog.Machete).Has(GearCapability.RemoveStump), Is.False);
            Assert.That(GearCatalog.For(GearCatalog.Saw).Has(GearCapability.RemoveStump), Is.False);
        });

        world.Tick = 100;
        var (tile, anchor) = FreeAnchor(world);
        var stump = WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmStump, world.Junctions.Items[anchor].Fragment, tile, anchor);
        world.Tick += WorldBalance.DayLengthTicks * 100;
        new FruitProductionSystem().Run(world);
        new PlantGrowthSystem().Run(world);
        Assert.That(world.Entities.Objects.TryGetValue(stump.Id, out var stillStump) &&
            stillStump.DefinitionId == ContentIds.PalmStump, Is.True,
            "A stump is now inert until an axe removes it; elapsed time must never create a palm.");
    }

    [Test]
    public void AxeRemovalRunsTheTimedProcessAndSpawnsBothTenItemYields()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var colonist in world.Entities.Npcs.Values)
        {
            engine.ApplyManualCommand(new SetManualControlCommand(colonist.Id, true));
        }
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
        npc.Needs.Stamina = 1f;
        engine.Step();

        // Isolate this yield assertion from old prototype-island fuel that may
        // be consumed or decay while the long work order is running.
        foreach (var existing in world.Entities.Objects.Values.Where(o =>
                     o.DefinitionId is ContentIds.Stick or ContentIds.Log).ToArray())
        {
            WorldObjectMutations.DespawnObject(world, existing.Id);
        }
        foreach (var survivor in world.Entities.Npcs.Values)
        {
            survivor.Inventory.Items.RemoveAll(item =>
                item.DefinitionId is ContentIds.Stick or ContentIds.Log);
        }

        var (tile, anchor) = FreeAnchor(world);
        var stump = WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmStump, world.Junctions.Items[anchor].Fragment, tile, anchor);
        var sticksBefore = CountWorldAndInventory(world, npc, ContentIds.Stick);
        var logsBefore = CountWorldAndInventory(world, npc, ContentIds.Log);

        var admission = engine.ApplyManualCommand(new InteractCommand(
            npc.Id, stump.Id, InteractionType.Process, "remove.stump"));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var i = 0; i < 2000 && world.Entities.Objects.ContainsKey(stump.Id); i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(stump.Id), Is.False);
            Assert.That(CountWorldAndInventory(world, npc, ContentIds.Stick) - sticksBefore,
                Is.EqualTo(10));
            Assert.That(CountWorldAndInventory(world, npc, ContentIds.Log) - logsBefore,
                Is.EqualTo(10));
        });
    }

    [Test]
    public void HarvestedHerbAndYuccaEachYieldTwoSeeds()
    {
        var definitions = PrototypeContentCatalog.CreateDefaults();
        var herb = definitions[ContentIds.HerbBush].Interactions.Single(i => i.Type == InteractionType.Harvest);
        var yucca = definitions[ContentIds.Yucca].Interactions.Single(i => i.Type == InteractionType.Harvest);

        Assert.Multiple(() =>
        {
            Assert.That(herb.Yields.Single(y => y.DefinitionId == ContentIds.HerbSeed).Count,
                Is.EqualTo(2));
            Assert.That(yucca.Yields.Single(y => y.DefinitionId == ContentIds.YuccaSeed).Count,
                Is.EqualTo(2));
        });
    }

    [Test]
    public void HerbPassiveSeedDropIsDeterministicAndHonoursTheLocalCap()
    {
        var previousChance = WorldBalance.HerbSeedDropChance;
        var previousCap = WorldBalance.HerbSeedGroundCap;
        try
        {
            WorldBalance.HerbSeedDropChance = 1f;
            WorldBalance.HerbSeedGroundCap = 2;
            var world = TestWorld.CreateWorld();
            var herb = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.HerbBush);
            foreach (var producer in world.Entities.Objects.Values.Where(o =>
                         world.Content.ObjectDefinitions[o.DefinitionId].Produce != null))
            {
                producer.NextProductionTick = int.MaxValue;
            }
            herb.NextProductionTick = 0;
            var production = new FruitProductionSystem();

            for (var cycle = 1; cycle <= 3; cycle++)
            {
                world.Tick = (cycle - 1) * 240;
                herb.NextProductionTick = world.Tick;
                production.Run(world);
                Assert.That(world.Entities.Objects.Values.Count(o =>
                    o.DefinitionId == ContentIds.HerbSeed), Is.EqualTo(System.Math.Min(cycle, 2)));

                foreach (var leaf in world.Entities.Objects.Values.Where(o =>
                             o.DefinitionId == ContentIds.HerbLeaf).ToArray())
                {
                    WorldObjectMutations.DespawnObject(world, leaf.Id);
                }
            }
        }
        finally
        {
            WorldBalance.HerbSeedDropChance = previousChance;
            WorldBalance.HerbSeedGroundCap = previousCap;
        }
    }

    private static void AssertStage(
        WorldState world, PlantGrowthSystem growth, JunctionId anchor,
        int tick, string expectedDefinitionId, int expectedSpawnTick)
    {
        world.Tick = tick;
        growth.Run(world);
        var plant = world.Entities.Objects.Values.Single(o => o.Junctions.Contains(anchor));
        Assert.Multiple(() =>
        {
            Assert.That(plant.DefinitionId, Is.EqualTo(expectedDefinitionId));
            Assert.That(plant.SpawnTick, Is.EqualTo(expectedSpawnTick));
        });
    }

    private static (TileCoord Tile, JunctionId Anchor) FreeAnchor(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values.OrderBy(j => j.Id.Value))
        {
            if (junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionPassable(world, junction.Id) ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                world.Entities.Objects.Values.Any(o => o.Junctions.Contains(junction.Id)))
            {
                continue;
            }
            return (junction.Tiles[0], junction.Id);
        }
        throw new System.InvalidOperationException("Test island has no free planting anchor.");
    }

    private static int CountWorldAndInventory(
        WorldState world, NPCState npc, string definitionId) =>
        world.Entities.Objects.Values.Count(o => o.DefinitionId == definitionId) +
        npc.Inventory.Items.Count(i => i.DefinitionId == definitionId);
}

}
