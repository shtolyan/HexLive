using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §62 r2 — регресс «бесстрашных NPC» (авг-2026) и его три починки.
/// <para>
/// История: аудит 20.07 выключил первый удар (<c>AttackMaxPack=0</c>), но
/// исключение «годна к бою — кольцо не платит» в <c>AvoidsThreatRings</c>
/// осталось со старым обоснованием «она всё равно атакует». После §139/§140
/// здоровая-и-с-ножом стала нормой, и колония месяц ходила сквозь волков без
/// единого <c>ThreatAvoid</c>. Параллельно §147 сделал дальних волков
/// виртуальными — и невидимыми для кольца опасности. Эти тесты закрепляют
/// все три стороны починки, чтобы бесстрашие не вернулось молча.
/// </para>
/// </summary>
public sealed class ThreatAvoidanceTests
{
    private int _savedAttackMaxPack;

    [SetUp]
    public void SaveKnobs() => _savedAttackMaxPack = Spec62.AttackMaxPack;

    // Урок ManualCraftingTests: TearDown обязан вернуть ровно прежнее
    // simdata-значение, а не код-дефолт — иначе флейк у соседних тестов.
    [TearDown]
    public void RestoreKnobs() => Spec62.AttackMaxPack = _savedAttackMaxPack;

    [Test]
    public void FitGirlPaysDangerRingWhileFirstStrikeIsDisabled()
    {
        var world = TestWorld.CreateWorld();
        var npc = MakeFitOutdoorGirl(world);

        Spec62.AttackMaxPack = 0;
        Assert.That(Spec62.FirstStrikeEnabled, Is.False);
        Assert.That(PathfindingSystem.AvoidsThreatRings(npc), Is.True,
            "Первый удар выключен — «она всё равно атакует» больше не правда, " +
            "и здоровая вооружённая девушка обязана платить кольцо опасности. " +
            "False здесь = регресс «бесстрашные NPC идут сквозь волков».");
    }

    [Test]
    public void FitExemptionAppliesOnlyWithFirstStrikeEnabled()
    {
        var world = TestWorld.CreateWorld();
        var npc = MakeFitOutdoorGirl(world);

        Spec62.AttackMaxPack = 1;
        Assert.That(PathfindingSystem.AvoidsThreatRings(npc), Is.False,
            "С включённым первым ударом годный боец задуманно ходит где хочет.");

        npc.Inventory.Items.RemoveAll(item =>
            GearCatalog.For(item.DefinitionId).MeleePriority > 0);
        Assert.That(ThreatAlertSystem.IsFitToFight(npc), Is.False,
            "Прекондиция: без оружия она не годна к бою.");
        Assert.That(PathfindingSystem.AvoidsThreatRings(npc), Is.True,
            "Безоружная платит кольцо и при включённом первом ударе.");
    }

    [Test]
    public void VirtualWolfSlotFeedsDangerRingAtItsPreviewWaypoint()
    {
        var world = TestWorld.CreateWorld();
        world.Mobs.Clear();

        var slot = MakeVirtualDogSlot(world);
        world.MobSpawnSlots.Add(slot);

        var ring = PathfindingSystem.DangerRing(world);
        MobPreview.PreviewPose(
            world.Seed, slot, world.Tick,
            WildlifeBalance.MobPreviewSegmentTicks,
            WildlifeBalance.MobPreviewPauseChance,
            out _, out _, out var nearestIndex);
        Assert.That(ring, Does.Contain(slot.Ring[nearestIndex].Junction),
            "§147-паритет: превью-позиция виртуального волка обязана гнуть " +
            "маршруты так же, как гнул бы живой волк на этом месте.");

        // Кулдаун-слот безволчий: кольцо пустеет (кэш на тик — двигаем тик).
        slot.State = MobSlotState.Cooldown;
        world.Tick += 1;
        Assert.That(PathfindingSystem.DangerRing(world), Is.Empty,
            "Слот в кулдауне — волка нет нигде, кольца быть не должно.");
    }

    [Test]
    public void ErrandWithDestinationInsideRingIsDeferredWithGoalCooldown()
    {
        var world = TestWorld.CreateWorld();
        var npc = MakeFitOutdoorGirl(world);
        var mobJunction = SpawnDogNear(world, npc, tilesAway: 2);

        npc.Mind.CurrentGoal = GoalType.WashClothes;
        npc.Plan.Goal = GoalType.WashClothes;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = mobJunction;

        new ThreatAlertSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active),
                "§62.7: поход, чей пункт назначения внутри кольца волка, " +
                "сносится — крюк всё равно привёл бы её в зубы.");
            Assert.That(
                PlanningSystem.IsGoalOnCooldown(npc, GoalType.WashClothes, world.Tick),
                Is.True,
                "Цель уходит в кулдаун, иначе аукцион вернёт её на тот же " +
                "маршрут следующим же проходом.");
        });
    }

    [Test]
    public void StarvingGirlBravesTheRingInsteadOfDeferring()
    {
        var world = TestWorld.CreateWorld();
        var npc = MakeFitOutdoorGirl(world);
        var mobJunction = SpawnDogNear(world, npc, tilesAway: 2);

        npc.Mind.IsStarving = true;
        npc.Mind.CurrentGoal = GoalType.Eat;
        npc.Plan.Goal = GoalType.Eat;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = mobJunction;

        new ThreatAlertSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active),
                "Голодная не откладывает: волк у единственной еды не должен " +
                "уморить её вежливостью (класс «empty-shell water trap»).");
            Assert.That(
                PlanningSystem.IsGoalOnCooldown(npc, GoalType.Eat, world.Tick),
                Is.False);
        });
    }

    // ── Сборка сцены ─────────────────────────────────────────────────────

    /// <summary>Первая колонистка, выведенная под открытое небо (стартовый
    /// дом — санктуарий, и ThreatAlertSystem её бы пропустил) и гарантированно
    /// годная к бою: полное здоровье мира + нож в паке.</summary>
    private static NPCState MakeFitOutdoorGirl(WorldState world)
    {
        var npc = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        var junction = OutdoorJunctions(world).First();
        npc.CurrentJunction = junction.Id;
        npc.Tile = junction.Tiles[0];
        npc.Position = junction.WorldPosition;

        if (SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.WeaponHands)
                is not { } weapon || GearCatalog.For(weapon).MeleePriority <= 0)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        }

        Assert.That(ThreatAlertSystem.IsFitToFight(npc), Is.True,
            "Прекондиция сцены: девушка обязана быть годной к бою, иначе " +
            "тест меряет не то исключение.");
        return npc;
    }

    /// <summary>Живой волк ровно в <paramref name="tilesAway"/> гексах от
    /// девушки — внутри пола восприятия (3) и на развязке, чьё кольцо
    /// накрывает его самого.</summary>
    private static JunctionId SpawnDogNear(WorldState world, NPCState npc, int tilesAway)
    {
        world.Mobs.Clear();
        var mobJunction = OutdoorJunctions(world).First(j =>
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) == tilesAway);
        world.Mobs.Add(new MobState
        {
            Id = int.MaxValue - 7,
            MobId = MobIds.Dog,
            Junction = mobJunction.Id,
            Tile = mobJunction.Tiles[0],
            Position = mobJunction.WorldPosition,
            Health = 1f,
        });
        return mobJunction.Id;
    }

    /// <summary>Виртуальный волчий слот с патрульным кольцом из двух соседних
    /// проходимых развязок — минимум, который принимает MobPreview.</summary>
    private static MobSpawnSlot MakeVirtualDogSlot(WorldState world)
    {
        foreach (var home in OutdoorJunctions(world))
        {
            var neighbor = home.Neighbors
                .Select(id => world.Junctions.Items.TryGetValue(id, out var j) ? j : null)
                .FirstOrDefault(j => j is { Blocked: false, Tiles.Count: > 0 });
            if (neighbor is null)
            {
                continue;
            }

            var slot = new MobSpawnSlot
            {
                SlotId = 1,
                MobId = MobIds.Dog,
                State = MobSlotState.Virtual,
                ReservedMobId = world.NextMobId++,
                HomeJunction = home.Id,
                StoredHealth = MobCatalog.For(MobIds.Dog).MaxHealth,
                CycleIndex = 0,
            };
            slot.Ring.Add(new PatrolWaypoint
            {
                Junction = home.Id,
                Tile = home.Tiles[0],
                Position = home.WorldPosition,
            });
            slot.Ring.Add(new PatrolWaypoint
            {
                Junction = neighbor.Id,
                Tile = neighbor.Tiles[0],
                Position = neighbor.WorldPosition,
            });
            return slot;
        }

        throw new System.InvalidOperationException(
            "Прототипный остров не дал пары соседних развязок под слот.");
    }

    private static System.Collections.Generic.IEnumerable<Junction>
        OutdoorJunctions(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values
                     .OrderBy(j => j.Id.Value))
        {
            if (junction.Blocked || junction.Tiles.Count == 0)
            {
                continue;
            }

            if (world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) &&
                (tile.Flags & TileFlags.Indoor) == 0 &&
                (tile.Flags & TileFlags.Water) == 0)
            {
                yield return junction;
            }
        }
    }
}

}
