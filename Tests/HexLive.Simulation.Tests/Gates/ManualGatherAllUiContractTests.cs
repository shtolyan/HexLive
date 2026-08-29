using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §121.10 / баг #270: headless-гейт на UI-половину «собрать все на гексе».
/// Симуляция без своего пункта меню — это починка, которой игрок не увидит,
/// а Unity-код в этой сборке не компилируется: проверяем исходник текстом,
/// как остальные UI-контракты.
/// </summary>
public sealed class ManualGatherAllUiContractTests
{
    private static string Adapter() => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Input",
        "SimulationInputAdapter.cs"));

    [Test]
    public void PickUpMenuOffersBothGatherAndGatherAllOnHex()
    {
        var adapter = Adapter();

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain(
                "new InteractCommand(\n                        actor, new ObjectId(objectId), type, interactionId)"),
                "Одиночное «Подобрать» обязано остаться прежним InteractCommand.");
            Assert.That(adapter, Does.Contain("if (type == InteractionType.PickUp)"),
                "Второй пункт вешается на действие подбора, а не на весь каталог.");
            Assert.That(adapter, Does.Contain("Loc.Get(\"menu.gather_all\")"),
                "§58: подпись пункта — термин I2, а не строка в C#.");
            Assert.That(adapter, Does.Contain(
                "new GatherAllOnHexCommand(\n                            actor, new ObjectId(objectId), type, interactionId)"),
                "«Собрать все» обязано ехать своей командой — иначе по сети " +
                "(и в -hexlive-loopback) пункт молча превратится в обычный подбор.");
        });
    }

    [Test]
    public void GatherAllMenuEntryShowsNoCount()
    {
        var adapter = Adapter();
        var entry = adapter.Substring(adapter.IndexOf(
            "if (type == InteractionType.PickUp)", System.StringComparison.Ordinal));
        entry = entry.Substring(0, entry.IndexOf('}'));

        Assert.That(entry, Does.Not.Contain("Count"),
            "Игрок просил не считать: «давай даже не будем считать, просто " +
            "собрать и собрать все». Счётчик в подписи — это ещё и ложь " +
            "прошлого кадра.");
    }

    [Test]
    public void GatherAllTermIsLocalizedInBothLanguages()
    {
        var asset = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.That(asset, Does.Match(
                @"- Term: '?menu\.gather_all'?\s*\n\s*TermType: 0\s*\n\s*Description:\s*\n\s*Languages:\s*\n\s*-\s+.+\n\s*-\s+.+"),
            "menu.gather_all должен существовать с непустыми EN и RU — иначе " +
            "в меню отрисуется сам ключ.");
    }
}
