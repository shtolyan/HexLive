using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §150.4 / bug #244: офф-скрин съёмка (импосторы, портреты) идёт на
/// ВЫДЕЛЕННОМ слое PhotoBake, который пуст между синхронными проходами.
/// Portrait — жилой слой живой identity-карты: на нём постоянно висит
/// неоновый задник PortraitStage в 20 wu перед лицом выделенной девушки, и
/// пекарня, снимавшая маской Portrait, запекала его в текстуры импосторов.
/// </summary>
public sealed class PhotoBakeLayerContractTests
{
    [Test]
    public void OffscreenBakersUseTheDedicatedPhotoBakeLayer()
    {
        var impostor = Read("Views", "ObjectImpostor.cs");
        var portraits = Read("UI", "NpcPortraitCache.cs");

        Assert.Multiple(() =>
        {
            Assert.That(impostor, Does.Contain("internal static int PhotoBakeLayer()"));
            Assert.That(impostor, Does.Contain("LayerMask.NameToLayer(\"PhotoBake\")"));
            Assert.That(impostor, Does.Not.Contain("LayerMask.NameToLayer(\"Portrait\")"),
                "Пекарня импосторов не смеет снимать на жилом слое Portrait.");
            Assert.That(portraits, Does.Contain("ObjectImpostor.PhotoBakeLayer()"));
            Assert.That(portraits, Does.Not.Contain("LayerMask.NameToLayer(\"Portrait\")"),
                "Фотограф портретов не смеет снимать на жилом слое Portrait.");
        });
    }

    [Test]
    public void LiveIdentityCardKeepsThePortraitLayerAndItsNeonSet()
    {
        var stage = Read("UI", "PortraitStage.cs");

        Assert.That(stage, Does.Contain("LayerMask.NameToLayer(\"Portrait\")"),
            "Живая identity-карта остаётся на Portrait со своим неоновым задником.");
    }

    [Test]
    public void PhotoBakeLayerIsDeclaredInTheTagManager()
    {
        var tags = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "ProjectSettings", "TagManager.asset"));

        Assert.That(tags, Does.Contain("- PhotoBake"));
    }

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}
