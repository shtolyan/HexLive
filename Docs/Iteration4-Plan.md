# Iteration 4 Plan — Social System v1 (Talk)

## Iteration Target

The two NPCs from iteration 3 coexist but ignore each other. This iteration
makes the Social need real and gives NPCs their first agent-to-agent
interaction, per spec 28.15 minimal scope (details in the new spec 28.15A):

1. **Social need decays** (-0.008/slow tick) and is satisfied by talking.
2. **Agent perception**: PerceptionSystem fills `Perception.Agents` with
   distance, junction, reachability, busy flag, and a relationship summary.
3. **Relationships**: `SocialState` becomes a per-pair dictionary
   (Trust / Familiarity / Affinity; Authority deferred). Talks raise
   Familiarity and Affinity +0.05 both directions; Affinity feeds back into
   the Socialize score (+0.1 × affinity), so friendships self-reinforce.
4. **Socialize goal → Talk**: plan targets an *agent* (new
   `NPCPlanState.TargetAgentId`), walks to a free neighbor junction of the
   target, talks 16 ticks; initiator +0.40 Social, listener +0.25.
   Rejection (busy/starving/moving/out-of-range) → goal cooldown.
5. **Invitation handshake** (spec 28.8/28.15A): the initiator claims the
   target (`PendingTalkFrom`); the claimed NPC waits in place (politely
   aborting its own plan) until arrival, a 120-tick timeout, or a Starving
   emergency. This was the load-bearing piece — see "Lessons" below.
6. **Deferred**: embarrassment (no privacy-sensitive action exists to trigger
   it), joint reserved actions (SitTogether), Talk as authored content.

## Lessons From the Soak Runs (kept for future agent-interaction work)

Three failure modes were found and fixed on the way to a stable talk loop;
all three generalize to any future agent-targeted interaction:

1. **Moving-target chase**: planning against a walking agent's position fails
   at arrival. Fix: only stationary agents are valid targets + wide range.
2. **Mid-execution availability collapse**: gating a goal on a live target
   scan interrupts the goal's own in-flight plan whenever the target twitches
   (390→1106 interrupt storms). Fix: an active plan shields its goal's
   availability; validation happens at arrival, not per tick.
3. **Mutual-approach dance**: two NPCs initiating at each other
   simultaneously never meet. Fix: the invitation handshake — claimed NPCs
   wait instead of initiating (talks went 9 → 59 per soak, 100% completion).

## Code Touch Points

- `Simulation/Social/SocialState.cs`: relationship dictionary + summary shape.
- `Simulation/AI/PerceptionSnapshot.cs`: PerceivedAgent gains Junction,
  IsReachable, IsBusy.
- `Simulation/AI/NpcMind.cs` / `NpcPlanState.cs`: GoalType.Socialize,
  TargetAgentId.
- `Simulation/Content/Definitions.cs`: InteractionType.Talk.
- `Simulation/Runtime/SimulationSystems.cs`: perception agents fill, Social
  decay, Socialize scoring with SocialModifier, Socialize planning branch,
  RunTalk execution branch, TargetAgentId cleanup in all plan resets.
- `Simulation/Bootstrap/PrototypeWorldDefinitionFactory.cs`: NPC Social
  starts 0.5 / 0.6 (so early game isn't dominated by chatting).
- Snapshot/exporter/panel: Social need bar, Relations row, Socialize color.

## Verification (headless harness)

1. TalkCompleted events occur throughout a 24000-tick soak; both NPCs both
   initiate and listen.
2. Social need cycles for both NPCs (min < start, max > start after talks).
3. Familiarity/Affinity grow monotonically and clamp at 1.
4. Survival loop unaffected: both still eat and sleep; no leaks
   (reservations/occupancy/caches clean at cutoff).
5. Rejections observable (InteractionRejected / out-of-range traces) without
   deadlock — cooldown moves the NPC to another activity.
