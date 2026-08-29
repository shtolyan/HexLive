using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class BuildSiteViewTests
{
    [Test]
    public void FullyBuiltLiveCampfireDoesNotAcceptBuildOrder()
    {
        var fire = CampfireSnapshot();
        fire.BuildProduct = string.Empty;

        Assert.That(BuildSiteView.AcceptsBuildOrder(fire), Is.False,
            "Bug #292: a raised campfire with no open build product must not leave Build in the menu.");
    }

    [Test]
    public void LiveCampfireWithOpenUpgradeBillStillAcceptsBuildOrder()
    {
        var fire = CampfireSnapshot();
        fire.DeliveredStones--;

        Assert.That(BuildSiteView.AcceptsBuildOrder(fire), Is.True,
            "A raised campfire still exposes Build while its staged upgrade bill is open.");
    }

    [Test]
    public void FullyStockedButUnraisedCampfireStillAcceptsFinalBuildOrder()
    {
        var fire = CampfireSnapshot();

        Assert.That(BuildSiteView.AcceptsBuildOrder(fire), Is.True,
            "Closing the material bill must not hide the final Build order before the site is raised.");
    }

    private static ObjectSnapshot CampfireSnapshot() => new()
    {
        DefinitionId = ContentIds.Campfire,
        BuildProduct = ContentIds.Campfire,
        BillSticks = SimBalance.CampfireBillSticks,
        DeliveredSticks = SimBalance.CampfireBillSticks,
        BillRope = SimBalance.CampfireBillRope,
        DeliveredRope = SimBalance.CampfireBillRope,
        BillStones = SimBalance.CampfireBillStones,
        DeliveredStones = SimBalance.CampfireBillStones,
    };
}

}
