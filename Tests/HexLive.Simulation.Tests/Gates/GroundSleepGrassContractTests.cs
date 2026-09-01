using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Spec §20 / bug #342: only ground sleep suppresses grass.</summary>
public sealed class GroundSleepGrassContractTests
{
    private static readonly string RendererPath = Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
        "HexWorldRenderer.cs");

    [Test]
    public void GroundSleepPredicateExcludesEveryOtherPronePoseAndBedSleep()
    {
        var source = File.ReadAllText(RendererPath);
        var start = source.IndexOf(
            "internal static bool ShouldHideGrassForGroundSleep", StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void UpdateGrassFlattening", start, StringComparison.Ordinal);
        var predicate = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            Assert.That(predicate, Does.Contain("npc.CurrentInteraction == \"Sleep\""));
            Assert.That(predicate, Does.Contain("npc.ExecutionStatus == \"InProgress\""));
            Assert.That(predicate, Does.Contain("npc.TargetObjectId is null"),
                "A sleep with an exact bed target must not hide terrain grass.");
            Assert.That(predicate, Does.Not.Contain("PostureHint"),
                "Crawl must leave grass visible.");
            Assert.That(predicate, Does.Not.Contain("IsFainted"));
            Assert.That(predicate, Does.Not.Contain("IsCrying"));
            Assert.That(predicate, Does.Not.Contain("IsUnconscious"));
        });
    }

    [Test]
    public void GrassUpdaterUsesOnlyGroundSleepWhilePreservingFloorSuppression()
    {
        var source = File.ReadAllText(RendererPath);
        var start = source.IndexOf(
            "private void UpdateGrassFlattening", StringComparison.Ordinal);
        var end = source.IndexOf("private void HideGrassOn", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("ShouldHideGrassForGroundSleep(npc)"));
            Assert.That(method, Does.Not.Contain("IsLyingDown(npc)"));
            Assert.That(method, Does.Not.Contain("ContentIds.CorpseNpc"));
            Assert.That(method, Does.Not.Contain("ContentIds.HumanRemains"));
            Assert.That(method, Does.Contain("HideGrassOn(_groundSleepTiles)"));
            Assert.That(method, Does.Contain("HideGrassOn(_floorTiles)"));
            Assert.That(method, Does.Contain("HideGrassOn(_architectureFloorTiles)"));
        });
    }
}
