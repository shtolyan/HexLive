using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

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
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } walkTarget)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.TreatWounds);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                    "TreatSelf: bandage source unreachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            if (npc.CurrentJunction is not { } at || !at.Equals(walkTarget))
            {
                return;
            }
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            ObjectId? claimedSource = null;
            if (npc.Plan.TargetObjectId is { } requestedSource &&
                MedicalSupplyMath.TryClaimBandageSource(world, npc, requestedSource))
            {
                claimedSource = requestedSource;
            }

            if (!claimedSource.HasValue && MedicalSupplyMath.BandageCount(npc) <= 0)
            {
                // Cooldown, or a decision layer that still believes she can
                // treat re-selects it every tick and she stands in an
                // ExecFailed loop (the CraftBandage death class, Jul 2026).
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.TreatWounds);
                PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                    "TreatSelf: neither carried nor claimable nearby bandage");
                npc.Mind.CurrentGoal = GoalType.None;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ExecFailed",
                        "TreatSelf: neither carried nor claimed nearby bandage");
                }
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.TreatSelf;
            npc.Execution.TargetObject = claimedSource;
            npc.Execution.StartTick = world.Tick;
            // §76: a practised hand winds a dressing faster (Wits + Medicine).
            var treatTicks = Spec118.Enabled
                ? WoundMath.BandageTicks(npc)
                : AttributeMath.WorkTicks(
                    npc, Spec53.SelfTreatDuration, InteractionType.TreatSelf, npc.Plan.Goal);
            npc.Execution.EndTick = world.Tick + treatTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"TreatSelf Duration={treatTicks}ticks " +
                    $"Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} " +
                    $"InventoryBandages={MedicalSupplyMath.BandageCount(npc)} " +
                    $"Source={claimedSource?.Value.ToString() ?? "inventory"}");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress ||
            npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // The dressing is spent only now. Spec 44: burn pre-made gauze before
        // herbal wraps inside either source. A claimed world source can vanish
        // during the beat; a newly received carried dressing is then a safe
        // fallback, otherwise no treatment effect is produced.
        var spent = false;
        var herbal = false;
        if (npc.Execution.TargetObject is { } sourceId)
        {
            spent = MedicalSupplyMath.TrySpendClaimedBandageSource(
                world, npc, sourceId, out herbal);
        }

        if (!spent)
        {
            spent = MedicalSupplyMath.TrySpendBandage(npc, out herbal);
        }

        if (!spent)
        {
            if (npc.Execution.TargetObject is { } failedSource)
            {
                MedicalSupplyMath.ReleaseBandageSource(world, npc, failedSource);
            }
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.TreatWounds);
            PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure,
                "TreatSelf: reserved bandage vanished before completion");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (Spec118.Enabled)
        {
            var stabilized = WoundMath.StabilizeMostDangerous(npc, herbal, out var wound);
            Trace.Emit(world, npc.Id, "Bandaged",
                stabilized
                    ? $"Stabilized {wound.Zone} wound #{wound.Id} " +
                      $"({(herbal ? "herbal" : "gauze")}); no instant HP/Blood"
                    : "No open wound remained when the dressing completed");
        }
        else
        {
        // §76: a practised hand gets more out of the same dressing. Computed
        // once so the zone HP and the wound clotting below cannot disagree.
        var selfTreatHeal = Spec53.SelfTreatHeal * AttributeMath.TreatPowerMult(npc);
        // §105 r3: свои руки ограничены тем же потолком, что и чужие. Иначе
        // «полечить себя до сотни» осталось бы лазейкой мимо всего правила.
        var selfTreatCap = AttributeMath.TreatCap(npc);

        var parts = new System.Collections.Generic.List<BodyPart>(npc.Body.Parts.Keys);
        var dressed = 0;
        foreach (var part in parts)
        {
            if (npc.Body.IsSevered(part) || npc.Body.Parts[part] >= selfTreatCap)
            {
                // §50: a stump takes no dressing; §105 r3: зона выше потолка её
                // умений — тоже (бинт ей уже ничего не добавит).
                continue;
            }

            npc.Body.Parts[part] = System.Math.Min(
                selfTreatCap, npc.Body.Parts[part] + selfTreatHeal);
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
            wound.Heal01 = MathUtil.Clamp01(wound.Heal01 + selfTreatHeal);
        }

        npc.Health = npc.Body.Mean();
        npc.Needs.Blood = MathUtil.Clamp01(
            npc.Needs.Blood + Spec53.SelfTreatBlood * AttributeMath.TreatPowerMult(npc));

        // §76: dressing your own wounds is how Medicine is learned when there
        // is nobody else to practise on.
        SkillTrace.Award(world, npc, InteractionType.TreatSelf, Spec53.SelfTreatDuration);

        Trace.Emit(world, npc.Id, "Bandaged",
            $"Dressed her own wounds ({(herbal ? "herbal" : "gauze")}, zones={dressed}) " +
            $"Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} " +
            $"Left={MedicalSupplyMath.BandageCount(npc)}");
        }

        if (npc.Plan.TargetJunctionId is { } reserved)
        {
            SpatialMutations.ReleaseJunctionReservation(world, reserved, npc.Id);
        }
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CycleReset", "Goal->None Plan->Completed (treated her wounds)");

        }
    }
}

}
