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
            Assert.That(objectGroundY, Does.Contain("!OwnsRaisedFloorGeometry(worldObject)"));
            Assert.That(objectGroundY, Does.Contain("HutAssembly.FloorSurfaceLift"));
        });
    }

    [Test]
    public void ArchitectureOwnsItsLocalFloorRiseAndIsNotLiftedTwice()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));
        var helperStart = renderer.IndexOf("private static bool OwnsRaisedFloorGeometry(",
            System.StringComparison.Ordinal);
        var helperEnd = renderer.IndexOf("// §40.18-B", helperStart,
            System.StringComparison.Ordinal);

        Assert.That(helperStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(helperEnd, Is.GreaterThan(helperStart));
        var helper = renderer.Substring(helperStart, helperEnd - helperStart);
        Assert.Multiple(() =>
        {
            Assert.That(helper, Does.Contain("ContentIds.Hut1Hex"));
            Assert.That(helper, Does.Contain("ArchitectureOwnerObjectId.HasValue"));
        });
    }
}
