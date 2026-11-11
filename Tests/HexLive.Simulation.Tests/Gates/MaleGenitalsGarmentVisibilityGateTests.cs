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
    public void PelvisGarmentsUseAnIndependentVisibilitySettingInEveryClothingLayer_Bug685()
    {
        var sourcePath = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
            "BodyBones.cs");
        var source = File.ReadAllText(sourcePath);
        var wear = File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourcePath)!, "Wear.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "ClothingLayerCoversGenitals(VisualWearLayer.Underwear)"));
            Assert.That(source, Does.Contain(
                "ClothingLayerCoversGenitals(VisualWearLayer.Wear)"));
            Assert.That(source, Does.Contain(
                "ClothingLayerCoversGenitals(VisualWearLayer.Outerwear)"));
            Assert.That(source, Does.Contain("wear.HidesGenitals"));
            Assert.That(wear, Does.Contain("CoversPelvis => slots.Contains(VisualWearSlot.Pelvis)"));
            Assert.That(wear, Does.Contain("HidesGenitals => CoversPelvis && !showGenitals"));
            Assert.That(wear, Does.Contain("SetHidesGenitals(bool value) => showGenitals = !value"));
        });
    }
}
