using System.Collections.Generic;
using HexLive.Simulation.Navigation;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §21.21B v14 — асимметричная геометрия прыжка, замеренная на живом мире.
/// <para>
/// Оба теста писались ПОД найденные баги, каждый из которых прошёл бы мимо
/// глаз: длина прыжка вылетала до 2.4 wu вместо 0.75 (гард «прыгаю отсюда»
/// брал позицию НЕ на оси полёта), а промотка пути за раз съедала 118
/// junction'ов (критерий «не впереди посадки» выполняется для всего маршрута,
/// когда тот заворачивает назад вдоль стены). Оба видны только в статистике по
/// многим прыжкам, поэтому здесь прогон движка, а не арифметика.
/// </para>
/// </summary>
public sealed class HopGeometryTests
{
    private sealed record Hop(bool Up, float Length);

    [Test]
    public void EveryHopIsTheTunedLengthOrShorter()
    {
        var (hops, skips) = RunAndCollect();

        Assert.That(hops, Is.Not.Empty, "За прогон не случилось ни одного прыжка — мерить нечего.");

        var nominal = HexHopTuning.EdgePadding + HexHopTuning.FarPadding;
        foreach (var hop in hops)
        {
            // Строго не больше номинала: клэмп точки взлёта может только
            // укоротить прыжок (стену заметили поздно), но никогда не растянуть.
            Assert.That(hop.Length, Is.LessThanOrEqualTo(nominal + 0.01f),
                $"Прыжок {(hop.Up ? "вверх" : "вниз")} длиной {hop.Length:F3} при номинале " +
                $"{nominal:F3}: точка взлёта уехала с оси полёта.");
        }

        // Подавляющее большинство прыжков — штатные, номинальной длины.
        var nominalCount = 0;
        foreach (var hop in hops)
        {
            if (hop.Length > nominal - 0.01f)
            {
                nominalCount++;
            }
        }

        Assert.That(nominalCount, Is.GreaterThan(hops.Count / 2),
            $"Штатной длины только {nominalCount} из {hops.Count} прыжков — " +
            "значит стена почти всегда обнаруживается слишком поздно (окно скана?).");
        Assert.That(skips, Is.Not.Empty, "Промотка пути ни разу не сработала — тест ниже пустой.");
    }

    [Test]
    public void PathSkipStaysWithinTheFlight()
    {
        var (_, skips) = RunAndCollect();

        // Пролететь можно только точки, попавшие в отрезок полёта: при шаге
        // решётки 0.375 и полёте 0.75 их физически не больше двух. Всё, что
        // больше, означает съеденный маршрут (было: 118).
        foreach (var skip in skips)
        {
            Assert.That(skip, Is.LessThanOrEqualTo(2),
                $"Промотка съела {skip} junction'ов за один прыжок — в отрезок полёта " +
                "столько не влезает, значит критерий снова глотает весь путь.");
        }
    }

    private static (List<Hop> hops, List<int> skips) RunAndCollect()
    {
        var engine = TestWorld.CreateEngine();
        var hops = new List<Hop>();
        var skips = new List<int>();
        var seen = 0L;

        for (var tick = 0; tick < 4000; tick++)
        {
            engine.Step();
            foreach (var e in engine.World.Events.Items)
            {
                if (e.Seq <= seen)
                {
                    continue;
                }

                seen = e.Seq;
                if (e.Type == "HopStarted")
                {
                    if (TryParseHop(e.Message, out var hop))
                    {
                        hops.Add(hop);
                    }
                }
                else if (e.Type == "HopLanded" && TryParseSkip(e.Message, out var skip))
                {
                    skips.Add(skip);
                }
            }
        }

        return (hops, skips);
    }

    // "Up From=(19.16,-15.19) To=(18.79,-14.54)" — числа печатаются текущей
    // локалью, поэтому разбираем по позиции, а не по разделителю.
    private static bool TryParseHop(string message, out Hop hop)
    {
        hop = default;
        var from = ParsePoint(message, "From=(");
        var to = ParsePoint(message, "To=(");
        if (from is null || to is null)
        {
            return false;
        }

        var dx = to.Value.x - from.Value.x;
        var dy = to.Value.y - from.Value.y;
        hop = new Hop(message.StartsWith("Up"), System.MathF.Sqrt(dx * dx + dy * dy));
        return true;
    }

    private static (float x, float y)? ParsePoint(string message, string prefix)
    {
        var start = message.IndexOf(prefix, System.StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = message.IndexOf(')', start);
        if (end < 0)
        {
            return null;
        }

        var body = message.Substring(start, end - start);
        var parts = SplitPair(body);
        return parts is null ? null : parts;
    }

    // "19,16,-15,19" (запятая как десятичный разделитель) или "19.16,-15.19".
    private static (float x, float y)? SplitPair(string body)
    {
        var pieces = body.Split(',');
        if (pieces.Length == 4)
        {
            return (Num(pieces[0] + "." + pieces[1]), Num(pieces[2] + "." + pieces[3]));
        }

        return pieces.Length == 2 ? (Num(pieces[0]), Num(pieces[1])) : null;
    }

    private static float Num(string s) =>
        float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryParseSkip(string message, out int skip)
    {
        skip = 0;
        const string marker = "Eaten=";
        var at = message.IndexOf(marker, System.StringComparison.Ordinal);
        return at >= 0 && int.TryParse(message.Substring(at + marker.Length), out skip);
    }
}

}
