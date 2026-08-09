using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class AidStallRecoveryTests
{
    [Test]
    public void OccupiedApproachAbortsAfterPoliteWaitInsteadOfRepathingForever()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(3).ToList();
        Assert.That(npcs, Has.Count.EqualTo(3));
        var junctions = world.Junctions.Items.Values
            .Where(j => j.Tiles.Count > 0)
            .Take(3)
            .ToList();
        Assert.That(junctions, Has.Count.EqualTo(3));
        for (var i = 0; i < npcs.Count; i++)
        {
            npcs[i].CurrentJunction = junctions[i].Id;
            npcs[i].Tile = junctions[i].Tiles[0];
            npcs[i].Position = junctions[i].WorldPosition;
        }
        var helper = npcs[0];
        var patient = npcs[1];
        var blocker = npcs[2];
        helper.Mind.CurrentGoal = GoalType.Aid;
        helper.Plan.Goal = GoalType.Aid;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = blocker.CurrentJunction;
        helper.Plan.Steps.Clear();
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = blocker.CurrentJunction
        });
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = blocker.CurrentJunction
        });
        helper.Plan.Status = PlanStatus.Active;
        helper.Movement.JunctionPath.Clear();
        helper.Movement.JunctionPath.Add(helper.CurrentJunction!.Value);
        helper.Movement.JunctionPath.Add(blocker.CurrentJunction!.Value);
        helper.Movement.PathIndex = 1;
        helper.Movement.IsMoving = true;
        helper.Movement.SetStatus(MovementStatus.Moving);
        patient.Mind.PendingAidFrom = helper.Id;

        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());

        for (var i = 0; i < 45; i++)
        {
            engine.Step();
        }

        Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        Assert.That(helper.Mind.Cooldowns.Any(c => c.Goal == GoalType.Aid), Is.True);
        Assert.That(patient.Mind.PendingAidFrom, Is.Null,
            "The occupied approach must release the patient for another helper.");
    }

    [Test]
    public void OccupiedRescueApproachAlsoAbortsAfterPoliteWait()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(3).ToList();
        var junctions = world.Junctions.Items.Values
            .Where(j => j.Tiles.Count > 0)
            .Take(3)
            .ToList();
        Assert.That(npcs, Has.Count.EqualTo(3));
        Assert.That(junctions, Has.Count.EqualTo(3));
        for (var i = 0; i < npcs.Count; i++)
        {
            npcs[i].CurrentJunction = junctions[i].Id;
            npcs[i].Tile = junctions[i].Tiles[0];
            npcs[i].Position = junctions[i].WorldPosition;
        }

        var helper = npcs[0];
        var patient = npcs[1];
        var blocker = npcs[2];
        patient.Mind.ComaCause = ComaCause.Exhaustion;
        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Plan.Goal = GoalType.Rescue;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = blocker.CurrentJunction;
        helper.Plan.Steps.Clear();
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = blocker.CurrentJunction
        });
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PickUpPerson,
            TargetJunction = blocker.CurrentJunction
        });
        helper.Plan.Status = PlanStatus.Active;
        helper.Movement.JunctionPath.Clear();
        helper.Movement.JunctionPath.Add(helper.CurrentJunction!.Value);
        helper.Movement.JunctionPath.Add(blocker.CurrentJunction!.Value);
        helper.Movement.PathIndex = 1;
        helper.Movement.IsMoving = true;
        helper.Movement.SetStatus(MovementStatus.Moving);
        patient.Mind.PendingAidFrom = helper.Id;

        var engine = new SimulationEngine(
            world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        for (var i = 0; i < 45; i++)
        {
            engine.Step();
        }

        Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        Assert.That(helper.Mind.Cooldowns.Any(c => c.Goal == GoalType.Rescue), Is.True);
        Assert.That(patient.Mind.PendingAidFrom, Is.Null);
    }
}

}
