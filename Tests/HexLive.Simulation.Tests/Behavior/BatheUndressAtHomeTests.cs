using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: раздеваться она идёт домой. Куча одежды больше не остаётся на берегу
/// на другом конце острова — вещи висят в гардеробе (или на сушилке), а сама
/// она возвращается туда же одеваться.
/// </summary>
public sealed class BatheUndressAtHomeTests
{
    private static WorldObjectState Wardrobe(WorldState world) =>
        world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Wardrobe);

    /// <summary>
    /// Мир после нескольких тиков: до первого шага у колонисток нет джанкшена,
    /// а без него планировать нечего (и StowMath честно отвечает «не знаю»).
    /// </summary>
    private static WorldState SettledWorld(int seed = 12345)
    {
        var engine = TestWorld.CreateEngine(seed);
        for (var i = 0; i < 4; i++)
        {
            engine.Step();
        }

        return engine.World;
    }

    /// <summary>⭐ Главное: гардероб в доме выигрывает у всего остального.</summary>
    [Test]
    public void UndressSpotPrefersTheWardrobeInTheHouse()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

        var spot = StowMath.FindUndressSpot(world, npc);

        Assert.That(spot, Is.Not.Null, "Место для раздевания не найдено, хотя дом есть.");
        Assert.That(spot.Value.StowObject, Is.EqualTo(Wardrobe(world).Id),
            "Раздеваться собираются не у гардероба — приоритет дома не работает.");
    }

    /// <summary>Гардероб полон — раздеваемся всё равно у дома, просто на землю.</summary>
    [Test]
    public void AFullWardrobeFallsBackToTheHomeGroundNotTheShore()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        for (var i = 0; i < Spec133.WardrobeCapacity; i++)
        {
            WorldObjectMutations.SpawnObject(
                world, "underwear.bra_riot", wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
        }

        var rack = world.Entities.Objects.Values
            .FirstOrDefault(o => o.DefinitionId == ContentIds.DryingRack);
        if (rack != null)
        {
            for (var i = 0; i < SimBalance.RackCapacity; i++)
            {
                WorldObjectMutations.SpawnObject(
                    world, "underwear.bra_riot", rack.Fragment, rack.Tile, rack.Junctions[0]);
            }
        }

        var spot = StowMath.FindUndressSpot(world, npc);

        Assert.That(spot, Is.Not.Null, "С полным гардеробом раздеваться расхотелось совсем.");
        Assert.That(spot.Value.StowObject, Is.Null,
            "В полный гардероб всё равно вешают.");
        Assert.That(ColonyQueries.Home(world, npc.Faction), Is.Not.Null);
        Assert.That(
            HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(
                world.Junctions.Items[spot.Value.Stand].Tiles[0],
                ColonyQueries.Home(world, npc.Faction).Value),
            Is.LessThanOrEqualTo(Spec133.HomeStowRadiusTiles),
            "Запасная точка раздевания оказалась не у дома.");
    }

    /// <summary>
    /// Снятая у гардероба вещь висит НА НЁМ и остаётся её собственной.
    /// </summary>
    [Test]
    public void DoffedGarmentHangsOnTheWardrobeAndKeepsItsOwner()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        var garment = new ItemInstance("underwear.bra_riot") { OwnerId = npc.Id.Value };

        var stowed = ExecutionSystem.StowGarmentWithContents(world, npc, garment, wardrobe.Id);

        Assert.That(stowed, Is.Not.Null);
        Assert.That(stowed.Junctions[0], Is.EqualTo(wardrobe.Junctions[0]),
            "Вещь легла не на гардероб.");
        Assert.That(stowed.Owner, Is.EqualTo(npc.Id), "Повешенная вещь потеряла хозяйку.");
        Assert.That(stowed.RotationDegrees, Is.EqualTo(wardrobe.RotationDegrees).Within(0.001f),
            "Вещь висит мимо поворота станции (§66).");
    }

    /// <summary>Полная станция не съедает вещь: она честно падает под ноги.</summary>
    [Test]
    public void AFullStationDropsTheGarmentAtHerFeetInstead()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        for (var i = 0; i < Spec133.WardrobeCapacity; i++)
        {
            WorldObjectMutations.SpawnObject(
                world, "underwear.bra_riot", wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
        }

        var garment = new ItemInstance("underwear.thong_anarchy") { OwnerId = npc.Id.Value };
        var stowed = ExecutionSystem.StowGarmentWithContents(world, npc, garment, wardrobe.Id);

        Assert.That(stowed, Is.Not.Null, "Вещь исчезла при переполненном гардеробе.");
        Assert.That(stowed.Junctions[0], Is.Not.EqualTo(wardrobe.Junctions[0]),
            "Вещь всё-таки повесили в полный гардероб.");
        Assert.That(stowed.Owner, Is.EqualTo(npc.Id));
    }

    [Test]
    public void PostBatheReturnKeepsThePlanThroughTransientPathRetries()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var remembered = world.Entities.Objects.Values.First();
        var missing = new JunctionId(int.MaxValue - 410);

        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Mind.RedressShore = missing;
        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressGarments.Add(remembered.Id);
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = missing;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.RedressAfterBathe,
            TargetJunction = missing
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Idle);

        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new PathfindingSystem());
        engine.Register(new ExecutionSystem());
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Bathe));
            Assert.That(npc.Movement.Status, Is.EqualTo(MovementStatus.Blocked));
            Assert.That(npc.Movement.BlockedWaitTicks, Is.EqualTo(1));
            Assert.That(npc.Mind.RedressShore, Is.EqualTo(missing));
            Assert.That(npc.Mind.RedressGarments, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void BathAvailabilityRejectsACoarselyConnectedButForbiddenDoorRoute()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = world.Entities.Objects.Values.Single(o =>
            o.DefinitionId == ContentIds.Hut1Hex);
        var door = world.Entities.Objects.Values.Single(o =>
            o.DefinitionId == DoorTopology.DoorDefinitionId);
        var inside = world.Tiles.Items[hut.Tile].Junctions
            .Select(id => world.Junctions.Items[id])
            .First(j => !j.Blocked && !j.Door && j.Tiles.Count == 1 &&
                        j.Tiles[0] == hut.Tile);
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var npc = world.Entities.Npcs.Values.First();
        npc.Faction = Faction.Outsiders;
        npc.CurrentJunction = inside.Id;
        npc.Tile = hut.Tile;
        npc.Position = inside.WorldPosition;

        var coarseShore = world.Junctions.Items.Values.FirstOrDefault(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            HygieneMath.IsShoreTile(world, j.Tiles[0]) &&
            HexLive.Simulation.Spatial.HexSpatialMath.Distance(
                j.WorldPosition, npc.Position) <
                HexLive.Simulation.Spatial.HexSpatialMath.HexRadius * 12f &&
            Connectivity.Reachable(world, inside.Id, j.Id, npc.Body.CanJump));

        Assert.Multiple(() =>
        {
            Assert.That(coarseShore, Is.Not.Null,
                "Фикстура должна иметь компонентно достижимый берег рядом с домом.");
            Assert.That(HygieneMath.FindReachableBathShore(world, npc), Is.Null,
                "Чужая закрытая дверь физически запирает NPC: грубая связность не должна обещать купание.");
        });
    }

    [Test]
    public void LostWaterRouteAfterArrivalPutsBatheOnCooldown()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var dry = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            HygieneMath.FindRoundTripBathWater(world, npc, j.Id) is null);

        npc.CurrentJunction = dry.Id;
        npc.Tile = dry.Tiles[0];
        npc.Position = dry.WorldPosition;
        npc.WornItems.Clear();
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = dry.Id;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = dry.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Mind.Cooldowns.Any(c =>
                c.Goal == GoalType.Bathe && c.EndTick > world.Tick), Is.True,
                "Изменившийся берег не должен переназначаться каждые четыре тика.");
        });
    }

    [Test]
    public void HomeUndressKeepsTheRealShoreAsTheNextDestination()
    {
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, n) is not null);
        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressShore = null;
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Execution.Status = ExecutionStatus.None;

        new PlanningSystem().Run(world);

        var prepare = npc.Plan.Steps.Single(step => step.Type == PlanStepType.PrepareBathe);
        Assert.That(npc.Plan.TargetJunctionId, Is.Not.Null,
            "План должен вести к месту раздевания.");
        Assert.That(prepare.TargetJunction, Is.EqualTo(npc.Plan.TargetJunctionId));
        Assert.That(prepare.TimeoutEndTick, Is.Not.Null,
            "PrepareBathe должен отдельно помнить настоящий берег.");
        Assert.That(new JunctionId(prepare.TimeoutEndTick!.Value), Is.Not.EqualTo(npc.Plan.TargetJunctionId),
            "Фикстура должна раздеваться дома, а не на берегу.");

        var home = world.Junctions.Items[prepare.TargetJunction!.Value];
        var shore = new JunctionId(prepare.TimeoutEndTick.Value);
        npc.CurrentJunction = home.Id;
        npc.Tile = home.Tiles[0];
        npc.Position = home.WorldPosition;
        npc.WornItems.Clear();
        npc.Plan.CurrentStepIndex = npc.Plan.Steps.IndexOf(prepare);
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Plan.Steps[0].Type, Is.EqualTo(PlanStepType.MoveToJunction));
            Assert.That(npc.Plan.Steps[0].TargetJunction, Is.EqualTo(shore),
                "После домашнего раздевания надо идти к сохранённому берегу.");
            Assert.That(npc.Mind.RedressShore, Is.EqualTo(home.Id),
                "Возвращаться за одеждой надо домой, а не к воде.");
        });
    }

    [Test]
    public void FinalBathStepCrossesFromTheShoreIntoTheSelectedWaterTile()
    {
        // Seed 1104 is the stable bathing fixture already used above: its
        // colony has a physically reachable shore and a reversible water dip.
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);

        // Keep the fixture about terrain, not actor yielding.
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id != npc.Id)
            {
                other.CurrentJunction = null;
            }
        }
        world.Mobs.Clear();

        var shore = HygieneMath.FindReachableBathShore(world, npc)!;
        var water = HygieneMath.FindRoundTripBathWater(world, npc, shore.Id)!;
        var route = HexPathfinder.FindPath(world, shore.Id, water.Id);
        var dryTile = shore.Tiles.First(coord =>
            world.Tiles.Items.TryGetValue(coord, out var tile) &&
            !tile.Flags.HasFlag(TileFlags.Water));

        Assert.Multiple(() =>
        {
            Assert.That(route, Has.Count.GreaterThan(1));
            Assert.That(world.Tiles.Items[water.Tiles[0]].Flags.HasFlag(TileFlags.Water),
                Is.True, "Фикстура должна выбрать настоящую водную сторону.");
            Assert.That(SpatialQueries.IsSwimTile(world, water.Tiles[0]), Is.True,
                "Фикстура должна проверять именно глубокую воду и прыжок, а не мелководье.");
        });

        npc.CurrentJunction = shore.Id;
        npc.Tile = dryTile;
        npc.Position = shore.WorldPosition;
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = water.Id;
        npc.Plan.TargetTile = water.Tiles[0];
        npc.Movement.JunctionPath.Clear();
        npc.Movement.JunctionPath.AddRange(route);
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
        npc.Movement.PostTurnTimer = 0f;
        npc.Movement.ClimbPauseTimer = 0f;
        npc.Movement.HopTimer = 0f;
        npc.Movement.HopArmed = false;
        npc.Movement.HopPathIndex = -1;

        var movement = new MovementSystem();
        var sawJump = false;
        var sawSwimEntry = false;
        for (var tick = 0; tick < 240 && npc.Movement.IsMoving; tick++)
        {
            movement.Run(world);
            sawJump |= npc.Movement.HopTimer > 0f;
            sawSwimEntry |= npc.Movement.ClimbPauseTimer > 0f;
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Movement.IsMoving, Is.False,
                "Короткий заход в воду должен завершиться за бюджет фикстуры.");
            Assert.That(npc.CurrentJunction, Is.EqualTo(water.Id));
            Assert.That(npc.Tile, Is.EqualTo(water.Tiles[0]),
                "Финальный береговой узел не должен оставлять купальщицу на сухой стороне.");
            Assert.That(world.Tiles.Items[npc.Tile].Flags.HasFlag(TileFlags.Water), Is.True);
            Assert.That(sawJump, Is.True, "Вход в глубокую воду должен проиграть штатный прыжок вниз.");
            Assert.That(sawSwimEntry, Is.True, "После прыжка должен начаться штатный tread-переход.");
        });
    }
}

}
