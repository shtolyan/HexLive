using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class VegetationWindContractTests
{
    [Test]
    public void PalmsKeepStableRandomYawAndOneWindLoopMovesTreesAndGrass()
    {
        var renderer = SourceText.Read(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Rendering", "HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(renderer, Does.Contain(
                "DeterministicVegetationYaw(worldObject.Id.Value)"));
            Assert.That(renderer, Does.Contain(
                "var palmRoot = new GameObject($\"Object {worldObject.DefinitionId}\")"));
            Assert.That(renderer, Does.Contain(
                "palm.transform.SetParent(palmRoot.transform, false)"));
            Assert.That(renderer, Does.Contain(
                "RegisterVegetationWind(\n                    palmRoot.transform"));
            Assert.That(renderer, Does.Not.Contain(
                "palm.transform.localRotation = Quaternion.Euler"));
            Assert.That(renderer, Does.Contain(
                "RegisterVegetationWind(\n            go.transform"));
            Assert.That(renderer, Does.Contain("UpdateVegetationWind();"));
            Assert.That(renderer, Does.Contain("UnregisterVegetationWind(view.transform);"));
            Assert.That(renderer, Does.Contain("0 => 0.22f"));
            Assert.That(renderer, Does.Contain("1 => 0.55f"));
            Assert.That(renderer, Does.Contain("_ => 1f"));
        });
    }
}
