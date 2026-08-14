using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class AidStallRecoveryTests
{
    [Test]
    public void DeadTiredHelperDoesNotAbandonActiveAidForPeacetimeChores()
    {
        var world = TestWorld.CreateWorld();
        var helper = world.Entities.Npcs.Values.First();
        helper.Needs.Energy = Spec49.DeadTiredEnergy - 0.01f;
        helper.Needs.Hunger = 0.1f;
        helper.Needs.Thirst = 0.1f;
        helper.Mind.CurrentGoal = GoalType.Aid;
        helper.Mind.LastDecision = new DecisionResult { SelectedGoal = GoalType.Aid };
        helper.Mind.LastDecision.Scores.Add(new GoalScore
        {
            Goal = GoalType.Aid,
            FinalScore = 1f
        });
        helper.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = GoalType.Sleep,
            EndTick = world.Tick + 100
        });
        helper.Plan.Goal = GoalType.Aid;
        helper.Plan.Status = PlanStatus.Active;
        helper.Plan.Steps.Clear();
        helper.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });

        new DecisionSystem().Run(world);

        Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Aid),
            "Низкая энергия разрешает перейти к настоящему сну, но не рвать помощь ради Sit/стройки.");
    }

    [Test]
    public void ColdThermalDiscomfortNeverSetsTheOverheatedLatch()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        world.Environment.GlobalTemperature = 5f;
        npc.EquippedWarmth = 0f;
        npc.Needs.ThermalDiscomfort = 1f;
        npc.Mind.IsOverheated = true;

        new DecisionSystem().Run(world);

        Assert.That(npc.Mind.IsOverheated, Is.False,
            "Симметричный ThermalDiscomfort=1 при холоде не означает перегрев.");
    }

    [Test]
    public void DyingWardRemainsAidableWhileRecoverySleepIsBusy()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var helper = npcs[0];
        var patient = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        helper.CurrentJunction = junctions[0].Id;
        helper.Tile = junctions[0].Tiles[0];
        helper.Position = junctions[0].WorldPosition;
        patient.CurrentJunction = junctions[1].Id;
        patient.Tile = junctions[1].Tiles[0];
        patient.Position = junctions[1].WorldPosition;
        patient.Needs.Thirst = 1f;
        MortalityHelpers.EnterDying(world, patient, DyingCause.Dehydration);
        helper.Needs.Hunger = 0.1f;
        helper.Needs.Thirst = 0.1f;
        helper.Needs.Blood = 1f;
        helper.Health = 1f;
        helper.CompassionTrait = 1f;
        helper.BottleWater = WaterKind.Rain;
        helper.BottleCharges = 2;
        var blockedFeet = SpatialQueries.FindNearestJunction(
            world, LyingStations.Point(patient, LyingStations.FeetSlot));
        Assert.That(blockedFeet, Is.Not.Null);
        world.Occupancy.JunctionOwner[blockedFeet.Value] =
            new EntityId(int.MaxValue - 531);
        helper.Perception.Agents.Clear();
        helper.Perception.Agents.Add(new PerceivedAgent
        {
            Id = patient.Id,
            Tile = patient.Tile,
            Junction = patient.CurrentJunction,
            Distance = 1f,
            CanSee = true,
            IsReachable = true,
            IsBusy = true,
            IsDying = true,
            Suffering = 1f,
            AidKind = AidKind.Hydrate
        });

        new DecisionSystem().Run(world);

        Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Aid),
            "A bed/sleep interaction must not hide a dying ward from aid.");
        new PlanningSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(helper.Plan.TargetAgentId, Is.EqualTo(patient.Id));
            Assert.That(patient.Mind.PendingAidFrom, Is.EqualTo(helper.Id));
        });
    }

    [Test]
    public void AidBidIsWithheldWhenEveryLyingStationIsPhysicallyOccupied()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToArray();
        var helper = npcs[0];
        var patient = npcs[1];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(2)
            .ToArray();
        helper.CurrentJunction = junctions[0].Id;
        helper.Tile = junctions[0].Tiles[0];
        helper.Position = junctions[0].WorldPosition;
        patient.CurrentJunction = junctions[1].Id;
        patient.Tile = junctions[1].Tiles[0];
        patient.Position = junctions[1].WorldPosition;
        patient.Needs.Thirst = 1f;
        MortalityHelpers.EnterDying(world, patient, DyingCause.Dehydration);

        for (var slot = 0; slot < LyingStations.Count; slot++)
        {
            var station = SpatialQueries.FindNearestJunction(
                world, LyingStations.Point(patient, slot));
            Assert.That(station, Is.Not.Null);
            world.Occupancy.JunctionOwner[station.Value] =
                new EntityId(int.MaxValue - 540 - slot);
        }

        helper.Health = 1f;
        helper.Needs.Blood = 1f;
        helper.Needs.Hunger = 0.1f;
        helper.Needs.Thirst = 0.1f;
        helper.CompassionTrait = 1f;
        helper.BottleWater = WaterKind.Rain;
        helper.BottleCharges = 2;
        helper.Perception.Agents.Clear();
        helper.Perception.Remembered.Clear();
        helper.Perception.Agents.Add(new PerceivedAgent
        {
            Id = patient.Id,
            Tile = patient.Tile,
            Junction = patient.CurrentJunction,
            Distance = 1f,
            CanSee = true,
            IsReachable = true,
            IsDying = true,
            Suffering = 1f,
            AidKind = AidKind.Hydrate
        });

        Assert.That(PlanningSystem.HasAvailableArmsLengthApproach(
            world, helper, patient, patient.CurrentJunction.Value), Is.False);
        new DecisionSystem().Run(world);
        Assert.That(helper.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Aid),
            "Известное отсутствие станции не должно порождать Aid/PlanFailed каждые 40 тиков.");
    }

    [Test]
    public void SameTickSecondHelperYieldsWithoutAnAidFailureCooldown()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(3).ToArray();
        var helper = npcs[0];
        var patient = npcs[1];
        var firstHelper = npcs[2];
        var junctions = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .Take(3)
            .ToArray();
        for (var i = 0; i < npcs.Length; i++)
        {
            npcs[i].CurrentJunction = junctions[i].Id;
            npcs[i].Tile = junctions[i].Tiles[0];
            npcs[i].Position = junctions[i].WorldPosition;
        }

        helper.Mind.CurrentGoal = GoalType.Aid;
        helper.BottleWater = WaterKind.Rain;
        helper.BottleCharges = 2;
        helper.Perception.Agents.Clear();
        helper.Perception.Agents.Add(new PerceivedAgent
        {
            Id = patient.Id,
            Tile = patient.Tile,
            Junction = patient.CurrentJunction,
            Distance = 1f,
            CanSee = true,
            IsReachable = true,
            Suffering = 0.8f,
            AidKind = AidKind.Hydrate
        });
        patient.Mind.PendingAidFrom = firstHelper.Id;
        firstHelper.Mind.CurrentGoal = GoalType.Aid;
        firstHelper.Plan.Goal = GoalType.Aid;
        firstHelper.Plan.Status = PlanStatus.Active;
        firstHelper.Plan.TargetAgentId = patient.Id;
        firstHelper.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(helper.Mind.Cooldowns.Any(c => c.Goal == GoalType.Aid), Is.False);
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "PlanFailed" && e.EntityId == helper.Id.Value), Is.False);
        });
    }

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
