using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §106 «Вода — убежище»: пловца не бьют с суши, пловец не бьёт сам, и всякая
/// погоня за нырнувшей жертвой бросается. Среда атаки остаётся данными
/// (<c>MobStats.AttackMediums</c>) для будущих видов.
///
/// <para>
/// Главный риск фичи — кольцо §102 нового образца: гейт «нельзя бить» без
/// клапана «нельзя хотеть» оставил бы охотника вечно шагать к недосягаемой
/// цели. Поэтому тесты меряют ОБЕ половины: что удар не проходит и что цель
/// отпускается.
/// </para>
/// </summary>
public sealed class WaterSanctuaryTests
{
    // Смежная пара на суше — как в SwingSignalTests, плюс координата глубокой
    // воды, куда можно «нырнуть» (перенос npc.Tile: §106 меряет по тайлу).
    private static (WorldState world, NPCState a, NPCState b, TileCoord swim) Setup()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToList();
        var a = npcs[0];
        var b = npcs[1];

        var from = world.Junctions.Items.Values.First(j => j.Neighbors.Count > 0);
        var to = world.Junctions.Items[from.Neighbors[0]];
        a.CurrentJunction = from.Id;
        a.Position = from.WorldPosition;
        a.Tile = from.Tiles[0];
        b.CurrentJunction = to.Id;
        b.Position = to.WorldPosition;
        b.Tile = to.Tiles[0];

        var swim = world.Tiles.Items
            .First(p => SpatialQueries.IsSwimTile(p.Value)).Key;
        return (world, a, b, swim);
    }

    [Test]
    public void SwimPredicate_DeepWaterYes_WalkableShallowsNo()
    {
        var (world, _, _, swim) = Setup();
        Assert.That(SpatialQueries.IsSwimTile(world, swim), Is.True,
            "Глубокая вода (Water && !Walkable) обязана считаться плаванием.");

        Assert.That(
            SpatialQueries.IsSwimTile(new Tile
            {
                Flags = TileFlags.Water | TileFlags.Walkable
            }),
            Is.False,
            "Ходибельная мель — брод, не плавание: §40.18-B.");
        Assert.That(
            SpatialQueries.IsSwimTile(new Tile { Flags = TileFlags.Walkable }),
            Is.False, "Сухая земля — не плавание.");
    }

    [Test]
    public void CanStrike_BlockedForSwimmerOnEitherSide_KnobRestores()
    {
        var (world, a, b, swim) = Setup();
        Assert.That(InteractionReach.CanStrike(world, a, b), Is.True,
            "На суше смежная пара обязана доставать друг до друга.");

        var landTile = b.Tile;
        b.Tile = swim;
        Assert.That(InteractionReach.CanStrike(world, a, b), Is.False,
            "По пловчихе с суши не бьют (§106).");
        Assert.That(InteractionReach.CanStrike(world, b, a), Is.False,
            "Пловчиха не бьёт сама (§106) — симметрия обязательна.");

        var was = Spec106.WaterSanctuaryEnabled;
        try
        {
            Spec106.WaterSanctuaryEnabled = false;
            Assert.That(InteractionReach.CanStrike(world, a, b), Is.True,
                "Выключенная ручка обязана возвращать до-§106 поведение.");
        }
        finally
        {
            Spec106.WaterSanctuaryEnabled = was;
        }

        b.Tile = landTile;
        Assert.That(InteractionReach.CanStrike(world, a, b), Is.True,
            "Вылезла на берег — снова уязвима.");
    }

    /// <summary>Замах, начатый до нырка, уходит в воздух: HumanCombatSystem
    /// не доносит урон до нырнувшей (ядро §104 при этом не трогали).</summary>
    [Test]
    public void HumanCombat_LandedSwingWhiffsWhenVictimDove()
    {
        var (world, a, b, swim) = Setup();
        a.IsFighting = true;
        a.Mind.CombatOpponentNpcId = b.Id;

        b.Tile = swim; // нырнула до первого удара
        var healthBefore = b.Health;
        var system = new HumanCombatSystem();
        for (var i = 0; i < 60; i++)
        {
            world.Tick++;
            system.Run(world);
        }

        Assert.That(b.Health, Is.EqualTo(healthBefore),
            "Ни один удар не должен дойти до пловчихи — замахи обязаны уходить " +
            "в воздух (§106: inReach=false гасит урон).");
    }

    [Test]
    public void PreyVictim_SwimmerIsNotAVictim()
    {
        var (world, a, b, swim) = Setup();

        // Оставляем ровно пару хищник+жертва: остальные соседки не должны
        // подменить жертву в выборе.
        foreach (var extra in world.Entities.Npcs.Values
                     .Where(n => n.Id != a.Id && n.Id != b.Id).ToList())
        {
            world.Entities.Npcs.Remove(extra.Id);
        }

        Assert.That(DecisionSystem.NearestPreyVictim(a, world), Is.Not.Null,
            "На суше единственная соседка — валидная жертва §56.");

        b.Tile = swim;
        Assert.That(DecisionSystem.NearestPreyVictim(a, world), Is.Null,
            "Пловчиха не жертва (§106): без фильтра хищница полезла бы в воду — " +
            "вода ДОСТИЖИМА для Connectivity.Reachable.");
    }

    [Test]
    public void Raid_CommittedVictimDives_RaidAbandonedWithSwimmingReason()
    {
        var (world, raider, victim, swim) = Setup();
        var wasEnabled = Spec72.Enabled;
        try
        {
            Spec72.Enabled = true;
            raider.Faction = Faction.Outsiders;
            raider.Mind.CurrentGoal = GoalType.Raid;
            raider.Mind.RaidTargetNpcId = victim.Id;
            raider.Mind.RaidStartedTick = world.Tick;
            raider.Mind.CombatOpponentNpcId = victim.Id;
            raider.IsFighting = true;

            victim.Tile = swim;
            new RaidSystem().Run(world);

            Assert.That(raider.Mind.RaidTargetNpcId, Is.Null,
                "Нырок жертвы обязан снять коммит охоты.");
            Assert.That(raider.Mind.CombatOpponentNpcId, Is.Null,
                "Сцепка обязана расцепиться.");
            Assert.That(world.Events.Items.Any(e =>
                    e.Type == "RaidAbandoned" && e.Message.Contains("Reason=Swimming")),
                Is.True,
                "Молчаливый Unpair оставил бы план жив — и планировщик повёл бы " +
                "налётчика в море (§106: нужен явный AbandonRaid).");
        }
        finally
        {
            Spec72.Enabled = wasEnabled;
        }
    }

    [Test]
    public void RaidAndAbuse_TargetSelection_SkipsSwimmers()
    {
        var (world, raider, mark, swim) = Setup();
        var wasEnabled = Spec72.Enabled;
        try
        {
            Spec72.Enabled = true;
            raider.Faction = Faction.Outsiders;

            mark.Tile = swim;
            Assert.That(RaidMath.BestVictim(world, raider, out _), Is.Null.Or.Property("Id").Not.EqualTo(mark.Id),
                "BestVictim не выбирает пловчиху (§106).");
            Assert.That(AbuseMath.BestMark(world, raider, out _), Is.Null.Or.Property("Id").Not.EqualTo(mark.Id),
                "BestMark не выбирает пловчиху (§106).");
        }
        finally
        {
            Spec72.Enabled = wasEnabled;
        }
    }

    /// <summary>Волк отпускает нырнувшую СРАЗУ (medium-тик), а не после
    /// stall-таймера — и не может укусить через кромку даже до сброса.</summary>
    [Test]
    public void Wolf_DropsSwimmingTargetImmediately()
    {
        var (world, girl, _, swim) = Setup();
        world.NextMobSpawnCheckTick = world.Tick + 1_000_000;

        var junction = world.Junctions.Items[girl.CurrentJunction!.Value];
        // Нырнувшая — в воде и джанкшеном тоже: реальный пловец стоит на
        // swim-узле, а не «телом в море, ногами в хижине» (первый узел из
        // Setup может оказаться indoor — и sanctuary перебил бы воду).
        var swimJunction = world.SwimJunctions
            .Select(id => world.Junctions.Items[id])
            .First(j => j.Tiles.Any(t => SpatialQueries.IsSwimTile(world, t)));
        girl.CurrentJunction = swimJunction.Id;
        girl.Position = swimJunction.WorldPosition;
        var dog = new HexLive.Simulation.Wildlife.MobState
        {
            Id = 777,
            MobId = MobIds.Dog,
            Junction = junction.Id,
            Tile = junction.Tiles[0],
            Position = junction.WorldPosition,
            TargetNpc = girl.Id,
            Status = HexLive.Simulation.Wildlife.MobStatus.Chasing,
        };
        world.Mobs.Add(dog);

        girl.Tile = swimJunction.Tiles.First(t => SpatialQueries.IsSwimTile(world, t));
        new MobSystem().Run(world);

        Assert.That(dog.TargetNpc, Is.Null,
            "Цель в воде — волк обязан отпустить её на этом же medium-тике, " +
            "а не стоять статуей до DogChaseStallGiveUpTicks.");
        var mobEvents = string.Join(" | ", world.Events.Items
            .Where(e => e.Type.StartsWith("Dog"))
            .Select(e => $"{e.Type}: {e.Message}"));
        Assert.That(world.Events.Items.Any(e =>
                e.Type == "DogLostTarget" && e.Message.Contains("(in the water)")),
            Is.True,
            $"Причина потери цели обязана быть видна в трассе. Dog-события: [{mobEvents}]");
    }

    /// <summary>Среда атаки — данные, и гейт один для сухопутных, водных и
    /// будущих амфибийных противников.</summary>
    [Test]
    public void AttackMediums_LandAndWaterGateHonoursThem()
    {
        var (world, girl, _, swim) = Setup();

        Assert.That(MobCatalog.For(MobIds.Dog).AttackMediums,
            Is.EqualTo(AttackMedium.Land), "Волк — сухопутный.");
        Assert.That(CombatMedium.CanEngage(world, AttackMedium.Water, girl),
            Is.False, "Водный противник не достаёт стоящую на суше.");
        Assert.That(CombatMedium.CanEngage(world, AttackMedium.Land, girl),
            Is.True, "Волк достаёт стоящую на суше.");

        girl.Tile = swim;
        Assert.That(CombatMedium.CanEngage(world, AttackMedium.Water, girl),
            Is.True, "Водный противник достаёт пловчиху.");
        Assert.That(CombatMedium.CanEngage(world, AttackMedium.Land, girl),
            Is.False, "Волк пловчиху не достаёт.");
        Assert.That(CombatMedium.CanEngage(world, AttackMedium.Amphibious, girl),
            Is.True, "Амфибия достаёт везде — один флаг, не новая ось.");
    }
}

}
