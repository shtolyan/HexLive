using System.Collections.Generic;
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

/// <summary>
/// §121.12: ручное «Строить» — до конца, а не на один цикл. Игрок: «чтобы он
/// строил, пока не закончатся материалы… а если в инвентаре закончились, искал
/// бы материалы рядом». Проверяется обещание, а не устройство:
/// <list type="number">
/// <item>один клик с пустыми руками достраивает сушилку из того, что лежит рядом;</item>
/// <item>кончился материал — встаёт и говорит почему, а не молча;</item>
/// <item>нечем помочь уже в момент клика — отказ, а не пустой цикл;</item>
/// <item>любой новый приказ сессию обрывает.</item>
/// </list>
/// </summary>
public sealed class ManualBuildSessionTests
{
    private const int RackSticks = 4;
    private const int RackRope = 4;

    private static NPCState TakeControl(SimulationEngine engine)
    {
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        engine.Step();
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = 1f;
        npc.Inventory.Items.Clear();
        var control = ManualCommandExecutor.Apply(
            world, new SetManualControlCommand(npc.Id, true));
        Assert.That(control.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            control.Reason);

        // Соседки не растаскивают материалы из-под теста.
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Equals(npc.Id)) continue;
            other.Needs.Hunger = 0f;
            other.Needs.Thirst = 0f;
            other.Mind.ManualControl = true;
        }

        return npc;
    }

    /// <summary>Площадка сушилки на достижимом гексе в 2–4 клетках, вокруг
    /// которой в радиусе поиска не лежит ни одной палки и верёвки, кроме
    /// разложенных тестом; и свободные узлы под эти предметы.</summary>
    private static WorldObjectState RackSite(
        WorldState world, NPCState npc, int freeSpots, out List<JunctionId> spots)
    {
        var start = npc.CurrentJunction!.Value;
        foreach (var anchor in world.Junctions.Items.Values.OrderBy(j => j.Id.Value))
        {
            if (anchor.Blocked || anchor.Tiles.Count == 0) continue;
            var tile = anchor.Tiles[0];
            if (HexSpatialMath.HexDistance(npc.Tile, tile) is < 2 or > 4 ||
                !SpatialQueries.IsJunctionFree(world, anchor.Id) ||
                !Connectivity.Reachable(world, start, anchor.Id))
            {
                continue;
            }

            var free = world.Junctions.Items.Values
                .Where(j => !j.Blocked && j.Tiles.Count > 0 && !j.Id.Equals(anchor.Id) &&
                            HexSpatialMath.HexDistance(j.Tiles[0], tile) == 1 &&
                            SpatialQueries.IsJunctionFree(world, j.Id) &&
                            Connectivity.Reachable(world, start, j.Id))
                .OrderBy(j => j.Id.Value)
                .Select(j => j.Id)
                .Take(freeSpots)
                .ToList();
            if (free.Count < freeSpots) continue;

            foreach (var stray in world.Entities.Objects.Values
                         .Where(o => (o.DefinitionId == ContentIds.Stick ||
                                      o.DefinitionId == ContentIds.Rope) &&
                                     HexSpatialMath.HexDistance(o.Tile, tile) <=
                                     ManualBuildSession.FetchRadiusTiles)
                         .ToList())
            {
                WorldObjectMutations.DespawnObject(world, stray.Id);
            }

            var site = WorldObjectMutations.SpawnObject(
                world, ContentIds.BuildSite, anchor.Fragment, tile, anchor.Id);
            site.BuildProduct = ContentIds.DryingRack;
            site.BillSticks = RackSticks;
            site.BillRope = RackRope;
            site.InstalledBuildMaterials = new int[BuildSiteMath.AllMaterials.Length];
            spots = free;
            return site;
        }

        Assert.Fail("В мире не нашлось места под площадку сушилки.");
        spots = new List<JunctionId>();
        return null!;
    }

    private static void Lay(
        WorldState world, NPCState npc, string definitionId, IEnumerable<JunctionId> spots)
    {
        foreach (var spot in spots)
        {
            var junction = world.Junctions.Items[spot];
            WorldObjectMutations.SpawnObject(
                world, definitionId, npc.Fragment, junction.Tiles[0], spot);
        }
    }

    /// <summary>Шагает, пока условие не выполнится, и собирает исходы
    /// стройсессии по монотонному Seq (кольцо событий подрезается).</summary>
    private static List<string> StepCollecting(
        SimulationEngine engine, NPCState npc, System.Func<bool> done, int maxTicks)
    {
        var world = engine.World;
        var finished = new List<string>();
        var watermark = world.Events.Items.Count > 0 ? world.Events.Items.Max(e => e.Seq) : 0;
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            engine.Step();
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark) continue;
                watermark = e.Seq;
                if (e.Type == "ManualBuildFinished" && e.EntityId is { } id && id == npc.Id.Value)
                {
                    finished.Add(e.Message);
                }
            }
        }

        return finished;
    }

    // ── 1. Один клик — сушилка целиком, материалы из окрестности ──────────

    [Test]
    public void OneBuildClickFetchesNearbyMaterialsAndFinishesTheRack()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var site = RackSite(world, npc, RackSticks + RackRope, out var spots);
        var tile = site.Tile;
        Lay(world, npc, ContentIds.Stick, spots.Take(RackSticks));
        Lay(world, npc, ContentIds.Rope, spots.Skip(RackSticks));

        var order = ManualCommandExecutor.Apply(world, new InteractCommand(
            npc.Id, site.Id, InteractionType.Build));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);
        Assert.That(ManualBuildSession.IsActive(npc.Mind), Is.True,
            "«Строить» по площадке обязано взвести стройсессию.");
        Assert.That(npc.Plan.TargetObjectId, Is.Not.EqualTo(site.Id),
            "С пустыми руками первый шаг — сходить за материалом, а не пустой цикл.");

        var finished = StepCollecting(
            engine, npc, () => !ManualBuildSession.IsActive(npc.Mind), 12000);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False,
                "Площадка не поднялась — сессия встала на середине.");
            Assert.That(world.Entities.Objects.Values.Any(o =>
                    o.DefinitionId == ContentIds.DryingRack && o.Tile.Equals(tile)),
                Is.True, "На месте площадки нет сушилки.");
            Assert.That(finished, Is.EqualTo(new[] { "Outcome=Built" }),
                "Конец сессии обязан быть назван ровно один раз.");
            Assert.That(world.Entities.Objects.Values.Count(o =>
                    (o.DefinitionId == ContentIds.Stick || o.DefinitionId == ContentIds.Rope) &&
                    spots.Contains(o.Junctions[0])),
                Is.Zero, "Разложенные рядом материалы не дошли до стройки.");
        });
    }

    // ── 2. Кончился материал — встаёт и говорит почему ───────────────────

    [Test]
    public void SessionStopsWithNoMaterialsWhenTheStageRunsDry()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var site = RackSite(world, npc, 0, out _);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));

        var order = ManualCommandExecutor.Apply(world, new InteractCommand(
            npc.Id, site.Id, InteractionType.Build));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);

        var finished = StepCollecting(
            engine, npc, () => !ManualBuildSession.IsActive(npc.Mind), 4000);

        Assert.Multiple(() =>
        {
            Assert.That(BuildSiteMath.Installed(site, ContentIds.Stick), Is.EqualTo(2),
                "Принесённые палки обязаны быть поставлены все, а не одна за клик.");
            Assert.That(finished, Is.EqualTo(new[] { "Outcome=NoMaterials" }));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(AI.GoalType.None));
        });
    }

    // ── 3. Нечем помочь уже при клике — отказ, а не пустой цикл ──────────

    [Test]
    public void BuildWithNothingInHandOrNearbyIsRejected()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var site = RackSite(world, npc, 0, out _);

        var order = ManualCommandExecutor.Apply(world, new InteractCommand(
            npc.Id, site.Id, InteractionType.Build));

        Assert.Multiple(() =>
        {
            Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(order.Reason, Is.EqualTo("NoMaterials"));
            Assert.That(ManualBuildSession.IsActive(npc.Mind), Is.False);
        });
    }

    // ── 4. Новый приказ обрывает сессию ──────────────────────────────────

    [Test]
    public void AnyNewOrderEndsTheSession()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var site = RackSite(world, npc, RackSticks, out var spots);
        Lay(world, npc, ContentIds.Stick, spots);

        var order = ManualCommandExecutor.Apply(world, new InteractCommand(
            npc.Id, site.Id, InteractionType.Build));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);
        Assert.That(ManualBuildSession.IsActive(npc.Mind), Is.True);

        var stop = ManualCommandExecutor.Apply(world, new StopCommand(npc.Id));
        Assert.That(stop.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), stop.Reason);
        Assert.That(ManualBuildSession.IsActive(npc.Mind), Is.False,
            "«Отставить» посреди стройки не должно оставлять её строить за спиной игрока.");
    }
}

}
