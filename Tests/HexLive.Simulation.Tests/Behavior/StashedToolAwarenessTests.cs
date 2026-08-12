using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: инструмент, уехавший в кармане снятой одежды (§52), — это НАЙДЕННЫЙ
/// инструмент, а не потерянный. Колония идёт за ним, а не делает второй.
/// </summary>
public sealed class StashedToolAwarenessTests
{
    private const string Jacket = "clothing.jacket_autumn";

    private static (WorldState world, NPCState npc, WorldObjectState stash) KnifeInAPocket()
    {
        var engine = TestWorld.CreateEngine(12345);
        for (var i = 0; i < 4; i++)
        {
            engine.Step();
        }

        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.RemoveAll(i => i.DefinitionId == ContentIds.Knife);
        // Единственный нож в мире должен быть ТОТ, что в кармане, иначе тест
        // проверял бы не заначку, а случайный нож под ногами.
        foreach (var loose in world.Entities.Objects.Values
                     .Where(o => o.DefinitionId == ContentIds.Knife).Select(o => o.Id).ToArray())
        {
            WorldObjectMutations.DespawnObject(world, loose);
        }

        // Куртка под ногами, а в её кармане — нож.
        var stash = WorldObjectMutations.SpawnObject(
            world, Jacket, npc.Fragment, npc.Tile, npc.CurrentJunction.Value);
        stash.Contents.Add(new ItemInstance(ContentIds.Knife));

        new PerceptionSystem().Run(world);
        return (world, npc, stash);
    }

    /// <summary>⭐ Крафт видит нож в кармане и не заказывает второй.</summary>
    [Test]
    public void AKnifeInAPocketCountsAsAReachableKnife()
    {
        var (world, npc, stash) = KnifeInAPocket();

        Assert.That(CraftProjectMath.HasReachableCompletedOutput(world, npc, GoalType.CraftKnife),
            Is.True,
            "Нож в кармане куртки не засчитан — колония сделает второй, лёжа на первом.");

        stash.Contents.Clear();
        Assert.That(CraftProjectMath.HasReachableCompletedOutput(world, npc, GoalType.CraftKnife),
            Is.False, "Пустой карман почему-то считается ножом.");
    }

    /// <summary>А цель «сходить за инструментом» умеет выбрать саму куртку как цель.</summary>
    [Test]
    public void TheGarmentItselfIsAValidGatherToolsTarget()
    {
        var (world, npc, stash) = KnifeInAPocket();

        Assert.That(InventoryMath.StashHoldsWantedTool(world, npc, stash), Is.True,
            "Заначка с ножом не опознана.");

        npc.Mind.CurrentGoal = GoalType.GatherTools;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);

        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active),
            "План за ножом в кармане не построился.");
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(stash.Id),
            "Пошли не за той вещью — карман с ножом остался лежать.");
    }

    /// <summary>
    /// Полный рюкзак не должен отменять поход за заначкой: поднимают ИНСТРУМЕНТ,
    /// а не куртку, — иначе решение зовёт идти, а планировщик молча отказывает.
    /// </summary>
    [Test]
    public void AFullPackStillPlansTheStashPickup()
    {
        var (world, npc, stash) = KnifeInAPocket();
        while (npc.Inventory.UsedSlots < npc.Inventory.Capacity)
        {
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stone));
        }

        npc.Mind.CurrentGoal = GoalType.GatherTools;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);

        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(stash.Id),
            "С полным рюкзаком за ножом в кармане не идут, хотя место освободят на месте.");
    }
}

}
