using System;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
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
    /// <summary>
    /// Стартовая четвёрка — ровно то, что перечислял старый список в коде.
    /// §120 добавил plan-дом и его LEGO-модули: их вяжут руками, без тега
    /// такую стройку нельзя было бы завершить без молотка
    /// (<c>BuildSiteMath.NeedsHammer</c>).
    /// </summary>
    private static readonly string[] HistoricalHandBuilt =
    {
        "architecture.door.wood",
        "architecture.floor.board",
        "architecture.roof.palm",
        "architecture.support.wood",
        "architecture.wall.wood",
        "architecture.window.wood",
        "building.hut_1hex",
        "building.hut_plan",
        "campfire.spot",
        ContentIds.HeadPike, // #675: one spear and one head, put together by hand.
        ContentIds.StorageChest, // #732: posts, boards and rope lashings; replaced a no-tool hand craft.
        "station.drying_rack",
        "station.water_collector",
        ContentIds.Torch, // #627: the same portable item can be assembled by hand at its placement site.
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

    [Test]
    public void PortableTorchConstructionNeedsItsWholeBillButNoHammer()
    {
        var world = TestWorld.CreateWorld();
        Assert.That(world.Content.ObjectDefinitions[ContentIds.Torch].HasTag(ObjectTags.HandBuilt), Is.True);
        var site = BuildSiteView.FurnitureBillPreview(ContentIds.Torch);
        Assert.That(site, Is.Not.Null);
        Assert.That(site.BuildProduct, Is.EqualTo(ContentIds.Torch),
            "Construction must produce the same physical item as inventory crafting.");
        Assert.That(BuildSiteView.NeedsHammer(world, site), Is.False);
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
