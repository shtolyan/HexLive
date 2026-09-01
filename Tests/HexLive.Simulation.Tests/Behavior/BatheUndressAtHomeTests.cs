using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
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

    private static WorldState RoundTrip(WorldState world)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        return loaded;
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
        Assert.Multiple(() =>
        {
            Assert.That(spot.Value.Stand, Is.Not.EqualTo(Wardrobe(world).Junctions[0]),
                "Нельзя вставать на заблокированный anchor внутри шкафа.");
            Assert.That(world.Junctions.Items[spot.Value.Stand].Blocked, Is.False,
                "Interaction-stand обязан лежать вне authored footprint.");
            Assert.That(Connectivity.Reachable(world, npc.CurrentJunction!.Value,
                spot.Value.Stand, PlanningSystem.CanUseRoutineTraversal(npc)), Is.True,
                "Доступность шкафа проверяется по реальной точке взаимодействия.");
        });
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
        var hut = TestWorld.StartHut(world);
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

    [Test, Ignore("Superseded: bathing and laundry are separate commands in §40.6.")]
    public void DirtyWornLaundryUsesTheShoreNotTheHomeWardrobe()
    {
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressShore = null;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.None;
        npc.WornItems.Clear();
        npc.WornItems.Add(new ItemInstance("underwear.bra_riot")
        {
            Dirtiness = SimBalance.WashClothesNeedThreshold,
            OwnerId = npc.Id.Value
        });
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Execution.Status = ExecutionStatus.None;

        new PlanningSystem().Run(world);

        var prepare = npc.Plan.Steps.Single(step => step.Type == PlanStepType.PrepareBathe);
        Assert.Multiple(() =>
        {
            Assert.That(prepare.TargetJunction, Is.EqualTo(npc.Plan.TargetJunctionId));
            Assert.That(prepare.TimeoutEndTick, Is.EqualTo(prepare.TargetJunction!.Value.Value),
                "Грязную одежду надо снимать у самой воды, а не в домашнем гардеробе.");
            Assert.That(npc.Mind.RedressShore, Is.EqualTo(prepare.TargetJunction));
            Assert.That(npc.Mind.PersonalCareBathShore, Is.EqualTo(prepare.TargetJunction));
            Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.LaundryBatch));
        });
    }

    [Test, Ignore("Superseded: bathing no longer washes each doffed garment.")]
    public void EachDirtyPieceIsWashedRightAfterItIsTakenOff()
    {
        // §40.6 r14 (#147 rework): «сняли первую шмотку, начали стирать её…
        // постирали, положили на песок, потом следующую». Прошлая версия
        // снимала весь ворох и стирала одним общим тактом — игрок вернул её на
        // доработку именно за это.
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        foreach (var candidate in world.Entities.Npcs.Values)
        {
            candidate.Plan.Status = PlanStatus.Completed;
        }

        var shore = HygieneMath.FindReachableBathShore(world, npc)!;
        npc.CurrentJunction = shore.Id;
        npc.Tile = shore.Tiles.First(tile =>
            world.Tiles.Items.TryGetValue(tile, out var state) &&
            !state.Flags.HasFlag(TileFlags.Water));
        npc.Position = shore.WorldPosition;
        npc.Needs.Hygiene = 1f; // сама чистая — купаться незачем
        npc.WornItems.Clear();
        var clean = new ItemInstance("underwear.bra_riot")
        {
            Dirtiness = 0f,
            Wetness = 0.17f,
            OwnerId = npc.Id.Value
        };
        npc.WornItems.Add(clean);
        npc.WornItems.Add(new ItemInstance("underwear.thong_anarchy")
        {
            Dirtiness = 0.8f,
            OwnerId = npc.Id.Value
        });
        npc.WornItems.Add(new ItemInstance("clothing.jacket_autumn")
        {
            Bloodiness = 0.7f,
            OwnerId = npc.Id.Value
        });
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.LaundryBatch;
        npc.Mind.PersonalCareBathShore = shore.Id;
        npc.Mind.RedressShore = shore.Id;
        npc.Mind.RedressGarments.Clear();
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = shore.Id;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = shore.Id,
            TimeoutEndTick = shore.Id.Value
        });
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        var execution = new ExecutionSystem();
        for (var piece = 0; piece < 2; piece++)
        {
            // Такт снятия.
            execution.Run(world);
            Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Undress),
                $"вещь {piece + 1}: сначала снимает");
            if (piece == 0)
            {
                world.Tick = npc.Execution.StartTick +
                    (npc.Execution.EndTick - npc.Execution.StartTick) / 2;
                execution.Run(world);
                Assert.Multiple(() =>
                {
                    Assert.That(npc.WornItems, Has.Count.EqualTo(3),
                        "До завершения doff вещь должна оставаться в сериализуемом worn-владении.");
                    Assert.That(npc.Execution.HeldGarment, Is.Null,
                        "Personal-care не должен зависеть от несохраняемого hand-slot.");
                });
            }

            // Снятие завершилось — и ТУТ ЖЕ началась стирка этой самой вещи.
            world.Tick = npc.Execution.EndTick;
            execution.Run(world);
            Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.WashClothes),
                $"вещь {piece + 1}: сняла — стирает её же, а не копит ворох");
            Assert.That(npc.Mind.RedressGarments, Has.Count.EqualTo(piece + 1),
                $"вещь {piece + 1}: уже лежит на песке");
            var washing = world.Entities.Objects[npc.Execution.TargetObject!.Value];
            Assert.That(npc.Execution.TargetObject, Is.EqualTo(npc.Mind.RedressGarments[piece]),
                "стирается ровно та вещь, которую только что сняли");

            world.Tick = npc.Execution.EndTick;
            execution.Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(washing.Dirtiness, Is.EqualTo(0f));
                Assert.That(washing.Bloodiness, Is.EqualTo(0f));
                Assert.That(washing.Wetness, Is.EqualTo(1f));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems, Has.Count.EqualTo(1));
            Assert.That(npc.WornItems[0], Is.SameAs(clean),
                "Чистая вещь должна оставаться надетой во время стирки.");
            Assert.That(clean.Wetness, Is.EqualTo(0.17f),
                "Чистый остаток не должен намокать от чужой стирки.");
            Assert.That(npc.Execution.HeldGarment, Is.Null);
        });

        // Сама чистая — купания нет, она сразу идёт одеваться.
        execution.Run(world);
        Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.Redress),
            "грязна была только одежда — в воду лезть незачем");
        Assert.That(
            npc.Plan.Steps.Any(step => step.Type == PlanStepType.RedressAfterBathe),
            Is.True, "постиранное надевается сразу");
    }

    [Test, Ignore("Superseded: laundry no longer transitions into body bathing.")]
    public void ADirtyBodyStillGoesIntoTheWaterAfterTheLaundry()
    {
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        foreach (var candidate in world.Entities.Npcs.Values)
        {
            candidate.Plan.Status = PlanStatus.Completed;
        }

        var shore = HygieneMath.FindReachableBathShore(world, npc)!;
        npc.CurrentJunction = shore.Id;
        npc.Tile = shore.Tiles.First(tile =>
            world.Tiles.Items.TryGetValue(tile, out var state) &&
            !state.Flags.HasFlag(TileFlags.Water));
        npc.Position = shore.WorldPosition;
        npc.Needs.Hygiene = 0f; // грязна сама
        npc.WornItems.Clear();
        npc.WornItems.Add(new ItemInstance("underwear.thong_anarchy")
        {
            Dirtiness = 0.8f,
            OwnerId = npc.Id.Value
        });
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.LaundryBatch;
        npc.Mind.PersonalCareBathShore = shore.Id;
        npc.Mind.RedressShore = shore.Id;
        npc.Mind.RedressGarments.Clear();
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = shore.Id;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = shore.Id,
            TimeoutEndTick = shore.Id.Value
        });
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        var execution = new ExecutionSystem();
        execution.Run(world);              // снимает
        world.Tick = npc.Execution.EndTick;
        execution.Run(world);              // стирает
        world.Tick = npc.Execution.EndTick;
        execution.Run(world);              // достирала
        execution.Run(world);              // развилка

        Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.Bathing),
            "грязная сама — после стирки идёт купаться");
    }

    [Test]
    public void PersonalCarePhaseAndBothShoreAnchorsSurviveSaveLoad()
    {
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony && candidate.CurrentJunction is not null);
        var remembered = world.Entities.Objects.Values.First();
        var bathShore = npc.CurrentJunction!.Value;
        var pileShore = world.Junctions.Items.Values.First(junction => junction.Id != bathShore).Id;
        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressGarments.Add(remembered.Id);
        npc.Mind.RedressShore = pileShore;
        npc.Mind.PersonalCareBathShore = bathShore;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.LaundryBatch;

        var loaded = RoundTrip(world);
        var reloaded = loaded.Entities.Npcs[npc.Id];

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Mind.RedressGarments, Is.EqualTo(new[] { remembered.Id }));
            Assert.That(reloaded.Mind.RedressShore, Is.EqualTo(pileShore));
            Assert.That(reloaded.Mind.PersonalCareBathShore, Is.EqualTo(bathShore));
            Assert.That(reloaded.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.LaundryBatch));
            // Пин версии блоба: он поднимается сознательно и только вместе с
            // читателем старого формата. 48 → 49 в §35.4 r2 (флаг крыши, #167);
            // 53 → 54 в §148 (разведанные гексы; старый сейв читается, туман
            // просто открывается заново); 54 → 55 в §133.9 (#193,
            // сохраняемый запрет смены одежды); 55 → 56 в §127 (парная
            // сцена); 56 → 57 в §133.10 (выбранный комплект и точный объект
            // снятой вещи).
            // 59 → 60 in §120.10: queued architecture demolition and
            // the pending replacement definition of that exact slot; also §53.9
            // (#240, ordered player aid kind and target). 60 → 61 in #266:
            // exact catalog interaction id for same-type manual actions.
            // 61 → 62 in §129/#237: persisted building owner faction
            // (door camp ownership survives camp merge/removal).
            // 62 → 63 in §121.11/#294: persistent per-NPC manual run tempo.
            // 63 → 64 in §76.14/#304: девятый навык — Атлетика.
            // 64 → 65 in §55.4/#347: число глотков и provenance бутылки
            // теперь переживают сейв; v64 без количества мигрирует в пустую.
            Assert.That(WorldSaveSerializer.BlobVersion, Is.EqualTo(65));
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

    /// <summary>
    /// #173: план, ПЕРЕСОБРАННЫЙ после прерывания, приходит в PrepareBathe без
    /// станции в шаге (resume-ветка BuildBathePlan помнит только точку
    /// возврата). Раньше вещь честно падала на пол ПРЯМО У СТАНЦИИ; теперь доф
    /// разрешает станцию поздно — «стоит рядом» — и вешает на неё.
    /// </summary>
    [Test]
    public void ResumedBatheStillHangsTheGarmentOnTheStationSheStandsAt()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        var spot = StowMath.FindUndressSpot(world, npc);
        Assert.That(spot, Is.Not.Null, "Фикстура должна находить гардероб.");
        Assert.That(spot.Value.StowObject, Is.EqualTo(wardrobe.Id));

        var stand = world.Junctions.Items[spot.Value.Stand];
        npc.CurrentJunction = stand.Id;
        npc.Tile = stand.Tiles[0];
        npc.Position = stand.WorldPosition;
        npc.WornItems.Clear();
        var garment = new ItemInstance("underwear.bra_riot")
        {
            Dirtiness = 0f,
            OwnerId = npc.Id.Value
        };
        npc.WornItems.Add(garment);

        // Ровно то, что оставляет resume-ветка BuildBathePlan: фаза уже идёт,
        // шаг знает точку, но НЕ знает станцию (TargetObject нет).
        npc.Mind.PersonalCarePhase = PersonalCarePhase.Bathing;
        npc.Mind.RedressShore = stand.Id;
        npc.Mind.RedressGarments.Clear();
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = stand.Id;
        npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = stand.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Undress;
        npc.Execution.HeldGarment = null;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick; // доф уже дозрел
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        new ExecutionSystem().Run(world);

        Assert.That(npc.Mind.RedressGarments, Has.Count.EqualTo(1),
            "Снятая вещь должна попасть в список возврата.");
        var doffed = world.Entities.Objects[npc.Mind.RedressGarments[0]];
        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems, Is.Empty, "Вещь так и осталась надетой.");
            Assert.That(doffed.Junctions[0], Is.EqualTo(wardrobe.Junctions[0]),
                "Возобновлённый план снова уронил вещь на пол вместо станции рядом.");
            Assert.That(doffed.Owner, Is.EqualTo(npc.Id));
        });
    }

    /// <summary>
    /// #173 (контроль): стирка НЕ вешает — LaundryBatch кладёт снятое под
    /// стирку, даже если станция стоит рядом.
    /// </summary>
    [Test]
    public void ResumedLaundryStillLaysThePieceDownForWashing()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        var spot = StowMath.FindUndressSpot(world, npc);
        Assert.That(spot, Is.Not.Null);

        var stand = world.Junctions.Items[spot.Value.Stand];
        npc.CurrentJunction = stand.Id;
        npc.Tile = stand.Tiles[0];
        npc.Position = stand.WorldPosition;
        npc.WornItems.Clear();
        var garment = new ItemInstance("underwear.bra_riot")
        {
            Dirtiness = 0.9f,
            OwnerId = npc.Id.Value
        };
        npc.WornItems.Add(garment);

        npc.Mind.PersonalCarePhase = PersonalCarePhase.LaundryBatch;
        npc.Mind.RedressShore = stand.Id;
        npc.Mind.RedressGarments.Clear();
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = stand.Id;
        npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = stand.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Undress;
        npc.Execution.HeldGarment = null;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        new ExecutionSystem().Run(world);

        Assert.That(npc.Mind.RedressGarments, Has.Count.EqualTo(1));
        var doffed = world.Entities.Objects[npc.Mind.RedressGarments[0]];
        Assert.That(doffed.Junctions[0], Is.Not.EqualTo(wardrobe.Junctions[0]),
            "Бельё для стирки не должно уезжать на станцию — его сейчас будут стирать.");
    }

    [Test, Ignore("Superseded: outfit lock no longer merges laundry into bathing.")]
    public void LockedLaundryRedressesTheExactPieceWithoutBodyBath_Bug193()
    {
        var world = SettledWorld(1104);
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        var shore = HygieneMath.FindReachableBathShore(world, npc)!;
        npc.CurrentJunction = shore.Id;
        npc.Tile = shore.Tiles.First(tile =>
            world.Tiles.Items.TryGetValue(tile, out var state) &&
            !state.Flags.HasFlag(TileFlags.Water));
        npc.Position = shore.WorldPosition;
        npc.Needs.Hygiene = 0f;
        npc.WornItems.Clear();
        var garment = new ItemInstance("underwear.bra_riot")
        {
            Dirtiness = 0.9f,
            Durability = 0.73f,
            OwnerId = npc.Id.Value
        };
        npc.WornItems.Add(garment);
        npc.Mind.OutfitLocked = true;
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.LaundryBatch;
        npc.Mind.PersonalCareBathShore = shore.Id;
        npc.Mind.RedressShore = shore.Id;
        npc.Mind.RedressGarments.Clear();
        npc.Plan.Goal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = shore.Id;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = shore.Id,
            TimeoutEndTick = shore.Id.Value
        });
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        var execution = new ExecutionSystem();
        execution.Run(world); // start doff
        world.Tick = npc.Execution.EndTick;
        execution.Run(world); // exact piece becomes a world object; wash starts
        var washedObjectId = npc.Mind.RedressGarments.Single();
        world.Tick = npc.Execution.EndTick;
        execution.Run(world); // wash completes
        execution.Run(world); // lock forces laundry -> redress, never SwimBathe

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.Redress));
            Assert.That(npc.Plan.Steps.Any(step => step.Type == PlanStepType.SwimBathe), Is.False);
            Assert.That(npc.Plan.Steps.Any(step => step.Type == PlanStepType.RedressAfterBathe), Is.True);
            Assert.That(npc.Mind.RedressGarments.Single(), Is.EqualTo(washedObjectId));
        });

        npc.Plan.CurrentStepIndex = npc.Plan.Steps.FindIndex(
            step => step.Type == PlanStepType.RedressAfterBathe);
        execution.Run(world); // start redress beat
        world.Tick = npc.Execution.EndTick;
        execution.Run(world); // re-don exact remembered object

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(washedObjectId), Is.False);
            Assert.That(npc.WornItems, Has.Count.EqualTo(1));
            Assert.That(npc.WornItems[0].DefinitionId, Is.EqualTo(garment.DefinitionId));
            Assert.That(npc.WornItems[0].Durability, Is.EqualTo(0.73f).Within(0.0001f));
            Assert.That(npc.WornItems[0].Dirtiness, Is.Zero);
            Assert.That(npc.Mind.OutfitLocked, Is.True);
            Assert.That(npc.Needs.Hygiene, Is.Zero,
                "Закреплённый наряд разрешает стирку, но не скрытое купание.");
        });
    }
}

}
