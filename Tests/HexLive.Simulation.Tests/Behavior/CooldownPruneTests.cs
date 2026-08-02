using System.Linq;
using HexLive.Simulation.AI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Истёкшие кулдауны не копятся.
/// <para>
/// Тест писался под правку, которой не потребовалось: подрезка уже стоит в
/// начале решающего прохода (<c>DecisionSystem</c>, <c>Cooldowns.RemoveAll</c>
/// по истёкшему <c>EndTick</c>). Ревью утверждало обратное — «список только
/// растёт», — и это оказалось неверным. Тест оставлен включённым, чтобы
/// зафиксировать РЕАЛЬНОЕ поведение: список сканируется линейно на каждую из
/// ~55 целей каждого решения, так что его рост был бы дорогим и незаметным.
/// </para>
/// </summary>
public sealed class CooldownPruneTests
{
    [Test]
    public void ExpiredCooldownsDoNotAccumulate()
    {
        var engine = TestWorld.CreateEngine();
        var npc = engine.World.Entities.Npcs.Values.First();

        for (var i = 0; i < 200; i++)
        {
            npc.Mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = GoalType.Explore,
                EndTick = engine.World.Tick + 1
            });
        }

        for (var i = 0; i < 40; i++)
        {
            engine.Step();
        }

        Assert.That(npc.Mind.Cooldowns.Count, Is.LessThanOrEqualTo(16),
            "Истёкшие кулдауны должны подрезаться: список сканируется линейно на " +
            "каждую цель каждого решения, и за долгий прогон он растёт без границы.");
    }
}

}
