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

public sealed partial class DecisionSystem : ISimulationSystem
{
    public string Name => nameof(DecisionSystem);

    public TickLayer Layer => TickLayer.Medium;

    // Spec 23.17 / 23.8: Starving hysteresis and emergency boost.
    private static float StarvingEnterThreshold => SimBalance.StarvingEnterThreshold;

    private static float StarvingClearThreshold => SimBalance.StarvingClearThreshold;

    private static float StarvingBoost => SimBalance.StarvingBoost;

    // Spec 23.8–23.10 (iteration 3): goal stability.
    private const int GoalLockTicks = 24;

    private const float LockOverrideDelta = 0.5f;

    private const float SwitchDelta = 0.15f;

    // Spec 28.8/28.15A: how long an invited NPC waits for the initiator.
    private const int TalkWaitTimeoutTicks = 120;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // Spec 29C.3: combat is reactive and consumes the NPC entirely.
            if (npc.IsFighting)
            {
                continue;
            }

            // Spec §60: comatose — the body lies as if dead; recovery runs in
            // NeedsDecaySystem (sleep rules) and the wake check lives there
            // too. No decisions of any kind while out.
            if (npc.Mind.ComaCause != ComaCause.None)
            {
                continue;
            }

            // Spec 40.13: unconscious — lie helpless, recovering a little
            // stamina, until the body comes to. No decisions while out.
            if (world.Tick < npc.Mind.FaintedUntilTick)
            {
                npc.Needs.Stamina = MathUtil.Clamp01(npc.Needs.Stamina + 0.02f);
                continue;
            }

            // Spec 41.5: just woke up — stand where you slept and come to
            // your senses; goals wait out the grace.
            if (world.Tick < npc.Mind.WakeGraceUntilTick)
            {
                continue;
            }

            // Spec 29C.4A: nothing outbids running for your life.
            if (npc.Mind.CurrentGoal == GoalType.Flee && npc.Plan.Status == PlanStatus.Active)
            {
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Defend &&
                (npc.Plan.Status == PlanStatus.Active ||
                 npc.Mind.CombatAssistDogId.HasValue ||
                 npc.Mind.CombatAssistAttackerNpcId.HasValue))
            {
                continue;
            }

            // Spec 29C.9: while an action is genuinely underway, don't
            // re-decide. Gradual needs (iter 30) drain the executing goal's
            // OWN score every tick (eating lowers Hunger -> Eat's score
            // falls), which otherwise flips the goal mid-action and explodes
            // the interrupt count (seed 31337: 782). Real emergencies still
            // break in: a threatening dog sets Flee reactively via the fear
            // path (handled above), and starvation/dehydration interrupt on
            // the very next decision once the short action completes. Actions
            // are <= 100 ticks, so deferring is imperceptible.
            if (npc.Execution.Status == ExecutionStatus.InProgress)
            {
                continue;
            }

            var previousGoal = npc.Mind.CurrentGoal;
            npc.Mind.LastScores.Clear();
            npc.Mind.Cooldowns.RemoveAll(c => c.EndTick <= world.Tick);
            npc.Memory.Dangers.RemoveAll(d => world.Tick - d.Tick > 2400);

            UpdateStarvingStatus(world, npc);
            UpdateDehydratedStatus(world, npc);
            UpdateOverheatedStatus(world, npc);
            var bleedingCrisis = IsBleedingCrisis(npc);
            var emergencyBoost = npc.Mind.IsStarving ? StarvingBoost : 0f;
            var drinkBoost = npc.Mind.IsDehydrated ? StarvingBoost : 0f;

            // Spec 28.15C: discovering a body triggers grief on sight.
            foreach (var perceived in npc.Perception.Objects)
            {
                if (!perceived.FromMemory &&
                    world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var perceivedDef) &&
                    perceivedDef.Tags.Contains("Corpse") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var corpseObject))
                {
                    GriefSystemHelpers.TriggerGrief(world, npc, corpseObject);
                }
            }
            var isGrieving = world.Tick < npc.Mind.GrievingUntilTick;

            // Self-heal a stale talk invitation: valid only while the initiator
            // still exists and still targets this NPC.
            if (npc.Mind.PendingTalkFrom is { } fromId &&
                (!world.Entities.Npcs.TryGetValue(fromId, out var initiator) ||
                 initiator.Plan.TargetAgentId is not { } initiatorTarget ||
                 !initiatorTarget.Equals(npc.Id)))
            {
                npc.Mind.PendingTalkFrom = null;
            }

            // Spec 28.8: an accepted invitation means waiting in place until
            // the initiator arrives. Emergencies and timeouts break the wait.
            if (npc.Mind.PendingTalkFrom is { } waitingFor)
            {
                if (npc.Mind.IsStarving)
                {
                    npc.Mind.PendingTalkFrom = null;
                }
                else if (world.Tick - npc.Mind.PendingTalkSinceTick > TalkWaitTimeoutTicks)
                {
                    npc.Mind.PendingTalkFrom = null;
                    Trace.Emit(world, npc.Id, "TalkWaitTimeout",
                        $"Gave up waiting for NPC{waitingFor.Value} after {TalkWaitTimeoutTicks} ticks");
                }
                else if (npc.Execution.Status != ExecutionStatus.InProgress ||
                         npc.Execution.CurrentInteraction != InteractionType.Talk)
                {
                    if (npc.Plan.Status == PlanStatus.Active ||
                        npc.Execution.Status == ExecutionStatus.InProgress)
                    {
                        PlanInterruption.Abort(world, npc,
                            $"Accepting talk from NPC{waitingFor.Value}, waiting in place");
                    }

                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }
            }

            // Spec §53: self-heal a stale aid claim — valid only while the helper
            // still exists, still targets this NPC, and is still on an Aid goal.
            if (npc.Mind.PendingAidFrom is { } aidFromId &&
                (!world.Entities.Npcs.TryGetValue(aidFromId, out var aider) ||
                 aider.Plan.TargetAgentId is not { } aiderTarget ||
                 !aiderTarget.Equals(npc.Id) ||
                 aider.Mind.CurrentGoal != GoalType.Aid))
            {
                npc.Mind.PendingAidFrom = null;
            }

            // Spec §53: a sufferer being helped holds still until the helper
            // arrives — but danger (a fight, a flee) or a timeout breaks the wait.
            // Unlike a talk invite, hunger does NOT break it: she needs the help.
            if (npc.Mind.PendingAidFrom is { } aidWaitingFor)
            {
                if (npc.IsFighting || npc.Mind.CurrentGoal == GoalType.Flee)
                {
                    npc.Mind.PendingAidFrom = null;
                }
                else if (world.Tick - npc.Mind.PendingAidSinceTick > TalkWaitTimeoutTicks)
                {
                    npc.Mind.PendingAidFrom = null;
                    Trace.Emit(world, npc.Id, "AidWaitTimeout",
                        $"Gave up waiting for helper NPC{aidWaitingFor.Value}");
                }
                else if (npc.Execution.Status != ExecutionStatus.InProgress)
                {
                    if (npc.Plan.Status == PlanStatus.Active)
                    {
                        PlanInterruption.Abort(world, npc,
                            $"Awaiting help from NPC{aidWaitingFor.Value}, waiting in place");
                    }

                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }
            }

            // Eat normally consumes from inventory. Coconuts are the exception:
            // whole/pierced/split states are processed and consumed on the ground.
            var hasFoodInInventory = npc.Inventory.FindFirstFood(world.Content) != null;
            var hasCoconutMeal = HasCoconutMeal(npc, world);
            var hasCoconutWater = HasCoconutWater(npc, world);
            var hasCoconutBlade = HasCoconutBlade(npc);
            var canUseToolsOrWeapons = npc.Body.CanUseToolsOrWeapons;
            // Restraint gate (spec 29B.2): don't harvest food you don't need,
            // or the ground stock never survives until the productionless night.
            var getFoodHungerThreshold = SimBalance.GetFoodHungerThreshold;

            var eatAvail = hasFoodInInventory || hasCoconutMeal;
            var getFoodAvail = !hasFoodInInventory && !hasCoconutMeal &&
                npc.Needs.Hunger >= getFoodHungerThreshold &&
                (HasReachableFoodForCurrentTools(npc, world) || KnowsReachableProducer(npc, world));
            // Spec 29G: the land itself is furniture — a bed is better, but
            // sleep never blocks on owning one. Still, nobody naps at noon
            // out of boredom: sleep is for the tired or for the dark hours
            // (without this gate the first soak showed 73 ground naps eating
            // every idle minute — no explores, the fire never lit).
            // §49-parity: never LIE DOWN when the sleep interrupt would fire on
            // the first tick (hunger/thirst over the wake threshold, danger
            // remembered) — the same condition execution wakes on. Without
            // this the thirsty-and-tired girl loops lie-down→wake forever.
            var sleepAvail = (npc.Needs.Energy < SimBalance.SleepEnergyThreshold ||
                    world.Environment.Phase is DayPhase.Night or DayPhase.Evening) &&
                !ExecutionSystem.HasSleepInterrupt(world, npc);
            // Spec 31C.7A: sit because you need it — and never settle into a
            // chair on an empty stomach. Sitting yields to sleep hours (the
            // Sit->Sleep churn was 47 interrupts/soak before this gate).
            // §54.12: sitting needs a REAL seat — Sit-furniture in view (stump/
            // chair/bed) or a ledge step within plan range. Flat ground is not
            // a seat (the old ground-sit fallback is gone), so without one Sit
            // doesn't even bid — else she'd churn Sit→NoLedge→cooldown forever
            // on a flat island.
            var sitAvail = npc.Needs.Comfort < SimBalance.SitComfortThreshold &&
                npc.Needs.Hunger < SimBalance.SitNeedGate && npc.Needs.Thirst < SimBalance.SitNeedGate &&
                !sleepAvail &&
                (HasPerceivedSeat(npc) || AnyLedgeNear(world, npc, HexSpatialMath.HexRadius * 8f));
            // Spec 29C.4 restraint: dress only against cold — an overheated
            // NPC reaching for more clothes is a doom loop.
            // Spec 29C.4A: fresh danger overrides the weather — arm up.
            var effectiveTemp = world.Environment.GlobalTemperature + npc.EquippedWarmth * 10f;
            var wantsArmor = npc.Memory.Dangers.Count > 0 && npc.EquippedArmor < 0.3f &&
                KnowsReachableArmor(npc, world);
            var dressAvail = wantsArmor ||
                (npc.Needs.ThermalDiscomfort >= SimBalance.DressThermalThreshold &&
                 effectiveTemp < SimBalance.DressColdTemp && // spec 42: dress against REAL cold only —
                 // a merely-cool girl (14..16) must not circle the wardrobe all
                 // day while the fire/water chain starves (worn=183/soak once)
                 npc.EquippedWarmth < SimBalance.DressWarmthCeiling && // already bundled up: more cloth
                 // won't fix 10°C — the campfire will (stops armor-swap churn)
                 HasInteraction(npc, InteractionType.Dress));
            var dressNeed = wantsArmor
                ? System.Math.Max(npc.Needs.ThermalDiscomfort, 0.6f)
                : npc.Needs.ThermalDiscomfort;

            // Spec 28.6 / 28.15A: Socialize needs a reachable non-busy agent;
            // affinity toward the best target feeds the score back positively.
            var socializeAvail = false;
            float? bestAffinity = null;
            foreach (var agent in npc.Perception.Agents)
            {
                if (!agent.IsReachable || agent.IsBusy || agent.IsMoving ||
                    agent.IsUnconscious) // §60: a comatose girl is no company
                {
                    continue;
                }

                socializeAvail = true;
                if (bestAffinity is null || agent.Relationship.Affinity > bestAffinity)
                {
                    bestAffinity = agent.Relationship.Affinity;
                }
            }

            // Spec §49: don't START a chat on an empty stomach or a dry throat.
            // With talks now longer (up to 90 ticks), a moderately thirsty/hungry
            // girl who talks instead of drinking climbs toward dehydration during
            // the conversation — the soak's dominant new death. Gating INITIATION
            // (an in-flight talk below still finishes) decouples talk length from
            // the water economy, so the "linger and chat" feel is safe. Mirrors
            // the Sit gate (comfort leisure yields to real needs).
            if (npc.Needs.Hunger >= Spec49.SocializeNeedGate ||
                npc.Needs.Thirst >= Spec49.SocializeNeedGate)
            {
                socializeAvail = false;
            }

            // Spec 28.8 handshake: while someone is coming over to talk,
            // don't initiate a talk yourself.
            if (npc.Mind.PendingTalkFrom is not null)
            {
                socializeAvail = false;
            }

            // An in-flight talk (walking to the target or already talking) keeps
            // its own goal available: the per-tick availability scan must not
            // zero out a plan that validates its target at arrival anyway.
            // Transient target movement is not "target gone" (spec 28.15A).
            if ((npc.Plan.Status == PlanStatus.Active && npc.Plan.TargetAgentId is not null) ||
                (npc.Execution.Status == ExecutionStatus.InProgress &&
                 npc.Execution.CurrentInteraction == InteractionType.Talk))
            {
                socializeAvail = true;
            }

            // Spec 19.7A: sleeping is more attractive after dark.
            var sleepEnvironmentBonus = world.Environment.Phase switch
            {
                DayPhase.Night => 0.25f,
                DayPhase.Evening => 0.10f,
                _ => 0f
            };

            AddGoalScore(npc, world.Tick, GoalType.Eat, npc.Needs.Hunger, eatAvail, emergencyBoost);
            AddGoalScore(npc, world.Tick, GoalType.GetFood, npc.Needs.Hunger, getFoodAvail, emergencyBoost);
            AddGoalScore(npc, world.Tick, GoalType.Sleep, 1f - npc.Needs.Energy, sleepAvail,
                environment: sleepEnvironmentBonus);
            // Sitting anywhere is leisure, not survival: half-weight keeps it
            // an idle-time filler instead of outbidding fire and food chores
            // (full 1-Comfort made Sit >= 0.4 by construction of its gate).
            // Spec 40.1: low stamina adds a gentle pull toward sitting to
            // recover (score only — availability unchanged, so the economy
            // isn't reshaped, just the timing of an already-available rest).
            AddGoalScore(npc, world.Tick, GoalType.Sit,
                (1f - npc.Needs.Comfort) * 0.5f + (1f - npc.Needs.Stamina) * 0.25f, sitAvail);
            AddGoalScore(npc, world.Tick, GoalType.Dress, dressNeed, dressAvail);
            // Spec 28.15B: dislike lowers the urge, embarrassment causes
            // post-quarrel withdrawal.
            AddGoalScore(npc, world.Tick, GoalType.Socialize, 1f - npc.Needs.Social, socializeAvail,
                social: (bestAffinity ?? 0f) * 0.1f - npc.Social.Embarrassment * 0.3f -
                    (isGrieving ? 0.2f : 0f));

            // Spec §53: compassion — tend the worst-off reachable housemate
            // (feed/treat/medicate/console). Gated HARD behind the helper's own
            // survival: a girl in her own crisis (starving, dehydrated, badly
            // hurt or bleeding, fighting, fleeing) looks after herself first.
            // The bid scales with the sufferer's plight × this girl's personality
            // CompassionTrait, plus the accumulated pressure of a spent
            // Compassion need — so a caring girl (high trait) will outbid a bed
            // build for a dying housemate, a reserved one only helps when idle.
            var aidAvail = false;
            var bestSuffering = 0f;
            var bestSuffererAffinity = 0f;
            var bestAidKind = AidKind.None;
            if (Spec53.Enabled)
            {
                var selfOk = !npc.Mind.IsStarving && !npc.Mind.IsDehydrated &&
                    !npc.IsFighting && npc.Mind.CurrentGoal != GoalType.Flee &&
                    npc.Needs.Hunger < Spec53.SelfHungerGate &&
                    npc.Health >= Spec53.SelfHealthGate &&
                    npc.Needs.Blood >= Spec53.SelfHealthGate;
                if (selfOk)
                {
                    foreach (var agent in npc.Perception.Agents)
                    {
                        if (agent.AidKind == AidKind.None ||
                            agent.Suffering < Spec53.SufferingThreshold ||
                            !agent.IsReachable || agent.IsBusy || agent.IsMoving)
                        {
                            continue;
                        }
                        if (agent.Suffering > bestSuffering)
                        {
                            bestSuffering = agent.Suffering;
                            bestSuffererAffinity = agent.Relationship.Affinity;
                            bestAidKind = agent.AidKind;
                            aidAvail = true;
                        }
                    }
                }
                // An in-flight aid (walking to the sufferer or mid-care) keeps its
                // goal available so the availability scan can't zero a live plan.
                var curIt = npc.Execution.CurrentInteraction;
                var inAidExec = curIt == InteractionType.FeedOther ||
                    curIt == InteractionType.HydrateOther ||
                    curIt == InteractionType.TreatOther ||
                    curIt == InteractionType.MedicateOther ||
                    curIt == InteractionType.ConsoleOther;
                if ((npc.Plan.Status == PlanStatus.Active &&
                     npc.Mind.CurrentGoal == GoalType.Aid && npc.Plan.TargetAgentId is not null) ||
                    (npc.Execution.Status == ExecutionStatus.InProgress && inAidExec))
                {
                    aidAvail = true;
                }
            }
            var aidEmergency = bestAidKind == AidKind.Treat && bestSuffering >= 0.65f
                ? StarvingBoost * 0.75f
                : 0f;
            AddGoalScore(npc, world.Tick, GoalType.Aid,
                bestSuffering * npc.CompassionTrait * Spec53.AidWeight +
                    (1f - npc.Needs.Compassion) * Spec53.PressureWeight,
                aidAvail, aidEmergency, social: System.Math.Max(0f, bestSuffererAffinity) * 0.05f);

            // Spec 35.3: what the communal hut needs next (null = done/absent).
            // Construction is peacetime work: material hauling pauses while
            // hungry, thirsty, or while the fire is starving (soak lesson —
            // logs feed walls AND the hearth, and the hearth comes first).
            var piece = NextBuildPiece(world);
            if (piece is not null &&
                (npc.Needs.Hunger >= 0.5f || npc.Needs.Thirst >= 0.5f))
            {
                piece = null;
            }
            // Spec §54: logs frame the builds/raft/premium bed; sticks are the
            // fuel + hand-tool currency (split from logs, or picked from deadfall).
            var carriedLogs = CountInventory(npc, "resource.log");
            var carriedSticks = CountInventory(npc, "resource.stick");
            var carriedLeaves = CountInventory(npc, "resource.palm_leaf");
            // Spec §54: cordage chain + knife.
            var carriedFiber = CountInventory(npc, "resource.fiber");
            var carriedRope = CountInventory(npc, "resource.rope");
            var carriedCloth = CountInventory(npc, "resource.cloth");
            var hasKnife = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Cut);
            // The knife is today's only Butcher tool, but the CAPABILITY is the
            // truth: an axe cuts yucca (Cut) yet can't dress a carcass, so the
            // knife stays craftable until Butcher is covered too.
            var hasButcherTool = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Butcher);

            // Spec §52: the furniture build-site chain. A site is an intent point
            // every NPC knows; materials are hauled in (deposited into it) over
            // many trips, then a builder with a hammer raises the piece. Build
            // work is peacetime (like the hut): it pauses when hungry/thirsty.
            var buildSite = FindBuildSite(npc, world);
            var hasHammer = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Hammer);
            // §54.13: the old 0.55/any-danger gate held the window open only
            // ~1-36% of npc-ticks (10-day soak) — thirst equilibrates at
            // 0.5-0.6 and a day-old wolf memory froze building colony-wide.
            // Pause for real pressure (0.65, the lifeThreatened bar) and for
            // FRESH danger only.
            var freshDanger = false;
            foreach (var danger in npc.Memory.Dangers)
            {
                if (world.Tick - danger.Tick <= SimBalance.BuildDangerFreshTicks)
                {
                    freshDanger = true;
                    break;
                }
            }

            var buildPeacetime = npc.Needs.Hunger < SimBalance.BuildNeedGate &&
                npc.Needs.Thirst < SimBalance.BuildNeedGate && !freshDanger;
            // Spec §54 cold start: raising the FIRST hearth is survival-critical
            // (no fire ⇒ no warmth, no cooking, no crafting), so building the
            // campfire-site bypasses the peacetime gate and outranks everything —
            // the colony must pile the stones and light up before it can thrive.
            var noCampfireYet = !HasReachableWithTag(npc, world, "Campfire");
            var siteIsHearth = buildSite != null && buildSite.BuildProduct == "campfire.spot";
            // §54.14 (r2): the bypass stops at the LIFE-critical band — the
            // bare-site cold start made the first hearth a longer project, and
            // a girl must never starve to death with the pile sticks in her
            // pack (probe: Marta dead at hunger 1.0 carrying 8/9 sticks).
            var hearthUrgent = siteIsHearth && noCampfireYet &&
                npc.Needs.Hunger < 0.8f && npc.Needs.Thirst < 0.8f;
            var buildWindow = buildPeacetime || hearthUrgent;
            var siteNeedsLogs = buildSite != null && buildWindow &&
                BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialLogs) && carriedLogs < 1;
            var siteNeedsStones = buildSite != null && buildWindow &&
                BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialStones);
            // Spec §54.2: a bed build-site also pulls leaves + sticks — the gather
            // feeders (ChopCrown/GatherLeaves, SplitLog) fetch them so BuildFurniture
            // can haul each piece over to the growing mat.
            var siteNeedsLeaves = buildSite != null && buildWindow &&
                BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialLeaves);
            var siteNeedsSticks = buildSite != null && buildWindow &&
                BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialSticks);
            var siteNeedsRope = buildSite != null && buildWindow &&
                BuildSiteMath.Needs(buildSite, BuildSiteMath.MaterialRope);
            // §54.10: when a staked bed site is waiting on materials, its gather +
            // deliver chain outranks peacetime leisure (Sit/Socialize/idle) so the
            // mat actually finishes — still peacetime-gated, so hunger/thirst/danger
            // always preempt it (survival is never traded for a bed).
            var bedLeafPull = siteNeedsLeaves ? 0.35f : 0f;
            var bedStickPull = siteNeedsSticks ? 0.35f : 0f;
            var bedRopePull = siteNeedsRope ? 0.35f : 0f;
            // BuildFurniture fires when I can advance the site: bring a material
            // it still needs, or raise it once stocked — with a hammer, except a
            // §54 campfire (piled from stones) and the leaf mat (hand-lashed).
            var siteIsBed = buildSite?.BuildProduct is "bed.leaf" or "bed.basic";
            // §35.5B: the rack is lashed sticks like the leaf mat — no hammer.
            var siteWaivesHammer = siteIsHearth ||
                buildSite?.BuildProduct is "bed.leaf" or "station.drying_rack";
            // §54.13: this is only the RAISE half. The deliver half is decided
            // next to the BuildFurniture score, where the gather flags exist —
            // staged sites take bundles, not single pieces (see below).
            var buildFurnitureRaise = buildSite != null && buildWindow &&
                BuildSiteMath.IsStocked(buildSite) &&
                (siteWaivesHammer || (canUseToolsOrWeapons && hasHammer));

            // Spec 29E: the fire chain still needs these — a pot/lighter/wood
            // and a seen campfire drive the fuel/craft goals further below.
            var hasLighter = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Ignite);
            var hasPot = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Boil);
            // Spec §54: "wood in hand" for fire/craft now means a STICK.
            var hasWood = npc.Inventory.Items.Contains("resource.stick");
            var (campfireSeen, campfireFuel, campfireObj) = FindCampfire(npc, world);
            // §gear-craft: a recipe with NO station crafts in place — its
            // availability must not demand a campfire in view.
            bool CraftPlaceOk(GoalType craftGoal)
            {
                var station = Content.RecipeCatalog.StationOf(craftGoal);
                if (string.IsNullOrEmpty(station))
                {
                    return true;
                }

                return station == "Campfire"
                    ? campfireSeen
                    : HasReachableWithTag(npc, world, station);
            }
            var hasBottleWater = HasBottleWater(npc);
            var drinkAvail = npc.Needs.Thirst >= 0.35f && (hasBottleWater || hasCoconutWater);
            var getWaterAvail = hasCoconutBlade && npc.Needs.Thirst >= 0.35f && !hasCoconutWater &&
                !hasBottleWater &&
                HasReachableDefinitionWorthCarrying(npc, world, "food.coconut");
            // Costs come from the RECIPES (asset-overridable), not constants —
            // an asset that reprices a tool re-prices its gathering too.
            var axeStoneCost = Content.RecipeCatalog.InputCount(GoalType.CraftAxe, "resource.stone");
            var pickaxeStoneCost = Content.RecipeCatalog.InputCount(GoalType.CraftPickaxe, "resource.stone");
            var knifeStickCost = Content.RecipeCatalog.InputCount(GoalType.CraftKnife, "resource.stick");
            var knifeStoneCost = Content.RecipeCatalog.InputCount(GoalType.CraftKnife, "resource.stone");
            var coconutToolPressure = !hasCoconutBlade &&
                (npc.Needs.Thirst >= 0.35f || npc.Needs.Hunger >= getFoodHungerThreshold) &&
                HasCoconutOpportunity(npc, world);
            var coconutToolBoost = coconutToolPressure
                ? System.MathF.Max(npc.Needs.Thirst, npc.Needs.Hunger)
                : 0f;
            var coconutEmergencyBoost =
                coconutToolPressure &&
                (npc.Needs.Thirst >= SimBalance.SleepInterruptThirst ||
                 npc.Needs.Hunger >= SimBalance.SleepInterruptHunger ||
                 npc.Mind.IsDehydrated ||
                 npc.Mind.IsStarving)
                    ? SimBalance.StarvingBoost
                    : 0f;
            // §55: boiling water is retired — the fire chain no longer earns a
            // "boil" bonus, only warmth/cooking motivate it now.
            var wantsBoil = false;
            // Spec 35.2: any reachable Tool not carried (saw, dropped gear).
            var gatherToolsAvail = HasMissingToolReachable(npc, world);
            var fuelLow = campfireSeen && campfireFuel < 600f;
            // Spec 40.15 r3: the raft is a wood SINK of its own — with only
            // fire/build demand the endgame rode on leftover logs and crawled
            // (15-day soaks: 3 deposits). A settled girl who knows the raft
            // stocks up to 3 logs before the coast run so each trip counts.
            var raftWoodDemand = carriedLogs < 3 &&
                world.RaftProgress < WorldState.RaftTarget &&
                npc.Needs.Hunger < 0.55f && npc.Needs.Thirst < 0.55f &&
                npc.Memory.Dangers.Count == 0 &&
                KnowsReachableWithTag(npc, world, "Raft");
            // Spec §54: fetch wood off the ground when the fire is dying and
            // there's nothing burnable in hand (no stick AND no log to split), or
            // when a build/site/raft still wants logs. Targets any "Wood" — a
            // stick fuels directly, a log gets split (or built with).
            // §54.12: a site's stick stage pulls loose ground sticks too — SplitLog
            // only covers the log-rich camp; with a fed fire and no logs around,
            // nothing else ever picked a scattered stick up for the bed.
            var gatherWoodAvail = ((fuelLow && carriedSticks == 0 && carriedLogs == 0) ||
                    (piece is { } pLog && carriedLogs < pLog.Logs) ||
                    siteNeedsLogs ||
                    (siteNeedsSticks &&
                     carriedSticks < BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialSticks)) ||
                    raftWoodDemand ||
                    (coconutToolPressure && carriedSticks < knifeStickCost)) &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Wood");
            // §45 r5: a genuinely cold girl can start the fire WITHOUT the
            // lighter (friction/hand-drill). The freeze probe showed 60-75%
            // of all freezing npc-ticks were "dead fire + wood in hand + no
            // lighter" — one lighter per colony and a half-day burn time
            // meant the carrier was almost never the one freezing at the
            // pit, and seed 777 died of hypothermia around that lock. The
            // threshold (-0.35, before the -0.85 damage band) keeps the
            // lighter meaningful in mild weather.
            // §54.14 (r2) hysteresis: once she's been that cold, the drill
            // stays in her hands for a grace window — the probe showed the
            // walk to the pit warming her past the gate, the goal collapsing
            // mid-route and the fire never lighting (cold start regression).
            if (npc.Needs.ThermalComfort < -0.35f)
            {
                npc.Mind.LastFreezingTick = world.Tick;
            }

            var canFrictionLight = npc.Needs.ThermalComfort < -0.35f ||
                world.Tick - npc.Mind.LastFreezingTick < SimBalance.FrictionLightGraceTicks;
            var tendFireAvail = hasWood && fuelLow &&
                (campfireFuel > 0f || canFrictionLight || (canUseToolsOrWeapons && hasLighter));

            var drinkNeedScore = npc.Needs.Thirst +
                (npc.Needs.Thirst >= npc.Needs.Hunger ? 0.05f : 0f);
            AddGoalScore(npc, world.Tick, GoalType.Drink, drinkNeedScore, drinkAvail, drinkBoost);
            AddGoalScore(npc, world.Tick, GoalType.GetWater, drinkNeedScore, getWaterAvail, drinkBoost);
            // Spec 42: cold drives the WHOLE fire chain, not just the last
            // link — a freezing girl fetches the lighter and hauls wood with
            // fire-priority, otherwise the chain never outbids water/food and
            // the pit stays cold forever (goal histogram: fire goals absent).
            var coldChain = npc.Needs.ThermalComfort < -0.15f
                ? 0.4f * npc.Needs.ThermalDiscomfort
                : 0f;
            // Spec §49 (Tier C): once she's decided to boil rather than gamble on
            // raw, push the fire chain so the pit actually gets lit — otherwise
            // the suppressed raw goal just leaves her thirsty by a dead fire.
            var boilChain = wantsBoil ? Spec49.BoilChainWeight : 0f;
            AddGoalScore(npc, world.Tick, GoalType.GatherTools,
                0.25f + 0.2f * npc.Needs.Thirst + coldChain + boilChain,
                gatherToolsAvail, coconutEmergencyBoost);
            // The raft pull mirrors BuildRaft's weight: stocking logs for the
            // coast run must win the auction as often as the run itself, or
            // the demand flag never turns into wood in hand (soak: GatherWood
            // won 8-10 times in 15 days while the raft starved).
            AddGoalScore(npc, world.Tick, GoalType.GatherWood,
                0.2f + 0.3f * npc.Needs.Thirst + coldChain + boilChain +
                (raftWoodDemand ? 0.3f : 0f) + coconutToolBoost + bedStickPull,
                gatherWoodAvail, coconutEmergencyBoost);
            // Spec 42: cold is the second reason to light the fire — a
            // freezing girl with wood and a lighter prioritizes the flame
            // over almost everything (this is THE way to warm up now).
            var freezing = npc.Needs.ThermalComfort < -0.15f;
            // Fire-priority probe (6 seeds x 4 days): when freezing AND the fire
            // was fully lightable (wood in hand, pit seen, friction unlocked),
            // TendFire still LOST the auction 82% of the time — beaten by Dress
            // and Sleep, which both carry the FULL cold weight (Dress = 0.1+TD).
            // At 0.45*TD the fire chain could never outbid "put on clothes" on
            // the cold axis, so a near-naked girl circled the empty wardrobe
            // while the pit stayed dark (fire uptime ~20%, lit ~2x/seed/4d).
            // Raised 0.45 -> 0.9*TD: lighting a DEAD fire is at least as urgent
            // as re-dressing (which can't fix a 6C night) — clears Dress (~1.1)
            // and Sleep (~1.1) plus the 0.15 switch margin at full cold.
            AddGoalScore(npc, world.Tick, GoalType.TendFire,
                0.25f + 0.3f * npc.Needs.Thirst + boilChain +
                (freezing ? 0.9f * npc.Needs.ThermalDiscomfort : 0f), tendFireAvail);

            // Spec 42: WarmUp — go stand by the burning fire until the chill
            // lifts. Available while genuinely cold and a lit fire is known;
            // the thermal system does the rest (fire is a real heat source).
            var warmUpAvail = freezing && npc.Needs.ThermalDiscomfort >= 0.35f &&
                campfireSeen && campfireFuel > 0f;
            AddGoalScore(npc, world.Tick, GoalType.WarmUp,
                0.35f + 0.55f * npc.Needs.ThermalDiscomfort, warmUpAvail);

            // Spec 44: the herbal first-aid chain — gather leaves, craft a
            // bandage at the fire. Urgency scales with how hurt anyone is.
            var herbLeaves = CountInventory(npc, "resource.herb_leaf");
            var hurtUrgency = npc.Health < 0.7f ? 0.3f : 0f;
            var gatherHerbAvail = herbLeaves < 2 && npc.Needs.Bandages < 2 &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Herb");
            AddGoalScore(npc, world.Tick, GoalType.GatherHerb,
                0.22f + hurtUrgency, gatherHerbAvail);
            var craftBandageAvail = herbLeaves >= 2 && npc.Needs.Bandages < 2 && CraftPlaceOk(GoalType.CraftBandage);
            AddGoalScore(npc, world.Tick, GoalType.CraftBandage,
                0.3f + hurtUrgency, craftBandageAvail);

            // Spec 29F: hunting & crafting.
            var hasSpear = npc.Inventory.Items.Contains("tool.spear");
            var hasRawMeat = npc.Inventory.Items.Contains("food.meat_raw");
            var hideCount = CountInventory(npc, "resource.hide");
            // Spec 35.6: ranged hunters need no spear.
            var hasBow = npc.Inventory.Items.Contains("tool.bow");
            var arrowCount = CountInventory(npc, "resource.arrow");
            // Spec §52: the spear is two-handed — wielding it needs both hands,
            // so a one-armed survivor (§50) can't spear-hunt. The bow is likewise
            // two-handed. Lose an arm and hunting is off the table.
            var canWield2Handed = canUseToolsOrWeapons && npc.Body.IntactHands >= 2;
            var armed = (hasSpear || (hasBow && arrowCount > 0)) && canWield2Handed;
            // A carried coconut does not block the hunt — meat is also hide,
            // and hide is pants and a bow; the old any-food gate left the
            // whole leather/bow tier dormant (4 seeds, ~0 hunts). But the
            // window closes at 0.55: a truly hungry NPC takes the sure meal,
            // not a chase with a 50% roll (starving storms otherwise).
            // Spec 29F.4 (iter 32): window widened 0.55 -> 0.8. With coconuts
            // scarce, hunger climbs past 0.55 often and GetFood may find no
            // fruit — hunting must stay available as the real meat/hide source
            // rather than ceding to a starve.
            var huntAvail = armed && !hasRawMeat &&
                npc.Needs.Hunger >= 0.3f && npc.Needs.Hunger < 0.8f &&
                NearestVisibleRabbit(npc, world) is not null;
            var craftSpearAvail = canUseToolsOrWeapons && !hasSpear && hasWood && CraftPlaceOk(GoalType.CraftSpear);
            // §54.14 (r2): cooking is the SPIT's job (stage 3) — CookMeat now
            // HANGS a raw chunk on the crossbar of a lit fire; the roast itself
            // runs in FireSystem over ~MeatRoastDurationTicks. No spit (or a
            // full crossbar) = no cooking, whatever else the fire can do.
            var spitReady = BuildSiteMath.CampfireSpitComplete(campfireObj);
            var spitHooksFree = spitReady &&
                BuildSiteMath.HangingMeat(campfireObj, "food.meat_raw") +
                BuildSiteMath.HangingMeat(campfireObj, "food.meat_cooked") <
                SimBalance.CampfireSpitCapacity;
            var cookAvail = hasRawMeat && campfireSeen && campfireFuel > 0f && spitHooksFree;
            var craftLeatherAvail = hideCount >= 1 && CraftPlaceOk(GoalType.CraftLeather) &&
                !npc.WornItems.Contains("clothing.leather_pants");

            // Inside the peckish window the hunt genuinely outbids GetFood
            // (0.3+0.5h > h for h < 0.6); the availability window above is
            // what protects mealtimes, not the curve.
            AddGoalScore(npc, world.Tick, GoalType.Hunt,
                0.3f + 0.5f * npc.Needs.Hunger, huntAvail, emergencyBoost);
            AddGoalScore(npc, world.Tick, GoalType.CraftSpear,
                0.2f + 0.2f * npc.Needs.Hunger, craftSpearAvail);
            AddGoalScore(npc, world.Tick, GoalType.CookMeat,
                0.3f + 0.4f * npc.Needs.Hunger, cookAvail, emergencyBoost);
            AddGoalScore(npc, world.Tick, GoalType.CraftLeather, 0.35f, craftLeatherAvail);

            // Spec 35.6: bow & arrows — pants outrank the first hide (0.35).
            // §gear: the bow is RETIRED pending the hunting rework — never
            // crafted, so the whole archery path stays dormant.
            var craftBowAvail = false && !hasBow && carriedSticks >= 2 && hideCount >= 1 &&
                carriedRope >= 1 && CraftPlaceOk(GoalType.CraftBow) && !craftLeatherAvail;
            var craftArrowsAvail = false && hasBow && arrowCount == 0 && hasWood && CraftPlaceOk(GoalType.CraftArrows); // §gear: bow retired
            AddGoalScore(npc, world.Tick, GoalType.CraftBow, 0.3f, craftBowAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftArrows, 0.3f, craftArrowsAvail);

            // Spec 28.15C/28.15D: a griever visits the body for closure;
            // a lonely NPC visits a known grave for remembrance.
            var corpseReachable = HasReachableWithTag(npc, world, "Corpse");
            var graveVisit = !isGrieving && npc.Needs.Social < 0.35f &&
                HasReachableWithTag(npc, world, "Grave");
            var mournAvail = (isGrieving && corpseReachable) || graveVisit;
            AddGoalScore(npc, world.Tick, GoalType.Mourn, isGrieving ? 0.7f : 0.3f, mournAvail);

            // Spec 28.15D: any housemate lays a body to rest.
            AddGoalScore(npc, world.Tick, GoalType.Bury, 0.6f, corpseReachable);

            if (piece is not null && fuelLow)
            {
                piece = null; // the hearth outranks the walls
            }

            // Spec 35.2: the tool & harvest chain.
            // Capability-driven (gear unification): "axe" here means ANY
            // wood-chopper and "pickaxe" any miner — a new chopping/mining
            // tool asset satisfies these decisions with no code change.
            var hasAxe = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.ChopWood);
            var hasSaw = false; // folded into the ChopWood capability above
            var hasPickaxe = Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Mine);
            var stoneCount = CountInventory(npc, "resource.stone");
            var stonesNeeded = (!hasAxe && !hasSaw ? axeStoneCost : 0) + (!hasPickaxe ? pickaxeStoneCost : 0);
            var gatherStoneAvail = (stoneCount < stonesNeeded ||
                    (coconutToolPressure && stoneCount < knifeStoneCost) ||
                    (piece is { } pStone && stoneCount < pStone.Stones) ||
                    (siteNeedsStones && stoneCount < 1)) &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Stone");
            var craftAxeAvail = canUseToolsOrWeapons && !hasAxe && !hasSaw && hasWood &&
                stoneCount >= axeStoneCost && CraftPlaceOk(GoalType.CraftAxe);
            var craftPickaxeAvail = canUseToolsOrWeapons && !hasPickaxe && hasWood &&
                stoneCount >= pickaxeStoneCost && CraftPlaceOk(GoalType.CraftPickaxe);
            var canChop = canUseToolsOrWeapons && (hasAxe || hasSaw);
            // §47 comfort: a bed per girl, not per colony. bedDeficit drives
            // both the leaf supply (chop a palm when short) and CraftBed.
            var bedDeficit = CountReachableWithTag(npc, world, "Bed") <
                world.Entities.Npcs.Count;
            // §54.2 fix: don't fell a NEW palm while the LAST one's harvest still
            // lies unprocessed on the ground. A felled palm scatters its CROWN
            // (the leaf source) + logs instead of filling the pack, so the
            // leaf/wood gates below stay "satisfied by 0 carried" and re-fire on
            // the next-nearest palm — the NPC mowed the whole grove and left a
            // trail of un-worked crowns/leaves. Finish one tree's output first: an
            // un-chopped crown OR loose leaves already on the ground block the leaf
            // motive here (chop-crown/gather-leaves take over); the wood/fuel motive
            // is already blocked by a reachable "Wood" (logs carry that tag).
            var pendingLeafSource = HasReachableWithTag(npc, world, "PalmCrown") ||
                HasReachableWithTag(npc, world, "PalmLeaf");
            var harvestTreeAvail = canChop && npc.Inventory.HasSpace &&
                ((fuelLow && !HasReachableWithTag(npc, world, "Wood") &&
                  HasReachableWithTag(npc, world, "Palm")) ||
                 (!pendingLeafSource &&
                  (CountInventory(npc, "resource.palm_leaf") == 0 ||
                   (piece is { } pLeaf && carriedLeaves < pLeaf.Leaves) ||
                   (bedDeficit && carriedLeaves < 3)) &&
                  HasReachableWithTag(npc, world, "Palm")));
            var mineBoulderAvail = canUseToolsOrWeapons && hasPickaxe && stoneCount < 2 && npc.Inventory.HasSpace &&
                HasReachableWithTag(npc, world, "Boulder");

            // Spec 45: FREE HANDS — needs handled, no danger => the surplus
            // goes into progress. Static 0.25-0.3 scores never beat the
            // needs-driven day (30-day runs: raft 0/20, no tools, no build);
            // a settled girl now picks up the pickaxe instead of strolling.
            // Spec 45 r2: "good enough" beats "perfect" — the strict 0.45
            // gate never opened (thirst lives above it), so no surplus ever
            // reached the projects. Comfortable-ish and safe is enough.
            var freeHands = npc.Needs.Hunger < 0.55f && npc.Needs.Thirst < 0.55f &&
                npc.Needs.Energy > 0.35f && npc.Needs.ThermalDiscomfort < 0.5f &&
                npc.Memory.Dangers.Count == 0
                    ? 0.3f
                    : 0f;
            // §54 cold start: fetching stones for the first hearth is urgent too.
            AddGoalScore(npc, world.Tick, GoalType.GatherStone,
                (hearthUrgent ? 0.9f : 0.25f) + freeHands + coconutToolBoost,
                gatherStoneAvail, coconutEmergencyBoost);
            AddGoalScore(npc, world.Tick, GoalType.CraftAxe, 0.3f + freeHands, craftAxeAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftPickaxe, 0.25f + freeHands, craftPickaxeAvail);
            // §47 comfort: the bed-chain pull — mirrors the spec-42 cold
            // chain. Bedless nights are the colony's loudest churn (Sleep
            // plan-starts 787-995 per 25 days, ~all retries), yet at 0.3
            // HarvestTree lost the auction to Dress/Socialize and leaves>=3
            // held 0% of npc-ticks: the palm never got chopped, so the bed
            // never got woven. The pull fires only while the chain is
            // actually short (deficit + no leaves in hand).
            var bedChainPull = bedDeficit && carriedLeaves < 3 && canChop ? 0.25f : 0f;
            AddGoalScore(npc, world.Tick, GoalType.HarvestTree,
                0.3f + freeHands + bedChainPull, harvestTreeAvail);
            AddGoalScore(npc, world.Tick, GoalType.MineBoulder, 0.25f + freeHands, mineBoulderAvail);

            // Spec §54: split a ground log into sticks (the fuel/craft currency)
            // when short on sticks and there's stick demand — chiefly a dying
            // fire, or a stick-framed craft one split away. Needs a chop tool
            // (deadfall sticks bootstrap the first axe, breaking the chicken-and-
            // egg). Fire-urgent so it can win the auction and keep the hearth fed.
            var wantsSticks = fuelLow || siteNeedsSticks ||
                (campfireSeen && (!hasSpear ||
                    (!hasPickaxe && stoneCount >= 2) ||
                    (hasBow && arrowCount == 0)));
            // §54.10: sticks stack too — split toward the bed's stick bill when a
            // site needs them, not just the 2-stick fuel reserve (§54.12: toward
            // the CURRENT stage's shortfall).
            var stickCap = siteNeedsSticks
                ? BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialSticks)
                : 2;
            // §gear-data: the log's own declaration decides the tool (axe OR
            // knife OR whatever the asset lists) — canChop is only the legacy
            // fallback for undeclared content.
            var splitLogAvail = carriedSticks < stickCap &&
                CanPerformDeclared(world, npc, "resource.log", InteractionType.Process,
                    legacyOk: canChop) &&
                npc.Inventory.HasSpace &&
                HasReachableWithTag(npc, world, "Log") && wantsSticks;
            AddGoalScore(npc, world.Tick, GoalType.SplitLog,
                (fuelLow ? 0.5f : 0.3f) + freeHands + bedStickPull, splitLogAvail);

            // Spec §54.2: chop a felled palm CROWN into loose leaves — when leaves
            // are wanted (a bed short, or a build/tent bill) and a crown lies
            // reachable. Needs a chop tool (the same Process gate as splitting).
            // §54.10: with leaves stacking into one slot, a girl brings a whole
            // bundle per trip — so when a bed site is hungry for leaves, gather up
            // toward the full bill instead of stopping at 3 (which forced a dozen
            // half-empty trips and the bed never finished).
            // §54.12: the pre-stake "bed is coming, hoard a few leaves" clause only
            // applies while NO site exists — once one is staked, leaf demand follows
            // its STAGE (hoarding early leaves just fed a stash-at-fire/pick-back-up
            // churn loop with HaulToFire while the stick stage rejected them).
            var wantsLeaves = (bedDeficit && !siteIsBed && carriedLeaves < 3) ||
                (siteNeedsLeaves &&
                 carriedLeaves < BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialLeaves)) ||
                (piece is { } pcrown && carriedLeaves < pcrown.Leaves) ||
                (world.Environment.UvIndex > 0.4f && carriedLeaves < 4);
            var chopCrownAvail = wantsLeaves && canChop &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "PalmCrown");
            AddGoalScore(npc, world.Tick, GoalType.ChopCrown, 0.3f + freeHands + bedLeafPull, chopCrownAvail);
            // Spec §54.2: pick scattered palm leaves off the ground when wanted.
            var gatherLeavesAvail = wantsLeaves && npc.Inventory.HasSpace &&
                HasReachableWithTag(npc, world, "PalmLeaf");
            AddGoalScore(npc, world.Tick, GoalType.GatherLeaves, 0.28f + freeHands + bedLeafPull, gatherLeavesAvail);

            // Spec §54: the cordage & knife chain. Rope is wanted for bowstrings
            // and bed-site lashings; cloth when a sun-shelter is due; the knife is
            // a survival tool (no butchering without it). Gather fiber to feed
            // rope/cloth, then craft at the fire.
            var ropeTarget = siteNeedsRope && buildSite != null
                ? BuildSiteMath.Remaining(buildSite, BuildSiteMath.MaterialRope)
                : 1;
            var wantRope = (siteNeedsRope && carriedRope < ropeTarget) ||
                (!hasBow && hideCount >= 1 && carriedRope == 0);
            var wantCloth = bedDeficit && carriedCloth == 0;
            // §54.13: gather fiber for the WHOLE rope shortfall, not one rope's
            // worth — the old cost-of-one made a girl cut a yucca (4 fibers
            // scatter), pick up ONE, craft one rope, deliver it, and walk all
            // the way back seven more times. Fiber stacks (§54.10), so carrying
            // the full lashing bill is one pocket slot.
            var ropeShortfall = wantRope ? System.Math.Max(1, ropeTarget - carriedRope) : 0;
            var fiberNeed = ropeShortfall * SimBalance.RopeFiberCost +
                (wantCloth ? SimBalance.ClothFiberCost : 0);
            // Fiber now comes from CUTTING a yucca with a blade (knife/axe); the
            // cut fibers scatter, then get picked up. So: cut yucca → gather fiber.
            // §54.13: pick the ground CLEAN before cutting another plant. The
            // rope-stage soak showed HarvestYucca (0.26) outbidding GatherFiber
            // (0.24) until every yucca on the island was felled — 32 fibers lay
            // scattered while the site waited. Cutting is only available while
            // no loose fiber is reachable; yuccas are consumed, don't waste them.
            var harvestYuccaAvail = canUseToolsOrWeapons && carriedFiber < fiberNeed && npc.Inventory.HasSpace &&
                (hasKnife || hasAxe) && !HasReachableWithTag(npc, world, "Fiber") &&
                HasReachableWithTag(npc, world, "Yucca");
            var gatherFiberAvail = carriedFiber < fiberNeed && npc.Inventory.HasSpace &&
                HasReachableWithTag(npc, world, "Fiber");
            var craftRopeAvail = canUseToolsOrWeapons && wantRope && carriedFiber >= SimBalance.RopeFiberCost && CraftPlaceOk(GoalType.CraftRope);
            var craftClothAvail = canUseToolsOrWeapons && wantCloth && carriedFiber >= SimBalance.ClothFiberCost && CraftPlaceOk(GoalType.CraftCloth);
            var craftKnifeAvail = canUseToolsOrWeapons && !(hasKnife && hasButcherTool) &&
                carriedSticks >= knifeStickCost &&
                stoneCount >= knifeStoneCost && CraftPlaceOk(GoalType.CraftKnife);
            AddGoalScore(npc, world.Tick, GoalType.HarvestYucca, 0.26f + freeHands + bedRopePull, harvestYuccaAvail);
            AddGoalScore(npc, world.Tick, GoalType.GatherFiber, 0.24f + freeHands + bedRopePull, gatherFiberAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftRope, 0.28f + freeHands + bedRopePull, craftRopeAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftCloth, 0.28f + freeHands, craftClothAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftKnife,
                0.34f + freeHands + coconutToolBoost, craftKnifeAvail, coconutEmergencyBoost);

            // Spec §54: butcher a carcass (or, starving, a housemate's body) with
            // a knife — hunger-driven, since the payoff is meat.
            var butcherAvail = canUseToolsOrWeapons && hasButcherTool &&
                (HasReachableWithTag(npc, world, "Carcass") ||
                 (SimBalance.CannibalismEnabled &&
                  npc.Needs.Hunger >= SimBalance.CannibalizeHungerGate &&
                  HasReachableWithTag(npc, world, "Corpse")));
            AddGoalScore(npc, world.Tick, GoalType.Butcher,
                0.3f + 0.5f * npc.Needs.Hunger, butcherAvail);

            // §56 Predation: the absolute last resort. A low-compassion survivor,
            // starving with NO softer food left (not even a corpse to butcher)
            // and a knife in hand, hunts the weakest housemate for meat. The
            // NoOtherFoodReachable gate keeps it strictly below every real food
            // source; PredationBaseScore is a low floor so it can never outbid one.
            var preyAvail = SimBalance.PredationEnabled && canUseToolsOrWeapons && hasButcherTool &&
                npc.CompassionTrait <= SimBalance.PredationCompassionCeiling &&
                npc.Needs.Hunger >= SimBalance.PredationHungerGate &&
                NoOtherFoodReachable(npc, world) &&
                NearestPreyVictim(npc, world) is not null;
            AddGoalScore(npc, world.Tick, GoalType.Prey,
                SimBalance.PredationBaseScore + npc.Needs.Hunger, preyAvail);

            // Spec 35.3 + §52: build a hut piece when the full bill is carried
            // AND a hammer is in hand — raising a wall now needs the tool.
            var buildAvail = canUseToolsOrWeapons && piece is { } needNow &&
                carriedLogs >= needNow.Logs && stoneCount >= needNow.Stones &&
                carriedLeaves >= needNow.Leaves && hasHammer &&
                HasReachableWithTag(npc, world, "BuildSite");
            AddGoalScore(npc, world.Tick, GoalType.Build, 0.4f + freeHands, buildAvail);

            // Spec §52: haul a material to the furniture build-site, or raise it
            // with a hammer once stocked. Weighted like the retired CraftBed
            // (0.55) so a girl already carrying the stones actually delivers them
            // instead of wandering off — otherwise materials never reach the site.
            // §54 cold start: raising the first hearth outranks the day's chores.
            // §54.10: a bed delivery is lifted over peacetime leisure too, so a
            // girl carrying a bundle actually walks it to the site and taps it home.
            // §54.13: a STAGED site (the beds) takes BUNDLES, not single pieces.
            // Delivery (0.55+) outbid every gather goal (≤0.95) the moment ONE
            // piece was in hand, so the 46-leaf mattress became 46 round trips
            // (soak: leaf deliveries 1/46, 2/46, 3/46… spaced ~50-200 ticks).
            // A girl now keeps gathering until she carries the stage's shortfall
            // (capped at half a stack) — or until nothing more can be produced —
            // and only then walks the pile over. Non-bed sites (the campfire's
            // stone/spit upgrades §54.14, hut pieces) still take any piece:
            // stones don't stack.
            var deliverWorthwhile = buildSite != null && buildWindow &&
                CarriesSiteMaterial(npc, buildSite);
            if (deliverWorthwhile && siteIsBed)
            {
                foreach (var mat in BuildSiteMath.AllMaterials)
                {
                    var stageRemaining = BuildSiteMath.Remaining(buildSite, mat);
                    if (stageRemaining <= 0)
                    {
                        continue;
                    }

                    var carriedOfStage = 0;
                    foreach (var item in npc.Inventory.Items)
                    {
                        if (item.DefinitionId == mat)
                        {
                            carriedOfStage++;
                        }
                    }

                    var canGetMore = mat switch
                    {
                        BuildSiteMath.MaterialSticks => splitLogAvail || gatherWoodAvail,
                        BuildSiteMath.MaterialRope => craftRopeAvail || gatherFiberAvail || harvestYuccaAvail,
                        BuildSiteMath.MaterialLeaves => gatherLeavesAvail || chopCrownAvail,
                        BuildSiteMath.MaterialLogs => gatherWoodAvail,
                        _ => false
                    };
                    var bundle = System.Math.Min(
                        stageRemaining, InventoryState.StackSizeFor(mat) / 2);
                    deliverWorthwhile = carriedOfStage >= bundle ||
                        (carriedOfStage > 0 && !canGetMore);
                    break;
                }
            }

            var buildFurnitureAvail = buildFurnitureRaise || deliverWorthwhile;
            var buildFurniturePull = (siteNeedsLeaves || siteNeedsSticks || siteNeedsRope) ? 0.2f : 0f;
            // §54.14 (r2): the hearth is built FOR warmth/comfort — a cold girl
            // pushes the stick-pile delivery with the same cold weight that
            // drives the rest of the fire chain (there is no fire to tend yet).
            AddGoalScore(npc, world.Tick, GoalType.BuildFurniture,
                (hearthUrgent ? 0.95f : 0.55f) + freeHands + buildFurniturePull +
                (siteIsHearth ? coldChain : 0f), buildFurnitureAvail);

            // Spec §52: free a slot by carrying a low-value item to the fireside
            // stockpile — but only in peace. Life-threatening pressure (a dog, a
            // stat below 40 %) cancels the errand: you don't tidy your pockets
            // while something is trying to eat you.
            var lifeThreatened = npc.IsFighting || npc.Memory.Dangers.Count > 0 ||
                npc.Health < 0.4f || npc.Needs.Hunger >= 0.6f || npc.Needs.Thirst >= 0.6f;
            var haulVictim = InventoryMath.LowestImportanceDroppable(world, npc);
            var haulToFireAvail = !npc.Inventory.HasSpace && !lifeThreatened && campfireSeen &&
                haulVictim != null &&
                InventoryMath.Importance(world, haulVictim) <= 25;
            AddGoalScore(npc, world.Tick, GoalType.HaulToFire, 0.28f, haulToFireAvail);

            // Spec 35.4: overheating drives a trip to shade or the river. Gate on
            // the latched IsOverheated (enter 0.35 / clear 0.20) rather than a raw
            // 0.35 compare, so availability doesn't flicker on/off around the edge
            // and re-win at zero margin every tick (the old None→CoolOff churn).
            var coolOffUrge = System.MathF.Max(npc.Needs.ThermalDiscomfort, npc.SunExposure - 0.4f);
            var coolOffAvail = ((effectiveTemp > 20f && npc.Mind.IsOverheated) ||
                    npc.SunExposure >= 0.6f) &&
                (HasReachableWithTag(npc, world, "Shade") || HasReachableWithTag(npc, world, "Water"));
            AddGoalScore(npc, world.Tick, GoalType.CoolOff,
                0.1f + 0.5f * coolOffUrge, coolOffAvail);

            var batheNeed = 1f - npc.Needs.Hygiene;
            var batheAvail = batheNeed >= SimBalance.BatheNeedThreshold &&
                HasReachableBathTile(world, npc) && npc.Body.CanUseToolsOrWeapons;
            if (npc.Mind.CurrentGoal == GoalType.Bathe && npc.Plan.Status == PlanStatus.Active)
            {
                batheAvail = true;
            }
            AddGoalScore(npc, world.Tick, GoalType.Bathe, batheNeed * 0.7f, batheAvail);

            var washNeed = DirtyGarmentWashNeed(world, npc);
            var washAvail = washNeed >= SimBalance.WashClothesNeedThreshold;
            if (npc.Mind.CurrentGoal == GoalType.WashClothes && npc.Plan.Status == PlanStatus.Active)
            {
                washAvail = true;
            }
            AddGoalScore(npc, world.Tick, GoalType.WashClothes, washNeed * 0.75f, washAvail);

            // Spec 35.5: rain, wet clothes, and the drying chain.
            var wornWetness = 0f;
            foreach (var wornItem in npc.WornItems)
            {
                wornWetness = System.MathF.Max(wornWetness, wornItem.Wetness);
            }


            // Spec 29G: the bed must be earned — 2 logs + 3 palm leaves.
            // The hearth outranks the mattress: never spend logs on a bed
            // while the fire is hungry (777 soak: the bed ate the only wood
            // and the fire never burned again — boiledDrinks=0 all game).
            // Spec 40.14: 3 leaves alone weave the cheap leaf mat; 2 spare logs
            // upgrade it to the solid bedroll (chosen at craft time). The fire
            // must still be alive — its logs are never robbed for the bedroll.
            // §47 comfort: two locks removed. (1) "any reachable bed"
            // capped the colony at ONE crafted bed for three girls —
            // bedDeficit (beds < living girls) lets everyone earn her own.
            // (2) campfireFuel > 0 was the same permanent lock the raft and
            // fire chains had (the pit burns 10-20% of the time; CraftBed
            // never appeared in ANY soak's won-auction top-14) — weaving at
            // the cold pit is fine, the Craft interaction never needed the
            // flame anyway.
            // Spec §54.2: the leaf mat is no longer an atomic craft. BedSiteSystem
            // stakes a bed.leaf build-site by the hearth, and the girls haul the
            // 16 leaves + 6 sticks to it over many trips via the BuildFurniture
            // chain (below) — the mat grows piece by piece, a hammer finishes it.
            // So CraftBed is retired here; leave it disabled rather than churn the
            // Craft dispatch/catalog it still nominally routes through.
            var craftBedAvail = false;
            AddGoalScore(npc, world.Tick, GoalType.CraftBed, 0.6f, craftBedAvail);

            // Spec 40.14: a sun shelter — woven from 4 spare palm leaves at the
            // fire when the sun bites and there's no shade near home yet. Score
            // scales with the current UV so it's a fair-weather project, not a
            // constant pull (keeps the fragile colony from reshuffling).
            var craftTentAvail = canUseToolsOrWeapons && world.Environment.UvIndex > 0.4f &&
                carriedLeaves >= 4 && carriedCloth >= 1 && campfireSeen &&
                !HasReachableWithTag(npc, world, "Shelter");
            AddGoalScore(npc, world.Tick, GoalType.CraftTent,
                0.25f + 0.3f * world.Environment.UvIndex, craftTentAvail);

            // Spec 40.15: the escape raft — a low-priority background project.
            // Only when survival is handled (fed, watered, no danger) does a
            // log get carried to the coast; the way off the island is earned
            // slowly, never at the expense of staying alive. No fire clause:
            // !fuelLow (fuel >= 600) was almost never true in the campfire
            // era, and even "fire burning" holds <16% of the time on 10-day
            // soaks — either variant locks the endgame out permanently.
            // Hunger/thirst/danger already express "the household can spare
            // a pair of hands"; TendFire outbids the raft when fuel matters.
            var buildRaftAvail = canUseToolsOrWeapons && carriedLogs >= 1 && world.RaftProgress < WorldState.RaftTarget &&
                npc.Needs.Hunger < 0.55f && npc.Needs.Thirst < 0.55f &&
                npc.Memory.Dangers.Count == 0 && KnowsReachableWithTag(npc, world, "Raft");
            // A loaded girl leans coastward: each carried log adds pull so
            // the stocked-up trip actually happens instead of dissolving
            // into strolls. Base 0.4 (was 0.28): plan statistics showed the
            // old weight won the auction 1-5 times in 15 days — the endgame
            // needs to outbid moderate needs (~0.6) whenever the gate is
            // open, and the gate itself already guarantees she's fed,
            // watered and safe when she commits to the coast run.
            AddGoalScore(npc, world.Tick, GoalType.BuildRaft,
                0.4f + freeHands + 0.05f * carriedLogs, buildRaftAvail);
            // §35.5B: CraftRack retired — the rack is a staged fireside
            // build-site now (BedSiteSystem stakes it; BuildFurniture raises).

            var dryAvail = wornWetness > 0.5f && !world.Environment.IsRaining &&
                (HasReachableWithTag(npc, world, "Rack") ||
                 (campfireSeen && campfireFuel > 0f));
            AddGoalScore(npc, world.Tick, GoalType.DryClothes,
                0.15f + 0.4f * wornWetness, dryAvail);


            // Spec 31A.5A: hot and safe → take something off. If everything
            // warm is also armor under fresh danger, keep sweating. Spec 42:
            // only in REAL heat (past the [16,22] band) — day-warmth must not
            // strip the layers that the cold night needs back in an hour.
            var undressAvail = effectiveTemp > 24f && npc.Needs.ThermalDiscomfort >= 0.4f &&
                FindRemovableItem(npc, world) is not null;
            AddGoalScore(npc, world.Tick, GoalType.Undress, npc.Needs.ThermalDiscomfort, undressAvail);

            // Spec 29C.5: seeded wandering urge, changes every 160 ticks —
            // occasionally beats Idle, loses to any pressing need.
            var exploreJitter = MathUtil.Hash01(world.Seed, world.Tick / 160, npc.Id.Value, 77) * 0.2f;
            // Spec 31C.7A: a settled colony strolls — long rests made the
            // girls genuinely idle, and idle should wander, not loiter.
            // ...and only when the hearth is in order — full-time tourism
            // collapsed the fire/craft economy on the first soak.
            // Comfort > 0.35 (was 0.5): with beds earned rather than given
            // (spec 29G) comfort is a luxury — the old bar made wanderlust
            // unreachable and the outings gate starved (777: explores=0).
            var wellRested = npc.Needs.Hunger < 0.5f && npc.Needs.Thirst < 0.5f &&
                npc.Needs.Energy > 0.5f && npc.Needs.Comfort > 0.35f && !fuelLow ? 0.15f : 0f;
            AddGoalScore(npc, world.Tick, GoalType.Explore, 0.05f + exploreJitter + wellRested, true);

            AddGoalScore(npc, world.Tick, GoalType.Idle, 0.05f, true);

            if (bleedingCrisis)
            {
                SuppressPeacetimeDuringBleeding(npc);
            }

            Trace.Emit(world, npc.Id, "DecisionInput",
                $"Needs=[{Trace.FormatNeeds(npc.Needs)}] " +
                $"Available=[Eat={eatAvail} GetFood={getFoodAvail} Sleep={sleepAvail} Sit={sitAvail} " +
                $"Dress={dressAvail} Socialize={socializeAvail}] " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
                $"PrevGoal={previousGoal} PlanStatus={npc.Plan.Status} ExecStatus={npc.Execution.Status}");

            GoalScore? best = null;
            foreach (var score in npc.Mind.LastScores)
            {
                Trace.Emit(world, npc.Id, "GoalScored",
                    $"{score.Goal}: Base={score.BaseScore:F3} Need={score.NeedModifier:F3} " +
                    $"Mem={score.MemoryModifier:F3} Soc={score.SocialModifier:F3} " +
                    $"Env={score.EnvironmentModifier:F3} Cmd={score.CommandModifier:F3} " +
                    $"Emg={score.EmergencyModifier:F3} " +
                    $"=> Final={score.FinalScore:F3}");

                if (best is null || score.FinalScore > best.FinalScore)
                {
                    best = score;
                }
            }

            if (best is null)
            {
                Trace.Emit(world, npc.Id, "DecisionSkipped", "No scores available");
                continue;
            }

            // Spec 23.8/23.9 (iteration 3): hold-or-adopt. A competing goal must
            // beat the current one by a margin; locks resist ordinary drift.
            if (best.Goal != previousGoal &&
                previousGoal != GoalType.None && previousGoal != GoalType.Idle)
            {
                var currentScore = 0f;
                foreach (var score in npc.Mind.LastScores)
                {
                    if (score.Goal == previousGoal)
                    {
                        currentScore = score.FinalScore;
                        break;
                    }
                }

                var hasActivePlan = npc.Plan.Status == PlanStatus.Active ||
                    npc.Execution.Status == ExecutionStatus.InProgress;
                var locked = npc.Mind.GoalLock is { } goalLock &&
                    goalLock.Goal == previousGoal && world.Tick < goalLock.EndTick;
                var threshold = locked ? LockOverrideDelta : (hasActivePlan ? SwitchDelta : 0f);

                if (best.FinalScore - currentScore <= threshold)
                {
                    Trace.Emit(world, npc.Id, "GoalHeld",
                        $"{previousGoal} kept over {best.Goal} " +
                        $"(lead={best.FinalScore - currentScore:F3} <= {threshold:F2}" +
                        $"{(locked ? $", locked until {npc.Mind.GoalLock!.EndTick}" : "")})");
                    continue;
                }
            }

            npc.Mind.CurrentGoal = best.Goal;
            npc.Mind.LastDecision = new DecisionResult
            {
                SelectedGoal = best.Goal,
                Reason = $"Selected {best.Goal} at tick {world.Tick}"
            };

            foreach (var score in npc.Mind.LastScores)
            {
                npc.Mind.LastDecision.Scores.Add(score);
            }

            var changed = previousGoal != best.Goal;
            if (changed && best.Goal != GoalType.Idle && best.Goal != GoalType.None)
            {
                npc.Mind.GoalLock = new GoalLock
                {
                    Goal = best.Goal,
                    StartTick = world.Tick,
                    EndTick = world.Tick + GoalLockTicks
                };
            }

            Trace.Emit(world, npc.Id, "GoalSelected",
                $"{best.Goal} (Score={best.FinalScore:F3}) " +
                $"{(changed ? $"CHANGED from {previousGoal}" : "UNCHANGED")}");

            // Spec 23.17: a goal change over an active plan must abort cleanly,
            // releasing object occupancy and junction reservation before replanning.
            if (changed &&
                (npc.Plan.Status == PlanStatus.Active || npc.Execution.Status == ExecutionStatus.InProgress))
            {
                PlanInterruption.Abort(world, npc,
                    $"Goal changed {previousGoal}->{best.Goal} over active plan " +
                    $"(Starving={npc.Mind.IsStarving})");
            }
        }
    }

    private static bool IsBleedingCrisis(NPCState npc)
    {
        if (npc.Needs.Blood >= 0.45f)
        {
            return false;
        }

        var worstPart = 1f;
        foreach (var part in npc.Body.Parts.Keys)
        {
            if (npc.Body.Parts[part] < worstPart)
            {
                worstPart = npc.Body.Parts[part];
            }
        }

        if (worstPart >= 0.4f)
        {
            return false;
        }

        foreach (var wound in npc.Wounds)
        {
            if (wound.Heal01 < 0.3f && wound.Severity >= 0.05f)
            {
                return true;
            }
        }

        return false;
    }

    private static void SuppressPeacetimeDuringBleeding(NPCState npc)
    {
        foreach (var score in npc.Mind.LastScores)
        {
            if (IsBleedingCrisisGoal(score.Goal, npc))
            {
                if (score.Goal == GoalType.CraftBandage)
                {
                    score.EmergencyModifier = System.MathF.Max(score.EmergencyModifier, StarvingBoost * 0.5f);
                    score.FinalScore += StarvingBoost * 0.5f;
                }

                continue;
            }

            score.FinalScore = 0f;
        }
    }

    private static bool IsBleedingCrisisGoal(GoalType goal, NPCState npc)
    {
        return goal switch
        {
            GoalType.Idle or GoalType.None => true,
            GoalType.Eat or GoalType.GetFood => npc.Mind.IsStarving,
            GoalType.Drink or GoalType.GetWater => npc.Mind.IsDehydrated,
            GoalType.CraftBandage => true,
            _ => false
        };
    }

    private static void UpdateDehydratedStatus(WorldState world, NPCState npc)
    {
        if (!npc.Mind.IsDehydrated && npc.Needs.Thirst >= StarvingEnterThreshold)
        {
            npc.Mind.IsDehydrated = true;
            Trace.Emit(world, npc.Id, "StatusDehydrated",
                $"Entered (Thirst={npc.Needs.Thirst:F2})");
        }
        else if (npc.Mind.IsDehydrated && npc.Needs.Thirst < StarvingClearThreshold)
        {
            npc.Mind.IsDehydrated = false;
            Trace.Emit(world, npc.Id, "StatusDehydrated",
                $"Cleared (Thirst={npc.Needs.Thirst:F2})");
        }
    }

    private static void UpdateStarvingStatus(WorldState world, NPCState npc)
    {
        if (!npc.Mind.IsStarving && npc.Needs.Hunger >= StarvingEnterThreshold)
        {
            npc.Mind.IsStarving = true;
            Trace.Emit(world, npc.Id, "StatusStarving",
                $"Entered (Hunger={npc.Needs.Hunger:F2} >= {StarvingEnterThreshold})");
        }
        else if (npc.Mind.IsStarving && npc.Needs.Hunger < StarvingClearThreshold)
        {
            npc.Mind.IsStarving = false;
            Trace.Emit(world, npc.Id, "StatusStarving",
                $"Cleared (Hunger={npc.Needs.Hunger:F2} < {StarvingClearThreshold})");
        }
    }

    // Spec 35.4: overheating latch. Enter at CoolOffEnterThreshold, clear at
    // CoolOffClearThreshold — the hysteresis stops CoolOff's availability from
    // flickering around the entry edge (the tight None→CoolOff→None churn loop).
    private static void UpdateOverheatedStatus(WorldState world, NPCState npc)
    {
        if (!npc.Mind.IsOverheated && npc.Needs.ThermalDiscomfort >= SimBalance.CoolOffEnterThreshold)
        {
            npc.Mind.IsOverheated = true;
            Trace.Emit(world, npc.Id, "StatusOverheated",
                $"Entered (Thermal={npc.Needs.ThermalDiscomfort:F2} >= {SimBalance.CoolOffEnterThreshold})");
        }
        else if (npc.Mind.IsOverheated && npc.Needs.ThermalDiscomfort < SimBalance.CoolOffClearThreshold)
        {
            npc.Mind.IsOverheated = false;
            Trace.Emit(world, npc.Id, "StatusOverheated",
                $"Cleared (Thermal={npc.Needs.ThermalDiscomfort:F2} < {SimBalance.CoolOffClearThreshold})");
        }
    }

    private static void AddGoalScore(NPCState npc, int currentTick, GoalType goal, float needValue, bool isAvailable, float emergency = 0f, float social = 0f, float environment = 0f)
    {
        // Spec 23.10: goals on cooldown are hard-gated.
        var onCooldown = false;
        foreach (var cooldown in npc.Mind.Cooldowns)
        {
            if (cooldown.Goal == goal && cooldown.EndTick > currentTick)
            {
                onCooldown = true;
                break;
            }
        }

        var available = isAvailable && !onCooldown;
        var score = new GoalScore
        {
            Goal = goal,
            BaseScore = goal == GoalType.Idle ? 0.01f : 0.1f,
            NeedModifier = needValue,
            EmergencyModifier = emergency,
            SocialModifier = social,
            EnvironmentModifier = environment,
            FinalScore = available ? 0.1f + needValue + emergency + social + environment : 0f
        };

        npc.Mind.LastScores.Add(score);
    }
}

}
