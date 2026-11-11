using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class InventorySurvivalTests
{
    [TestCase(ContentIds.CoconutOpen, GoalType.Eat, InteractionType.Eat)]
    [TestCase(ContentIds.Coconut, GoalType.Eat, InteractionType.Eat)]
    [TestCase(ContentIds.CoconutPierced, GoalType.Drink, InteractionType.Drink)]
    [TestCase(ContentIds.Coconut, GoalType.Drink, InteractionType.Drink)]
    public void FullPackCanConsumeReachableCoconutWithoutThrowingAwayACarriedCell(
        string coconutId, GoalType goal, InteractionType consume)
    {
        var world = TestWorld.CreateWorld(12345);
        var npc = world.Entities.Npcs.Values.Single(actor => actor.Id.Value == 1);
        PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "test setup");
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.Inventory.HolsterSlotIds.Clear();
        npc.Inventory.Capacity = 3;
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Machete));
        foreach (var id in new[] { ContentIds.Log, ContentIds.Stick })
            for (var i = 0; i < InventoryState.StackSizeFor(id); i++)
                npc.Inventory.Items.Add(new ItemInstance(id));
        var before = npc.Inventory.Items.ToArray();
        Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(npc.Inventory.Capacity));

        npc.Needs.Hunger = goal == GoalType.Eat ? .9f : .1f;
        npc.Needs.Thirst = goal == GoalType.Drink ? .9f : .1f;
        npc.Mind.ManualControl = false;
        npc.Mind.WakeGraceUntilTick = 0;
        npc.Movement.IsMoving = false;
        npc.Mind.CurrentGoal = goal;
        npc.Plan.Status = PlanStatus.None;
        var stand = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && junction.Tiles.Contains(npc.Tile) &&
            SpatialQueries.IsJunctionFree(world, junction.Id));
        npc.CurrentJunction = stand.Id;
        npc.Position = stand.WorldPosition;
        SpatialMutations.OccupyJunction(world, stand.Id, npc.Id);
        var coconut = WorldObjectMutations.SpawnObject(world, coconutId, npc.Fragment, npc.Tile, stand.Id);
        if (coconutId == ContentIds.CoconutPierced)
        {
            coconut.ResourceAmount = 4;
            coconut.WaterKind = WaterKind.Coconut;
        }
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject
        {
            Id = coconut.Id, DefinitionId = coconutId, Tile = coconut.Tile, IsReachable = true
        });
        var looseBefore = world.Entities.Objects.Values.Count(obj =>
            obj.DefinitionId is ContentIds.Log or ContentIds.Stick);
        new NeedsDecaySystem().Run(world);
        Assert.That(npc.Inventory.Items.Count, Is.EqualTo(before.Length), "Slow tick must retain the entire pack.");

        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Assert.That(npc.Plan.Steps.Last().Interaction, Is.EqualTo(consume));
        var pathfinding = new PathfindingSystem();
        var movement = new MovementSystem();
        var execution = new ExecutionSystem();
        for (var i = 0; i < 240 && npc.Plan.Status == PlanStatus.Active; i++)
        {
            world.Tick++;
            pathfinding.Run(world);
            movement.Run(world);
            execution.Run(world);
        }

        var consumptionEvent = goal == GoalType.Eat ? "CoconutEaten" : "CoconutDrank";
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
        Assert.That(world.Events.Items.Count(item => item.Type == consumptionEvent), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(goal == GoalType.Eat ? npc.Needs.Hunger : npc.Needs.Thirst, Is.LessThan(.9f));
            Assert.That(npc.Inventory.Items.Count, Is.EqualTo(before.Length));
            Assert.That(npc.Inventory.Items.Zip(before, ReferenceEquals).All(same => same), Is.True);
            Assert.That(world.Entities.Objects.Values.Count(obj =>
                obj.DefinitionId is ContentIds.Log or ContentIds.Stick), Is.EqualTo(looseBefore));
        });
    }
}
}
