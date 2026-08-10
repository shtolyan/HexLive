using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// §115: план только подводит хозяина к чужаку. Реплика, выбор и бой
// принадлежат CampExpulsionSystem. TargetAgentId намеренно пуст:
// общий ExecutionSystem иначе примет это за приглашение поговорить.
public sealed partial class PlanningSystem
{
    private void BuildExpelPlan(WorldState world, NPCState npc)
    {
        // Отвечающий чужак тоже держит Expel, чтобы Decision/Planning
        // не вернули его к быту посреди ответа или драки. Ему идти некуда.
        if (npc.Mind.ExpulsionTargetNpcId is null &&
            npc.Mind.PendingExpulsionFrom is not null)
        {
            npc.Plan.Status = PlanStatus.Completed;
            return;
        }

        if (npc.Mind.ExpulsionTargetNpcId is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var intruder) ||
            intruder.Health <= 0f ||
            intruder.CurrentJunction is not { } targetJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            return;
        }

        if (InteractionReach.CanStrike(world, npc, intruder) ||
            (npc.CurrentJunction is { } current &&
             IsAdjacentJunction(world, current, targetJunction)))
        {
            npc.Plan.Status = PlanStatus.Completed;
            return;
        }

        if (PickApproachJunction(world, npc, targetJunction) is not { } approach)
        {
            npc.Plan.Status = PlanStatus.Completed;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CampExpelHolding",
                    $"Target=NPC{targetId.Value} NoFreeApproachJunction");
            }
            return;
        }

        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = intruder.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=Expel Target=NPC{targetId.Value} ApproachJunction={approach.Value}");
        }
    }
}

}
