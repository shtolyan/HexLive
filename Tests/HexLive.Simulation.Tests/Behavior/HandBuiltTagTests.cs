using System;
using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Пин на перенос правила «собирается руками» из кода в контент.
/// <para>
/// Было списком-отрицанием в исполнителе:
/// <c>BuildProduct is not ("campfire.spot" or "bed.leaf" or
/// "station.drying_rack" or "station.water_collector")</c>. Стало тегом
/// <see cref="ObjectTags.HandBuilt"/> на самих постройках.
/// </para>
/// <para>
/// Тест сверяет НАБОР — что перенос ничего не потерял и ничего не прихватил.
/// После одного релиза его можно удалить: дальше правду хранит контент, и
/// сверять её будет не с чем.
/// </para>
/// </summary>
public sealed class HandBuiltTagTests
{
    /// <summary>Ровно то, что перечислял старый список в коде.</summary>
    private static readonly string[] HistoricalHandBuilt =
    {
        "bed.leaf",
        "building.hut_1hex",
        "campfire.spot",
        "station.drying_rack",
        "station.water_collector",
    };

    [Test]
    public void HandBuiltSetMatchesTheListItReplaced()
    {
        var world = TestWorld.CreateWorld();

        var tagged = world.Content.ObjectDefinitions
            .Where(pair => pair.Value.Tags.Contains(ObjectTags.HandBuilt))
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.That(tagged, Is.EqualTo(HistoricalHandBuilt),
            "Набор построек «собирается руками» разошёлся со списком, который " +
            "тег заменил. Если постройка добавлена или снята осознанно — обнови " +
            "HistoricalHandBuilt и опиши почему; молотком дело не ограничится, " +
            "по этому же признаку решается, можно ли вообще завершить стройку.");
    }

    /// <summary>
    /// Тег обязан дожить до рантайма. Экспорт собирает определение с нуля и
    /// ставит через <c>Override</c>, а <c>ApplyTo</c> ДОПОЛНЯЕТ теги каталога
    /// кода — на этом и построен расчёт, что новый тег не требует Unity и
    /// переэкспорта. Если слияние однажды станет заменой, тег исчезнет молча.
    /// </summary>
    [Test]
    public void TagSurvivesTheSimDataMerge()
    {
        var world = TestWorld.CreateWorld();

        Assert.That(world.Content.ObjectDefinitions.TryGetValue("campfire.spot", out var campfire),
            Is.True);
        Assert.That(campfire.Tags.Contains(ObjectTags.HandBuilt), Is.True,
            "Тег из каталога кода не дожил до рантайма — значит слияние тегов " +
            "перестало быть слиянием, и все теги, заведённые без ассета, тихо " +
            "пропали вместе с ним.");

        // Заодно: экспорт объявляет у костра свои теги, и они не должны вытеснять
        // объявленные кодом.
        Assert.That(campfire.Tags.Contains(ObjectTags.Campfire), Is.True);
    }
}

}
