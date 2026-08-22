using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class InventoryWindowSizingGateTests
{
    [Test]
    public void InventoryWindowFitsFiveRowsAndLetsTheGridMeasureAvailableHeight()
    {
        var panel = File.ReadAllText(Path.Combine(
            RepoPaths.Root,
            "Assets",
            "HexLive",
            "UnityPresentation",
            "UI",
            "CharacterPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("InventoryWindowMaxHeight = 700f"),
                "Five 106px rows plus their gaps, header and tabs need more than 620px.");
            Assert.That(panel, Does.Contain(
                "_inventoryWindow.style.height = InventoryWindowMaxHeight"));
            Assert.That(panel, Does.Contain(
                "InventoryWindowMaxHeight, Mathf.Max(300f, availableHeight)"),
                "Small screens must still clamp the window to their available height.");
            Assert.That(panel, Does.Contain("_invItemsPane.style.minHeight = 0f"),
                "The grid pane must report the clipped row height so density fallback can run.");
        });
    }
}
