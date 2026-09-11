using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
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

    [TestCase("missing", true, false)]
    [TestCase("carried", true, true)]
    [TestCase("site", true, true)]
    [TestCase("missing", false, true)]
    public void PaidBedNeedsHammerButMaterialDeliveryDoesNot(string toolLocation, bool paid, bool accepted)
    {
        var engine = TestWorld.CreateEngine(40954);
        var world = engine.World;
        engine.Step();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = npc.Needs.Energy = 1f;
        ManualCommandExecutor.Apply(world, new SetManualControlCommand(npc.Id, true));
        npc.Inventory.Items.Clear();
        foreach (var item in world.Entities.Objects.Values.Where(o => GearCatalog.For(o.DefinitionId).Has(GearCapability.Hammer)).ToArray())
            WorldObjectMutations.DespawnObject(world, item.Id);
        var node = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) is >= 2 and <= 4 &&
            Connectivity.Reachable(world, npc.CurrentJunction!.Value, j.Id));
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite, npc.Fragment, node.Tiles[0], node.Id);
        site.BuildProduct = ContentIds.BedBasic;
        site.BillLogs = 4; site.BillSticks = 5; site.BillRope = 10; site.BillLeaves = 50;
        if (paid)
            foreach (var material in BuildSiteMath.AllMaterials)
                for (var i = 0; i < BuildSiteMath.Bill(site, material); i++) site.Contents.Add(new ItemInstance(material));
        else npc.Inventory.Items.Add(new ItemInstance(ContentIds.Log));
        if (toolLocation == "carried") npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Hammer));
        if (toolLocation == "site") WorldObjectMutations.SpawnObject(world, GearCatalog.Hammer, npc.Fragment, site.Tile, node.Id);
        var before = site.Contents.Count;
        var result = ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, site.Id, InteractionType.Build));
        Assert.That(result.Status, Is.EqualTo(accepted ? ManualCommandAdmissionStatus.Accepted : ManualCommandAdmissionStatus.Rejected));
        if (!accepted) Assert.That(result.Reason, Is.EqualTo("MissingTool"));
        Assert.That(site.Contents.Count, Is.EqualTo(before), "Admission must not deliver or consume material.");
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
