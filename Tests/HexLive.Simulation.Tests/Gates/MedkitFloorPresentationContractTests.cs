using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class MedkitFloorPresentationContractTests
{
    [Test]
    public void LooseObjectsInsideHutUseRaisedFloorSurface()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));
        var methodStart = renderer.IndexOf("private float ObjectGroundY(",
            System.StringComparison.Ordinal);
        var methodEnd = renderer.IndexOf("// §40.18-B", methodStart,
            System.StringComparison.Ordinal);

        Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(methodEnd, Is.GreaterThan(methodStart));
        var objectGroundY = renderer.Substring(methodStart, methodEnd - methodStart);
        Assert.Multiple(() =>
        {
            Assert.That(objectGroundY, Does.Contain("_floorTiles.Contains(worldObject.Tile)"));
            Assert.That(objectGroundY, Does.Contain("HutAssembly.FloorSurfaceLift"));
        });
    }
}
