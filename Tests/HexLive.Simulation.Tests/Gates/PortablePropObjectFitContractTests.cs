using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Specs §20/§118, bug #341: portable props share exact hand/world sizes.</summary>
public sealed class PortablePropObjectFitContractTests
{
    [TestCase("tool.bottle", 0.18f, 0.27f)]
    [TestCase("med.splint", 0.12f, 0.18f)]
    public void PortablePropKeepsItsExactSharedTarget(
        string definitionId,
        float expectedHexRadii,
        float expectedWorldUnits)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "ObjectFit.cs"));
        var prefix = Regex.Escape(
            $"if (definitionId == \"{definitionId}\") return r * ");
        var match = Regex.Match(source,
            prefix + @"(?<factor>[0-9]+(?:\.[0-9]+)?)f;");

        Assert.That(match.Success, Is.True,
            $"{definitionId} must keep an explicit target in the shared ObjectFit table.");
        var factor = float.Parse(match.Groups["factor"].Value, CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(factor, Is.EqualTo(expectedHexRadii).Within(0.000001f));
            Assert.That(factor * HexSpatialMath.HexRadius,
                Is.EqualTo(expectedWorldUnits).Within(0.000001f));
        });
    }

    [Test]
    public void BottleAndSplintUseTheCommonHandAndGroundFitPaths()
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
        var absoluteScaleTable = actor[fallbackStart..fallbackEnd];

        Assert.Multiple(() =>
        {
            Assert.That(gear, Does.Contain("handLocalScale: {x: 1, y: 1, z: 1}"),
                "Bottle grip scale must remain a multiplier over ObjectFit.");
            Assert.That(actor, Does.Contain(
                "ApplyObjectFitScale(_handProp, itemId, prefabLocalScale, cfgScale)"),
                "The authored hand path must normalize through the shared ObjectFit target.");
            Assert.That(actor, Does.Contain(
                "ApplyObjectFitScale(_handProp, itemId, prefabLocalScale, Vector3.one)"),
                "The untuned splint hand path must normalize through the shared ObjectFit target.");
            Assert.That(renderer, Does.Contain(
                "instance.transform.localScale *= ObjectFit.FitScaleFactor(instance, definitionId)"),
                "The ground path must normalize through the shared ObjectFit target.");
            Assert.That(renderer, Does.Contain(
                "FitObjectPrefab(instance, worldObject.DefinitionId, worldObject.Id.Value"),
                "Atomic world props must pass through the shared ground fitter.");
            Assert.That(absoluteScaleTable, Does.Not.Contain("case \"tool.bottle\":"));
            Assert.That(absoluteScaleTable, Does.Not.Contain("case \"med.splint\":"),
                "The splint must not acquire a hand-only absolute scale.");
        });
    }
}
