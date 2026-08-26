using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
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
