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
/// Баг #235 «костёр занят».
/// <para>
/// Заявку на объект (<c>IsOccupied</c>/<c>CurrentUser</c>) снимает КАЖДЫЙ рукав
/// <c>ExecutionSystem.ApplyInteractionCompletion</c> сам: безусловный хвост
/// метода освобождает только УЗЕЛ, а <c>PlanInterruption</c> отпускает объект
/// лишь пока <c>Execution.Status == InProgress</c> — на завершении он уже
/// <c>Completed</c>. Поэтому забытая строка не «подтечёт и рассосётся», а
/// запирает объект навсегда.
/// </para>
/// <para>
/// Ровно это и случилось с §52-заначкой у очага (<c>HaulToFire</c>): после
/// каждой штатной ходки костёр оставался занятым принёсшей вещь колонисткой.
/// Приказ игрока отбивался <c>Reject(Occupied)</c>, автономные
/// TendFire/CookMeat/FillBottle ловили <c>InteractionBlocked</c>, шунили очаг и
/// копили обиду на «занявшую» (§28.15B), а огонь тух — подкинуть дров было
/// некому.
/// </para>
/// <para>
/// Гейт сторожит ИНВАРИАНТ, а не одну строку: оба глагола-Observe на костре
/// обязаны уходить, оставив его свободным.
/// </para>
/// </summary>
public sealed class CampfireOccupancyTests
{
    [Test]
    public void StashingAtTheHearthReleasesTheFire()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var fire = SpawnLitFire(world, npc);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stone));

        FinishObserveAtFire(world, npc, fire, GoalType.HaulToFire);

        Assert.Multiple(() =>
        {
            Assert.That(fire.IsOccupied, Is.False,
                "Заначка у очага оставила костёр занятым — это и есть баг #235: " +
                "приказ игрока получит Reject(Occupied), а TendFire/CookMeat — " +
                "InteractionBlocked, и никто больше не подойдёт к огню.");
            Assert.That(fire.CurrentUser, Is.Null,
                "CurrentUser пережил завершённое взаимодействие — костёр " +
                "остался числиться за той, кто просто положила рядом вещь.");
        });
    }

    /// <summary>
    /// Соседний глагол того же типа — грелась у огня (§42). Он и до починки
    /// освобождался (общим <c>else</c>), и держит инвариант с другой стороны:
    /// правка не должна чинить один рукав ценой другого.
    /// </summary>
    [Test]
    public void WarmingByTheFireReleasesItToo()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var fire = SpawnLitFire(world, npc);

        FinishObserveAtFire(world, npc, fire, GoalType.WarmUp);

        Assert.Multiple(() =>
        {
            Assert.That(fire.IsOccupied, Is.False);
            Assert.That(fire.CurrentUser, Is.Null);
        });
    }

    /// <summary>
    /// Вторая половина той же заявки — УЗЕЛ подхода. Его освобождает
    /// безусловный хвост <c>ApplyInteractionCompletion</c>, а рукав «набить
    /// холодный очаг по приказу игрока» (#228) уходит из метода собственным
    /// <c>return true</c> и хвост пропускает. Узел остаётся за колонисткой
    /// навсегда: занятость узла не имеет срока (в отличие от брони) и снимается
    /// только явным <c>FreeJunction</c> — или смертью владелицы. Каждый приказ
    /// «подбросить дров» отнимал у костра один подход, пока к нему было не
    /// подойти.
    /// </summary>
    [Test]
    public void ManualStockingReleasesTheApproachJunction()
    {
        var engine = TestWorld.CreateEngine(22801);
        var world = engine.World;
        var npc = ManualColonistWithStick(world, engine);
        var fire = SpawnColdFire(world, npc);

        var order = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, fire.Id, InteractionType.Fuel));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);

        var approach = StepUntilSceneStarts(engine, npc, fire);
        StepUntil(engine, () => npc.Execution.TargetObject != fire.Id);

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.True,
                "Приказ не доиграл — тест меряет не то, что собирался.");
            Assert.That(fire.IsOccupied, Is.False);
            Assert.That(SpatialQueries.IsJunctionFree(world, approach), Is.True,
                "Узел подхода остался занят после доигранного приказа: рукав " +
                "ушёл из метода мимо хвоста, который единственный освобождает " +
                "узел. Занятость узла бессрочна — костёр теряет подход навсегда.");
        });
    }

    /// <summary>
    /// Отказ на завершении: палку между приказом и концом сцены успели забрать
    /// (§140 — чужак, обмен, съеденный инвентарь). Рукав снимает заявку с очага
    /// и уходит с <c>false</c> — но сцену за собой не закрывает: план остаётся
    /// <c>Active</c>, <c>Execution.Status</c> — <c>Completed</c>, а этого
    /// состояния не читает НИКТО (ни один <c>if</c> исполнителя под него не
    /// подходит). Колонистка застывает навсегда, держа узел подхода к костру, а
    /// у ручной ещё и планировщик выключен — вытащить её оттуда некому.
    /// </summary>
    [Test]
    public void FailedStockingClosesTheSceneInsteadOfWedgingIt()
    {
        var engine = TestWorld.CreateEngine(22801);
        var world = engine.World;
        var npc = ManualColonistWithStick(world, engine);
        var fire = SpawnColdFire(world, npc);

        var order = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, fire.Id, InteractionType.Fuel));
        Assert.That(order.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            order.Reason);

        var approach = StepUntilSceneStarts(engine, npc, fire);
        // Палки больше нет: подкидывать нечего, рукав обязан отказать.
        npc.Inventory.Items.Clear();
        StepUntil(engine, () => npc.Execution.TargetObject != fire.Id);

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.False,
                "Без палки в буфере очага не должно появиться топливо.");
            Assert.That(npc.Execution.Status, Is.Not.EqualTo(ExecutionStatus.Completed),
                "Колонистка осталась в Completed — тупик: ни одна ветка " +
                "исполнителя это состояние не подхватывает, и она стоит так " +
                "до конца игры.");
            Assert.That(fire.IsOccupied, Is.False,
                "Отказ оставил костёр занятым — это баг #235 со стороны отказа.");
            Assert.That(fire.CurrentUser, Is.Null);
            Assert.That(SpatialQueries.IsJunctionFree(world, approach), Is.True,
                "Узел подхода остался за ней после отказа: хвост метода " +
                "пропущен, а PlanInterruption сюда не звали вовсе.");
        });
    }

    private static NPCState ManualColonistWithStick(WorldState world, SimulationEngine engine)
    {
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        engine.Step(); // бутстрап ставит колонисток на их первый узел
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        var control = ManualCommandExecutor.Apply(
            world, new SetManualControlCommand(npc.Id, true));
        Assert.That(control.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
        return npc;
    }

    /// <summary>Холодный очаг в двух-четырёх гексах — приказу нужна дорога, а
    /// сцене нужен настоящий узел подхода, занятый настоящим стартом.</summary>
    private static WorldObjectState SpawnColdFire(WorldState world, NPCState npc)
    {
        var start = npc.CurrentJunction!.Value;
        var junction = world.Junctions.Items.Values.First(candidate =>
            !candidate.Blocked &&
            candidate.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, candidate.Tiles[0]) is >= 2 and <= 4 &&
            Connectivity.Reachable(world, start, candidate.Id)).Id;
        var tile = world.Junctions.Items[junction].Tiles[0];
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, tile, junction);
        fire.ResourceAmount = 0f;
        return fire;
    }

    /// <summary>Дошагать до начала сцены и вернуть узел, который её старт занял
    /// под колонисткой — только он и обязан освободиться в конце.</summary>
    private static JunctionId StepUntilSceneStarts(
        SimulationEngine engine, NPCState npc, WorldObjectState fire)
    {
        StepUntil(engine, () =>
            npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.TargetObject == fire.Id);
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress),
            "Сцена у костра так и не началась — тест меряет не то, что собирался.");
        var approach = npc.Plan.TargetJunctionId;
        Assert.That(approach, Is.Not.Null, "Сцена без узла подхода.");
        Assert.That(SpatialQueries.IsJunctionFree(engine.World, approach!.Value), Is.False,
            "Старт сцены не занял узел — освобождать будет нечего.");
        return approach.Value;
    }

    private static void StepUntil(
        SimulationEngine engine, System.Func<bool> condition, int maxTicks = 600)
    {
        for (var i = 0; i < maxTicks && !condition(); i++)
        {
            engine.Step();
        }
    }

    /// <summary>Горящий костёр НЕ на узле колонистки: §42-гейт «огонь потух»
    /// не должен сорвать сцену раньше, чем она доиграет.</summary>
    private static WorldObjectState SpawnLitFire(WorldState world, NPCState npc)
    {
        var junction = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0 &&
            (npc.CurrentJunction is not { } feet || !j.Id.Equals(feet)));
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, junction.Tiles[0], junction.Id);
        fire.ResourceAmount = 500f;
        return fire;
    }

    /// <summary>
    /// Ровно то состояние, в котором ExecutionSystem застаёт доигранную сцену у
    /// костра: план активен, действие идёт, срок вышел ЭТИМ тиком. Сцену
    /// собираем руками, а не соаком, — иначе непонятно, кто прибрал заявку.
    /// </summary>
    private static void FinishObserveAtFire(
        WorldState world, NPCState npc, WorldObjectState fire, GoalType goal)
    {
        npc.Mind.CurrentGoal = goal;
        npc.Plan.Goal = goal;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = fire.Id;
        npc.Plan.TargetJunctionId = npc.CurrentJunction;

        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Observe;
        npc.Execution.TargetObject = fire.Id;
        npc.Execution.StartTick = world.Tick - 8;
        npc.Execution.EndTick = world.Tick;

        // Заявка, которую поставил старт взаимодействия.
        fire.IsOccupied = true;
        fire.CurrentUser = npc.Id;

        new ExecutionSystem().Run(world);

        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.None),
            "Сцена не завершилась — тест меряет не то, что собирался.");
    }
}

}
