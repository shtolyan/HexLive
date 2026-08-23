using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§20 / bug #202: an empty native prefab must not suppress the visible fallback.</summary>
public sealed class HandPropFallbackContractTests
{
    [Test]
    public void EmptyNativeHandPrefabFallsBackToProceduralProp()
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
        var fallback = method.IndexOf("LowPolyToolFactory.Build(itemId)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(validate, Is.GreaterThan(load));
            Assert.That(fallback, Is.GreaterThan(validate));
            Assert.That(method, Does.Contain("if (_handProp == null)"));
        });
    }
}
