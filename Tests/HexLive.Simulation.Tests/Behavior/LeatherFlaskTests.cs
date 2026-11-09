using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior;
public sealed class LeatherFlaskTests
{
    [Test] public void FutureNpcStartsWithFlaskWithoutReplacingLegacyBottleDefinition()
    {
        var world=TestWorld.CreateWorld(65002);
        var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        Assert.That(npc.Inventory.Items.Count(i=>i.DefinitionId==ContentIds.LeatherFlask),Is.EqualTo(1));
        Assert.That(npc.Inventory.Items.Any(i=>i.DefinitionId==ContentIds.Bottle),Is.False);
        Assert.That(world.Content.ObjectDefinitions.ContainsKey(ContentIds.Bottle),Is.True);
        Assert.That(GearCatalog.For(ContentIds.LeatherFlask).Has(GearCapability.CarryWater),Is.True);
        Assert.That(WaterVesselCatalog.Capacity(ContentIds.LeatherFlask),Is.EqualTo(2*SimBalance.CoconutWaterCapacity));
    }
    [TestCase(ContentIds.LeatherFlask,8)] [TestCase(ContentIds.Bottle,10)]
    public void ContentsClampToOwnCapacityAndDrinkingConsumesExactOneSip(string id,int capacity)
    {
        var vessel=new ItemInstance(id);
        BottleInventoryMath.SetContents(vessel,WaterKind.Raw,100);
        Assert.That(BottleInventoryMath.Charges(vessel),Is.EqualTo(capacity));
        Assert.That(BottleInventoryMath.ConsumeOne(vessel,out var water),Is.True);
        Assert.That(water,Is.EqualTo(WaterKind.Raw));Assert.That(vessel.ResourceAmount,Is.EqualTo(capacity-1));
    }
    [Test] public void TwoCoconutsFillFlaskAndPlasticOverflowRemainsInExactSource()
    {
        var world=TestWorld.CreateWorld(65003);var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        npc.Inventory.Items.Clear();var flask=new ItemInstance(ContentIds.LeatherFlask);npc.Inventory.Items.Add(flask);
        var a=new ItemInstance(ContentIds.CoconutPierced){ResourceAmount=SimBalance.CoconutWaterCapacity};
        var b=new ItemInstance(ContentIds.CoconutPierced){ResourceAmount=SimBalance.CoconutWaterCapacity};
        npc.Inventory.Items.Add(a);npc.Inventory.Items.Add(b);
        Assert.That(VesselTransferMath.FillBottleFromCoconuts(npc,flask),Is.EqualTo(8));
        Assert.That(a.ResourceAmount+b.ResourceAmount,Is.Zero);Assert.That(flask.ResourceAmount,Is.EqualTo(8));
        BottleInventoryMath.SetContents(flask,WaterKind.None,0);
        var plastic=new ItemInstance(ContentIds.Bottle);BottleInventoryMath.SetContents(plastic,WaterKind.Raw,9.25f);
        npc.Inventory.Items.Add(plastic);
        Assert.That(VesselTransferMath.ConsolidateBottleFromSources(flask,npc.Inventory.Items),Is.EqualTo(8));
        Assert.That(plastic.ResourceAmount,Is.EqualTo(1.25f));Assert.That(flask.WaterKind,Is.EqualTo(WaterKind.Raw));
        Assert.That(plastic.WaterKind,Is.EqualTo(WaterKind.Raw));
    }
    [Test] public void ManualFillCommandBindsTheClickedFlaskAndHeldPropInsteadOfPlastic()
    {
        var world=TestWorld.CreateWorld(65004);var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        npc.Inventory.Items.Clear();npc.Mind.ManualControl=true;
        var flask=new ItemInstance(ContentIds.LeatherFlask);npc.Inventory.Items.Add(flask);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutPierced){ResourceAmount=4});
        var admission=ManualCommandExecutor.Apply(world,new FillVesselCommand(npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried,0,ContentIds.LeatherFlask)));
        Assert.That(admission.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),admission.Reason);
        var execution=new ExecutionSystem();execution.Run(world);
        Assert.That(ActionItemResolver.HeldId(world,npc),Is.EqualTo(ContentIds.LeatherFlask));
        for(int i=0;i<200&&flask.ResourceAmount==0;i++){world.Tick++;execution.Run(world);}
        Assert.That(flask.ResourceAmount,Is.EqualTo(4));Assert.That(flask.WaterKind,Is.EqualTo(WaterKind.Coconut));
    }
}
