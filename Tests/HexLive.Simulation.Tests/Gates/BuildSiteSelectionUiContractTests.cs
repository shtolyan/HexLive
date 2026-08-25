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
        Assert.That(input, Does.Contain("var objectId = view.ContextObjectId"));
        Assert.That(view, Does.Contain("public void SetContextProxy"));
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

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}
