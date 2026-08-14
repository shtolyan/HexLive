using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Детектор застоя ловит подпись §102 — и молчит на нормальной работе.
/// <para>
/// Здесь зарегистрирован ТОЛЬКО наблюдатель: без решений и планирования
/// состояние остаётся ровно тем, каким его поставили, и тест проверяет
/// детектор, а не способность колонии случайно застрять. Полный движок для
/// этого не годится — он бы за те же тики трижды перепланировал.
/// </para>
/// </summary>
public sealed class StuckDiagnosticTests
{
    private static (SimulationEngine engine, NPCState npc) Arena()
    {
        var world = TestWorld.CreateWorld();
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new StuckDiagnosticSystem());
        return (engine, world.Entities.Npcs.Values.First());
    }

    private static int StuckEvents(WorldState world, string reason) =>
        world.Events.Items.Count(e =>
            e.Type == "StuckDetected" && e.Message.Contains("Reason=" + reason));

    /// <summary>
    /// ⭐ Ровно то, на чём стоял чужак 2872 тика подряд: цель есть,
    /// взаимодействия нет, никуда не идёт — и симуляция об этом молчала.
    /// </summary>
    [Test]
    public void GoalWithNothingHappeningIsReported()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Abuse;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;

        for (var i = 0; i < AiBalance.StuckIdleTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "IdleWithGoal"), Is.GreaterThan(0),
            "Застой не замечен — а это единственное состояние, которое нельзя " +
            "услышать по событиям, потому что застой и есть их отсутствие.");
    }

    [Test]
    public void ShortStallsStaySilent()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Eat;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;

        // Заметно меньше порога: обычная передача хода от плана к пути занимает
        // считаные тики, и жаловаться на неё значит утопить настоящие находки.
        for (var i = 0; i < AiBalance.StuckIdleTicks / 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "IdleWithGoal"), Is.Zero,
            "Детектор сработал на нормальной паузе — такой шум обесценивает его.");
    }

    [Test]
    public void RepeatedCompletedWorkAtOnePileIsProgressNotStall()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.GatherLeaves;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;

        for (var i = 0; i < AiBalance.StuckIdleTicks * 3; i++)
        {
            if (i % 8 == 0)
            {
                npc.Execution.LastCompletedTick = engine.World.Tick;
            }
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(StuckEvents(engine.World, "IdleWithGoal"), Is.Zero,
                "Successful pickups at one junction must reset the stationary watch.");
            Assert.That(StuckDiagnosticSystem.CountsAsIdleWithGoal(
                engine.World, npc), Is.False,
                "The soak metric must use the same recent-progress grace.");
        });
    }

    [Test]
    public void CompletedIdleIsNotAStall()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Idle;
        npc.Plan.Goal = GoalType.Idle;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;

        for (var i = 0; i < AiBalance.StuckIdleTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "IdleWithGoal"), Is.Zero,
            "Idle deliberately means standing still; it must not pollute the " +
            "stuck signal.");
    }

    [Test]
    public void UnconsciousIsNotStuck()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Sleep;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.IsMoving = false;
        npc.Mind.ComaCause = ComaCause.Exhaustion;

        for (var i = 0; i < AiBalance.StuckIdleTicks * 3; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "IdleWithGoal"), Is.Zero,
            "Лежащая в коме обязана лежать неподвижно — это сюжет, а не застой.");
    }

    /// <summary>
    /// Долгое честное дело (сон — тысячи тиков) не должно попадать под
    /// перерасход. Поэтому порог и меряется от СОБСТВЕННОГО EndTick
    /// взаимодействия, а не общим числом тиков.
    /// </summary>
    [Test]
    public void LongHonestInteractionIsNotOverrun()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Sleep;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.StartTick = engine.World.Tick;
        npc.Execution.EndTick = engine.World.Tick + 100000;
        npc.Movement.IsMoving = false;

        for (var i = 0; i < AiBalance.StuckStepTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "StepOverrun"), Is.Zero,
            "Сон длиной в тысячи тиков — не перерасход; порог обязан считаться " +
            "от конца САМОГО взаимодействия.");
    }

    [Test]
    public void InteractionPastItsOwnEndIsReported()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Eat;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.StartTick = engine.World.Tick;
        npc.Execution.EndTick = engine.World.Tick + 10;
        npc.Movement.IsMoving = false;

        for (var i = 0; i < AiBalance.StuckStepTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "StepOverrun"), Is.GreaterThan(0),
            "Взаимодействие давно просрочило собственный EndTick и всё ещё идёт — " +
            "это ошибка, и она обязана быть слышна.");
    }

    [Test]
    public void TurningAndPathProgressDoNotCountAsPositionFrozen()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.GatherWood;
        npc.Plan.Goal = GoalType.GatherWood;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = new HexLive.Simulation.Common.JunctionId(700001);
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Rotating);

        for (var i = 0; i < AiBalance.StuckFrozenTicks * 3; i++)
        {
            npc.RotationDegrees += 1f;
            if (i % 32 == 0)
            {
                npc.Movement.PathIndex++;
            }
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "PositionFrozen"), Is.Zero,
            "A planted turn and advancing path index are movement progress, " +
            "even while the world position has not changed yet.");
    }

    [Test]
    public void NewGoalStartsANewFrozenWindow()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var diagnostic = new StuckDiagnosticSystem();
        npc.Mind.CurrentGoal = GoalType.WashClothes;
        npc.Plan.Goal = GoalType.WashClothes;
        npc.Plan.Status = PlanStatus.Active;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Blocked);

        diagnostic.Run(world);
        world.Tick = AiBalance.StuckFrozenTicks;

        npc.Mind.CurrentGoal = GoalType.CoolOff;
        npc.Plan.Goal = GoalType.CoolOff;
        diagnostic.Run(world);

        Assert.That(StuckEvents(world, "PositionFrozen"), Is.Zero,
            "A freshly selected route must not inherit the previous goal's " +
            "stationary time and be reported four ticks after it starts.");
    }

    [Test]
    public void ReplanSampleDoesNotHideRealPositionProgress()
    {
        var (engine, npc) = Arena();
        npc.Mind.CurrentGoal = GoalType.Expel;
        npc.Plan.Goal = GoalType.Expel;
        npc.Plan.Status = PlanStatus.Active;
        npc.Movement.IsMoving = false;

        // A moving-target chase rebuilds on Medium ticks.  The Slow observer
        // can see IsMoving=false at every sample even though the actor covered
        // ground between samples; coordinates are the authoritative progress.
        for (var i = 0; i < AiBalance.StuckIdleTicks * 3; i++)
        {
            if (i % 16 == 0)
            {
                npc.Position = new HexLive.Simulation.Common.Float2(
                    npc.Position.X + HexSpatialMath.HexRadius, npc.Position.Y);
            }
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "IdleWithGoal"), Is.Zero,
            "A chase which changes world position is not idle just because each " +
            "diagnostic sample lands on its replan tick.");
    }

    [Test]
    public void ValidAidWaitIsNotAGoallessCrisis()
    {
        var (engine, patient) = Arena();
        var helper = engine.World.Entities.Npcs.Values.First(n => n.Id != patient.Id);
        patient.Needs.Hunger = 1f;
        patient.Mind.CurrentGoal = GoalType.None;
        patient.Mind.PendingAidFrom = helper.Id;
        helper.Mind.CurrentGoal = GoalType.Aid;
        helper.Plan.TargetAgentId = patient.Id;

        for (var i = 0; i < AiBalance.StuckGoallessTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "GoallessCrisis"), Is.Zero,
            "The patient deliberately holds still for an active helper; this is " +
            "not an auction failure.");
    }

    [Test]
    public void PlayingDeadIsNotAGoallessCrisis()
    {
        var (engine, npc) = Arena();
        npc.Needs.Hunger = 1f;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.PlayDeadSinceTick = engine.World.Tick + 1;
        npc.Mind.PlayDeadUntilTick = engine.World.Tick + AiBalance.StuckGoallessTicks * 4;

        for (var i = 0; i < AiBalance.StuckGoallessTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "GoallessCrisis"), Is.Zero,
            "Feigning death is an authored stationary state, not a missing goal.");
    }

    [Test]
    public void ActiveAbuseRoleIsNotAGoallessCrisis()
    {
        var (engine, mark) = Arena();
        var abuser = engine.World.Entities.Npcs.Values.First(n => n.Id != mark.Id);
        mark.Needs.Hunger = 1f;
        mark.Mind.CurrentGoal = GoalType.None;
        mark.Mind.PendingAbuseFrom = abuser.Id;
        abuser.Execution.CurrentInteraction = InteractionType.Abuse;

        for (var i = 0; i < AiBalance.StuckGoallessTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "GoallessCrisis"), Is.Zero,
            "The mark's choice is owned by the active abuse scene; the lack of an " +
            "auction goal during that scene is intentional.");
    }

    [Test]
    public void ReactiveCombatIsNotAGoallessCrisis()
    {
        var (engine, npc) = Arena();
        npc.Needs.Hunger = 1f;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.IsFighting = true;

        for (var i = 0; i < AiBalance.StuckGoallessTicks * 2; i++)
        {
            engine.Step();
        }

        Assert.That(StuckEvents(engine.World, "GoallessCrisis"), Is.Zero,
            "Reactive combat deliberately owns the body outside the utility auction.");
    }

    /// <summary>
    /// Самописец обязан пережить то, чего не переживает общее кольцо: у
    /// зависшей молчуньи её собственные последние решения остаются на месте,
    /// сколько бы ни болтали остальные.
    /// </summary>
    [Test]
    public void FlightRecorderKeepsThisNpcsTailWhileOthersFillTheRing()
    {
        var world = TestWorld.CreateWorld();
        world.FlightRecorder = FlightRecorder.ForBehavior();

        var npcs = world.Entities.Npcs.Values.ToList();
        var quiet = npcs[0];
        var loud = npcs[1];

        Trace.Emit(world, quiet.Id, "GoalSelected", "Abuse (Score=3,000) CHANGED from None");

        // Забиваем общее кольцо (2048) с запасом — чужой болтовнёй.
        for (var i = 0; i < 4000; i++)
        {
            Trace.Emit(world, loud.Id, "GoalSelected", "Explore " + i);
        }

        var globalHasIt = world.Events.Items.Any(e => e.EntityId == quiet.Id.Value);
        Assert.That(globalHasIt, Is.False,
            "Кольцо должно было вытеснить её событие — иначе тест не про то.");

        var tail = world.FlightRecorder.Tail(quiet.Id.Value);
        Assert.That(tail.Count, Is.EqualTo(1));
        Assert.That(tail[0].Message, Does.Contain("Abuse"),
            "Самописец обязан хранить хвост КАЖДОГО NPC отдельно — ради этого он и есть.");
    }
}

}
