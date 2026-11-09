using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
namespace HexLive.Simulation.Runtime
{
public sealed partial class ExecutionSystem
{
    private static void RunDoorOrder(WorldState world,NPCState npc)
    {
        if(npc.Plan.TargetObjectId is not {} id || !world.Entities.Objects.TryGetValue(id,out var door) ||
            !BuildingDoorRules.IsDoor(door) || npc.Plan.TargetJunctionId is not {} target)
        { FailDoorOrder(world,npc,"Door disappeared"); return; }
        if(npc.Movement.IsMoving) return;
        if(npc.Movement.Status==MovementStatus.Blocked)
        { FailDoorOrder(world,npc,"Door approach blocked"); return; }
        if(npc.CurrentJunction!=target) return;
        var breaking=npc.Plan.Steps[^1].Type==PlanStepType.BreakDoor;
        if(door.IsDoorOpen) { CompleteDoorOrder(world,npc,door); return; }
        var tool=breaking?DoorInteractionMath.Tool(npc):null;
        if(!npc.Body.HasUsableHand || !npc.Body.CanUseToolsOrWeapons || (breaking && tool==null))
        { FailDoorOrder(world,npc,"Door tool/hand lost"); return; }
        if(door.IsOccupied && door.CurrentUser!=npc.Id)
        { FailDoorOrder(world,npc,"Door occupied"); return; }
        if(npc.Execution.Status!=ExecutionStatus.InProgress)
        {
            var duration=breaking?(int)Math.Ceiling(DoorInteractionMath.BreakWorkTicks(world,npc)*door.Durability):DoorInteractionMath.KnockTicks;
            door.IsOccupied=true; door.CurrentUser=npc.Id;
            npc.Execution.Status=ExecutionStatus.InProgress;
            npc.Execution.TargetObject=door.Id;
            npc.Execution.CurrentInteraction=breaking?InteractionType.Harvest:(InteractionType?)null;
            npc.Execution.ActionTool=tool;
            npc.Execution.StartTick=world.Tick;
            npc.Execution.EndTick=world.Tick+Math.Max(1,duration);
            if(breaking) DoorInteractionMath.BreakAttempt(world,npc,door);
            if(door.IsDoorOpen) CompleteDoorOrder(world,npc,door);
            return;
        }
        npc.Execution.ActionTool=tool;
        if(breaking)
        {
            door.Durability=Math.Max(0f,door.Durability-1f/DoorInteractionMath.BreakWorkTicks(world,npc));
            if((world.Tick-npc.Execution.StartTick)%DoorInteractionMath.StressPulseTicks==0)
                DoorInteractionMath.StressPulse(world,npc,door);
        }
        if(world.Tick<npc.Execution.EndTick) return;
        if(breaking)
        {
            door.Durability=0f;
            BuildingDoorRules.TryOpen(world,door.Id);
            Trace.Emit(world,npc.Id,"DoorBroken",$"Door={door.Id.Value}");
        }
        else DoorInteractionMath.Knock(world,npc,door);
        CompleteDoorOrder(world,npc,door);
    }
    private static void CompleteDoorOrder(WorldState world,NPCState npc,WorldObjectState door)
    {
        if(door.CurrentUser==npc.Id) { door.CurrentUser=null; door.IsOccupied=false; }
        if(npc.Plan.TargetJunctionId is {} stand) SpatialMutations.ReleaseJunctionReservation(world,stand,npc.Id);
        npc.Execution.Status=ExecutionStatus.Completed;
        npc.Execution.LastCompletedTick=world.Tick;
        npc.Execution.CurrentInteraction=null;
        npc.Execution.TargetObject=null;
        npc.Execution.ActionTool=null;
        npc.Plan.Status=PlanStatus.Completed;
        npc.Plan.CurrentStepIndex=npc.Plan.Steps.Count;
        npc.Mind.CurrentGoal=GoalType.None;
        ManualControlMath.RenewInactivityLease(world,npc);
    }
    private static void FailDoorOrder(WorldState world,NPCState npc,string reason)
    {
        PlanInterruption.TryAbort(world,npc,InterruptionCause.ExecutionFailure,reason);
        npc.Mind.CurrentGoal=GoalType.None;
    }
}
}
