using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§119.1 / bug #137: crafting reuses real prop art and size.</summary>
public sealed class CraftProjectVisualContractTests
{
    private static string EnvironmentFile(string name) => Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Environment", name);

    [Test]
    public void PlayerSafePropBuildUsesOnlyItsAuthoredOwner()
    {
        var source = File.ReadAllText(EnvironmentFile("WorldPropResources.cs"));
        var methodStart = source.IndexOf("public static GameObject? Build", StringComparison.Ordinal);
        var method = source[methodStart..];

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("var prefab = Load(id)"));
            Assert.That(method, Does.Contain("ObjectFit.HasRenderableGeometry(instance)"));
            Assert.That(method, Does.Contain("return null"));
            Assert.That(method, Does.Not.Contain("LowPolyToolFactory"));
            Assert.That(method, Does.Not.Contain("CreatePrimitive"));
        });
    }

    [Test]
    public void CraftIngredientsUseTheSharedWorldAndHandSize()
    {
        var source = File.ReadAllText(EnvironmentFile("CraftProjectVisual.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("WorldPropResources.Load(ids[i])"));
            Assert.That(source, Does.Contain("Object.Instantiate(prefabs[i])"));
            Assert.That(source, Does.Not.Contain("WorldPropResources.Build(ids[i])"));
            Assert.That(source, Does.Contain("ObjectFit.FitScaleFactor(model, ids[i])"));
            Assert.That(source, Does.Not.Contain("0.16f / horizontal"));
        });
    }

    [Test]
    public void CraftOutputKeepsTheWorldDropScale()
    {
        var source = File.ReadAllText(EnvironmentFile("CraftProjectVisual.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Not.Contain("0.24f / current"));
            Assert.That(source, Does.Not.Contain("_outputSized"));
        });
    }
}

}
