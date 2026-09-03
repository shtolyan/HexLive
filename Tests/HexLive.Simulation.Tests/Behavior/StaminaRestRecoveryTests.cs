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

        // Баг #152 запрещал ход НАЗАД; баг #353 добавил, что отдых при этом
        // всё равно идёт вперёд — голод больше не запирает запас у потолка.
        Assert.That(npc.Needs.Stamina, Is.GreaterThanOrEqualTo(before),
            "a positive rest tick must not clamp accumulated stamina downward");
        Assert.That(npc.Needs.Stamina - before,
            Is.EqualTo(SimBalance.StaminaRestGain).Within(0.000001f),
            "отдых голодной идёт штатным темпом, а не замирает под потолком");
    }

    /// <summary>
    /// Баг #353: «было десять, быстро восстановилось до шестидесяти и всё».
    /// Кровать входит в ту же ветку отдыха, что и <c>Sleep</c>, поэтому тест
    /// меряет её напрямую: подъём обязан быть монотонным, штатным по темпу
    /// (без мгновенного скачка) и обязан дойти до полного запаса, а не встать
    /// у метаболического потолка.
    /// </summary>
    [Test]
    public void SleepingClimbsSmoothlyToFullStaminaInsteadOfStallingAtTheCeiling()
    {
        var world = TestWorld.CreateWorld(353);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Sleep;
        // Ровно та обстановка, что даёт потолок бодрствования ~0.60.
        npc.Needs.Hunger = 0.5f;
        npc.Needs.Energy = 0.4f;
        npc.Needs.Comfort = 0.2f;
        npc.Needs.Stamina = 0.10f;

        var system = new NeedsDecaySystem();
        var previous = npc.Needs.Stamina;
        var biggestStep = 0f;
        for (var slowTick = 0; slowTick < 40 && npc.Needs.Stamina < 1f; slowTick++)
        {
            npc.Execution.CurrentInteraction = InteractionType.Sleep;
            system.Run(world);

            Assert.That(npc.Needs.Stamina, Is.GreaterThanOrEqualTo(previous - 0.000001f),
                $"сон обязан быть монотонным, а на тике {slowTick} запас упал");
            biggestStep = System.MathF.Max(biggestStep, npc.Needs.Stamina - previous);
            previous = npc.Needs.Stamina;
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Stamina, Is.GreaterThanOrEqualTo(0.95f),
                "выносливость на кровати обязана дойти до полного запаса, " +
                "а не замереть около 0.60");
            Assert.That(biggestStep,
                Is.LessThanOrEqualTo(SimBalance.StaminaRestGain + 0.000001f),
                "восстановление плавное: ни один тик не даёт скачка больше " +
                "штатного темпа отдыха");
        });
    }

    /// <summary>
    /// Вторая половина бага #353: полный запас после сна не должен
    /// схлопываться обратно к потолку бодрствования на первом же тике. Смысл
    /// потолка сохранён — излишек стекает, — но по чуть-чуть.
    /// </summary>
    [Test]
    public void WakingUpSettlesTheStaminaSurplusGraduallyNotWithASnapDown()
    {
        var world = TestWorld.CreateWorld(3531);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null; // проснулась: не отдых и не работа
        npc.Needs.Hunger = 0.5f;
        npc.Needs.Energy = 0.4f;
        npc.Needs.Comfort = 0.2f;
        npc.Needs.Stamina = 1f;

        var system = new NeedsDecaySystem();
        var previous = npc.Needs.Stamina;
        var biggestDrop = 0f;
        for (var slowTick = 0; slowTick < 30; slowTick++)
        {
            npc.Execution.CurrentInteraction = null;
            system.Run(world);
            biggestDrop = System.MathF.Max(biggestDrop, previous - npc.Needs.Stamina);
            previous = npc.Needs.Stamina;
        }

        Assert.Multiple(() =>
        {
            Assert.That(biggestDrop,
                Is.LessThanOrEqualTo(SimBalance.StaminaIdleGain + 0.000001f),
                "излишек над потолком бодрствования обязан стекать по чуть-чуть");
            Assert.That(npc.Needs.Stamina, Is.LessThan(1f),
                "и всё-таки стекать: потолок бодрствования не отменён");
        });
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
