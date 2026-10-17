using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§120/§133: Blender stage parents and hauling order stay aligned.</summary>
public sealed class WardrobeStageTests
{
    private static WorldObjectState NewSite() => new()
    {
        DefinitionId = "build.site",
        BuildProduct = ContentIds.Wardrobe,
        BillSticks = SimBalance.WardrobeBillSticks,
        BillBoards = SimBalance.WardrobeBillBoards,
        BillRope = SimBalance.WardrobeBillRope,
    };

    private static void Deliver(WorldObjectState site, string material, int count)
    {
        for (var i = 0; i < count; i++) site.Contents.Add(new ItemInstance(material));
    }

    [Test]
    public void FrameBoardsAndLashingAreRequestedInAuthoredOrder()
    {
        var site = NewSite();

        Assert.That(BuildSiteMath.Remaining(site, ContentIds.Stick), Is.EqualTo(4));
        Assert.That(BuildSiteMath.Needs(site, ContentIds.Board), Is.False);
        Assert.That(BuildSiteMath.Needs(site, ContentIds.Rope), Is.False);

        Deliver(site, ContentIds.Stick, 4);
        Assert.That(BuildSiteMath.Remaining(site, ContentIds.Board), Is.EqualTo(8));
        Assert.That(BuildSiteMath.Needs(site, ContentIds.Rope), Is.False);

        Deliver(site, ContentIds.Board, 8);
        Assert.That(BuildSiteMath.Remaining(site, ContentIds.Rope), Is.EqualTo(1));

        Deliver(site, ContentIds.Rope, 1);
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
    }

    [Test]
    public void IndoorHearthDoesNotWaitForOutdoorSpitOrEighteenStones()
    {
        var site = new WorldObjectState
        {
            DefinitionId = "build.site",
            BuildProduct = ContentIds.Campfire,
            Variant = BuildingRules.HutHearthVariant,
            BillSticks = SimBalance.HutHearthBillSticks,
            BillStones = SimBalance.HutHearthBillStones,
            BillRope = SimBalance.HutHearthBillRope,
        };
        Assert.That(BuildSiteMath.Remaining(site, ContentIds.Stick), Is.EqualTo(8));
        Assert.That(BuildSiteMath.Needs(site, ContentIds.Stone), Is.False);
        Deliver(site, ContentIds.Stick, 8);
        Assert.That(BuildSiteMath.Remaining(site, ContentIds.Stone), Is.EqualTo(12));
        Assert.That(BuildSiteMath.Needs(site, ContentIds.Rope), Is.False);
        Deliver(site, ContentIds.Stone, 12);
        Assert.That(BuildSiteMath.Remaining(site, ContentIds.Rope), Is.EqualTo(1));
        Deliver(site, ContentIds.Rope, 1);
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
    }
}
