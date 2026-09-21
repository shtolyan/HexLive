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
        "SimulationInputAdapter.cs")).Replace("\r\n", "\n");

    [Test]
    public void PickUpMenuOffersBothGatherAndGatherAllOnHex()
    {
        var adapter = Adapter();

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain(
                "new InteractCommand(\n                        actor, new ObjectId(objectId), type, interactionId)"),
                "Одиночное «Подобрать» обязано остаться прежним InteractCommand.");
            // Bug #331: у вертела мясо и так берётся по кусочку — «собрать
            // все» там не предлагается.
            Assert.That(adapter, Does.Contain(
                "if (type == InteractionType.PickUp && interactionId != \"take.from.spit\")"),
                "Второй пункт вешается на действие подбора (кроме вертела), " +
                "а не на весь каталог.");
            Assert.That(adapter, Does.Contain("Loc.Get(\"menu.gather_all\")"),
                "§58: подпись пункта — термин I2, а не строка в C#.");
            Assert.That(adapter, Does.Contain(
                "new GatherAllOnHexCommand(\n                            actor, new ObjectId(objectId), type, interactionId)"),
                "«Собрать все» обязано ехать своей командой — иначе по сети " +
                "(и в -hexlive-loopback) пункт молча превратится в обычный подбор.");
        });
    }

    [Test]
    public void CraftProjectsUseExplicitResumeOrReadyPickupInsteadOfGenericGatherAll()
    {
        var adapter = Adapter();
        var interactionLoop = adapter.Substring(adapter.IndexOf(
            "foreach (var interaction in definition.Interactions)", System.StringComparison.Ordinal));
        interactionLoop = interactionLoop.Substring(0, interactionLoop.IndexOf(
            "// §124.1", System.StringComparison.Ordinal));
        Assert.That(interactionLoop, Does.Match(
            @"if \(clicked != null && clicked\.CraftWorkRequired > 0 &&\s*" +
            @"\(interaction\.Type == InteractionType\.PickUp \|\| interaction\.Type == InteractionType\.Craft\)\) continue;"),
            "Проекты исключены из обычного подбора и следующего за ним «Собрать все».");
        Assert.That(interactionLoop.IndexOf("clicked.CraftWorkRequired", System.StringComparison.Ordinal),
            Is.LessThan(interactionLoop.IndexOf("new InteractCommand", System.StringComparison.Ordinal)),
            "Отсечение проектов должно выполняться до создания обычных пунктов меню.");

        var projectEntries = adapter.Substring(adapter.IndexOf(
            "private List<ContextMenuEntry> CraftProjectEntries", System.StringComparison.Ordinal));
        projectEntries = projectEntries.Substring(0, projectEntries.IndexOf(
            "private static ObjectSnapshot? FindObject", System.StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(projectEntries, Does.Contain(
                "var ready = project.CraftWorkDone >= project.CraftWorkRequired;"));
            Assert.That(projectEntries, Does.Match(
                @"(?s)if \(ready\).*?Loc\.Get\(""craft\.project\.take""\).*?" +
                @"new ObjectId\(projectId\), InteractionType\.PickUp"),
                "Готовый результат забирается по точному id, а не через общий сбор с гекса.");
            Assert.That(projectEntries, Does.Match(
                @"(?s)else\s*\{.*?Loc\.Get\(""craft\.project\.resume""\).*?""craft\.resume"""));
            Assert.That(projectEntries, Does.Not.Contain("GatherAllOnHexCommand"));
        });
    }

    [Test]
    public void GatherAllMenuEntryShowsNoCount()
    {
        var adapter = Adapter();
        var entry = adapter.Substring(adapter.IndexOf(
            "if (type == InteractionType.PickUp && interactionId != \"take.from.spit\")",
            System.StringComparison.Ordinal));
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
