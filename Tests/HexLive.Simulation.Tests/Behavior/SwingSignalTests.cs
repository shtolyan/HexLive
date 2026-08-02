using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Сигналы, по которым ВИД рисует удар, обязаны реально меняться.
///
/// <para>
/// §103 r4: удары человека против человека не рисовались НИКОГДА, и причина была
/// не в таймингах, не в числе ударов и не в боевом флаге — их правили четыре
/// круга подряд. <c>NpcActorView</c> опознаёт новый удар по СМЕНЕ
/// <c>SwingStartTick</c> (окно <c>IsSwinging</c> живёт один-два тика, и кадр
/// может его проскочить). Штамп ставил только собачий бой; <c>MeleeSwing</c> —
/// копия того же кода — эту строку потерял, и человеческий замах приходил в вид
/// с нулевым или чужим давним штампом.
/// </para>
/// <para>
/// Такое не ловится ничем: модель верна, события верны, урон верен. Поэтому
/// проверка стоит здесь и спрашивает ровно то, что нужно виду.
/// </para>
/// </summary>
public sealed class SwingSignalTests
{
    private static (WorldState world, NPCState a, NPCState b) Pair()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToList();
        var a = npcs[0];
        var b = npcs[1];

        // Ставим их на смежные узлы — рука должна доставать.
        var from = world.Junctions.Items.Values.First(j => j.Neighbors.Count > 0);
        var to = world.Junctions.Items[from.Neighbors[0]];
        a.CurrentJunction = from.Id;
        a.Position = from.WorldPosition;
        b.CurrentJunction = to.Id;
        b.Position = to.WorldPosition;

        return (world, a, b);
    }

    [Test]
    public void HumanSwingStampsSwingStartTick()
    {
        var (world, a, b) = Pair();
        Assert.That(InteractionReach.CanStrike(world, a, b), Is.True,
            "Пара должна доставать друг до друга — иначе замах не начнётся.");

        a.SwingStartTick = 0;
        a.StrikeReadyAtTick = 0;
        a.StrikeLandsAtTick = 0;

        MeleeSwing.TryAdvanceSwing(world, a, inReach: true, out _, out _);

        Assert.That(a.AttackAnimUntilTick, Is.GreaterThan(world.Tick),
            "Замах должен был открыть окно анимации.");
        Assert.That(a.SwingStartTick, Is.EqualTo(world.Tick),
            "Окно открылось, но штамп начала замаха не поставлен — вид не узнает, " +
            "что бьют, и не проиграет НИЧЕГО. Именно так удары человека против " +
            "человека были невидимы (§103 r4).");
    }

    /// <summary>
    /// Каждый следующий замах обязан дать НОВЫЙ штамп: вид сравнивает с
    /// предыдущим и на равном не играет ничего.
    /// </summary>
    [Test]
    public void EachSwingGetsAFreshStamp()
    {
        var (world, a, b) = Pair();
        var stamps = new System.Collections.Generic.List<int>();

        for (var i = 0; i < 200; i++)
        {
            world.Tick++;
            MeleeSwing.TryAdvanceSwing(world, a, inReach: true, out _, out _);
            if (a.SwingStartTick != 0 &&
                (stamps.Count == 0 || stamps[^1] != a.SwingStartTick))
            {
                stamps.Add(a.SwingStartTick);
            }
        }

        Assert.That(stamps.Count, Is.GreaterThanOrEqualTo(3),
            "За 200 тиков должно пройти несколько замахов, и каждый — со своим " +
            "штампом. Повторяющийся штамп вид считает тем же ударом и молчит.");
    }
}

}
