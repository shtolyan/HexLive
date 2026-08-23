using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class MaleGenitalsGarmentVisibilityGateTests
{
    [Test]
    public void WaistStrapsOccupyPelvisWithoutCoveringTheBody_Bug210()
    {
        var prefabPath = Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "Wear",
            "FCO Waist Strappy Male", "FCO Waist Strappy Male.prefab");
        var prefab = File.ReadAllText(prefabPath);

        Assert.Multiple(() =>
        {
            Assert.That(prefab, Does.Contain("slots: 0c000000"),
                "The straps still need the Pelvis slot for outfit conflicts.");
            Assert.That(prefab, Does.Contain("noHideUnderwearSlots: 0c000000"),
                "VisualWearSlot.Pelvis (12) must remain visibly open under the straps.");
        });
    }

    [Test]
    public void GenitalsUseTheAuthoredPelvisMaskForClothingLayers_Bug210()
    {
        var sourcePath = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
            "BodyBones.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "_byLayer[VisualWearLayer.Underwear].ContainsKey(VisualWearSlot.Pelvis)"),
                "Pelvis underwear must always cover the body.");
            Assert.That(source, Does.Contain(
                "ClothingLayerCoversGenitals(VisualWearLayer.Wear)"));
            Assert.That(source, Does.Contain(
                "ClothingLayerCoversGenitals(VisualWearLayer.Outerwear)"));
            Assert.That(source, Does.Contain(
                "wear.HeedHideUnderwearSlot(VisualWearSlot.Pelvis)"),
                "Wear and Outerwear must share their authored lower-layer visibility mask.");
        });
    }
}
