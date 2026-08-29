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
    public void AuthoredLimbWrapLoadsBothBootstrapTexturesWithoutLegacyFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "SkinTexturePainter.cs"));
        var start = source.IndexOf(
            "if (isBandage && !isPlaster)", StringComparison.Ordinal);
        var end = source.IndexOf("// §118.2: наклейка", start, StringComparison.Ordinal);
        var branch = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(branch, Does.Contain("var wrap = WrapOverlayFor(zoneName);"));
            Assert.That(branch, Does.Contain("var wrapNormal = WrapNormalFor(zoneName);"));
            Assert.That(branch, Does.Contain("(wrap == null || wrapNormal == null)"));
            Assert.That(branch, Does.Contain("OverNormal = wrapNormal"));
            Assert.That(branch, Does.Not.Contain("TryResolveLegacyPath"),
                "Registry readiness must not choose the retired round bandage forever.");
            Assert.That(source, Does.Contain(
                "Resources.Load<Texture2D>($\"HexLive/Decals/bandage_wrap_{zoneName}\")"));
            Assert.That(source, Does.Contain(
                "Resources.Load<Texture2D>($\"HexLive/Decals/bandage_wrap_{zoneName}_n\")"));
            Assert.That(source, Does.Not.Contain(
                "AtomicResources.Load<Texture2D>($\"HexLive/Decals/bandage_wrap_"),
                "Arm wraps must be available synchronously with the Player.");
        });
    }

    [TestCase("ArmL")]
    [TestCase("ArmR")]
    [TestCase("LegL")]
    [TestCase("LegR")]
    [TestCase("Torso")]
    [TestCase("Pelvis")]
    public void EveryAuthoredWrapZoneShipsAlbedoAndNormalInPlayer(string zone)
    {
        var root = Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "HexLive", "Decals");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(root, $"bandage_wrap_{zone}.png")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, $"bandage_wrap_{zone}_n.png")), Is.True);
        });
    }

    [Test]
    public void ActorBloodSoakReadsHealFieldFromExtendedWoundWire_Bug288()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "NpcActorView.cs"));
        var start = source.IndexOf("var bloodSoak = 0f;", StringComparison.Ordinal);
        var end = source.IndexOf("if (wornDirtiness != null)", start, StringComparison.Ordinal);
        var block = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(block, Does.Contain("entry.Split('|')"));
            Assert.That(block, Does.Contain("parts[2]"),
                "Wounds export as Zone|Seed|Heal01|Clot01|Severity|Plastered; clothing blood must fade from Heal01.");
            Assert.That(block, Does.Not.Contain("LastIndexOf('|')"),
                "The tail field is Plastered now, not Heal01.");
        });
    }

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
