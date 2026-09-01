using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Spec §20 / bug #341: the bottle has one compact hand/world size.</summary>
public sealed class BottleObjectFitContractTests
{
    private const float BottleTargetInHexRadii = 0.036f;
    private const float BottleTargetWorldUnits = 0.054f;

    [Test]
    public void BottleTargetIsTheMeasuredCompactSize()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "ObjectFit.cs"));
        var match = Regex.Match(source,
            "if \\(definitionId == \\\"tool\\.bottle\\\"\\) return r \\* " +
            "(?<factor>[0-9]+(?:\\.[0-9]+)?)f;");

        Assert.That(match.Success, Is.True,
            "tool.bottle must keep an explicit target in the shared ObjectFit table.");
        var factor = float.Parse(match.Groups["factor"].Value, CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(factor, Is.EqualTo(BottleTargetInHexRadii).Within(0.000001f));
            Assert.That(factor * HexSpatialMath.HexRadius,
                Is.EqualTo(BottleTargetWorldUnits).Within(0.000001f),
                "The bottle regressed from its player-approved 0.054-world-unit size.");
        });
    }

    [Test]
    public void BottleHasNoHandOnlyAbsoluteScale()
    {
        var gear = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "RuntimeSource", "Gear", "bottle.asset"));
        var actor = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs"));
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering", "HexWorldRenderer.cs"));
        var fallbackStart = actor.IndexOf(
            "private static bool TryGetHandPropTransform", StringComparison.Ordinal);
        var fallbackEnd = actor.IndexOf(
            "private static bool TryGetLeftHandPropTransform", fallbackStart, StringComparison.Ordinal);
        var fallbackTable = actor[fallbackStart..fallbackEnd];

        Assert.Multiple(() =>
        {
            Assert.That(gear, Does.Contain("handLocalScale: {x: 1, y: 1, z: 1}"),
                "Bottle grip scale must remain a multiplier over ObjectFit.");
            Assert.That(actor, Does.Contain(
                "ApplyObjectFitScale(_handProp, itemId, prefabLocalScale, cfgScale)"),
                "The authored hand path must normalize through the same ObjectFit target.");
            Assert.That(renderer, Does.Contain(
                "instance.transform.localScale *= ObjectFit.FitScaleFactor(instance, definitionId)"),
                "The ground path must normalize through the same ObjectFit target.");
            Assert.That(renderer, Does.Contain(
                "FitObjectPrefab(instance, worldObject.DefinitionId, worldObject.Id.Value"),
                "Atomic world props must pass through the shared ground fitter.");
            Assert.That(fallbackTable, Does.Not.Contain("case \"tool.bottle\":"),
                "Do not restore the retired hand-only absolute bottle scale.");
        });
    }
}
