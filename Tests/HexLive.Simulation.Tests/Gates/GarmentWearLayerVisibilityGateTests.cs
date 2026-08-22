using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class GarmentWearLayerVisibilityGateTests
{
    private static string Presentation(params string[] parts) =>
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            Path.Combine(parts));

    [Test]
    public void WearPrefabOwnsAnOptInOuterwearMask()
    {
        var source = File.ReadAllText(Presentation("Wearing", "Wear.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "private List<VisualWearSlot> hideWearSlots = new();"),
                "The serialized default must keep lower clothing visible.");
            Assert.That(source, Does.Contain("public bool HidesWearSlot("));
            Assert.That(source, Does.Contain("public void SetHideWear("));
            Assert.That(source, Does.Contain(
                "public IReadOnlyList<VisualWearSlot> HideWearSlots"));
        });
    }

    [Test]
    public void RuntimeAndInventoryDollComposeTheSameOuterwearMask()
    {
        var body = File.ReadAllText(Presentation("Wearing", "BodyBones.cs"));
        var doll = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("private void RefreshWearVisibility()"));
            Assert.That(body, Does.Contain("outerwear.HidesWearSlot(slot)"));
            Assert.That(body.Split("RefreshWearVisibility();").Length - 1,
                Is.GreaterThanOrEqualTo(3),
                "Equip, take-off and debug restore must all rebuild the mask.");
            Assert.That(doll, Does.Contain("lowerWear.Layer != VisualWearLayer.Wear"));
            Assert.That(doll, Does.Contain("outerwear.Layer != VisualWearLayer.Outerwear"));
            Assert.That(doll, Does.Contain("outerwear.HidesWearSlot(slot)"));
        });
    }

    [Test]
    public void WardrobeTestAuthorsAndReextractPreservesTheMask()
    {
        var wardrobe = File.ReadAllText(Presentation(
            "WardrobeTest", "WardrobeTestBootstrap.cs"));
        var extractor = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Editor", "NewWearExtractor.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(wardrobe, Does.Contain("wardrobe.hides_wear"));
            Assert.That(wardrobe, Does.Contain("ToggleHideWear"));
            Assert.That(wardrobe, Does.Contain("RebuildHideWearRow"));
            Assert.That(wardrobe, Does.Contain("entry.Asset.SetHideWear"));
            Assert.That(wardrobe, Does.Contain("MarkDirtyAndRedress(entry)"));
            Assert.That(extractor, Does.Contain("tunedWear.HideWearSlots.ToArray()"));
            Assert.That(localization, Does.Contain("Term: wardrobe.hides_wear"));
        });
    }
}
