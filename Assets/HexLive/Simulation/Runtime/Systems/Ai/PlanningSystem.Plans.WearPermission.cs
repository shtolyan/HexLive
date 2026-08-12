using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;

namespace HexLive.Simulation.Runtime
{

public sealed partial class PlanningSystem
{
    /// <summary>Есть ли живое разрешение надеть именно эту вещь.</summary>
    internal static bool HasWearGrant(NPCState npc, ObjectId item, int tick)
    {
        foreach (var grant in npc.Mind.WearGrants)
        {
            if (grant.Item.Equals(item) && tick <= grant.ExpiresTick)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasWearDenial(NPCState npc, ObjectId item, int tick)
    {
        foreach (var denial in npc.Mind.WearDenials)
        {
            if (denial.Item.Equals(item) && tick <= denial.UntilTick)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §133: одеться нечем, кроме чужого — значит идём к хозяйке спрашивать.
    /// План короткий: дойти и спросить. Само надевание планируется потом,
    /// отдельным проходом, уже с разрешением на руках — так сцена не тащит за
    /// собой четырёхшаговый план, который пришлось бы перепроверять на каждом
    /// шаге (вещь могли забрать, хозяйка могла уйти).
    /// </summary>
    private bool TryBuildAskWearPermissionPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is null)
        {
            return false;
        }

        PerceivedObject bestGarment = null;
        NPCState bestOwner = null;
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!world.Entities.Objects.TryGetValue(perceived.Id, out var obj) ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) ||
                def.Layer is null ||
                !Content.GarmentLibrary.FitsSex(npc.Sex, obj.DefinitionId) ||
                HasWearDenial(npc, perceived.Id, world.Tick) ||
                HasWearGrant(npc, perceived.Id, world.Tick))
            {
                continue;
            }

            var owner = ClothingOwnership.FellowOwner(world, npc, obj);
            if (owner is null || owner.IsUnconscious(world.Tick) || owner.Movement.IsMoving ||
                owner.CurrentJunction is null ||
                (owner.Mind.PendingTalkFrom is { } claimed && !claimed.Equals(npc.Id)))
            {
                continue;
            }

            if (bestGarment is null || perceived.Distance < bestGarment.Distance)
            {
                bestGarment = perceived;
                bestOwner = owner;
            }
        }

        if (bestGarment is null || bestOwner.CurrentJunction is not { } ownerJunction)
        {
            return false;
        }

        var approach = TryReserveArmsLengthApproach(world, npc, bestOwner, ownerJunction);
        if (approach is not { } approachJunction)
        {
            return false;
        }

        // Та же заявка «я иду к тебе», что у разговора: хозяйка стоит на месте,
        // и второй проситель к ней не побежит.
        bestOwner.Mind.PendingTalkFrom = npc.Id;
        bestOwner.Mind.PendingTalkSinceTick = world.Tick;
        SocialCueSignals.Stamp(world, npc, "TalkRequest", bestOwner.Id);
        SocialCueSignals.Stamp(world, bestOwner, "TalkIncoming", npc.Id);

        npc.Plan.TargetAgentId = bestOwner.Id;
        npc.Plan.TargetObjectId = bestGarment.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = bestOwner.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.AskWearPermission,
            TargetJunction = approachJunction,
            TargetObject = bestGarment.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "WearPermissionAsking",
                $"Owner=NPC{bestOwner.Id.Value} Obj={bestGarment.Id.Value} " +
                $"Def={bestGarment.DefinitionId} Approach={approachJunction.Value}");
        }

        return true;
    }
}

}
