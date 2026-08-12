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
}
