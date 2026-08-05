using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Множество типов событий обязано быть КОНЕЧНЫМ и записанным литералами.
/// <para>
/// §63 нашёл эмит, где id вещи подставлялся В САМ ТИП
/// (<c>"ClothesWashed underwear.bra …"</c>) — такой тип не увидит ни вайтлист,
/// ни счётчик соака, ни grep. Событие как бы есть, и его как бы нет: каждая
/// вещь порождала СВОЙ тип, и множество становилось бесконечным.
/// </para>
/// <para>
/// Тернарник из двух литералов нарушением не считается намеренно: имена в нём
/// константны и грепаются, а запрет ради единообразия заставил бы дублировать
/// вызов ради одного слова.
/// </para>
/// </summary>
public sealed class TraceEmitLintTests
{
    [Test]
    public void EventTypesAreNotComposedAtRuntime()
    {
        var offenders = SourceScan.ComposedEventTypes()
            .OrderBy(h => h.RelativePath, System.StringComparer.Ordinal)
            .ThenBy(h => h.Line)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Тип события собирается во время работы (интерполяция или склейка) — так " +
            "множество типов становится бесконечным, а событие невидимым для " +
            "вайтлиста и счётчиков. Всё переменное — в Message, формат Key=Value:\n  " +
            string.Join("\n  ", offenders.Select(o => o.ToString())));
    }
}

}
