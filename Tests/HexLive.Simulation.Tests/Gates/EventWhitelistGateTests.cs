using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Дрейф вайтлиста игровых событий (<see cref="GameEventTypes"/>).
/// <para>
/// Тест существует из-за конкретного багa, прожившего годы: список ждал
/// <c>Collapsed</c>, пока путь комы эмитил <c>FellAsleepExhausted</c>, и
/// <c>MeatCooked</c>, пока огонь эмитил <c>MeatRoasted</c>. Комы и готовка не
/// доходили до истории колонии и до звука ВООБЩЕ — и в локальной игре тоже.
/// Мёртвая запись выглядит как покрытие, поэтому её не видно глазами.
/// </para>
/// </summary>
public sealed class EventWhitelistGateTests
{
    /// <summary>
    /// Записи, которые ЗАВЕДОМО ждут ещё не написанного кода. Пусто по замыслу:
    /// строка здесь — это обещание, а не оправдание, и она обязана нести причину.
    /// </summary>
    private static readonly HashSet<string> KnownUnemitted = new HashSet<string>(StringComparer.Ordinal);

    [Test]
    public void EveryWhitelistedTypeIsActuallyEmitted()
    {
        var emitted = SourceScan.EmittedEventTypes()
            .Select(h => h.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(emitted, Is.Not.Empty,
            "Сканер не нашёл НИ ОДНОЙ точки эмита — сломан разбор, а не код.");

        // Часть имён живёт за одним прыжком: их возвращает хелпер
        // (CraftedTraceName) или передаёт вызывающий (FinishPersonalCare(…,
        // "Bathed")). Точный скан такое не видит, но настоящий баг был не в
        // косвенности, а в ОТСУТСТВИИ имени во всём исходнике — "Collapsed" и
        // "MeatCooked" не встречались нигде.
        var anywhere = SourceScan.AllStringLiterals();

        var dead = GameEventTypes.ListedTypes
            .Where(t => !emitted.Contains(t) && !anywhere.Contains(t) && !KnownUnemitted.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.That(dead, Is.Empty,
            "В вайтлисте есть типы, которых симуляция не эмитит НИКОГДА — их нет во " +
            "всём исходнике даже строкой. Это не покрытие, а его видимость: такие " +
            "события не дойдут ни до истории, ни до звука. Найди, как событие " +
            "называется на самом деле, и исправь GameEventTypes.cs:\n  " +
            string.Join("\n  ", dead));

        var indirect = GameEventTypes.ListedTypes
            .Where(t => !emitted.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        if (indirect.Count > 0)
        {
            TestContext.Out.WriteLine(
                "Эмитятся через хелпер или параметр — имя есть, но не в самой точке " +
                "эмита (" + indirect.Count + "):\n  " + string.Join("\n  ", indirect));
        }
    }

    /// <summary>
    /// Обратная сторона: тип эмитится, но его нет в вайтлисте. Это НОРМА для
    /// 90% типов (AI думает вслух), поэтому тест не падает — он печатает список,
    /// чтобы при добавлении игрового события было видно, что оно пока не видно
    /// игроку.
    /// </summary>
    [Test]
    public void ReportEmittedTypesOutsideWhitelist()
    {
        var listed = GameEventTypes.ListedTypes.ToHashSet(StringComparer.Ordinal);
        var emitted = SourceScan.EmittedEventTypes()
            .Select(h => h.Value)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !listed.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        TestContext.Out.WriteLine(
            "Эмитится, но игроку не видно (" + emitted.Count + " типов):\n  " +
            string.Join("\n  ", emitted));

        Assert.Pass();
    }
}

}
