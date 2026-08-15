using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§74.10 / bug #131: bags overlay the complete outfit.</summary>
public sealed class BackpackVisibilityGateTests
{
    private static string Presentation(params string[] parts) =>
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            Path.Combine(parts));

    [Test]
    public void RuntimeBagsNeverEnterUnderwearOcclusion()
    {
        var source = File.ReadAllText(Presentation("Wearing", "BodyBones.cs"));
        var equipStart = source.IndexOf("public void Equip", StringComparison.Ordinal);
        var hairStart = source.IndexOf("private void RefreshHairVisibility", equipStart,
            StringComparison.Ordinal);
        var equip = source[equipStart..hairStart];

        Assert.Multiple(() =>
        {
            Assert.That(equip, Does.Contain(
                "wearPrefab.Layer is VisualWearLayer.Wear or VisualWearLayer.Outerwear"));
            Assert.That(equip, Does.Not.Contain(
                "wearPrefab.Layer != VisualWearLayer.Underwear"),
                "A broad non-underwear rule accidentally includes the Bags overlay layer.");
        });
    }

    [Test]
    public void InventoryDollAlsoExcludesBagsFromOcclusion()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var filterStart = source.IndexOf("private void ApplyWearLayerVisibility",
            StringComparison.Ordinal);
        var enableStart = source.IndexOf("private void SetStageEnabled", filterStart,
            StringComparison.Ordinal);
        var filter = source[filterStart..enableStart];

        Assert.That(filter, Does.Contain(
            "(int)outer.Layer > (int)VisualWearLayer.Outerwear"),
            "The inventory doll must keep Bags out of underwear occlusion too.");
    }
}

}
