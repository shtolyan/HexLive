using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    // §68: self first-aid. The in-place twin of the §53 Aid(Treat) beat — same
    // dressing, same decals, applied by the wounded girl to herself. Before
    // this the ONLY active wound care was a housemate walking over; her own
    // bandages were spent by the passive last-resort in NeedsDecaySystem, which
    // needs a zone below 0.4 AND blood below 0.35 and therefore never fired for
    // the common case (a dozen shallow bites, low mean HP, every zone > 0.5).
    private static void RunTreatSelf(WorldState world, NPCState npc)
    {
        if (npc.Needs.Bandages <= 0)
        {
            // Cooldown, or a decision layer that still believes she can treat
            // re-selects it every tick and she stands in an ExecFailed loop
            // (the CraftBandage death class, Jul 2026).
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.TreatWounds);
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "TreatSelf: no bandage left");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.TreatSelf;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec53.SelfTreatDuration;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"TreatSelf Duration={Spec53.SelfTreatDuration}ticks " +
                $"Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} " +
                $"Bandages={npc.Needs.Bandages}");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress ||
            npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // The dressing is spent. Spec 44: burn the pre-made medkit stock first;
        // only a HERBAL dressing leaves the plantain leaf-wrap decal, so the
        // leaf visual always means she actually gathered the leaves.
        var herbal = npc.Needs.HerbalBandages >= npc.Needs.Bandages;
        npc.Needs.Bandages--;
        if (herbal)
        {
            npc.Needs.HerbalBandages--;
        }

        var parts = new System.Collections.Generic.List<BodyPart>(npc.Body.Parts.Keys);
        var dressed = 0;
        foreach (var part in parts)
        {
            if (npc.Body.IsSevered(part) || npc.Body.Parts[part] >= 1f)
            {
                continue; // §50: a stump takes no dressing; a whole zone needs none
            }

            npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + Spec53.SelfTreatHeal);
            if (herbal)
            {
                npc.BandagedZones.Add(part);
                npc.GauzeZones.Remove(part);
            }
            else
            {
                npc.GauzeZones.Add(part);
                npc.BandagedZones.Remove(part);
            }

            dressed++;
        }

        // Spec 44 clotting: a dressed wound starts closing, which is also what
        // stops the bleed (NeedsDecaySystem only bleeds for Heal01 < 0.3).
        foreach (var wound in npc.Wounds)
        {
            wound.Heal01 = MathUtil.Clamp01(wound.Heal01 + Spec53.SelfTreatHeal);
        }

        npc.Health = npc.Body.Mean();
        npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + Spec53.SelfTreatBlood);

        Trace.Emit(world, npc.Id, "Bandaged",
            $"Dressed her own wounds ({(herbal ? "herbal" : "gauze")}, zones={dressed}) " +
            $"Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} Left={npc.Needs.Bandages}");

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        Trace.Emit(world, npc.Id, "CycleReset", "Goal->None Plan->Completed (treated her wounds)");
    }
}

}
