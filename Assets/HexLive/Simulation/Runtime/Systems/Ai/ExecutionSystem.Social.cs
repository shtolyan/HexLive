using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    // Spec 28.15A: agent-targeted Talk. The listener stays passive — only the
    // initiator runs this state machine; both sides receive gains at the end.
    // Spec 29C.9 (iter 30): a real conversation, not a one-second exchange.
    // Spec §49: linger longer (40->90) but each chat sates less (0.40->0.20,
    // 0.25->0.12) — they STAND and talk visibly, yet still want another chat
    // later instead of one exchange topping the bar off for the day. The gap
    // is filled by the passive ambient-social trickle (NeedsDecaySystem).
    private static int TalkDurationTicks => Spec49.TalkDuration;

    private static float TalkInitiatorSocialGain => Spec49.TalkInitGain;

    private static float TalkListenerSocialGain => Spec49.TalkListenGain;

    private static float TalkRelationshipGain => SocialBalance.TalkRelationshipGain;

    // Spec 28.15B: quarrels and refusal-by-dislike.
    private static float QuarrelInitiatorSocialGain => SocialBalance.QuarrelInitiatorSocialGain;

    private static float QuarrelListenerSocialGain => SocialBalance.QuarrelListenerSocialGain;

    private static float QuarrelAffinityLoss => SocialBalance.QuarrelAffinityLoss;

    private static float QuarrelEmbarrassment => SocialBalance.QuarrelEmbarrassment;

    private static float RefusalAffinityThreshold => SocialBalance.RefusalAffinityThreshold;

    private static float LonelinessOverrideThreshold => SocialBalance.LonelinessOverrideThreshold;

    private static float RejectionAffinityPenalty => SocialBalance.RejectionAffinityPenalty;

    private static void RunTalk(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetAgentId is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var target))
        {
            AbortTalk(world, npc, "Talk target vanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
        {
            return;
        }

        // Spec 26.3 r2: a Blocked walk with an empty path fell through this
        // gate and the chat started from wherever she stood.
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Movement.Status == MovementStatus.Blocked)
        {
            AbortTalk(world, npc, "Talk approach blocked (no route)");
            return;
        }

        // Spec 26.3 r2: arrival is literal — standing ON the reserved approach
        // junction. PathfindingSystem re-routes next tick (or goes Blocked and
        // the gate above aborts).
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } wantJunction &&
            (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(wantJunction)))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            var distance = HexSpatialMath.Distance(npc.Position, target.Position);
            if (!InteractionReach.CheckStart(world, npc, target.Position,
                    InteractionReach.Talk, $"Talk NPC{targetId.Value}"))
            {
                AbortTalk(world, npc, $"Target NPC{targetId.Value} out of talk range");
                return;
            }

            // Spec 28.9 (v1 acceptance): busy, starving, or walking-through
            // listeners refuse — and so do listeners who dislike the initiator
            // (28.15B), unless they are lonely enough for a reconciliation.
            // Spec 28.8: conversation partners turn to face each other.
            var faceDelta = new Float2(
                target.Position.X - npc.Position.X,
                target.Position.Y - npc.Position.Y);
            var faceDirection = HexSpatialMath.Normalize(faceDelta);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
            // §60: don't spin a lying listener (coma/asleep/prone) to face the
            // speaker — she keeps her authored lying pose.
            if (!target.IsLyingDown(world.Tick))
            {
                target.RotationDegrees = HexSpatialMath.AngleDegrees(
                    new Float2(-faceDirection.X, -faceDirection.Y));
            }

            // §60: a listener who collapsed while the initiator was walking
            // over counts as busy — the neutral "sorry, busy" refusal, no
            // resentment (you can't be insulted by a coma).
            var targetBusy = (target.Execution.Status == ExecutionStatus.InProgress &&
                target.Execution.CurrentInteraction != InteractionType.Talk) ||
                target.IsUnconscious(world.Tick);
            var listenerAffinity = target.Social.GetOrCreate(npc.Id).Affinity;
            var dislikes = listenerAffinity < RefusalAffinityThreshold &&
                target.Needs.Social >= LonelinessOverrideThreshold;
            if (targetBusy || target.Mind.IsStarving || target.Movement.IsMoving || dislikes)
            {
                // Spec 28.10/28.15B: only a *personal* refusal breeds resentment;
                // "sorry, busy" is not an insult (a lesson from the first soak:
                // penalizing neutral refusals created an irreversible spiral).
                var rejectedRel = npc.Social.GetOrCreate(target.Id);
                if (dislikes)
                {
                    rejectedRel.Affinity = MathUtil.Clamp(
                        rejectedRel.Affinity - RejectionAffinityPenalty, -1f, 1f);
                    npc.Execution.LastTalkResultTick = world.Tick;
                    npc.Execution.LastTalkAffinityDelta = -RejectionAffinityPenalty;
                    Trace.Emit(world, npc.Id, "RelationshipChanged",
                        $"NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                        $"Aff={rejectedRel.Affinity:F2} (-{RejectionAffinityPenalty:F2}) " +
                        $"after talk refusal");
                }

                SocialCueSignals.Stamp(world, npc, "TalkRejected", target.Id);
                SocialCueSignals.Stamp(world, target, "TalkRefused", npc.Id);
                Trace.Emit(world, npc.Id, "InteractionRejected",
                    $"Talk rejected by NPC{targetId.Value} " +
                    $"(Busy={targetBusy} Starving={target.Mind.IsStarving} " +
                    $"Moving={target.Movement.IsMoving} Dislikes={dislikes} " +
                    $"ListenerAff={listenerAffinity:F2}) MyAff->{rejectedRel.Affinity:F2}");
                AbortTalk(world, npc, $"Talk rejected by NPC{targetId.Value}");
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Talk;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + TalkDurationTicks;
            // Spec 28.15E: pick the conversation subject (context-biased,
            // deterministic) — the presentation shows the matching emoji over
            // the speaker's head for the talk.
            // §67.10: the SHARED subject is only a default. Each participant
            // then gets HER OWN topic: a colonist who is starving/parched/hurt
            // tells her housemate about it instead of chatting coconuts, so the
            // two bubbles differ and read as a real exchange.
            var topic = PickTalkTopic(world, npc, target);
            npc.Execution.CurrentTalkTopic = PickSpeakerTopic(world, npc, target, topic);
            target.Execution.CurrentTalkTopic = PickSpeakerTopic(world, target, npc, topic);
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            Trace.Emit(world, npc.Id, "TalkStarted",
                $"With NPC{targetId.Value} Topic={topic} " +
                $"Mine={npc.Execution.CurrentTalkTopic} Hers={target.Execution.CurrentTalkTopic} " +
                $"Duration={TalkDurationTicks}ticks " +
                $"({TalkDurationTicks * world.TickDeltaTime:F1}s) Dist={distance:F2}");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            if (remaining > 0)
            {
                // §67.10: a 90-tick chat is not one sentence — the subject moves
                // on every TalkTopicRefreshTicks (and follows whatever each of
                // them is suffering right now), so the bubbles change mid-talk.
                RefreshTalkTopics(world, npc, target);
                return;
            }

            // Spec 28.15B: talk outcome roll. Cranky participants quarrel;
            // mutual affinity protects. Deterministic (hash-based).
            var initiatorRel = npc.Social.GetOrCreate(target.Id);
            var listenerRel = target.Social.GetOrCreate(npc.Id);
            var irritability = 0.5f * (
                System.Math.Max(npc.Needs.Hunger, 1f - npc.Needs.Energy) +
                System.Math.Max(target.Needs.Hunger, 1f - target.Needs.Energy));
            var mutualAffinity = (initiatorRel.Affinity + listenerRel.Affinity) * 0.5f;
            var friendshipProtection = 0.25f * System.Math.Max(0f, mutualAffinity);
            var hostilitySpice = 0.10f * System.Math.Max(0f, -mutualAffinity);
            var quarrelChance = MathUtil.Clamp(
                0.10f + 0.35f * irritability - friendshipProtection + hostilitySpice,
                0.05f, 0.60f);
            var roll = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, target.Id.Value);
            var quarreled = roll < quarrelChance;

            initiatorRel.Familiarity = MathUtil.Clamp01(initiatorRel.Familiarity + TalkRelationshipGain);
            listenerRel.Familiarity = MathUtil.Clamp01(listenerRel.Familiarity + TalkRelationshipGain);

            if (quarreled)
            {
                npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + QuarrelInitiatorSocialGain);
                target.Needs.Social = MathUtil.Clamp01(target.Needs.Social + QuarrelListenerSocialGain);
                initiatorRel.Affinity = MathUtil.Clamp(initiatorRel.Affinity - QuarrelAffinityLoss, -1f, 1f);
                listenerRel.Affinity = MathUtil.Clamp(listenerRel.Affinity - QuarrelAffinityLoss, -1f, 1f);
                npc.Social.Embarrassment = MathUtil.Clamp01(npc.Social.Embarrassment + QuarrelEmbarrassment);
                target.Social.Embarrassment = MathUtil.Clamp01(target.Social.Embarrassment + QuarrelEmbarrassment);
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Socialize);
                SocialCueSignals.Stamp(world, npc, "TalkQuarrel", target.Id);
                SocialCueSignals.Stamp(world, target, "TalkQuarrel", npc.Id);

                Trace.Emit(world, npc.Id, "TalkQuarreled",
                    $"With NPC{targetId.Value} Chance={quarrelChance:F2} Roll={roll:F2} " +
                    $"Irritability={irritability:F2} MutualAff={mutualAffinity:F2} " +
                    $"Social={npc.Needs.Social:F2} TargetSocial={target.Needs.Social:F2}");
            }
            else
            {
                npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + TalkInitiatorSocialGain);
                target.Needs.Social = MathUtil.Clamp01(target.Needs.Social + TalkListenerSocialGain);
                initiatorRel.Affinity = MathUtil.Clamp(initiatorRel.Affinity + TalkRelationshipGain, -1f, 1f);
                listenerRel.Affinity = MathUtil.Clamp(listenerRel.Affinity + TalkRelationshipGain, -1f, 1f);
                SocialCueSignals.Stamp(world, npc, "TalkSuccess", target.Id);
                SocialCueSignals.Stamp(world, target, "TalkSuccess", npc.Id);

                Trace.Emit(world, npc.Id, "TalkCompleted",
                    $"With NPC{targetId.Value} Chance={quarrelChance:F2} Roll={roll:F2} " +
                    $"SocialGain=[{TalkInitiatorSocialGain:F2}/{TalkListenerSocialGain:F2}] " +
                    $"Social={npc.Needs.Social:F2} TargetSocial={target.Needs.Social:F2}");
            }

            // Spec 28.15E: stamp the outcome on BOTH so a Sims-style "+/-"
            // relationship pop can float over each head (both relationships
            // moved). Sign = direction, magnitude = single vs double glyph.
            var affinityDelta = quarreled ? -QuarrelAffinityLoss : TalkRelationshipGain;
            npc.Execution.LastTalkResultTick = world.Tick;
            npc.Execution.LastTalkAffinityDelta = affinityDelta;
            target.Execution.LastTalkResultTick = world.Tick;
            target.Execution.LastTalkAffinityDelta = affinityDelta;

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;
            // Spec 31C.8: the snapshot must not report a finished interaction —
            // the view would keep the pose while the body walks away.
            npc.Execution.CurrentInteraction = null;
            // Spec 28.15E: talk's over — drop the topic so the bubble clears.
            npc.Execution.CurrentTalkTopic = null;
            target.Execution.CurrentTalkTopic = null;

            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.FreeJunction(world, jId, npc.Id);
                SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
            }
            Trace.Emit(world, npc.Id, "RelationshipChanged",
                $"NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                $"Fam={initiatorRel.Familiarity:F2} (+{TalkRelationshipGain:F2}) " +
                $"Aff={initiatorRel.Affinity:F2} ({affinityDelta:+0.00;-0.00})");
            Trace.Emit(world, target.Id, "RelationshipChanged",
                $"NPC{target.Id.Value}->NPC{npc.Id.Value} " +
                $"Fam={listenerRel.Familiarity:F2} (+{TalkRelationshipGain:F2}) " +
                $"Aff={listenerRel.Affinity:F2} ({affinityDelta:+0.00;-0.00})");

            if (target.Mind.PendingTalkFrom is { } inviterId && inviterId.Equals(npc.Id))
            {
                target.Mind.PendingTalkFrom = null;
            }

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetAgentId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (talked)");
        }
    }

    private static void AbortTalk(WorldState world, NPCState npc, string reason)
    {
        // Spec 28.15E: a dropped talk clears its topic so no bubble lingers.
        npc.Execution.CurrentTalkTopic = null;
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Socialize);
        PlanInterruption.Abort(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }

    // Spec §53: what a suffering NPC most needs help with right now, and how
    // badly (0..1). Mirrors the perception build so a helper re-checks on
    // arrival — she may have recovered, worsened, or died on the way over.
    private static AidKind AssessAidKind(NPCState t, int tick, out float severity)
    {
        severity = 0f;
        if (t.Health <= 0f)
        {
            return AidKind.None;
        }

        var treatSev = t.Wounds.Count > 0 || t.Needs.Blood < 0.6f
            ? System.Math.Max(1f - t.Needs.Blood, 1f - t.Health)
            : 0f;
        var medSev = t.Mind.SickUntilTick > tick
            ? 0.6f
            : (t.Health < 0.4f && t.Wounds.Count == 0 ? 1f - t.Health : 0f);
        var hydrateSev = t.Needs.Thirst >= 0.55f ? t.Needs.Thirst : 0f;
        var feedSev = t.Needs.Hunger >= 0.55f ? t.Needs.Hunger : 0f;
        var consoleSev = tick < t.Mind.GrievingUntilTick ? 0.5f : 0f;
        if (t.Needs.Stress > 0.6f)
        {
            consoleSev = System.Math.Max(consoleSev, t.Needs.Stress * 0.6f);
        }

        var kind = AidKind.Treat;
        severity = treatSev;
        if (medSev > severity) { severity = medSev; kind = AidKind.Medicate; }
        if (hydrateSev > severity) { severity = hydrateSev; kind = AidKind.Hydrate; }
        if (feedSev > severity) { severity = feedSev; kind = AidKind.Feed; }
        if (consoleSev > severity) { severity = consoleSev; kind = AidKind.Console; }
        return severity <= 0f ? AidKind.None : kind;
    }

    // Spec §53: apply the help to the TARGET (no item is spent — the relief is
    // applied directly, so aid can never bankrupt the colony). Both sides' bond
    // is credited by the caller.
    private static void ApplyAidRelief(WorldState world, NPCState helper, NPCState target, AidKind kind)
    {
        switch (kind)
        {
            case AidKind.Feed:
                target.Needs.Hunger = MathUtil.Clamp01(target.Needs.Hunger - Spec53.FeedRelief);
                break;

            case AidKind.Hydrate:
                target.Needs.Thirst = MathUtil.Clamp01(target.Needs.Thirst - Spec53.HydrateRelief);
                break;

            case AidKind.Treat:
            {
                // Lift every intact wounded part and stop the bleed, and drop a
                // gauze wrap decal on the treated zones (mirrors self first-aid).
                var parts = new System.Collections.Generic.List<BodyPart>(target.Body.Parts.Keys);
                foreach (var part in parts)
                {
                    if (target.Body.IsSevered(part))
                    {
                        continue;
                    }
                    if (target.Body.Parts[part] < 1f)
                    {
                        target.Body.Parts[part] = MathUtil.Clamp01(target.Body.Parts[part] + Spec53.TreatHeal);
                        target.GauzeZones.Add(part);
                    }
                }
                foreach (var wound in target.Wounds)
                {
                    wound.Heal01 = MathUtil.Clamp01(wound.Heal01 + Spec53.TreatHeal);
                }
                target.Needs.Blood = MathUtil.Clamp01(target.Needs.Blood + Spec53.TreatBlood);
                break;
            }

            case AidKind.Medicate:
                target.Health = MathUtil.Clamp01(target.Health + Spec53.MedicateHeal);
                target.Needs.Blood = MathUtil.Clamp01(target.Needs.Blood + Spec53.TreatBlood * 0.5f);
                // A dose settles the sickness window and its remaining damage.
                target.Mind.SickUntilTick = 0;
                target.Mind.SicknessDamageRemaining = 0f;
                break;

            case AidKind.Console:
                target.Needs.Stress = MathUtil.Clamp01(target.Needs.Stress - Spec53.ConsoleStressRelief);
                // Sitting with her shortens the mourning a little.
                if (world.Tick < target.Mind.GrievingUntilTick)
                {
                    target.Mind.GrievingUntilTick = System.Math.Max(
                        world.Tick, target.Mind.GrievingUntilTick - 600);
                }
                // Comforting someone eases the comforter's own tension a touch.
                helper.Needs.Stress = MathUtil.Clamp01(
                    helper.Needs.Stress - Spec53.ConsoleStressRelief * 0.3f);
                break;
        }
    }

    // Spec §53: walk-up-and-help execution. Structured like RunTalk (arrive,
    // begin, run for AidDuration, complete) but with no refusal roll — a
    // sufferer doesn't turn help away — and a care outcome instead of a chat.
    private static void RunAid(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetAgentId is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var target))
        {
            AbortAid(world, npc, "Aid target vanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
        {
            return;
        }

        // Spec 26.3 r2: a blocked walk means she never reached arm's length —
        // abort and replan a fresh approach instead of feeding from afar.
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Movement.Status == MovementStatus.Blocked)
        {
            AbortAid(world, npc, "Aid approach blocked (no route)");
            return;
        }

        // Spec 26.3 r2: arrival is literal — standing ON the reserved approach
        // junction. PathfindingSystem re-routes next tick (or goes Blocked and
        // the gate above aborts).
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } wantJunction &&
            (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(wantJunction)))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            // Arm's length: the plan walks to a spot 0.9*R beside her and the
            // planner caps the reserved junction at the same reach — this is
            // the belt-and-braces re-check at start (was 2*R, a full hex:
            // visibly kneeling and feeding from across the clearing).
            if (!InteractionReach.CheckStart(world, npc, target.Position,
                    InteractionReach.Aid, $"Aid NPC{targetId.Value}"))
            {
                AbortAid(world, npc, $"Target NPC{targetId.Value} out of aid range");
                return;
            }

            // Re-check on arrival: she may have recovered / died on the way.
            var kindNow = AssessAidKind(target, world.Tick, out var severity);
            if (kindNow == AidKind.None || severity < Spec53.SufferingThreshold)
            {
                AbortAid(world, npc, $"NPC{targetId.Value} no longer needs aid");
                return;
            }

            // Turn to face her — a caring stance. The HELPER kneels toward the
            // patient; the patient, if she's lying (coma/asleep/prone), keeps
            // her authored pose and is NOT rotated to face back (§60: a flat
            // body pivoting to look at you reads as creepy).
            var faceDelta = new Float2(
                target.Position.X - npc.Position.X, target.Position.Y - npc.Position.Y);
            var faceDirection = HexSpatialMath.Normalize(faceDelta);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
            if (!target.IsLyingDown(world.Tick))
            {
                target.RotationDegrees = HexSpatialMath.AngleDegrees(
                    new Float2(-faceDirection.X, -faceDirection.Y));
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kindNow switch
            {
                AidKind.Feed => InteractionType.FeedOther,
                AidKind.Hydrate => InteractionType.HydrateOther,
                AidKind.Treat => InteractionType.TreatOther,
                AidKind.Medicate => InteractionType.MedicateOther,
                _ => InteractionType.ConsoleOther
            };
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec53.AidDuration;
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            SocialCueSignals.Stamp(world, npc, "AidStarted", target.Id);
            SocialCueSignals.Stamp(world, target, "AidStarted", npc.Id);
            Trace.Emit(world, npc.Id, "AidStarted",
                $"Kind={kindNow} With NPC{targetId.Value} Severity={severity:F2} " +
                $"Duration={Spec53.AidDuration}ticks " +
                $"Dist={HexSpatialMath.Distance(npc.Position, target.Position):F2}");
            if (kindNow == AidKind.Treat)
            {
                StabilizeBleedingOnAidStart(world, npc, target);
            }
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            if (remaining > 0)
            {
                return;
            }

            // The patient may have got up and fled mid-care (a dog scare, a
            // fight): relief landing across the clearing reads as telekinesis.
            if (!InteractionReach.CheckStart(world, npc, target.Position,
                    InteractionReach.Aid, $"AidComplete NPC{targetId.Value}"))
            {
                AbortAid(world, npc, $"Target NPC{targetId.Value} moved away mid-aid");
                return;
            }

            var kind = npc.Execution.CurrentInteraction switch
            {
                InteractionType.FeedOther => AidKind.Feed,
                InteractionType.HydrateOther => AidKind.Hydrate,
                InteractionType.TreatOther => AidKind.Treat,
                InteractionType.MedicateOther => AidKind.Medicate,
                _ => AidKind.Console
            };

            ApplyAidRelief(world, npc, target, kind);

            // Both relationships rise — kindness under hardship bonds hard.
            var helperRel = npc.Social.GetOrCreate(target.Id);
            var wardRel = target.Social.GetOrCreate(npc.Id);
            var gain = Spec53.AidRelationshipGain;
            helperRel.Affinity = MathUtil.Clamp(helperRel.Affinity + gain, -1f, 1f);
            wardRel.Affinity = MathUtil.Clamp(wardRel.Affinity + gain, -1f, 1f);
            helperRel.Familiarity = MathUtil.Clamp01(helperRel.Familiarity + gain);
            wardRel.Familiarity = MathUtil.Clamp01(wardRel.Familiarity + gain);
            helperRel.Trust = MathUtil.Clamp01(helperRel.Trust + gain);
            wardRel.Trust = MathUtil.Clamp01(wardRel.Trust + gain);

            // Reuse the Sims-style "+/-" pop over both heads (spec 28.15E).
            npc.Execution.LastTalkResultTick = world.Tick;
            npc.Execution.LastTalkAffinityDelta = gain;
            target.Execution.LastTalkResultTick = world.Tick;
            target.Execution.LastTalkAffinityDelta = gain;
            SocialCueSignals.Stamp(world, npc, "AidCompleted", target.Id);
            SocialCueSignals.Stamp(world, target, "AidCompleted", npc.Id);

            // Helping settles the helper's own compassion.
            npc.Needs.Compassion = MathUtil.Clamp01(npc.Needs.Compassion + Spec53.AidSelfRestore);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;
            npc.Execution.CurrentInteraction = null;

            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.FreeJunction(world, jId, npc.Id);
                SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
            }

            Trace.Emit(world, npc.Id, "Aided",
                $"Kind={kind} NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                $"Trust={helperRel.Trust:F2} (+{gain:F2}) Fam={helperRel.Familiarity:F2} (+{gain:F2}) " +
                $"Aff={helperRel.Affinity:F2} (+{gain:F2}) MyCompassion={npc.Needs.Compassion:F2}");
            Trace.Emit(world, target.Id, "RelationshipChanged",
                $"NPC{target.Id.Value}->NPC{npc.Id.Value} " +
                $"Trust={wardRel.Trust:F2} (+{gain:F2}) Fam={wardRel.Familiarity:F2} (+{gain:F2}) " +
                $"Aff={wardRel.Affinity:F2} (+{gain:F2}) aided");

            if (target.Mind.PendingAidFrom is { } aiderId && aiderId.Equals(npc.Id))
            {
                target.Mind.PendingAidFrom = null;
            }

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetAgentId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;

            Trace.Emit(world, npc.Id, "CycleReset", "Goal->None (aided)");
        }
    }

    private static void StabilizeBleedingOnAidStart(WorldState world, NPCState helper, NPCState target)
    {
        var stabilized = false;
        foreach (var wound in target.Wounds)
        {
            if (wound.Heal01 < 0.3f && wound.Severity >= 0.05f)
            {
                wound.Heal01 = 0.3f;
                stabilized = true;
            }
        }

        if (!stabilized)
        {
            return;
        }

        target.Needs.Blood = MathUtil.Clamp01(target.Needs.Blood + Spec53.TreatBlood * 0.5f);
        Trace.Emit(world, helper.Id, "AidStabilized",
            $"NPC{target.Id.Value} bleeding stemmed (Blood={target.Needs.Blood:F2})");
    }

    private static void AbortAid(WorldState world, NPCState npc, string reason)
    {
        // Release the claim so the sufferer is free for another helper.
        if (npc.Plan.TargetAgentId is { } tid &&
            world.Entities.Npcs.TryGetValue(tid, out var t) &&
            t.Mind.PendingAidFrom is { } aider && aider.Equals(npc.Id))
        {
            t.Mind.PendingAidFrom = null;
        }
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Aid);
        PlanInterruption.Abort(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }

    // Spec 28.15E: choose the subject of a talk. A weighted, deterministic draw
    // biased by the pair's situation — hungry housemates talk food, cold ones
    // talk weather/fire, scared ones talk dogs, friends flirt and joke, rivals
    // grumble — with the island's escape/shark themes always simmering. The
    // roll is a stateless hash of (tick, pair) so it's resume-safe and both the
    // sim and any replay agree. Presentation turns the value into an emoji.
    private static readonly TalkTopic[] TalkTopicOrder =
    {
        TalkTopic.SmallTalk, TalkTopic.Escape, TalkTopic.Sharks, TalkTopic.Dogs,
        TalkTopic.Weather, TalkTopic.Food, TalkTopic.Fire, TalkTopic.Home,
        TalkTopic.Gossip, TalkTopic.Flirt, TalkTopic.Joke, TalkTopic.Grumble
    };

    private static readonly float[] TalkTopicWeights = new float[12];

    private static TalkTopic PickTalkTopic(WorldState world, NPCState npc, NPCState target)
    {
        // Shared situation of the two participants (means, so one cranky/hungry
        // party still tilts the subject without dominating).
        var hunger = 0.5f * (npc.Needs.Hunger + target.Needs.Hunger);
        var social = 0.5f * (npc.Needs.Social + target.Needs.Social);
        var stress = 0.5f * (npc.Needs.Stress + target.Needs.Stress);
        var thermal = 0.5f * (npc.Needs.ThermalComfort + target.Needs.ThermalComfort);
        var cold = System.Math.Max(0f, -thermal);
        var hot = System.Math.Max(0f, thermal);
        var rain = world.Environment.IsRaining ? 1f : 0f;
        var mutualAff = 0.5f * (
            npc.Social.GetOrCreate(target.Id).Affinity +
            target.Social.GetOrCreate(npc.Id).Affinity);
        var like = System.Math.Max(0f, mutualAff);
        var dislike = System.Math.Max(0f, -mutualAff);

        // Weights are parallel to TalkTopicOrder. Base keeps every subject
        // possible; context terms make the fitting ones far likelier.
        var w = TalkTopicWeights;
        w[0] = 1.00f;                                   // SmallTalk
        w[1] = 0.60f + 0.60f * System.Math.Max(cold, rain); // Escape (misery → wanting off the rock)
        w[2] = 0.40f;                                   // Sharks (the island's ever-present menace)
        w[3] = 0.40f + 1.00f * stress;                  // Dogs (fear talk)
        w[4] = 0.30f + 1.20f * rain + 1.00f * cold + 0.80f * hot; // Weather
        w[5] = 0.30f + 1.50f * hunger;                  // Food
        w[6] = 0.30f + 1.00f * cold;                    // Fire (warmth)
        w[7] = 0.50f + 0.80f * (1f - social);           // Home (homesick when starved of company)
        w[8] = 0.50f;                                   // Gossip
        w[9] = 0.20f + 1.20f * like;                    // Flirt (they warm to each other)
        w[10] = 0.40f + 0.80f * like;                   // Joke
        w[11] = 0.30f + 1.00f * dislike + 0.60f * hunger; // Grumble (dislike / crankiness)

        var total = 0f;
        for (var i = 0; i < w.Length; i++)
        {
            total += w[i];
        }

        // Independent salt (5501) so the topic roll doesn't correlate with the
        // quarrel roll on the same (tick, pair).
        var roll = MathUtil.Hash01(world.Seed, world.Tick,
            npc.Id.Value * 31 + target.Id.Value, 5501) * total;
        for (var i = 0; i < w.Length; i++)
        {
            roll -= w[i];
            if (roll <= 0f)
            {
                return TalkTopicOrder[i];
            }
        }

        return TalkTopic.SmallTalk;
    }

    // §67.10: how often each speaker's subject is re-drawn inside one talk.
    // 30 ticks ≈ 3 s at the default tick rate — about one turn of the view-side
    // turn-taking, so a bubble change lands between utterances, not mid-word.
    private const int TalkTopicRefreshTicks = 30;

    // §67.10: what THIS speaker is on about, as opposed to the pair's shared
    // subject. A pressing personal state (starving, parched, wounded, dead
    // tired, freezing) wins over small talk about coconuts — that is how "she
    // tells her housemate she's hungry" happens with no extra sim machinery:
    // presentation reads the same CurrentTalkTopic field it always did.
    //
    // Deterministic: the only randomness is a stateless hash of (tick-bucket,
    // speaker), so a resume/replay picks the same subject.
    private static TalkTopic PickSpeakerTopic(
        WorldState world, NPCState speaker, NPCState listener, TalkTopic shared)
    {
        // Strongest complaint first — one clear voice per turn, not a mixture.
        // Thresholds are deliberately high: chatter stays about the island and
        // each other until something really is wrong.
        var complaint = (TalkTopic?)null;
        var severity = 0f;

        void Consider(TalkTopic topic, float value, float threshold)
        {
            if (value <= threshold)
            {
                return;
            }

            // Normalise "how far past the threshold" so different needs compare.
            var s = (value - threshold) / System.Math.Max(0.001f, 1f - threshold);
            if (s > severity)
            {
                severity = s;
                complaint = topic;
            }
        }

        Consider(TalkTopic.Hunger, speaker.Needs.Hunger, 0.55f);
        Consider(TalkTopic.Thirst, speaker.Needs.Thirst, 0.55f);
        // Energy sits low for long stretches of a working day, so this bar is
        // the highest of the five: at 0.70 "I'm tired" drowned out every other
        // complaint in the probe (388 of 478 talk-ticks).
        Consider(TalkTopic.Tired, 1f - speaker.Needs.Energy, 0.82f);
        Consider(TalkTopic.Cold, -speaker.Needs.ThermalComfort, 0.35f);
        // An open wound speaks for itself — count it as a hard complaint.
        Consider(TalkTopic.Pain, speaker.Wounds.Count > 0 ? 1f : 0f, 0.5f);

        if (complaint is not { } personal)
        {
            return shared;
        }

        // Even a real complaint doesn't monopolise every turn: the worse it is,
        // the likelier she brings it up (0.45 at the threshold → 0.95 at the
        // extreme). Otherwise she keeps to the shared subject.
        var chance = 0.45f + 0.50f * MathUtil.Clamp01(severity);
        var bucket = world.Tick / TalkTopicRefreshTicks;
        // Salt 5507: independent of the shared-subject and quarrel rolls.
        var roll = MathUtil.Hash01(world.Seed, bucket,
            speaker.Id.Value * 131 + listener.Id.Value, 5507);
        return roll < chance ? personal : shared;
    }

    // §67.10: re-draw both participants' subjects at the refresh cadence while
    // a talk is running. Only touches the two topic fields — no needs, no
    // relationships, no plan state — so it cannot alter simulation outcomes.
    private static void RefreshTalkTopics(WorldState world, NPCState npc, NPCState target)
    {
        var elapsed = world.Tick - npc.Execution.StartTick;
        if (elapsed <= 0 || elapsed % TalkTopicRefreshTicks != 0)
        {
            return;
        }

        var shared = PickTalkTopic(world, npc, target);
        npc.Execution.CurrentTalkTopic = PickSpeakerTopic(world, npc, target, shared);
        target.Execution.CurrentTalkTopic = PickSpeakerTopic(world, target, npc, shared);
    }
}

}
