using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Headless source gate for the §121 Unity click cadence.</summary>
public sealed class ManualMoveClickUiContractTests
{
    [Test]
    public void GroundSingleClickWalksAndNearbyDoubleClickRuns()
    {
        var path = Path.Combine(
            new[] { RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                "Input", "SimulationInputAdapter.cs" });
        var adapter = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("DoubleClickSeconds = 0.30f"));
            Assert.That(adapter, Does.Contain("DoubleClickRadiusPixels = 18f"));
            Assert.That(adapter, Does.Contain(
                "ConsumeGroundDoubleClick(mousePos, Time.unscaledTime)"));
            Assert.That(adapter, Does.Contain(
                "new MoveToCommand(new EntityId(ManualNpcId), point, run)"));
            Assert.That(adapter, Does.Contain(
                "new GroupMoveCommand(SelectedActors(), point, run)"));
            Assert.That(adapter, Does.Contain("ResetGroundClickCadence();"),
                "A menu/entity click must not complete a stale ground double-click.");
        });
    }

    [Test]
    public void ContextMenuOwnsTheWholePointerSequenceWithoutWorldClickThrough()
    {
        var uiRoot = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation");
        var menu = File.ReadAllText(Path.Combine(uiRoot, "UI", "ContextMenuPanel.cs"));
        var camera = File.ReadAllText(Path.Combine(uiRoot, "Input", "RtsCameraController.cs"));
        var adapter = File.ReadAllText(Path.Combine(uiRoot, "Input", "SimulationInputAdapter.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(menu, Does.Contain("public static bool BlocksWorldPointer"));
            Assert.That(menu, Does.Contain("_worldPointerSuppressed = true;"),
                "Closing an item on MouseDown must keep the world blocked through release.");
            Assert.That(menu, Does.Contain("Time.frameCount > _worldPointerReleaseFrame"),
                "The suppression latch must survive the physical release frame.");
            Assert.That(camera, Does.Contain("UI.ContextMenuPanel.BlocksWorldPointer"));
            Assert.That(camera, Does.Contain(
                "UI.ContextMenuPanel.IsOpen && !UI.ContextMenuPanel.PointerOverPanel"),
                "A click outside an open menu must close only the menu.");
            Assert.That(adapter, Does.Contain("ContextMenuPanel.BlocksWorldPointer"));
        });
    }
}
