using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§26.26: occupancy must have a live consumer, including after load.
/// Cost follows NPC plans and indexed claims, never the island's object count.</summary>
public sealed class ObjectReservationSystem : ISimulationSystem
{
    public string Name => nameof(ObjectReservationSystem);
    public TickLayer Layer => TickLayer.Medium;
    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world) => Reconcile(world);

    internal static void Reconcile(WorldState world)
    {
        if (world.Entities.ObjectReservations.Count == 0) return;
        var live = world.Entities.LiveObjectReservations;
        var usedBodies = world.Entities.UsedBodyObjects;
        live.Clear();
        usedBodies.Clear();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f) continue;
            var hasPlan = npc.Plan.Status == PlanStatus.Active &&
                npc.Plan.CurrentStepIndex >= 0 && npc.Plan.CurrentStepIndex < npc.Plan.Steps.Count;
            if (npc.Execution.Status == ExecutionStatus.InProgress && !npc.IsBeingCarried &&
                (hasPlan || npc.Execution.CurrentInteraction == InteractionType.Sleep))
            {
                if (npc.Execution.TargetObject is { } target)
                {
                    live.Add((target, npc.Id));
                    usedBodies.Add(target);
                }
                if (npc.Execution.CraftProjectId is { } project) live.Add((project, npc.Id));
                foreach (var piece in npc.Execution.CraftLayout) live.Add((piece, npc.Id));
            }

            if (!hasPlan) continue;

            // A long approach and a future leg are legitimate reservations.
            // Their existing movement/step timeout owns cancellation, not a TTL
            // that could steal a bed or an ingredient from a progressing actor.
            if (npc.Plan.TargetObjectId is { } planTarget) live.Add((planTarget, npc.Id));
            for (var i = npc.Plan.CurrentStepIndex; i < npc.Plan.Steps.Count; i++)
                if (npc.Plan.Steps[i].TargetObject is { } stepTarget)
                    live.Add((stepTarget, npc.Id));

            // Rescue reserves for the PATIENT even before the pickup beat.
            if (npc.Plan.Goal == GoalType.Rescue &&
                npc.Plan.TargetAgentId is { } patient &&
                world.Entities.Npcs.ContainsKey(patient) &&
                npc.RescueDestinationObjectId is { } destination)
                live.Add((destination, patient));
        }

        // Writes remove entries from the index, so iterate a claim-only copy.
        var claims = world.Entities.ObjectReservationScratch;
        claims.Clear();
        claims.AddRange(world.Entities.ObjectReservations);
        foreach (var obj in claims)
        {
            if (PreservesIdentity(world, obj))
            {
                // CurrentUser identifies the deceased/limb owner, not the mourner.
                if (obj.IsOccupied && !usedBodies.Contains(obj.Id)) obj.IsOccupied = false;
                // Provenance alone is not an active claim. A later occupancy
                // write automatically puts this object back into the index.
                if (!obj.IsOccupied) world.Entities.ObjectReservations.Remove(obj);
                continue;
            }
            if (obj.CurrentUser is { } owner && live.Contains((obj.Id, owner)))
            {
                obj.IsOccupied = true;
                continue;
            }
            obj.IsOccupied = false;
            obj.CurrentUser = null;
        }
        claims.Clear();
    }

    internal static void ReleaseForPlan(WorldState world, NPCState npc)
    {
        var claims = world.Entities.ObjectReservationScratch;
        claims.Clear();
        claims.AddRange(world.Entities.ObjectReservations);
        foreach (var obj in claims)
        {
            if (PreservesIdentity(world, obj))
            {
                if (npc.Execution.TargetObject == obj.Id) obj.IsOccupied = false;
                continue;
            }
            if (obj.CurrentUser != npc.Id) continue;
            obj.IsOccupied = false;
            obj.CurrentUser = null;
        }
        claims.Clear();
    }

    private static bool PreservesIdentity(WorldState world, WorldObjectState obj) =>
        MobLimbPrize.IsSeveredLimb(obj) ||
        world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
        CorpseMath.IsHumanDead(definition);
}

}
