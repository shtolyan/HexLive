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

    [Test]
    public void GatherToolsAvailabilityAllowsMissingToolPastDuplicateKnives()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 2;
        npc.Inventory.Items.Add(ContentIds.Knife);
        npc.Inventory.Items.Add(ContentIds.Knife);
        npc.Perception.Objects.Clear();

        var pickaxe = new PerceivedObject
        {
            Id = new ObjectId(int.MaxValue - 2),
            DefinitionId = ContentIds.PickaxeStone,
            IsReachable = true,
            IsOccupied = false,
            Distance = 1f
        };
        pickaxe.AvailableInteractions.Add(InteractionType.PickUp);
        npc.Perception.Objects.Add(pickaxe);

        Assert.That(DecisionSystem.HasMissingToolReachable(npc, world), Is.True,
            "GatherTools must replace a redundant knife instead of looping on a full pack.");
    }

    [Test]
    public void OccupiedFinalGroundCraftPointReplansAfterOneWaitWindow()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var crafter = npcs[0];
        var blocker = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        Assert.That(junctions, Has.Length.EqualTo(2));

        crafter.CurrentJunction = junctions[0].Id;
        crafter.Tile = junctions[0].Tiles[0];
        crafter.Position = junctions[0].WorldPosition;
        blocker.CurrentJunction = junctions[1].Id;
        blocker.Tile = junctions[1].Tiles[0];
        blocker.Position = junctions[1].WorldPosition;
        crafter.Mind.CurrentGoal = GoalType.CraftBandage;
        crafter.Plan.Goal = GoalType.CraftBandage;
        crafter.Plan.Status = PlanStatus.Active;
        crafter.Plan.TargetJunctionId = junctions[1].Id;
        crafter.Plan.Steps.Clear();
        crafter.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
        crafter.Movement.JunctionPath.Clear();
        crafter.Movement.JunctionPath.Add(junctions[0].Id);
        crafter.Movement.JunctionPath.Add(junctions[1].Id);
        crafter.Movement.PathIndex = 1;
        crafter.Movement.IsMoving = true;
        crafter.Movement.SetStatus(MovementStatus.Moving);

        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new MovementSystem());
        for (var i = 0; i < 17; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(crafter.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(crafter.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(crafter.Movement.IsMoving, Is.False);
            Assert.That(crafter.Mind.Cooldowns.Any(c =>
                c.Goal == GoalType.CraftBandage), Is.False,
                "Transient congestion must not lock out medical crafting.");
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "PathOccupied" &&
                e.Message.Contains("ground-work point")), Is.True);
        });
    }

    [Test]
    public void OccupiedFinalAutonomousObjectPointReplansAfterOneWaitWindow()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var worker = npcs[0];
        var blocker = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        var log = WorldObjectMutations.SpawnObject(
            world, ContentIds.Log, worker.Fragment,
            junctions[1].Tiles[0], junctions[1].Id);
        worker.CurrentJunction = junctions[0].Id;
        worker.Tile = junctions[0].Tiles[0];
        worker.Position = junctions[0].WorldPosition;
        blocker.CurrentJunction = junctions[1].Id;
        blocker.Tile = junctions[1].Tiles[0];
        blocker.Position = junctions[1].WorldPosition;
        worker.Mind.CurrentGoal = GoalType.SplitLog;
        worker.Plan.Goal = GoalType.SplitLog;
        worker.Plan.Status = PlanStatus.Active;
        worker.Plan.TargetObjectId = log.Id;
        worker.Plan.TargetJunctionId = junctions[1].Id;
        worker.Plan.Steps.Clear();
        worker.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = junctions[1].Id,
            TargetObject = log.Id
        });
        worker.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = junctions[1].Id,
            TargetObject = log.Id
        });
        worker.Movement.JunctionPath.Clear();
        worker.Movement.JunctionPath.Add(junctions[0].Id);
        worker.Movement.JunctionPath.Add(junctions[1].Id);
        worker.Movement.PathIndex = 1;
        worker.Movement.IsMoving = true;
        worker.Movement.SetStatus(MovementStatus.Moving);

        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new MovementSystem());
        for (var i = 0; i < 17; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(worker.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(worker.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(worker.Memory.IsShunned(log.Id, world.Tick), Is.False,
                "A housemate on one work point does not make the log itself bad.");
            Assert.That(worker.Mind.Cooldowns.Any(c =>
                c.Goal == GoalType.SplitLog), Is.False);
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "PathOccupied" &&
                e.Message.Contains("object-work point")), Is.True);
        });
    }

    [Test]
    public void OccupiedFinalAutonomousLocationReplansAfterOneWaitWindow()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var worker = npcs[0];
        var blocker = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        worker.CurrentJunction = junctions[0].Id;
        worker.Tile = junctions[0].Tiles[0];
        worker.Position = junctions[0].WorldPosition;
        blocker.CurrentJunction = junctions[1].Id;
        blocker.Tile = junctions[1].Tiles[0];
        blocker.Position = junctions[1].WorldPosition;
        worker.Mind.CurrentGoal = GoalType.CoolOff;
        worker.Plan.Goal = GoalType.CoolOff;
        worker.Plan.Status = PlanStatus.Active;
        worker.Plan.TargetObjectId = null;
        worker.Plan.TargetAgentId = null;
        worker.Plan.TargetJunctionId = junctions[1].Id;
        worker.Plan.Steps.Clear();
        worker.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = junctions[1].Id
        });
        worker.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.GroundCool,
            TargetJunction = junctions[1].Id
        });
        worker.Movement.JunctionPath.Clear();
        worker.Movement.JunctionPath.Add(junctions[0].Id);
        worker.Movement.JunctionPath.Add(junctions[1].Id);
        worker.Movement.PathIndex = 1;
        worker.Movement.IsMoving = true;
        worker.Movement.SetStatus(MovementStatus.Moving);

        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new MovementSystem());
        for (var i = 0; i < 17; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(worker.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(worker.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(worker.Mind.Cooldowns.Any(c =>
                c.Goal == GoalType.CoolOff), Is.False);
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "PathOccupied" &&
                e.Message.Contains("autonomous destination")), Is.True);
        });
    }
}

}
