using System.Linq;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class StaminaRestRecoveryTests
{
    [Test]
    public void ExhaustedColonistChoosesRestAfterTheCurrentJobEnds()
    {
        var world = TestWorld.CreateWorld(267);
        var npc = world.Entities.Npcs.Values.First();
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.Completed;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Needs.Energy = 1f;
        npc.Needs.Comfort = 1f;
        npc.Needs.Stamina = 0f;

        new DecisionSystem().Run(world);

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Sit),
            "Нулевая выносливость после завершённой работы обязана выиграть " +
            "аукцион отдыха даже при полном комфорте.");
    }

    [Test]
    public void ExhaustedSitFallsBackToTheGroundWhenThereIsNoSeat()
    {
        var world = TestWorld.CreateWorld(2671);
        var npc = world.Entities.Npcs.Values.First();
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        npc.Mind.CurrentGoal = GoalType.Sit;
        npc.Mind.ManualControl = false;
        npc.Mind.AdrenalineUntilTick = 0;
        npc.Mind.RestCooldownUntilTick = 0;
        npc.CurrentJunction = null; // no ledge candidate; ground rest needs none
        npc.Movement.IsMoving = false;
        npc.Needs.Stamina = 0f;

        new PlanningSystem().BuildGroundSitPlan(world, npc);

        Assert.That(npc.Plan.Steps.Single().Type, Is.EqualTo(PlanStepType.IdleRest),
            "Без стула или уступа истощённая должна сесть там, где стоит.");
    }

    [Test]
    public void SittingNeverLowersStaminaWhenDynamicCeilingFalls()
    {
        var world = TestWorld.CreateWorld(152);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.CurrentInteraction = InteractionType.Sit;
        npc.Needs.Hunger = 1f;
        npc.Needs.Energy = 0f;
        npc.Needs.Comfort = 0f;
        npc.Needs.Stamina = 0.8f;
        var before = npc.Needs.Stamina;

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.Needs.Stamina, Is.EqualTo(before).Within(0.000001f),
            "a positive rest tick must not clamp accumulated stamina downward");
    }

    [Test]
    public void SittingBelowCeilingStillGainsCanonicalRestRate()
    {
        var world = TestWorld.CreateWorld(1521);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.CurrentInteraction = InteractionType.Sit;
        npc.Needs.Hunger = 0f;
        npc.Needs.Energy = 1f;
        npc.Needs.Comfort = 1f;
        npc.Needs.Stamina = 0.2f;
        var before = npc.Needs.Stamina;

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.Needs.Stamina - before,
            Is.EqualTo(SimBalance.StaminaRestGain).Within(0.000001f));
    }

    [Test]
    public void IdleRestRecoversStaminaAndReportsRestInsteadOfWork()
    {
        var world = TestWorld.CreateWorld(220);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Rest;
        npc.Needs.Hunger = 0f;
        npc.Needs.Energy = 1f;
        npc.Needs.Comfort = 1f;
        npc.Needs.Stamina = 0.2f;
        var before = npc.Needs.Stamina;

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Stamina - before,
                Is.EqualTo(SimBalance.StaminaRestGain).Within(0.000001f));
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Stamina &&
                    impact.Kind == EffectKind.Resting &&
                    impact.Direction == EffectImpactDirection.Positive),
                Is.True);
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Stamina &&
                    impact.Kind == EffectKind.Working),
                Is.False);
        });
    }
}
