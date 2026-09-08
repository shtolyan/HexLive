using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§20/§152: a hand prop may only use its exact atomic owner.</summary>
public sealed class HandPropFallbackContractTests
{
    [Test]
    public void AuthoredGearWaitsForItsAtomicGripBeforeInstantiation()
    {
        var actor = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "NpcActorView.cs"));
        var start = actor.IndexOf(
            "private void SetHandProp(string itemId)", StringComparison.Ordinal);
        var end = actor.IndexOf("private void SyncHandedness", start, StringComparison.Ordinal);
        var method = actor[start..end];
        var request = method.IndexOf("Config.GearLibrary.ConfigFor(itemId)",
            StringComparison.Ordinal);
        var wait = method.IndexOf("Config.GearLibrary.RequiresAuthoredConfig(itemId)",
            StringComparison.Ordinal);
        var model = method.IndexOf("Config.GearLibrary.LoadPrefab(itemId)",
            StringComparison.Ordinal);

        var library = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Config", "GearTuning.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(request, Is.GreaterThanOrEqualTo(0));
            Assert.That(wait, Is.GreaterThan(request));
            Assert.That(model, Is.GreaterThan(wait),
                "Known gear must not be instantiated before its authored grip is ready.");
            Assert.That(library, Does.Match(
                @"AtomicResources\.Load<GearConfig>\(\s*""HexLive/Objects/""\s*\+\s*gearId\)"),
                "Atomic GearConfig ownership must not depend on source indentation.");
            Assert.That(library, Does.Contain("GearCatalog.Defaults.ContainsKey(gearId)"));
        });
    }

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
