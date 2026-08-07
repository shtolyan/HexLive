using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// A path search is allowed a short transient retry, but never an unbounded
/// full-graph search every fast tick. This is the exact Explore/WashClothes
/// stall reproduced by bug #78.
/// </summary>
public sealed class PathFailureRecoveryTests
{
    private static (SimulationEngine engine, NPCState npc, ObjectId target) BlockedPlan()
    {
        var world = TestWorld.CreateWorld();
        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new PathfindingSystem());

        var npc = world.Entities.Npcs.Values.First();
        var target = world.Entities.Objects.Values.First().Id;
        var missingJunction = new JunctionId(int.MaxValue);

        npc.Mind.CurrentGoal = GoalType.WarmUp;
        npc.Plan.Goal = GoalType.WarmUp;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetObjectId = target;
        npc.Plan.TargetJunctionId = missingJunction;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetObject = target,
            TargetJunction = missingJunction
        });
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.BlockedWaitTicks = 0;
        npc.Movement.SetStatus(MovementStatus.Idle);
        npc.Movement.StopReason = string.Empty;

        return (engine, npc, target);
    }

    [Test]
    public void TransientEmptySearchesKeepThePlanRetryable()
    {
        var (engine, npc, target) = BlockedPlan();

        for (var i = 0; i < AiBalance.PathFailureRetryAttempts - 1; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Assert.That(npc.Movement.Status, Is.EqualTo(MovementStatus.Blocked));
        Assert.That(npc.Movement.BlockedWaitTicks,
            Is.EqualTo(AiBalance.PathFailureRetryAttempts - 1));
        Assert.That(npc.Memory.IsShunned(target, engine.World.Tick), Is.False,
            "Короткая пробка другим персонажем не должна объявлять объект тупиком.");
    }

    [Test]
    public void StableEmptySearchFailsPlanAndShunsTarget()
    {
        var (engine, npc, target) = BlockedPlan();

        for (var i = 0; i < AiBalance.PathFailureRetryAttempts; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(npc.Memory.IsShunned(target, engine.World.Tick), Is.True,
            "Недостижимый объект должен быть общим временным запретом для всех целей.");
        Assert.That(npc.Mind.Cooldowns.Any(c => c.Goal == GoalType.WarmUp), Is.True);
        Assert.That(engine.World.Events.Items.Count(e => e.Type == "PathFailed"),
            Is.EqualTo(AiBalance.PathFailureRetryAttempts));
        Assert.That(engine.World.Events.Items.Any(e =>
            e.Type == "PlanFailed" && e.Message.Contains("PathRetryLimit")), Is.True);
    }

    [Test]
    public void GatherToolsAvailabilityMatchesPickUpPlannerAndShun()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Perception.Objects.Clear();

        var tool = new PerceivedObject
        {
            Id = new ObjectId(int.MaxValue - 1),
            DefinitionId = ContentIds.PickaxeStone,
            IsReachable = true,
            IsOccupied = false,
            Distance = 1f
        };
        npc.Perception.Objects.Add(tool);

        Assert.That(DecisionSystem.HasMissingToolReachable(npc, world), Is.False,
            "Аукцион не должен обещать GatherTools для объекта без PickUp.");

        tool.AvailableInteractions.Add(InteractionType.PickUp);
        Assert.That(DecisionSystem.HasMissingToolReachable(npc, world), Is.True,
            "Доступный pickaxe с PickUp должен оставаться обычной целью GatherTools.");

        npc.Memory.Shun(tool.Id, world.Tick + AiBalance.ShunTicks);
        Assert.That(DecisionSystem.HasMissingToolReachable(npc, world), Is.False,
            "Planner пропустит shun-объект, значит availability обязана сделать то же.");
    }
}

}
