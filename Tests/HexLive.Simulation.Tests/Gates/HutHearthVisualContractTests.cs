using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class HutHearthVisualContractTests
{
    [Test]
    public void IntegratedHearthCachesItsChildFireEffect()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));
        var furniture = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Environment",
            "HutFurnitureFactory.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(furniture, Does.Contain("new GameObject(\"fire_point\")"));
            Assert.That(renderer, Does.Contain("GetComponentInChildren<"));
            Assert.That(renderer, Does.Contain(
                "HexLive.UnityPresentation.Environment.CampfireEffect>(true)"));
            Assert.That(renderer, Does.Contain("fire.SetLit(worldObject.ResourceAmount > 0f)"));
        });
    }
}
