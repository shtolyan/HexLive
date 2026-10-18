using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §127.13: the Sims slap as one ordinary bare-handed blow. The swing is the
/// shared MeleeSwing timeline (the view plays it as a §96 swing without a
/// fight); the blow skips §93.5 mutual hatred and never starts a fight.
/// </summary>
public sealed partial class ExecutionSystem
{
    private static void RunJealousStrike(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Plan.TargetAgentId is not { } cheaterId ||
            !world.Entities.Npcs.TryGetValue(cheaterId, out var cheater) ||
            cheater.Health <= 0f)
        {
            AbortJealousStrike(world, npc, "Jealousy: cheater vanished");
            return;
        }

        if (step.TimeoutEndTick is { } deadline && world.Tick > deadline)
        {
            AbortJealousStrike(world, npc, "Jealousy: gave up");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status == MovementStatus.Blocked)
        {
            AbortJealousStrike(world, npc, "Jealousy: approach blocked");
            return;
        }

        // Прибытие буквальное — стоя на зарезервированной точке подхода (§26.3 r2).
        if (step.TargetJunction is { } want &&
            (npc.CurrentJunction is not { } at || !at.Equals(want)))
        {
            return;
        }

        var inReach = InteractionReach.CanStrike(world, npc, cheater);
        if (npc.Mind.ForcedMeleeWeaponId is null)
        {
            // Arrived: fists, face him, and the swing gets its own short clock.
            if (!inReach)
            {
                AbortJealousStrike(world, npc, $"Jealousy: NPC{cheaterId.Value} out of reach");
                return;
            }

            npc.Mind.ForcedMeleeWeaponId = string.Empty;
            step.TimeoutEndTick = world.Tick + Spec127.JealousStrikeSwingTicks;
            var faceDelta = new Float2(
                cheater.Position.X - npc.Position.X, cheater.Position.Y - npc.Position.Y);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(HexSpatialMath.Normalize(faceDelta));
            SocialCueSignals.Stamp(world, npc, "JealousStrike", cheaterId);
        }

        if (!MeleeSwing.TryAdvanceHumanSwing(world, npc, cheater, inReach, 1f,
                out var damage, out var weaponId, out _))
        {
            return;
        }

        if (!inReach || damage <= 0f)
        {
            npc.PendingHumanStrikeTargetId = null;
            AbortJealousStrike(world, npc, "Jealousy: the blow missed");
            return;
        }

        MeleeSwing.ApplyHumanBlow(world, npc, cheater, damage, weaponId,
            "JealousStruck", mutualHatred: false);

        // As in the Sims, the slap ends what he was doing.
        if (cheater.Execution.CurrentInteraction == InteractionType.Romance &&
            cheater.Mind.RomanceLeaderNpcId is { } leaderId &&
            world.Entities.Npcs.TryGetValue(leaderId, out var leader))
        {
            AbortRomancePlan(world, leader, "CaughtCheating", leader.Mind.RomanceForced);
        }

        FinishJealousStrike(world, npc, step);
    }

    private static void FinishJealousStrike(WorldState world, NPCState npc, PlanStep step)
    {
        if (step.TargetJunction is { } approach)
        {
            SpatialMutations.ReleaseJunctionReservation(world, approach, npc.Id);
        }

        // The swing animation keeps playing out; only the scene's fists go.
        npc.Mind.ForcedMeleeWeaponId = null;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetAgentId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Mind.CurrentGoal = GoalType.None;
    }

    private static void AbortJealousStrike(WorldState world, NPCState npc, string reason)
    {
        PlanInterruption.TryAbort(world, npc, InterruptionCause.ExecutionFailure, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }

    /// <summary>§127.13: any teardown of the jealous walk returns her own
    /// weapon choice — fists were the scene's, not hers.</summary>
    internal static void ClearInterruptedJealousStrike(NPCState npc)
    {
        if (npc.Plan.Goal != GoalType.Jealousy &&
            npc.Mind.CurrentGoal != GoalType.Jealousy)
        {
            return;
        }

        npc.Mind.ForcedMeleeWeaponId = null;
        MeleeSwing.Cancel(npc);
    }
}

}
