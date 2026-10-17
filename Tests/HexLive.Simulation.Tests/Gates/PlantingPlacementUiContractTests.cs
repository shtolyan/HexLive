using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §173.2: the Unity-facing half is source-guarded because UnityEngine code is
/// intentionally outside the headless simulation assembly.
/// </summary>
public sealed class PlantingPlacementUiContractTests
{
    private static string Source(params string[] path) =>
        RepoPaths.ReadText(Path.Combine(new[] { RepoPaths.Root }.Concat(path).ToArray()))
            .Replace("\r\n", "\n");

    [Test]
    public void SeedCardStartsExactJunctionPlacementWithoutDroppingTheItem()
    {
        var panel = Source("Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.cs");
        var input = Source("Assets", "HexLive", "UnityPresentation", "Input",
            "SimulationInputAdapter.cs");

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("Loc.Get(\"inv.action.plant\")"));
            Assert.That(panel, Does.Contain("PlantGrowthCatalog.TryGetBySeed(id, out _)"));
            Assert.That(panel, Does.Contain("input.BeginPlantingPlacement("));
            Assert.That(input, Does.Contain("new PlantInventorySeedCommand("));
            Assert.That(input, Does.Contain("new InventoryItemRef("));
            Assert.That(input, Does.Contain("PlantingPlacementMarker.Show("));
            Assert.That(input, Does.Contain("_plantingCandidateValid"));
            Assert.That(input, Does.Contain("HasCarriedSeedAtSource(actor,"),
                "Placement must revalidate the physical SourceIndex exported by inventory slots.");
            Assert.That(input, Does.Contain("actor.InventoryContainers"));
            Assert.That(input, Does.Not.Contain(
                    "actor.InventoryItems[_plantingSourceIndex]"),
                "InventoryItems is stack-folded and cannot validate a physical SourceIndex.");
            Assert.That(input, Does.Not.Contain("new ManageInventoryCommand("),
                "Placement must not implement planting as a preliminary drop.");
        });
    }

    [Test]
    public void PlacementHasBothRightClickAndEscapeCancellation()
    {
        var input = Source("Assets", "HexLive", "UnityPresentation", "Input",
            "SimulationInputAdapter.cs");
        var camera = Source("Assets", "HexLive", "UnityPresentation", "Input",
            "RtsCameraController.cs");

        Assert.Multiple(() =>
        {
            Assert.That(input, Does.Contain(
                "if (TryCancelPlantingPlacement())\n        {\n            return true;"),
                "Right-click path must cancel before opening any context menu.");
            Assert.That(camera, Does.Contain(
                "_manualInput.TryCancelPlantingPlacement()"),
                "Escape must cancel the planting cursor before changing selection.");
        });
    }

    [Test]
    public void PlantingAnimationUsesGardeningKneelAndVisibleSelectedSeed()
    {
        var actor = Source("Assets", "HexLive", "UnityPresentation", "Wearing",
            "NpcActorView.cs");
        var resolver = Source("Assets", "HexLive", "Simulation", "Runtime",
            "Helpers", "ActionItemResolver.cs");

        Assert.Multiple(() =>
        {
            Assert.That(actor, Does.Contain(
                "var planting = !_legless && interaction == \"Plant\";"));
            Assert.That(actor, Does.Contain(
                "(crafting && !standingCraft) || planting"));
            Assert.That(resolver, Does.Contain("case InteractionType.Plant:"));
            Assert.That(resolver, Does.Contain(
                "InventoryMath.ContainsReference(npc.Inventory.Items, selected)"));
        });
    }
}
