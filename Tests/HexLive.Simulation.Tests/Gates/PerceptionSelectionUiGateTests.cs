using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class PerceptionSelectionUiGateTests
{
    private static string Ui(string folder, string file) => Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", folder, file);

    [Test]
    public void EveryPersonSelectionPathUsesLivePerceptionAndPrunesLostTargets()
    {
        var adapter = File.ReadAllText(Ui("Input", "SimulationInputAdapter.cs"));
        var camera = File.ReadAllText(Ui("Input", "RtsCameraController.cs"));
        var panel = File.ReadAllText(Ui("UI", "CharacterPanel.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("internal bool CanTargetPerson"));
            Assert.That(adapter, Does.Contain("_worldRenderer.IsNpcPickable"));
            Assert.That(camera, Does.Contain("if (!CanTargetPerson(npc)"),
                "Exact geometry and screen-radius picking must reject invisible people.");
            Assert.That(camera, Does.Contain("PruneInvisibleSelection(snapshot)"));
            Assert.That(camera, Does.Contain(
                "NpcSelection.ReplaceMany(_visibleSelectionScratch, requestFrame: false)"));
            Assert.That(panel, Does.Contain("_worldRenderer.IsNpcPickable"),
                "The roster must not provide a side door to an invisible outsider.");
        });
    }
}
