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
}
