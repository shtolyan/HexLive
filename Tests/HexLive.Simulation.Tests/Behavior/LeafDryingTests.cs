using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using System.IO;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class LeafDryingTests
{
    [Test]
    public void PickupDropAndContainerTransferKeepTheSamePhysicalLeafClock()
    {
        var engine=TestWorld.CreateEngine();engine.Step(); var world=engine.World;world.Tick=50000;
        foreach(var actor in world.Entities.Npcs.Values) actor.Mind.ManualControl=true;
        var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        npc.Inventory.Items.Clear();
        var leaf=WorldObjectMutations.SpawnObject(world,ContentIds.PalmLeaf,npc.Fragment,npc.Tile,npc.CurrentJunction.Value);
        leaf.LeafDryingStartTick=1000;
        var admission=ManualCommandExecutor.Apply(world,new InteractCommand(npc.Id,leaf.Id,InteractionType.PickUp));
        Assert.That(admission.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),admission.Reason);
        for(var i=0;i<300 && world.Entities.Objects.ContainsKey(leaf.Id);i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(leaf.Id),Is.False,"Pickup must actually complete");
        var carried=npc.Inventory.Items.Single(i=>i.DefinitionId==ContentIds.PalmLeaf);
        Assert.That(carried.LeafDryingStartTick,Is.EqualTo(1000));
        var bag=new WorldObjectState { DefinitionId="remains.human" };
        ContainerLootMath.GiveToContainer(world,bag,npc,new[]{carried});
        ContainerLootMath.TakeFromContainer(world,bag,npc,new[]{carried},System.Array.Empty<HexLive.Simulation.Common.ObjectId>());
        Assert.That(npc.Inventory.Items.Single(i=>i.DefinitionId==ContentIds.PalmLeaf),Is.SameAs(carried));
        var dropped=ExecutionSystem.DropAutomaticItemAtFeet(world,npc,carried);
        Assert.That(dropped,Is.Not.Null);
        Assert.That(dropped.LeafDryingStartTick,Is.EqualTo(1000));
    }

    [Test]
    public void DrynessSurvivesDeltaIncludingResetToFresh()
    {
        var sent=new WorldSnapshot { Tick=1 };
        sent.Objects.Add(new ObjectSnapshot { Id=new HexLive.Simulation.Common.ObjectId(1),
            DefinitionId=ContentIds.PalmLeaf,LeafDryness=1f });
        var mirror=new WorldSnapshot();var encoder=new SnapshotDeltaEncoder();
        void Apply(int tick)
        {
            using var reader=new BinaryReader(new MemoryStream(encoder.Encode(sent,false)));
            SnapshotDeltaReader.Apply(reader,mirror,tick);
        }
        Apply(-1);
        Assert.That(mirror.Objects.Single().LeafDryness,Is.EqualTo(1f));
        sent.Tick=2;sent.Objects[0].LeafDryness=0;
        Apply(1);
        Assert.That(mirror.Objects.Single().LeafDryness,Is.Zero);
    }

    [Test]
    public void OnlyFullyDryLeavesBurnAndFourEqualOneStickWithoutConsumingFreshLeaf()
    {
        var world=TestWorld.CreateWorld(); world.Tick=SimBalance.PalmLeafWitherTicks+100;
        var npc=world.Entities.Npcs.Values.First(); npc.Inventory.Items.Clear();
        var fresh=new ItemInstance(ContentIds.PalmLeaf) { LeafDryingStartTick=world.Tick };
        npc.Inventory.Items.Add(fresh);
        Assert.That(ContainerLootMath.FindCarriedCampfireFuel(world,npc),Is.Null);
        var fire=new WorldObjectState { DefinitionId=ContentIds.Campfire };
        Assert.That(ContainerLootMath.CanAccept(world,fire,new[]{fresh}),Is.False);
        var total=0f;
        for(var i=0;i<4;i++)
        {
            var dry=new ItemInstance(ContentIds.PalmLeaf) { LeafDryingStartTick=1 };
            npc.Inventory.Items.Add(dry);
            Assert.That(ContainerLootMath.FindCarriedCampfireFuel(world,npc),Is.SameAs(dry));
            npc.Execution.CurrentInteraction=InteractionType.Fuel;
            Assert.That(ActionItemResolver.HeldId(world,npc),Is.EqualTo(ContentIds.PalmLeaf));
            Assert.That(LeafDryingMath.HeldFraction(world,npc),Is.EqualTo(1f),"The hand must show the selected dry leaf, not the first green stack member");
            Assert.That(ContainerLootMath.CanAccept(world,fire,new[]{dry}),Is.True);
            ContainerLootMath.GiveToContainer(world,fire,npc,new[]{dry});
            Assert.That(ContainerLootMath.TryConsumeCampfireFuel(world,fire,out var fuel),Is.True);
            total+=fuel;
        }
        Assert.That(total,Is.EqualTo(ContainerLootMath.FuelTicksPerStick));
        Assert.That(npc.Inventory.Items.Single(),Is.SameAs(fresh));
    }

    [TestCase(83)]
    [TestCase(84)]
    public void SavePreservesPhysicalClockAndOlderInventoryStartsAtRestore(int version)
    {
        var world=TestWorld.CreateWorld(); world.Tick=50000;
        var npc=world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.PalmLeaf) { LeafDryingStartTick=1000 });
        var obj=world.Entities.Objects.Values.First(); obj.LeafDryingStartTick=222;
        using var bytes=new MemoryStream();
        using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
            WorldSaveSerializer.WriteAtVersion(world,writer,version);
        var loaded=TestWorld.CreateWorld(); bytes.Position=0;
        WorldSaveSerializer.Read(loaded,new BinaryReader(bytes));
        var item=loaded.Entities.Npcs[npc.Id].Inventory.Items.Single();
        Assert.That(item.LeafDryingStartTick,Is.EqualTo(version==84 ? 1000 : loaded.Tick));
        Assert.That(loaded.Entities.Objects[obj.Id].LeafDryingStartTick,Is.EqualTo(version==84 ? 222 : -1));
    }

    [Test]
    public void DryLeafCanDropAndSnapshotReflectsAgeInsteadOfRefreshingClock()
    {
        var engine=TestWorld.CreateEngine(); engine.Step();
        var world=engine.World; world.Tick=50000;
        var npc=world.Entities.Npcs.Values.First(n => n.Faction==Faction.Colony);
        var leaf=new ItemInstance(ContentIds.PalmLeaf) { LeafDryingStartTick=1000 };
        var dropped=ExecutionSystem.DropAutomaticItemAtFeet(world,npc,leaf);
        Assert.That(dropped,Is.Not.Null);
        Assert.That(dropped.LeafDryingStartTick,Is.EqualTo(1000));
        Assert.That(WorldSnapshotExporter.Export(world).Objects.Single(o=>o.Id==dropped.Id).LeafDryness,Is.EqualTo(1f));
    }

    [Test]
    public void RaisedBedKeepsDeliveredLeafAgeAndLivingTreesDoNotDry()
    {
        var engine=TestWorld.CreateEngine();engine.Step(); var world=engine.World;
        var npc=world.Entities.Npcs.Values.First(); world.Tick=50000;
        var site=WorldObjectMutations.SpawnObject(world,ContentIds.BuildSite,npc.Fragment,npc.Tile,npc.CurrentJunction.Value);
        site.BuildProduct=ContentIds.BedBasic;
        LeafDryingMath.Receive(site,new ItemInstance(ContentIds.PalmLeaf) { LeafDryingStartTick=1000 },world.Tick);
        var bed=ExecutionSystem.RaiseFurnitureSite(world,site,npc.Fragment,npc.CurrentJunction);
        Assert.That(bed.LeafDryingStartTick,Is.EqualTo(1000));
        Assert.That(WorldSnapshotExporter.Export(world).Objects.Single(o=>o.Id==bed.Id).LeafDryness,Is.EqualTo(1f));
        Assert.That(LeafDryingMath.HasLeaves("tree.palm"),Is.False);
        Assert.That(LeafDryingMath.HasLeaves("architecture.wall.wood"),Is.False);
    }

    [Test]
    public void OldLeafRemainsOnGroundInsteadOfDespawning()
    {
        var world = TestWorld.CreateWorld();
        var npc=world.Entities.Npcs.Values.First();
        var anchor=world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count>0);
        var leaf=WorldObjectMutations.SpawnObject(world,ContentIds.PalmLeaf,npc.Fragment,anchor.Tiles[0],anchor.Id);
        leaf.SpawnTick=1; leaf.IsOccupied=false;
        world.Tick=SimBalance.PalmLeafWitherTicks+20;
        new MeatSpoilageSystem().Run(world);
        Assert.That(world.Entities.Objects.ContainsKey(leaf.Id), Is.True);
    }
}
