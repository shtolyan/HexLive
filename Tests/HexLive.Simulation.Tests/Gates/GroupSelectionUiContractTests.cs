using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class GroupSelectionUiContractTests
    {
        private static string Presentation(params string[] parts) =>
            Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                Path.Combine(parts));

        [Test]
        public void SelectionIsOrderedAndArrowKeysOnlyRotateTheCamera()
        {
            var selection = File.ReadAllText(Presentation("Input", "NpcSelection.cs"));
            var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));
            Assert.Multiple(() =>
            {
                Assert.That(selection, Does.Contain("IReadOnlyList<int> SelectedIds"));
                Assert.That(selection, Does.Contain("void ReplaceMany"));
                Assert.That(selection, Does.Contain("void AddMany"));
                Assert.That(selection, Does.Contain("void Toggle"));
                Assert.That(selection, Does.Contain("Selected.ToArray()"));
                Assert.That(camera, Does.Contain("SelectionDragThresholdPixels = 6f"));
                Assert.That(camera, Does.Contain("HandleKeyboardRotation"));
                Assert.That(camera, Does.Contain("leftArrowKey.isPressed"));
                Assert.That(camera, Does.Not.Contain("CycleOrbitTarget"));
                Assert.That(camera, Does.Not.Contain("BuildOrbitRoster"));
            });
        }

        [Test]
        public void RosterGroupPanelAndReadOnlyOutsiderAreExplicitContracts()
        {
            var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
            var adapter = File.ReadAllText(Presentation("Input", "SimulationInputAdapter.cs"));
            Assert.Multiple(() =>
            {
                Assert.That(panel, Does.Contain("BuildRoster()"));
                Assert.That(panel, Does.Contain("BuildGroupCard()"));
                Assert.That(panel, Does.Contain("Time.unscaledTime * Mathf.PI * 4f"));
                Assert.That(panel, Does.Contain("panel.control.mixed"));
                Assert.That(panel, Does.Contain("inv.readonly"));
                Assert.That(panel, Does.Contain("ManageInventoryCommand"));
                Assert.That(panel, Does.Contain("BeginInventoryDrag"));
                Assert.That(panel, Does.Contain("inv.drop_zone"));
                Assert.That(adapter, Does.Contain("new GroupMoveCommand"));
                Assert.That(adapter, Does.Contain("menu.select_one_character"));
            });
        }

        [Test]
        public void CameraHasFrameFollowDetachAndUiAwareFit()
        {
            var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));
            Assert.Multiple(() =>
            {
                Assert.That(camera, Does.Contain("EnterOrbitSelection"));
                Assert.That(camera, Does.Contain("TryGetSelectionFrame"));
                Assert.That(camera, Does.Contain("NpcSelection.RightUiCoverage"));
                Assert.That(camera, Does.Contain("Mathf.Max(_requestedDistance, fit)"));
                Assert.That(camera, Does.Contain("if (HasPanInput())"));
                Assert.That(camera, Does.Contain("ExitOrbit();"));
            });
        }
    }
}
