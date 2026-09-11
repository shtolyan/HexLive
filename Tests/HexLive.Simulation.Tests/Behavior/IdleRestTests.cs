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
/// §137: незанятая колонистка садится на землю, а не стоит столбом.
/// <para>
/// Арена без <c>DecisionSystem</c>: аукцион переписал бы <c>Idle</c> на первом
/// же проходе, и тест мерил бы удачливость мира вместо самой фичи. Цель здесь
/// ставится руками — ровно то, что делает аукцион, когда ему нечего предложить.
/// </para>
/// </summary>
public sealed class IdleRestTests
{
    private static (SimulationEngine engine, NPCState npc) Arena()
    {
        var world = TestWorld.CreateWorld();
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        engine.Register(new PlanningSystem());
        return (engine, world.Entities.Npcs.Values.First());
    }

    private static void Step(SimulationEngine engine, NPCState npc, int ticks, bool holdIdle = true)
    {
        for (var i = 0; i < ticks; i++)
        {
            if (holdIdle && npc.Mind.CurrentGoal == GoalType.None)
            {
                // Так же ведёт себя аукцион: пока дел нет, он снова и снова
                // выбирает Idle. Без этого тест мерил бы один-единственный
                // такт, а вся суть §137 — что будет на втором.
                npc.Mind.CurrentGoal = GoalType.Idle;
            }

            engine.Step();
        }
    }

    [Test]
    public void ExplicitManualSitUsesIdleFallbackAndRemainsSeatedUntilInterrupted()
    {
        var (engine, npc) = Arena();
        var world = engine.World;
        npc.Mind.ManualControl = true;
        npc.Mind.CurrentGoal = GoalType.Sit;
        npc.Plan.Goal = GoalType.Sit;
        npc.Mind.AdrenalineUntilTick = 0;
        npc.Mind.RestCooldownUntilTick = 0;
        new PlanningSystem().BuildIdleRestPlan(world, npc);
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Step(engine, npc, 4, holdIdle: false);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Rest));
        Step(engine, npc, 30, holdIdle: false);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Rest), "Manual control must not abort its own explicit rest order.");
        npc.Mind.AdrenalineUntilTick = world.Tick + 100;
        engine.Step();
        Assert.That(npc.Execution.CurrentInteraction, Is.Null, "A real threat must still interrupt explicit rest.");
    }

    [Test]
    public void ManualControlStillForbidsUnrequestedIdleRest()
    {
        var (engine, npc) = Arena();
        npc.Mind.ManualControl = true;
        npc.Mind.CurrentGoal = GoalType.Idle;
        npc.Mind.AdrenalineUntilTick = 0;
        new PlanningSystem().BuildIdleRestPlan(engine.World, npc);
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
        Assert.That(npc.Execution.CurrentInteraction, Is.Null);
    }

    [Test]
    public void IdleColonistSitsDownWhereSheStands()
    {
        var (engine, npc) = Arena();
        var where = npc.Position;
        npc.Mind.CurrentGoal = GoalType.Idle;

        Step(engine, npc, 12);

        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Rest),
            "Цель Idle обязана превращаться в отдых, а не в пустой план.");
        Assert.That(npc.Movement.IsMoving, Is.False, "Отдых на месте не ходит.");
        Assert.That(HexLive.Simulation.Spatial.HexSpatialMath.Distance(npc.Position, where),
            Is.LessThan(0.01f), "Сесть надо ТАМ, ГДЕ СТОИШЬ — ни шага в сторону.");
    }

    /// <summary>
    /// ⭐ Сидит — не лежит. От этого зависят три десятка читателей
    /// <see cref="NPCState.IsLyingDown"/>: беспомощную поднимают на руки, к
    /// лежащей ищут станцию у тела, её не разворачивают лицом к собеседнице.
    /// Отдыхающую всё это трогать не должно.
    /// </summary>
    [Test]
    public void RestingIsNotLyingDown()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Idle;

        Step(engine, npc, 12);

        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Rest));
        Assert.That(npc.IsLyingDown(engine.World.Tick), Is.False,
            "Отдых сидя не должен читаться как лежачее тело.");
        Assert.That(npc.ClaimedJunctions, Is.Empty,
            "Сидящая занимает свой узел, и только его — лежачего футпринта §113 тут нет.");
    }

    [Test]
    public void DangerKeepsHerOnHerFeet()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Idle;
        npc.Mind.AdrenalineUntilTick = engine.World.Tick + 500;

        Step(engine, npc, 20);

        Assert.That(npc.Execution.CurrentInteraction, Is.Not.EqualTo(InteractionType.Rest),
            "Со свежим испугом не садятся.");
    }

    /// <summary>
    /// §41.5/баг #1: подъём — это КЛИП. Сим не имеет права везти её, пока он
    /// доигрывает, иначе ноги едут по земле.
    /// </summary>
    [Test]
    public void StandingUpBuysTheGetUpClipAndACooldown()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Idle;
        Step(engine, npc, 12);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Rest),
            "Предусловие теста: она должна была сесть.");

        // Что-то случилось — например, к ней пошли поговорить.
        npc.Mind.PendingTalkFrom = engine.World.Entities.Npcs.Values
            .First(other => !other.Id.Equals(npc.Id)).Id;
        var raised = engine.World.Tick;
        Step(engine, npc, 1, holdIdle: false);

        Assert.That(npc.Execution.CurrentInteraction, Is.Null, "Отдых обязан кончиться.");
        Assert.That(npc.Mind.WakeGraceUntilTick,
            Is.GreaterThanOrEqualTo(raised + AiBalance.WakeGraceTicks),
            "Вставание должно занять ровно столько, сколько длится клип подъёма.");
        Assert.That(npc.Mind.RestCooldownUntilTick,
            Is.GreaterThanOrEqualTo(raised + Spec137.CooldownTicks),
            "И небольшой колдаун сверху, чтобы «встала — села» не мельтешило.");
    }

    [Test]
    public void SheDoesNotFlopStraightBackDown()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Idle;
        Step(engine, npc, 12);
        npc.Mind.PendingTalkFrom = engine.World.Entities.Npcs.Values
            .First(other => !other.Id.Equals(npc.Id)).Id;
        Step(engine, npc, 1, holdIdle: false);
        npc.Mind.PendingTalkFrom = null;

        // Грация подъёма плюс запас — но всё ещё внутри колдауна.
        Step(engine, npc, AiBalance.WakeGraceTicks + 8);

        Assert.That(npc.Execution.CurrentInteraction, Is.Not.EqualTo(InteractionType.Rest),
            "Колдаун §137 обязан держать её на ногах, пока не выйдет.");
        Assert.That(npc.Mind.RestCooldownUntilTick, Is.GreaterThan(engine.World.Tick),
            "Предусловие теста: колдаун ещё не истёк.");
    }

    /// <summary>
    /// Потолок перевзводов — единственное, что заставляет её встать и спросить
    /// аукцион заново, когда прерывания так и не случилось.
    /// </summary>
    [Test]
    public void RestEndsByItselfAtTheRearmCap()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Idle;
        Step(engine, npc, 12);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Rest),
            "Предусловие теста: она должна была сесть.");

        Step(engine, npc, Spec137.RestBlockTicks * (Spec137.MaxRearms + 2), holdIdle: false);

        Assert.That(npc.Execution.CurrentInteraction, Is.Null,
            "Бесконечно сидеть нельзя: после потолка перевзводов она встаёт.");
        Assert.That(engine.World.Events.Items.Any(e =>
                e.Type == "RestEnded" && e.Message.Contains("Reason=Rested")),
            Is.True, "И встать она должна была именно по потолку.");
    }
}

}
