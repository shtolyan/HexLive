using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class SnapshotMovementPresentationContractTests
{
    [Test]
    public void NpcInterpolationUsesPassableSnapshotPolyline()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(renderer, Does.Contain("SnapshotMovementRoute.Build("));
            Assert.That(renderer, Does.Contain("SnapshotMovementRoute.Evaluate(route, alpha)"));
            Assert.That(renderer, Does.Contain("RouteKind.Disconnected"));
        });
    }

    [Test]
    public void GroundSleeperCannotFallBackToNearestBed()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));
        var start = renderer.IndexOf(
            "private Transform? FindBedAttachPoint", StringComparison.Ordinal);
        var end = renderer.IndexOf(
            "private bool TryGetFurnitureSeatPose", start, StringComparison.Ordinal);

        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var method = renderer.Substring(start, end - start);

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("npc.TargetObjectId is { } sleepTargetId"));
            Assert.That(method, Does.Contain("HexSpatialMath.HexDistance(worldObject.Tile, npc.Tile) <= 1"));
            Assert.That(method, Does.Contain("if (bed is null ||"));
            Assert.That(method, Does.Not.Contain("bestSq"),
                "a sleeper without TargetObjectId must stay at her snapshot position");
            Assert.That(method, Does.Not.Contain("HexSpatialMath.Distance("),
                "presentation must not guess a nearest bed from visual distance");
        });
    }
}
