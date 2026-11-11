using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior;
public sealed class HutHoverRevealTests
{
    static ObjectSnapshot House()=>new(){Id=new ObjectId(661),DefinitionId=ContentIds.HutPlan,Tile=TileCoord.Zero};
    [TestCase(Faction.Colony)] [TestCase(Faction.Colony2)] [TestCase(Faction.Colony5)]
    public void ExplicitCampOwnerUsesActualViewerCamp(Faction camp)
    {
        var snapshot=new WorldSnapshot();var house=House();house.PropertyOwnerFaction=camp;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,camp,true),Is.True);
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Outsiders,true),Is.False);
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,camp,false),Is.False);
    }
    [TestCase(false)] [TestCase(true)]
    public void PersonalOwnershipCannotBeOverriddenByOwnFactionStamp(bool dead)
    {
        var snapshot=new WorldSnapshot();var owner=new NpcSnapshot{Id=new EntityId(3),Faction=Faction.Colony2};
        if(dead)snapshot.Corpses.Add(owner);else snapshot.Npcs.Add(owner);
        var house=House();house.PropertyOwnerFaction=Faction.Colony;house.PropertyOwnerNpcId=3;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.False);
        house.PropertyOwnerFaction=null;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony2,true),Is.True);
        house.PropertyOwnerNpcId=999;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony2,true),Is.False);
    }
    [TestCase(0,true)] [TestCase(TheftMath.PrivateGroundRadiusTiles,true)]
    [TestCase(TheftMath.PrivateGroundRadiusTiles+1,false)]
    public void UnstampedLegacyHouseRequiresOurPrivateGround(int distance,bool expected)
    {
        var snapshot=new WorldSnapshot();var house=House();house.DefinitionId=ContentIds.Hut1Hex;
        snapshot.CampHomes.Add(new CampHomeSnapshot{Faction=Faction.Colony,Tile=new TileCoord(distance,0)});
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.EqualTo(expected));
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony2,true),Is.False);
        snapshot.CampHomes.Add(new CampHomeSnapshot{Faction=Faction.Colony2,Tile=TileCoord.Zero});
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.False,"foreign overlap is not ours");
    }
    [Test]
    public void NoSelectionNoCampConstructionAndRuinsNeverGainHoverAccess()
    {
        var snapshot=new WorldSnapshot();var house=House();
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.False);
        house.PropertyOwnerFaction=Faction.Colony;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,false),Is.False);
        house.BuildProduct=ContentIds.HutPlan;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.False);
        house.BuildProduct="";house.Durability=0;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.False);
        house.Durability=1;house.DefinitionId=ContentIds.WaterCollector;
        Assert.That(HutHoverRevealMath.CanReveal(snapshot,house,Faction.Colony,true),Is.False);
    }

    [TestCase(ContentIds.HutPlan)]
    [TestCase(ContentIds.BuildSite)]
    public void CompletedEnvelopeAllowsOwnedPlanToRevealDuringExpansion(string definitionId)
    {
        var snapshot = new WorldSnapshot();
        var house = House();
        house.DefinitionId = definitionId;
        house.BuildProduct = ContentIds.HutPlan;
        house.PropertyOwnerFaction = Faction.Colony;

        Assert.That(HutHoverRevealMath.CanReveal(snapshot, house, Faction.Colony, true), Is.False);
        Assert.That(HutHoverRevealMath.CanReveal(snapshot, house, Faction.Colony, true,
            hasCompletedEnvelope: true), Is.True);
        Assert.That(HutHoverRevealMath.CanReveal(snapshot, house, Faction.Colony2, true,
            hasCompletedEnvelope: true), Is.False);
    }

    [TestCase("architecture.wall.wood", 4, true)]
    [TestCase("architecture.wall.wood", 3, false)]
    [TestCase("architecture.window.wood", 4, true)]
    [TestCase("architecture.door.wood", 4, true)]
    [TestCase("architecture.support.wood", 4, true)]
    [TestCase("architecture.roof.palm", 4, true)]
    [TestCase("architecture.roof.palm", 0, false)]
    [TestCase("architecture.floor.board", 4, false)]
    public void CutawayCollectsOnlyCompletedEnvelopeModules(string definitionId, int delivered, bool expected)
    {
        var piece = new ObjectSnapshot { ArchitectureOwnerObjectId = 661 };
        piece.ArchitectureElements.Add(new ArchitectureElementSnapshot
        {
            DefinitionId = definitionId, Buildable = true, RequiredSticks = 4, DeliveredSticks = delivered,
        });

        Assert.That(HutHoverRevealMath.IsCompletedCutawayModule(piece), Is.EqualTo(expected));
    }
}
