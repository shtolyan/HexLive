using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§20/§152: a hand prop may only use its exact atomic owner.</summary>
public sealed class HandPropFallbackContractTests
{
    [Test]
    public void PendingOrEmptyHandPrefabNeverBecomesAProceduralProp()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "NpcActorView.cs"));
        var start = source.IndexOf(
            "private void SetHandProp(string itemId)", StringComparison.Ordinal);
        var end = source.IndexOf("private void SyncHandedness", start, StringComparison.Ordinal);
        var method = source[start..end];

        var load = method.IndexOf("Config.GearLibrary.LoadPrefab(itemId)",
            StringComparison.Ordinal);
        var validate = method.IndexOf("ObjectFit.HasRenderableGeometry(_handProp)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(validate, Is.GreaterThan(load));
            Assert.That(method, Does.Contain("if (model == null)"));
            Assert.That(method, Does.Not.Contain("LowPolyToolFactory"));
            Assert.That(method, Does.Not.Contain("CreatePrimitive"));
        });
    }
}
