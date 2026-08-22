using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§146.9: geometry, roster and loot contract of HugeIsland.</summary>
public sealed class HugeIslandWorldGenGateTests
{
    private static readonly int[] Seeds = { 12345, 777, 31337 };
    private static readonly Dictionary<int, WorldState> Worlds = new();

    private static WorldState Build(int seed)
    {
        if (!Worlds.TryGetValue(seed, out var world))
        {
            world = new WorldStateFactory().Create(
                PrototypeWorldDefinitionFactory.Create(seed, GameMode.HugeIsland));
            Worlds[seed] = world;
        }
        return world;
    }

    [Test]
    public void MapIsTwiceBigIslandAndHasSixSoloCampsPlusCentralOutsiders()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            Assert.That(world.Mode, Is.EqualTo(GameMode.HugeIsland), $"seed {seed}");
            Assert.That(world.Tiles.Items, Has.Count.EqualTo(102 * 80), $"seed {seed}");

            var girlFactions = new[]
            {
                Faction.Colony, Faction.Colony2, Faction.Colony3,
                Faction.Colony4, Faction.Colony5, Faction.Colony6
            };
            Assert.That(world.FactionHomes.Keys,
                Is.EquivalentTo(girlFactions.Append(Faction.Outsiders)), $"seed {seed}");

            var girls = world.Entities.Npcs.Values
                .Where(npc => girlFactions.Contains(npc.Faction)).ToList();
            Assert.That(girls, Has.Count.EqualTo(6), $"seed {seed}");
            foreach (var faction in girlFactions)
            {
                Assert.That(girls.Count(npc => npc.Faction == faction), Is.EqualTo(1),
                    $"seed {seed}: {faction}");
            }
            for (var a = 0; a < girlFactions.Length; a++)
            {
                for (var b = a + 1; b < girlFactions.Length; b++)
                {
                    Assert.That(FactionRelations.AreHostile(world,
                            girlFactions[a], girlFactions[b]), Is.False,
                        $"seed {seed}: {girlFactions[a]}/{girlFactions[b]} должны быть нейтральны");
                }
            }
            Assert.That(FactionRelations.AreHostile(world,
                    Faction.Colony, Faction.Outsiders), Is.True,
                $"seed {seed}: чужаки остаются врагами всех лагерей");
            Assert.That(world.Entities.Npcs.Values.Count(npc => npc.Faction == Faction.Outsiders),
                Is.EqualTo(1), $"seed {seed}: стартовый чужак");

            var center = new TileCoord(
                (PrototypeWorldDefinitionFactory.HugeMinQ +
                 PrototypeWorldDefinitionFactory.HugeMaxQ) / 2,
                (PrototypeWorldDefinitionFactory.HugeMinR +
                 PrototypeWorldDefinitionFactory.HugeMaxR) / 2);
            Assert.That(HexSpatialMath.HexDistance(
                    world.FactionHomes[Faction.Outsiders], center),
                Is.LessThanOrEqualTo(12), $"seed {seed}: чужаки не в центре");
        }
    }

    [Test]
    public void KshishtofExistsAtTickZeroAsArmedAbuserAndOwnsTheOnlyMachete()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var kshishtof = world.Entities.Npcs[new EntityId(101)];
            var carriedMachetes = world.Entities.Npcs.Values
                .SelectMany(npc => npc.Inventory.Items.Select(item =>
                    (owner: npc, item)))
                .Where(pair => pair.item.DefinitionId == GearCatalog.Machete)
                .ToArray();
            var looseMachetes = world.Entities.Objects.Values
                .Where(obj => obj.DefinitionId == GearCatalog.Machete)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(world.Tick, Is.Zero, $"seed {seed}: проверяется самый старт");
                Assert.That(kshishtof.DisplayName, Is.EqualTo("Kshishtof"));
                Assert.That(kshishtof.Faction, Is.EqualTo(Faction.Outsiders));
                Assert.That(kshishtof.Traits.Has(TraitKind.Abuser), Is.True,
                    "RaidSystem запускает абьюз по авторской черте Abuser.");
                Assert.That(carriedMachetes, Has.Length.EqualTo(1),
                    $"seed {seed}: стартовая мачете должна быть ровно одна");
                Assert.That(carriedMachetes[0].owner.Id, Is.EqualTo(kshishtof.Id),
                    $"seed {seed}: мачете можно получить только с Кшиштова");
                Assert.That(looseMachetes, Is.Empty,
                    $"seed {seed}: свободная мачете на карте запрещена");
            });
        }
    }

    [Test]
    public void GirlCampsStayDisjointReachableAndOwnTheirBuildSites()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var camps = world.FactionHomes
                .Where(pair => pair.Key != Faction.Outsiders)
                .OrderBy(pair => (int)pair.Key).ToArray();

            for (var a = 0; a < camps.Length; a++)
            {
                for (var b = a + 1; b < camps.Length; b++)
                {
                    Assert.That(HexSpatialMath.HexDistance(camps[a].Value, camps[b].Value),
                        Is.GreaterThanOrEqualTo(
                            PrototypeWorldDefinitionFactory.HugeCampMinSeparationTiles),
                        $"seed {seed}: {camps[a].Key}/{camps[b].Key}");
                    Assert.That(Connectivity.Reachable(world,
                            FreeJunction(world, camps[a].Value),
                            FreeJunction(world, camps[b].Value), canJump: true),
                        $"seed {seed}: лагеря отрезаны");
                }

                var home = camps[a].Value;
                Assert.That(world.Entities.Objects.Values.Any(obj =>
                    obj.DefinitionId == ContentIds.BuildSite &&
                    obj.BuildProduct == ContentIds.Campfire &&
                    HexSpatialMath.HexDistance(obj.Tile, home) <= 2), Is.True,
                    $"seed {seed}: {camps[a].Key} без кострового сайта");
                Assert.That(world.Entities.Objects.Values.Any(obj =>
                    obj.DefinitionId == ContentIds.BuildSite &&
                    obj.BuildProduct == ContentIds.HutPlan &&
                    HexSpatialMath.HexDistance(obj.Tile, home) <= 4), Is.True,
                    $"seed {seed}: {camps[a].Key} без Hut1Hex");
            }
        }
    }

    [Test]
    public void StartersHaveOnlyUnderwearAndBackpackWhileClothesAreWorldLoot()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var girls = world.Entities.Npcs.Values
                .Where(npc => npc.Faction != Faction.Outsiders).ToArray();
            foreach (var girl in girls)
            {
                Assert.That(girl.Inventory.Items, Is.Empty,
                    $"seed {seed}: NPC{girl.Id.Value} получила скрытый стартовый груз");
                Assert.That(girl.WornItems, Has.Count.EqualTo(3),
                    $"seed {seed}: NPC{girl.Id.Value} — нужны трусы, лифчик и рюкзак");
                Assert.That(girl.WornItems.Count(item => Garment(item.DefinitionId).Layer ==
                    WearLayer.Underwear), Is.EqualTo(2));
                Assert.That(girl.WornItems.Count(item => Garment(item.DefinitionId).Layer ==
                    WearLayer.Bags), Is.EqualTo(1));
            }

            var drops = world.Entities.Objects.Values
                .Where(obj => GarmentLibrary.Active.Any(g => g.Id == obj.DefinitionId))
                .ToArray();
            Assert.That(drops, Has.Length.EqualTo(50),
                $"seed {seed}: шестилагерный режим должен создавать ровно 50 вещей");

            foreach (var camp in world.FactionHomes.Where(pair =>
                         pair.Key != Faction.Outsiders))
            {
                var nearby = drops.Where(obj =>
                    HexSpatialMath.HexDistance(obj.Tile, camp.Value) <= 6).ToArray();
                Assert.That(drops.Any(obj =>
                    Category(obj.DefinitionId) == GarmentCategory.Bottom &&
                    HexSpatialMath.HexDistance(obj.Tile, camp.Value) <= 6), Is.True,
                    $"seed {seed}: {camp.Key} без штанов рядом");
                Assert.That(drops.Any(obj =>
                    Category(obj.DefinitionId) == GarmentCategory.Footwear &&
                    HexSpatialMath.HexDistance(obj.Tile, camp.Value) <= 6), Is.True,
                    $"seed {seed}: {camp.Key} без обуви рядом");
                Assert.That(nearby.Select(obj => obj.DefinitionId).Distinct().Count(),
                    Is.GreaterThanOrEqualTo(5),
                    $"seed {seed}: {camp.Key} должен получить 5 разных вещей рядом");
            }
        }
    }

    [Test]
    public void SaveRoundTripPreservesHugeModeAndOneShotSurvivor()
    {
        const int seed = 24680;
        var world = new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(seed, GameMode.HugeIsland));
        world.Tick = WorldBalance.DayLengthTicks * 6;
        new ShipwreckSurvivorSystem().Run(world);
        world.ColonyArrivalsProcessedByFaction[Faction.Colony6] = 3;

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(seed, GameMode.HugeIsland));
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Mode, Is.EqualTo(GameMode.HugeIsland));
            Assert.That(loaded.ColonyArrivalsProcessedByFaction[Faction.Colony6], Is.EqualTo(3));
            Assert.That(loaded.Entities.Npcs.ContainsKey(new EntityId(900)), Is.True);
            Assert.That(loaded.Entities.Npcs[new EntityId(900)].WornItems, Is.Empty);
        });
    }

    private static GarmentCategory Category(string id) =>
        Garment(id).Category;

    private static GarmentParams Garment(string id) =>
        GarmentLibrary.Active.First(garment => garment.Id == id);

    private static JunctionId FreeJunction(WorldState world, TileCoord tile) =>
        world.Tiles.Items[tile].Junctions.First(id => !world.Junctions.Items[id].Blocked);
}

}
