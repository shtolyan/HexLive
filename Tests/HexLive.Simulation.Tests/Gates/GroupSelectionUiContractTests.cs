using System.Collections.Generic;
using System.IO;
using HexLive.UnityPresentation.Input;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class GroupSelectionUiContractTests
    {
        private static string Presentation(params string[] parts) =>
            Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                Path.Combine(parts));

        [Test]
        public void SelectionIsOrderedAndArrowKeysPanLikeWasd()
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
                // Дальний обзор: стрелки панорамируют вместе с WASD, а вращение
                // живёт в собственном обработчике свободной камеры.
                Assert.That(camera, Does.Contain("HandleFreePan"));
                Assert.That(camera, Does.Contain("HandleFreeRotation"));
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
                Assert.That(panel, Does.Contain("name = \"roster-health-ring\""));
                Assert.That(panel, Does.Contain("var hp = Mathf.Clamp01(npc.DisplayHealth)"));
                Assert.That(panel, Does.Contain(
                    "hp, CharacterDollStage.StatusColor(hp, false)"));
                Assert.That(panel, Does.Contain("healthRing.Add(face)"));
                Assert.That(panel, Does.Contain("panel.control.mixed"));
                Assert.That(panel, Does.Contain("inv.readonly"));
                Assert.That(panel, Does.Contain("ManageInventoryCommand"));
                Assert.That(panel, Does.Contain("_invDropActionLabel"));
                Assert.That(panel, Does.Not.Contain("_invDropZone"));
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
                Assert.That(camera, Does.Contain("_requestedDistance = FitDistance(radius)"));
                Assert.That(camera, Does.Contain("if (HasPanInput())"));
                Assert.That(camera, Does.Contain("ExitOrbit();"));
                Assert.That(camera, Does.Contain("TryGetNpcViewPosition"));
                Assert.That(camera, Does.Not.Contain("TryGetNpcBodyCenter(npcId"),
                    "An animated hip/head pivot makes follow recenter on every pose change.");
            });
        }

        [Test]
        public void CharacterActivationSelectsFirstAndFocusesExactRepeat()
        {
            Assert.Multiple(() =>
            {
                Assert.That(NpcActivationPolicy.ShouldFocus(new[] { 1 }, 2), Is.False);
                Assert.That(NpcActivationPolicy.ShouldFocus(new[] { 1 }, 1), Is.True);
                Assert.That(NpcActivationPolicy.ShouldFocus(new[] { 1, 2 }, 2), Is.False,
                    "A member of a group must first become the exclusive selection.");
                Assert.That(NpcActivationPolicy.ShouldFocus(
                    new[] { 1, 2 }, new[] { 1, 2 }), Is.True);
                Assert.That(NpcActivationPolicy.ShouldFocus(
                    new[] { 1, 2 }, new[] { 2, 1 }), Is.False,
                    "The ordered selection is part of the exact-repeat contract.");
                Assert.That(NpcActivationPolicy.ShouldFocus(
                    new List<int>(), new List<int>()), Is.False);
            });
        }

        [Test]
        public void EveryCharacterClickUsesSharedActivationAndSelectionDetachesFollow()
        {
            var selection = File.ReadAllText(Presentation("Input", "NpcSelection.cs"));
            var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));
            var panel = File.ReadAllText(Presentation("UI", "CharacterPanel.cs"));
            var adapter = File.ReadAllText(Presentation("Input", "SimulationInputAdapter.cs"));
            Assert.Multiple(() =>
            {
                Assert.That(selection, Does.Contain("NpcActivationPolicy.ShouldFocus"));
                Assert.That(selection, Does.Contain("public static void Select(int npcId) => Replace(npcId);"));
                Assert.That(camera, Does.Contain("_pendingFrameSelection = false;"));
                Assert.That(camera, Does.Contain("if (_mode == Mode.Orbit)"));
                Assert.That(camera, Does.Contain(
                    "HandlePointerGesture(snapshot);\n            // A world click is handled inside UpdateOrbit itself."));
                Assert.That(panel, Does.Not.Contain("Replace(actorId, requestFrame: true)"));
                Assert.That(panel, Does.Contain("NpcSelection.Activate(otherId)"));
                Assert.That(panel, Does.Contain("Toggle(actorId, requestFrame: false)"));
                Assert.That(camera, Does.Contain("Toggle(npcId, requestFrame: false)"));
                Assert.That(adapter, Does.Contain("() => NpcSelection.Activate(npcId)"));
            });
        }
    }
}
