using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

public sealed class WardrobeTestIconContractTests
{
    [Test]
    public void WardrobeTestUsesSourceIconsAndRefreshesAsyncFallbacks()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "WardrobeTest", "WardrobeTestBootstrap.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "$\"Assets/HexLiveContent/Icons/{id}.png\""));
            Assert.That(source, Does.Contain(
                "UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>"));
            Assert.That(source, Does.Contain(
                "PopulateItemIcon(iconBox, entry.Group"));
            Assert.That(source, Does.Contain(
                "PopulateItemIcon(iconBox, def.id"));
            Assert.That(source, Does.Contain("image.schedule.Execute"));
        });
    }

    [Test]
    public void AuthoredWardrobeIconsAreImportedAsSprites()
    {
        var root = Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "Icons");
        var icons = Directory.GetFiles(root, "*.png");

        Assert.That(icons.Length, Is.GreaterThan(600),
            "Wardrobe authoring should not silently fall back to glyph-only cards.");
        Assert.That(icons.Select(path => File.ReadAllText(path + ".meta")),
            Is.All.Contains("textureType: 8"));
    }
}

}
