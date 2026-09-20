using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Bug #238 — «модель двери не спавнится». Обе причины были в презентации и обе
/// невидимы в игре до конца сессии, поэтому они стерегутся текстом исходника: в
/// Unity-сборку эти файлы не входят, а собрать их здесь нечем.
/// <list type="number">
/// <item>§152 сделал загрузку контента АСИНХРОННОЙ:
/// <c>AtomicResources.Load</c> отдаёт null на первом промахе и лишь запускает
/// запрос бандла. <c>BlueprintArchitectureFactory.InstantiateModel</c> помнил
/// этот null в своём мемо — памятке, написанной под синхронный
/// <c>Resources.Load</c>, который для существующего ассета null не возвращал, —
/// и следующий снапшот попадал в отравленную запись. Дверь молча пропадала до
/// перезапуска игры.</item>
/// <item>Модуль §120 (<c>architecture.door.wood</c> и соседи) публикуется как
/// content-объект типа <c>building</c>: так его именует и
/// <c>AtomicResources</c>, и atomic-сборка. <c>ScenePrewarm</c> просил его как
/// <c>object</c> — то есть <c>Resolve</c> объявлял всю архитектурную семью
/// неразрешённой, прогрев не грел ничего, и первый же кадр с дверью приходил на
/// пустой кэш, ровно в условие пункта 1.</item>
/// </list>
/// </summary>
public sealed class ArchitectureModelContentContractTests
{
    [Test]
    public void ArchitectureModelLoaderNeverMemoizesTheAsynchronousFirstNull()
    {
        var source = SourceText.Read(Path.Combine(RepoPaths.Root, "Assets", "HexLive",
            "UnityPresentation", "Environment", "BlueprintArchitectureFactory.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                    "if (!ModelPrefabs.TryGetValue(definitionId, out var prefab) || prefab == null)"),
                "Промах кэша обязан включать уже записанный null, иначе повтор не случится.");
            Assert.That(source, Does.Contain(
                    "if (prefab != null)\n                {\n                    ModelPrefabs[definitionId] = prefab;"),
                "В кэш кладётся только реально приехавший префаб; безусловная " +
                "запись возвращает отравление кэша null-ом (bug #238).");
        });
    }

    [Test]
    public void PrewarmAsksForArchitectureModulesUnderTheirPublishedContentType()
    {
        var source = SourceText.Read(Path.Combine(RepoPaths.Root, "Assets", "HexLive",
            "UnityPresentation", "Wearing", "ScenePrewarm.cs"));
        var loader = SourceText.Read(Path.Combine(RepoPaths.Root, "Assets", "HexLive",
            "UnityPresentation", "Content", "AtomicResources.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("definitionId.StartsWith(\"architecture.\""),
                "Архитектурный модуль публикуется как building, а не как object.");
            // Ранний pre-wind проход классифицирует до прихода реестра и обязан
            // вести architecture. в ту же семью building, что и основной путь.
            Assert.That(source, Does.Contain("id.StartsWith(\"architecture.\""),
                "Pre-wind проход разошёлся с основной классификацией.");
            Assert.That(source, Does.Contain("WarmOwnerMain(\"building\", id)"),
                "Pre-wind обязан греть архитектуру как building.");
            Assert.That(source, Does.Not.Contain("? \"building\" : \"object\""),
                "Старая двухветочная классификация без architecture. вернулась.");
            Assert.That(loader, Does.Contain("tail.StartsWith(\"architecture.\", StringComparison.Ordinal)"),
                "AtomicResources — эталон этой классификации; гейт держит их вместе.");
        });
    }
}

}
