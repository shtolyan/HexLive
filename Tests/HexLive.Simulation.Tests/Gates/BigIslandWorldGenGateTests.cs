using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §146.4/§146.7: контракт генератора большого острова. Ни одно из этих
/// свойств не проверяется глазами: лагеря может развести в соседние бухты
/// один патологический сид, роща может замкнуться в стену, а поляна лагеря —
/// зарасти так, что чертёж дома некуда ставить. Гейт держит контракт на
/// нескольких сидах, чтобы «работает на 12345» не превращалось в спеку.
/// </summary>
public sealed class BigIslandWorldGenGateTests
{
    private static readonly int[] Seeds = { 12345, 777, 999, 31337 };

    private static WorldState Build(int seed) =>
        new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(seed, GameMode.BigIsland));

    [Test]
    public void ThreeHostileCampsExistAndOutsidersDoNot()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            Assert.That(world.Mode, Is.EqualTo(GameMode.BigIsland), $"seed {seed}");
            Assert.That(world.FactionHomes.Keys, Is.EquivalentTo(new[]
            {
                Faction.Colony, Faction.Colony2, Faction.Colony3
            }), $"seed {seed}: три лагеря девушек и никакого чужака (§146.4)");
            Assert.Multiple(() =>
            {
                Assert.That(FactionRelations.AreHostile(Faction.Colony, Faction.Colony2),
                    Is.True);
                Assert.That(FactionRelations.AreHostile(Faction.Colony, Faction.Colony3),
                    Is.True);
                Assert.That(FactionRelations.AreHostile(Faction.Colony2, Faction.Colony3),
                    Is.True);
            });
        }
    }

    [Test]
    public void CampsAreFarApartAndMutuallyReachable()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var anchors = world.FactionHomes.OrderBy(p => (int)p.Key)
                .Select(p => p.Value).ToList();

            for (var a = 0; a < anchors.Count; a++)
            {
                for (var b = a + 1; b < anchors.Count; b++)
                {
                    var distance = HexSpatialMath.HexDistance(anchors[a], anchors[b]);
                    Assert.That(distance,
                        Is.GreaterThanOrEqualTo(
                            PrototypeWorldDefinitionFactory.BigCampMinSeparationTiles),
                        $"seed {seed}: лагеря {a} и {b} слишком близко — " +
                        $"диски InCamp (радиус {Spec72.MaxCampRadiusTiles}) пересекутся");

                    var from = FreeJunction(world, anchors[a]);
                    var to = FreeJunction(world, anchors[b]);
                    Assert.That(Connectivity.Reachable(world, from, to, canJump: true),
                        $"seed {seed}: из лагеря {a} нет пути в лагерь {b} — " +
                        "лес или рельеф замкнулся в стену (§146.7)");
                }
            }
        }
    }

    [Test]
    public void ForestFeedsTenHousesButStaysPassable()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var palms = world.Entities.Objects.Values
                .Count(o => o.DefinitionId == "tree.palm");
            var yucca = world.Entities.Objects.Values
                .Count(o => o.DefinitionId == "plant.yucca");

            // §146.7: десять домов по 16-29 пальм + живой лес после стройки;
            // верхний предел ловит убежавшую плотность рощ. Диапазон привязан
            // к константе цели, чтобы рост ревизии не рассинхронизировал гейт.
            var target = PrototypeWorldDefinitionFactory.BigPalmTarget;
            Assert.That(palms, Is.InRange(target, (int)(target * 1.9f)),
                $"seed {seed}: пальм {palms} — посев разъехался с §146.7");
            Assert.That(yucca, Is.InRange(280, 360),
                $"seed {seed}: юкки {yucca} — верёвке не из чего виться");
        }
    }

    [Test]
    public void CampClearingsAreBuildableAndToolsAreNearby()
    {
        // Всё, что блокирует узлы, делает гекс нестроябельным в радиусе своего
        // диска (HexFreeForBuild) — поляна радиуса 2 обязана быть чистой.
        var obstacles = new HashSet<string>
        {
            "tree.palm", "rock.boulder", "plant.yucca", "herb.bush", "forest.deadfall"
        };

        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            foreach (var pair in world.FactionHomes.OrderBy(p => (int)p.Key))
            {
                var anchor = pair.Value;
                foreach (var obj in world.Entities.Objects.Values)
                {
                    if (obstacles.Contains(obj.DefinitionId))
                    {
                        Assert.That(HexSpatialMath.HexDistance(obj.Tile, anchor),
                            Is.GreaterThan(2),
                            $"seed {seed}: {obj.DefinitionId} на {obj.Tile} зарос " +
                            $"поляну лагеря {pair.Key} у {anchor} (§146.7)");
                    }
                }

                // §146.7: пила обязательна — без Saw-капабилити у дома не будет
                // досок; молоток поднимает постройку.
                AssertToolNear(world, anchor, "tool.saw", seed, pair.Key);
                AssertToolNear(world, anchor, "tool.hammer", seed, pair.Key);

                // §55.4 (bug #317): запасная бутылка лежит у каждого лагеря.
                AssertToolNear(world, anchor, "tool.bottle", seed, pair.Key);
            }
        }
    }

    [Test]
    public void SpareBottlesAreSeededAcrossTheIsland()
    {
        // §55.4 (bug #317): свою бутылку каждая девушка носит со старта, а на
        // острове ждут запасные — по одной у лагеря плюс пара в глуши.
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var bottles = world.Entities.Objects.Values
                .Count(o => o.DefinitionId == "tool.bottle");
            Assert.That(bottles, Is.InRange(3, 8),
                $"seed {seed}: бутылок на земле {bottles} — посев §55.4 разъехался");
        }
    }

    [Test]
    public void EveryStarterCarriesAnEmptyBottle()
    {
        // §55.4 (bug #317): стартовый комплект выровнен с прибывающей новенькой
        // (ColonyArrivalSystem): личная ПУСТАЯ бутылка есть у каждой, и в
        // «голом» старте HugeIsland тоже — остальной запас остаётся лутом.
        foreach (var mode in new[] { GameMode.BigIsland, GameMode.HugeIsland })
        {
            var world = new WorldStateFactory().Create(
                PrototypeWorldDefinitionFactory.Create(12345, mode));
            foreach (var npc in world.Entities.Npcs.Values)
            {
                Assert.That(npc.Inventory.Items.Contains(new ItemInstance("tool.bottle")),
                    $"{mode}: NPC{npc.Id.Value} без личной бутылки (§55.4)");
                Assert.That(npc.BottleCharges, Is.EqualTo(0),
                    $"{mode}: NPC{npc.Id.Value} стартует с непустой бутылкой");
            }
        }
    }

    [Test]
    public void SixGirlsSpawnTwoPerCampWithStableIds()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var byFaction = world.Entities.Npcs.Values
                .GroupBy(n => n.Faction)
                .ToDictionary(g => g.Key, g => g.Select(n => n.Id.Value).OrderBy(v => v).ToList());

            Assert.That(byFaction.Keys, Is.EquivalentTo(new[]
            {
                Faction.Colony, Faction.Colony2, Faction.Colony3
            }), $"seed {seed}");
            Assert.That(byFaction[Faction.Colony], Is.EqualTo(new[] { 1, 2 }), $"seed {seed}");
            Assert.That(byFaction[Faction.Colony2], Is.EqualTo(new[] { 11, 12 }), $"seed {seed}");
            Assert.That(byFaction[Faction.Colony3], Is.EqualTo(new[] { 21, 22 }), $"seed {seed}");

            foreach (var npc in world.Entities.Npcs.Values)
            {
                Assert.That(npc.Health, Is.GreaterThan(0f), $"seed {seed}: NPC{npc.Id.Value}");
                Assert.That(string.IsNullOrEmpty(npc.DisplayName), Is.False,
                    $"seed {seed}: NPC{npc.Id.Value} без выпавшего имени — " +
                    "AssignAppearance не считает её девушкой (§146.3)");
            }
        }
    }

    [Test]
    public void EveryCampStakesItsOwnHearthSite()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            foreach (var pair in world.FactionHomes.OrderBy(p => (int)p.Key))
            {
                var found = world.Entities.Objects.Values.Any(o =>
                    o.DefinitionId == Content.ContentIds.BuildSite &&
                    o.BuildProduct == Content.ContentIds.Campfire &&
                    HexSpatialMath.HexDistance(o.Tile, pair.Value) <= 2);
                Assert.That(found,
                    $"seed {seed}: у лагеря {pair.Key} нет кострового сайта §54.6");
            }
        }
    }

    [Test]
    public void EveryCampStakesItsOwnEditableHutBlueprint()
    {
        foreach (var seed in Seeds)
        {
            var world = Build(seed);
            var sites = world.Entities.Objects.Values
                .Where(o => o.DefinitionId == Content.ContentIds.BuildSite &&
                            o.BuildProduct == Content.ContentIds.HutPlan)
                .ToList();

            Assert.That(sites, Has.Count.EqualTo(3),
                $"seed {seed}: чертёж Hut1Hex обязан стоять у каждого лагеря (§146.5)");

            var usedBlueprints = new HashSet<int>();
            foreach (var pair in world.FactionHomes.OrderBy(p => (int)p.Key))
            {
                var near = sites.Where(s =>
                    HexSpatialMath.HexDistance(s.Tile, pair.Value) <= 4).ToList();
                Assert.That(near, Has.Count.EqualTo(1),
                    $"seed {seed}: у лагеря {pair.Key} не ровно один чертёж");

                var site = near[0];
                Assert.That(site.BlueprintId, Is.GreaterThan(0),
                    $"seed {seed}: сайт лагеря {pair.Key} не привязан к драфту реестра");
                Assert.That(world.PlayerBlueprints.ContainsKey(site.BlueprintId),
                    $"seed {seed}: драфт {site.BlueprintId} не зарегистрирован");
                Assert.That(usedBlueprints.Add(site.BlueprintId),
                    $"seed {seed}: лагеря делят один драфт — правка игроком своего " +
                    "чертежа мутировала бы чужие дома (§146.5)");
            }
        }
    }

    private static void AssertToolNear(
        WorldState world, TileCoord anchor, string toolId, int seed, Faction camp)
    {
        var found = world.Entities.Objects.Values.Any(o =>
            o.DefinitionId == toolId &&
            HexSpatialMath.HexDistance(o.Tile, anchor) <= 4);
        Assert.That(found, $"seed {seed}: у лагеря {camp} нет {toolId} в радиусе 4 (§146.7)");
    }

    private static JunctionId FreeJunction(WorldState world, TileCoord tile)
    {
        var junctions = world.Tiles.Items[tile].Junctions;
        foreach (var id in junctions)
        {
            if (!world.Junctions.Items[id].Blocked)
            {
                return id;
            }
        }

        return junctions[0];
    }
}

}
