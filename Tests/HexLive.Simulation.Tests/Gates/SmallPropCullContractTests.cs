using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §121.1 «невидимое не кликается»: отсечка SmallProps в пикинге обязана
/// читать слой ВИДИМОЙ геометрии вида. SmallProps назначается дочернему
/// FBX-инстансу (SuppressSmallPropShadows → SetLayerRecursive), корень вида
/// остаётся на Default с cull 0 — проверка по слою корня была мёртвым кодом,
/// и задистанс-куленные мелкие пропы оставались наводимыми и кликабельными.
/// </summary>
public sealed class SmallPropCullContractTests
{
    [Test]
    public void CullCheckReadsTheVisibleGeometryLayerNotTheViewRoot()
    {
        var input = Read("Input", "SimulationInputAdapter.cs");
        var view = Read("Views", "WorldObjectView.cs");

        Assert.Multiple(() =>
        {
            Assert.That(input, Does.Contain("view.VisibleGeometryLayer()"),
                "Отсечка обязана мерить слой той геометрии, которую камера " +
                "реально режет.");
            Assert.That(input, Does.Not.Contain("var layer = view.gameObject.layer;"),
                "Слой корня вида — всегда Default (cull 0), проверка по нему " +
                "мертва.");
            Assert.That(view, Does.Contain("public int VisibleGeometryLayer()"));
            Assert.That(view, Does.Contain("forceRenderingOff"),
                "Выключенный квад импостора (§150.4) не смеет подменять слой " +
                "настоящего меша.");
        });
    }

    [Test]
    public void SmallPropLayerGoesToTheRenderedChildren()
    {
        var renderer = Read("Rendering", "HexWorldRenderer.cs");

        Assert.That(renderer,
            Does.Contain("SetLayerRecursive(instance.transform, smallPropLayer)"),
            "SmallProps живёт на детях с renderers — это и есть то, что " +
            "камера дистанционно режет.");
    }

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}
