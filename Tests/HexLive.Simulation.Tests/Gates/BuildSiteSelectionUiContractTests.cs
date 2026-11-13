using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§121.1 / bug #198: unfinished construction stays selectable.</summary>
public sealed class BuildSiteSelectionUiContractTests
{
    [Test]
    public void FreshStakeGeometryRegistersTheAuthoritativeBuildSiteView()
    {
        var source = Read("Environment", "BuildSiteStakeRenderer.cs");

        Assert.That(source, Does.Contain("AddComponent<Views.WorldObjectView>()"));
        Assert.That(source, Does.Contain(".Init(site.Id.Value, site.DefinitionId)"));
    }

    [Test]
    public void VisibleModulesProxyBuildMenuToTheirUnfinishedPlanOwner()
    {
        var renderer = Read("Rendering", "HexWorldRenderer.cs");
        var input = Read("Input", "SimulationInputAdapter.cs");
        var view = Read("Views", "WorldObjectView.cs");

        Assert.That(renderer, Does.Contain("_unfinishedPlanBuildingOwners.Contains(ownerId)"));
        Assert.That(renderer, Does.Contain("SetContextProxy(ownerId, ContentIds.BuildSite)"));
        Assert.That(renderer, Does.Contain("ClearContextProxy()"));
        Assert.That(input, Does.Contain("view.ContextDefinitionId"));
        // #749: «Строить» addresses the clicked module itself; the simulation
        // redirects it to the footprint owner and walks the builder to that piece.
        Assert.That(input, Does.Contain(
            "var objectId = interaction.Type == InteractionType.Build ? view.ObjectId : view.ContextObjectId"));
        Assert.That(view, Does.Contain("public void SetContextProxy"));
    }

    // #766: a half-built chest (4 sticks delivered) and a torch (rope, cloth)
    // were invisible outside build mode: the pile drew only stones, logs and
    // leaves, and the stakes had left with the first delivery.
    [Test]
    public void ASiteWithoutAGrowingModelKeepsItsOutlineAndPilesEveryMaterial()
    {
        var renderer = Read("Environment", "BuildSiteStakeRenderer.cs");
        var pile = Read("Environment", "BuildSitePile.cs");

        Assert.Multiple(() =>
        {
            Assert.That(renderer, Does.Contain(
                "if (!BuildSitePile.GrowsInPlace(site.BuildProduct)) return true;"),
                "Изделие без собственной растущей модели держит колышки до постройки.");
            Assert.That(renderer.IndexOf("BuildSitePile.GrowsInPlace", System.StringComparison.Ordinal),
                Is.LessThan(renderer.IndexOf("return !BuildSiteStakeVisibility.HasPhysicalProgress(site, modules)",
                    System.StringComparison.Ordinal)),
                "Проверка стоит ДО правила «первый материал убирает колышки».");
            Assert.That(pile, Does.Contain("public static bool GrowsInPlace(string product)"));
            Assert.That(pile, Does.Contain("BedAssembly.IsAssembled(product)"),
                "Видимым «по умолчанию» изделие делает отсутствие в этом списке, а не присутствие.");
            foreach (var material in new[] { "DeliveredStones", "DeliveredLogs", "DeliveredLeaves",
                         "DeliveredSticks", "DeliveredBoards" })
                Assert.That(pile, Does.Contain("site." + material + ", placed)"),
                    material + " не рисуется в куче площадки.");
        });
    }

    [Test]
    public void ConstructionStakesDisappearAfterTheFirstPhysicalProgress()
    {
        var renderer = Read("Environment", "BuildSiteStakeRenderer.cs");
        var rule = Read("Environment", "BuildSiteStakeVisibility.cs");

        Assert.Multiple(() =>
        {
            Assert.That(renderer,
                Does.Contain("BuildSiteStakeVisibility.HasPhysicalProgress"));
            Assert.That(rule, Does.Contain("element.DeliveredTotal > 0"));
            Assert.That(rule, Does.Contain("element.WorkDone > 0"));
            Assert.That(rule, Does.Not.Contain("element.Complete"),
                "Колышки не должны ждать полного завершения элемента.");
        });
    }

    [Test]
    public void StreamedPartialFurnitureRefreshesItsPickingGeometry()
    {
        var pile = Read("Environment", "BuildSitePile.cs");
        var view = Read("Views", "WorldObjectView.cs");

        Assert.Multiple(() =>
        {
            Assert.That(pile, Does.Contain(".RefreshGeometry()"),
                "Когда prefab кровати догрузился, build-site обязан обновить hit surface.");
            Assert.That(view, Does.Contain("public void RefreshGeometry()"));
            Assert.That(view, Does.Contain("GetComponentsInChildren<Renderer>(true)"));
            Assert.That(view, Does.Contain("GetComponentsInChildren<WorldObjectPickBounds>(true)"));
        });
    }

    [Test]
    public void StreamedPlanModuleRefreshesItsPickingGeometry()
    {
        var module = Read("Environment", "ArchitectureModuleView.cs");

        // Bug #282: площадка plan-дома не имеет своей геометрии (BuildSitePile
        // пропускает HutPlan), кликабельное тело дома — модули, чьи префабы
        // приходят из асинхронного бандла ПОСЛЕ Init вида. Модуль обязан
        // обновлять hit surface, когда модель наконец построена.
        Assert.That(module, Does.Contain(".RefreshGeometry()"),
            "Когда prefab модуля догрузился, дом обязан обновить hit surface.");
    }

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}
