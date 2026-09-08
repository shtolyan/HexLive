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
    public void OccupiedHangerTemplateBakesTheWardrobeBasisAndRuntimeAddsNoAngle()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Assets")))
            root = Directory.GetParent(root)?.FullName;

        Assert.That(root, Is.Not.Null, "project root");
        var exporter = File.ReadAllText(Path.Combine(root!,
            "Tools/blender/export_wardrobe_module.py"));
        var factory = File.ReadAllText(Path.Combine(root,
            "Assets/HexLive/UnityPresentation/Environment/WardrobeHangerFactory.cs"));
        var renderer = File.ReadAllText(Path.Combine(root,
            "Assets/HexLive/UnityPresentation/Rendering/HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            // #349: the source hanger root owns +90° across the wardrobe rail
            // and an authored width scale. Export both into clean template mesh
            // data; taking hanger_source.inverse() erased them and swapped X/Z.
            Assert.That(exporter, Does.Contain(
                "hanger_to_wardrobe = inverse @ hanger_source.matrix_world"));
            Assert.That(exporter, Does.Contain(
                "Matrix.Translation(-hanger_origin) @ inverse @ source.matrix_world"));
            Assert.That(exporter, Does.Not.Contain(
                "hanger_inverse = hanger_source.matrix_world.inverted()"));
            Assert.That(exporter, Does.Contain(
                "socket.matrix_basis = Matrix.Translation(hanger_local.translation)"));

            // The real production chain clones that normalized template and
            // seats it at the authored socket. Neither layer may add a private
            // 30°/90° gameplay compensation after the asset is repaired.
            Assert.That(factory, Does.Contain("model.transform.localRotation = Quaternion.identity"));
            Assert.That(renderer, Does.Contain("hanger.transform.localRotation = Quaternion.identity"));
            Assert.That(renderer, Does.Not.Match(
                @"hanger\.transform\.localRotation\s*=\s*Quaternion\.Euler"));
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
