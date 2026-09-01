using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
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
/// §129: дверь открывают руки. Закрытая дверь — поведение и права, не
/// топология: своя открывает створку и проходит (и закрывает за собой),
/// чужой фракции закрытый портал запрещён на уровне маршрута, а «дотянуться
/// руками» сквозь закрытую створку нельзя никому.
/// </summary>
public sealed class DoorPassageTests
{
    private static WorldObjectState Door(WorldState world) =>
        world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == DoorTopology.DoorDefinitionId);

    private static WorldObjectState Hut(WorldState world) =>
        TestWorld.StartHut(world);

    private static JunctionId InteriorJunction(WorldState world)
    {
        var hut = Hut(world);
        var tile = world.Tiles.Items[hut.Tile];
        foreach (var junctionId in tile.Junctions)
        {
            var junction = world.Junctions.Items[junctionId];
            if (!junction.Blocked && !junction.Door && junction.Tiles.Count == 1 &&
                junction.Tiles[0] == hut.Tile)
            {
                return junctionId;
            }
        }

        Assert.Fail("В хижине не нашлось свободного внутреннего узла.");
        return default;
    }

    private static JunctionId OutsideNeighborOfPortal(WorldState world, JunctionId portalId)
    {
        var hut = Hut(world);
        var portal = world.Junctions.Items[portalId];
        foreach (var neighborId in portal.Neighbors)
        {
            var neighbor = world.Junctions.Items[neighborId];
            if (!neighbor.Blocked && !neighbor.Tiles.Contains(hut.Tile))
            {
                return neighborId;
            }
        }

        Assert.Fail("У портала нет свободного наружного соседа.");
        return default;
    }

    private static JunctionId InsideNeighborOfPortal(WorldState world, JunctionId portalId)
    {
        var hut = Hut(world);
        var portal = world.Junctions.Items[portalId];
        foreach (var neighborId in portal.Neighbors)
        {
            var neighbor = world.Junctions.Items[neighborId];
            if (!neighbor.Blocked && neighbor.Tiles.Count == 1 &&
                neighbor.Tiles[0] == hut.Tile)
            {
                return neighborId;
            }
        }

        Assert.Fail("У портала нет свободного внутреннего соседа.");
        return default;
    }

    // Ставит NPC на узел и вручную заряжает шаг «через портал» — ровно то
    // состояние, в котором MovementSystem встречает дверь. Без движка, без
    // соседних систем: гейт судится в изоляции.
    private static void ArmStepThroughPortal(
        WorldState world, NPCState npc, JunctionId start, JunctionId portal, JunctionId beyond)
    {
        var startJunction = world.Junctions.Items[start];
        npc.Position = startJunction.WorldPosition;
        npc.CurrentJunction = start;
        npc.Tile = startJunction.Tiles[0];
        npc.Movement.JunctionPath.Clear();
        npc.Movement.JunctionPath.Add(start);
        npc.Movement.JunctionPath.Add(portal);
        npc.Movement.JunctionPath.Add(beyond);
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
    }

    private static List<(string Type, int Npc, string Message)> DrainSince(
        WorldState world, ref long watermark)
    {
        var fresh = new List<(string, int, string)>();
        foreach (var e in world.Events.Items)
        {
            if (e.Seq <= watermark) continue;
            watermark = e.Seq;
            fresh.Add((e.Type, e.EntityId ?? -1, e.Message));
        }

        return fresh;
    }

    [Test]
    public void RouterBansClosedDoorForOutsidersOnly()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var portalId = door.Junctions[0];
        var inside = InteriorJunction(world);
        var outside = OutsideNeighborOfPortal(world, portalId);

        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        Assert.Multiple(() =>
        {
            // Свои: запретов нет вовсе (fast path — null), путь строится
            // СКВОЗЬ закрытую дверь, и связность интерьер не теряет.
            Assert.That(DoorTopology.ForbiddenFor(world, Faction.Colony), Is.Null);
            Assert.That(HexPathfinder.FindPath(world, outside, inside, null), Is.Not.Empty);
            Assert.That(Connectivity.Reachable(world, outside, inside), Is.True,
                "Закрытая дверь не имеет права отрезать интерьер от общего графа (§129.1).");

            // Чужие: закрытый чужой портал — жёсткий запрет, пути внутрь нет.
            var forbidden = DoorTopology.ForbiddenFor(world, Faction.Outsiders);
            Assert.That(forbidden, Is.Not.Null);
            Assert.That(forbidden, Does.Contain(portalId));
            Assert.That(HexPathfinder.FindPath(
                world, outside, inside, null, hardAvoid: forbidden), Is.Empty);
        });

        // Открытая дверь — обычный проём для всех.
        Assert.That(BuildingDoorRules.TryOpen(world, door.Id), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(DoorTopology.ForbiddenFor(world, Faction.Outsiders), Is.Null);
            Assert.That(HexPathfinder.FindPath(world, outside, inside, null), Is.Not.Empty);
        });
    }

    [Test]
    public void ClosedDoorIsAReachBarrierForEveryoneOpenIsNot()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var portalId = door.Junctions[0];

        Assert.That(SpatialQueries.IsBarrierFor(
            world, portalId, null, SpatialQueries.RimPurpose.Reach), Is.False,
            "Открытый портал руки не останавливает.");

        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(SpatialQueries.IsBarrierFor(
                world, portalId, null, SpatialQueries.RimPurpose.Reach), Is.True,
                "Сквозь закрытую створку нельзя дотянуться никому (§129.1).");
            Assert.That(SpatialQueries.IsBarrierFor(
                world, portalId, null, SpatialQueries.RimPurpose.Route), Is.False,
                "Для вопроса «есть ли где встать вообще» закрытая дверь — не стена.");
        });
    }

    [Test]
    public void MovementGateOpensDoorForAllyAndHoldsTheSwing()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var portalId = door.Junctions[0];
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        ArmStepThroughPortal(world, npc,
            OutsideNeighborOfPortal(world, portalId), portalId,
            InsideNeighborOfPortal(world, portalId));

        var watermark = world.Events.Items.Count > 0
            ? world.Events.Items[^1].Seq : 0L;
        new MovementSystem().Run(world);

        var fresh = DrainSince(world, ref watermark);
        Assert.Multiple(() =>
        {
            Assert.That(door.IsDoorOpen, Is.True, "Своя обязана открыть дверь.");
            Assert.That(npc.Movement.ClimbPauseTimer,
                Is.GreaterThanOrEqualTo(Spec129.DoorSwingSeconds - 0.001f),
                "Распах створки пережидается паузой (§129.3).");
            Assert.That(npc.Movement.JunctionPath, Is.Not.Empty,
                "Путь через дверь остаётся живым — она пройдёт после паузы.");
            Assert.That(fresh.Any(e => e.Type == "DoorOpened" && e.Npc == npc.Id.Value),
                Is.True);
        });
    }

    [Test]
    public void MovementGateRefusesOutsiderWithStalePathThroughClosedDoor()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var portalId = door.Junctions[0];
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var npc = world.Entities.Npcs.Values.First();
        npc.Faction = Faction.Outsiders;
        ArmStepThroughPortal(world, npc,
            OutsideNeighborOfPortal(world, portalId), portalId,
            InsideNeighborOfPortal(world, portalId));

        var watermark = world.Events.Items.Count > 0
            ? world.Events.Items[^1].Seq : 0L;
        new MovementSystem().Run(world);

        var fresh = DrainSince(world, ref watermark);
        Assert.Multiple(() =>
        {
            Assert.That(door.IsDoorOpen, Is.False, "Чужак дверь не открывает.");
            Assert.That(npc.Movement.JunctionPath, Is.Empty,
                "Протухший путь сквозь закрытую дверь сбрасывается (§129.3).");
            Assert.That(npc.Movement.IsMoving, Is.False);
            Assert.That(fresh.Any(e => e.Type == "DoorRefused" && e.Npc == npc.Id.Value),
                Is.True);
        });
    }

    [Test]
    public void AllyWalksInThroughClosedDoorAndClosesItBehind()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var door = Door(world);
        var inside = InteriorJunction(world);

        // Дать миру осесть (CurrentJunction появляется после первых тиков).
        for (var i = 0; i < 8; i++) engine.Step();

        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && n.CurrentJunction.HasValue &&
            n.Tile != Hut(world).Tile);
        npc.Mind.ManualControl = true;
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetJunctionId = inside;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = inside
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        var watermark = world.Events.Items.Count > 0
            ? world.Events.Items[^1].Seq : 0L;
        var opened = false;
        var closedBehind = false;
        var arrived = false;
        for (var tick = 0; tick < 3000 && !(arrived && closedBehind); tick++)
        {
            engine.Step();
            foreach (var e in DrainSince(world, ref watermark))
            {
                if (e.Npc != npc.Id.Value) continue;
                if (e.Type == "DoorOpened") opened = true;
                if (e.Type == "DoorClosed") closedBehind = true;
            }

            if (npc.CurrentJunction is { } current && current.Equals(inside))
            {
                arrived = true;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(arrived, Is.True, "Своя не дошла до узла внутри хижины.");
            Assert.That(opened, Is.True, "По дороге она обязана была открыть дверь.");
            Assert.That(closedBehind, Is.True, "И закрыть её за собой (§129.3).");
            Assert.That(door.IsDoorOpen, Is.False,
                "После прохода дверь снова закрыта.");
        });
    }

    [Test]
    public void AllyWalksOutThroughClosedDoorAndClosesItBehind()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        var door = Door(world);
        var hut = Hut(world);
        var inside = InteriorJunction(world);

        for (var i = 0; i < 8; i++) engine.Step();

        // Поселить её внутрь руками и закрыть дверь.
        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && n.CurrentJunction.HasValue);
        var insideJunction = world.Junctions.Items[inside];
        var fromTile = npc.Tile;
        npc.Position = insideJunction.WorldPosition;
        npc.CurrentJunction = inside;
        npc.Tile = hut.Tile;
        SpatialMutations.MoveEntityToTile(world, npc.Id, fromTile, hut.Tile);
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var home = world.FactionHomes[Faction.Colony];
        var outside = StructurePlacement.CenterJunction(world, home);
        Assert.That(outside, Is.Not.Null);

        npc.Mind.ManualControl = true;
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetJunctionId = outside.Value;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = outside.Value
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        var watermark = world.Events.Items.Count > 0
            ? world.Events.Items[^1].Seq : 0L;
        var opened = false;
        var closedBehind = false;
        var arrived = false;
        for (var tick = 0; tick < 3000 && !(arrived && closedBehind); tick++)
        {
            engine.Step();
            foreach (var e in DrainSince(world, ref watermark))
            {
                if (e.Npc != npc.Id.Value) continue;
                if (e.Type == "DoorOpened") opened = true;
                if (e.Type == "DoorClosed") closedBehind = true;
            }

            if (npc.CurrentJunction is { } current && current.Equals(outside.Value))
            {
                arrived = true;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(arrived, Is.True, "Своя не вышла из хижины к лагерю.");
            Assert.That(opened, Is.True, "Изнутри дверь открывается так же.");
            Assert.That(closedBehind, Is.True, "И закрывается за спиной.");
            Assert.That(door.IsDoorOpen, Is.False);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Баг #237: «двери запирают жителей внутри».
    //
    // §146 дал дом с дверью КАЖДОМУ девичьему лагерю, а владельцем двери в коде
    // осталась константа Faction.Colony. Замер по живому серверному сейву
    // (HugeIsland, seed 149187134, тик 144476): шесть хижин, у пяти жительницы —
    // Colony2…Colony6, и ни одна не имела права открыть собственную дверь.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>#237: владелец двери — лагерь, который в этом доме живёт.</summary>
    [Test]
    public void DoorBelongsToTheCampThatActuallyLivesThere()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var portalId = door.Junctions[0];
        var hut = Hut(world);

        // Дом второго девичьего лагеря — ровно случай §146.5: StakeCampHutPlans
        // штампует площадку своим лагерем, подъём уносит штамп на здание.
        DoorTopology.StampOwner(world, hut, Faction.Colony2);

        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var inside = InteriorJunction(world);
        var outside = OutsideNeighborOfPortal(world, portalId);
        var forbidden = DoorTopology.ForbiddenFor(world, Faction.Colony2);

        Assert.Multiple(() =>
        {
            Assert.That(DoorTopology.OwnerFaction(world, door), Is.EqualTo(Faction.Colony2),
                "Дверь принадлежит своему лагерю, а не константе Colony.");
            Assert.That(FactionRelations.AreAllies(
                Faction.Colony2, DoorTopology.OwnerFaction(world, door)), Is.True,
                "Жительница обязана иметь право открыть свою же дверь.");
            Assert.That(forbidden is null || !forbidden.Contains(portalId), Is.True,
                "Роутер не смеет запрещать жительнице её собственный портал.");
            Assert.That(HexPathfinder.FindPath(
                    world, inside, outside, null, hardAvoid: forbidden), Is.Not.Empty,
                "Изнутри собственного дома обязан существовать маршрут наружу (#237).");

            // Аутсайдер дверей не строит и владельцем не становится никогда —
            // ни очагом по соседству, ни штампом.
            world.FactionHomes[Faction.Outsiders] = hut.Tile;
            DoorTopology.StampOwner(world, hut, Faction.Outsiders);
            world.DoorStateVersion++;
            Assert.That(DoorTopology.OwnerFaction(world, door), Is.EqualTo(Faction.Colony2));
        });
    }

    /// <summary>
    /// #237 r2 (блокер ревью): владелец непроштампованного дома по-прежнему
    /// выводится по ближайшему девичьему очагу — этим путём продолжают жить
    /// сейвы ≤59 и миры без лагерной разметки.
    /// </summary>
    [Test]
    public void UnstampedBuildingStillFallsBackToTheNearestCamp()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var hut = Hut(world);

        hut.OwnerFaction = null; // как у здания из старого блоба
        world.FactionHomes[Faction.Colony2] = hut.Tile;
        world.DoorStateVersion++;

        Assert.That(DoorTopology.OwnerFaction(world, door), Is.EqualTo(Faction.Colony2),
            "Без штампа действует прежнее правило «чей очаг ближе».");
    }

    /// <summary>
    /// ⭐ #237 r2: дом остаётся домом СВОЕГО лагеря, когда очаг этого лагеря
    /// исчезает — слиянием (§146.13) или гибелью. Вывод «чей очаг сейчас
    /// ближе» отдавал такой дом ТРЕТЬЕМУ, постороннему лагерю, и запирал
    /// жительниц ровно так же, как исходный #237, только позже по времени.
    /// </summary>
    [Test]
    public void DoorKeepsItsCampWhenTheOwnerCampHomeIsRemoved()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = Door(world);
        var portalId = door.Junctions[0];
        var hut = Hut(world);

        // Дом — Colony2; её очаг рядом, очаг постороннего Colony3 — дальше.
        DoorTopology.StampOwner(world, hut, Faction.Colony2);
        world.FactionHomes[Faction.Colony2] = hut.Tile;
        world.FactionHomes[Faction.Colony3] = hut.Tile;
        world.DoorStateVersion++;
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        // Лагерь Colony2 теряет очаг: слились, вымерли, перенесли стоянку.
        world.FactionHomes.Remove(Faction.Colony2);
        world.DoorStateVersion++;

        var inside = InteriorJunction(world);
        var outside = OutsideNeighborOfPortal(world, portalId);
        var mine = DoorTopology.ForbiddenFor(world, Faction.Colony2);
        var theirs = DoorTopology.ForbiddenFor(world, Faction.Colony3);

        Assert.Multiple(() =>
        {
            Assert.That(DoorTopology.OwnerFaction(world, door), Is.EqualTo(Faction.Colony2),
                "Исчезновение очага не передаёт дом соседнему лагерю (#237 r2).");
            Assert.That(mine is null || !mine.Contains(portalId), Is.True,
                "Жительнице её собственный портал не запрещают.");
            Assert.That(HexPathfinder.FindPath(
                    world, inside, outside, null, hardAvoid: mine), Is.Not.Empty,
                "Из собственного дома обязан существовать выход наружу.");
            Assert.That(HexPathfinder.FindPath(
                    world, outside, inside, null, hardAvoid: mine), Is.Not.Empty,
                "И вход внутрь — заперли бы снаружи так же надёжно.");
            Assert.That(theirs, Is.Not.Null);
            Assert.That(theirs, Does.Contain(portalId),
                "А посторонний лагерь чужую дверь по-прежнему не открывает.");
        });
    }

    /// <summary>
    /// ⭐ #237 r2: слияние лагерей (§146.13) переписывает хозяина дома на
    /// канонический лагерь. Очаги ОБОИХ исходных лагерей при этом удаляются,
    /// так что дом без переписанного штампа достался бы соседу по расстоянию.
    /// </summary>
    [Test]
    public void CampMergeCarriesTheHutToTheMergedCamp()
    {
        var world = TestWorld.CreateWorld(12345);
        world.Mode = Bootstrap.GameMode.HugeIsland; // слияние живёт только здесь
        var door = Door(world);
        var portalId = door.Junctions[0];
        var hut = Hut(world);

        DoorTopology.StampOwner(world, hut, Faction.Colony2);
        world.FactionHomes[Faction.Colony2] = hut.Tile;
        // Третий лагерь стоит ровно на хижине — прежний вывод по расстоянию
        // отдал бы ему дверь сразу же после слияния.
        world.FactionHomes[Faction.Colony3] = hut.Tile;
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var resident = world.Entities.Npcs.Values.First();
        var partner = world.Entities.Npcs.Values.First(n => !n.Id.Equals(resident.Id));
        resident.Faction = Faction.Colony2;
        partner.Faction = Faction.Colony;
        resident.Social.GetOrCreate(partner.Id).Affinity = 0.9f;
        partner.Social.GetOrCreate(resident.Id).Affinity = 0.9f;

        Assert.That(CampDiplomacyMath.TryMerge(
                world, resident, partner, CampHomeChoice.SecondCamp, out var reason),
            Is.True, $"Слияние не состоялось: {reason}");
        Assert.That(resident.Faction, Is.EqualTo(Faction.Colony));

        var inside = InteriorJunction(world);
        var outside = OutsideNeighborOfPortal(world, portalId);
        var forbidden = DoorTopology.ForbiddenFor(world, resident.Faction);

        Assert.Multiple(() =>
        {
            Assert.That(hut.OwnerFaction, Is.EqualTo(Faction.Colony),
                "Дом переезжает в объединённый лагерь вместе с жительницами.");
            Assert.That(DoorTopology.OwnerFaction(world, door),
                Is.EqualTo(resident.Faction),
                "После слияния дверь принадлежит объединённому лагерю, не третьему.");
            Assert.That(forbidden is null || !forbidden.Contains(portalId), Is.True,
                "Жительницу объединённого лагеря её дверь не запирает.");
            Assert.That(HexPathfinder.FindPath(
                    world, inside, outside, null, hardAvoid: forbidden), Is.Not.Empty,
                "Выход из собственного дома переживает слияние лагерей (#237 r2).");
            var stranger = DoorTopology.ForbiddenFor(world, Faction.Colony3);
            Assert.That(stranger, Does.Contain(portalId),
                "Посторонний лагерь дом слиянием не приобретает.");
        });
    }

    /// <summary>#237 r2: штамп хозяина — состояние мира, значит он в сейве.</summary>
    [Test]
    public void BuildingOwnerCampSurvivesSaveLoad()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = Hut(world);
        DoorTopology.StampOwner(world, hut, Faction.Colony4);

        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(
                   stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Persistence.WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(12345);
        using (var reader = new System.IO.BinaryReader(
                   stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Persistence.WorldSaveSerializer.Read(loaded, reader);
        }

        var restoredHut = loaded.Entities.Objects[hut.Id];
        var restoredDoor = loaded.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == DoorTopology.DoorDefinitionId);
        Assert.Multiple(() =>
        {
            Assert.That(restoredHut.OwnerFaction, Is.EqualTo(Faction.Colony4),
                "Без записи в сейв каждая загрузка заново гадала бы владельца.");
            Assert.That(DoorTopology.OwnerFaction(loaded, restoredDoor),
                Is.EqualTo(Faction.Colony4));
        });
    }

    /// <summary>
    /// #237: запрет роутера обязан совпадать с правом открыть створку. Между
    /// «враждебна» и «союзница» жил НЕЙТРАЛЬНЫЙ лагерь (§146.12, solo-camp
    /// режимы): роутер вёл её сквозь закрытую дверь, движенческий гейт
    /// отказывал, путь сбрасывался — вечная петля вместо честного PathFailed.
    /// </summary>
    [Test]
    public void RouterBansClosedDoorForEveryoneWhoMayNotOpenIt()
    {
        var world = TestWorld.CreateWorld(12345);
        world.Mode = Bootstrap.GameMode.HugeIsland; // лагеря нейтральны, не враждебны
        var door = Door(world);
        var portalId = door.Junctions[0];
        var hut = Hut(world);
        world.FactionHomes[Faction.Colony2] = hut.Tile;
        world.DoorStateVersion++;

        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);

        var owner = DoorTopology.OwnerFaction(world, door);
        var neighbourCamp = Faction.Colony3;
        Assert.Multiple(() =>
        {
            Assert.That(FactionRelations.AreHostile(world, neighbourCamp, owner), Is.False,
                "В solo-camp мире соседний девичий лагерь именно НЕЙТРАЛЕН — это и есть щель.");
            Assert.That(FactionRelations.AreAllies(neighbourCamp, owner), Is.False,
                "…и открыть чужую дверь он всё равно не вправе.");

            var forbidden = DoorTopology.ForbiddenFor(world, neighbourCamp);
            Assert.That(forbidden, Is.Not.Null);
            Assert.That(forbidden, Does.Contain(portalId),
                "Кто не вправе открыть — тому портал запрещён и роутером (#237).");
        });

        // Гейт движения обязан отвечать тем же: путь есть только у союзницы.
        var npc = world.Entities.Npcs.Values.First();
        npc.Faction = neighbourCamp;
        ArmStepThroughPortal(world, npc,
            OutsideNeighborOfPortal(world, portalId), portalId,
            InsideNeighborOfPortal(world, portalId));
        new MovementSystem().Run(world);
        Assert.That(door.IsDoorOpen, Is.False,
            "Нейтральная соседка чужую дверь не открывает.");
    }

    [Test]
    public void KillSwitchLeavesDoorsUntouched()
    {
        var previous = Spec129.Enabled;
        try
        {
            Spec129.Enabled = false;
            var engine = TestWorld.CreateEngine(12345);
            var world = engine.World;
            var door = Door(world);
            Assert.That(door.IsDoorOpen, Is.True);

            var watermark = 0L;
            for (var tick = 0; tick < 2000; tick++)
            {
                engine.Step();
                foreach (var e in DrainSince(world, ref watermark))
                {
                    Assert.That(e.Type, Is.Not.EqualTo("DoorOpened"));
                    Assert.That(e.Type, Is.Not.EqualTo("DoorClosed"));
                    Assert.That(e.Type, Is.Not.EqualTo("DoorRefused"));
                }
            }

            Assert.That(door.IsDoorOpen, Is.True,
                "С выключенным §129 дверь никто не трогает.");
        }
        finally
        {
            Spec129.Enabled = previous;
        }
    }
}

}
