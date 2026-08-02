using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// У каждой цели есть ход: её либо считает аукцион, либо ставит система
/// напрямую, либо она объявлена мёртвой.
/// <para>
/// Цель, которую никто не считает и никто не ставит, выглядит рабочей — она есть
/// в enum, у неё есть локализованное имя, её видно в дебаг-панели, — и не
/// случается никогда.
/// </para>
/// <para>
/// «Реактивная» и «мёртвая» здесь НЕ перечисляются: это свойства цели, и живут
/// они в <see cref="GoalCatalog"/>. Второй список в тесте был бы ровно тем, от
/// чего таблица избавляет — местом, которое молча разойдётся с первым. Здесь
/// остаётся единственный вопрос, на который таблица не отвечает: делает ли
/// аукцион ставку на эту цель.
/// </para>
/// </summary>
public sealed class GoalTypeCoverageGateTests
{
    [Test]
    public void EveryGoalIsScoredReactiveOrDeclaredDead()
    {
        var scored = ScoredGoals();
        Assert.That(scored, Is.Not.Empty, "Сканер не нашёл ни одного AddGoalScore.");

        var orphans = Enum.GetValues(typeof(GoalType))
            .Cast<GoalType>()
            .Where(goal => goal != GoalType.None)
            .Where(goal => !scored.Contains(goal))
            .Where(goal => !GoalCatalog.IsReactive(goal))
            .Where(goal => !GoalCatalog.IsDead(goal))
            .Select(goal => goal.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(orphans, Is.Empty,
            "Цель есть в enum, но её никто не считает и никто не ставит — она не " +
            "случится никогда, при этом выглядит рабочей. Либо дай ей скоринг, " +
            "либо пометь в GoalCatalog реактивной или мёртвой:\n  " +
            string.Join("\n  ", orphans));
    }

    [Test]
    public void DeadGoalsAreNotScored()
    {
        var scored = ScoredGoals();

        var resurrected = Enum.GetValues(typeof(GoalType))
            .Cast<GoalType>()
            .Where(goal => GoalCatalog.IsDead(goal) && scored.Contains(goal))
            .Select(goal => goal.ToString())
            .ToList();

        Assert.That(resurrected, Is.Empty,
            "Цель помечена мёртвой в GoalCatalog, но аукцион делает на неё " +
            "ставку — сними пометку:\n  " + string.Join("\n  ", resurrected));
    }

    private static HashSet<GoalType> ScoredGoals()
    {
        var goals = new HashSet<GoalType>();
        var expressions = SourceScan.CallArguments("AddGoalScore", 2)
            .Concat(SourceScan.CallArguments("RaiseGoalScore", 2))
            .Select(hit => hit.Value);

        foreach (var expression in expressions)
        {
            const string prefix = "GoalType.";
            var at = expression.IndexOf(prefix, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var start = at + prefix.Length;
            var end = start;
            while (end < expression.Length &&
                   (char.IsLetterOrDigit(expression[end]) || expression[end] == '_'))
            {
                end++;
            }

            if (Enum.TryParse(expression.Substring(start, end - start), out GoalType goal))
            {
                goals.Add(goal);
            }
        }

        return goals;
    }
}

}
