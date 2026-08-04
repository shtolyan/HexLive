using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Взаимодействие живёт ровно столько, сколько план, который его начал.
///
/// <para>
/// История, ради которой файл существует («поехавший кокос»): план
/// <c>ConsumeInventoryItem</c> начал Drink, через два тика пробитый кокос ушёл
/// из рюкзака — <c>ExecFailed</c>, план Failed, — но ЦЕЛЬ осталась той же, а
/// чистит осиротевшее действие только <c>PlanInterruption.Abort</c> на СМЕНЕ
/// цели (§23.17). Планировщик построил новую дорогу на 158 шагов, и колонистка
/// ехала через весь остров с <c>CurrentInteraction=Drink</c>: вид зеркалит
/// глагол каждый кадр, поэтому клип питья играл поверх ходьбы, с тем же
/// кокосом в руке. Сид 42, тики 4825-4841.
/// </para>
///
/// <para>
/// Гейт стережёт САМ инвариант, а не одну дорожку к нему: любой глагол,
/// оставшийся включённым при ходьбе, — это тот же баг под другим именем.
/// </para>
/// </summary>
public sealed class InteractionOrphanTests
{
    /// <summary>
    /// Шов, на котором чинилось: план перестал быть активным, а действие
    /// осталось. Здесь зарегистрирован ТОЛЬКО планировщик — иначе неясно, кто
    /// прибрал состояние.
    /// </summary>
    [Test]
    public void ReplanClosesTheInteractionItOrphaned()
    {
        var world = TestWorld.CreateWorld();
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new PlanningSystem());

        var npc = world.Entities.Npcs.Values.First();
        npc.Mind.CurrentGoal = GoalType.Drink;
        npc.Plan.Goal = GoalType.Drink;
        npc.Plan.Status = PlanStatus.Failed;   // ровно то, что делает ExecFailed
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Drink;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + 16;

        // Планировщик — Medium-слой: даём ему заведомо дойти до хода.
        for (var i = 0; i < 8; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Execution.CurrentInteraction, Is.Null,
            "Перепланирование оставило начатое действие включённым — именно так " +
            "клип питья уезжает вместе с телом.");
        Assert.That(npc.Execution.Status, Is.Not.EqualTo(ExecutionStatus.InProgress));
    }

    /// <summary>
    /// И то же самое на настоящем острове: за 5000 тиков ни один глагол не
    /// должен оказаться включённым у идущей колонистки. До починки сид 42 давал
    /// 252 таких тика подряд — это и есть «поехавший кокос».
    /// </summary>
    [Test]
    public void NobodyWalksWithALiveInteraction_OnThePrototypeIsland()
    {
        var engine = TestWorld.CreateEngine(42);
        var world = engine.World;

        for (var i = 0; i < 5000; i++)
        {
            engine.Step();

            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Movement.IsMoving && npc.Execution.CurrentInteraction is { } verb)
                {
                    Assert.Fail(
                        $"Тик {world.Tick}: NPC{npc.Id.Value} идёт с включённым {verb} " +
                        $"(Goal={npc.Mind.CurrentGoal} Plan={npc.Plan.Status} " +
                        $"Exec={npc.Execution.Status}) — вид проиграет клип действия " +
                        "поверх ходьбы.");
                }
            }
        }
    }
}

}
