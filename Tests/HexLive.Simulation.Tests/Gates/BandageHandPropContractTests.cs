using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§137.7 r2: Searching Pockets visibly holds the dressing it applies.</summary>
public sealed class BandageHandPropContractTests
{
    [Test]
    public void TreatInteractionsExportBandageOnlyWhileTheyAreActive()
    {
        var world = TestWorld.CreateWorld(13772);
        var npc = world.Entities.Npcs.Values.First();
        npc.Mind.ForcedMeleeWeaponId = null;
        npc.Mind.CombatOpponentNpcId = null;
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bandage));
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + 20;

        npc.Execution.CurrentInteraction = InteractionType.TreatSelf;
        Assert.That(Exported(world, npc).HeldItemId, Is.EqualTo(ContentIds.Bandage));

        npc.Execution.CurrentInteraction = InteractionType.TreatOther;
        Assert.That(Exported(world, npc).HeldItemId, Is.EqualTo(ContentIds.Bandage));

        npc.Execution.CurrentInteraction = null;
        npc.Execution.Status = ExecutionStatus.None;
        Assert.That(Exported(world, npc).HeldItemId, Is.Empty,
            "После завершения или прерывания перевязки бинт должен исчезнуть из руки.");
    }

    [Test]
    public void TreatOtherKeepsExportedBandageAndNormalHandContractPrefersRight()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "NpcActorView.cs"));
        var interactionStart = source.IndexOf(
            "public void SetInteraction(", StringComparison.Ordinal);
        var interactionEnd = source.IndexOf(
            "public void SetWardrobeAction(", interactionStart, StringComparison.Ordinal);
        var setInteraction = source[interactionStart..interactionEnd];

        var anchorStart = source.IndexOf(
            "private Transform ActingHandPropAnchor()", StringComparison.Ordinal);
        var anchorEnd = source.IndexOf(
            "private void RefreshLeglessPresentation()", anchorStart, StringComparison.Ordinal);
        var anchor = source[anchorStart..anchorEnd];

        Assert.Multiple(() =>
        {
            Assert.That(setInteraction, Does.Contain("\"TreatOther\" => heldItemId"),
                "TreatOther не должен выбрасывать HeldItemId перед SetHandProp.");
            Assert.That(anchor, Does.Contain(
                "GetBone(_leftHanded ? \"lHand\" : \"rHand\")"),
                "Обычная рабочая рука должна оставаться правой; левая — только fallback.");
        });
    }

    private static NpcSnapshot Exported(
        HexLive.Simulation.Core.WorldState world, NPCState npc) =>
        WorldSnapshotExporter.Export(world).Npcs.Single(
            candidate => candidate.Id.Value == npc.Id.Value);
}
