using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
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
