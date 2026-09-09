using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class PickupCapacityTests
{
    // A controlled regression at the report's seed/tick/NPC, not a replay of
    // its production save. The pack filled after the pickup plan was admitted.
    [TestCase(ContentIds.Hide, ExecutionStatus.InProgress)]
    [TestCase(ContentIds.Stone, ExecutionStatus.InProgress)]
    [TestCase(ContentIds.Hide, ExecutionStatus.None)]
    [TestCase(ContentIds.Hide, ExecutionStatus.Completed)]
    public void FullPackClosesPickupAndGatherQueueWithoutRetrying(
        string definitionId, ExecutionStatus status)
    {
        var (world, npc, item) = Pickup(definitionId, status);
        npc.Inventory.Items.Add(ContentIds.Knife);
        var carried = npc.Inventory.Items.Single();
        ManualGatherTargets.Arm(npc.Mind, definitionId, item.Tile,
            InteractionType.PickUp, null, 10);
        Assert.That(InventoryMath.CanMakeRoomForGoal(
            world, npc, npc.Plan.Goal, definitionId), Is.False);

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.None));
            Assert.That(npc.Execution.CurrentInteraction, Is.Null);
            Assert.That(npc.Plan.TargetObjectId, Is.Null);
            Assert.That(item.IsOccupied, Is.False);
            Assert.That(item.CurrentUser, Is.Null);
            Assert.That(world.Entities.Objects.ContainsKey(item.Id), Is.True);
            Assert.That(npc.Inventory.Items.Single(), Is.SameAs(carried));
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "ManualOrderInterrupted"), Is.True);
        });

        // The ordinary manual lifecycle must end "gather all", not re-arm
        // the same failed object every medium tick.
        for (var i = 0; i < 16; i++)
        {
            world.Tick++;
            new ManualOrderSystem().Run(world);
            new ExecutionSystem().Run(world);
        }
        Assert.That(ManualGatherTargets.IsActive(npc.Mind), Is.False);
        Assert.That(world.Events.Items.Count(e => e.Type == "PickupBlocked"), Is.EqualTo(1));
        Assert.That(npc.Memory.IsShunned(item.Id, world.Tick), Is.False,
            "A full pack must not blacklist a perfectly reachable item after space is freed.");
    }

    [Test]
    public void FullSlotWithPartialHideStackStillAcceptsAnotherHide()
    {
        var (world, npc, item) = Pickup(ContentIds.Hide, ExecutionStatus.InProgress);
        npc.Inventory.Items.Add(ContentIds.Hide);
        Assert.That(npc.Inventory.HasSpace, Is.False);

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Hide), Is.EqualTo(2));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(world.Entities.Objects.ContainsKey(item.Id), Is.False);
        });
    }

    [Test]
    public void AutonomousGatherDoesNotRetryUntilSpaceActuallyBecomesAvailable()
    {
        var (world, npc, item) = Pickup(ContentIds.PickaxeStone, ExecutionStatus.InProgress);
        npc.Mind.ManualControl = false;
        npc.Mind.CurrentGoal = npc.Plan.Goal = GoalType.GatherTools;
        npc.Inventory.Items.Add(ContentIds.Knife);
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject
        {
            Id = item.Id, DefinitionId = item.DefinitionId, Tile = item.Tile,
            IsReachable = true, AvailableInteractions = { InteractionType.PickUp }
        });
        npc.Needs.Hunger = npc.Needs.Thirst = npc.Needs.Social = 0f;
        npc.Needs.Energy = npc.Needs.Stamina = 1f;
        // Keep unrelated chores out of this capacity regression; the actual
        // GatherTools availability, auction score and planner remain live.
        foreach (GoalType goal in System.Enum.GetValues(typeof(GoalType)))
            if (goal is not GoalType.GatherTools and not GoalType.None and not GoalType.Idle)
                npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = goal, EndTick = world.Tick + 1000 });

        var decision = new DecisionSystem();
        var planning = new PlanningSystem();
        var pathfinding = new PathfindingSystem();
        var movement = new MovementSystem();
        var execution = new ExecutionSystem();
        execution.Run(world); // capacity changed after admission
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
        for (var i = 0; i < 32; i++)
        {
            world.Tick++;
            decision.Run(world);
            planning.Run(world);
            pathfinding.Run(world);
            movement.Run(world);
            execution.Run(world);
            Assert.That(npc.Mind.LastScores.Single(s => s.Goal == GoalType.GatherTools).FinalScore,
                Is.Zero, "A full pack must make the gather bid unavailable.");
        }
        Assert.That(world.Events.Items.Count(e =>
            e.EntityId == npc.Id.Value && e.Type == "PickupBlocked"), Is.EqualTo(1));

        npc.Inventory.Items.Clear();
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(world, npc, GoalType.GatherTools), Is.True);
        // §137: after finding no work she may finish her bounded idle-rest
        // block and the normal stand-up grace before reopening the auction.
        var resumeTicks = Spec137.RestBlockTicks * (Spec137.MaxRearms + 1) +
            AiBalance.WakeGraceTicks + 100;
        for (var i = 0; i < resumeTicks && world.Entities.Objects.ContainsKey(item.Id); i++)
        {
            world.Tick++;
            decision.Run(world);
            planning.Run(world);
            pathfinding.Run(world);
            movement.Run(world);
            execution.Run(world);
        }
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(item.Id), Is.False,
                "Freeing a slot must resume the real autonomous pickup without waiting out a shun. " +
                $"Goal={npc.Mind.CurrentGoal} Plan={npc.Plan.Status} Exec={npc.Execution.Status} " +
                $"Recent={string.Join(";", world.Events.Items.Where(e => e.EntityId == npc.Id.Value).TakeLast(12).Select(e => e.Type + ":" + e.Message))}");
            Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == item.DefinitionId), Is.True);
        });
    }

    private static (WorldState world, NPCState npc, WorldObjectState item) Pickup(
        string definitionId, ExecutionStatus status)
    {
        var world = TestWorld.CreateWorld(12345);
        world.Tick = 175652;
        var npc = world.Entities.Npcs.Values.Single(n => n.Id.Value == 1);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 1;
        npc.Inventory.HolsterSlotIds.Clear();
        npc.Mind.ManualControl = true;
        npc.Mind.LastManualInputTick = world.Tick;
        npc.Mind.WakeGraceUntilTick = 0;
        var anchor = npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
        npc.CurrentJunction = anchor;
        npc.Position = world.Junctions.Items[anchor].WorldPosition;
        var item = WorldObjectMutations.SpawnObject(
            world, definitionId, npc.Fragment, npc.Tile, anchor);
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetObjectId = item.Id;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = item.Id,
            Interaction = InteractionType.PickUp
        });
        npc.Movement.IsMoving = false;
        npc.Movement.JunctionPath.Clear();
        npc.Execution.Status = status;
        npc.Execution.CurrentInteraction = status == ExecutionStatus.InProgress
            ? InteractionType.PickUp : null;
        npc.Execution.TargetObject = item.Id;
        npc.Execution.StartTick = world.Tick - 4;
        npc.Execution.EndTick = world.Tick;
        item.IsOccupied = true;
        item.CurrentUser = npc.Id;
        return (world, npc, item);
    }
}

}
