using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class GroundItemPlacementTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DropPreviewDoesNotMutateAndItsSuggestedOriginUsesNativeAdmission(bool blockLocalRing)
    {
        var (world, npc) = Fixture();
        var item = new ItemInstance("resource.palm_crown");
        if (blockLocalRing)
            foreach (var node in world.Junctions.Items.Values)
                if (node.Tiles.Any(t => HexSpatialMath.HexDistance(npc.Tile, t) <= 1))
                    world.Occupancy.JunctionOwner[node.Id] = new EntityId(999999);
        var position = npc.Position; var tile = npc.Tile; var count = world.Entities.Objects.Count;
        var found = GroundItemPlacementPreview.TryFindApproach(world, npc, item,
            out var here, out var approach, out var target, out var checkedOrigins);
        Assert.That(found, Is.True);
        Assert.That(here, Is.EqualTo(!blockLocalRing));
        Assert.That(checkedOrigins, Is.InRange(1, 19));
        Assert.That(npc.Position, Is.EqualTo(position));
        Assert.That(npc.Tile, Is.EqualTo(tile));
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(count));
        // Move only in the fixture to verify the spatial admission, not routing.
        var origin = world.Junctions.Items.Values.First(n => n.WorldPosition.Equals(approach));
        if (!here) Assert.That(origin.Tiles, Has.Count.EqualTo(1), "Move must resolve to the same origin tile used by preview.");
        npc.Position = approach; npc.CurrentJunction = origin.Id;
        npc.Tile = origin.Tiles.OrderBy(t => HexSpatialMath.HexDistance(t, tile)).First();
        var dropped = ExecutionSystem.DropItemAtFeet(world, npc, item);
        Assert.That(dropped, Is.Not.Null);
        Assert.That(dropped!.Junctions[0], Is.EqualTo(target));
    }

    [Test]
    public void ExpandedPreviewFindsNativeDropBeyondAnExhaustedLocalSearch()
    {
        var (world, npc) = Fixture();
        var item = new ItemInstance("resource.palm_crown");
        foreach (var node in world.Junctions.Items.Values)
            if (node.Tiles.Any(t => HexSpatialMath.HexDistance(npc.Tile, t) <= 3))
                world.Occupancy.JunctionOwner[node.Id] = new EntityId(999999);
        var position = npc.Position; var tile = npc.Tile;
        Assert.That(GroundItemPlacementPreview.TryFindApproach(world, npc, item,
            out _, out _, out _, out _), Is.False);
        Assert.That(GroundItemPlacementPreview.TryFindApproach(world, npc, item,
            out var here, out var approach, out var target, out var origins, 6), Is.True);
        Assert.That(here, Is.False);
        Assert.That(origins, Is.InRange(1, 127));
        Assert.That(npc.Position, Is.EqualTo(position));
        var origin = world.Junctions.Items.Values.First(n => n.WorldPosition.Equals(approach));
        Assert.That(origin.Tiles, Has.Count.EqualTo(1));
        Assert.That(HexSpatialMath.HexDistance(tile, origin.Tiles[0]), Is.GreaterThan(2));
        npc.Position = approach; npc.CurrentJunction = origin.Id; npc.Tile = origin.Tiles[0];
        Assert.That(ExecutionSystem.DropItemAtFeet(world, npc, item)!.Junctions[0], Is.EqualTo(target));
    }

    [Test]
    public void SaturatedPreviewReportsNoNearbySpaceWithoutMovingTheActor()
    {
        var (world, npc) = Fixture(onlyOwnPoint: true);
        Put(world, npc, new ItemInstance(ContentIds.Coconut));
        var before = npc.Position; var count = world.Entities.Objects.Count;
        Assert.That(GroundItemPlacementPreview.TryFindApproach(world, npc, new ItemInstance("resource.palm_crown"),
            out var here, out _, out _, out var checkedOrigins), Is.False);
        Assert.That(here, Is.False);
        Assert.That(checkedOrigins, Is.InRange(1, 19));
        Assert.That(npc.Position, Is.EqualTo(before));
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(count));
    }

    [Test]
    public void EveryPortableDefinitionHasSharedGeometryAndCampfireIsExcluded()
    {
        var world=TestWorld.CreateWorld();
        var missing=world.Content.ObjectDefinitions.Values
            .Where(d=>GroundItemPlacement.IsLoose(d))
            .Where(d=>!GroundPileCatalog.TryGet(d.Id,d.Layer!=null,out _))
            .Select(d=>d.Id).OrderBy(id=>id).ToArray();
        Assert.That(missing,Is.Empty,"Every active loose factory branch requires explicit shared geometry");
        Assert.That(GroundItemPlacement.IsLoose(world.Content.ObjectDefinitions["campfire.spot"]),Is.False);
        Assert.That(GroundPileCatalog.Capacity(ContentIds.Coconut),Is.EqualTo(1));
        Assert.That(GroundPileCatalog.Capacity(ContentIds.CoconutPierced),Is.EqualTo(1));
        Assert.That(GroundPileCatalog.Capacity(ContentIds.CoconutOpen),Is.EqualTo(8));
    }

    [Test]
    public void DifferentGarmentsUseSeparateNonOverlappingPoints()
    {
        var (world,npc)=Fixture();
        var first=ExecutionSystem.DropItemAtFeet(world,npc,new ItemInstance("clothing.jacket_biker"));
        var second=ExecutionSystem.DropItemAtFeet(world,npc,new ItemInstance("clothing.jacket_autumn"));
        Assert.That(first,Is.Not.Null);Assert.That(second,Is.Not.Null);
        Assert.That(first.Junctions[0],Is.EqualTo(npc.CurrentJunction.Value));
        Assert.That(second.Junctions[0],Is.Not.EqualTo(first.Junctions[0]));
        Assert.That(GroundPileCatalog.TryGet(first.DefinitionId,true,out var a),Is.True);
        Assert.That(GroundPileCatalog.TryGet(second.DefinitionId,true,out var b),Is.True);
        var ap=world.Junctions.Items[first.Junctions[0]].WorldPosition;
        var bp=world.Junctions.Items[second.Junctions[0]].WorldPosition;
        var ar=a.FullBounds.RadiusXZ;var br=b.FullBounds.RadiusXZ;
        var boundsA=new GroundPileBounds(ap.X-ar,0,ap.Y-ar,ap.X+ar,1,ap.Y+ar);
        var boundsB=new GroundPileBounds(bp.X-br,0,bp.Y-br,bp.X+br,1,bp.Y+br);
        Assert.That(boundsA.OverlapsXZ(boundsB),Is.False);
    }

    [Test]
    public void LegacyMixedAndOverfullGroupsDoNotAdmitNewInstances()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        Put(world,npc,new ItemInstance(ContentIds.Stick));
        Put(world,npc,new ItemInstance(ContentIds.CoconutOpen));
        Assert.That(GroundItemPlacement.TryFind(world,npc,new ItemInstance(ContentIds.Stick),out _,out _,out _),Is.False);
        Assert.That(world.Entities.Objects.Count,Is.EqualTo(2));
        foreach(var id in world.Entities.Objects.Keys.ToArray()) WorldObjectMutations.DespawnObject(world,id);
        for(var i=0;i<21;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
        Assert.That(GroundItemPlacement.TryFind(world,npc,new ItemInstance(ContentIds.Stick),out _,out _,out _),Is.False);
        Assert.That(world.Entities.Objects.Count,Is.EqualTo(21));
    }

    [Test]
    public void EmptyOwnPointWinsByActualDistance()
    {
        var (world,npc)=Fixture();
        Assert.That(GroundItemPlacement.TryFind(world,npc,new ItemInstance(ContentIds.Stick),out _,out var point,out var reason),Is.True,reason);
        Assert.That(point,Is.EqualTo(npc.CurrentJunction.Value));
    }

    [TestCase(19,true)]
    [TestCase(20,false)]
    public void ExactGroundStackCapacityMatchesInventorySlot(int existing,bool accepted)
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        for(var i=0;i<existing;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
        Assert.That(GroundItemPlacement.TryFind(world,npc,new ItemInstance(ContentIds.Stick),out _,out _,out _),Is.EqualTo(accepted));
        Assert.That(GroundPileCatalog.Capacity(ContentIds.Stick),Is.EqualTo(InventoryState.StackSizeFor(ContentIds.Stick)));
    }

    [TestCase(18,2,true)]
    [TestCase(19,2,false)]
    public void BatchPreflightCountsExistingAndReservedInstances(int existing,int incoming,bool accepted)
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        for(var i=0;i<existing;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
        var items=Enumerable.Range(0,incoming).Select(_=>new ItemInstance(ContentIds.Stick)).ToArray();
        Assert.That(GroundItemPlacement.CanPlaceBatch(world,npc,items),Is.EqualTo(accepted));
        Assert.That(world.Entities.Objects.Count,Is.EqualTo(existing));
    }

    [Test]
    public void RealBulkExecutorPreflightFailureDoesNotSpawnPublishOrConsume()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        for(var i=0;i<19;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
        var first=new ItemInstance(ContentIds.Stick);var second=new ItemInstance(ContentIds.Stick);
        npc.Inventory.Items.Clear();npc.Inventory.Items.Add(first);npc.Inventory.Items.Add(second);
        var nextId=world.NextRuntimeObjectId;var sequence=world.Events.HighestSeq;
        var events=world.Events.Items.ToArray();
        Assert.That(PlayerInventoryCommandExecutor.TryApply(world,npc,
            new InventoryItemRef(InventoryItemSource.Carried,0,ContentIds.Stick),InventoryAction.Drop,out var reason,count:2),Is.False);
        Assert.That(reason,Is.EqualTo("NoDropSpot"));
        Assert.That(world.NextRuntimeObjectId,Is.EqualTo(nextId));
        Assert.That(world.Events.HighestSeq,Is.EqualTo(sequence));
        Assert.That(world.Events.Items,Is.EqualTo(events));
        Assert.That(world.Entities.Objects.Count,Is.EqualTo(19));
        Assert.That(npc.Inventory.Items[0],Is.SameAs(first));Assert.That(npc.Inventory.Items[1],Is.SameAs(second));
    }

[Test]
public void MixedStateBulkStackUsesOnePointWithoutMergingInstanceState()
{
    var (world,npc)=Fixture(onlyOwnPoint:true);
    var first=new ItemInstance(ContentIds.Stick)
        {Durability=.21f,Wetness=.31f,Dirtiness=.41f,Bloodiness=.11f,ResourceAmount=2.5f,OwnerId=npc.Id.Value};
    var second=new ItemInstance(ContentIds.Stick)
        {Durability=.82f,Wetness=.02f,Dirtiness=.03f,Bloodiness=.04f,ResourceAmount=3.5f,OwnerId=999998};
    npc.Inventory.Items.Clear();npc.Inventory.Items.Add(first);npc.Inventory.Items.Add(second);
    Assert.That(PlayerInventoryCommandExecutor.TryApply(world,npc,
        new InventoryItemRef(InventoryItemSource.Carried,0,ContentIds.Stick),InventoryAction.Drop,out var reason,count:2),Is.True,reason);
    Assert.That(npc.Inventory.Items,Is.Empty);
    var objects=world.Entities.Objects.Values.OrderBy(o=>o.Id.Value).ToArray();
    Assert.That(objects.Length,Is.EqualTo(2));
    for(var i=0;i<2;i++)
    {
        var original=i==0?first:second;var obj=objects[i];
        Assert.That(obj.Junctions.Single(),Is.EqualTo(npc.CurrentJunction.Value));
        Assert.That(obj.Durability,Is.EqualTo(original.Durability));
        Assert.That(obj.Wetness,Is.EqualTo(original.Wetness));
        Assert.That(obj.Dirtiness,Is.EqualTo(original.Dirtiness));
        Assert.That(obj.Bloodiness,Is.EqualTo(original.Bloodiness));
        Assert.That(obj.ResourceAmount,Is.EqualTo(original.ResourceAmount));
        Assert.That(obj.Owner?.Value,Is.EqualTo(original.OwnerId));
    }
}

[Test]
public void RegisteredConsumedProcessSourceReleasesItsOnlyGroundPointForYields()
{
    var (world,npc)=Fixture(onlyOwnPoint:true);
    npc.Inventory.Items.Clear();npc.WornItems.Clear();npc.Inventory.Capacity=0;
    var source=WorldObjectMutations.SpawnObject(world,"resource.log",npc.Fragment,npc.Tile,npc.CurrentJunction.Value);
    var interaction=new InteractionDefinition {Type=InteractionType.Process};
    interaction.Yields.Add(new HarvestDrop{DefinitionId=ContentIds.Stick,Count=3,Scatter=true});
    var method=typeof(ExecutionSystem).GetMethod("CompleteProcess",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
    Assert.That(method.Invoke(null,new object[]{world,npc,source,world.Content.ObjectDefinitions[source.DefinitionId],interaction,""}),Is.EqualTo(true));
    Assert.That(world.Entities.Objects.ContainsKey(source.Id),Is.False);
    Assert.That(world.Entities.Objects.Count,Is.EqualTo(3));
    Assert.That(world.Entities.Objects.Values.All(o=>o.DefinitionId==ContentIds.Stick && o.Junctions.Single()==npc.CurrentJunction.Value),Is.True);
    Assert.That(npc.Inventory.Items,Is.Empty,"Consumed source must not force scatter yields into retained cargo");
}

[TestCase(PersonalCarePhase.Bathing)]
[TestCase(PersonalCarePhase.LaundryBatch)]
public void SaturatedDoffKeepsExactWornGarmentAndAbortsWithCooldown(PersonalCarePhase phase)
{
    var (world,npc)=Fixture(onlyOwnPoint:true);
    for(var i=0;i<21;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
    var garment=new ItemInstance("clothing.jacket_biker"){Durability=.27f,Wetness=.63f};
    npc.Inventory.Items.Clear();npc.WornItems.Clear();npc.WornItems.Add(garment);
    EquipmentMath.Recalculate(world,npc);
    npc.Mind.PersonalCarePhase=phase;npc.Mind.CurrentGoal=GoalType.Bathe;
    npc.Plan.Goal=GoalType.Bathe;npc.Plan.Status=PlanStatus.Active;
    npc.Plan.TargetItemDefinitionId=garment.DefinitionId;
    npc.Plan.TargetJunctionId=npc.CurrentJunction;
    npc.Execution.Status=ExecutionStatus.InProgress;npc.Execution.CurrentInteraction=InteractionType.Undress;
    npc.Execution.HeldGarment=garment;npc.Execution.StartTick=world.Tick;npc.Execution.EndTick=world.Tick;
    npc.Movement.IsMoving=false;
    var step=new PlanStep{Type=PlanStepType.PrepareBathe,TargetJunction=npc.CurrentJunction};
    npc.Plan.Steps.Clear();npc.Plan.Steps.Add(step);npc.Plan.CurrentStepIndex=0;
    var method=typeof(ExecutionSystem).GetMethod("RunPrepareBathe",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
    method.Invoke(null,new object[]{world,npc,step});
    Assert.That(npc.WornItems.Single(),Is.SameAs(garment));
    Assert.That(garment.Durability,Is.EqualTo(.27f));Assert.That(garment.Wetness,Is.EqualTo(.63f));
    Assert.That(npc.Execution.HeldGarment,Is.Null);
    Assert.That(npc.Plan.Status,Is.Not.EqualTo(PlanStatus.Active));
    Assert.That(npc.Mind.Cooldowns.Any(c=>c.Goal==GoalType.Bathe&&c.EndTick>world.Tick),Is.True);
    Assert.That(npc.Mind.PersonalCarePhase,Is.EqualTo(phase));
    Assert.That(world.Entities.Objects.Count,Is.EqualTo(21));
}

[TestCase(false)]
[TestCase(true)]
public void SaturatedGarmentCompletionRetainsExactHandAndPocketOwnership(bool stow)
{
    var (world,npc)=Fixture(onlyOwnPoint:true);
    for(var i=0;i<21;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
    var garment=new ItemInstance("clothing.jacket_biker"){Durability=.27f,Wetness=.63f};
    var pocket=new ItemInstance("item.plaster"){Durability=.31f,OwnerId=999998};
    npc.Inventory.Items.Clear();npc.WornItems.Clear();npc.Mind.OutfitLocked=false;
    npc.Mind.CurrentGoal=stow?GoalType.StowClothes:GoalType.Undress;
    npc.Plan.Goal=npc.Mind.CurrentGoal;npc.Plan.Status=PlanStatus.Active;
    npc.Plan.TargetItemDefinitionId=garment.DefinitionId;npc.Plan.TargetJunctionId=npc.CurrentJunction;
    npc.Execution.Status=ExecutionStatus.InProgress;npc.Execution.CurrentInteraction=InteractionType.Undress;
    npc.Execution.HeldGarment=garment;npc.Execution.StartTick=world.Tick;npc.Execution.EndTick=world.Tick;
    if(stow) npc.Execution.HeldGarmentContents.Add(pocket); else npc.Inventory.Items.Add(pocket);
    npc.Movement.IsMoving=false;
    var step=new PlanStep{TargetJunction=npc.CurrentJunction};
    npc.Plan.Steps.Clear();npc.Plan.Steps.Add(step);npc.Plan.CurrentStepIndex=0;
    var method=typeof(ExecutionSystem).GetMethod(stow?"RunStowCarriedGarment":"RunUndressItem",
        System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
    var seq=world.Events.HighestSeq;
    method.Invoke(null,new object[]{world,npc,step});
    Assert.That(npc.Inventory.Items.Count(i=>ReferenceEquals(i,garment)),Is.EqualTo(1));
    Assert.That(npc.Inventory.Items.Count(i=>ReferenceEquals(i,pocket)),Is.EqualTo(1));
    Assert.That(garment.Durability,Is.EqualTo(.27f));Assert.That(pocket.OwnerId,Is.EqualTo(999998));
    Assert.That(npc.Execution.HeldGarment,Is.Null);Assert.That(npc.Execution.HeldGarmentContents,Is.Empty);
    Assert.That(world.Entities.Objects.Count,Is.EqualTo(21));
    Assert.That(npc.Plan.Status,Is.EqualTo(PlanStatus.Invalid));
    Assert.That(world.Events.Items.Any(e=>e.Seq>seq&&(e.Type=="ClothesStowed"||e.Type=="ItemUndressed"||e.Type=="CycleReset")),Is.False);
    if(stow)
    {
        Assert.That(world.Events.Items.Any(e=>e.Seq>seq&&e.Type=="ClothesStowed"),Is.False);
        Assert.That(npc.Mind.Cooldowns.Any(c=>c.Goal==GoalType.StowClothes&&c.EndTick>world.Tick),Is.True);
    }
}

[TestCase(false,false)]
[TestCase(false,true)]
[TestCase(true,false)]
[TestCase(true,true)]
public void WearPreflightReservesOnlyActualDisplacedRemainderAndNeverMutates(bool external,bool saturated)
{
    var (world,npc)=Fixture(onlyOwnPoint:true);
    var first=new ItemInstance("clothing.jacket_biker"){Durability=.32f,OwnerId=npc.Id.Value};
    var second=new ItemInstance("clothing.pants_biker"){Durability=.78f,OwnerId=999998};
    var incoming=new ItemInstance("clothing.dress_city"){Wetness=.29f};
    foreach(var item in new[]{first,second,incoming})
    {
        var definition=world.Content.ObjectDefinitions[item.DefinitionId];
        definition.Layer=WearLayer.Wear;definition.InventoryCapacity=0;
    }
    npc.Mind.OutfitLocked=false;npc.Inventory.Items.Clear();npc.WornItems.Clear();
    npc.WornItems.Add(first);npc.WornItems.Add(second);EquipmentMath.Recalculate(world,npc);
    Assert.That(npc.Inventory.Capacity,Is.GreaterThan(0));
    for(var i=0;i<npc.Inventory.Capacity-1;i++) npc.Inventory.Items.Add(new ItemInstance("item.plaster"));
    if(!external) npc.Inventory.Items.Add(incoming);
    if(saturated) Put(world,npc,new ItemInstance(ContentIds.Coconut));
    var carriedBefore=npc.Inventory.Items.ToArray();var wornBefore=npc.WornItems.ToArray();
    var nextId=world.NextRuntimeObjectId;var events=world.Events.HighestSeq;
    var accepted=external?PlayerInventoryTransferMath.CanWearIncoming(world,npc,incoming):
        PlayerInventoryMath.FitsAfter(world,npc,new InventoryItemRef(InventoryItemSource.Carried,
            npc.Inventory.Items.Count-1,incoming.DefinitionId),InventoryAction.Wear);
    Assert.That(accepted,Is.EqualTo(!saturated));
    Assert.That(npc.Inventory.Items.Count,Is.EqualTo(carriedBefore.Length));
    for(var i=0;i<carriedBefore.Length;i++) Assert.That(npc.Inventory.Items[i],Is.SameAs(carriedBefore[i]));
    for(var i=0;i<wornBefore.Length;i++) Assert.That(npc.WornItems[i],Is.SameAs(wornBefore[i]));
    Assert.That(first.Durability,Is.EqualTo(.32f));Assert.That(second.OwnerId,Is.EqualTo(999998));
    Assert.That(world.NextRuntimeObjectId,Is.EqualTo(nextId));Assert.That(world.Events.HighestSeq,Is.EqualTo(events));
    if(saturated) return;
    if(external) npc.Inventory.Items.Add(incoming);
    ExecutionSystem.WearCarriedItem(world,npc,incoming);
    Assert.That(npc.WornItems.Single(),Is.SameAs(incoming));
    Assert.That(npc.Inventory.Items.Any(i=>ReferenceEquals(i,first)),Is.True,"First displaced garment fits the one free cell");
    Assert.That(npc.Inventory.Items.Any(i=>ReferenceEquals(i,second)),Is.False);
    var dropped=world.Entities.Objects.Values.Single();
    Assert.That(dropped.DefinitionId,Is.EqualTo(second.DefinitionId));
    Assert.That(dropped.Durability,Is.EqualTo(second.Durability));Assert.That(dropped.Owner?.Value,Is.EqualTo(second.OwnerId));
    Assert.That(npc.Inventory.UsedSlots,Is.LessThanOrEqualTo(npc.Inventory.Capacity));
}

[TestCase(false)]
[TestCase(true)]
public void WearResidualSpillProjectionRespectsLiveRetryCooldown(bool cooldown)
{
    var (world,npc)=Fixture(onlyOwnPoint:true);
    npc.Mind.OutfitLocked=false;npc.WornItems.Clear();npc.Inventory.Items.Clear();
    var incoming=new ItemInstance("clothing.dress_city");
    world.Content.ObjectDefinitions[incoming.DefinitionId].InventoryCapacity=0;
    EquipmentMath.Recalculate(world,npc);
    for(var i=0;i<npc.Inventory.Capacity+1;i++) npc.Inventory.Items.Add(new ItemInstance("item.plaster"));
    var original=npc.Inventory.Items.ToArray();
    npc.Inventory.NextGroundDropRetryTick=cooldown?world.Tick+100:0;
    Assert.That(PlayerInventoryTransferMath.CanWearIncoming(world,npc,incoming),Is.EqualTo(!cooldown));
    Assert.That(npc.Inventory.Items.Count,Is.EqualTo(original.Length));
    for(var i=0;i<original.Length;i++) Assert.That(npc.Inventory.Items[i],Is.SameAs(original[i]));
    Assert.That(world.Entities.Objects,Is.Empty);
    if(cooldown) return;
    npc.Inventory.Items.Add(incoming);ExecutionSystem.WearCarriedItem(world,npc,incoming);
    Assert.That(world.Entities.Objects.Values.Single().DefinitionId,Is.EqualTo("item.plaster"));
    Assert.That(npc.Inventory.UsedSlots,Is.EqualTo(npc.Inventory.Capacity));
}

[Test]
public void LegacyOverflowStowsFirst64ExactVictimsThenSpillsRemainderAfterGarment()
{
    var (world,npc)=Fixture();
    npc.WornItems.Clear();npc.Inventory.Items.Clear();npc.Inventory.Capacity=0;
    npc.Inventory.NextGroundDropRetryTick=0;
    var garment=new ItemInstance("clothing.jacket_biker"){Durability=.73f};
    var cargo=Enumerable.Range(0,65).Select(i=>new ItemInstance("item.plaster")
        {Durability=.1f+i*.01f,OwnerId=900000+i}).ToArray();
    npc.Inventory.Items.AddRange(cargo);
    Assert.That(PlayerInventoryMath.FitsWearProjected(world,npc,npc.Inventory.Items,npc.WornItems,new[]{garment}),Is.True);
    Assert.That(world.Entities.Objects,Is.Empty);
    for(var i=0;i<cargo.Length;i++) Assert.That(npc.Inventory.Items[i],Is.SameAs(cargo[i]));
    // Enter the real completion seam after conflict resolution/equipment recalc:
    // legacy overload is deliberately preserved, with no capacity left.
    var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
    var displaced=(System.Collections.Generic.List<ItemInstance>)typeof(ExecutionSystem).GetField("_displacedGarments",flags).GetValue(null);
    Assert.That(displaced,Is.Empty);
    try
    {
        displaced.Add(garment);
        typeof(ExecutionSystem).GetMethod("StowDisplacedGarments",flags).Invoke(null,new object[]{world,npc});
    }
    finally {displaced.Clear();}
    var placed=world.Entities.Objects.Values.OrderBy(o=>o.Id.Value).ToArray();
    Assert.That(placed.Length,Is.EqualTo(2));
    Assert.That(placed[0].DefinitionId,Is.EqualTo(garment.DefinitionId));
    Assert.That(placed[0].Contents.Count,Is.EqualTo(64));
    for(var i=0;i<64;i++) Assert.That(placed[0].Contents[i],Is.SameAs(cargo[i]));
    Assert.That(placed[1].DefinitionId,Is.EqualTo(cargo[64].DefinitionId));
    Assert.That(placed[1].Durability,Is.EqualTo(cargo[64].Durability));
    Assert.That(placed[1].Owner?.Value,Is.EqualTo(cargo[64].OwnerId));
    Assert.That(npc.Inventory.Items,Is.Empty);
}

    [Test]
    public void UnknownGeometryIsExplicitAndNeverConsumesProducedItem()
    {
        var (world,npc)=Fixture();
        const string id="test.unmeasured.garment";
        world.Content.ObjectDefinitions[id]=new ObjectDefinition{Id=id,Layer=WearLayer.Wear};
        var item=new ItemInstance(id){Durability=.42f,Dirtiness=.37f,ResourceAmount=3.25f,OwnerId=npc.Id.Value};
        Assert.That(GroundItemPlacement.TryFind(world,npc,item,out _,out _,out var reason),Is.False);
        Assert.That(reason,Is.EqualTo("UnknownGroundGeometry:"+id));
        npc.Inventory.Capacity=0;npc.Inventory.Items.Clear();npc.WornItems.Clear();
        ExecutionSystem.GiveOrDrop(world,npc,item);
        Assert.That(npc.Inventory.Items.Single(),Is.SameAs(item));
        Assert.That(npc.Inventory.NextGroundDropRetryTick,Is.GreaterThan(world.Tick));
        var retry=npc.Inventory.NextGroundDropRetryTick;
        InventoryMath.SpillOverflow(world,npc);
        Assert.That(npc.Inventory.Items.Single(),Is.SameAs(item));
        Assert.That(npc.Inventory.NextGroundDropRetryTick,Is.EqualTo(retry));
    }

    [Test]
    public void EmergencyAbortRetainsGarmentAndPocketInstancesAcrossSave()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        Put(world,npc,new ItemInstance(ContentIds.Stick));
        npc.Inventory.Capacity=0;
        npc.Inventory.Items.Clear();npc.WornItems.Clear();
        var garment=new ItemInstance("clothing.jacket_biker"){Durability=.42f,Wetness=.31f,Dirtiness=.27f,Bloodiness=.19f,OwnerId=npc.Id.Value};
        var pocket=new ItemInstance(ContentIds.Bottle){ResourceAmount=3.25f,WaterKind=WaterKind.Rain,Durability=.63f,OwnerId=npc.Id.Value};
        npc.Execution.HeldGarment=garment;npc.Execution.HeldGarmentContents.Add(pocket);
        Assert.That(PlanInterruption.TryAbort(world,npc,InterruptionCause.CombatVictim,"bounded ground full"),Is.True);
        Assert.That(npc.Inventory.Items.Any(i=>ReferenceEquals(i,garment)),Is.True);
        Assert.That(npc.Inventory.Items.Any(i=>ReferenceEquals(i,pocket)),Is.True);
        Assert.That(npc.Execution.HeldGarment,Is.Null);
        Assert.That(npc.Execution.HeldGarmentContents,Is.Empty);
        using(var bytes=new MemoryStream())
        {
            using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Write(world,writer);
            bytes.Position=0;
            var restored=TestWorld.CreateWorld();
            using(var reader=new BinaryReader(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Read(restored,reader);
            var carried=restored.Entities.Npcs[npc.Id].Inventory.Items;
            Assert.That(carried.Count,Is.EqualTo(2));
            var coat=carried.Single(i=>i.DefinitionId=="clothing.jacket_biker");
            Assert.That(coat.Durability,Is.EqualTo(.42f));Assert.That(coat.Bloodiness,Is.EqualTo(.19f));
            Assert.That(coat.Wetness,Is.EqualTo(.31f));Assert.That(coat.Dirtiness,Is.EqualTo(.27f));
            Assert.That(coat.OwnerId,Is.EqualTo(npc.Id.Value));
            var bottle=carried.Single(i=>i.DefinitionId==ContentIds.Bottle);
            Assert.That(bottle.ResourceAmount,Is.EqualTo(3.25f));Assert.That(bottle.WaterKind,Is.EqualTo(WaterKind.Rain));
            Assert.That(bottle.Durability,Is.EqualTo(.63f));Assert.That(bottle.OwnerId,Is.EqualTo(npc.Id.Value));
        }
    }

    [Test]
    public void ConcurrentReadOnlyPreflightUsesIndependentScratchAndDoesNotSpawn()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        var items=new[]{new ItemInstance(ContentIds.Stick),new ItemInstance(ContentIds.Stick)};
        Assert.That(GroundItemPlacement.CanPlaceBatch(world,npc,items),Is.True); // warm immutable catalog/cache before concurrent reads
        Assert.DoesNotThrow(()=>Parallel.For(0,32,_=>Assert.That(GroundItemPlacement.CanPlaceBatch(world,npc,items),Is.True)));
        Assert.That(world.Entities.Objects,Is.Empty);
    }

    [Test]
    public void SaturatedHarvestRetainsEveryProducedInstance()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        for(var i=0;i<21;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
        npc.Inventory.Items.Clear();npc.WornItems.Clear();npc.Inventory.Capacity=0;
        var source=new WorldObjectState {Tile=npc.Tile,Fragment=npc.Fragment};
        source.Junctions.Add(npc.CurrentJunction.Value);
        ExecutionSystem.ApplyHarvestYields(world,npc,source,new[]{new HarvestDrop
            {DefinitionId=ContentIds.CoconutOpen,Count=3,Scatter=true}});
        Assert.That(npc.Inventory.Items.Count,Is.EqualTo(3));
        Assert.That(npc.Inventory.Items.All(i=>i.DefinitionId==ContentIds.CoconutOpen),Is.True);
        Assert.That(ReferenceEquals(npc.Inventory.Items[0],npc.Inventory.Items[1]),Is.False);
        Assert.That(world.Entities.Objects.Count,Is.EqualTo(21));
    }

    [Test]
    public void ConsumedSplitSourceRetainsEveryOutputWithoutWholeOperationRetry()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        for(var i=0;i<21;i++) Put(world,npc,new ItemInstance(ContentIds.Stick));
        npc.Inventory.Items.Clear();npc.WornItems.Clear();npc.Inventory.Capacity=0;
        var sourceAnchor=world.Tiles.Items[npc.Tile].Junctions.First(j=>j!=npc.CurrentJunction.Value);
        var source=WorldObjectMutations.SpawnObject(world,"resource.log",npc.Fragment,npc.Tile,sourceAnchor);
        var method=typeof(ExecutionSystem).GetMethod("ReplaceWithYields",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        var primary=method.Invoke(null,new object[]{world,npc,source,new[]{new HarvestDrop
            {DefinitionId=ContentIds.Stick,Count=3,Scatter=true}}});
        Assert.That(primary,Is.Null);
        Assert.That(world.Entities.Objects.ContainsKey(source.Id),Is.False);
        Assert.That(world.Entities.Objects.Count,Is.EqualTo(21));
        Assert.That(npc.Inventory.Items.Count,Is.EqualTo(3));
        Assert.That(npc.Inventory.Items.All(i=>i.DefinitionId==ContentIds.Stick),Is.True);
    }

    [Test]
    public void ButcherPartialGroundAdmissionFinishesOnceAndRetainsOriginalGear()
    {
        var (world,npc)=Fixture(onlyOwnPoint:true);
        npc.Inventory.Items.Clear();npc.WornItems.Clear();npc.Inventory.Capacity=100;
        var first=new ItemInstance("clothing.jacket_biker"){Durability=.65f,OwnerId=npc.Id.Value};
        var retained=new ItemInstance("clothing.jacket_biker"){Durability=.23f,Dirtiness=.31f,OwnerId=npc.Id.Value};
        var body=new NPCState {Id=new EntityId(999998)};
        body.WornItems.Add(first);body.Inventory.Items.Add(retained);
        world.Entities.Corpses.Add(body.Id,body);
        var bodyAnchor=world.Tiles.Items[npc.Tile].Junctions.First(j=>j!=npc.CurrentJunction.Value);
        var corpse=WorldObjectMutations.SpawnObject(world,ContentIds.CorpseNpc,npc.Fragment,npc.Tile,bodyAnchor);
        corpse.CurrentUser=body.Id;
        Assert.That(GroundItemPlacement.TryFind(world,npc,first,out _,out _,out var preflightReason),Is.True,preflightReason);
        var interaction=new InteractionDefinition();
        interaction.Yields.Add(new HarvestDrop {DefinitionId=ContentIds.Stick,Count=3,Scatter=false});
        var method=typeof(ExecutionSystem).GetMethod("CompleteButcher",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        var result=method.Invoke(null,new object[]{world,npc,corpse,world.Content.ObjectDefinitions[ContentIds.CorpseNpc],interaction,""});
        Assert.That(result,Is.EqualTo(true),"A full ground must not retry the entire paid-yield completion");
        Assert.That(world.Entities.Corpses.ContainsKey(body.Id),Is.False);
        Assert.That(world.Entities.Objects.ContainsKey(corpse.Id),Is.False);
        Assert.That(npc.Inventory.Items.Count(i=>i.DefinitionId==ContentIds.Stick),Is.EqualTo(3));
        Assert.That(npc.Inventory.Items.Any(i=>ReferenceEquals(i,retained)),Is.True);
        Assert.That(npc.Inventory.Items.Any(i=>ReferenceEquals(i,first)),Is.False);
        var dropped=world.Entities.Objects.Values.Single(o=>o.DefinitionId=="clothing.jacket_biker");
        Assert.That(dropped.Durability,Is.EqualTo(first.Durability));
        Assert.That(retained.Durability,Is.EqualTo(.23f));Assert.That(retained.Dirtiness,Is.EqualTo(.31f));
    }

    [Test]
    public void PreflightAndUnchangedWorldSpawnChooseTheSamePoint()
    {
        var (world,npc)=Fixture();
        var item=new ItemInstance(ContentIds.Stick);
        Assert.That(GroundItemPlacement.TryFind(world,npc,item,out var tile,out var junction,out var reason),Is.True,reason);
        Assert.That(GroundItemPlacement.CanPlaceBatch(world,npc,new[]{item}),Is.True);
        var dropped=ExecutionSystem.DropItemAtFeet(world,npc,item);
        Assert.That(dropped,Is.Not.Null);
        Assert.That(dropped.Tile,Is.EqualTo(tile));
        Assert.That(dropped.Junctions.Single(),Is.EqualTo(junction));
    }

    [Test]
    public void WarmAllocatorWorkIsIndependentOfDistantObjectCount()
    {
        var (world,npc)=Fixture();
        var item=new ItemInstance(ContentIds.Stick);
        Put(world,npc,new ItemInstance(ContentIds.Stick));
        int Distance(TileCoord tile)
        {
            var dq=tile.Q-npc.Tile.Q;var dr=tile.R-npc.Tile.R;
            return Math.Max(Math.Abs(dq),Math.Max(Math.Abs(dr),Math.Abs(dq+dr)));
        }
        var far=world.Junctions.Items.Values.Where(j=>j.Tiles.Count>0)
            .OrderByDescending(j=>Distance(j.Tiles[0])).First();
        Assert.That(GroundPileCatalog.TryGet(ContentIds.Stick,false,out var profile),Is.True);
        var searchRings=2+(int)Math.Ceiling((profile.FullBounds.RadiusXZ+GroundPileCatalog.MaximumRadiusXZ)/
            (HexSpatialMath.HexRadius*HexSpatialMath.HexRowStepFactor));
        Assert.That(Distance(far.Tiles[0]),Is.GreaterThan(searchRings),"Fixture needs a genuinely distant region");
        for(var i=0;i<100;i++) GroundItemPlacement.TryFind(world,npc,item,out _,out _,out _,out _);
        var baseline=Measure();
        for(var i=0;i<1000;i++) WorldObjectMutations.SpawnObject(world,ContentIds.Stick,far.Fragment,far.Tiles[0],far.Id);
        var populated=Measure();
        Assert.That(populated.inspected,Is.EqualTo(baseline.inspected));
        Assert.That(populated.candidates,Is.EqualTo(baseline.candidates));
        Assert.That(populated.bytes,Is.EqualTo(baseline.bytes),"Distant objects must not allocate per-query scratch");
        TestContext.WriteLine($"[bug396-allocator] 1000 queries before: {baseline.ms:F3}ms/{baseline.bytes}B, after1000far: {populated.ms:F3}ms/{populated.bytes}B; inspected={populated.inspected}, candidates={populated.candidates}");

        (int inspected,int candidates,long bytes,double ms) Measure()
        {
            GroundItemPlacement.TryFind(world,npc,item,out _,out _,out _,out var metrics);
            var started=Stopwatch.GetTimestamp();var before=GC.GetAllocatedBytesForCurrentThread();
            for(var i=0;i<1000;i++) GroundItemPlacement.TryFind(world,npc,item,out _,out _,out _,out _);
            var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            return(metrics.ObjectsInspected,metrics.CandidatesChecked,allocated,
                (Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency);
        }
    }

    private static (WorldState,NPCState) Fixture(bool onlyOwnPoint=false)
    {
        var world=TestWorld.CreateWorld();
        foreach(var id in world.Entities.Objects.Keys.ToArray()) WorldObjectMutations.DespawnObject(world,id);
        var npc=world.Entities.Npcs.Values.First(n=>n.Faction==Faction.Colony);
        world.Occupancy.JunctionOwner.Clear();
        var junction=world.Junctions.Items.Values.First(j=>j.Fragment==npc.Fragment&&!j.Blocked&&j.Tiles.Any(t=>SpatialQueries.IsTileWalkable(world,t)));
        npc.CurrentJunction=junction.Id;npc.Position=junction.WorldPosition;
        npc.Tile=junction.Tiles.First(t=>SpatialQueries.IsTileWalkable(world,t));
        if(onlyOwnPoint)
            foreach(var id in world.Junctions.Items.Keys) world.Occupancy.JunctionOwner[id]=new EntityId(999999);
        world.Occupancy.JunctionOwner[junction.Id]=npc.Id;
        return (world,npc);
    }
    private static WorldObjectState Put(WorldState world,NPCState npc,ItemInstance item)
    {
        var obj=WorldObjectMutations.SpawnObject(world,item.DefinitionId,npc.Fragment,npc.Tile,npc.CurrentJunction.Value);
        obj.Durability=item.Durability;obj.Wetness=item.Wetness;
        obj.ResourceAmount=item.ResourceAmount;obj.WaterKind=item.WaterKind;
        obj.Dirtiness=item.Dirtiness;obj.Bloodiness=item.Bloodiness;obj.Owner=item.OwnerId==0?(EntityId?)null:new EntityId(item.OwnerId);
        return obj;
    }
}
}
