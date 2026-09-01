using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§50.5 / bug #345: Drink is one verb and therefore one clip in every posture.</summary>
public sealed class DrinkAnimationPresentationContractTests
{
    private static string ActorSource() => File.ReadAllText(Path.Combine(
        RepoPaths.Root,
        "Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs"));

    private static string Method(string source, string signature, string nextSignature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var end = source.IndexOf(nextSignature, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing {signature}");
        Assert.That(end, Is.GreaterThan(start), $"Missing boundary {nextSignature}");
        return source[start..end];
    }

    [Test]
    public void LeglessPostureNeverReplacesOrRestoresTheDrinkClip()
    {
        var source = ActorSource();
        var restore = Method(
            source,
            "private void RestoreLeglessClipOverrides()",
            "private void ApplyLeglessClipOverrides()");
        var apply = Method(
            source,
            "private void ApplyLeglessClipOverrides()",
            "private void SpawnSeverFountain(");

        Assert.Multiple(() =>
        {
            Assert.That(restore, Does.Not.Contain("X Bot@Drinking"),
                "Leaving Crawl must not swap the canonical drink take either.");
            Assert.That(apply, Does.Not.Contain("X Bot@Drinking"),
                "Entering Crawl must not turn a Drink interaction into prone idle.");
        });
    }

    [Test]
    public void DrinkUsesTheConfiguredTakeAndPreservesTheExportedVessel()
    {
        var source = ActorSource();
        var interaction = Method(
            source,
            "public void SetInteraction(",
            "public void SetWardrobeAction(");

        Assert.Multiple(() =>
        {
            Assert.That(interaction, Does.Contain(
                "if (drinking && _animSet != null) OverrideClip(\"X Bot@Drinking\", _animSet.drink);"),
                "Bottle and pierced coconut must share the configured canonical drink take.");
            Assert.That(interaction, Does.Not.Contain(
                "OverrideClip(\"X Bot@Drinking\", Standing(_animSet.drink))"),
                "Posture must not remap the drinking clip.");
            Assert.That(interaction, Does.Contain(
                "SetHandProp(crafting || looting ? string.Empty\n" +
                "            : aidingOther ? aidPropId\n" +
                "            : heldItemId);"),
                "The shared clip must not erase whether the sim exported a bottle or coconut.");
        });
    }
}
