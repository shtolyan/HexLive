using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class CollectorQuickPlacementTests
{
    [TestCase(ContentIds.Bottle, 0f)]
    [TestCase(ContentIds.Bottle, .25f)]
    [TestCase(ContentIds.Bottle, 3.5f)]
    [TestCase(ContentIds.LeatherFlask, 0f)]
    [TestCase(ContentIds.LeatherFlask, .25f)]
    [TestCase(ContentIds.LeatherFlask, 3.5f)]
    public void QuickPlaceParksThePhysicalVesselWithoutDiscardingContents(string id,float amount)
    {
        var engine=TestWorld.CreateEngine(658);var world=engine.World;
        var npc=world.Entities.Npcs.Values.First(n=>(int)n.Faction==0);
        npc.Mind.ManualControl=true;npc.Needs.Hunger=npc.Needs.Thirst=0;
        npc.Inventory.Items.Clear();
        var item=new ItemInstance(id){Durability=.64f,Wetness=.23f,Dirtiness=.17f,Bloodiness=.11f,OwnerId=711};
        BottleInventoryMath.SetContents(item,WaterKind.Raw,amount);
        if(amount>0)item.LastAddedWaterKind=WaterKind.Coconut;
        npc.Inventory.Items.Add(item);
        var destination=world.Junctions.Items.Values.First(j=>!j.Blocked&&SpatialQueries.IsJunctionFree(world,j.Id)&&
            j.Neighbors.Any(n=>SpatialQueries.IsJunctionFree(world,n)));
        if(npc.CurrentJunction is {} previous){SpatialMutations.FreeJunction(world,previous,npc.Id);SpatialMutations.ReleaseJunctionReservation(world,previous,npc.Id);}
        var oldTile=npc.Tile;npc.CurrentJunction=destination.Id;npc.Position=destination.WorldPosition;
        npc.Tile=destination.Tiles[0];npc.Fragment=destination.Fragment;
        SpatialMutations.MoveEntityToTile(world,npc.Id,oldTile,npc.Tile);SpatialMutations.OccupyJunction(world,destination.Id,npc.Id);
        var anchor=SpatialQueries.GetPassableNeighbors(world,destination.Id).First();
        var collector=WorldObjectMutations.SpawnObject(world,ContentIds.WaterCollector,npc.Fragment,world.Junctions.Items[anchor].Tiles[0],anchor);
        collector.RotationDegrees=120f;
        var admitted=ManualCommandExecutor.Apply(world,new InteractCommand(npc.Id,collector.Id,InteractionType.PlaceVessel,"vessel.place"));
        Assert.That(admitted.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),admitted.Reason);
        for(int tick=0;tick<300&&WaterCollectorMath.FindVessel(world,collector)==null;tick++)engine.Step();
        var parked=WaterCollectorMath.FindVessel(world,collector);
        Assert.That(parked,Is.Not.Null,"quick collector order must work for a partial/full vessel as inventory Give does");
        Assert.Multiple(()=>{
            Assert.That(parked.DefinitionId,Is.EqualTo(id));
            Assert.That(WaterCollectorMath.AmountIn(parked),Is.EqualTo(amount).Within(1e-5));
            Assert.That(parked.WaterKind,Is.EqualTo(amount>0?WaterKind.Raw:WaterKind.None));
            Assert.That(parked.LastAddedWaterKind,Is.EqualTo(amount>0?WaterKind.Coconut:WaterKind.None));
            Assert.That(parked.Durability,Is.EqualTo(.64f));
            Assert.That(parked.Owner,Is.EqualTo(new HexLive.Simulation.Common.EntityId(711)));
            Assert.That(parked.RotationDegrees,Is.EqualTo(120f));
            Assert.That(parked.Dirtiness,Is.EqualTo(.17f));
            Assert.That(parked.Bloodiness,Is.EqualTo(.11f));
            Assert.That(npc.Inventory.Items.Contains(item),Is.False);
        });
    }
}

