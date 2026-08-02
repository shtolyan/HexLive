using System.Linq;
using HexLive.Simulation.AI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// <c>Mind.Cooldowns</c> только растёт: истёкшие записи не удаляются никогда
/// (кроме одного явного <c>RemoveAll</c> для Dress), а каждый <c>AddGoalScore</c>
/// и каждая проверка кулдауна сканируют список линейно — ~55 целей на NPC каждые
/// четыре тика.
/// <para>
/// Тест написан ЗАРАНЕЕ и выключен намеренно: он фиксирует, каким должно быть
/// поведение после правки Фазы 2, чтобы намерение жило в коде, а не в роадмапе.
/// Включить вместе с подрезкой.
/// </para>
/// </summary>
public sealed class CooldownPruneTests
{
    [Test]
    [Ignore("Фаза 2: подрезка Mind.Cooldowns ещё не сделана — тест описывает цель.")]
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
