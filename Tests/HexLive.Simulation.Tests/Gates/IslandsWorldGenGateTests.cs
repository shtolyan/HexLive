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

/// <summary>
/// §157: контракт геометрии, ростера и старта режима «Острова». Главное, что
/// здесь сторожится, — шесть ОТДЕЛЬНЫХ массивов суши, по лагерю в каждом, и
/// броды, через которые они всё же достижимы друг для друга.
/// </summary>
public sealed class IslandsWorldGenGateTests
{
    private static readonly int[] Seeds = { 12345, 777, 31337 };
    private static readonly Dictionary<int, WorldState> Worlds = new();

    private static readonly Faction[] GirlFactions =
    {
        Faction.Colony, Faction.Colony2, Faction.Colony3,
        Faction.Colony4, Faction.Colony5, Faction.Colony6
    };

    private static WorldState Build(int seed)
    {
        if (!Worlds.TryGetValue(seed, out var world))
        {
            world = new WorldStateFactory().Create(
                PrototypeWorldDefinitionFactory.Create(seed, GameMode.Islands));
            Worlds[seed] = world;
        }
        return world;
    }

    [Test]
    public void MapIsSixHugeCellsWithOneSoloCampEachAndNoOutsiders()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            Assert.That(world.Mode, Is.EqualTo(GameMode.Islands), $"seed {seed}");
            Assert.That(world.Tiles.Items, Has.Count.EqualTo(314 * 168), $"seed {seed}");
            Assert.That(world.FactionHomes.Keys, Is.EquivalentTo(GirlFactions),
                $"seed {seed}: только шесть девичьих лагерей, стоянки чужаков нет");

            var girls = world.Entities.Npcs.Values.ToList();
            Assert.That(girls, Has.Count.EqualTo(6), $"seed {seed}: ровно шесть девушек на старте");
            foreach (var faction in GirlFactions)
            {
                Assert.That(girls.Count(npc => npc.Faction == faction), Is.EqualTo(1),
                    $"seed {seed}: {faction}");
            }
            Assert.That(girls.Any(npc => npc.Faction == Faction.Outsiders), Is.False,
                $"seed {seed}: чужаки приходят с моря, а не стоят на нулевом тике (§157.7)");

            for (var a = 0; a < GirlFactions.Length; a++)
            {
                for (var b = a + 1; b < GirlFactions.Length; b++)
                {
                    Assert.That(FactionRelations.AreHostile(world, GirlFactions[a], GirlFactions[b]),
                        Is.False, $"seed {seed}: лагеря {GirlFactions[a]}/{GirlFactions[b]} нейтральны");
                }
            }
        }
    }

    /// <summary>
    /// Шесть компонент суши — не удача сида, а свойство спада-максимума
    /// (§157.1): ядро каждой ячейки всегда суша, и суша никогда не выходит
    /// за ячейку. Гейт проверяет следствие: центры ячеек лежат в шести
    /// РАЗНЫХ компонентах, и лагерь k стоит в компоненте k.
    /// </summary>
    [Test]
    public void EachIslandIsItsOwnLandmassAndHoldsItsCamp()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var components = new List<HashSet<TileCoord>>();
            foreach (var cell in PrototypeWorldDefinitionFactory.IslandCells)
            {
                var start = NearestDry(world, cell.Center);
                var component = Flood(world, start, coord => !IsWater(world, coord));
                Assert.That(component, Has.Count.GreaterThanOrEqualTo(2000),
                    $"seed {seed}: остров {cell.Index} слишком мал");
                foreach (var other in components)
                {
                    Assert.That(other.Overlaps(component), Is.False,
                        $"seed {seed}: острова слились в один массив");
                }

                Assert.That(component, Does.Contain(world.FactionHomes[GirlFactions[cell.Index]]),
                    $"seed {seed}: лагерь {GirlFactions[cell.Index]} не на своём острове");
                components.Add(component);
            }
        }
    }

    [Test]
    public void FordsJoinTheIslandsAndCampsReachEachOther()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var fords = world.Tiles.Items.Values
                .Where(tile => (tile.Flags & TileFlags.Water) != 0 &&
                               (tile.Flags & TileFlags.Walkable) != 0)
                .ToList();
            Assert.That(fords, Is.Not.Empty, $"seed {seed}: ни одного брода");
            foreach (var ford in fords)
            {
                Assert.That(ford.Elevation, Is.Zero,
                    $"seed {seed}: брод {ford.Coord} не на уровне моря (20.16)");
                foreach (var dir in HexDirection.All)
                {
                    var n = new TileCoord(ford.Coord.Q + dir.DQ, ford.Coord.R + dir.DR);
                    if (world.Tiles.Items.TryGetValue(n, out var tile) && !IsWater(world, n))
                    {
                        Assert.That(tile.Elevation, Is.EqualTo(1),
                            $"seed {seed}: у брода {ford.Coord} утёс {n}");
                    }
                }
            }

            var camps = GirlFactions.Select(f => world.FactionHomes[f]).ToArray();
            var walkable = Flood(world, camps[0], coord => IsWalkable(world, coord));
            foreach (var camp in camps)
            {
                Assert.That(walkable, Does.Contain(camp),
                    $"seed {seed}: лагерь {camp} отрезан по тайлам");
            }

            for (var a = 0; a < camps.Length; a++)
            {
                for (var b = a + 1; b < camps.Length; b++)
                {
                    Assert.That(HexSpatialMath.HexDistance(camps[a], camps[b]),
                        Is.GreaterThanOrEqualTo(PrototypeWorldDefinitionFactory.HugeCampMinSeparationTiles),
                        $"seed {seed}: лагеря {a}/{b} слишком близко");
                    Assert.That(Connectivity.Reachable(world,
                            FreeJunction(world, camps[a]), FreeJunction(world, camps[b]), canJump: true),
                        $"seed {seed}: лагеря {a}/{b} отрезаны по джанкшенам");
                }
            }
        }
    }

    [Test]
    public void EveryCampHasAFiresiteAndAHutPlan()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            foreach (var faction in GirlFactions)
            {
                var home = world.FactionHomes[faction];
                Assert.That(world.Entities.Objects.Values.Any(obj =>
                    obj.DefinitionId == ContentIds.BuildSite &&
                    obj.BuildProduct == ContentIds.Campfire &&
                    HexSpatialMath.HexDistance(obj.Tile, home) <= 2), Is.True,
                    $"seed {seed}: {faction} без кострового сайта");
                Assert.That(world.Entities.Objects.Values.Any(obj =>
                    obj.DefinitionId == ContentIds.BuildSite &&
                    obj.BuildProduct == ContentIds.HutPlan &&
                    HexSpatialMath.HexDistance(obj.Tile, home) <= 4), Is.True,
                    $"seed {seed}: {faction} без Hut1Hex");
            }
        }
    }

    /// <summary>§157.8: трусы, лифчик, рюкзак, пустая бутылка, пять бинтов — и ничего сверх.</summary>
    [Test]
    public void StartersWearUnderwearAndBackpackAndCarryBottleWithFiveBandages()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            foreach (var girl in world.Entities.Npcs.Values)
            {
                var carried = girl.Inventory.Items.Select(i => i.DefinitionId).ToList();
                Assert.That(carried.Count(id => id == ContentIds.Bottle), Is.EqualTo(1),
                    $"seed {seed}: NPC{girl.Id.Value} — одна бутылка");
                Assert.That(girl.BottleCharges, Is.Zero, $"seed {seed}: бутылка пустая");
                Assert.That(carried.Count(id => id == ContentIds.Bandage), Is.EqualTo(5),
                    $"seed {seed}: NPC{girl.Id.Value} — пять бинтов");
                Assert.That(girl.Inventory.Items.Where(i => i.DefinitionId == ContentIds.Bandage)
                    .All(i => i.ResourceAmount == 0f), Is.True, $"seed {seed}: бинты фабричные");
                Assert.That(carried, Has.Count.EqualTo(6), $"seed {seed}: скрытого груза нет");
                Assert.That(girl.WornItems, Has.Count.EqualTo(3),
                    $"seed {seed}: NPC{girl.Id.Value} — трусы, лифчик и рюкзак");
                Assert.That(girl.WornItems.Count(item => Garment(item.DefinitionId).Layer ==
                    WearLayer.Underwear), Is.EqualTo(2));
                Assert.That(girl.WornItems.Count(item => Garment(item.DefinitionId).Layer ==
                    WearLayer.Bags), Is.EqualTo(1));
            }
        }
    }

    /// <summary>
    /// §157.8: мало, целое, со шлемом и двумя рюкзаками — и только на своём
    /// острове. Три строки порчи из §146.9 здесь не зовутся.
    /// </summary>
    [Test]
    public void EachIslandHoldsASmallIntactLootSetWithHelmetAndTwoBackpacks()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var drops = world.Entities.Objects.Values
                .Where(obj => GarmentLibrary.Active.Any(g => g.Id == obj.DefinitionId))
                .ToArray();
            Assert.That(drops.Length, Is.InRange(66, 78),
                $"seed {seed}: 11-13 вещей на остров, а не {drops.Length / 6.0:F1}");
            foreach (var drop in drops)
            {
                Assert.That(drop.Durability, Is.EqualTo(1f), $"seed {seed}: {drop.DefinitionId} изношена");
                Assert.That(drop.Dirtiness, Is.Zero, $"seed {seed}: {drop.DefinitionId} грязная");
                Assert.That(drop.Wetness, Is.Zero, $"seed {seed}: {drop.DefinitionId} мокрая");
            }

            foreach (var faction in GirlFactions)
            {
                var home = world.FactionHomes[faction];
                var nearby = drops.Where(obj => HexSpatialMath.HexDistance(obj.Tile, home) <= 12).ToArray();
                Assert.That(nearby.Count(obj => obj.DefinitionId.StartsWith("clothing.helmet_")),
                    Is.GreaterThanOrEqualTo(1), $"seed {seed}: {faction} без шлема");
                Assert.That(nearby.Count(obj => Category(obj.DefinitionId) == GarmentCategory.Bag),
                    Is.GreaterThanOrEqualTo(2), $"seed {seed}: {faction} без двух рюкзаков");
                Assert.That(nearby.Count(obj => Category(obj.DefinitionId) == GarmentCategory.Footwear),
                    Is.GreaterThanOrEqualTo(2), $"seed {seed}: {faction} без обуви");
                Assert.That(nearby.Count(obj => BuildingBootstrap.IsStarterWardrobePants(Garment(obj.DefinitionId))),
                    Is.GreaterThanOrEqualTo(2), $"seed {seed}: {faction} без штанов");
                Assert.That(nearby.Count(obj => Category(obj.DefinitionId) == GarmentCategory.Bottom &&
                                                obj.DefinitionId.Contains("skirt")),
                    Is.GreaterThanOrEqualTo(2), $"seed {seed}: {faction} без юбок");
                Assert.That(nearby.Length, Is.GreaterThanOrEqualTo(11),
                    $"seed {seed}: {faction} получил {nearby.Length} вещей");
            }
        }
    }

    [Test]
    public void SaveRoundTripKeepsIslandsMode()
    {
        var world = Build(Seeds[0]);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(Seeds[0], GameMode.Islands));
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        WorldSaveSerializer.Read(loaded, reader);
        Assert.That(loaded.Mode, Is.EqualTo(GameMode.Islands));
        Assert.That(loaded.FactionHomes.Keys, Is.EquivalentTo(GirlFactions));
    }

    // ── хелперы ─────────────────────────────────────────────────────────

    private static bool IsWater(WorldState world, TileCoord coord) =>
        (world.Tiles.Items[coord].Flags & TileFlags.Water) != 0;

    private static bool IsWalkable(WorldState world, TileCoord coord) =>
        (world.Tiles.Items[coord].Flags & TileFlags.Walkable) != 0;

    private static TileCoord NearestDry(WorldState world, TileCoord from)
    {
        if (world.Tiles.Items.ContainsKey(from) && !IsWater(world, from))
        {
            return from;
        }

        return world.Tiles.Items.Keys
            .Where(coord => !IsWater(world, coord))
            .OrderBy(coord => HexSpatialMath.HexDistance(coord, from))
            .ThenBy(coord => coord.Q).ThenBy(coord => coord.R)
            .First();
    }

    private static HashSet<TileCoord> Flood(
        WorldState world, TileCoord start, System.Func<TileCoord, bool> inside)
    {
        var seen = new HashSet<TileCoord> { start };
        var queue = new Queue<TileCoord>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dir in HexDirection.All)
            {
                var next = new TileCoord(current.Q + dir.DQ, current.R + dir.DR);
                if (seen.Contains(next) || !world.Tiles.Items.ContainsKey(next) || !inside(next))
                {
                    continue;
                }

                seen.Add(next);
                queue.Enqueue(next);
            }
        }

        return seen;
    }

    private static GarmentCategory Category(string id) => Garment(id).Category;

    private static GarmentParams Garment(string id) =>
        GarmentLibrary.Active.First(garment => garment.Id == id);

    private static JunctionId FreeJunction(WorldState world, TileCoord tile) =>
        world.Tiles.Items[tile].Junctions.First(id => !world.Junctions.Items[id].Blocked);
}

}
