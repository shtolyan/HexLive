using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Source gate for the HP window layout. The presentation assembly is owned by
/// Unity, so the simulation test project verifies its non-scrolling geometry
/// contract without instantiating UI Toolkit.
/// </summary>
public sealed class HealthPanelLayoutContractTests
{
    private static string CharacterPanelPath => Path.Combine(
        RepoPaths.Root,
        "Assets",
        "HexLive",
        "UnityPresentation",
        "UI",
        "CharacterPanel.cs");

    [Test]
    public void HealthCardsFillTheDollHeightWithoutATrailingGap()
    {
        var source = File.ReadAllText(CharacterPanelPath);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private const float HealthWindowWidth = 540f"));
            Assert.That(source, Does.Contain("body.style.alignItems = Align.Stretch"));
            Assert.That(source, Does.Contain(
                "list.style.height = DollViewportWidth * DollAspectHeight"));
            Assert.That(source, Does.Contain("list.style.marginLeft = HealthColumnGap"));
            Assert.That(source, Does.Contain("row.style.flexGrow = 1f"));
            Assert.That(source, Does.Contain("row.style.flexBasis = 0f"));
            Assert.That(source, Does.Contain("row.style.backgroundColor = PanelMid"));
            Assert.That(source, Does.Contain(
                "if (zoneIndex + 1 < CharacterDollStage.ZoneOrder.Length)"),
                "Only gaps between cards are allowed; the last row must meet the viewport edge.");
            Assert.That(source, Does.Not.Contain("list.style.justifyContent = Justify.Center"),
                "Centering the old compact list creates a large dead band beside the taller doll.");
        });
    }
}
