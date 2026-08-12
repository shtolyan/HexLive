using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Keeps the aspect-fit inventory portrait on one continuous studio surface.
/// The Unity presentation assembly itself is verified by source contract here.
/// </summary>
public sealed class InventoryDollPaneLayoutContractTests
{
    private static string CharacterPanelPath => Path.Combine(
        RepoPaths.Root,
        "Assets",
        "HexLive",
        "UnityPresentation",
        "UI",
        "CharacterPanel.cs");

    [Test]
    public void OuterPaneOwnsTheStudioCardAndInnerPreviewKeepsOnlyTheTexture()
    {
        var source = File.ReadAllText(CharacterPanelPath);
        var buildStart = source.IndexOf("private void BuildInventoryWindow()", StringComparison.Ordinal);
        var fitStart = source.IndexOf("private void FitInventoryWindow()", buildStart,
            StringComparison.Ordinal);
        Assert.That(buildStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(fitStart, Is.GreaterThan(buildStart));
        var build = source[buildStart..fitStart];

        Assert.Multiple(() =>
        {
            Assert.That(build, Does.Contain(
                "_invDollPane.style.backgroundColor = InventoryDollBackdrop"));
            Assert.That(build, Does.Contain("SetBorder(_invDollPane, Stroke, 1f)"));
            Assert.That(build, Does.Contain("SetRadius(_invDollPane, 13f)"));
            Assert.That(build, Does.Contain("_invDollPane.style.overflow = Overflow.Hidden"));
            Assert.That(build, Does.Contain(
                "_invPreviewView.style.backgroundColor = InventoryDollBackdrop"));
            Assert.That(build, Does.Not.Contain("SetBorder(_invPreviewView"),
                "A narrow inner border recreates the two empty side gutters.");
            Assert.That(build, Does.Not.Contain("SetRadius(_invPreviewView"));
            Assert.That(build, Does.Contain("BackgroundSizeType.Contain"),
                "Removing the gutters must never crop or stretch the doll.");
        });
    }
}
