using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class WardrobeShelfPresentationContractTests
{
    [Test]
    public void OccupiedHangerComesFromTheWardrobeOwnerBundle()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Assets")))
            root = Directory.GetParent(root)?.FullName;

        Assert.That(root, Is.Not.Null, "project root");
        var factory = File.ReadAllText(Path.Combine(root!,
            "Assets/HexLive/UnityPresentation/Environment/WardrobeHangerFactory.cs"));
        var exporter = File.ReadAllText(Path.Combine(root,
            "Tools/blender/export_wardrobe_module.py"));

        Assert.Multiple(() =>
        {
            Assert.That(factory, Does.Contain("WardrobeAssembly.ResourcePath"));
            Assert.That(factory, Does.Contain("HangerTemplate"));
            Assert.That(factory, Does.Not.Contain("CreatePrimitive"));
            Assert.That(exporter, Does.Contain("HangerTemplate"));
        });
    }

    [Test]
    public void FootwearUsesOneBoundsAwareShelfPlacementForCreateAndSync()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Assets")))
            root = Directory.GetParent(root)?.FullName;

        Assert.That(root, Is.Not.Null, "project root");
        var hangers = File.ReadAllText(Path.Combine(root!,
            "Assets/HexLive/UnityPresentation/Environment/WardrobeHangers.cs"));
        var renderer = File.ReadAllText(Path.Combine(root,
            "Assets/HexLive/UnityPresentation/Rendering/HexWorldRenderer.cs"));

        Assert.That(hangers, Does.Contain("GroundFootwearOnShelf"));
        Assert.That(hangers, Does.Contain("bounds.min.y"));
        Assert.That(Regex.Matches(renderer, "GroundFootwearOnShelf\\(").Count,
            Is.EqualTo(2), "Create и per-tick sync обязаны использовать один shelf placement.");
    }
}
