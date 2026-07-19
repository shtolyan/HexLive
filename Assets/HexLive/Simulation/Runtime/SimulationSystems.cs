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

public sealed class DecisionSystem : ISimulationSystem
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

    // Spec 35.3: materials bill for the pending hut piece.
    internal readonly struct BuildPiece
    {
        public BuildPiece(int logs, int stones, int leaves, int edge, string kind)
        {
            Logs = logs;
            Stones = stones;
            Leaves = leaves;
            Edge = edge;
            Kind = kind;
        }

        public int Logs { get; }

        public int Stones { get; }

        public int Leaves { get; }

        public int Edge { get; }

        public string Kind { get; }
    }

    internal static BuildPiece? NextBuildPiece(WorldState world)
    {
        var project = world.Project;
        if (project is null || project.Completed)
        {
            return null;
        }

        if (!project.FloorDone)
        {
            return new BuildPiece(1, 0, 2, -1, "Floor");
        }

        for (var i = 0; i < 6; i++)
        {
            if (i != project.DoorEdge && !project.EdgeDone[i])
            {
                return new BuildPiece(1, 1, 0, i, "Wall");
            }
        }

        if (!project.EdgeDone[project.DoorEdge])
        {
            return new BuildPiece(2, 0, 0, project.DoorEdge, "Door");
        }

        return null;
    }

    // Spec 29F helpers.
    // Spec 35.5: one communal rack is enough for v1.
    internal static bool RackExists(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == "station.drying_rack")
            {
                return true;
            }
        }

        return false;
    }

    internal static int CountInventory(NPCState npc, string definitionId)
    {
        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item == definitionId)
            {
                count++;
            }
        }

        return count;
    }

    // Spec 29F.1: rabbits are perceived by direct proximity scan (<= 4 tiles),
    // no rabbit memory in v1. Spooked rabbits don't count.
    internal static Wildlife.RabbitState? NearestVisibleRabbit(NPCState npc, WorldState world)
    {
        Wildlife.RabbitState? best = null;
        var bestDistance = int.MaxValue;
        foreach (var rabbit in world.Rabbits)
        {
            if (world.Tick < rabbit.SpookedUntilTick)
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(npc.Tile, rabbit.Tile);
            // Radius stays 4: 5-6 made hunts more frequent but the longer
            // chases dragged NPCs into dog country (wipes on two seeds).
            if (distance <= 4 && distance < bestDistance)
            {
                bestDistance = distance;
                best = rabbit;
            }
        }

        return best;
    }

    // §56: Prey unlocks only when EVERY softer food source is absent — carried
    // food, ground food, a known fruit producer, a rabbit to hunt, an animal
    // carcass, or an existing corpse to butcher. The corpse clause guarantees a
    // found body is always eaten before anyone is killed (a killed housemate is
    // strictly worse than one who died on their own).
    internal static bool NoOtherFoodReachable(NPCState npc, WorldState world)
    {
        if (npc.Inventory.FindFirstFood(world.Content) is not null)
        {
            return false;
        }

        if (HasReachableFoodForCurrentTools(npc, world) || HasCoconutMeal(npc, world))
        {
            return false;
        }

        if (KnowsReachableProducer(npc, world))
        {
            return false;
        }

        if (NearestVisibleRabbit(npc, world) is not null)
        {
            return false;
        }

        if (HasReachableWithTag(npc, world, "Carcass"))
        {
            return false;
        }

        if (HasReachableWithTag(npc, world, "Corpse"))
        {
            return false;
        }

        return true;
    }

    internal static bool HasCoconutMeal(NPCState npc, WorldState world)
    {
        if (npc.Inventory.Items.Contains("food.coconut_open"))
        {
            return true;
        }

        var hasBlade = HasCoconutBlade(npc);
        if (hasBlade &&
            (npc.Inventory.Items.Contains("food.coconut") ||
             npc.Inventory.Items.Contains("food.coconut_pierced")))
        {
            return true;
        }

        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable || !ObjectUsableBy(obj, npc.Id))
            {
                continue;
            }

            if (obj.DefinitionId == "food.coconut_open")
            {
                return true;
            }

            if (hasBlade &&
                (obj.DefinitionId == "food.coconut" ||
                 obj.DefinitionId == "food.coconut_pierced"))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasCoconutWater(NPCState npc, WorldState world)
    {
        var hasBlade = HasCoconutBlade(npc);
        if (hasBlade && npc.Inventory.Items.Contains("food.coconut"))
        {
            return true;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == "food.coconut_pierced" && item.ResourceAmount > 0f)
            {
                return true;
            }
        }

        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable || !ObjectUsableBy(obj, npc.Id))
            {
                continue;
            }

            if (hasBlade && obj.DefinitionId == "food.coconut")
            {
                return true;
            }

            if (obj.DefinitionId == "food.coconut_pierced" &&
                world.Entities.Objects.TryGetValue(obj.Id, out var worldObject) &&
                worldObject.ResourceAmount > 0f)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasBottleWater(NPCState npc) =>
        npc.BottleWater != WaterKind.None && npc.BottleCharges > 0;

    internal static bool HasInventoryCoconutMeal(NPCState npc)
    {
        if (npc.Inventory.Items.Contains("food.coconut_open"))
        {
            return true;
        }

        return HasCoconutBlade(npc) &&
            (npc.Inventory.Items.Contains("food.coconut") ||
             npc.Inventory.Items.Contains("food.coconut_pierced"));
    }

    internal static bool HasInventoryCoconutWater(NPCState npc)
    {
        if (HasBottleWater(npc))
        {
            return true;
        }

        if (HasCoconutBlade(npc) && npc.Inventory.Items.Contains("food.coconut"))
        {
            return true;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == "food.coconut_pierced" && item.ResourceAmount > 0f)
            {
                return true;
            }
        }

        return false;
    }

    // §50-prone: piercing a coconut is LIGHT hand-work — a one-legged crawler
    // with a knife still opens her dinner (Marta starved to death at day 28
    // sitting NEXT to coconuts because the blanket prone-gate blocked this).
    // Fighting and heavy tool work stay forbidden while lying.
    internal static bool HasCoconutBlade(NPCState npc) =>
        Content.GearCatalog.HasCapability(npc.Inventory.Items, Content.GearCapability.Cut);

    internal static bool HasCoconutOpportunity(NPCState npc, WorldState world)
    {
        if (npc.Inventory.Items.Contains("food.coconut") ||
            npc.Inventory.Items.Contains("food.coconut_pierced") ||
            npc.Inventory.Items.Contains("food.coconut_open"))
        {
            return true;
        }

        return HasReachableDefinition(npc, world, "food.coconut") ||
            HasReachableDefinition(npc, world, "food.coconut_pierced") ||
            HasReachableDefinition(npc, world, "food.coconut_open") ||
            KnowsReachableCoconutProducer(npc, world);
    }

    internal static bool HasReachableFoodForCurrentTools(NPCState npc, WorldState world)
    {
        var hasBlade = HasCoconutBlade(npc);
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !ObjectUsableBy(obj, npc.Id) ||
                !obj.AvailableInteractions.Contains(InteractionType.PickUp) ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition))
            {
                continue;
            }

            // §54.14 (r2): cooked meat hanging on the spit counts as reachable
            // food — the fire is the source object, the meat is what's taken.
            if (definition.Tags.Contains("Campfire"))
            {
                if (world.Entities.Objects.TryGetValue(obj.Id, out var fire) &&
                    BuildSiteMath.HangingMeat(fire, "food.meat_cooked") > 0 &&
                    InventoryMath.CanMakeRoomFor(world, npc, "food.meat_cooked"))
                {
                    return true;
                }

                continue;
            }

            if (!InventoryMath.CanMakeRoomFor(world, npc, obj.DefinitionId) ||
                !definition.Tags.Contains("Food"))
            {
                continue;
            }

            if (definition.Tags.Contains("Coconut") && !hasBlade)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static bool KnowsReachableCoconutProducer(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce?.ProducedDefinitionId == "food.coconut")
            {
                return true;
            }
        }

        return false;
    }

    // §56: the victim of a predation — the weakest reachable housemate. Lowest
    // health wins; a sleeper is discounted (an easy kill is preferred) and the
    // nearest breaks remaining ties. A starved predator preys on the frail, so
    // when no soft target is in reach the goal simply has no victim (it fails).
    internal static NPCState? NearestPreyVictim(NPCState npc, WorldState world)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return null;
        }

        NPCState? best = null;
        var bestScore = float.MaxValue;
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id == npc.Id || other.Health <= 0f ||
                other.CurrentJunction is not { } otherJunction)
            {
                continue;
            }

            if (!from.Equals(otherJunction) &&
                !Connectivity.Reachable(world, from, otherJunction, npc.Body.CanJump))
            {
                continue;
            }

            var vulnerability = other.Health -
                (other.Mind.CurrentGoal == GoalType.Sleep ? 0.25f : 0f) +
                HexSpatialMath.HexDistance(npc.Tile, other.Tile) * 0.01f;
            if (vulnerability < bestScore)
            {
                bestScore = vulnerability;
                best = other;
            }
        }

        return best;
    }

    // Spec 29E helpers.
    internal static bool HasReachableWithTag(NPCState npc, WorldState world, string tag)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains(tag))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasReachableDefinition(NPCState npc, WorldState world, string definitionId)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                obj.DefinitionId == definitionId)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasReachableDefinitionWorthCarrying(
        NPCState npc, WorldState world, string definitionId)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                obj.DefinitionId == definitionId &&
                InventoryMath.CanMakeRoomFor(world, npc, obj.DefinitionId))
            {
                return true;
            }
        }

        return false;
    }

    // §47 comfort: like HasReachableWithTag but counts — used for the
    // bed-per-girl deficit. Occupancy is ignored on purpose (a bed someone
    // sleeps in right now still exists as furniture).
    internal static int CountReachableWithTag(NPCState npc, WorldState world, string tag)
    {
        var count = 0;
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains(tag))
            {
                count++;
            }
        }

        return count;
    }

    // Spec 40.15: a KNOWN object with this tag that's reachable overland —
    // used for far, out-of-perception goals (the coastal raft) that NPCs
    // remember from the start (SeedHomeKnowledge) even when they can't see it.
    internal static bool KnowsReachableWithTag(NPCState npc, WorldState world, string tag)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return false;
        }

        foreach (var known in npc.Memory.KnownObjects.Values)
        {
            if (known.Junction is { } j &&
                world.Content.ObjectDefinitions.TryGetValue(known.DefinitionId, out var def) &&
                def.Tags.Contains(tag) &&
                (Connectivity.Reachable(world, from, j) || Connectivity.ReachableBeside(world, from, j)))
            {
                return true;
            }
        }

        return false;
    }

    // §gear: any-of capability check over the inventory (typed).
    internal static bool HasAnyCapability(
        NPCState npc, System.Collections.Generic.List<Content.GearCapability> capabilities)
    {
        foreach (var capability in capabilities)
        {
            if (Content.GearCatalog.HasCapability(npc.Inventory.Items, capability))
            {
                return true;
            }
        }

        return false;
    }

    // §gear-data: can the npc perform this object's interaction per its
    // DECLARED capabilities? Undeclared (legacy) content falls back to the
    // caller's own check — decision and execution stay in agreement.
    internal static bool CanPerformDeclared(
        WorldState world, NPCState npc, string definitionId, InteractionType type, bool legacyOk)
    {
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def))
        {
            foreach (var interaction in def.Interactions)
            {
                if (interaction.Type == type)
                {
                    return interaction.RequiredCapabilities.Count == 0
                        ? legacyOk
                        : HasAnyCapability(npc, interaction.RequiredCapabilities);
                }
            }
        }

        return legacyOk;
    }

    private static bool HasMissingToolReachable(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable || !ObjectUsableBy(obj, npc.Id))
            {
                continue;
            }

            if (!npc.Inventory.Items.Contains(obj.DefinitionId) &&
                InventoryMath.CanMakeRoomFor(world, npc, obj.DefinitionId) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains("Tool") &&
                Content.GearCatalog.AddsValueOver(
                    npc.Inventory.Items, obj.DefinitionId, npc.Body.IntactHands))
            {
                return true;
            }

            // Spec §52: a tool stashed in a dropped garment's pockets counts
            // as reachable too — GatherTools rifles the pockets on arrival.
            if (world.Entities.Objects.TryGetValue(obj.Id, out var container) &&
                container.Contents.Count > 0 &&
                InventoryMath.StashHoldsWantedTool(world, npc, container))
            {
                return true;
            }
        }

        return false;
    }

    internal static (bool Seen, float Fuel, WorldObjectState Fire) FindCampfire(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            // §54.14 (r2): hand the live object back too — spit/ring stage
            // checks and hanging-meat counts read it directly.
            world.Entities.Objects.TryGetValue(obj.Id, out var worldObject);
            return (true, worldObject?.ResourceAmount ?? 0f, worldObject);
        }

        return (false, 0f, null);
    }

    // Spec §52: the nearest reachable, unfinished furniture build-site the NPC
    // can see (live object, so its Contents/Bill are readable).
    internal static WorldObjectState FindBuildSite(NPCState npc, WorldState world)
    {
        // Spec §54: the hearth build-site wins over any other site — it only
        // exists during cold start (until the campfire is raised), and it must
        // be built first (fire gates warmth, cooking and all crafting).
        WorldObjectState firstSite = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Entities.Objects.TryGetValue(obj.Id, out var site) ||
                !BuildSiteMath.IsSite(site))
            {
                continue;
            }

            // §54.14: only the BARE hearth site (no fire raised yet) gets the
            // cold-start priority. A live campfire mid-upgrade (stone ring /
            // spit outstanding) queues like any other furniture site.
            if (site.BuildProduct == "campfire.spot" && site.DefinitionId == "build.site")
            {
                return site;
            }

            firstSite ??= site;
        }

        return firstSite;
    }

    // Does the NPC carry at least one material this site still needs?
    // §54.12: a perceived object she could actually sit ON (stump/chair/bed).
    private static bool HasPerceivedSeat(NPCState npc)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable && !perceived.IsOccupied &&
                perceived.AvailableInteractions.Contains(InteractionType.Sit))
            {
                return true;
            }
        }

        return false;
    }

    // §54.12: any ledge junction within the sit-plan search radius. Ledges only
    // change with terrain, so the junction list is cached per TopologyVersion —
    // the per-decision cost is a distance sweep over the (short) ledge list.
    private static int _ledgeCacheTopology = -1;
    private static readonly System.Collections.Generic.List<Junction> _ledgeCache = new();

    private static bool AnyLedgeNear(WorldState world, NPCState npc, float radius)
    {
        if (_ledgeCacheTopology != world.TopologyVersion)
        {
            _ledgeCacheTopology = world.TopologyVersion;
            _ledgeCache.Clear();
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (PlanningSystem.IsLedge(world, junction))
                {
                    _ledgeCache.Add(junction);
                }
            }
        }

        foreach (var junction in _ledgeCache)
        {
            if (!junction.Blocked &&
                HexSpatialMath.Distance(junction.WorldPosition, npc.Position) < radius)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool CarriesSiteMaterial(NPCState npc, WorldObjectState site)
    {
        foreach (var mat in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.Needs(site, mat) && npc.Inventory.Items.Contains(mat))
            {
                return true;
            }
        }

        return false;
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

    private static bool HasReachableBathTile(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return false;
        }

        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !HygieneMath.IsShoreTile(world, junction.Tiles[0]))
            {
                continue;
            }

            if (Connectivity.Reachable(world, from, junction.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static float DirtyGarmentWashNeed(WorldState world, NPCState npc)
    {
        var need = 0f;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Dirtiness <= need ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Layer is null ||
                !HygieneMath.IsBathingTile(world, obj.Tile))
            {
                continue;
            }

            need = obj.Dirtiness;
        }

        return need;
    }

    private static bool HasInteraction(NPCState npc, InteractionType interactionType)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                ObjectUsableBy(obj, npc.Id) &&
                obj.AvailableInteractions.Contains(interactionType))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 24.3: occupied objects are unavailable — unless occupied by this
    // NPC itself (an NPC mid-interaction must not lose its own target).
    internal static bool ObjectUsableBy(PerceivedObject obj, EntityId self)
    {
        return !obj.IsOccupied ||
            (obj.OccupiedBy.HasValue && obj.OccupiedBy.Value == self);
    }

    // Spec 31A.5A: warmest worn item that is safe to take off — armor stays
    // on while any danger memory is fresh.
    internal static string? FindRemovableItem(NPCState npc, WorldState world)
    {
        string? best = null;
        var bestWarmth = -1f;
        foreach (var itemId in npc.WornItems)
        {
            var (warmth, armor) = EquipmentMath.ItemValues(world, itemId);
            if (armor > 0f && npc.Memory.Dangers.Count > 0)
            {
                continue; // protection beats comfort under threat
            }

            // Spec 42: heat never strips the girls naked — underwear stays on
            // (it barely warms anyway), only real layers come off.
            if (world.Content.ObjectDefinitions.TryGetValue(itemId, out var def) &&
                def.Layer == WearLayer.Underwear)
            {
                continue;
            }

            if (warmth > bestWarmth)
            {
                best = itemId;
                bestWarmth = warmth;
            }
        }

        return best;
    }

    // Spec 29C.4A: does the NPC know a reachable Dress item with armor?
    internal static bool KnowsReachableArmor(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                CandidateArmor(world, obj) > npc.EquippedArmor)
            {
                return true;
            }
        }

        return false;
    }

    internal static float CandidateArmor(WorldState world, PerceivedObject obj)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition))
        {
            return 0f;
        }

        var best = 0f;
        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == InteractionType.Dress &&
                interaction.Effects.ArmorDelta > best)
            {
                best = interaction.Effects.ArmorDelta;
            }
        }

        return best;
    }

    // Spec 27.18A foraging: food can also be sought at a known producer
    // (an apple tree), even when no food item itself is known.
    internal static bool KnowsReachableProducer(NPCState npc, WorldState world)
    {
        var hasBlade = HasCoconutBlade(npc);
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce != null &&
                (definition.Produce.ProducedDefinitionId != "food.coconut" || hasBlade))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class PlanningSystem : ISimulationSystem
{
    public string Name => nameof(PlanningSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status == PlanStatus.Active && npc.Plan.Goal == npc.Mind.CurrentGoal)
            {
                Trace.Emit(world, npc.Id, "PlanSkipped",
                    $"ActivePlan already matches Goal={npc.Mind.CurrentGoal} Step={npc.Plan.CurrentStepIndex}/{npc.Plan.Steps.Count}");
                continue;
            }

            var prevStatus = npc.Plan.Status;
            var prevGoal = npc.Plan.Goal;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetItemDefinitionId = null;
            npc.Plan.TargetAgentId = null;
            npc.Plan.Goal = npc.Mind.CurrentGoal;

            Trace.Emit(world, npc.Id, "PlanStarted",
                $"Goal={npc.Mind.CurrentGoal} PrevGoal={prevGoal} PrevStatus={prevStatus}");

            if (npc.Mind.CurrentGoal == GoalType.Eat)
            {
                // Eating happens in place from inventory (spec 29B.3):
                // no target object, no junction reservation.
                var foodDefinitionId = npc.Inventory.FindFirstFood(world.Content);
                if (foodDefinitionId is not null)
                {
                    npc.Plan.TargetItemDefinitionId = foodDefinitionId;
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.ConsumeInventoryItem,
                        Interaction = InteractionType.Eat
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    Trace.Emit(world, npc.Id, "PlanBuilt",
                        $"Goal=Eat Item={foodDefinitionId} Steps=[ConsumeInventoryItem]");
                    continue;
                }

                if (BuildCoconutEatPlan(world, npc))
                {
                    continue;
                }

                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "PlanFailed",
                    "Goal=Eat but no food in inventory");
                continue;
            }

            // §gear-craft: the recipe declares NO station — craft right where
            // she stands: a one-step in-place plan, no walk, no target object.
            if (Content.RecipeCatalog.IsItemOutputGoal(npc.Mind.CurrentGoal) &&
                string.IsNullOrEmpty(Content.RecipeCatalog.StationOf(npc.Mind.CurrentGoal)))
            {
                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PlanBuilt",
                    $"Goal={npc.Mind.CurrentGoal} Steps=[CraftInPlace] (no station)");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Drink)
            {
                if (DecisionSystem.HasBottleWater(npc))
                {
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.DrinkBottle
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    Trace.Emit(world, npc.Id, "PlanBuilt",
                        $"Goal=Drink Item=tool.bottle Steps=[DrinkBottle] Charges={npc.BottleCharges}");
                    continue;
                }

                var drinkDefinitionId = npc.Inventory.FindFirstDrink(world.Content);
                if (drinkDefinitionId is not null)
                {
                    npc.Plan.TargetItemDefinitionId = drinkDefinitionId;
                    npc.Plan.Steps.Add(new PlanStep
                    {
                        Type = PlanStepType.ConsumeInventoryItem,
                        Interaction = InteractionType.Drink
                    });
                    npc.Plan.CurrentStepIndex = 0;
                    npc.Plan.Status = PlanStatus.Active;
                    Trace.Emit(world, npc.Id, "PlanBuilt",
                        $"Goal=Drink Item={drinkDefinitionId} Steps=[ConsumeInventoryItem]");
                    continue;
                }

                if (BuildCoconutDrinkPlan(world, npc))
                {
                    continue;
                }

                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "PlanFailed",
                    "Goal=Drink but nothing drinkable in inventory");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Socialize)
            {
                BuildTalkPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Aid)
            {
                BuildAidPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Defend)
            {
                BuildDefendPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Explore)
            {
                BuildExplorePlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.CoolOff)
            {
                BuildCoolOffPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Bathe)
            {
                BuildBathePlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.WashClothes)
            {
                BuildWashClothesPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Hunt)
            {
                // Spec 29F.2: a move-only chase to the rabbit's junction;
                // the rabbit flees, re-planning produces a genuine pursuit.
                var rabbit = DecisionSystem.NearestVisibleRabbit(npc, world);
                if (rabbit is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Hunt);
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Hunt NoVisibleRabbit");
                    continue;
                }

                npc.Plan.TargetJunctionId = rabbit.Junction;
                npc.Plan.TargetTile = rabbit.Tile;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = rabbit.Junction
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "HuntPlanned",
                    $"Rabbit={rabbit.Id} Tile={rabbit.Tile.Q},{rabbit.Tile.R}");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Prey)
            {
                // §56: a move-only stalk to the victim's junction — the strike is
                // resolved by PredationSystem once adjacent. Like the hunt, the
                // completion→rebuild cycle produces a genuine pursuit if the
                // victim moves. The kill drops a corpse the existing §54 Butcher
                // goal then processes into meat to eat.
                var victim = DecisionSystem.NearestPreyVictim(npc, world);
                if (victim?.CurrentJunction is not { } victimJunction)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Prey);
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Prey NoReachableVictim");
                    continue;
                }

                npc.Plan.TargetJunctionId = victimJunction;
                npc.Plan.TargetTile = victim.Tile;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = victimJunction
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PreyPlanned",
                    $"Victim={victim.Id.Value} Tile={victim.Tile.Q},{victim.Tile.R}");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Undress)
            {
                var removable = DecisionSystem.FindRemovableItem(npc, world);
                if (removable is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, GoalType.Undress);
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Undress NothingRemovable");
                    continue;
                }

                npc.Plan.TargetItemDefinitionId = removable;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.UndressItem,
                    Interaction = InteractionType.Undress
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PlanBuilt",
                    $"Goal=Undress Item={removable} Steps=[UndressItem]");
                continue;
            }

            // Spec 29G: no chair/bed among candidates -> rest on the land.
            if (npc.Mind.CurrentGoal == GoalType.Sit && !HasFurnitureCandidate(world, npc, InteractionType.Sit))
            {
                BuildGroundSitPlan(world, npc);
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Sleep && !HasFurnitureCandidate(world, npc, InteractionType.Sleep))
            {
                BuildGroundSleepPlan(world, npc);
                continue;
            }

            // Spec 35.5: no free rack in sight -> stand by the lit fire
            // instead (x4 drying covers the whole outfit).
            if (npc.Mind.CurrentGoal == GoalType.DryClothes && !HasFreeRackCandidate(world, npc))
            {
                BuildFireDryPlan(world, npc);
                continue;
            }

            var interactionType = GoalToInteraction(npc.Mind.CurrentGoal);
            if (interactionType is null)
            {
                npc.Plan.Status = PlanStatus.Completed;
                Trace.Emit(world, npc.Id, "PlanNoInteraction",
                    $"Goal={npc.Mind.CurrentGoal} has no mapped interaction (Idle?)");
                continue;
            }

            // Spec 29C.4A: a threatened underarmored NPC dressing up prefers
            // the best armor over the nearest garment.
            var preferArmor = interactionType == InteractionType.Dress &&
                npc.Memory.Dangers.Count > 0 && npc.EquippedArmor < 0.3f;
            // §55: boiling is retired — GetWater now just fetches the nearest
            // coconut to crack open (no boiled-vs-raw source preference).
            var preferBoiled = false;

            PerceivedObject? selected = null;
            var selectedArmor = 0f;
            var selectedBoiled = false;
            var candidateCount = 0;
            foreach (var perceived in npc.Perception.Objects)
            {
                if (!perceived.IsReachable ||
                    !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                    !perceived.AvailableInteractions.Contains(interactionType.Value))
                {
                    continue;
                }

                // Spec 29E.4: per-goal target filtering by tags.
                if (!IsValidTargetFor(world, npc, npc.Mind.CurrentGoal, perceived))
                {
                    continue;
                }

                if (interactionType == InteractionType.PickUp &&
                    !InventoryMath.CanMakeRoomFor(world, npc, perceived.DefinitionId))
                {
                    Trace.Emit(world, npc.Id, "PlanCandidateSkipped",
                        $"Obj={perceived.Id.Value} Def={perceived.DefinitionId} NoRoomForImportance");
                    continue;
                }

                // Spec 29C.4A food avoidance: don't shop for food where the
                // dogs are — unless starving (desperation overrides caution).
                if (interactionType == InteractionType.PickUp && !npc.Mind.IsStarving &&
                    IsNearDanger(npc, perceived.Tile, 2))
                {
                    continue;
                }

                candidateCount++;
                Trace.Emit(world, npc.Id, "PlanCandidate",
                    $"Obj={perceived.Id.Value} Tile={perceived.Tile.Q},{perceived.Tile.R} " +
                    $"Dist={perceived.Distance:F2} Occupied={perceived.IsOccupied}");

                if (preferArmor)
                {
                    var armor = DecisionSystem.CandidateArmor(world, perceived);
                    if (selected is null || armor > selectedArmor + 0.01f ||
                        (System.Math.Abs(armor - selectedArmor) <= 0.01f &&
                         perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedArmor = armor;
                    }
                }
                else if (preferBoiled)
                {
                    var isBoiled = world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var d) &&
                        d.Tags.Contains("Campfire");
                    if (selected is null ||
                        (isBoiled && !selectedBoiled) ||
                        (isBoiled == selectedBoiled && perceived.Distance < selected.Distance))
                    {
                        selected = perceived;
                        selectedBoiled = isBoiled;
                    }
                }
                else if (selected is null || perceived.Distance < selected.Distance)
                {
                    selected = perceived;
                }
            }

            if (selected is null)
            {
                if (npc.Mind.CurrentGoal == GoalType.GetFood)
                {
                    BuildForagePlan(world, npc);
                    continue;
                }

                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                Trace.Emit(world, npc.Id, "PlanFailed",
                    $"Goal={npc.Mind.CurrentGoal} Interaction={interactionType} " +
                    $"Candidates={candidateCount} NoSuitableObject");
                continue;
            }

            // Resolve the target junction: from the live object when it exists,
            // from memory for remembered-but-unseen targets (the plan will walk
            // there and only then discover whether the belief was stale).
            JunctionId? targetJunction;
            if (world.Entities.Objects.TryGetValue(selected.Id, out var worldObject))
            {
                targetJunction = worldObject.Junctions.Count > 0 ? worldObject.Junctions[0] : null;
            }
            else if (selected.FromMemory &&
                     npc.Memory.KnownObjects.TryGetValue(selected.Id, out var rememberedTarget))
            {
                targetJunction = rememberedTarget.Junction;
            }
            else
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                Trace.Emit(world, npc.Id, "PlanFailed",
                    $"Goal={npc.Mind.CurrentGoal} Obj={selected.Id.Value} vanished and not remembered");
                continue;
            }

            Trace.Emit(world, npc.Id, "PlanTargetSelected",
                $"Obj={selected.Id.Value} Def={selected.DefinitionId} " +
                $"Tile={selected.Tile.Q},{selected.Tile.R} Dist={selected.Distance:F2} " +
                $"FromMemory={selected.FromMemory} FromCandidates={candidateCount}");

            // Spec 31C.1: obstacle anchors are blocked — stand beside the
            // trunk, not inside it. Gathering a ground item (PickUp) also stands
            // BESIDE now: she walks up to the nearest cell next to the item and
            // collects from there instead of stepping onto it.
            var gatherBeside = interactionType == InteractionType.PickUp;
            var anchorIsWater = targetJunction is { } wetId &&
                SpatialQueries.IsAllWaterJunction(world, wetId);
            if (targetJunction is { } anchorId &&
                (anchorIsWater || gatherBeside ||
                 (world.Junctions.Items.TryGetValue(anchorId, out var anchorJunction) &&
                  anchorJunction.Blocked)))
            {
                // Reserve in-loop: the first free neighbor is the same for
                // every claimant — without reserving here two sleepers fight
                // over one spot forever (ReservationFailed loop). Nearest-to-NPC
                // first, so "beside" is the CLOSEST reachable cell to the item.
                JunctionId? beside = null;
                SpatialQueries.CollectStandableAround(world, anchorId, _rimScratch);
                _rimScratch.Sort((a, b) =>
                {
                    var da = world.Junctions.Items.TryGetValue(a, out var ja)
                        ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
                    var db = world.Junctions.Items.TryGetValue(b, out var jb)
                        ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
                    return da.CompareTo(db);
                });
                foreach (var rim in _rimScratch)
                {
                    if (SpatialQueries.IsJunctionFree(world, rim) &&
                        SpatialMutations.TryReserveJunction(world, rim, npc.Id, world.Tick, 48))
                    {
                        beside = rim;
                        break;
                    }
                }

                if (beside is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                    Trace.Emit(world, npc.Id, "PlanFailed",
                        $"Goal={npc.Mind.CurrentGoal} Obj={selected.Id.Value} no free junction beside obstacle");
                    continue;
                }

                targetJunction = beside;
            }

            npc.Plan.TargetObjectId = selected.Id;
            npc.Plan.TargetTile = selected.Tile;
            npc.Plan.TargetJunctionId = targetJunction;

            if (targetJunction is { } jId &&
                !SpatialMutations.TryReserveJunction(world, jId, npc.Id, world.Tick, 48))
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, npc.Mind.CurrentGoal);
                Trace.Emit(world, npc.Id, "ReservationFailed",
                    $"Junction={jId.Value} Already reserved or occupied");
                continue;
            }

            if (targetJunction is { } reservedJId)
            {
                Trace.Emit(world, npc.Id, "JunctionReserved",
                    $"Junction={reservedJId.Value} Duration=48ticks Until={world.Tick + 48}");
            }

            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = targetJunction,
                TargetObject = selected.Id
            });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetObject = selected.Id,
                TargetJunction = targetJunction,
                Interaction = interactionType.Value
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            Trace.Emit(world, npc.Id, "PlanBuilt",
                $"Goal={npc.Plan.Goal} Target={selected.DefinitionId} " +
                $"Tile={selected.Tile.Q},{selected.Tile.R} Junction={Trace.FormatJunction(targetJunction)} " +
                $"FromMemory={selected.FromMemory} Steps=[MoveToJunction,Interact]");
        }
    }

    private static bool BuildCoconutDrinkPlan(WorldState world, NPCState npc)
    {
        if (TryFindInventoryItem(npc, "food.coconut_pierced", requireWater: true, out _))
        {
            return false;
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut", out var carriedWhole))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Drink, carriedWhole,
                InteractionType.Process, InteractionType.PickUp);
        }

        if (TryFindCoconutObject(npc, world, "food.coconut_pierced", requireWater: true, out var pierced))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Drink, pierced, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut", requireWater: false, out var whole))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Drink, whole,
                InteractionType.Process, InteractionType.PickUp);
        }

        return false;
    }

    private static bool BuildCoconutEatPlan(WorldState world, NPCState npc)
    {
        if (TryFindInventoryItem(npc, "food.coconut_open", out _))
        {
            return false;
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut_pierced", out var carriedPierced))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedPierced,
                InteractionType.Process, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut", out var carriedWhole))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedWhole,
                InteractionType.Process, InteractionType.Process, InteractionType.PickUp);
        }

        if (TryFindCoconutObject(npc, world, "food.coconut_open", requireWater: false, out var open))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, open, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut_pierced", requireWater: false, out var pierced))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, pierced,
                InteractionType.Process, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut", requireWater: false, out var whole))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, whole,
                InteractionType.Process, InteractionType.Process, InteractionType.PickUp);
        }

        return false;
    }

    private static bool BuildCoconutInventoryPlan(
        WorldState world,
        NPCState npc,
        GoalType goal,
        ItemInstance item,
        params InteractionType[] interactions)
    {
        if (npc.CurrentJunction is not { } current)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, goal);
            Trace.Emit(world, npc.Id, "PlanFailed", $"Goal={goal} Coconut inventory plan has no current junction");
            return true;
        }

        npc.Plan.TargetItemDefinitionId = item.DefinitionId;
        npc.Plan.TargetJunctionId = current;
        npc.Plan.TargetTile = npc.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.DropInventoryItem,
            TargetJunction = current
        });
        foreach (var interaction in interactions)
        {
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetJunction = current,
                Interaction = interaction
            });
        }

        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal={goal} Item={item.DefinitionId} Steps=[DropInventoryItem,{FormatInteractions(interactions)}]");
        return true;
    }

    private static bool BuildCoconutWorldPlan(
        WorldState world,
        NPCState npc,
        GoalType goal,
        PerceivedObject target,
        params InteractionType[] interactions)
    {
        if (!world.Entities.Objects.TryGetValue(target.Id, out var worldObject) ||
            worldObject.Junctions.Count == 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, goal);
            Trace.Emit(world, npc.Id, "PlanFailed", $"Goal={goal} Coconut target vanished or has no junction");
            return true;
        }

        var anchorJunction = worldObject.Junctions[0];
        if (!TryReserveBesideJunction(world, npc, anchorJunction, 48, out var targetJunction))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, goal);
            Trace.Emit(world, npc.Id, "ReservationFailed",
                $"Coconut Anchor={anchorJunction.Value} has no free junction beside it");
            return true;
        }

        if (npc.CurrentJunction is not { } current || !current.Equals(targetJunction))
        {
            if (!SpatialMutations.TryReserveJunction(world, targetJunction, npc.Id, world.Tick, 48))
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, goal);
                Trace.Emit(world, npc.Id, "ReservationFailed",
                    $"Coconut beside Junction={targetJunction.Value} already reserved or occupied");
                return true;
            }
        }

        npc.Plan.TargetObjectId = target.Id;
        npc.Plan.TargetTile = target.Tile;
        npc.Plan.TargetJunctionId = targetJunction;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetJunction,
            TargetObject = target.Id
        });
        foreach (var interaction in interactions)
        {
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetObject = target.Id,
                TargetJunction = targetJunction,
                Interaction = interaction
            });
        }

        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal={goal} Target={target.DefinitionId} Tile={target.Tile.Q},{target.Tile.R} " +
            $"Anchor={anchorJunction.Value} Junction={targetJunction.Value} " +
            $"Steps=[MoveToJunction,{FormatInteractions(interactions)}]");
        return true;
    }

    private static bool TryReserveBesideJunction(
        WorldState world,
        NPCState npc,
        JunctionId anchorId,
        int durationTicks,
        out JunctionId beside)
    {
        SpatialQueries.CollectStandableAround(world, anchorId, _rimScratch);
        if (npc.CurrentJunction is { } current && _rimScratch.Contains(current))
        {
            beside = current;
            return true;
        }

        _rimScratch.Sort((a, b) =>
        {
            var da = world.Junctions.Items.TryGetValue(a, out var ja)
                ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
            var db = world.Junctions.Items.TryGetValue(b, out var jb)
                ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
            return da.CompareTo(db);
        });

        foreach (var rim in _rimScratch)
        {
            if (SpatialQueries.IsJunctionFree(world, rim) &&
                SpatialMutations.TryReserveJunction(world, rim, npc.Id, world.Tick, durationTicks))
            {
                beside = rim;
                return true;
            }
        }

        beside = default;
        return false;
    }

    private static string FormatInteractions(InteractionType[] interactions)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < interactions.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(interactions[i]);
        }

        return text.ToString();
    }

    private static bool TryFindInventoryItem(NPCState npc, string definitionId, out ItemInstance item)
    {
        return TryFindInventoryItem(npc, definitionId, requireWater: false, out item);
    }

    private static bool TryFindInventoryItem(
        NPCState npc,
        string definitionId,
        bool requireWater,
        out ItemInstance item)
    {
        foreach (var carried in npc.Inventory.Items)
        {
            if (carried.DefinitionId == definitionId &&
                (!requireWater || carried.ResourceAmount > 0f))
            {
                item = carried;
                return true;
            }
        }

        item = null;
        return false;
    }

    private static bool TryFindCoconutObject(
        NPCState npc,
        WorldState world,
        string definitionId,
        bool requireWater,
        out PerceivedObject result)
    {
        PerceivedObject? best = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                obj.DefinitionId != definitionId ||
                !DecisionSystem.ObjectUsableBy(obj, npc.Id) ||
                !world.Entities.Objects.TryGetValue(obj.Id, out var worldObject) ||
                (requireWater && worldObject.ResourceAmount <= 0f))
            {
                continue;
            }

            if (best is null || obj.Distance < best.Distance)
            {
                best = obj;
            }
        }

        result = best;
        return best is not null;
    }

    // ONE truth — DecisionSystem owns the rule (§50-prone: piercing a coconut
    // is light hand-work, allowed lying). This private duplicate silently kept
    // the old stand-up-only body and starved one-legged Marta at day 28.
    private static bool HasCoconutBlade(NPCState npc) =>
        DecisionSystem.HasCoconutBlade(npc);

    // Spec 29C.5: wander to a seeded-random unblocked junction 3-8 tiles away.
    // Discoveries along the way land in spatial memory.
    private readonly System.Collections.Generic.List<Junction> _exploreCandidates = new();

    private void BuildExplorePlan(WorldState world, NPCState npc)
    {
        _exploreCandidates.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked)
            {
                continue;
            }

            var tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : npc.Tile;
            var distance = HexSpatialMath.HexDistance(npc.Tile, tile);
            if (distance is < 3 or > 8)
            {
                continue;
            }

            // Spec 29C.4A: avoid places where we were recently attacked.
            var dangerous = false;
            foreach (var danger in npc.Memory.Dangers)
            {
                if (HexSpatialMath.HexDistance(tile, danger.Tile) <= 3)
                {
                    dangerous = true;
                    break;
                }
            }

            if (!dangerous)
            {
                _exploreCandidates.Add(junction);
            }
        }

        if (_exploreCandidates.Count == 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Explore);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Explore NoCandidateJunctions");
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 991) * _exploreCandidates.Count);
        pick = System.Math.Min(pick, _exploreCandidates.Count - 1);
        var destination = _exploreCandidates[pick];

        // Reachability check: destination must connect to where we stand.
        if (npc.CurrentJunction is not { } startJunction ||
            !Connectivity.Reachable(world, startJunction, destination.Id))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Explore);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Explore Destination={destination.Id.Value} unreachable");
            return;
        }

        npc.Plan.TargetJunctionId = destination.Id;
        npc.Plan.TargetTile = destination.Tiles.Count > 0 ? destination.Tiles[0] : null;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "ExplorePlanned",
            $"To Junction={destination.Id.Value} " +
            $"Tile={Trace.FormatTile(npc.Plan.TargetTile)} Steps=[MoveToJunction]");
    }

    // Spec 35.4: is standing on this tile genuinely cooling? Either the cast-
    // shadow map shades it, or it's a walkable water tile (the shallows). This is
    // the tile TemperatureSystem reads (npc.Tile == junction.Tiles[0] on arrival),
    // so a plan that lands her here actually sheds heat — unlike the old plan that
    // parked her on an approach *neighbour* of the shade object and never cooled.
    private static bool IsCoolingTile(WorldState world, Common.TileCoord tile)
    {
        if (TemperatureSystem.IsShaded(world, tile))
        {
            return true;
        }

        return world.Tiles.Items.TryGetValue(tile, out var t) &&
            t.Flags.HasFlag(TileFlags.Water);
    }

    // Spec 35.4: walk to the nearest genuinely cool tile (real shade or the
    // shallows) and DWELL there until cooled. Mirrors BuildGroundSitPlan — a
    // move + an in-place GroundCool step — rather than the old move-only trip
    // that completed on arrival and re-won None→CoolOff every tick.
    private void BuildCoolOffPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.CoolOff);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=CoolOff NoJunction");
            return;
        }

        // Nearest reachable, free junction whose standing tile actually cools.
        // §54.12: never dwell ON a build-site (the half-built fireside bed).
        var siteJunctions = CollectBuildSiteJunctions(world);
        Junction best = null;
        var bestDist = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                siteJunctions.Contains(junction.Id) ||
                !IsCoolingTile(world, junction.Tiles[0]))
            {
                continue;
            }

            var d = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (d < bestDist && d < HexSpatialMath.HexRadius * 12f &&
                Connectivity.Reachable(world, from, junction.Id))
            {
                bestDist = d;
                best = junction;
            }
        }

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.CoolOff);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=CoolOff NoCoolTile");
            return;
        }

        npc.Plan.TargetJunctionId = best.Id;
        npc.Plan.TargetTile = best.Tiles[0];
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = best.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundCool, TargetJunction = best.Id });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CoolRearmCount = 0;
        Trace.Emit(world, npc.Id, "CoolOffPlanned",
            $"Junction={best.Id.Value} Tile={Trace.FormatTile(npc.Plan.TargetTile)} " +
            $"Shade={TemperatureSystem.IsShaded(world, best.Tiles[0])}");
    }

    private void BuildBathePlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            return;
        }

        Junction best = null;
        var bestDist = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !HygieneMath.IsShoreTile(world, junction.Tiles[0]) ||
                !Connectivity.Reachable(world, from, junction.Id))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDist && distance < HexSpatialMath.HexRadius * 12f)
            {
                bestDist = distance;
                best = junction;
            }
        }

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Bathe NoWaterTile");
            return;
        }

        npc.Plan.TargetJunctionId = best.Id;
        npc.Plan.TargetTile = best.Tiles[0];
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = best.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.PrepareBathe, TargetJunction = best.Id });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CoolRearmCount = 0;
        Trace.Emit(world, npc.Id, "BathePlanned",
            $"Junction={best.Id.Value} BodyHygiene={npc.Needs.Hygiene:F2} " +
            $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
    }

    private void BuildWashClothesPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.WashClothes);
            return;
        }

        WorldObjectState best = null;
        JunctionId bestTarget = default;
        TileCoord bestStandTile = default;
        var bestDistance = float.MaxValue;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Dirtiness < SimBalance.WashClothesNeedThreshold ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Layer is null || !HygieneMath.IsBathingTile(world, obj.Tile) ||
                obj.Junctions.Count == 0)
            {
                continue;
            }

            var objectPosition = world.Junctions.Items.TryGetValue(obj.Junctions[0], out var objectJunction)
                ? objectJunction.WorldPosition
                : HexSpatialMath.TileToWorld(obj.Tile);
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (!TryGetEdgeSeatGeometry(world, junction, waterOnly: true,
                        out var standTile, out _) ||
                    !JunctionAvailableFor(world, junction.Id, npc.Id) ||
                    !Connectivity.Reachable(world, from, junction.Id))
                {
                    continue;
                }

                var garmentDistance = HexSpatialMath.Distance(junction.WorldPosition, objectPosition);
                if (garmentDistance > HexSpatialMath.HexRadius * 2.2f)
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(npc.Position, junction.WorldPosition) +
                               garmentDistance * 0.5f;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = obj;
                    bestTarget = junction.Id;
                    bestStandTile = standTile;
                }
            }
        }

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, bestTarget, npc.Id, world.Tick,
                SimBalance.WashClothesDurationTicks + 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.WashClothes);
            return;
        }

        npc.Plan.TargetObjectId = best.Id;
        npc.Plan.TargetJunctionId = bestTarget;
        npc.Plan.TargetTile = bestStandTile;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = bestTarget });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.WashClothes,
            TargetJunction = bestTarget,
            TargetObject = best.Id,
            Interaction = InteractionType.WashClothes
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "WashClothesPlanned",
            $"Object={best.Id.Value} Def={best.DefinitionId} Dirt={best.Dirtiness:F2} " +
            $"Edge={bestTarget.Value} StandTile={bestStandTile.Q},{bestStandTile.R}");
    }

    private static bool JunctionAvailableFor(WorldState world, JunctionId junction, EntityId npc)
    {
        return !world.Occupancy.JunctionOwner.TryGetValue(junction, out var owner) ||
               owner is null || owner.Value == npc;
    }

    // Spec 29G: does perception offer real furniture for this interaction?
    private static bool HasFurnitureCandidate(WorldState world, NPCState npc, InteractionType interaction)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable && !perceived.IsOccupied &&
                perceived.AvailableInteractions.Contains(interaction))
            {
                return true;
            }
        }

        return false;
    }

    // §54.12: junctions occupied by a build-site (the half-built bed by the
    // fire). Never chosen as a ground-sit / cool-off spot — she'd plop down
    // ON the growing mat she just stocked.
    private static readonly System.Collections.Generic.HashSet<JunctionId> _siteJunctionsScratch = new();

    private static System.Collections.Generic.HashSet<JunctionId> CollectBuildSiteJunctions(WorldState world)
    {
        _siteJunctionsScratch.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!BuildSiteMath.IsSite(obj))
            {
                continue;
            }

            foreach (var junction in obj.Junctions)
            {
                _siteJunctionsScratch.Add(junction);
            }
        }

        return _siteJunctionsScratch;
    }

    // Spec 29G: sit on the land — a ledge with the legs over the edge when
    // one is close, any free junction otherwise.
    private void BuildGroundSitPlan(WorldState world, NPCState npc)
    {
        JunctionId? spot = null;
        var siteJunctions = CollectBuildSiteJunctions(world);

        // Prefer a scenic ledge within ~4 tiles.
        if (npc.CurrentJunction is { } from)
        {
            var bestDist = float.MaxValue;
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (junction.Blocked || junction.Tiles.Count < 2 ||
                    !TryGetEdgeSeatGeometry(world, junction, waterOnly: false, out _, out _) ||
                    !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                    siteJunctions.Contains(junction.Id))
                {
                    continue;
                }

                var d = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
                if (d < bestDist && d < HexSpatialMath.HexRadius * 8f &&
                    Connectivity.Reachable(world, from, junction.Id))
                {
                    bestDist = d;
                    spot = junction.Id;
                }
            }
        }

        // NO flat-ground fallback: sitting happens ONLY on a real seat — a
        // ledge (the hex edge working as a step, legs over the drop), a stump
        // or Sit-furniture (chair/bed, handled by the furniture path before
        // this). The old §29G "sit right where she stands" plopped her onto
        // flat land — and onto the half-built bed she'd just stocked (§54.12).
        if (spot is not { } sitSpot ||
            !SpatialMutations.TryReserveJunction(world, sitSpot, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Sit);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Sit NoLedgeOrSeat");
            return;
        }

        npc.Plan.TargetJunctionId = sitSpot;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = sitSpot });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundSit, TargetJunction = sitSpot });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "GroundSitPlanned",
            $"Junction={sitSpot.Value} Ledge={IsLedgeId(world, sitSpot)}");
    }

    // Spec 29G: lie at the center of a free hexagon — walkable, dry, no
    // objects, nobody else lying there. The spot is anchored to HOME (the
    // campfire), not to wherever the night caught the NPC: the first soak
    // with self-anchored sleep had the colony bedding down in dog country
    // and getting eaten (fights=215, 5 deaths).
    private void BuildGroundSleepPlan(WorldState world, NPCState npc)
    {
        var anchor = npc.Tile;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var objDef) &&
                objDef.Tags.Contains("Campfire"))
            {
                anchor = obj.Tile;
                break;
            }
        }

        JunctionId? spot = null;
        var bestDist = float.MaxValue;
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (!tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Water) ||
                tile.Junctions.Count == 0)
            {
                continue;
            }

            if (world.Caches.ObjectsByTile.TryGetValue(tile.Coord, out var objects) && objects.Count > 0)
            {
                continue;
            }

            var d = (float)HexSpatialMath.HexDistance(tile.Coord, anchor);
            if (d > 6f)
            {
                continue;
            }

            // A roof beats proximity: indoor sleepers are sanctuary-safe
            // (spec 29C.4A) — outdoor night camps got mauled by dogs.
            if (tile.Flags.HasFlag(TileFlags.Indoor))
            {
                d -= 100f;
            }

            // Spec §49 (Tier C): comfort nudge — a hot day pulls toward shade, a
            // cold night toward the fireside. SMALL vs the distance range (0..6)
            // and dwarfed by the indoor -100, so it only re-orders nearby spots,
            // never sends her into danger. (Fireside already correlates with
            // "near the anchor", so this mostly adds daytime shade-seeking.)
            if (Spec49.SmartSleepSpot)
            {
                var cold = world.Environment.GlobalTemperature < 16f ||
                    world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
                var hot = world.Environment.GlobalTemperature > 24f &&
                    world.Environment.Phase is DayPhase.Day or DayPhase.Morning;
                if (cold && TemperatureSystem.NearbyFireWarmth(world, tile.Coord, out _) > 0f)
                {
                    d -= Spec49.SleepSpotFireWeight;
                }
                if (hot && TemperatureSystem.IsShaded(world, tile.Coord))
                {
                    d -= Spec49.SleepSpotShadeWeight;
                }
            }

            if (d >= bestDist)
            {
                continue;
            }

            // center junction: nearest to the tile's world center
            var center = HexSpatialMath.TileToWorld(tile.Coord);
            JunctionId? centerJunction = null;
            var centerDist = float.MaxValue;
            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || junction.Blocked)
                {
                    continue;
                }

                var cd = HexSpatialMath.Distance(junction.WorldPosition, center);
                if (cd < centerDist)
                {
                    centerDist = cd;
                    centerJunction = junctionId;
                }
            }

            if (centerJunction is { } cj && SpatialQueries.IsJunctionFree(world, cj) &&
                npc.CurrentJunction is { } from2 && Connectivity.Reachable(world, from2, cj))
            {
                bestDist = d;
                spot = cj;
            }
        }

        if (spot is not { } lieSpot ||
            !SpatialMutations.TryReserveJunction(world, lieSpot, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Sleep);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Sleep NoGroundSpot");
            return;
        }

        npc.Plan.TargetJunctionId = lieSpot;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = lieSpot });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundSleep, TargetJunction = lieSpot });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "GroundSleepPlanned", $"Junction={lieSpot.Value}");
    }

    // Spec 29G: a junction on a boundary with >=1 level difference.
    internal static bool IsLedge(WorldState world, Junction junction)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var coord in junction.Tiles)
        {
            if (world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                min = System.Math.Min(min, tile.Elevation);
                max = System.Math.Max(max, tile.Elevation);
            }
        }

        return max - min >= 1;
    }

    internal static bool IsLedgeId(WorldState world, JunctionId id)
    {
        return world.Junctions.Items.TryGetValue(id, out var junction) && IsLedge(world, junction);
    }

    // Picks one stable point per hex edge: the lattice point nearest the
    // midpoint shared by the upper/dry tile and the lower/water tile. Choosing
    // merely the nearest ledge junction made sitters drift toward edge corners.
    // Facing is the tile-centre normal, exactly perpendicular to that edge.
    internal static bool TryGetEdgeSeatGeometry(
        WorldState world, Junction junction, bool waterOnly,
        out TileCoord standTile, out Float2 facing)
    {
        standTile = default;
        facing = Float2.Zero;
        if (junction.Blocked || junction.Tiles.Count < 2)
        {
            return false;
        }

        Tile high = null;
        Tile low = null;
        var bestPairDistance = float.MaxValue;
        foreach (var aCoord in junction.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(aCoord, out var a))
            {
                continue;
            }

            foreach (var bCoord in junction.Tiles)
            {
                if (aCoord == bCoord || !world.Tiles.Items.TryGetValue(bCoord, out var b))
                {
                    continue;
                }

                Tile pairHigh;
                Tile pairLow;
                if (waterOnly)
                {
                    if (a.Flags.HasFlag(TileFlags.Water) ||
                        !a.Flags.HasFlag(TileFlags.Walkable) ||
                        !b.Flags.HasFlag(TileFlags.Water))
                    {
                        continue;
                    }

                    pairHigh = a;
                    pairLow = b;
                }
                else
                {
                    if (a.Elevation <= b.Elevation ||
                        !a.Flags.HasFlag(TileFlags.Walkable) ||
                        a.Elevation - b.Elevation < 1)
                    {
                        continue;
                    }

                    pairHigh = a;
                    pairLow = b;
                }

                var highCenter = HexSpatialMath.TileToWorld(pairHigh.Coord);
                var lowCenter = HexSpatialMath.TileToWorld(pairLow.Coord);
                var midpoint = (highCenter + lowCenter) * 0.5f;
                var pairDistance = HexSpatialMath.Distance(junction.WorldPosition, midpoint);
                if (pairDistance < bestPairDistance)
                {
                    bestPairDistance = pairDistance;
                    high = pairHigh;
                    low = pairLow;
                }
            }
        }

        if (high is null || low is null)
        {
            return false;
        }

        var edgeMidpoint = (HexSpatialMath.TileToWorld(high.Coord) +
                            HexSpatialMath.TileToWorld(low.Coord)) * 0.5f;
        var canonical = junction.Id;
        var canonicalDistance = float.MaxValue;
        foreach (var id in high.Junctions)
        {
            if (!low.Junctions.Contains(id) ||
                !world.Junctions.Items.TryGetValue(id, out var shared) || shared.Blocked)
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(shared.WorldPosition, edgeMidpoint);
            if (distance < canonicalDistance - 0.0001f ||
                System.MathF.Abs(distance - canonicalDistance) <= 0.0001f && id.Value < canonical.Value)
            {
                canonicalDistance = distance;
                canonical = id;
            }
        }

        if (canonical != junction.Id)
        {
            return false;
        }

        standTile = high.Coord;
        facing = HexSpatialMath.Normalize(
            HexSpatialMath.TileToWorld(low.Coord) - HexSpatialMath.TileToWorld(high.Coord));
        return HexSpatialMath.Distance(facing, Float2.Zero) > 0.0001f;
    }

    // Spec 35.5: is a free drying rack within reach?
    private static bool HasFreeRackCandidate(WorldState world, NPCState npc)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition) &&
                definition.Tags.Contains("Rack") &&
                world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                !ExecutionSystem.RackIsFull(world, rack))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 35.5: a move-only trip to the lit campfire — standing within a
    // tile dries the whole outfit at x4 (MoistureSystem does the rest).
    private void BuildFireDryPlan(WorldState world, NPCState npc)
    {
        PerceivedObject? fire = null;
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable &&
                perceived.DefinitionId == "campfire.spot" &&
                world.Entities.Objects.TryGetValue(perceived.Id, out var campfire) &&
                campfire.ResourceAmount > 0f &&
                (fire is null || perceived.Distance < fire.Distance))
            {
                fire = perceived;
            }
        }

        JunctionId? anchor = null;
        if (fire is not null && world.Entities.Objects.TryGetValue(fire.Id, out var fireObject))
        {
            anchor = fireObject.Junctions.Count > 0 ? fireObject.Junctions[0] : null;
        }

        if (anchor is not { } target)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.DryClothes);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=DryClothes NoLitFire");
            return;
        }

        JunctionId? approach = null;
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, target))
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
            {
                approach = neighbor;
                break;
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.DryClothes);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=DryClothes NoFreeApproach");
            return;
        }

        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = fire!.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "FireDryPlanned",
            $"To campfire Tile={fire.Tile.Q},{fire.Tile.R}");
    }

    // Spec 27.18A foraging: no known food item — walk to the nearest known
    // producer; arriving brings dropped fruit into perception radius.
    private void BuildForagePlan(WorldState world, NPCState npc)
    {
        PerceivedObject? flora = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Produce is null)
            {
                continue;
            }

            // Spec 29C.4A food avoidance: skip producers near fresh danger
            // unless starving.
            if (!npc.Mind.IsStarving && IsNearDanger(npc, obj.Tile, 2))
            {
                continue;
            }

            if (flora is null || obj.Distance < flora.Distance)
            {
                flora = obj;
            }
        }

        if (flora is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=GetFood NoKnownProducer");
            return;
        }

        if (HexSpatialMath.HexDistance(npc.Tile, flora.Tile) <= 1)
        {
            // Already by the tree and still no fruit in sight: wait it out.
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "ForageWaiting",
                $"At producer {flora.DefinitionId} Tile={flora.Tile.Q},{flora.Tile.R}, no fruit visible");
            return;
        }

        JunctionId? floraJunction = null;
        if (world.Entities.Objects.TryGetValue(flora.Id, out var floraObject))
        {
            floraJunction = floraObject.Junctions.Count > 0 ? floraObject.Junctions[0] : null;
        }
        else if (npc.Memory.KnownObjects.TryGetValue(flora.Id, out var floraMemory))
        {
            floraJunction = floraMemory.Junction;
        }

        if (floraJunction is not { } anchor)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=GetFood Producer={flora.Id.Value} has no junction");
            return;
        }

        JunctionId? approach = null;
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, anchor))
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
            {
                approach = neighbor;
                break;
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=GetFood Producer={flora.Id.Value} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetTile = flora.Tile;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "ForagePlanned",
            $"To {flora.DefinitionId} Tile={flora.Tile.Q},{flora.Tile.R} " +
            $"Junction={approachJunction.Value} FromMemory={flora.FromMemory} Steps=[MoveToJunction]");
    }

    // Spec 28.15A: walk to a free neighbor junction of the target agent, then Talk.
    private void BuildTalkPlan(WorldState world, NPCState npc)
    {
        // Handshake (spec 28.8): if someone is already coming to talk to us,
        // wait for them instead of initiating our own approach.
        if (npc.Mind.PendingTalkFrom is { } incoming)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            Trace.Emit(world, npc.Id, "PlanNoInteraction",
                $"Goal=Socialize WaitingForTalkFrom=NPC{incoming.Value}");
            return;
        }

        // Spec 28.6 (iteration 8): prefer the most-liked available partner;
        // distance only breaks ties. Friendship self-selects.
        PerceivedAgent? target = null;
        foreach (var agent in npc.Perception.Agents)
        {
            if (!agent.IsReachable || agent.IsBusy || agent.IsMoving ||
                agent.IsUnconscious) // §60: never plan a chat with a body
            {
                continue;
            }

            // Skip targets already claimed by another initiator.
            if (world.Entities.Npcs.TryGetValue(agent.Id, out var agentState) &&
                agentState.Mind.PendingTalkFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (target is null ||
                agent.Relationship.Affinity > target.Relationship.Affinity + 0.01f ||
                (System.Math.Abs(agent.Relationship.Affinity - target.Relationship.Affinity) <= 0.01f &&
                 agent.Distance < target.Distance))
            {
                target = agent;
            }
        }

        if (target?.Junction is not { } targetJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Socialize);
            Trace.Emit(world, npc.Id, "PlanFailed",
                "Goal=Socialize NoApproachableAgent");
            return;
        }

        // Spec 28.8: talk at arm's length — a free junction ~0.9 hex radius
        // from the partner, on the initiator's side, not the adjacent
        // sub-grid point (that reads as standing inside each other).
        JunctionId? approach = null;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var partnerState))
        {
            var toMe = HexSpatialMath.Normalize(new Float2(
                npc.Position.X - partnerState.Position.X,
                npc.Position.Y - partnerState.Position.Y));
            var spot = new Float2(
                partnerState.Position.X + toMe.X * HexSpatialMath.HexRadius * 0.9f,
                partnerState.Position.Y + toMe.Y * HexSpatialMath.HexRadius * 0.9f);
            if (SpatialQueries.FindNearestJunction(world, spot) is { } armsLength &&
                !armsLength.Equals(targetJunction) &&
                SpatialQueries.IsJunctionFree(world, armsLength) &&
                SpatialMutations.TryReserveJunction(world, armsLength, npc.Id, world.Tick, 48))
            {
                approach = armsLength;
            }
        }

        if (approach is null)
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, targetJunction))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                    SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
                {
                    approach = neighbor;
                    break;
                }
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Socialize);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Socialize Target={target.Id.Value} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetAgentId = target.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = target.Tile;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var claimedTarget))
        {
            claimedTarget.Mind.PendingTalkFrom = npc.Id;
            claimedTarget.Mind.PendingTalkSinceTick = world.Tick;
            SocialCueSignals.Stamp(world, npc, "TalkRequest", target.Id);
            SocialCueSignals.Stamp(world, claimedTarget, "TalkIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "TalkRequested",
                $"Asked NPC{target.Id.Value} to talk " +
                $"Affinity={npc.Social.GetOrCreate(target.Id).Affinity:F2}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = InteractionType.Talk
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal=Socialize Target=NPC{target.Id.Value} " +
            $"ApproachJunction={approachJunction.Value} Steps=[MoveToJunction,Talk]");
    }

    // Spec §53: which aid interaction serves this kind of suffering.
    private static InteractionType AidInteraction(AidKind kind) => kind switch
    {
        AidKind.Feed => InteractionType.FeedOther,
        AidKind.Hydrate => InteractionType.HydrateOther,
        AidKind.Treat => InteractionType.TreatOther,
        AidKind.Medicate => InteractionType.MedicateOther,
        _ => InteractionType.ConsoleOther
    };

    // Spec §53: walk to a suffering housemate and help. Mirrors BuildTalkPlan
    // but selects the WORST-OFF reachable neighbour (highest Suffering) rather
    // than the most-liked, and claims her with PendingAidFrom so she holds still.
    private void BuildAidPlan(WorldState world, NPCState npc)
    {
        // If someone is already coming to help US, don't set off ourselves.
        if (npc.Mind.PendingAidFrom is { } incoming)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            Trace.Emit(world, npc.Id, "PlanNoInteraction",
                $"Goal=Aid WaitingForAidFrom=NPC{incoming.Value}");
            return;
        }

        PerceivedAgent? target = null;
        foreach (var agent in npc.Perception.Agents)
        {
            if (agent.AidKind == AidKind.None || agent.Suffering < Spec53.SufferingThreshold ||
                !agent.IsReachable || agent.IsBusy || agent.IsMoving)
            {
                continue;
            }

            // Skip a sufferer another helper is already on the way to.
            if (world.Entities.Npcs.TryGetValue(agent.Id, out var agentState) &&
                agentState.Mind.PendingAidFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (target is null || agent.Suffering > target.Suffering + 0.001f ||
                (System.Math.Abs(agent.Suffering - target.Suffering) <= 0.001f &&
                 agent.Distance < target.Distance))
            {
                target = agent;
            }
        }

        if (target?.Junction is not { } targetJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Aid);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Aid NoReachableSufferer");
            return;
        }

        // Help at arm's length — a free junction ~0.9 hex radius from her, on
        // our side (same geometry as a talk approach).
        JunctionId? approach = null;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var partnerState))
        {
            var toMe = HexSpatialMath.Normalize(new Float2(
                npc.Position.X - partnerState.Position.X,
                npc.Position.Y - partnerState.Position.Y));
            var spot = new Float2(
                partnerState.Position.X + toMe.X * HexSpatialMath.HexRadius * 0.9f,
                partnerState.Position.Y + toMe.Y * HexSpatialMath.HexRadius * 0.9f);
            if (SpatialQueries.FindNearestJunction(world, spot) is { } armsLength &&
                !armsLength.Equals(targetJunction) &&
                SpatialQueries.IsJunctionFree(world, armsLength) &&
                SpatialMutations.TryReserveJunction(world, armsLength, npc.Id, world.Tick, 48))
            {
                approach = armsLength;
            }
        }

        if (approach is null)
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, targetJunction))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                    SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
                {
                    approach = neighbor;
                    break;
                }
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Aid);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Aid Target={target.Id.Value} NoFreeApproachJunction");
            return;
        }

        var interaction = AidInteraction(target.AidKind);
        npc.Plan.TargetAgentId = target.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = target.Tile;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var claimedTarget))
        {
            claimedTarget.Mind.PendingAidFrom = npc.Id;
            claimedTarget.Mind.PendingAidSinceTick = world.Tick;
            SocialCueSignals.Stamp(world, npc, "AidRequest", target.Id);
            SocialCueSignals.Stamp(world, claimedTarget, "AidIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "AidRequested",
                $"Going to help NPC{target.Id.Value} Kind={target.AidKind} " +
                $"Suffering={target.Suffering:F2}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = interaction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal=Aid Kind={target.AidKind} Target=NPC{target.Id.Value} " +
            $"Suffering={target.Suffering:F2} ApproachJunction={approachJunction.Value} " +
            $"Steps=[MoveToJunction,{interaction}]");
    }

    private void BuildDefendPlan(WorldState world, NPCState npc)
    {
        JunctionId? attackerJunction = null;
        TileCoord attackerTile = npc.Tile;
        var label = string.Empty;

        if (npc.Mind.CombatAssistDogId is { } dogId)
        {
            foreach (var dog in world.Mobs)
            {
                if (dog.Id == dogId && dog.Health > 0f)
                {
                    attackerJunction = dog.Junction;
                    attackerTile = dog.Tile;
                    label = $"Dog={dog.Id}";
                    break;
                }
            }
        }
        else if (npc.Mind.CombatAssistAttackerNpcId is { } attackerId &&
                 world.Entities.Npcs.TryGetValue(attackerId, out var attacker) &&
                 attacker.Health > 0f)
        {
            attackerJunction = attacker.CurrentJunction;
            attackerTile = attacker.Tile;
            label = $"Attacker=NPC{attacker.Id.Value}";
        }

        if (attackerJunction is not { } target)
        {
            npc.Plan.Status = PlanStatus.Failed;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.CombatAssistDogId = null;
            npc.Mind.CombatAssistAttackerNpcId = null;
            Trace.Emit(world, npc.Id, "HelpCryAssistLost", "Attacker vanished before defender arrived");
            return;
        }

        JunctionId? approach = null;
        if (npc.CurrentJunction is { } current &&
            (current.Equals(target) ||
             (world.Junctions.Items.TryGetValue(target, out var targetJ) &&
              targetJ.Neighbors.Contains(current))))
        {
            approach = current;
        }
        else
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, target))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                    SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
                {
                    approach = neighbor;
                    break;
                }
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Defend);
            npc.Mind.CurrentGoal = GoalType.None;
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Defend {label} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = attackerTile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        SocialCueSignals.Stamp(world, npc, "HelpCryAssistStarted", npc.Id);
        Trace.Emit(world, npc.Id, "HelpCryAssistStarted",
            $"{label} ApproachJunction={approachJunction.Value} Tile={attackerTile.Q},{attackerTile.R}");
    }

    // Spec 29C.4A: is this tile within `radius` of a fresh danger memory?
    private static bool IsNearDanger(NPCState npc, TileCoord tile, int radius)
    {
        foreach (var danger in npc.Memory.Dangers)
        {
            if (HexSpatialMath.HexDistance(tile, danger.Tile) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    // Spec 23.10: a failed plan puts its goal on cooldown so the NPC does
    // something else instead of hammering the same target.
    private const int FailureCooldownTicks = 40;

    internal static void SetGoalCooldown(WorldState world, NPCState npc, GoalType goal)
    {
        if (goal == GoalType.Idle || goal == GoalType.None)
        {
            return;
        }

        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = goal,
            EndTick = world.Tick + FailureCooldownTicks
        });

        // A failed goal must not be defended by its own lock — otherwise the
        // hold rule keeps the zero-scored goal and planning hammers the same
        // target until the lock expires.
        if (npc.Mind.GoalLock is { } goalLock && goalLock.Goal == goal)
        {
            npc.Mind.GoalLock = null;
        }

        Trace.Emit(world, npc.Id, "GoalCooldownSet",
            $"{goal} on cooldown until tick {world.Tick + FailureCooldownTicks}");
    }

    private static readonly System.Collections.Generic.List<JunctionId> _rimScratch = new();

    private static InteractionType? GoalToInteraction(GoalType goal)
    {
        return goal switch
        {
            GoalType.GetFood => InteractionType.PickUp,
            GoalType.GatherWood => InteractionType.PickUp,
            GoalType.GatherTools => InteractionType.PickUp,
            GoalType.GetWater => InteractionType.PickUp, // §55: fetch a coconut to crack open
            GoalType.TendFire => InteractionType.Fuel,
            GoalType.CraftSpear => InteractionType.Craft,
            GoalType.CookMeat => InteractionType.Craft,
            GoalType.CraftLeather => InteractionType.Craft,
            GoalType.CraftAxe => InteractionType.Craft,
            GoalType.CraftPickaxe => InteractionType.Craft,
            GoalType.CraftRack => InteractionType.Craft,
            GoalType.CraftBed => InteractionType.Craft,
            GoalType.CraftTent => InteractionType.Craft,
            GoalType.BuildRaft => InteractionType.BuildRaft,
            GoalType.CraftBow => InteractionType.Craft,
            GoalType.CraftArrows => InteractionType.Craft,
            GoalType.DryClothes => InteractionType.Hang,
            GoalType.GatherStone => InteractionType.PickUp,
            GoalType.HarvestTree => InteractionType.Harvest,
            GoalType.MineBoulder => InteractionType.Harvest,
            GoalType.SplitLog => InteractionType.Process,
            GoalType.ChopCrown => InteractionType.Process,
            GoalType.GatherLeaves => InteractionType.PickUp,
            GoalType.HarvestYucca => InteractionType.Harvest,
            GoalType.Butcher => InteractionType.Butcher,
            GoalType.Build => InteractionType.Build,
            GoalType.BuildFurniture => InteractionType.Build,
            GoalType.Mourn => InteractionType.Observe,
            GoalType.WarmUp => InteractionType.Observe,
            GoalType.HaulToFire => InteractionType.Observe,
            GoalType.GatherHerb => InteractionType.PickUp,
            GoalType.CraftBandage => InteractionType.Craft,
            GoalType.GatherFiber => InteractionType.PickUp,
            GoalType.CraftRope => InteractionType.Craft,
            GoalType.CraftCloth => InteractionType.Craft,
            GoalType.CraftKnife => InteractionType.Craft,
            GoalType.Bury => InteractionType.Bury,
            GoalType.Sleep => InteractionType.Sleep,
            GoalType.Sit => InteractionType.Sit,
            GoalType.Dress => InteractionType.Dress,
            _ => null
        };
    }

    // Spec 29E.4: goals shop by tag — food pickups and wood pickups never cross.
    // §54.12: is a WHOLE log in demand anywhere — a furniture site whose
    // current stage bills logs, or the raft (hauled log by log)?
    private static bool WholeLogsWanted(WorldState world, NPCState npc)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (BuildSiteMath.IsSite(obj) &&
                BuildSiteMath.Needs(obj, BuildSiteMath.MaterialLogs))
            {
                return true;
            }
        }

        return world.RaftProgress < WorldState.RaftTarget &&
            DecisionSystem.HasReachableWithTag(npc, world, "Raft");
    }

    private static bool IsValidTargetFor(WorldState world, NPCState npc, GoalType goal, PerceivedObject perceived)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition))
        {
            return false;
        }

        switch (goal)
        {
            case GoalType.GetFood:
                // §54.14 (r2): cooked meat hanging on the spit is takeable food —
                // the campfire itself becomes a GetFood target while any hangs.
                if (definition.Tags.Contains("Campfire"))
                {
                    return world.Entities.Objects.TryGetValue(perceived.Id, out var spitSource) &&
                        BuildSiteMath.HangingMeat(spitSource, "food.meat_cooked") > 0;
                }

                return definition.Tags.Contains("Food") &&
                    (!definition.Tags.Contains("Coconut") || HasCoconutBlade(npc));
            case GoalType.GatherWood:
                // Spec §54: wood on the ground. A stick is always useful (fuel,
                // slats, crafts). §54.12: a whole LOG only when logs are billed
                // somewhere (a site's log stage, the raft) or she can split it
                // into sticks — the bed's stick stage otherwise sent her home
                // hugging a useless log.
                if (!definition.Tags.Contains("Wood"))
                {
                    return false;
                }

                if (!definition.Tags.Contains("Log"))
                {
                    return true;
                }

                return Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood) ||
                    WholeLogsWanted(world, npc);
            case GoalType.SplitLog:
                // Spec §54: split a log lying on the ground into sticks.
                return definition.Tags.Contains("Log");
            case GoalType.ChopCrown:
                // Spec §54.2: chop a felled palm crown into loose leaves.
                return definition.Tags.Contains("PalmCrown");
            case GoalType.GatherLeaves:
                // Spec §54.2: pick a scattered palm leaf off the ground.
                return definition.Tags.Contains("PalmLeaf");
            case GoalType.GatherTools:
                // Only a tool that ADDS something: a verb the pack can't do
                // yet or a better weapon — no hoarding capability-duplicates.
                if (definition.Tags.Contains("Tool") &&
                    !npc.Inventory.Items.Contains(perceived.DefinitionId) &&
                    Content.GearCatalog.AddsValueOver(
                        npc.Inventory.Items, perceived.DefinitionId, npc.Body.IntactHands))
                {
                    return true;
                }

                // Spec §52: a dropped garment whose pockets hold a missing
                // tool is a valid target — she rifles the pockets on arrival.
                return world.Entities.Objects.TryGetValue(perceived.Id, out var stashContainer) &&
                    stashContainer.Contents.Count > 0 &&
                    InventoryMath.StashHoldsWantedTool(world, npc, stashContainer);
            case GoalType.GatherHerb:
                return definition.Tags.Contains("Herb");
            case GoalType.HarvestYucca:
                return definition.Tags.Contains("Yucca");
            case GoalType.GatherFiber:
                return definition.Tags.Contains("Fiber");
            case GoalType.CookMeat:
                // §54.14 (r2): hanging meat needs a finished spit (stage 3)
                // with a free hook — any other lit fire won't do.
                return definition.Tags.Contains("Campfire") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var spitFire) &&
                    BuildSiteMath.CampfireSpitComplete(spitFire) &&
                    BuildSiteMath.HangingMeat(spitFire, "food.meat_raw") +
                    BuildSiteMath.HangingMeat(spitFire, "food.meat_cooked") <
                    SimBalance.CampfireSpitCapacity;
            case GoalType.CraftRope:
            case GoalType.CraftCloth:
            case GoalType.CraftKnife:
            case GoalType.CraftBandage:
            case GoalType.TendFire:
            case GoalType.CraftSpear:
            case GoalType.CraftLeather:
            case GoalType.CraftAxe:
            case GoalType.CraftPickaxe:
            case GoalType.CraftRack:
            case GoalType.CraftBed:
            case GoalType.CraftTent:
            case GoalType.CraftBow:
            case GoalType.CraftArrows:
                return definition.Tags.Contains("Campfire");
            case GoalType.DryClothes:
                // §35.5B: only a rack with a free hanger slot (capacity 8).
                return definition.Tags.Contains("Rack") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                    !ExecutionSystem.RackIsFull(world, rack);
            case GoalType.GatherStone:
                return definition.Tags.Contains("Stone");
            case GoalType.HarvestTree:
                return definition.Tags.Contains("Palm");
            case GoalType.MineBoulder:
                return definition.Tags.Contains("Boulder");
            case GoalType.Butcher:
                // Spec §54: an animal carcass any time; a housemate's body only
                // as a starvation last resort (cannibalism gate).
                if (definition.Tags.Contains("Carcass"))
                {
                    return true;
                }

                return definition.Tags.Contains("Corpse") &&
                    SimBalance.CannibalismEnabled &&
                    npc.Needs.Hunger >= SimBalance.CannibalizeHungerGate;
            case GoalType.Build:
                // Spec §52: the hut anchor only — a furniture site is a separate goal.
                return definition.Tags.Contains("BuildSite") &&
                    !definition.Tags.Contains("FurnitureSite");
            case GoalType.BuildFurniture:
                return definition.Tags.Contains("FurnitureSite");
            case GoalType.BuildRaft:
                return definition.Tags.Contains("Raft");
            case GoalType.Mourn:
                return definition.Tags.Contains("Corpse") || definition.Tags.Contains("Grave");
            case GoalType.WarmUp:
                // Spec 42: only a BURNING fire warms — a cold pit is no target.
                return definition.Tags.Contains("Campfire") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var pit) &&
                    pit.ResourceAmount > 0f;
            case GoalType.HaulToFire:
                // Spec §52: the stockpile is the hearth — any campfire, lit or not.
                return definition.Tags.Contains("Campfire");
            case GoalType.Bury:
                return definition.Tags.Contains("Corpse");
            case GoalType.GetWater:
                // Fetch a whole coconut; Drink will put it on the ground and
                // open it with a blade before sipping.
                return HasCoconutBlade(npc) && perceived.DefinitionId == "food.coconut";
            default:
                return true;
        }
    }
}

public sealed class ExecutionSystem : ISimulationSystem
{
    public string Name => nameof(ExecutionSystem);

    public TickLayer Layer => TickLayer.Fast;

    // Spec §54 (R2): recipe ingredient/gate check, sourced from RecipeCatalog so
    // the ingredient bill lives in one place (the §54 firewood→stick rewire
    // edits the catalog, not this switch). Returns false for any goal with no
    // catalog entry — preserving the old switch's `_ => false` default (e.g.
    // CraftBandage, deliberately not routed through the craft-start gate today).
    private static bool CraftGateOk(WorldState world, NPCState npc, WorldObjectState worldObject, GoalType goal)
    {
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            return false;
        }

        foreach (var ing in recipe.Inputs)
        {
            if (DecisionSystem.CountInventory(npc, ing.Id) < ing.Count)
            {
                return false;
            }
        }

        if (recipe.NeedsLitFire && worldObject.ResourceAmount <= 0f)
        {
            return false;
        }

        if (recipe.RequiresNoRack && DecisionSystem.RackExists(world))
        {
            return false;
        }

        return true;
    }

    private static bool CraftNeedsToolsOrWeapons(GoalType goal)
    {
        switch (goal)
        {
            case GoalType.CraftSpear:
            case GoalType.CraftAxe:
            case GoalType.CraftPickaxe:
            case GoalType.CraftRack:
            case GoalType.CraftTent:
            case GoalType.CraftBow:
            case GoalType.CraftArrows:
            case GoalType.CraftRope:
            case GoalType.CraftCloth:
            case GoalType.CraftKnife:
                return true;
            default:
                return false;
        }
    }

    // Spec §54 (R2): consume a recipe's inputs from the pack. Mirrors the exact
    // Remove-per-ingredient the effect switch used to do inline.
    private static void ConsumeRecipeInputs(NPCState npc, GoalType goal)
    {
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            return;
        }

        foreach (var ing in recipe.Inputs)
        {
            for (var i = 0; i < ing.Count; i++)
            {
                npc.Inventory.Items.Remove(ing.Id);
            }
        }
    }

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active)
            {
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GetFood &&
                (npc.Inventory.FindFirstFood(world.Content) is not null ||
                 DecisionSystem.HasInventoryCoconutMeal(npc)))
            {
                PlanInterruption.Abort(world, npc, "Food already available in inventory");
                npc.Mind.CurrentGoal = GoalType.None;
                Trace.Emit(world, npc.Id, "PlanAborted",
                    "GetFood stopped: inventory food is available");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.GetWater &&
                DecisionSystem.HasInventoryCoconutWater(npc))
            {
                PlanInterruption.Abort(world, npc, "Water already available in inventory");
                npc.Mind.CurrentGoal = GoalType.None;
                Trace.Emit(world, npc.Id, "PlanAborted",
                    "GetWater stopped: inventory water is available");
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.ConsumeInventoryItem)
            {
                RunConsumeInventoryItem(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.DropInventoryItem)
            {
                RunDropInventoryItem(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.UndressItem)
            {
                RunUndressItem(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.DrinkBottle)
            {
                RunDrinkBottle(world, npc);
                continue;
            }

            if (npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Type == PlanStepType.CraftInPlace)
            {
                RunCraftInPlace(world, npc);
                continue;
            }

            // Spec 29G: ground rest plans have no target object — the last
            // step says what to do once the walk (if any) is over.
            var lastStep = npc.Plan.Steps.Count > 0 ? npc.Plan.Steps[npc.Plan.Steps.Count - 1] : null;
            if (lastStep is { Type: PlanStepType.PrepareBathe })
            {
                RunPrepareBathe(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.SwimBathe })
            {
                RunSwimBathe(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.WashClothes })
            {
                RunWashClothes(world, npc, lastStep);
                continue;
            }

            if (lastStep is { Type: PlanStepType.GroundSit or PlanStepType.GroundSleep or PlanStepType.GroundCool })
            {
                RunGroundRestPlan(world, npc, lastStep);
                continue;
            }

            if (npc.Plan.TargetAgentId is not null)
            {
                // Spec §53: aid plans also carry a TargetAgentId — route them to
                // the aid handler; everything else agent-targeted is a talk.
                if (npc.Mind.CurrentGoal == GoalType.Aid)
                {
                    RunAid(world, npc);
                }
                else
                {
                    RunTalk(world, npc);
                }
                continue;
            }

            if (npc.Plan.TargetObjectId is null)
            {
                if (npc.Plan.Steps.Count > 0 &&
                    npc.Plan.Steps[npc.Plan.Steps.Count - 1].Type == PlanStepType.MoveToJunction)
                {
                    RunMoveOnly(world, npc);
                }

                continue;
            }

            if (!world.Entities.Objects.TryGetValue(npc.Plan.TargetObjectId.Value, out var worldObject))
            {
                // The target may be a remembered object we have not reached yet
                // (spec 27.18A): keep walking; judge the belief only at arrival.
                var atTarget = npc.Plan.TargetJunctionId is { } targetJ &&
                    npc.CurrentJunction is { } currentJ && currentJ.Equals(targetJ);
                if (!atTarget && npc.Execution.Status != ExecutionStatus.InProgress)
                {
                    continue;
                }

                // Arrived (or was mid-interaction) and the object is gone:
                // stale memory discovered — forget, release, re-decide.
                if (npc.Memory.KnownObjects.Remove(npc.Plan.TargetObjectId.Value))
                {
                    Trace.Emit(world, npc.Id, "MemoryForgotten",
                        $"Obj={npc.Plan.TargetObjectId.Value.Value} Stale (arrived, object gone)");
                }

                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"TargetObject={npc.Plan.TargetObjectId.Value.Value} not found in world (despawned?)");
                PlanInterruption.Abort(world, npc, "Target object despawned mid-plan");
                npc.Mind.CurrentGoal = GoalType.None;
                continue;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"Definition={worldObject.DefinitionId} not found in catalog");
                continue;
            }

            if (npc.Movement.IsMoving)
            {
                Trace.Emit(world, npc.Id, "ExecWaitingForMovement",
                    $"Status={npc.Movement.Status} PathStep={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}");
                continue;
            }

            if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
            {
                Trace.Emit(world, npc.Id, "ExecWaitingForArrival",
                    $"MovementStatus={npc.Movement.Status} (not Arrived)");
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.None)
            {
                // Memory promised a free object; reality may disagree
                // (spec 27.18A): never stomp another NPC's occupancy.
                if (worldObject.IsOccupied && worldObject.CurrentUser != npc.Id)
                {
                    // Spec 28.15B: scarcity breeds friction — resent the occupant.
                    if (worldObject.CurrentUser is { } occupant)
                    {
                        var resentRel = npc.Social.GetOrCreate(occupant);
                        resentRel.Affinity = MathUtil.Clamp(resentRel.Affinity - 0.08f, -1f, 1f);
                        npc.Execution.LastTalkResultTick = world.Tick;
                        npc.Execution.LastTalkAffinityDelta = -0.08f;
                        SocialCueSignals.Stamp(world, npc, "Resentment", occupant);
                        Trace.Emit(world, npc.Id, "RelationshipChanged",
                            $"NPC{npc.Id.Value}->NPC{occupant.Value} Aff={resentRel.Affinity:F2} " +
                            $"(-0.08 resentment: {worldObject.DefinitionId} taken)");
                    }

                    Trace.Emit(world, npc.Id, "InteractionBlocked",
                        $"{worldObject.DefinitionId} occupied by " +
                        $"NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"}");
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Target {worldObject.DefinitionId} occupied on arrival");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                var interaction = ResolveInteraction(definition, GetPlannedInteractionType(npc.Plan));
                if (interaction is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "ExecFailed",
                        $"No interaction of planned type on {worldObject.DefinitionId}");
                    continue;
                }

                // Spec 35.5: hanging needs a wet worn garment and a free rack.
                if (interaction.Type == InteractionType.Hang)
                {
                    var wetWorn = FindWettestWornItem(npc);
                    if (wetWorn is null || wetWorn.Wetness <= 0.5f || RackIsFull(world, worldObject))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            "Cannot hang (nothing wet or rack full)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 29F.3: recipe ingredients validated at start.
                if (interaction.Type == InteractionType.Craft)
                {
                    if (!npc.Body.CanUseToolsOrWeapons && CraftNeedsToolsOrWeapons(npc.Plan.Goal))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot craft {npc.Plan.Goal} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    // Spec §54 (R2): ingredients/gate sourced from RecipeCatalog.
                    // Same arm set as before — CraftBandage stays out (=> false),
                    // preserving today's behaviour that its craft-start gate never
                    // passes here.
                    var craftOk = npc.Plan.Goal switch
                    {
                        GoalType.CraftSpear => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CookMeat => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftLeather => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftAxe => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftPickaxe => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftRack => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftBed => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftTent => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftBow => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftArrows => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftRope => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftCloth => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        GoalType.CraftKnife => CraftGateOk(world, npc, worldObject, npc.Plan.Goal),
                        _ => false
                    };

                    if (!craftOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot craft ({npc.Plan.Goal}: ingredients or fire missing)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 35.3: building needs the full bill for the pending piece.
                // Spec 35.3: the HUT piece-placement (GoalType.Build) needs the full
                // wall/floor bill in hand at start. InteractionType.Build is SHARED
                // with GoalType.BuildFurniture, though — a furniture build-site
                // (campfire, bed) instead accepts a partial delivery of whatever
                // material it still needs (ApplyFurnitureSite), so it must NOT be
                // gated on the hut bill (that wrongly aborted bed deliveries, whose
                // leaves/sticks don't satisfy a hut piece).
                if (interaction.Type == InteractionType.Build && !BuildSiteMath.IsSite(worldObject))
                {
                    var pendingPiece = DecisionSystem.NextBuildPiece(world);
                    var billOk = pendingPiece is { } bill &&
                        DecisionSystem.CountInventory(npc, "resource.log") >= bill.Logs &&
                        DecisionSystem.CountInventory(npc, "resource.stone") >= bill.Stones &&
                        DecisionSystem.CountInventory(npc, "resource.palm_leaf") >= bill.Leaves;
                    if (!billOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc, "Cannot build (materials missing or hut done)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 35.2: trees need an axe or saw; boulders need the pickaxe.
                var harvestDurationDivisor = 1;
                if (interaction.Type == InteractionType.Harvest)
                {
                    if (!npc.Body.CanUseToolsOrWeapons)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot harvest {worldObject.DefinitionId} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    var isBoulder = definition.Tags.Contains("Boulder");
                    // Spec §54: yucca is cut with a BLADE — a knife or an axe (not
                    // a saw or pickaxe); trees still need an axe/saw; boulders the
                    // pickaxe.
                    var isYucca = definition.Tags.Contains("Yucca");
                    var hasChopTool = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood);
                    var hasBlade = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Cut);
                    var toolOk = isBoulder
                        ? Content.GearCatalog.HasCapability(
                            npc.Inventory.Items, Content.GearCapability.Mine)
                        : isYucca
                            ? hasBlade
                            : hasChopTool;
                    if (!toolOk)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot harvest {worldObject.DefinitionId} (missing tool)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    // Spec 35.2: faster felling comes from the gear sheet
                    // (saw = 2), not from an id check — any future power tool
                    // declares its own multiplier.
                    if (!isBoulder)
                    {
                        var speedMult = Content.GearCatalog.BestHarvestSpeedMult(npc.Inventory.Items);
                        if (speedMult > 1f)
                        {
                            harvestDurationDivisor = (int)speedMult;
                        }
                    }
                }

                // §gear: the DATA-DRIVEN skill gate, ANY-OF. The interaction
                // lists the capabilities it accepts — a log splits under an
                // axe (ChopWood) OR a knife (Cut); one generic check, no
                // per-verb code. Legacy per-type checks below run ONLY when
                // the interaction declares nothing (older content).
                if (interaction.RequiredCapabilities.Count > 0 &&
                    !DecisionSystem.HasAnyCapability(npc, interaction.RequiredCapabilities))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Cannot {interaction.Id} on {worldObject.DefinitionId} " +
                        $"(no gear with any of [{string.Join("/", interaction.RequiredCapabilities)}])");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec §54: splitting a log into sticks needs a chopping tool.
                // (Legacy path — skipped when the interaction DECLARES its
                // accepted capabilities; the any-of gate above already ran.)
                if (interaction.Type == InteractionType.Process &&
                    interaction.RequiredCapabilities.Count == 0)
                {
                    var isCoconut = definition.Tags.Contains("Coconut");
                    // §50-prone: coconuts are light hand-work — allowed lying.
                    // Heavy processing (log splitting) still needs standing.
                    if (!isCoconut && !npc.Body.CanUseToolsOrWeapons)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot process {worldObject.DefinitionId} (no legs)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }

                    var hasChopTool = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.ChopWood);
                    var hasCoconutBlade = Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Cut);
                    if (isCoconut ? !hasCoconutBlade : !hasChopTool)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot split {worldObject.DefinitionId} (missing tool)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                if (interaction.Type == InteractionType.Drink &&
                    definition.Tags.Contains("CoconutWater") &&
                    worldObject.ResourceAmount <= 0f)
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Cannot drink {worldObject.DefinitionId} (already drained)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec §54: butchering a carcass/corpse needs a knife in hand.
                // (Legacy path — declared interactions use the any-of gate.)
                if (interaction.Type == InteractionType.Butcher &&
                    interaction.RequiredCapabilities.Count == 0 &&
                    !Content.GearCatalog.HasCapability(
                        npc.Inventory.Items, Content.GearCapability.Butcher))
                {
                    PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                    PlanInterruption.Abort(world, npc,
                        $"Cannot butcher {worldObject.DefinitionId} (no knife)");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                // Spec 29E.3: fueling needs a carried log; lighting a dead
                // fire additionally needs the lighter — UNLESS she's cold
                // enough to friction/hand-drill it (§45 r5).
                if (interaction.Type == InteractionType.Fuel)
                {
                    var hasWoodNow = npc.Inventory.Items.Contains("resource.stick");
                    // §45 r5 parity: the DECISION layer already lets a genuinely
                    // cold girl SELECT TendFire without the one colony lighter
                    // (canFrictionLight = ThermalComfort < -0.35). Execution must
                    // honour the same rule or the brain sends her to the pit and
                    // this gate bounces her right back — the fire-probe soak
                    // showed 6007 "ready to light" freezing ticks converting to
                    // only 7 FireLit because the friction path was never wired
                    // into the Fuel interaction (only the lighter-carrier lit).
                    // §54.14 (r2): same hysteresis as the decision layer — the
                    // walk over must not revoke the drill (LastFreezingTick is
                    // stamped in DecisionSystem each freezing tick).
                    var canFrictionLight = npc.Needs.ThermalComfort < -0.35f ||
                        world.Tick - npc.Mind.LastFreezingTick < SimBalance.FrictionLightGraceTicks;
                    var missingLighter = worldObject.ResourceAmount <= 0f &&
                        !Content.GearCatalog.HasCapability(
                            npc.Inventory.Items, Content.GearCapability.Ignite) &&
                        !canFrictionLight;
                    if (!hasWoodNow || missingLighter)
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            $"Cannot fuel fire (wood={hasWoodNow} lighterMissing={missingLighter})");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                npc.Execution.Status = ExecutionStatus.InProgress;
                npc.Execution.CurrentInteraction = interaction.Type;
                npc.Execution.TargetObject = worldObject.Id;
                npc.Execution.StartTick = world.Tick;
                npc.Execution.EndTick = world.Tick + interaction.DurationTicks / harvestDurationDivisor;
                worldObject.IsOccupied = true;
                // Spec 28.15C: a corpse's CurrentUser records whose body it
                // is — mourning must not overwrite it.
                if (!definition.Tags.Contains("Corpse"))
                {
                    worldObject.CurrentUser = npc.Id;
                }
                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.OccupyJunction(world, jId, npc.Id);
                }

                Trace.Emit(world, npc.Id, "InteractionStarted",
                    $"{interaction.Type} -> {worldObject.DefinitionId} " +
                    $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                    $"EndTick={npc.Execution.EndTick} " +
                    $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
                    $"E={interaction.Effects.EnergyDelta:+0.00;-0.00} " +
                    $"C={interaction.Effects.ComfortDelta:+0.00;-0.00} " +
                    $"T={interaction.Effects.ThermalDelta:+0.00;-0.00} " +
                    $"W={interaction.Effects.WarmthDelta:+0.00;-0.00}]");
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.InProgress)
            {
                // Spec 42: the fire died mid-huddle — a dead pit warms nobody,
                // so warming (and boiling) at it stops NOW instead of playing
                // out the full interaction at a cold fireplace.
                if (npc.Execution.CurrentInteraction is InteractionType.Observe
                        or InteractionType.FillBottle &&
                    definition.Tags.Contains("Campfire") &&
                    worldObject.ResourceAmount <= 0f)
                {
                    PlanInterruption.Abort(world, npc, "Fire went out mid-interaction");
                    npc.Mind.CurrentGoal = GoalType.None;
                    continue;
                }

                var remaining = npc.Execution.EndTick - world.Tick;
                if (remaining > 0)
                {
                    var total = npc.Execution.EndTick - npc.Execution.StartTick;
                    var progress = total > 0 ? 1f - (float)remaining / total : 1f;
                    // Spec 29C.9: needs fill gradually over the action (Sims-
                    // style), not in a jump at the end. Each in-progress tick
                    // applies one duration-share; the final share lands at
                    // completion (total = duration shares = the full effect).
                    var inProgressInteraction = ResolveInteraction(definition, npc.Execution.CurrentInteraction);
                    if (inProgressInteraction is not null && total > 0)
                    {
                        ApplyEffectsScaled(npc, inProgressInteraction.Effects, 1f / total);
                    }

                    if (SimTrace.Verbose)
                    {
                        Trace.Emit(world, npc.Id, "ExecProgress",
                            $"{npc.Execution.CurrentInteraction} Progress={progress:P0} " +
                            $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                    }

                    continue;
                }

                var completedInteraction = ResolveInteraction(definition, npc.Execution.CurrentInteraction);
                if (completedInteraction is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "ExecFailed",
                        $"Interaction {npc.Execution.CurrentInteraction} vanished from {worldObject.DefinitionId}");
                    continue;
                }

                var needsBefore = Trace.FormatNeeds(npc.Needs);
                // Spec 29C.9: the final duration-share of the gradual fill —
                // the earlier shares landed tick-by-tick during the action.
                var completedTotal = npc.Execution.EndTick - npc.Execution.StartTick;
                ApplyEffectsScaled(npc, completedInteraction.Effects,
                    completedTotal > 0 ? 1f / completedTotal : 1f);

                // Spec 29H: filling the bottle charges it (raw at a bank,
                // boiled at a lit campfire) — thirst is quenched only on Drink.
                if (completedInteraction.Type == InteractionType.FillBottle)
                {
                    npc.BottleWater = definition.Tags.Contains("RawWater")
                        ? WaterKind.Raw : WaterKind.Boiled;
                    // Spec §52: one fill = several gulps; refill only when dry.
                    npc.BottleCharges = SimBalance.BottleCapacity;
                    Trace.Emit(world, npc.Id, "BottleFilled",
                        $"{npc.BottleWater} x{npc.BottleCharges} from {worldObject.DefinitionId}");
                }

                var needsAfter = Trace.FormatNeeds(npc.Needs);

                npc.Execution.Status = ExecutionStatus.Completed;
                npc.Execution.LastCompletedTick = world.Tick;
                // Spec 31C.8: the snapshot must not report a finished interaction —
                // the view would keep the pose while the body walks away.
                npc.Execution.CurrentInteraction = null;

                // Spec 31C.7A: after a proper rest she gets on with her day.
                if (completedInteraction.Type == InteractionType.Sit)
                {
                    npc.Mind.Cooldowns.Add(new GoalCooldown
                    {
                        Goal = GoalType.Sit,
                        EndTick = world.Tick + 240
                    });
                }

                if (completedInteraction.Type == InteractionType.PickUp)
                {
                    // §54.14 (r2): PickUp on the CAMPFIRE takes one cooked chunk
                    // off the spit — the fire itself never leaves the ground.
                    if (definition.Tags.Contains("Campfire"))
                    {
                        TakeMeatFromSpit(world, npc, worldObject);
                    }
                    // Spec §52: "gathering a tool" that rides in a dropped
                    // garment's pockets — rifle the pockets and leave the
                    // garment (with any non-tool stash) on the ground.
                    else if (npc.Plan.Goal == GoalType.GatherTools &&
                        worldObject.Contents.Count > 0 &&
                        !definition.Tags.Contains("Tool"))
                    {
                        RecoverStashedTools(world, npc, worldObject);
                    }
                    else if (!InventoryMath.MakeRoomFor(world, npc, worldObject.DefinitionId))
                    {
                        worldObject.IsOccupied = false;
                        worldObject.CurrentUser = null;
                        Trace.Emit(world, npc.Id, "PickupBlocked",
                            $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                            $"Inventory=[{string.Join(",", npc.Inventory.Items)}] " +
                            $"({npc.Inventory.UsedSlots}/{npc.Inventory.Capacity})");
                        continue;
                    }
                    else
                    {
                        // Item moves from world to inventory; the world object is gone,
                        // so occupancy flags die with it (spec 29B.2).
                        npc.Inventory.Items.Add(new ItemInstance(worldObject.DefinitionId)
                        {
                            Wetness = worldObject.Wetness,
                            Durability = worldObject.Durability,
                            ResourceAmount = worldObject.ResourceAmount,
                            Dirtiness = worldObject.Dirtiness,
                            Bloodiness = worldObject.Bloodiness
                        });
                        WorldObjectMutations.DespawnObject(world, worldObject.Id);
                        Trace.Emit(world, npc.Id, "ItemPickedUp",
                            $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                            $"Inventory=[{string.Join(",", npc.Inventory.Items)}] ({npc.Inventory.Items.Count}/{npc.Inventory.Capacity})");
                    }
                }
                else if (completedInteraction.Type == InteractionType.Dress)
                {
                    // Spec 31A.5B: one item per (layer, body part) — dressing
                    // over an occupied slot takes the old garment off first.
                    ResolveWearConflicts(world, npc, worldObject.DefinitionId);

                    // Spec 31A.5A: dressing consumes the world object — only
                    // one NPC can wear this garment.
                    npc.WornItems.Add(new ItemInstance(worldObject.DefinitionId)
                    {
                        Wetness = worldObject.Wetness,
                        Durability = worldObject.Durability,
                        Dirtiness = worldObject.Dirtiness,
                        Bloodiness = worldObject.Bloodiness
                    });
                    // Spec §52: putting the garment back on recovers whatever it
                    // was carrying — the pockets pour into the pack (capacity just
                    // grew by this garment's slots); anything still over spills.
                    _dressPourScratch.Clear();
                    _dressPourScratch.AddRange(worldObject.Contents);
                    worldObject.Contents.Clear();
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    EquipmentMath.Recalculate(world, npc);
                    foreach (var stashed in _dressPourScratch)
                    {
                        GiveOrDrop(world, npc, stashed);
                    }
                    if (_dressPourScratch.Count > 0)
                    {
                        Trace.Emit(world, npc.Id, "StashRecovered",
                            $"{worldObject.DefinitionId} returned [{string.Join(",", _dressPourScratch)}]");
                    }
                    Trace.Emit(world, npc.Id, "ItemWorn",
                        $"Def={worldObject.DefinitionId} Worn=[{string.Join(",", npc.WornItems)}] " +
                        $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");

                    // Spec 42: one wardrobe stop per while — never chain-dress.
                    // A cold girl with no real warmth in reach pinned Dress at
                    // score 1.0 forever (thermal=1.00, exec=InProgress at soak
                    // end) and starved the fire/water chain WITH THE LIGHTER IN
                    // HER POCKET. The cooldown opens a window for TendFire &
                    // GetWater between wardrobe attempts.
                    npc.Mind.Cooldowns.RemoveAll(c => c.Goal == GoalType.Dress);
                    npc.Mind.Cooldowns.Add(new GoalCooldown
                    {
                        Goal = GoalType.Dress,
                        EndTick = world.Tick + 160
                    });
                }
                else if (completedInteraction.Type == InteractionType.Craft &&
                         npc.Plan.Goal == GoalType.CookMeat &&
                         definition.Tags.Contains("Campfire"))
                {
                    // §54.14 (r2): "cooking" = HANGING the raw chunk on the
                    // spit. The roast itself runs in FireSystem while the fire
                    // burns; the cooked chunk stays on the crossbar until a
                    // hungry housemate takes it (GetFood → PickUp).
                    if (BuildSiteMath.CampfireSpitComplete(worldObject) &&
                        BuildSiteMath.HangingMeat(worldObject, "food.meat_raw") +
                        BuildSiteMath.HangingMeat(worldObject, "food.meat_cooked") <
                        SimBalance.CampfireSpitCapacity)
                    {
                        ConsumeRecipeInputs(npc, npc.Plan.Goal);
                        // ResourceAmount doubles as roast progress (ticks).
                        worldObject.Contents.Add(new ItemInstance("food.meat_raw"));
                        Trace.Emit(world, npc.Id, "MeatHungOnSpit",
                            $"food.meat_raw on the spit at Tile={worldObject.Tile.Q},{worldObject.Tile.R} " +
                            $"hanging raw={BuildSiteMath.HangingMeat(worldObject, "food.meat_raw")} " +
                            $"cooked={BuildSiteMath.HangingMeat(worldObject, "food.meat_cooked")}");
                    }
                    else
                    {
                        Trace.Emit(world, npc.Id, "SpitHangFailed",
                            $"spitComplete={BuildSiteMath.CampfireSpitComplete(worldObject)} " +
                            $"hooksUsed={BuildSiteMath.HangingMeat(worldObject, "food.meat_raw") + BuildSiteMath.HangingMeat(worldObject, "food.meat_cooked")}" +
                            $"/{SimBalance.CampfireSpitCapacity}");
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Craft)
                {
                    // Spec 29F.3 / §54 (R2): inputs consumed from RecipeCatalog
                    // via ConsumeRecipeInputs; the per-goal arm below only places
                    // the OUTPUT (tool / worn / furniture / side-effect). The
                    // firewood→stick rewire (§54 phase 1) edits the catalog, not
                    // these arms.
                    ConsumeRecipeInputs(npc, npc.Plan.Goal);
                    // Item-output crafts share one grant helper (also used by
                    // the in-place path); placed furniture keeps needing the
                    // station object it is raised beside.
                    if (!GrantCraftOutput(world, npc, npc.Plan.Goal))
                    {
                        switch (npc.Plan.Goal)
                        {
                            case GoalType.CraftRack:
                                PlaceRack(world, npc, worldObject);
                                break;
                            case GoalType.CraftBed:
                                // Spec §54.2: the campfire bed is the leaf MAT (8 leaves
                                // + 2 stick rails — consumed via the catalog). The
                                // premium bedroll is built at a progressive build-site,
                                // not here.
                                PlaceCraftedFurniture(world, npc, worldObject, "bed.leaf");
                                Trace.Emit(world, npc.Id, "BedCrafted", "A leaf sleeping-mat");
                                break;
                            case GoalType.CraftTent:
                                // Spec 40.14: 4 leaves woven into a shade canopy.
                                PlaceCraftedFurniture(world, npc, worldObject, "shelter.tent");
                                Trace.Emit(world, npc.Id, "TentCrafted", "A leaf sun shelter");
                                break;
                        }
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Build)
                {
                    // Spec §52: a furniture site accepts a delivery or is raised;
                    // the hut anchor runs the classic piece-placement.
                    if (BuildSiteMath.IsSite(worldObject))
                    {
                        ApplyFurnitureSite(world, npc, worldObject);
                    }
                    else
                    {
                        ApplyBuildPiece(world, npc);
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.BuildRaft)
                {
                    // Spec 40.15: every carried log goes into the raft; at the
                    // target the colony can sail off the island.
                    var deposited = DecisionSystem.CountInventory(npc, "resource.log");
                    npc.Inventory.Items.RemoveAll(i => i.DefinitionId == "resource.log");
                    world.RaftProgress = System.Math.Min(WorldState.RaftTarget, world.RaftProgress + deposited);
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                    Trace.Emit(world, npc.Id, "RaftProgress",
                        $"+{deposited} logs -> {world.RaftProgress}/{WorldState.RaftTarget}");
                    if (world.RaftProgress >= WorldState.RaftTarget)
                    {
                        world.Completed = true;
                        Trace.EmitSystem(world, "RaftLaunched",
                            "The raft is finished — the colony can leave the island!");
                    }
                }
                else if (completedInteraction.Type == InteractionType.Harvest)
                {
                    // Spec 35.2 / §54 (R1): the object is consumed; loot is
                    // declared as data (Yields) and scatters on the ground.
                    ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
                    if (definition.Tags.Contains("Boulder"))
                    {
                        Trace.Emit(world, npc.Id, "BoulderBroken",
                            $"{worldObject.DefinitionId} at Tile={worldObject.Tile.Q},{worldObject.Tile.R} -> 4 stones");
                    }
                    else
                    {
                        Trace.Emit(world, npc.Id, "TreeChopped",
                            $"{worldObject.DefinitionId} felled -> logs scattered");
                    }

                    // §54.2: a felled palm leaves a sit-able stump obstacle at its
                    // spot. Capture its placement, despawn the palm (unblocks its
                    // junction), then spawn the stump there (re-blocks it).
                    var leavesStump = definition.Tags.Contains("Palm");
                    var stumpTile = worldObject.Tile;
                    var stumpFragment = worldObject.Fragment;
                    var stumpJunction = worldObject.Junctions.Count > 0
                        ? (JunctionId?)worldObject.Junctions[0] : null;

                    WorldObjectMutations.DespawnObject(world, worldObject.Id);

                    if (leavesStump && stumpJunction is { } sj)
                    {
                        WorldObjectMutations.SpawnObject(world, "stump.palm", stumpFragment, stumpTile, sj);
                    }
                }
                else if (completedInteraction.Type == InteractionType.Process)
                {
                    if (definition.Tags.Contains("Coconut"))
                    {
                        var nextObject = ReplaceWithYields(world, npc, worldObject, completedInteraction.Yields);
                        Trace.Emit(world, npc.Id, "CoconutProcessed",
                            $"{worldObject.DefinitionId} -> {nextObject?.DefinitionId ?? "nothing"}");

                        if (nextObject is not null &&
                            TryContinueWorldPlanAfterInteraction(world, npc, nextObject, completedInteraction.Type))
                        {
                            continue;
                        }
                    }
                    else
                    {
                    // Spec §54: a Process consumes the object and scatters its
                    // yields — a log → sticks, a palm crown → leaves.
                    ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
                    var isCrown = definition.Tags.Contains("PalmCrown");
                    var yieldCount = 0;
                    foreach (var d in completedInteraction.Yields) yieldCount += d.Count;
                    Trace.Emit(world, npc.Id, isCrown ? "CrownChopped" : "LogSplit",
                        isCrown
                            ? $"{worldObject.DefinitionId} -> {yieldCount} leaves"
                            : $"{worldObject.DefinitionId} -> {SimBalance.LogSplitYield} sticks");
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    }
                }
                else if (completedInteraction.Type == InteractionType.Drink &&
                         definition.Tags.Contains("CoconutWater"))
                {
                    worldObject.ResourceAmount = System.MathF.Max(0f, worldObject.ResourceAmount - 1f);
                    Trace.Emit(world, npc.Id, "CoconutDrank",
                        $"{worldObject.DefinitionId} water left={worldObject.ResourceAmount:F0}");

                    if (TryContinueWorldPlanAfterInteraction(world, npc, worldObject, completedInteraction.Type))
                    {
                        continue;
                    }
                }
                else if (completedInteraction.Type == InteractionType.Eat &&
                         worldObject.DefinitionId == "food.coconut_open")
                {
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    Trace.Emit(world, npc.Id, "CoconutEaten", $"{worldObject.DefinitionId} consumed");
                }
                else if (completedInteraction.Type == InteractionType.Butcher)
                {
                    // Spec §54: knife a carcass/corpse — meat + hide scatter on the
                    // ground; the body is consumed. Butchering a housemate costs
                    // comfort (cannibalism).
                    ApplyHarvestYields(world, npc, worldObject, completedInteraction.Yields);
                    var wasCorpse = definition.Tags.Contains("Corpse");
                    if (wasCorpse && SimBalance.CannibalismEnabled)
                    {
                        // Comfort is satisfaction (higher = better) — the penalty
                        // subtracts.
                        npc.Needs.Comfort = MathUtil.Clamp(
                            npc.Needs.Comfort - SimBalance.CannibalismComfortPenalty, 0f, 1f);
                    }

                    Trace.Emit(world, npc.Id, "Butchered",
                        $"{worldObject.DefinitionId} (variant={worldObject.Variant}) -> meat + hide" +
                        (wasCorpse ? " [cannibalism]" : string.Empty));
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                }
                else if (completedInteraction.Type == InteractionType.Fuel)
                {
                    // Spec 29E.3 / §54: one stick per fueling, half a day of fire.
                    npc.Inventory.Items.Remove("resource.stick");
                    var wasLit = worldObject.ResourceAmount > 0f;
                    worldObject.ResourceAmount += 1200f;
                    Trace.Emit(world, npc.Id, wasLit ? "FireFueled" : "FireLit",
                        $"{worldObject.DefinitionId} Fuel={worldObject.ResourceAmount:F0} ticks");
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Observe &&
                         npc.Plan.Goal == GoalType.HaulToFire &&
                         definition.Tags.Contains("Campfire"))
                {
                    // Spec §52: set the low-value item down at the hearth (a
                    // fireside stockpile) — the pack has room again, and the item
                    // waits here to be reclaimed by normal pickup later.
                    var victim = InventoryMath.LowestImportanceDroppable(world, npc);
                    if (victim is not null)
                    {
                        npc.Inventory.Items.Remove(victim);
                        DropItemAtFeet(world, npc, victim);
                        Trace.Emit(world, npc.Id, "StashedAtFire",
                            $"{victim.DefinitionId} set by the fire (freed a slot)");
                    }
                }
                else if (completedInteraction.Type == InteractionType.Observe &&
                         definition.Tags.Contains("Corpse"))
                {
                    // Spec 28.15C: closure — the mourning period ends early.
                    npc.Mind.GrievingUntilTick = world.Tick;
                    worldObject.IsOccupied = false; // owner (CurrentUser) preserved
                    Trace.Emit(world, npc.Id, "Mourned",
                        $"Paid respects to NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"}");
                }
                else if (completedInteraction.Type == InteractionType.Observe &&
                         definition.Tags.Contains("Grave"))
                {
                    // Spec 28.15D: remembrance — the dead keep a social presence.
                    npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + 0.15f);
                    worldObject.IsOccupied = false; // owner preserved
                    Trace.Emit(world, npc.Id, "VisitedGrave",
                        $"Of NPC{worldObject.CurrentUser?.Value.ToString() ?? "?"} " +
                        $"Social={npc.Needs.Social:F2}");
                }
                else if (completedInteraction.Type == InteractionType.Hang)
                {
                    // Spec 35.5: the wettest garment moves onto the rack —
                    // an ownerless world object that dries at x5.
                    var wetWorn = FindWettestWornItem(npc);
                    if (wetWorn is not null && worldObject.Junctions.Count > 0)
                    {
                        npc.WornItems.Remove(wetWorn);
                        EquipmentMath.Recalculate(world, npc);
                        var hung = WorldObjectMutations.SpawnObject(
                            world, wetWorn.DefinitionId, npc.Fragment,
                            worldObject.Tile, worldObject.Junctions[0]);
                        hung.Wetness = wetWorn.Wetness;
                        hung.Durability = wetWorn.Durability;
                        hung.Dirtiness = wetWorn.Dirtiness;
                        hung.Bloodiness = wetWorn.Bloodiness;
                        Trace.Emit(world, npc.Id, "ItemHung",
                            $"{wetWorn.DefinitionId} Wetness={wetWorn.Wetness:F2} on rack " +
                            $"Obj={worldObject.Id.Value}");
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Bury)
                {
                    // Spec 28.15D: corpse -> permanent grave; the place is
                    // sanctified — fear leaves every living memory.
                    var deceased = worldObject.CurrentUser;
                    var graveJunction = worldObject.Junctions.Count > 0
                        ? worldObject.Junctions[0]
                        : npc.CurrentJunction ?? default;
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    var grave = WorldObjectMutations.SpawnObject(
                        world, "grave.npc", npc.Fragment, worldObject.Tile, graveJunction);
                    grave.CurrentUser = deceased;

                    foreach (var living in world.Entities.Npcs.Values)
                    {
                        living.Memory.Dangers.RemoveAll(dg => dg.Tile == worldObject.Tile);
                    }

                    npc.Mind.GrievingUntilTick = world.Tick;
                    Trace.Emit(world, npc.Id, "Buried",
                        $"NPC{deceased?.Value.ToString() ?? "?"} laid to rest at " +
                        $"Tile={worldObject.Tile.Q},{worldObject.Tile.R}");
                }
                else
                {
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }

                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.FreeJunction(world, jId, npc.Id);
                    SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
                }

                Trace.Emit(world, npc.Id, "InteractionCompleted",
                    $"{completedInteraction.Type} on {worldObject.DefinitionId} " +
                    $"Duration={npc.Execution.EndTick - npc.Execution.StartTick}ticks " +
                    $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}]");

                // Spec 41.5: waking from a bed = stand and come to your
                // senses for a beat before the next errand.
                if (completedInteraction.Type == InteractionType.Sleep)
                {
                    npc.Mind.WakeGraceUntilTick = world.Tick + 12;
                }

                npc.Plan.Status = PlanStatus.Completed;
                npc.Plan.Steps.Clear();
                npc.Plan.TargetObjectId = null;
                npc.Plan.TargetJunctionId = null;
                npc.Plan.TargetTile = null;
                npc.Plan.TargetItemDefinitionId = null;
                npc.Plan.TargetAgentId = null;
                npc.Mind.CurrentGoal = GoalType.None;
                // Back to None so the next plan's interaction can start
                // (Completed would block the start gate forever).
                npc.Execution.Status = ExecutionStatus.None;
                npc.Execution.CurrentInteraction = null;
                npc.Execution.TargetObject = null;
                npc.Execution.StartTick = 0;
                npc.Execution.EndTick = 0;
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;

                Trace.Emit(world, npc.Id, "CycleReset",
                    $"Goal->None Plan->Completed Execution->Cleared Movement->Cleared (ready for next decision)");
            }
        }
    }

    // Spec §52: one visit to a furniture build-site. If it still wants
    // materials, deposit whatever needed items are in hand (partial delivery is
    // fine — many NPCs top it up over many trips). Once fully stocked, a builder
    // WITH A HAMMER raises the piece: the product spawns at the site's junction
    // and the site despawns. The hammer is a tool — it is NOT consumed.
    private static void ApplyFurnitureSite(WorldState world, NPCState npc, WorldObjectState site)
    {
        var stockedBefore = BuildSiteMath.IsStocked(site);
        if (!stockedBefore)
        {
            // Deposit each material the site still needs, one at a time.
            foreach (var mat in BuildSiteMath.AllMaterials)
            {
                while (BuildSiteMath.Needs(site, mat))
                {
                    var carried = npc.Inventory.Items.Find(i => i.DefinitionId == mat);
                    if (carried is null)
                    {
                        break;
                    }

                    npc.Inventory.Items.Remove(carried);
                    site.Contents.Add(carried);
                }
            }

            Trace.Emit(world, npc.Id, "SiteDelivered",
                $"{site.BuildProduct}: logs {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialLogs)}/{site.BillLogs} " +
                $"stones {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialStones)}/{site.BillStones} " +
                $"leaves {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialLeaves)}/{site.BillLeaves} " +
                $"sticks {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialSticks)}/{site.BillSticks} " +
                $"rope {BuildSiteMath.Delivered(site, BuildSiteMath.MaterialRope)}/{site.BillRope}");
        }

        // §54.14: a campfire site raises EARLY — the moment the stage-1 stick
        // pile is delivered it becomes a real (cold, lightable) campfire that
        // keeps the open bill and accepts the upgrade stages in place. The
        // bootstrap hearth is born past this point already.
        if (site.DefinitionId == "build.site" && site.BuildProduct == "campfire.spot" &&
            BuildSiteMath.Delivered(site, BuildSiteMath.MaterialSticks) >= BuildSiteMath.CampfireStage1Sticks)
        {
            var fireJunction = site.Junctions.Count > 0 ? site.Junctions[0] : npc.CurrentJunction;
            var fireTile = site.Tile;
            WorldObjectMutations.DespawnObject(world, site.Id);
            if (fireJunction is { } fj)
            {
                var fire = WorldObjectMutations.SpawnObject(
                    world, "campfire.spot", npc.Fragment, fireTile, fj);
                fire.ResourceAmount = 0f; // born cold — light it like any fire
                fire.BuildProduct = "campfire.spot";
                fire.BillSticks = site.BillSticks;
                fire.BillStones = site.BillStones;
                fire.BillRope = site.BillRope;
                fire.Contents.AddRange(site.Contents);
                Trace.Emit(world, npc.Id, "FurnitureBuilt",
                    $"campfire.spot raised at stage 1, Tile={fireTile.Q},{fireTile.R} (upgrades continue in place)");
            }

            return;
        }

        // Raise it if it is now stocked and a hammer is at hand — carried, or
        // simply lying at the build site (the tool waits at the workbench). This
        // keeps the hammer a real requirement without demanding the one girl who
        // stocks the last stone also happen to be carrying it.
        var hammerAtSite = false;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!Content.GearCatalog.For(obj.DefinitionId).Has(Content.GearCapability.Hammer))
            {
                continue;
            }

            if (obj.Tile.Equals(site.Tile) ||
                HexSpatialMath.HexDistance(obj.Tile, site.Tile) <= 1)
            {
                hammerAtSite = true;
                break;
            }
        }

        // Spec §54: a campfire is piled from stones, and the leaf mat and the
        // drying rack (§35.5B) are hand-lashed. Rigid furniture still needs
        // the builder's hammer.
        var needsHammer = site.BuildProduct is not ("campfire.spot" or "bed.leaf" or "station.drying_rack");
        if (BuildSiteMath.IsStocked(site) &&
            (!needsHammer ||
             Content.GearCatalog.HasCapability(npc.Inventory.Items, Content.GearCapability.Hammer) ||
             hammerAtSite))
        {
            // §54.14: an upgraded-in-place piece (the campfire) IS its own
            // product — completion just closes the bill. Despawn/respawn here
            // would snuff the live fire and reset its fuel.
            if (site.DefinitionId == site.BuildProduct)
            {
                site.BuildProduct = string.Empty;
                Trace.Emit(world, npc.Id, "FurnitureBuilt",
                    $"{site.DefinitionId} upgrades finished in place at Tile={site.Tile.Q},{site.Tile.R}");
                return;
            }

            var junction = site.Junctions.Count > 0 ? site.Junctions[0] : npc.CurrentJunction;
            var tile = site.Tile;
            var product = site.BuildProduct;
            WorldObjectMutations.DespawnObject(world, site.Id);
            if (junction is { } j)
            {
                WorldObjectMutations.SpawnObject(world, product, npc.Fragment, tile, j);
            }

            Trace.Emit(world, npc.Id, "FurnitureBuilt",
                $"{product} raised at Tile={tile.Q},{tile.R}");
        }
    }

    // Spec 35.3: consume the bill and place the pending piece; walls block
    // edge junctions (TopologyVersion++), the door keeps one passable
    // junction marked Door; completion flips the tile Indoor and rewards
    // the colony with a wilderness bed.
    private static void ApplyBuildPiece(WorldState world, NPCState npc)
    {
        var project = world.Project;
        var piece = DecisionSystem.NextBuildPiece(world);
        if (project is null || piece is not { } bill)
        {
            return;
        }

        for (var i = 0; i < bill.Logs; i++) npc.Inventory.Items.Remove("resource.log");
        for (var i = 0; i < bill.Stones; i++) npc.Inventory.Items.Remove("resource.stone");
        for (var i = 0; i < bill.Leaves; i++) npc.Inventory.Items.Remove("resource.palm_leaf");

        if (bill.Kind == "Floor")
        {
            project.FloorDone = true;
            if (world.Tiles.Items.TryGetValue(project.Tile, out var floorTile))
            {
                floorTile.Flags |= TileFlags.HasFloor;
            }
        }
        else
        {
            var direction = HexDirection.All[bill.Edge];
            var neighbor = new TileCoord(project.Tile.Q + direction.DQ, project.Tile.R + direction.DR);
            var keepDoor = bill.Kind == "Door";
            var doorKept = false;
            if (world.Tiles.Items.TryGetValue(project.Tile, out var siteTile))
            {
                // Spec 35.3: pick the doorway first — it must be a mid-edge
                // junction (shared by exactly the site and the door
                // neighbor). Corner junctions are already walled by the
                // adjacent edges; marking one as the door seals the hut.
                if (keepDoor)
                {
                    foreach (var junctionId in siteTile.Junctions)
                    {
                        if (world.Junctions.Items.TryGetValue(junctionId, out var candidate) &&
                            candidate.Tiles.Contains(neighbor) && candidate.Tiles.Count == 2 &&
                            !candidate.Blocked)
                        {
                            candidate.Door = true;
                            doorKept = true;
                            break;
                        }
                    }
                }

                foreach (var junctionId in siteTile.Junctions)
                {
                    if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                        !junction.Tiles.Contains(neighbor) || junction.Tiles.Count < 2)
                    {
                        continue;
                    }

                    if (!junction.Blocked && !junction.Door)
                    {
                        // Nudge anyone standing where the wall goes up.
                        foreach (var bystander in world.Entities.Npcs.Values)
                        {
                            if (bystander.CurrentJunction is { } cj && cj.Equals(junctionId))
                            {
                                bystander.CurrentJunction = null;
                            }
                        }

                        junction.Blocked = true;
                    }
                }
            }

            if (keepDoor && !doorKept)
            {
                Trace.EmitSystem(world, "DoorPlacementFailed",
                    $"No free mid-edge junction on edge {bill.Edge} — hut may be sealed");
            }

            project.EdgeDone[bill.Edge] = true;
            world.TopologyVersion++;
        }

        Trace.Emit(world, npc.Id, "BuildProgress",
            $"{bill.Kind}{(bill.Edge >= 0 ? $" edge {bill.Edge}" : "")} placed at " +
            $"Tile={project.Tile.Q},{project.Tile.R}");

        if (DecisionSystem.NextBuildPiece(world) is null)
        {
            project.Completed = true;
            if (world.Tiles.Items.TryGetValue(project.Tile, out var hutTile))
            {
                hutTile.Flags |= TileFlags.Indoor;
                if (hutTile.Junctions.Count > 1)
                {
                    WorldObjectMutations.SpawnObject(world, "bed.basic",
                        npc.Fragment, project.Tile, hutTile.Junctions[1]);
                }
            }

            foreach (var obj in world.Entities.Objects.Values)
            {
                if (obj.DefinitionId == "construction.site")
                {
                    WorldObjectMutations.DespawnObject(world, obj.Id);
                    break;
                }
            }

            world.TopologyVersion++;
            Trace.EmitSystem(world, "HutCompleted",
                $"Hut at Tile={project.Tile.Q},{project.Tile.R} — indoor sanctuary with a bed");
        }
    }

    // Move-only plan (spec 27.18A foraging): no interaction — the plan
    // completes on arrival, letting perception refresh from the new spot.
    private static void RunMoveOnly(WorldState world, NPCState npc)
    {
        if (npc.Movement.IsMoving)
        {
            return;
        }

        var atTarget = npc.Plan.TargetJunctionId is { } targetJ &&
            npc.CurrentJunction is { } currentJ && currentJ.Equals(targetJ);
        if (!atTarget)
        {
            return;
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        Trace.Emit(world, npc.Id, "MoveOnlyArrived",
            $"Junction={npc.Plan.TargetJunctionId?.Value.ToString() ?? "-"} " +
            $"Tile={npc.Tile.Q},{npc.Tile.R} (looking around)");

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        if (npc.Mind.CurrentGoal == GoalType.Defend)
        {
            SocialCueSignals.Stamp(world, npc, "HelpCryAssistArrived", npc.Id);
            Trace.Emit(world, npc.Id, "HelpCryAssistArrived",
                "Reached attacker and joined the fight");
            return;
        }

        npc.Mind.CurrentGoal = GoalType.None;
        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed (move-only plan arrived)");
    }

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
    private const float TalkRelationshipGain = 0.075f;

    // Spec 28.15B: quarrels and refusal-by-dislike.
    private const float QuarrelInitiatorSocialGain = 0.15f;
    private const float QuarrelListenerSocialGain = 0.10f;
    private const float QuarrelAffinityLoss = 0.18f;
    private const float QuarrelEmbarrassment = 0.30f;
    private const float RefusalAffinityThreshold = -0.25f;
    private const float LonelinessOverrideThreshold = 0.25f;
    private const float RejectionAffinityPenalty = 0.075f;

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

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            var talkRange = HexSpatialMath.HexRadius * 4f;
            var distance = HexSpatialMath.Distance(npc.Position, target.Position);
            if (distance > talkRange)
            {
                AbortTalk(world, npc,
                    $"Target NPC{targetId.Value} out of range (Dist={distance:F2} > {talkRange:F2})");
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
            target.RotationDegrees = HexSpatialMath.AngleDegrees(
                new Float2(-faceDirection.X, -faceDirection.Y));

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
            // deterministic) and carry it on the initiator — the presentation
            // shows the matching emoji over the speaker's head for the talk.
            var topic = PickTalkTopic(world, npc, target);
            npc.Execution.CurrentTalkTopic = topic;
            target.Execution.CurrentTalkTopic = topic;
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            Trace.Emit(world, npc.Id, "TalkStarted",
                $"With NPC{targetId.Value} Topic={topic} Duration={TalkDurationTicks}ticks " +
                $"({TalkDurationTicks * world.TickDeltaTime:F1}s) Dist={distance:F2}");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            if (remaining > 0)
            {
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

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            var aidRange = HexSpatialMath.HexRadius * 4f;
            var distance = HexSpatialMath.Distance(npc.Position, target.Position);
            if (distance > aidRange)
            {
                AbortAid(world, npc,
                    $"Target NPC{targetId.Value} out of aid range (Dist={distance:F2} > {aidRange:F2})");
                return;
            }

            // Re-check on arrival: she may have recovered / died on the way.
            var kindNow = AssessAidKind(target, world.Tick, out var severity);
            if (kindNow == AidKind.None || severity < Spec53.SufferingThreshold)
            {
                AbortAid(world, npc, $"NPC{targetId.Value} no longer needs aid");
                return;
            }

            // Turn to face her — a caring stance (both turn toward each other).
            var faceDelta = new Float2(
                target.Position.X - npc.Position.X, target.Position.Y - npc.Position.Y);
            var faceDirection = HexSpatialMath.Normalize(faceDelta);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(faceDirection);
            target.RotationDegrees = HexSpatialMath.AngleDegrees(
                new Float2(-faceDirection.X, -faceDirection.Y));

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
                $"Duration={Spec53.AidDuration}ticks");
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

    // Spec 31A.5B: remove and drop any worn item sharing (layer, part) with
    // the garment about to be worn — molly BodyBones.Equip semantics.
    private static readonly System.Collections.Generic.List<string> _conflictScratch = new();

    private static void ResolveWearConflicts(WorldState world, NPCState npc, string newItemId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(newItemId, out var newDefinition) ||
            newDefinition.Layer is not { } newLayer)
        {
            return;
        }

        _conflictScratch.Clear();
        foreach (var wornId in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(wornId, out var wornDefinition) ||
                wornDefinition.Layer != newLayer)
            {
                continue;
            }

            foreach (var part in newDefinition.Covers)
            {
                if (wornDefinition.Covers.Contains(part))
                {
                    _conflictScratch.Add(wornId);
                    break;
                }
            }
        }

        foreach (var conflictId in _conflictScratch)
        {
            var conflictItem = npc.WornItems.Find(i => i.DefinitionId == (string)conflictId) ??
                new ItemInstance(conflictId);
            npc.WornItems.Remove(conflictItem);
            DropItemAtFeet(world, npc, conflictItem);
            Trace.Emit(world, npc.Id, "ItemReplaced",
                $"{conflictId} taken off (layer conflict with {newItemId})");
        }
    }

    // §35.5B: the rack holds up to SimBalance.RackCapacity garments — the
    // wearables at its junction, one per hanger slot on the assembled prefab.
    internal static bool RackIsFull(WorldState world, WorldObjectState rack)
    {
        if (rack.Junctions.Count == 0)
        {
            return true;
        }

        var hung = 0;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Id.Value != rack.Id.Value && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(rack.Junctions[0]) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Layer is not null)
            {
                hung++;
                if (hung >= SimBalance.RackCapacity)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static ItemInstance? FindWettestWornItem(NPCState npc)
    {
        ItemInstance? wettest = null;
        foreach (var item in npc.WornItems)
        {
            if (wettest is null || item.Wetness > wettest.Wetness)
            {
                wettest = item;
            }
        }

        return wettest;
    }

    // Spec 29G: crafted furniture lands on a free junction by the fire.
    private static void PlaceCraftedFurniture(WorldState world, NPCState npc, WorldObjectState campfire, string definitionId)
    {
        var spot = FindSpacedFurnitureSpot(world, campfire, definitionId) ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            return;
        }

        WorldObjectMutations.SpawnObject(world, definitionId, npc.Fragment, npc.Tile, junction);
    }

    // Spec 35.7 (iter 33): the camp is no longer a heap. Crafted furniture
    // still hugs the fire (keeping travel cheap — the economy is tight), but
    // never lands right on top of another bed/rack: prefer a fireside junction
    // that isn't within a tile of an existing piece, widening the search ring
    // only if the near ones are all taken.
    private static readonly string[] OtherFurnitureTags = { "Bed", "Rack" };

    private static JunctionId? FindSpacedFurnitureSpot(
        WorldState world, WorldObjectState campfire, string definitionId)
    {
        if (campfire.Junctions.Count == 0)
        {
            return null;
        }

        // §54.9A: the piece must PHYSICALLY fit — its footprint (ObstacleRadius
        // measured off the real prefab) may not cross boulders, palms, the
        // ember ring, or other furniture.
        var footprint = world.Content.ObjectDefinitions.TryGetValue(definitionId, out var placedDef)
            ? placedDef.ObstacleRadius
            : 0f;

        var anchor = campfire.Junctions[0];
        // Pass 1: a free junction on the standable rim just OUTSIDE the
        // fire's blocked ember ring (§47). GetPassableNeighbors would look
        // at the anchor's immediate neighbors — all inside the blocked ring
        // now — so we use the same beside-arrival BFS the planner uses:
        // furniture lands in the passable zone with a natural offset from
        // the flames, still fireside-close.
        SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch);
        foreach (var neighbor in _furnitureRimScratch)
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                world.Junctions.Items.TryGetValue(neighbor, out var j) && j.Tiles.Count > 0 &&
                !IsNearOtherFurniture(world, j.Tiles[0]) &&
                SpatialQueries.FootprintClear(world, j, footprint))
            {
                return neighbor;
            }
        }

        // Pass 2: any junction 2 tiles out that's clear of other furniture.
        JunctionId? ring = null;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Walkable) || tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            if (HexSpatialMath.HexDistance(junction.Tiles[0], campfire.Tile) == 2 &&
                !IsNearOtherFurniture(world, junction.Tiles[0]) &&
                SpatialQueries.FootprintClear(world, junction, footprint))
            {
                ring = junction.Id;
                break;
            }
        }

        // Pass 3: fall back to any free rim junction (heap beats nowhere).
        if (ring is null)
        {
            SpatialQueries.CollectStandableAround(world, anchor, _furnitureRimScratch);
            foreach (var neighbor in _furnitureRimScratch)
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor))
                {
                    return neighbor;
                }
            }
        }

        return ring;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _furnitureRimScratch = new();

    // Within 1 tile of an existing bed or rack (the campfire itself is fine
    // to sit beside — we only want to avoid stacking furniture on furniture).
    private static bool IsNearOtherFurniture(WorldState world, TileCoord tile)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            foreach (var tag in OtherFurnitureTags)
            {
                if (def.Tags.Contains(tag) && HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Spec 35.5: the crafted rack goes onto a free junction next to the
    // campfire; when the fireside is crowded it lands at the crafter's feet.
    private static void PlaceRack(WorldState world, NPCState npc, WorldObjectState campfire)
    {
        // Spec 35.7: spaced away from the fire and the bed (no more heap).
        var spot = FindSpacedFurnitureSpot(world, campfire, "station.drying_rack") ?? npc.CurrentJunction;
        if (spot is not { } junction)
        {
            GiveOrDrop(world, npc, "resource.stick");
            GiveOrDrop(world, npc, "resource.stick");
            Trace.Emit(world, npc.Id, "ExecFailed", "CraftRack: nowhere to place the rack");
            return;
        }

        var rack = WorldObjectMutations.SpawnObject(
            world, "station.drying_rack", npc.Fragment, npc.Tile, junction);
        Trace.Emit(world, npc.Id, "RackCrafted",
            $"Obj={rack.Id.Value} Junction={junction.Value}");
    }

    // Spec 29F: into the inventory, or at the feet when full.
    internal static void GiveOrDrop(WorldState world, NPCState npc, ItemInstance item)
    {
        if (npc.Inventory.HasSpace)
        {
            npc.Inventory.Items.Add(item);
        }
        else
        {
            DropItemAtFeet(world, npc, item);
        }
    }

    // Spec §54: a slain animal leaves a carcass on the map — knife it for meat
    // + hide. Rots on the CorpseSystem clock (Decays tag + ResourceAmount).
    internal static void SpawnCarcass(WorldState world, TileCoord tile, JunctionId junction, string variant)
    {
        var fragment = new FragmentId(1);
        foreach (var any in world.Entities.Npcs.Values)
        {
            fragment = any.Fragment;
            break;
        }

        var carcass = WorldObjectMutations.SpawnObject(world, "carcass.animal", fragment, tile, junction);
        carcass.ResourceAmount = SimBalance.CarcassDecayTicks;
        carcass.SpawnTick = world.Tick;
        carcass.Variant = variant;
        Trace.EmitSystem(world, "CarcassSpawned",
            $"{variant} carcass at Tile={tile.Q},{tile.R}");
    }

    // Spec §54 (R1): materialize a data-driven Yields list. Scatter drops land
    // as distinct ground objects at free junctions around the harvested/split
    // spot (logs & leaves around the stump, sticks around the split log); a
    // non-Scatter drop falls back to the legacy inventory-first GiveOrDrop.
    internal static void ApplyHarvestYields(
        WorldState world, NPCState npc, WorldObjectState source,
        System.Collections.Generic.IReadOnlyList<HarvestDrop> yields)
    {
        var used = new System.Collections.Generic.HashSet<JunctionId>();
        foreach (var drop in yields)
        {
            for (var i = 0; i < drop.Count; i++)
            {
                if (!drop.Scatter)
                {
                    GiveOrDrop(world, npc, CreateYieldItem(world, drop.DefinitionId));
                    continue;
                }

                var (tile, junction) = FindScatterSpot(world, source, used);
                if (junction is { } j)
                {
                    var spawned = WorldObjectMutations.SpawnObject(
                        world, drop.DefinitionId, source.Fragment, tile, j);
                    spawned.SpawnTick = world.Tick;
                    used.Add(j);
                }
                else
                {
                    // No free spot in the ring — don't lose the item, hand it over.
                    GiveOrDrop(world, npc, CreateYieldItem(world, drop.DefinitionId));
                }
            }
        }
    }

    private static ItemInstance CreateYieldItem(WorldState world, string definitionId)
    {
        var item = new ItemInstance(definitionId);
        // §59-склад: начальный запас из декларации (вода дырявого кокоса).
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def))
        {
            var water = def.StoredAmount(Content.StoredKind.Water);
            if (water > 0f)
            {
                item.ResourceAmount = water;
            }
        }

        return item;
    }

    private static WorldObjectState? ReplaceWithYields(
        WorldState world,
        NPCState npc,
        WorldObjectState source,
        System.Collections.Generic.IReadOnlyList<HarvestDrop> yields)
    {
        if (yields.Count == 0 || source.Junctions.Count == 0)
        {
            WorldObjectMutations.DespawnObject(world, source.Id);
            return null;
        }

        var tile = source.Tile;
        var junction = source.Junctions[0];
        var fragment = source.Fragment;
        WorldObjectMutations.DespawnObject(world, source.Id);

        WorldObjectState? primary = null;
        var used = new System.Collections.Generic.HashSet<JunctionId> { junction };
        foreach (var drop in yields)
        {
            for (var i = 0; i < drop.Count; i++)
            {
                var spawnTile = tile;
                var spawnJunction = junction;
                if (primary is not null)
                {
                    var scatter = FindScatterSpot(world, source, used);
                    if (scatter.Item2 is not { } freeJunction)
                    {
                        GiveOrDrop(world, npc, CreateYieldItem(world, drop.DefinitionId));
                        continue;
                    }

                    spawnTile = scatter.Item1;
                    spawnJunction = freeJunction;
                    used.Add(freeJunction);
                }

                var spawned = WorldObjectMutations.SpawnObject(world, drop.DefinitionId, fragment, spawnTile, spawnJunction);
                spawned.SpawnTick = world.Tick;
                // §59-склад: заявленный запас; без склада — ноль (расходники).
                spawned.ResourceAmount =
                    world.Content.ObjectDefinitions.TryGetValue(drop.DefinitionId, out var dropDef)
                        ? dropDef.StoredAmount(Content.StoredKind.Water)
                        : 0f;
                primary ??= spawned;
            }
        }

        return primary;
    }

    private static bool TryContinueWorldPlanAfterInteraction(
        WorldState world,
        NPCState npc,
        WorldObjectState target,
        InteractionType completed)
    {
        var completedIndex = -1;
        for (var i = npc.Plan.CurrentStepIndex; i < npc.Plan.Steps.Count; i++)
        {
            var step = npc.Plan.Steps[i];
            if (step.Type == PlanStepType.Interact && step.Interaction == completed)
            {
                completedIndex = i;
                break;
            }
        }

        if (completedIndex < 0)
        {
            return false;
        }

        var nextInteract = -1;
        for (var i = completedIndex + 1; i < npc.Plan.Steps.Count; i++)
        {
            if (npc.Plan.Steps[i].Type == PlanStepType.Interact)
            {
                nextInteract = i;
                break;
            }
        }

        if (nextInteract < 0)
        {
            return false;
        }

        npc.Plan.CurrentStepIndex = nextInteract;
        npc.Plan.TargetObjectId = target.Id;
        npc.Plan.TargetTile = target.Tile;
        var continuedJunction = npc.Plan.TargetJunctionId;
        if (continuedJunction is null && target.Junctions.Count > 0)
        {
            continuedJunction = target.Junctions[0];
        }

        npc.Plan.TargetJunctionId = continuedJunction;
        for (var i = nextInteract; i < npc.Plan.Steps.Count; i++)
        {
            if (npc.Plan.Steps[i].Type == PlanStepType.Interact)
            {
                npc.Plan.Steps[i].TargetObject = target.Id;
                npc.Plan.Steps[i].TargetJunction = continuedJunction;
            }
        }

        target.IsOccupied = true;
        target.CurrentUser = npc.Id;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        Trace.Emit(world, npc.Id, "PlanContinues",
            $"{completed} complete; next step={npc.Plan.Steps[nextInteract].Interaction} on {target.DefinitionId}");
        return true;
    }

    // Spec §54: a free junction on the harvested tile or a neighbour, skipping
    // junctions already claimed by earlier drops this call so the yields land
    // at DISTINCT points (the scattered look).
    private static (TileCoord, JunctionId?) FindScatterSpot(
        WorldState world, WorldObjectState source,
        System.Collections.Generic.HashSet<JunctionId> used)
    {
        var tiles = new System.Collections.Generic.List<TileCoord> { source.Tile };
        foreach (var neighbor in SpatialQueries.GetNeighbors(world, source.Tile))
        {
            tiles.Add(neighbor);
        }

        foreach (var tileCoord in tiles)
        {
            if (!SpatialQueries.IsTileWalkable(world, tileCoord) ||
                !world.Tiles.Items.TryGetValue(tileCoord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (used.Contains(junctionId) ||
                    !SpatialQueries.IsJunctionPassable(world, junctionId) ||
                    !SpatialQueries.IsJunctionFree(world, junctionId))
                {
                    continue;
                }

                return (tileCoord, junctionId);
            }
        }

        return (source.Tile, null);
    }

    // Spec 35.5: dropped items keep their instance state on the ground.
    internal static WorldObjectState DropItemAtFeet(WorldState world, NPCState npc, ItemInstance item)
    {
        if (TryFindDropSpotAtFeet(world, npc, out var dropTile, out var dropJunction))
        {
            var dropped = WorldObjectMutations.SpawnObject(
                world, item.DefinitionId, npc.Fragment, dropTile, dropJunction);
            dropped.Wetness = item.Wetness;
            dropped.Durability = item.Durability;
            dropped.ResourceAmount = item.ResourceAmount;
            dropped.Dirtiness = item.Dirtiness;
            dropped.Bloodiness = item.Bloodiness;
            return dropped;
        }

        return null;
    }

    private static bool TryFindDropSpotAtFeet(
        WorldState world,
        NPCState npc,
        out TileCoord tile,
        out JunctionId junction)
    {
        // Prefer a genuinely free ground junction near the actor so dropped
        // items become ordinary world objects immediately. The actor's current
        // junction is usually occupied by the actor, so keep it as a fallback
        // rather than the first choice.
        var source = new WorldObjectState
        {
            Tile = npc.Tile,
            Fragment = npc.Fragment
        };
        var used = new System.Collections.Generic.HashSet<JunctionId>();
        if (npc.CurrentJunction is { } current)
        {
            used.Add(current);
        }

        var scatter = FindScatterSpot(world, source, used);
        if (scatter.Item2 is { } freeJunction)
        {
            tile = scatter.Item1;
            junction = freeJunction;
            return true;
        }

        if (npc.CurrentJunction is { } fallback &&
            world.Junctions.Items.ContainsKey(fallback))
        {
            tile = npc.Tile;
            junction = fallback;
            return true;
        }

        if (world.Tiles.Items.TryGetValue(npc.Tile, out var tileState))
        {
            foreach (var candidate in tileState.Junctions)
            {
                if (SpatialQueries.IsJunctionPassable(world, candidate))
                {
                    tile = npc.Tile;
                    junction = candidate;
                    return true;
                }
            }
        }

        tile = npc.Tile;
        junction = default;
        return false;
    }

    // Spec §52: take a garment off the body and lay it on the ground carrying
    // its pockets. Any pocket item that no longer fits the (now smaller) pack
    // rides down inside the dropped garment — the NPC still knows where its
    // bottle is and can fetch it later without dressing.
    internal static WorldObjectState DropGarmentWithContents(
        WorldState world, NPCState npc, ItemInstance garment)
    {
        _garmentSpillScratch.Clear();
        var inv = npc.Inventory;
        var guard = 0;
        while (inv.UsedSlots > inv.Capacity && guard++ < 64)
        {
            var victim = InventoryMath.LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            inv.Items.Remove(victim);
            _garmentSpillScratch.Add(victim);
        }

        var dropped = DropItemAtFeet(world, npc, garment);
        if (dropped != null && _garmentSpillScratch.Count > 0)
        {
            dropped.Contents.AddRange(_garmentSpillScratch);
            Trace.Emit(world, npc.Id, "StashedInGarment",
                $"{garment.DefinitionId} holds [{string.Join(",", _garmentSpillScratch)}]");
        }

        return dropped;
    }

    private static readonly System.Collections.Generic.List<ItemInstance> _garmentSpillScratch = new();

    private static readonly System.Collections.Generic.List<ItemInstance> _dressPourScratch = new();

    private static readonly System.Collections.Generic.List<string> _stashRecoverScratch = new();

    // Spec §52: rifle a dropped garment's pockets for the tools the NPC lacks —
    // the tools come home, the garment (and any non-tool stash) stays on the
    // ground. This is how a knife left in an undressed jacket comes back
    // without re-dressing (seed 1104049673: both knives rode a doffed jacket
    // to the ground and their owner died of thirst two hexes away).
    private static void RecoverStashedTools(WorldState world, NPCState npc, WorldObjectState stash)
    {
        _stashRecoverScratch.Clear();
        for (var i = stash.Contents.Count - 1; i >= 0; i--)
        {
            var item = stash.Contents[i];
            if (npc.Inventory.Items.Contains(item.DefinitionId) ||
                !world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) ||
                !def.Tags.Contains("Tool") ||
                !InventoryMath.MakeRoomFor(world, npc, item.DefinitionId))
            {
                continue;
            }

            stash.Contents.RemoveAt(i);
            npc.Inventory.Items.Add(item);
            _stashRecoverScratch.Add(item.DefinitionId);
        }

        stash.IsOccupied = false;
        stash.CurrentUser = null;
        Trace.Emit(world, npc.Id, "StashRecovered",
            $"{stash.DefinitionId} pockets returned [{string.Join(",", _stashRecoverScratch)}] " +
            $"left [{string.Join(",", stash.Contents)}] " +
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
    }

    // §54.14 (r2): take ONE cooked chunk off the spit into the pack — the fire
    // (and any still-roasting raw meat) stays. Housemates share the crossbar,
    // so a single grab per trip keeps the spit a communal larder.
    private static void TakeMeatFromSpit(WorldState world, NPCState npc, WorldObjectState fire)
    {
        for (var i = 0; i < fire.Contents.Count; i++)
        {
            var item = fire.Contents[i];
            if (item.DefinitionId != "food.meat_cooked")
            {
                continue;
            }

            if (!InventoryMath.MakeRoomFor(world, npc, item.DefinitionId))
            {
                break; // no room — leave it hanging
            }

            fire.Contents.RemoveAt(i);
            npc.Inventory.Items.Add(item);
            fire.IsOccupied = false;
            fire.CurrentUser = null;
            Trace.Emit(world, npc.Id, "MeatTakenFromSpit",
                $"food.meat_cooked off the spit at Tile={fire.Tile.Q},{fire.Tile.R} " +
                $"left hanging={BuildSiteMath.HangingMeat(fire, "food.meat_cooked")} " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
            return;
        }

        fire.IsOccupied = false;
        fire.CurrentUser = null;
        Trace.Emit(world, npc.Id, "SpitTakeFailed",
            $"no takeable cooked meat on the spit at Tile={fire.Tile.Q},{fire.Tile.R}");
    }

    // Spec 31A.5A: take off a worn item in place; it drops to the world at
    // the NPC's feet, retrievable by anyone.
    // §Wardrobe-anim: 8 ticks = 2.0s at 0.25s/tick — matches the dress window.
    private const int UndressDurationTicks = 8;

    // §Wardrobe-anim: the fraction of a dress/undress window at which the
    // garment changes hands. Dressing: gather (before) -> don the piece (after).
    // Undressing: doff the piece (before) -> gather it up off the body (after),
    // so the garment leaves the body here and is only dropped at the very end.
    internal const float WardrobeHandoffFraction = 0.5f;

    // Spec 29G: drive a ground rest plan — walk to the reserved spot (the
    // PathfindingSystem does the walking), then rest in place.
    private static void RunGroundRestPlan(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                PlanInterruption.Abort(world, npc, "Ground rest spot unreachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var atTarget = npc.Plan.TargetJunctionId is { } target &&
                npc.CurrentJunction is { } current && current.Equals(target);
            if (!atTarget)
            {
                return;
            }
        }

        if (step.Type == PlanStepType.GroundSit)
        {
            RunGroundRest(world, npc, step, InteractionType.Sit, 70,
                step.TargetJunction is { } lg && PlanningSystem.IsLedgeId(world, lg)
                    ? SimBalance.GroundSitComfortLedge : SimBalance.GroundSitComfort,
                SimBalance.GroundSitEnergy);
        }
        else if (step.Type == PlanStepType.GroundCool)
        {
            // Spec 35.4: dwell in shade/water shedding heat — no comfort/energy
            // gain, the cooling is delivered for free by TemperatureSystem now
            // that she's standing on a genuinely cool tile.
            RunGroundCool(world, npc, step);
        }
        else
        {
            // Sleep restores as well as a bed (a night is a night) — the
            // bed's edge is comfort, not energy. +0.35 energy here produced
            // a poverty trap: 160 naps/soak and no time to live.
            RunGroundRest(world, npc, step, InteractionType.Sleep, 100, 0f, SimBalance.GroundSleepEnergy); // spec 42
        }
    }

    // Spec 29G: rest on the land — a timed in-place interaction with no
    // object. Lying claims the body's footprint so housemates path around.
    private static void RunGroundRest(
        WorldState world, NPCState npc, PlanStep step,
        InteractionType kind, int durationTicks, float comfort, float energy)
    {
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (step.TargetJunction is { } reserved &&
                !SpatialMutations.TryReserveJunction(
                    world, reserved, npc.Id, world.Tick, durationTicks + 8))
            {
                PlanInterruption.Abort(world, npc, "Ground rest edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kind;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);

                if (kind == InteractionType.Sit && world.Junctions.Items.TryGetValue(spot, out var ledge) &&
                    PlanningSystem.TryGetEdgeSeatGeometry(
                        world, ledge, waterOnly: false, out var standTile, out var facing))
                {
                    PlaceAtEdge(world, npc, ledge, standTile, facing);
                }

                if (kind == InteractionType.Sleep)
                {
                    ClaimLyingFootprint(world, npc, spot);
                }
            }

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{kind} on the ground Duration={durationTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // Spec 29C.9: comfort/energy recover gradually while she rests — the
        // whole point of "you can watch it fill", not a jump on standing up.
        var restShare = durationTicks > 0 ? 1f / durationTicks : 1f;
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfort * restShare);
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + energy * restShare);

        var interruptedSleep = kind == InteractionType.Sleep && HasSleepInterrupt(world, npc);
        if (!interruptedSleep && npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        if (interruptedSleep)
        {
            Trace.Emit(world, npc.Id, "SleepInterrupted",
                $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                $"Danger={npc.Memory.Dangers.Count}");
        }

        // Spec §49: sleep the night in ONE continuous lie. Instead of ending the
        // block, standing (wake-grace + get-up clip), re-planning a spot and
        // dropping back down — the "empty get-up" churn that was 57% of night
        // get-ups — re-arm the block in place, holding the footprint claim. She
        // only truly wakes when rested enough, dawn breaks, or a real need
        // (hunger/thirst/cold/danger) crosses its threshold and the decision
        // system takes over.
        if (kind == InteractionType.Sleep && ShouldKeepSleeping(world, npc))
        {
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;
            Trace.Emit(world, npc.Id, "SleepContinued",
                $"Energy={npc.Needs.Energy:F2} Comfort={npc.Needs.Comfort:F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, kind == InteractionType.Sleep ? "GroundSleptWell" : "GroundSatDown",
            $"Comfort+{comfort:F2} Energy+{energy:F2}");

        // Spec 41.5: wake up standing still for a beat — no sprinting off
        // the grass; the get-up clip plays out during the grace.
        if (kind == InteractionType.Sleep)
        {
            npc.Mind.WakeGraceUntilTick = world.Tick + 12;
        }

        if (kind == InteractionType.Sit)
        {
            npc.Mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = GoalType.Sit,
                EndTick = world.Tick + 240
            });
        }

        // Canonical cycle reset (same as InteractionCompleted): Execution
        // back to None or the next interaction's start gate never opens.
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (ground rest done)");
    }

    // Spec §49: should a finished sleep block re-arm in place (keep lying)
    // rather than stand and re-plan? Yes while she is still tired (nearly full
    // energy at night, or genuinely spent by day) AND no real need has crossed
    // its action threshold — the same thresholds at which Eat/Drink/Dress
    // become attractive, so she wakes exactly when there is something to do.
    private static float SleepWakeEnergyDay => SimBalance.SleepEnergyThreshold;
    private static float SleepInterruptHunger => SimBalance.SleepInterruptHunger;
    private static float SleepInterruptThirst => SimBalance.SleepInterruptThirst;
    // NOTE: cold is deliberately NOT a wake trigger — mild cold at night is the
    // norm and she usually can't fix it, so waking just produced the "empty
    // get-up" churn; sleeping through it is what a real body does (§49.1).
    private static bool ShouldKeepSleeping(WorldState world, NPCState npc)
    {
        if (!Spec49.Rearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the sleep — then the decision
        // system takes over. Everything else: keep lying. NOTE: cold is NOT a
        // wake trigger — a near-naked girl on a 6° night sits at max thermal
        // discomfort she usually can't fix, so waking her only produced the
        // "empty get-up" churn; the cold HP hit lands whether she's up or lying,
        // and lying still conserves. (A fire she could tend is a daytime chore.)
        if (HasSleepInterrupt(world, npc))
        {
            return false;
        }

        // Spec §49: sleep THROUGH the night in one lie (the user's ask — "let
        // them sleep more") — no energy cap after dark. By day, only nap while
        // genuinely tired.
        var night = world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
        return night || npc.Needs.Energy < SleepWakeEnergyDay;
    }

    // §49-parity: the DECISION layer reads this too — going to sleep while an
    // interrupt condition is already true produced the lie-down/stand-up loop
    // (Molly, thirst 0.79 ≥ 0.6: the first sleep tick woke her, the auction
    // put her right back to bed, forever).
    internal static bool HasSleepInterrupt(WorldState world, NPCState npc) =>
        world.Tick < npc.Mind.AdrenalineUntilTick ||
        npc.Memory.Dangers.Count > 0 ||
        npc.Needs.Hunger >= SleepInterruptHunger ||
        npc.Needs.Thirst >= SleepInterruptThirst;

    // Spec 35.4: dwell in the shade / shallows shedding heat. This is the
    // cool-off twin of RunGroundRest — a timed in-place interaction with no
    // object and no comfort/energy payoff; the cooling itself is delivered by
    // TemperatureSystem because the plan parked her on a genuinely cool tile
    // (shaded or water). At the end of each beat it re-arms in place (like the
    // sleep re-arm) until she has actually cooled, a more urgent need crosses,
    // or the safety cap trips — instead of completing→None and re-winning the
    // goal at zero margin every tick (the old None→CoolOff churn, ~40% of all).
    private static void RunGroundCool(WorldState world, NPCState npc, PlanStep step)
    {
        var bathing = npc.Plan.Goal == GoalType.Bathe;
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);
            }

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{(bathing ? "Bathe" : "CoolOff")} Duration={Spec49.CoolOffDwellTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Re-arm the dwell in place (hold the junction occupancy + reservation)
        // while still hot and nothing more urgent calls — bounded by CoolOffMaxRearms
        // so a fallback tile that never actually cools can't freeze her here forever.
        if (Spec49.CoolRearm &&
            npc.Mind.CoolRearmCount < Spec49.CoolOffMaxRearms &&
            (bathing ? ShouldKeepBathing(npc) : ShouldKeepCooling(world, npc)))
        {
            npc.Mind.CoolRearmCount++;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;
            Trace.Emit(world, npc.Id, bathing ? "BatheContinued" : "CoolContinued",
                $"Rearm={npc.Mind.CoolRearmCount} Hygiene={npc.Needs.Hygiene:F2} " +
                $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, bathing ? "Bathed" : "CooledOff",
            $"Hygiene={npc.Needs.Hygiene:F2} ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2} " +
            $"Rearms={npc.Mind.CoolRearmCount}");

        // A short refractory window so she doesn't instantly re-select CoolOff even
        // if discomfort still hovers just under the clear edge (mirrors Sit's 240t).
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = bathing ? GoalType.Bathe : GoalType.CoolOff,
            EndTick = world.Tick + SimBalance.CoolOffSettleTicks
        });

        // Canonical cycle reset (same as RunGroundRest / InteractionCompleted).
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.CoolRearmCount = 0;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (cool-off done)");
    }

    private static bool ShouldKeepBathing(NPCState npc) =>
        npc.Needs.Hygiene < 0.95f || EquipmentMath.AverageDirtiness(npc) > 0.05f;

    private static void RunPrepareBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } shore || !current.Equals(shore))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Undress)
        {
            var garment = npc.Execution.HeldGarment;
            if (garment is null && npc.Plan.TargetItemDefinitionId is { } definitionId)
            {
                for (var i = npc.WornItems.Count - 1; i >= 0; i--)
                {
                    if (npc.WornItems[i].DefinitionId == definitionId)
                    {
                        garment = npc.WornItems[i];
                        break;
                    }
                }
            }

            if (garment is null)
            {
                PlanInterruption.Abort(world, npc, "Bathe: active garment disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)(world.Tick - npc.Execution.StartTick) / total : 1f;
            if (npc.Execution.HeldGarment is null && progress >= WardrobeHandoffFraction)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
            }

            if (world.Tick < npc.Execution.EndTick)
            {
                return;
            }

            npc.WornItems.Remove(garment);
            DropGarmentWithContents(world, npc, garment);
            npc.Execution.HeldGarment = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Plan.TargetItemDefinitionId = null;
            EquipmentMath.Recalculate(world, npc);
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            var garment = npc.WornItems[npc.WornItems.Count - 1];
            npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            npc.Execution.HeldGarment = null;
            return;
        }

        Junction swim = null;
        var bestDistance = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Water) ||
                !Connectivity.Reachable(world, shore, junction.Id))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                swim = junction;
            }
        }

        if (swim is null)
        {
            PlanInterruption.Abort(world, npc, "Bathe: no reachable water junction");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        SpatialMutations.ReleaseJunctionReservation(world, shore, npc.Id);
        npc.Plan.TargetJunctionId = swim.Id;
        npc.Plan.TargetTile = swim.Tiles[0];
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = swim.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.SwimBathe, TargetJunction = swim.Id });
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        Trace.Emit(world, npc.Id, "BatheReady", $"Naked; swimming to {swim.Id.Value}");
    }

    private static void RunSwimBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            PlanInterruption.Abort(world, npc, "Bathe requires complete undressing");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.BatheDurationTicks;
            Trace.Emit(world, npc.Id, "BatheStarted",
                $"Duration={SimBalance.BatheDurationTicks} ticks (one game hour)");
            return;
        }

        npc.Needs.Hygiene = MathUtil.Clamp01(npc.Needs.Hygiene +
            1f / SimBalance.BatheDurationTicks);
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        npc.Needs.Hygiene = 1f;
        FinishPersonalCare(world, npc, target, GoalType.Bathe, "Bathed");
    }

    private static void RunWashClothes(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (npc.Plan.TargetObjectId is not { } objectId ||
            !world.Entities.Objects.TryGetValue(objectId, out var garment) ||
            !world.Junctions.Items.TryGetValue(target, out var edge) ||
            !PlanningSystem.TryGetEdgeSeatGeometry(
                world, edge, waterOnly: true, out var standTile, out var facing))
        {
            SpatialMutations.FreeJunction(world, target, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            PlanInterruption.Abort(world, npc, "WashClothes edge or garment disappeared");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Hold the exact edge midpoint and its water-facing normal throughout
        // the gathering clip; other systems cannot slowly turn the washer away.
        PlaceAtEdge(world, npc, edge, standTile, facing);

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!SpatialMutations.TryReserveJunction(world, target, npc.Id, world.Tick,
                    SimBalance.WashClothesDurationTicks + 8))
            {
                PlanInterruption.Abort(world, npc, "WashClothes edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            SpatialMutations.OccupyJunction(world, target, npc.Id);
            garment.Wetness = 1f;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.WashClothes;
            npc.Execution.TargetObject = objectId;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.WashClothesDurationTicks;
            npc.Execution.HeldGarment = new ItemInstance(garment.DefinitionId)
            {
                Wetness = 1f,
                Durability = garment.Durability,
                Dirtiness = garment.Dirtiness,
                Bloodiness = garment.Bloodiness
            };
            return;
        }

        // Dirtiness is the combined contamination score. Bloodiness remains a
        // separate visual layer, but fades over the same washing progress.
        var remainingTicks = System.Math.Max(0, npc.Execution.EndTick - world.Tick);
        var retainedContamination = remainingTicks / (remainingTicks + 1f);
        garment.Dirtiness = MathUtil.Clamp01(garment.Dirtiness * retainedContamination);
        garment.Bloodiness = MathUtil.Clamp01(garment.Bloodiness * retainedContamination);
        garment.Wetness = 1f;
        if (npc.Execution.HeldGarment is { } held)
        {
            held.Dirtiness = garment.Dirtiness;
            held.Bloodiness = garment.Bloodiness;
            held.Wetness = garment.Wetness;
        }
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        garment.Dirtiness = 0f;
        garment.Bloodiness = 0f;
        garment.Wetness = 1f;
        npc.Execution.HeldGarment = null;
        SpatialMutations.FreeJunction(world, target, npc.Id);
        FinishPersonalCare(world, npc, step.TargetJunction, GoalType.WashClothes,
            $"ClothesWashed {garment.DefinitionId} (fully wet)");
    }

    private static void FinishPersonalCare(WorldState world, NPCState npc, JunctionId? junction,
        GoalType goal, string trace)
    {
        if (junction is { } occupied)
        {
            SpatialMutations.ReleaseJunctionReservation(world, occupied, npc.Id);
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = goal, EndTick = world.Tick + 40 });
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        Trace.Emit(world, npc.Id, trace, $"Hygiene={npc.Needs.Hygiene:F2}");
    }

    // Spec 35.4: should the finished cool-off beat re-arm in place? Yes while she
    // is still hot AND no more-urgent need/threat has crossed its threshold —
    // reusing the sleep-interrupt thresholds so she leaves the shade exactly when
    // there's something better to do. Stops once cooled below the clear edge.
    private static bool ShouldKeepCooling(WorldState world, NPCState npc)
    {
        if (!Spec49.CoolRearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the dwell — the decision system
        // then takes over (she'll re-pick CoolOff only if still overheated and the
        // settle cooldown has lapsed).
        if (npc.Memory.Dangers.Count > 0 ||
            npc.Needs.Hunger >= SleepInterruptHunger ||
            npc.Needs.Thirst >= SleepInterruptThirst)
        {
            return false;
        }

        // Cooled enough: both the heat discomfort and the sun-exposure meter have
        // fallen below their clear edges (hysteresis vs the 0.35 entry).
        if (npc.Needs.ThermalDiscomfort < SimBalance.CoolOffClearThreshold &&
            npc.SunExposure < SimBalance.CoolOffSunClear)
        {
            return false;
        }

        return true;
    }

    // Spec 29G: the lying body covers junctions within half a hex radius.
    internal static void ClaimLyingFootprint(WorldState world, NPCState npc, JunctionId center)
    {
        npc.ClaimedJunctions.Clear();
        if (!world.Junctions.Items.TryGetValue(center, out var origin))
        {
            return;
        }

        var radius = HexSpatialMath.HexRadius * 0.5f;
        var radiusSq = radius * radius;
        foreach (var coord in origin.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || junction.Blocked)
                {
                    continue;
                }

                var dx = junction.WorldPosition.X - origin.WorldPosition.X;
                var dy = junction.WorldPosition.Y - origin.WorldPosition.Y;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    npc.ClaimedJunctions.Add(junctionId);
                }
            }
        }
    }

    internal static void ReleaseClaims(WorldState world, NPCState npc)
    {
        npc.ClaimedJunctions.Clear();
    }

    private static void PlaceAtEdge(
        WorldState world, NPCState npc, Junction edge, TileCoord standTile, Float2 facing)
    {
        if (npc.Tile != standTile)
        {
            var previous = npc.Tile;
            npc.Tile = standTile;
            SpatialMutations.MoveEntityToTile(world, npc.Id, previous, standTile);
        }

        npc.Position = edge.WorldPosition;
        npc.Movement.DesiredDirection = facing;
        npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(facing);
        npc.RotationDegrees = npc.Movement.DesiredRotationDegrees;
    }

    private static void RunUndressItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (itemId is null || !npc.WornItems.Contains(itemId))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "ExecFailed",
                    $"UndressItem: '{itemId ?? "-"}' is not worn");
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.TargetObject = null;
            npc.Execution.HeldGarment = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Undress -> {itemId} Duration={UndressDurationTicks}ticks");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            // §Wardrobe-anim beat 1 -> 2: at the handoff the piece comes OFF the
            // body and INTO the hand — warmth/armor drop here — but it isn't laid
            // on the floor until the gather beat finishes (see below).
            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var elapsed = world.Tick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)elapsed / total : 1f;
            if (npc.Execution.HeldGarment is null &&
                progress >= WardrobeHandoffFraction &&
                itemId is not null && npc.WornItems.Contains(itemId))
            {
                var doffed = npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                    new ItemInstance(itemId);
                npc.WornItems.Remove(doffed);
                EquipmentMath.Recalculate(world, npc);
                npc.Execution.HeldGarment = doffed;
                Trace.Emit(world, npc.Id, "GarmentInHand",
                    $"Undress {itemId} doffed to hand " +
                    $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
            }

            if (npc.Execution.EndTick - world.Tick > 0)
            {
                return;
            }

            // Beat 2 finished: the garment gathered up off the body lands on
            // the floor (preserving the doffed instance's wetness/durability).
            var wornItem = npc.Execution.HeldGarment ??
                npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                new ItemInstance(itemId);
            npc.WornItems.Remove(wornItem);
            npc.Execution.HeldGarment = null;
            EquipmentMath.Recalculate(world, npc);
            // Spec §52: the garment carries down whatever pocket items no longer
            // fit — they wait inside it on the ground, retrievable later.
            DropGarmentWithContents(world, npc, wornItem);

            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            Trace.Emit(world, npc.Id, "ItemUndressed",
                $"{itemId} dropped at Tile={npc.Tile.Q},{npc.Tile.R} " +
                $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed (undressed)");
        }
    }

    private static void RunDropInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "DropInventoryItem: no target item");
            return;
        }

        ItemInstance? item = null;
        foreach (var carried in npc.Inventory.Items)
        {
            if (carried.DefinitionId == itemId)
            {
                item = carried;
                break;
            }
        }

        if (item is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"DropInventoryItem: '{itemId}' not in inventory");
            return;
        }

        npc.Inventory.Items.Remove(item);
        var dropped = DropItemAtFeet(world, npc, item);
        if (dropped is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"DropInventoryItem: no drop junction for '{itemId}'");
            return;
        }

        npc.Plan.TargetObjectId = dropped.Id;
        npc.Plan.TargetTile = dropped.Tile;
        npc.Plan.TargetJunctionId = dropped.Junctions.Count > 0 ? dropped.Junctions[0] : npc.CurrentJunction;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.Steps.RemoveAt(0);
        npc.Plan.CurrentStepIndex = 0;

        foreach (var step in npc.Plan.Steps)
        {
            if (step.Type == PlanStepType.Interact)
            {
                step.TargetObject = dropped.Id;
                step.TargetJunction = npc.Plan.TargetJunctionId;
            }
        }

        Trace.Emit(world, npc.Id, "ItemDropped",
            $"{itemId} placed on ground Obj={dropped.Id.Value} for {npc.Plan.Goal}");
    }

    // In-place consumption from inventory (spec 29B.3): no world object,
    // no junction reservation, executable wherever the NPC stands.
    private static void RunConsumeInventoryItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null ||
            !world.Content.ObjectDefinitions.TryGetValue(itemId, out var itemDefinition))
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: item '{itemId ?? "-"}' not in inventory or unknown definition");
            return;
        }

        // §55: in-place consume now covers both Eat (food) and Drink (crack a
        // coconut). The verb comes from the plan step, so one handler serves
        // both; a Drink can carry Yields (the opened-coconut husk).
        var verb = npc.Plan.Steps.Count > 0 && npc.Plan.Steps[0].Interaction.HasValue
            ? npc.Plan.Steps[0].Interaction.Value
            : InteractionType.Eat;
        var interaction = ResolveInteraction(itemDefinition, verb);
        if (interaction is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' has no {verb} interaction");
            return;
        }

        var item = FindConsumableInventoryItem(npc, itemId, verb, itemDefinition);
        if (item is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' not available for {verb}");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = verb;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + interaction.DurationTicks;

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{verb} (inventory) -> {itemId} " +
                $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                $"EndTick={npc.Execution.EndTick} " +
                $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
                $"T={interaction.Effects.ThirstDelta:+0.00;-0.00} " +
                $"C={interaction.Effects.ComfortDelta:+0.00;-0.00}]");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            var remaining = npc.Execution.EndTick - world.Tick;
            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            if (remaining > 0)
            {
                var progress = total > 0 ? 1f - (float)remaining / total : 1f;
                // Spec 29C.9: hunger drops mouthful by mouthful, not in a jump.
                if (total > 0)
                {
                    ApplyEffectsScaled(npc, interaction.Effects, 1f / total);
                }

                if (SimTrace.Verbose)
                {
                    Trace.Emit(world, npc.Id, "ExecProgress",
                        $"{verb} (inventory) Progress={progress:P0} " +
                        $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                }

                return;
            }

            var needsBefore = Trace.FormatNeeds(npc.Needs);
            ApplyEffectsScaled(npc, interaction.Effects, total > 0 ? 1f / total : 1f);
            if (IsPortableCoconutDrink(itemDefinition, verb))
            {
                item.ResourceAmount = System.MathF.Max(0f, item.ResourceAmount - 1f);
            }
            else
            {
                npc.Inventory.Items.Remove(item);
                // §55: a consumed item may transform rather than vanish — cracking a
                // coconut (Drink) yields the opened husk straight into the hand.
                foreach (var yield in interaction.Yields)
                {
                    for (var n = 0; n < yield.Count; n++)
                    {
                        npc.Inventory.Items.Add(yield.DefinitionId);
                    }
                }
            }
            var needsAfter = Trace.FormatNeeds(npc.Needs);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;

            Trace.Emit(world, npc.Id, "ItemConsumed",
                $"{itemId} {verb} from inventory " +
                $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}] " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]" +
                (IsPortableCoconutDrink(itemDefinition, verb)
                    ? $" CoconutWaterLeft={item.ResourceAmount:F0}"
                    : string.Empty));

            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;

            Trace.Emit(world, npc.Id, "CycleReset",
                "Goal->None Plan->Completed Execution->Cleared (ate from inventory)");
        }
    }

    private static ItemInstance? FindConsumableInventoryItem(
        NPCState npc,
        string definitionId,
        InteractionType verb,
        ObjectDefinition definition)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId != definitionId)
            {
                continue;
            }

            if (IsPortableCoconutDrink(definition, verb) && item.ResourceAmount <= 0f)
            {
                continue;
            }

            return item;
        }

        return null;
    }

    private static bool IsPortableCoconutDrink(ObjectDefinition definition, InteractionType verb) =>
        verb == InteractionType.Drink && definition.Tags.Contains("CoconutWater");

    // Spec 29H: drink in place from the carried bottle — thirst quenched,
    // raw water carries the 30 % sickness roll, then the bottle empties.
    private static int DrinkBottleDurationTicks => SimBalance.DrinkBottleDurationTicks;
    // Spec §49: raw-water gut-rot. A bout adds SicknessDamagePerBout to the
    // torso-damage budget (matches the old instant lump) and shows the 🤢 icon
    // for SicknessDurationTicks (~6 game-hours). The budget is capped so
    // overlapping bouts can't grind the torso into the ground.
    private static int SicknessDurationTicks => SimBalance.SicknessDurationTicks;
    private static float SicknessDamagePerBout => SimBalance.SicknessDamagePerBout;
    private static float SicknessDamageBudgetCap => SimBalance.SicknessDamageBudgetCap;

    // §gear-craft: the item-output arm of the craft completion — shared by the
    // at-station path and the in-place path. Returns false for placed-object
    // crafts (rack/bed/tent), which the station switch handles.
    private static bool GrantCraftOutput(WorldState world, NPCState npc, GoalType goal)
    {
        switch (goal)
        {
            case GoalType.CraftBandage:
                npc.Needs.Bandages++;
                npc.Needs.HerbalBandages++; // spec 44: gathered plantain -> leaf-wrap decal
                Trace.Emit(world, npc.Id, "BandageCrafted",
                    $"Bandages={npc.Needs.Bandages} Herbal={npc.Needs.HerbalBandages}");
                return true;
            case GoalType.CraftSpear:
                GiveOrDrop(world, npc, "tool.spear");
                Trace.Emit(world, npc.Id, "CraftedSpear",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            // §54.14 (r2): CookMeat no longer grants here — the raw chunk is
            // hung on the spit at the station-craft arm and FireSystem roasts
            // it over time (a campfire-station recipe never crafts in place).
            case GoalType.CraftLeather:
                ResolveWearConflicts(world, npc, "clothing.leather_pants");
                npc.WornItems.Add("clothing.leather_pants");
                EquipmentMath.Recalculate(world, npc);
                Trace.Emit(world, npc.Id, "CraftedLeather",
                    $"Pants worn. Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                return true;
            case GoalType.CraftAxe:
                GiveOrDrop(world, npc, "tool.axe_stone");
                Trace.Emit(world, npc.Id, "CraftedAxe",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftPickaxe:
                GiveOrDrop(world, npc, "tool.pickaxe_stone");
                Trace.Emit(world, npc.Id, "CraftedPickaxe",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftBow:
                GiveOrDrop(world, npc, "tool.bow");
                Trace.Emit(world, npc.Id, "CraftedBow",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftArrows:
                GiveOrDrop(world, npc, "resource.arrow");
                GiveOrDrop(world, npc, "resource.arrow");
                GiveOrDrop(world, npc, "resource.arrow");
                Trace.Emit(world, npc.Id, "CraftedArrows",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftRope:
                GiveOrDrop(world, npc, "resource.rope");
                Trace.Emit(world, npc.Id, "CraftedRope",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftCloth:
                GiveOrDrop(world, npc, "resource.cloth");
                Trace.Emit(world, npc.Id, "CraftedCloth",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            case GoalType.CraftKnife:
                GiveOrDrop(world, npc, "tool.knife");
                Trace.Emit(world, npc.Id, "CraftedKnife",
                    $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                return true;
            default:
                return false;
        }
    }

    // §gear-craft v2: the in-place craft is a STAGED ritual, not a bare timer.
    //   1. Layout — the recipe inputs leave the pack and are laid out on the
    //      ground at her feet as ordinary world items (everyone sees the work
    //      spread out).
    //   2. Work — the Craft beat, twice the old workbench window (12 -> 24
    //      ticks = 6 s); the view kneels her into the craft-work clip.
    //   3. Take — the ingredients are used up, the finished item appears ON
    //      THE GROUND, and a short PickUp beat stoops her down to take it
    //      into hand/pack.
    // An interrupted craft leaves the laid-out pieces lying — they are normal
    // world objects, recoverable by the usual gather logic (no dupes: the
    // inputs left the inventory at layout time).
    private const int CraftInPlaceDurationTicks = 24;
    private const int CraftTakeDurationTicks = 6;

    // §gear-craft v2: which inventory ITEMS the craft lays on the ground for
    // the take beat. Non-item outputs (the bandage counter, leather worn
    // straight onto the body) return null and grant instantly at work's end.
    private static string[] CraftGroundOutputs(GoalType goal) => goal switch
    {
        GoalType.CraftSpear => new[] { "tool.spear" },
        GoalType.CraftAxe => new[] { "tool.axe_stone" },
        GoalType.CraftPickaxe => new[] { "tool.pickaxe_stone" },
        GoalType.CraftKnife => new[] { "tool.knife" },
        GoalType.CraftBow => new[] { "tool.bow" },
        GoalType.CraftArrows => new[] { "resource.arrow", "resource.arrow", "resource.arrow" },
        GoalType.CraftRope => new[] { "resource.rope" },
        GoalType.CraftCloth => new[] { "resource.cloth" },
        _ => null
    };

    // The legacy per-goal trace names, kept stable for soak metrics.
    private static string CraftedTraceName(GoalType goal) => goal switch
    {
        GoalType.CraftSpear => "CraftedSpear",
        GoalType.CraftAxe => "CraftedAxe",
        GoalType.CraftPickaxe => "CraftedPickaxe",
        GoalType.CraftKnife => "CraftedKnife",
        GoalType.CraftBow => "CraftedBow",
        GoalType.CraftArrows => "CraftedArrows",
        GoalType.CraftRope => "CraftedRope",
        GoalType.CraftCloth => "CraftedCloth",
        _ => "CraftedItem"
    };

    private static void RunCraftInPlace(WorldState world, NPCState npc)
    {
        var goal = npc.Plan.Goal != GoalType.None ? npc.Plan.Goal : npc.Mind.CurrentGoal;
        if (!Content.RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe))
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", $"CraftInPlace: no recipe for {goal}");
            return;
        }

        // Beat 1 — layout: the inputs leave the pack and land on the ground.
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            foreach (var ing in recipe.Inputs)
            {
                if (DecisionSystem.CountInventory(npc, ing.Id) < ing.Count)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "ExecFailed",
                        $"CraftInPlace {goal}: missing {ing.Id} x{ing.Count}");
                    return;
                }
            }

            npc.Execution.CraftLayout.Clear();
            foreach (var ing in recipe.Inputs)
            {
                for (var i = 0; i < ing.Count; i++)
                {
                    var index = npc.Inventory.Items.IndexOf(ing.Id);
                    if (index < 0)
                    {
                        continue;
                    }

                    var input = npc.Inventory.Items[index];
                    npc.Inventory.Items.RemoveAt(index);
                    var laid = DropItemAtFeet(world, npc, input);
                    if (laid != null)
                    {
                        // Tracked: despawned (used up) when the work beat ends.
                        npc.Execution.CraftLayout.Add(laid.Id);
                    }
                    // No free spot: the piece stays in her lap — already paid,
                    // just never visible on the ground.
                }
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Craft;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + CraftInPlaceDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"CraftInPlace {goal} Duration={CraftInPlaceDurationTicks}ticks " +
                $"LaidOut={npc.Execution.CraftLayout.Count}");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress ||
            npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Beat 2 done — the work window just ended: the laid-out ingredients
        // are used up and the finished item lands on the ground beside her.
        if (npc.Execution.CurrentInteraction == InteractionType.Craft)
        {
            foreach (var laidId in npc.Execution.CraftLayout)
            {
                WorldObjectMutations.DespawnObject(world, laidId);
            }

            npc.Execution.CraftLayout.Clear();

            var outputs = CraftGroundOutputs(goal);
            if (outputs == null)
            {
                // Non-item output: grant instantly, no take beat.
                GrantCraftOutput(world, npc, goal);
                FinishCraftInPlace(world, npc, goal);
                return;
            }

            foreach (var outputId in outputs)
            {
                var crafted = DropItemAtFeet(world, npc, CreateYieldItem(world, outputId));
                if (crafted != null)
                {
                    npc.Execution.CraftLayout.Add(crafted.Id);
                }
                else
                {
                    // Nowhere to lay it — straight into the pack.
                    GiveOrDrop(world, npc, CreateYieldItem(world, outputId));
                }
            }

            npc.Execution.CurrentInteraction = InteractionType.PickUp;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + CraftTakeDurationTicks;
            Trace.Emit(world, npc.Id, "CraftOutputLaid",
                $"{goal} -> [{string.Join(",", outputs)}] on the ground; " +
                $"take in {CraftTakeDurationTicks}ticks");
            return;
        }

        // Beat 3 done — she stoops and takes the finished item into the pack.
        foreach (var craftedId in npc.Execution.CraftLayout)
        {
            if (!world.Entities.Objects.TryGetValue(craftedId, out var crafted))
            {
                continue; // somebody took it first — the craft still ends
            }

            GiveOrDrop(world, npc, new ItemInstance(crafted.DefinitionId)
            {
                Wetness = crafted.Wetness,
                Durability = crafted.Durability,
                ResourceAmount = crafted.ResourceAmount,
                Dirtiness = crafted.Dirtiness,
                Bloodiness = crafted.Bloodiness
            });
            WorldObjectMutations.DespawnObject(world, craftedId);
        }

        npc.Execution.CraftLayout.Clear();
        Trace.Emit(world, npc.Id, CraftedTraceName(goal),
            $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
        FinishCraftInPlace(world, npc, goal);
    }

    private static void FinishCraftInPlace(WorldState world, NPCState npc, GoalType goal)
    {
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        Trace.Emit(world, npc.Id, "CycleReset",
            $"Goal->None Plan->Completed (crafted {goal} in place)");
    }

    private static void RunDrinkBottle(WorldState world, NPCState npc)
    {
        if (npc.BottleWater == WaterKind.None)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed", "DrinkBottle: the bottle is empty");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Drink;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + DrinkBottleDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Drink (bottle:{npc.BottleWater}) Duration={DrinkBottleDurationTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // A bottleful is a real drink: relief raw 0.7 / boiled 0.85 (so the
        // two-step chain matches the old single drink, 29H). Spec 29C.9: the
        // thirst drops gulp by gulp across the duration, not in one jump.
        var boiled = npc.BottleWater == WaterKind.Boiled;
        var thirstTotal = boiled ? SimBalance.DrinkThirstBoiled : SimBalance.DrinkThirstRaw;
        var comfortTotal = boiled ? SimBalance.DrinkComfortBoiled : 0f;
        var share = 1f / DrinkBottleDurationTicks;
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst - thirstTotal * share);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfortTotal * share);

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Spec 29H: raw water is a gamble — sickness roll (moved here from
        // the old water-edge Drink now that filling and drinking are split).
        // §45 r4: eased 30%/-0.15 -> 15%/-0.08. The colony drinks raw
        // 70-105 times per 15 days (boiled is ~2% of drinks — the fire is
        // dead ~90% of the time), so the old odds ground through 3-5 full
        // torsos per soak: half of all deaths were "Torso destroyed by
        // sickness". The gamble stays (chronic cough), but expected damage
        // (~0.012/drink) now sits within fed-regen's budget instead of
        // being a guaranteed death sentence for a fireless colony.
        if (!boiled)
        {
            var sickRoll = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 833);
            if (sickRoll < SimBalance.RawWaterSickChance && !Spec49.SickDoT)
            {
                // Baseline path (pre-§49): instant lump — torso -0.08 (floor 0.1,
                // only above 0.2) + comfort -0.2. Kept for harness A/B bisect.
                var beforeTorso = npc.Body.Parts[BodyPart.Torso];
                if (npc.Body.Parts[BodyPart.Torso] > 0.2f)
                {
                    npc.Body.Parts[BodyPart.Torso] =
                        System.Math.Max(0.1f, npc.Body.Parts[BodyPart.Torso] - 0.08f);
                }
                npc.Health = npc.Body.Mean();
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.2f);
                if (npc.Body.VitalDestroyed(out var sickVital0))
                {
                    npc.Health = 0f;
                    Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"{sickVital0} destroyed by sickness");
                }
                DamageReactionSystemHelpers.GrantAdrenaline(
                    world, npc, beforeTorso - npc.Body.Parts[BodyPart.Torso], "Sickness");
                Trace.Emit(world, npc.Id, "GotSick", $"Raw water (Roll={sickRoll:F2}) instant");
            }
            else if (sickRoll < SimBalance.RawWaterSickChance)
            {
                // Spec §49: don't lump the harm here. Add a BOUNDED torso-damage
                // budget the DoT pays down over the next hours (visible 🤢), and
                // open the icon/malaise window. The budget cap is the key: a
                // thirsty colony drinking raw back-to-back opens overlapping
                // windows, and without the cap the DoT would grind continuously
                // (this exact bug wiped seed 12345). Capped, total harm ≈ the old
                // instant -0.08 model, so the §45/§46 balance holds.
                npc.Mind.SicknessDamageRemaining = System.Math.Min(
                    SicknessDamageBudgetCap, npc.Mind.SicknessDamageRemaining + SicknessDamagePerBout);
                npc.Mind.SickUntilTick =
                    System.Math.Max(npc.Mind.SickUntilTick, world.Tick) + SicknessDurationTicks;
                Trace.Emit(world, npc.Id, "GotSick",
                    $"Raw water (Roll={sickRoll:F2}) DmgBudget={npc.Mind.SicknessDamageRemaining:F2}");
            }
        }

        // Spec §52: spend one gulp; the bottle only empties when the last is gone.
        npc.BottleCharges--;
        var driedOut = npc.BottleCharges <= 0;
        Trace.Emit(world, npc.Id, "DrankBottle",
            $"{(boiled ? "Boiled" : "Raw")} water Thirst={npc.Needs.Thirst:F2} Left={System.Math.Max(0, npc.BottleCharges)}");
        if (driedOut)
        {
            npc.BottleWater = WaterKind.None;
            npc.BottleCharges = 0;
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (drank from bottle)");
    }

    private static void ApplyEffects(NPCState npc, InteractionEffects effects)
    {
        ApplyEffectsScaled(npc, effects, 1f);
    }

    // Spec 29C.9: apply a fraction of an interaction's effect — used to drip
    // the need relief gradually across the action's duration (Sims-style).
    private static void ApplyEffectsScaled(NPCState npc, InteractionEffects effects, float k)
    {
        npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger + effects.HungerDelta * k);
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst + effects.ThirstDelta * k);
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + effects.EnergyDelta * k);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + effects.ComfortDelta * k);
        npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + effects.ThermalDelta * k);
        // Spec 31A.5A: warmth/armor are no longer touched here — they are
        // recomputed from the worn-items list by EquipmentMath.
    }

    private static InteractionType? GetPlannedInteractionType(NPCPlanState plan)
    {
        var start = plan.CurrentStepIndex;
        if (start < 0) start = 0;
        if (start > plan.Steps.Count) start = plan.Steps.Count;
        for (var i = start; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Type == PlanStepType.Interact && step.Interaction.HasValue)
            {
                return step.Interaction;
            }
        }

        return null;
    }

    private static InteractionDefinition? ResolveInteraction(ObjectDefinition definition, InteractionType? type)
    {
        if (type is null)
        {
            return definition.Interactions.Count > 0 ? definition.Interactions[0] : null;
        }

        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == type.Value)
            {
                return interaction;
            }
        }

        return null;
    }
}

}
