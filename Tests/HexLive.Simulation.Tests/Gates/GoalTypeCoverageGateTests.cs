using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Каждая цель обязана иметь ход: её либо считает аукцион, либо ставит система
/// напрямую, либо она объявлена мёртвой.
/// <para>
/// Цель, которую никто не считает и никто не ставит, выглядит рабочей — она есть
/// в enum, у неё есть локализованное имя, её видно в дебаг-панели, — и не
/// случается никогда. Так живут <c>PlaceSite</c> и <c>DeliverToSite</c>: удалить
/// их нельзя (сейв хранит цели ординалом), поэтому тест их ЗАПИСЫВАЕТ мёртвыми,
/// а не делает вид, что их нет.
/// </para>
/// <para>
/// После Фазы 4 (GoalCatalog) проверка переедет с «есть вызов AddGoalScore» на
/// «есть дескриптор» — вопрос тот же, источник ответа надёжнее.
/// </para>
/// </summary>
public sealed class GoalTypeCoverageGateTests
{
    /// <summary>Ставятся системами напрямую, мимо аукциона (§62, §72, §81).</summary>
    private static readonly HashSet<GoalType> Reactive = new HashSet<GoalType>
    {
        GoalType.Flee,
        GoalType.Defend,
        GoalType.Raid,
        GoalType.Abuse,
    };

    /// <summary>
    /// Ординалы, которые уже не значат ничего. Удалить нельзя: сейв хранит цели
    /// числом, и вырезание середины перемаркировало бы каждую цель в каждом
    /// существующем сейве.
    /// </summary>
    private static readonly HashSet<GoalType> DeadOrdinals = new HashSet<GoalType>
    {
        GoalType.PlaceSite,
        GoalType.DeliverToSite,

        // §35.5B: сушилка стала стадийным build-site у костра (BedSiteSystem
        // ставит, BuildFurniture поднимает), и скоринг цели убрали — а обвязка
        // осталась жить: GoalToInteraction, IsValidTargetFor, ворота крафта,
        // размещение выхода и рецепт в RecipeCatalog всё ещё знают про CraftRack.
        // Ставку никто не делает, значит ни одна из этих веток не исполняется.
        // Обвязку снимать Фазой 4, когда знание о цели соберётся в один дескриптор.
        GoalType.CraftRack,
    };

    [Test]
    public void EveryGoalIsScoredReactiveOrDeclaredDead()
    {
        var scored = ScoredGoals();
        var assigned = AssignedGoals();

        Assert.That(scored, Is.Not.Empty, "Сканер не нашёл ни одного AddGoalScore.");

        var orphans = new List<string>();
        foreach (GoalType goal in Enum.GetValues(typeof(GoalType)))
        {
            if (goal == GoalType.None || DeadOrdinals.Contains(goal) ||
                scored.Contains(goal) || assigned.Contains(goal))
            {
                continue;
            }

            orphans.Add(goal.ToString());
        }

        Assert.That(orphans, Is.Empty,
            "Цель есть в enum, но её никто не считает и никто не ставит — она не " +
            "случится никогда, при этом выглядит рабочей. Либо дай ей скоринг, либо " +
            "внеси в DeadOrdinals:\n  " + string.Join("\n  ", orphans));
    }

    [Test]
    public void ReactiveGoalsAreActuallyAssignedBySystems()
    {
        var assigned = AssignedGoals();

        var notAssigned = Reactive
            .Where(g => !assigned.Contains(g))
            .Select(g => g.ToString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.That(notAssigned, Is.Empty,
            "Цель объявлена реактивной, но ни одна система её не присваивает — " +
            "значит она мертва, а список Reactive это скрывает:\n  " +
            string.Join("\n  ", notAssigned));
    }

    [Test]
    public void DeadOrdinalsStayDead()
    {
        var scored = ScoredGoals();
        var assigned = AssignedGoals();

        var resurrected = DeadOrdinals
            .Where(g => scored.Contains(g) || assigned.Contains(g))
            .Select(g => g.ToString())
            .ToList();

        Assert.That(resurrected, Is.Empty,
            "Цель объявлена мёртвой, но код её использует — убери её из " +
            "DeadOrdinals:\n  " + string.Join("\n  ", resurrected));
    }

    private static HashSet<GoalType> ScoredGoals() =>
        ParseGoals(SourceScan.CallArguments("AddGoalScore", 2)
            .Concat(SourceScan.CallArguments("RaiseGoalScore", 2))
            .Select(h => h.Value));

    private static HashSet<GoalType> AssignedGoals() =>
        ParseGoals(SourceScan.DirectGoalAssignments().Select(h => "GoalType." + h.Value));

    private static HashSet<GoalType> ParseGoals(IEnumerable<string> expressions)
    {
        var goals = new HashSet<GoalType>();
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
