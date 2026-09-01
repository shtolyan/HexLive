using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
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
/// §121.10 / баг #270: «собрать все на гексе». Проверяется не наличие пункта в
/// меню, а обещание, данное игроку:
/// <list type="number">
/// <item>«Подобрать» осталось ровно прежним — один предмет и стоп;</item>
/// <item>«Собрать все» разбирает гекс ПО ОДНОМУ предмету за подход, а не
/// пачкой в один тик — это и была просьба («чтобы пальмовые листья собирались
/// по очереди, не все зараз»);</item>
/// <item>«все» — это однотипные предметы ЭТОГО гекса, а не всё подряд;</item>
/// <item>любой следующий приказ игрока очередь обрывает.</item>
/// </list>
/// </summary>
public sealed class ManualGatherAllTests
{
    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static void StepUntil(
        SimulationEngine engine, System.Func<bool> condition, int maxTicks = 1200)
    {
        for (var i = 0; i < maxTicks && !condition(); i++)
        {
            engine.Step();
        }
    }

    /// <summary>Ручная колонистка с пустыми руками и закрытыми нуждами: тест
    /// про очередь приказов, а не про §121.6.</summary>
    private static NPCState TakeControl(SimulationEngine engine)
    {
        var world = engine.World;
        var npc = Colonist(world);
        engine.Step(); // bootstrap ставит NPC на первый узел
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Inventory.Items.Clear();

        var control = ManualCommandExecutor.Apply(
            world, new SetManualControlCommand(npc.Id, true));
        Assert.That(control.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            control.Reason);

        // Соседки уходят с аукциона: пальмовый лист — штатный стройматериал
        // §120, и автономная сборщица унесла бы половину гекса из-под теста.
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Equals(npc.Id)) continue;
            other.Needs.Hunger = 0f;
            other.Needs.Thirst = 0f;
            other.Mind.ManualControl = true;
        }

        return npc;
    }

    /// <summary>Достижимый гекс в 2–4 клетках и его свободные узлы — по одному
    /// на каждый предмет, который тест хочет туда положить.</summary>
    private static List<JunctionId> FreeJunctionsOnOneTile(
        WorldState world, NPCState npc, int count, out TileCoord tile)
    {
        var start = npc.CurrentJunction!.Value;
        foreach (var candidate in world.Junctions.Items.Values.OrderBy(j => j.Id.Value))
        {
            if (candidate.Blocked || candidate.Tiles.Count == 0) continue;
            var candidateTile = candidate.Tiles[0];
            if (HexSpatialMath.HexDistance(npc.Tile, candidateTile) is < 2 or > 4)
            {
                continue;
            }

            var free = world.Junctions.Items.Values
                .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                            j.Tiles[0].Equals(candidateTile) &&
                            SpatialQueries.IsJunctionFree(world, j.Id) &&
                            Connectivity.Reachable(world, start, j.Id))
                .OrderBy(j => j.Id.Value)
                .Select(j => j.Id)
                .Take(count)
                .ToList();
            if (free.Count == count)
            {
                tile = candidateTile;
                return free;
            }
        }

        tile = default;
        Assert.Fail($"В мире не нашлось гекса со {count} свободными узлами.");
        return new List<JunctionId>();
    }

    private static List<WorldObjectState> Scatter(
        WorldState world, NPCState npc, string definitionId, int count,
        out TileCoord tile)
    {
        var junctions = FreeJunctionsOnOneTile(world, npc, count, out tile);
        var spawned = new List<WorldObjectState>();
        foreach (var junction in junctions)
        {
            spawned.Add(WorldObjectMutations.SpawnObject(
                world, definitionId, npc.Fragment, tile, junction));
        }

        return spawned;
    }

    private static int Carried(NPCState npc, string definitionId) =>
        npc.Inventory.Items.Count(item => item.DefinitionId == definitionId);

    // ── 1. Очередь: по одному за раз, но до конца ────────────────────────

    [Test]
    public void GatherAllTakesTheHexApartOneLeafPerOrder()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var leaves = Scatter(world, npc, ContentIds.PalmLeaf, 3, out _);

        var order = ManualCommandExecutor.Apply(world, new GatherAllOnHexCommand(
            npc.Id, leaves[0].Id, InteractionType.PickUp));

        Assert.Multiple(() =>
        {
            Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                order.Reason);
            Assert.That(npc.Plan.Steps.Count, Is.EqualTo(2),
                "Первый подход очереди обязан быть ТЕМ ЖЕ двухшаговым планом " +
                "«дойти и сделать», что и одиночное «Подобрать».");
            Assert.That(npc.Plan.TargetObjectId, Is.Not.Null,
                "Приказ взведён без конкретной цели — значит, это пакетный сбор, " +
                "а не очередь задач.");
            Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.True);
        });

        // ⭐ Суть бага: гекс разбирается ПО ОДНОМУ. Ни на одном тике не должно
        // исчезать больше одного листа сразу.
        var alive = new HashSet<ObjectId>(leaves.Select(leaf => leaf.Id));
        var removalTicks = new List<int>();
        for (var i = 0; i < 1600 && alive.Count > 0; i++)
        {
            engine.Step();
            var gone = alive.Where(id => !world.Entities.Objects.ContainsKey(id)).ToList();
            Assert.That(gone.Count, Is.LessThanOrEqualTo(1),
                $"На тике {world.Tick} с гекса исчезло {gone.Count} листа сразу — " +
                "приказ собрал их пачкой, а игрок просил по очереди.");
            foreach (var id in gone)
            {
                alive.Remove(id);
                removalTicks.Add(world.Tick);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(alive, Is.Empty,
                "Очередь «собрать все» остановилась, не разобрав гекс.");
            Assert.That(removalTicks, Is.Unique,
                "Листья ушли с гекса одним тиком — это не последовательные задачи.");
            Assert.That(Carried(npc, ContentIds.PalmLeaf), Is.EqualTo(3),
                "Собранные листья не доехали до инвентаря.");
        });

        StepUntil(engine, () => !ManualGatherTargets.IsActive(npc.Mind), 64);
        Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.False,
            "Пустой гекс обязан закрыть очередь, иначе она будет тикать вечно.");
    }

    // ── 2. Прежнее «Подобрать» не изменилось ─────────────────────────────

    [Test]
    public void PlainGatherStillTakesExactlyOneAndStops()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var leaves = Scatter(world, npc, ContentIds.PalmLeaf, 3, out _);

        var order = ManualCommandExecutor.Apply(world, new InteractCommand(
            npc.Id, leaves[0].Id, InteractionType.PickUp));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);

        StepUntil(engine, () => Carried(npc, ContentIds.PalmLeaf) >= 1);
        for (var i = 0; i < 400; i++) engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(Carried(npc, ContentIds.PalmLeaf), Is.EqualTo(1),
                "«Подобрать» подобрало больше одного — старое поведение сломано.");
            Assert.That(
                leaves.Count(leaf => world.Entities.Objects.ContainsKey(leaf.Id)),
                Is.EqualTo(2),
                "Одиночный приказ не должен трогать соседние листья.");
            Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.False,
                "Обычный Interact не имеет права взводить очередь.");
        });
    }

    // ── 3. «Все» — это однотипные, а не всё подряд ───────────────────────

    [Test]
    public void GatherAllTakesOnlyTheSameKindOfResource()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var junctions = FreeJunctionsOnOneTile(world, npc, 3, out var tile);

        var leaves = new List<WorldObjectState>
        {
            WorldObjectMutations.SpawnObject(
                world, ContentIds.PalmLeaf, npc.Fragment, tile, junctions[0]),
            WorldObjectMutations.SpawnObject(
                world, ContentIds.PalmLeaf, npc.Fragment, tile, junctions[1])
        };
        var stranger = WorldObjectMutations.SpawnObject(
            world, ContentIds.Stone, npc.Fragment, tile, junctions[2]);

        var order = ManualCommandExecutor.Apply(world, new GatherAllOnHexCommand(
            npc.Id, leaves[0].Id, InteractionType.PickUp));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);

        StepUntil(engine, () =>
            leaves.All(leaf => !world.Entities.Objects.ContainsKey(leaf.Id)));

        Assert.Multiple(() =>
        {
            Assert.That(leaves.Any(leaf => world.Entities.Objects.ContainsKey(leaf.Id)),
                Is.False, "Очередь не добрала однотипные листья.");
            Assert.That(world.Entities.Objects.ContainsKey(stranger.Id), Is.True,
                "«Собрать все листья» унесло с гекса камень — однотипность " +
                "считается по DefinitionId кликнутого предмета.");
        });
    }

    // ── 4. Следующий приказ обрывает очередь ─────────────────────────────

    [Test]
    public void AnyNextOrderCancelsTheGatherQueue()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var leaves = Scatter(world, npc, ContentIds.PalmLeaf, 3, out _);

        ManualCommandExecutor.Apply(world, new GatherAllOnHexCommand(
            npc.Id, leaves[0].Id, InteractionType.PickUp));
        StepUntil(engine, () => Carried(npc, ContentIds.PalmLeaf) >= 1);
        Assert.That(Carried(npc, ContentIds.PalmLeaf), Is.EqualTo(1),
            "Тест не дождался первого подобранного листа.");

        ManualCommandExecutor.Apply(world, new StopCommand(npc.Id));
        Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.False,
            "«Отставить» обязано снимать очередь — иначе она оживёт через тик " +
            "и приказ игрока окажется проигнорирован.");

        for (var i = 0; i < 400; i++) engine.Step();

        Assert.That(Carried(npc, ContentIds.PalmLeaf), Is.EqualTo(1),
            "После «отставить» сбор продолжился сам собой.");
    }

    // ── 5. Очередь конечна ───────────────────────────────────────────────

    /// <summary>
    /// §121.10, третье условие остановки: бюджет подходов ставится по числу
    /// однотипных предметов в МОМЕНТ ПРИКАЗА. Прилетевший позже лист очередь
    /// не продлевает — иначе гекс, на который соседка сбрасывает добычу,
    /// сделал бы приказ бесконечным, а девушку — вечно занятой.
    /// </summary>
    [Test]
    public void GatherAllStopsOnItsBudgetAndDoesNotChaseLateArrivals()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = TakeControl(engine);
        var junctions = FreeJunctionsOnOneTile(world, npc, 3, out var tile);
        var leaves = new List<WorldObjectState>
        {
            WorldObjectMutations.SpawnObject(
                world, ContentIds.PalmLeaf, npc.Fragment, tile, junctions[0]),
            WorldObjectMutations.SpawnObject(
                world, ContentIds.PalmLeaf, npc.Fragment, tile, junctions[1])
        };

        var order = ManualCommandExecutor.Apply(world, new GatherAllOnHexCommand(
            npc.Id, leaves[0].Id, InteractionType.PickUp));
        Assert.Multiple(() =>
        {
            Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                order.Reason);
            Assert.That(npc.Mind.GatherAllRemaining, Is.EqualTo(2),
                "Бюджет обязан равняться числу однотипных предметов гекса.");
        });

        // Пока она несёт первый лист, на гекс ложится третий — уже не её дело.
        StepUntil(engine, () => Carried(npc, ContentIds.PalmLeaf) >= 1);
        var late = WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmLeaf, npc.Fragment, tile, junctions[2]);

        StepUntil(engine, () => !ManualGatherTargets.IsActive(npc.Mind), 1600);
        for (var i = 0; i < 400; i++) engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.False,
                "Очередь не закрылась на своём бюджете — это вечный приказ.");
            Assert.That(Carried(npc, ContentIds.PalmLeaf), Is.EqualTo(2),
                "Собрано не ровно столько листьев, сколько лежало при приказе.");
            Assert.That(world.Entities.Objects.ContainsKey(late.Id), Is.True,
                "Лист, прилетевший ПОСЛЕ приказа, унесён: очередь продлевает " +
                "сама себя и может не кончиться никогда.");
        });
    }

    // ── 6. Граница приёма ────────────────────────────────────────────────

    [Test]
    public void GatherAllIsRefusedWithoutManualControlAndForAVanishedTarget()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = Colonist(world);
        engine.Step();
        var leaves = Scatter(world, npc, ContentIds.PalmLeaf, 1, out _);

        var notManual = ManualCommandExecutor.Apply(world, new GatherAllOnHexCommand(
            npc.Id, leaves[0].Id, InteractionType.PickUp));

        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        ManualCommandExecutor.Apply(world, new SetManualControlCommand(npc.Id, true));
        var vanished = ManualCommandExecutor.Apply(world, new GatherAllOnHexCommand(
            npc.Id, new ObjectId(999999), InteractionType.PickUp));

        Assert.Multiple(() =>
        {
            Assert.That(notManual.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(notManual.Reason, Is.EqualTo("NotManual"));
            Assert.That(notManual.Order, Is.EqualTo("GatherAll"),
                "Отказ обязан называть приказ своим именем — он едет игроку.");
            Assert.That(vanished.Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
            Assert.That(vanished.Reason, Is.EqualTo("TargetGone"));
            Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.False,
                "Отклонённый приказ не имеет права оставлять взведённую очередь.");
        });
    }
}

}
