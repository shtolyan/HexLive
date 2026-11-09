using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
public sealed class TerminalRetirementTests
{
    [TestCase(ExecutionStatus.Completed,PlanStatus.Completed)]
    [TestCase(ExecutionStatus.Failed,PlanStatus.Failed)]
    public void TerminalRetirementReleasesOwnClaimsWithoutInterruptingAResult(
        ExecutionStatus status,PlanStatus result)
    {
        var engine=TestWorld.CreateEngine();engine.Step();var world=engine.World;
        var npc=world.Entities.Npcs.Values.First();
        var other=world.Entities.Npcs.Values.First(n=>n.Id!=npc.Id);
        npc.Plan.Status=result;npc.Plan.Goal=GoalType.PlayerOrder;npc.Plan.Steps.Clear();
        npc.Execution.Status=status;npc.Execution.CurrentInteraction=null;
        npc.Execution.LastCompletedTick=world.Tick-1;
        npc.Movement.IsMoving=false;
        var points=world.Junctions.Items.Values.Where(j=>!j.Blocked &&
            SpatialQueries.IsJunctionFree(world,j.Id)).Take(2).ToArray();
        Assert.That(points,Has.Length.EqualTo(2));
        var owned=points[0];var foreign=points[1];
        Assert.That(SpatialMutations.TryReserveJunction(world,owned.Id,npc.Id,world.Tick,20),Is.True);
        Assert.That(SpatialMutations.TryReserveJunction(world,foreign.Id,other.Id,world.Tick,20),Is.True);
        npc.Plan.TargetJunctionId=owned.Id;
        npc.Plan.Steps.Add(new PlanStep{Type=PlanStepType.MoveToJunction,TargetJunction=foreign.Id});
        npc.ClaimedJunctions.Add(owned.Id);
        var item=WorldObjectMutations.SpawnObject(world,ContentIds.Log,owned.Fragment,owned.Tiles[0],owned.Id);
        item.IsOccupied=true;item.CurrentUser=npc.Id;
        var foreignItem=WorldObjectMutations.SpawnObject(world,ContentIds.Log,foreign.Fragment,foreign.Tiles[0],foreign.Id);
        foreignItem.IsOccupied=true;foreignItem.CurrentUser=other.Id;
        npc.Execution.TargetObject=item.Id;npc.Plan.TargetObjectId=item.Id;
        var standing=npc.CurrentJunction.Value;
        SpatialMutations.TryOccupyJunction(world,standing,npc.Id);
        var events=world.Events.Items.Count;
        var completed=npc.Execution.LastCompletedTick;
        Assert.That(PlanInterruption.TryRetireTerminalExecution(world,npc),Is.True);
        Assert.Multiple(()=>
        {
            Assert.That(npc.Execution.Status,Is.EqualTo(ExecutionStatus.None));
            Assert.That(npc.Execution.TargetObject,Is.Null);
            Assert.That(npc.Execution.LastCompletedTick,Is.EqualTo(completed));
            Assert.That(npc.Plan.Status,Is.EqualTo(result));
            Assert.That(npc.Plan.Goal,Is.EqualTo(GoalType.PlayerOrder));
            Assert.That(npc.ClaimedJunctions,Is.Empty);
            Assert.That(world.Entities.ObjectReservations.Any(o=>o.CurrentUser==npc.Id && o.IsOccupied),Is.False);
            Assert.That(item.IsOccupied,Is.False);
            Assert.That(item.CurrentUser,Is.Null);
            Assert.That(foreignItem.IsOccupied,Is.True);
            Assert.That(foreignItem.CurrentUser,Is.EqualTo(other.Id));
            Assert.That(world.Reservations.Junctions.ContainsKey(owned.Id),Is.False);
            Assert.That(world.Reservations.Junctions[foreign.Id].Owner,Is.EqualTo(other.Id));
            Assert.That(world.Occupancy.JunctionOwner[standing],Is.EqualTo(npc.Id));
            Assert.That(world.Events.Items.Count,Is.EqualTo(events),"A retired result is not GoalInterrupted.");
        });
    }

    [TestCase("active-plan")]
    [TestCase("running-interaction")]
    [TestCase("held-item")]
    [TestCase("pose")]
    [TestCase("movement")]
    public void AStateWithLiveWorkCannotUseTerminalRetirement(string live)
    {
        var engine=TestWorld.CreateEngine();engine.Step();var world=engine.World;
        var npc=world.Entities.Npcs.Values.First();
        npc.Plan.Status=PlanStatus.Completed;npc.Plan.Steps.Clear();
        npc.Execution.Status=ExecutionStatus.Completed;npc.Execution.CurrentInteraction=null;
        npc.Movement.IsMoving=false;
        switch(live)
        {
            case "active-plan":npc.Plan.Status=PlanStatus.Active;break;
            case "running-interaction":npc.Execution.Status=ExecutionStatus.InProgress;break;
            case "held-item":npc.Execution.HeldGarment=new ItemInstance(ContentIds.Cloth);break;
            case "pose":npc.Execution.CurrentInteraction=InteractionType.Plant;break;
            case "movement":npc.Movement.IsMoving=true;break;
        }
        var claim=world.Junctions.Items.Values.First(j=>!j.Blocked && SpatialQueries.IsJunctionFree(world,j.Id));
        Assert.That(SpatialMutations.TryReserveJunction(world,claim.Id,npc.Id,world.Tick,20),Is.True);
        npc.Plan.TargetJunctionId=claim.Id;
        npc.ClaimedJunctions.Add(claim.Id);
        var objectClaim=WorldObjectMutations.SpawnObject(world,ContentIds.Log,claim.Fragment,claim.Tiles[0],claim.Id);
        objectClaim.IsOccupied=true;objectClaim.CurrentUser=npc.Id;
        npc.Execution.TargetObject=objectClaim.Id;
        var events=world.Events.Items.Count;
        var before=Save(world);
        Assert.That(PlanInterruption.TryRetireTerminalExecution(world,npc),Is.False);
        Assert.That(Save(world),Is.EqualTo(before));
        Assert.That(world.Events.Items.Count,Is.EqualTo(events));
        Assert.That(world.Reservations.Junctions[claim.Id].Owner,Is.EqualTo(npc.Id));
        Assert.That(npc.ClaimedJunctions,Does.Contain(claim.Id));
        Assert.That(objectClaim.IsOccupied,Is.True);
        Assert.That(objectClaim.CurrentUser,Is.EqualTo(npc.Id));
    }

    private static byte[] Save(WorldState world)
    {
        using var bytes=new MemoryStream();using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
            WorldSaveSerializer.Write(world,writer);
        return bytes.ToArray();
    }
}
