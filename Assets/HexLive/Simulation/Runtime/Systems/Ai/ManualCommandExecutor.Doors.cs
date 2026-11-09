using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
namespace HexLive.Simulation.Runtime
{
internal static partial class ManualCommandExecutor
{
    private static void ApplyDoorOrder(WorldState world, EntityId id, ObjectId doorId, bool breaking, AdmissionTracker admission)
    {
        var verb = breaking ? "BreakDoor" : "KnockDoor";
        if (!Spec129.Enabled) { admission.Reject("FeatureDisabled"); return; }
        if (!TryTakeOrder(world, id, verb, true, admission, out var npc)) return;
        if (Incapacitated(world, npc) || !npc.Body.HasUsableHand || !npc.Body.CanUseToolsOrWeapons ||
            !world.Entities.Objects.TryGetValue(doorId, out var door) || !BuildingDoorRules.IsDoor(door) ||
            door.Junctions.Count != 1 || door.IsDoorOpen || door.Durability <= 0f)
        { Reject(world,id,verb,"TargetUnavailable",admission); return; }
        if (breaking && DoorInteractionMath.Tool(npc) == null)
        { Reject(world,id,verb,"MissingTool",admission); return; }
        if (door.IsOccupied && door.CurrentUser != npc.Id)
        { Reject(world,id,verb,"Occupied",admission); return; }
        ClearForNewOrder(world,npc,"Door order");
        ClearAttackOrder(world,npc);
        if (!DoorInteractionMath.TryReserveApproach(world,npc,door,out var stand))
        { Reject(world,id,verb,"Unreachable",admission); return; }
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetObjectId = door.Id;
        npc.Plan.TargetTile = door.Tile;
        npc.Plan.TargetJunctionId = stand;
        npc.Plan.Steps.Add(new PlanStep { Type=PlanStepType.MoveToJunction,TargetJunction=stand });
        npc.Plan.Steps.Add(new PlanStep { Type=breaking?PlanStepType.BreakDoor:PlanStepType.KnockDoor,
            TargetObject=door.Id,TargetJunction=stand });
        npc.Plan.Status=PlanStatus.Active;
        npc.Mind.CurrentGoal=GoalType.PlayerOrder;
    }
}
}
