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

public interface ISimulationSystem
{
    string Name { get; }

    TickLayer Layer { get; }

    void Run(WorldState world);
}

// Spec §49: sleep / social / water overhaul knobs. Static so the headless soak
// harness can bisect features deterministically, and so HexTuningConfig can push
// live slider values in the editor. Defaults = all features ON at design values.
public static class Spec49
{
    // Feature toggles (harness bisect).
    public static bool Rearm = true;         // sleep re-arm (kill empty get-ups)
    public static bool CoolRearm = true;     // spec 35.4: cool-off dwell re-arm (kill None→CoolOff spam)
    public static bool SleepComfort = true;  // unified sleep-comfort formula
    public static bool AmbientSocial = true; // passive proximity socialising
    public static bool SickDoT = true;       // delayed raw-water sickness

    // Spec 35.4: cool-off dwell — how long one "stay in the shade" beat lasts
    // before ShouldKeepCooling re-checks, and the max number of re-arms before a
    // fallback tile that never cools aborts the dwell (safety against a frozen NPC).
    public static int CoolOffDwellTicks = 40;
    public static int CoolOffMaxRearms = 6;

    // Talk tuning (longer, less rewarding).
    public static int TalkDuration = 90;
    public static float TalkInitGain = 0.20f;
    public static float TalkListenGain = 0.12f;

    // Ambient (passive proximity) social gain per slow tick.
    public static float AmbientGain = 0.012f;

    // A chat won't START once hunger/thirst reach this (in-flight talks finish).
    public static float SocializeNeedGate = 0.55f;

    // Tier A: unified sleep-comfort formula — comfort gained over a full night
    // of sleep by surface, plus a fireside bonus, minus sun/rain penalties.
    // (grass 0.05, +fire 0.05, a bed ~1.0 minus sun/rain → ~0.70.)
    public static float SleepComfortGrassNight = 0.05f;
    public static float SleepComfortLeafNight = 0.30f;
    public static float SleepComfortBedNight = 1.00f;
    // §49.8: a night's sleep beside a lit fire tops up ~5% comfort on its own —
    // the campfire's warmth reads as cosy even on bare grass.
    public static float SleepComfortFireBonusNight = 0.05f;
    public static float SleepComfortSunPenaltyNight = 0.15f;
    public static float SleepComfortRainPenaltyNight = 0.15f;
    // §49.8: awake by a lit fire is a touch comfier than trudging about — the
    // usual waking comfort drain (ComfortRate) reverses into a small gain, so
    // sitting fireside slowly restores comfort instead of bleeding it.
    public static float AwakeFireComfortGain = 0.003f;
    // A jacket/coat (torso-covering outer garment) bunched under the body pads
    // the bare ground a little — a touch more comfort than sleeping on plain
    // dirt. Only helps when there's no bed; a real mat/bed already dwarfs it.
    public static float SleepComfortJacketPadNight = 0.06f;

    // Tier C: pick a comfier sleep spot — shade on a hot day, fireside in the
    // cold — as a SMALL nudge that never overrides the home-anchor + indoor
    // safety (which historically stopped the colony bedding down in dog land).
    public static bool SmartSleepSpot = true;
    public static float SleepSpotFireWeight = 1.5f;
    public static float SleepSpotShadeWeight = 1.5f;

    // Tier C: when only mildly thirsty, prefer to set up / drink BOILED water
    // rather than gamble on raw (which the fire being dead 90% of the time makes
    // the default). Only urgent thirst reaches for raw.
    public static bool ProactiveBoil = true;
    public static float BoilThirstCeiling = 0.6f; // above this, raw is fine (urgent)
    public static float BoilChainWeight = 0.1f;   // fire/tool-chain push while boiling. 0.1 = safe (10W/0L/3d); raise toward 0.3 for more boiled water at a survival cost (0.2→13% boiled/3L, 0.3→20%/8W)

    // §49.7: soggy REAL garments (pants/vest — not bra/panties/bikini) drag on
    // the move; a soaked body is a little less comfortable (wet underwear too,
    // it just doesn't slow you). Drag is x(PerGarment) per wet non-underwear
    // item, floored.
    public static float WetDragPerGarment = 0.9f;
    public static float WetDragFloor = 0.8f;
    public static float WetComfortPenalty = 0.004f; // per slow tick while soaked

    // Tier B: shade is a genuinely cooler spot (35° sun → ~28° shade) — was -2.
    public static float ShadeCooling = -7f;
    // Tier B: a sleeping body's cold/heat accrues AND bites this much slower —
    // the Sims-style "needs slow while asleep", so a night's sleep doesn't
    // freeze her (pairs with re-arm, which lets her sleep THROUGH mild cold).
    public static float ThermalSleepFactor = 0.5f;
}

// Spec §50: limb loss / amputation tuning. A survivor can lose an arm or a
// leg — for good — either emergently (a bite that overwhelms an already-mauled
// limb) or at a prepared hazard (reef/trap). The consequence is HARD: a heavy
// one-shot blood dump plus a deep, slow-clotting wound → a likely bleed-out
// spiral unless dressed. All balance lives here so the harness can bisect it.
public static class Spec50
{
    public static bool Enabled = true;

    // A limb severs the moment a bite drives an arm/leg to 0 HP AND either:
    //  • the blow's own damage ≥ LimbSeverThreshold  (a big single hit — the
    //    shark's 0.2, a future weapon — tears it clean off), OR
    //  • a deterministic roll < GrindSeverChance     (the small dog bite that
    //    finally destroys an already-mauled leg rips it off — rare, so most
    //    zeroed legs stay attached-but-useless as before).
    // Both stay high/low so amputation is dramatic, not routine.
    public static float LimbSeverThreshold = 0.14f;
    public static float GrindSeverChance = 0.25f;

    // The instant blood loss (0..1 of the Blood need) when a limb comes off.
    public static float LimbSeverBloodLoss = 0.4f;

    // The severity of the fresh stump wound filed on sever — deep, so §44
    // clotting keeps it bleeding for a while (ongoing Blood drain).
    public static float LimbSeverWoundSeverity = 0.35f;

    // Strike collapse for a severed ARM (below the 0.4 mauled floor). One arm
    // gone → strike ×this; both gone → ×this².
    public static float SeveredLimbMobilityMult = 0.15f;

    // A survivor who has lost a leg (one or both) crawls at this fraction of
    // walking speed — a fixed ~1/3, matching the crawl animation.
    public static float CrawlSpeedFactor = 1f / 3f;

    // How long a severed limb lies in the world before it decays away (slow
    // ticks × 16/tick, mirroring corpse decay — 4800 ≈ 2 in-game days).
    public static float SeveredLimbDecayTicks = 4800f;

    // Prepared-hazard chance to take a leg per slow tick while standing on a
    // hazard junction (0..1). 1 = deterministic on contact.
    public static float HazardSeverChance = 1f;
}

// Spec §53: compassion & mutual aid. A girl with a full belly and no fire to
// tend will walk over to a starving / wounded / sick / grieving housemate and
// help — feed, dress a wound, hand a pill, or console — which lifts BOTH
// relationships. The pull scales with the sufferer's plight and with this
// girl's personality CompassionTrait, but is gated hard behind her own
// survival: if SHE is starving or bleeding she looks after herself first.
// Helping costs no items (the relief is applied straight to the target) so it
// can never bankrupt the knife-edge colony. All balance lives here so the
// headless harness can bisect and the HexTuningConfig sliders can drive it.
public static class Spec53
{
    public static bool Enabled = true;

    // Compassion need (NPCNeeds.Compassion): drains per slow tick by
    // CompassionRate × (nearby suffering) × CompassionTrait; recovers toward
    // full by RecoverRate when no one nearby is hurting.
    public static float CompassionRate = 0.02f;
    public static float RecoverRate = 0.01f;

    // Aid bid = base + suffering × CompassionTrait × AidWeight
    //                 + (1 − Compassion) × PressureWeight.
    // AidWeight 0.85 lets a high-trait girl (≈1.0) facing a dying housemate
    // (suffering≈1) bid ≈0.95 — over CraftBed's 0.7 and the 0.15 switch margin —
    // while a reserved girl (≈0.35) bids ≈0.4 and only helps when otherwise idle.
    // Her own StarvingBoost (1.0) still outranks aid: self-preservation wins.
    public static float AidWeight = 0.85f;
    public static float PressureWeight = 0.2f;

    // Self-survival gate — she will NOT set out to help while her own body is
    // in the red: hunger at/above this, health below this, actively fighting,
    // fleeing, or already flagged starving/dehydrated.
    public static float SelfHungerGate = 0.6f;
    public static float SelfHealthGate = 0.5f;

    // A neighbour must be suffering at least this much (0..1) to be worth a trip.
    public static float SufferingThreshold = 0.3f;

    // Relief applied to the TARGET on a completed aid (no item is spent):
    public static float FeedRelief = 0.5f;          // target Hunger down
    public static float TreatHeal = 0.15f;          // wounded body parts up
    public static float TreatBlood = 0.2f;          // target Blood up
    public static float MedicateHeal = 0.1f;        // target Health up (+ sickness cleared)
    public static float ConsoleStressRelief = 0.3f; // target Stress down (+ grief eased)

    // Relationship gain on BOTH sides of a completed aid — deliberately larger
    // than a chat: kindness under hardship bonds hard.
    public static float AidRelationshipGain = 0.36f;

    // How long the aid interaction runs (ticks), mirroring a talk.
    public static int AidDuration = 70;

    // How much of her own Compassion a completed aid restores.
    public static float AidSelfRestore = 0.4f;

    // Personality spread: CompassionTrait is seeded in [TraitMin, TraitMax].
    public static float TraitMin = 0.35f;
    public static float TraitMax = 1.0f;
}

public enum TickLayer
{
    Fast,
    Medium,
    Slow
}

public interface IDecisionModel
{
}

public interface IPlanner
{
}

public interface IPathfinder
{
}

public interface IInteractionResolver
{
}

public sealed class PerceptionSystem : ISimulationSystem
{
    public string Name => nameof(PerceptionSystem);

    public TickLayer Layer => TickLayer.Medium;

    // Spec 22.7 / 27.18A: live sight radius and memory TTL for discoveries.
    private const int PerceptionRadiusTiles = 2;
    private const int MemoryTtlTicks = 2400;

    private readonly System.Collections.Generic.List<ObjectId> _forgottenScratch = new();

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Perception.Objects.Clear();
            npc.Perception.Agents.Clear();
            npc.Perception.Self.Hunger = npc.Needs.Hunger;
            npc.Perception.Self.Energy = npc.Needs.Energy;
            npc.Perception.Self.Comfort = npc.Needs.Comfort;
            npc.Perception.Self.Social = npc.Needs.Social;
            npc.Perception.Self.ThermalDiscomfort = npc.Needs.ThermalDiscomfort;
            npc.Perception.Self.Tile = npc.Tile;
            npc.Perception.Self.Fragment = npc.Fragment;
            npc.Perception.Environment.Temperature = world.Environment.GlobalTemperature;
            npc.Perception.Environment.NearbyAgentsCount = world.Entities.Npcs.Count - 1;
            npc.Perception.Environment.IsCrowded = world.Entities.Npcs.Count > 2;
            npc.Perception.Environment.IsPrivate = world.Entities.Npcs.Count <= 1;
            npc.Perception.LastUpdatedTick = world.Tick;

            var npcJunction = ResolveCurrentJunction(world, npc);

            // Live sight (spec 22.7): only objects within the perception
            // radius; every sighting upserts spatial memory (spec 27.18A).
            foreach (var obj in world.Entities.Objects.Values)
            {
                if (HexSpatialMath.HexDistance(npc.Tile, obj.Tile) > PerceptionRadiusTiles)
                {
                    continue;
                }

                if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition))
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(npc.Position, HexSpatialMath.TileToWorld(obj.Tile));
                var objJunction = obj.Junctions.Count > 0 ? obj.Junctions[0] : (JunctionId?)null;
                var isReachable = npcJunction.HasValue && objJunction.HasValue &&
                    Connectivity.ReachableBeside(world, npcJunction.Value, objJunction.Value, npc.Body.CanJump);

                var perceived = new PerceivedObject
                {
                    Id = obj.Id,
                    DefinitionId = obj.DefinitionId,
                    FromMemory = false,
                    Tile = obj.Tile,
                    Distance = distance,
                    IsReachable = isReachable,
                    IsOccupied = obj.IsOccupied,
                    OccupiedBy = obj.CurrentUser
                };

                foreach (var interaction in definition.Interactions)
                {
                    perceived.AvailableInteractions.Add(interaction.Type);
                }

                npc.Perception.Objects.Add(perceived);

                if (!npc.Memory.KnownObjects.TryGetValue(obj.Id, out var record))
                {
                    record = new Memory.ObjectMemory { Id = obj.Id };
                    npc.Memory.KnownObjects[obj.Id] = record;
                    Trace.Emit(world, npc.Id, "MemoryAdded",
                        $"Obj={obj.Id.Value} Def={obj.DefinitionId} Tile={obj.Tile.Q},{obj.Tile.R}");
                }

                record.DefinitionId = obj.DefinitionId;
                record.Tile = obj.Tile;
                record.Junction = objJunction;
                record.LastSeenTick = world.Tick;
            }

            // Memory maintenance (spec 27.18A): negative evidence inside the
            // sight radius, TTL for discoveries, then remembered-but-unseen
            // objects join the perceived list flagged FromMemory.
            _forgottenScratch.Clear();
            foreach (var record in npc.Memory.KnownObjects.Values)
            {
                var withinSight = HexSpatialMath.HexDistance(npc.Tile, record.Tile) <= PerceptionRadiusTiles;
                var exists = world.Entities.Objects.ContainsKey(record.Id);

                if (withinSight && !exists)
                {
                    _forgottenScratch.Add(record.Id);
                    Trace.Emit(world, npc.Id, "MemoryForgotten",
                        $"Obj={record.Id.Value} Def={record.DefinitionId} Gone (negative evidence)");
                    continue;
                }

                if (!record.IsPermanent && world.Tick - record.LastSeenTick > MemoryTtlTicks)
                {
                    _forgottenScratch.Add(record.Id);
                    Trace.Emit(world, npc.Id, "MemoryForgotten",
                        $"Obj={record.Id.Value} Def={record.DefinitionId} Expired " +
                        $"(unseen for {world.Tick - record.LastSeenTick} ticks)");
                    continue;
                }

                if (withinSight)
                {
                    continue; // live entry already covers it
                }

                if (!world.Content.ObjectDefinitions.TryGetValue(record.DefinitionId, out var definition))
                {
                    continue;
                }

                var isReachable = npcJunction.HasValue && record.Junction.HasValue &&
                    Connectivity.ReachableBeside(world, npcJunction.Value, record.Junction.Value, npc.Body.CanJump);

                var remembered = new PerceivedObject
                {
                    Id = record.Id,
                    DefinitionId = record.DefinitionId,
                    FromMemory = true,
                    Tile = record.Tile,
                    Distance = HexSpatialMath.Distance(npc.Position, HexSpatialMath.TileToWorld(record.Tile)),
                    IsReachable = isReachable,
                    IsOccupied = false, // assumed free until seen (spec 27.18A)
                    OccupiedBy = null
                };

                foreach (var interaction in definition.Interactions)
                {
                    remembered.AvailableInteractions.Add(interaction.Type);
                }

                npc.Perception.Objects.Add(remembered);
            }

            foreach (var forgottenId in _forgottenScratch)
            {
                npc.Memory.KnownObjects.Remove(forgottenId);
            }

            // Spec 28.3: perceived agents with reachability and relationship summary.
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id == npc.Id)
                {
                    continue;
                }

                var agentDistance = HexSpatialMath.Distance(npc.Position, other.Position);
                var otherJunction = other.CurrentJunction;
                var agentReachable = npcJunction.HasValue && otherJunction.HasValue &&
                    (npcJunction.Value.Equals(otherJunction.Value) ||
                     Connectivity.Reachable(world, npcJunction.Value, otherJunction.Value, npc.Body.CanJump));
                var relationship = npc.Social.GetOrCreate(other.Id);

                var perceivedAgent = new PerceivedAgent
                {
                    Id = other.Id,
                    Tile = other.Tile,
                    Distance = agentDistance,
                    CanSee = true,
                    CanHear = true,
                    Junction = otherJunction,
                    IsReachable = agentReachable,
                    IsBusy = other.IsFighting ||
                        (other.Execution.Status == ExecutionStatus.InProgress &&
                         other.Execution.CurrentInteraction != InteractionType.Talk),
                    IsMoving = other.Movement.IsMoving
                };
                perceivedAgent.Relationship.Trust = relationship.Trust;
                perceivedAgent.Relationship.Affinity = relationship.Affinity;

                // Spec §53: read how badly this neighbour needs help and the
                // single most-urgent HELPABLE kind, so the Aid goal can bid on
                // and route to the worst-off without re-scanning full state.
                // Severity is 0..1; a bleed-out clock outranks mere hunger.
                var aidKind = AidKind.None;
                var suffering = 0f;
                if (other.Health > 0f)
                {
                    // Treat — open wounds / blood loss (a bleed-out is on a clock).
                    var treatSev = other.Wounds.Count > 0 || other.Needs.Blood < 0.6f
                        ? System.Math.Max(1f - other.Needs.Blood, 1f - other.Health)
                        : 0f;
                    // Medicate — actively sick, or gravely weak with nothing to dress.
                    var medSev = other.Mind.SickUntilTick > world.Tick
                        ? 0.6f
                        : (other.Health < 0.4f && other.Wounds.Count == 0 ? 1f - other.Health : 0f);
                    // Feed — genuinely hungry (not a passing dip).
                    var feedSev = other.Needs.Hunger >= 0.55f ? other.Needs.Hunger : 0f;
                    // Console — grieving or breaking under stress (soft, lowest).
                    var consoleSev = world.Tick < other.Mind.GrievingUntilTick ? 0.5f : 0f;
                    if (other.Needs.Stress > 0.6f)
                    {
                        consoleSev = System.Math.Max(consoleSev, other.Needs.Stress * 0.6f);
                    }

                    suffering = treatSev;
                    aidKind = AidKind.Treat;
                    if (medSev > suffering) { suffering = medSev; aidKind = AidKind.Medicate; }
                    if (feedSev > suffering) { suffering = feedSev; aidKind = AidKind.Feed; }
                    if (consoleSev > suffering) { suffering = consoleSev; aidKind = AidKind.Console; }
                    if (suffering <= 0f) { aidKind = AidKind.None; }
                }
                perceivedAgent.Suffering = suffering;
                perceivedAgent.AidKind = aidKind;

                npc.Perception.Agents.Add(perceivedAgent);
            }

            var reachableCount = 0;
            var occupiedCount = 0;
            foreach (var obj in npc.Perception.Objects)
            {
                if (obj.IsReachable) reachableCount++;
                if (obj.IsOccupied) occupiedCount++;
            }

            Trace.Emit(world, npc.Id, "PerceptionUpdated",
                $"Objects={npc.Perception.Objects.Count} Reachable={reachableCount} Occupied={occupiedCount} " +
                $"Needs=[{Trace.FormatNeeds(npc.Needs)}] Tile={npc.Tile.Q},{npc.Tile.R} " +
                $"Pos={Trace.FormatPos(npc.Position)} Junction={Trace.FormatJunction(npcJunction)} " +
                $"Env=[Temp={world.Environment.GlobalTemperature:F1} Agents={world.Entities.Npcs.Count - 1}]");

            foreach (var obj in npc.Perception.Objects)
            {
                var interactions = string.Join(",", obj.AvailableInteractions);
                Trace.Emit(world, npc.Id, "PerceivedObject",
                    $"Obj={obj.Id.Value} Tile={obj.Tile.Q},{obj.Tile.R} Dist={obj.Distance:F2} " +
                    $"Reachable={obj.IsReachable} Occupied={obj.IsOccupied} Interactions=[{interactions}]");
            }
        }
    }

    private static JunctionId? ResolveCurrentJunction(WorldState world, NPCState npc)
    {
        // §45 r5: a junction that BECAME blocked underfoot (obstacle spawn,
        // wall) must not anchor the NPC — pathfinding can't start from a
        // blocked node, so a kept key means every plan reads unreachable
        // until she starves. Re-anchor to the nearest open junction instead
        // (self-healing net for any blocker that forgets to nudge).
        if (npc.CurrentJunction.HasValue &&
            world.Junctions.Items.TryGetValue(npc.CurrentJunction.Value, out var current) &&
            !current.Blocked)
        {
            return npc.CurrentJunction;
        }

        var nearest = SpatialQueries.FindNearestJunction(world, npc.Position);
        npc.CurrentJunction = nearest;
        Trace.Emit(world, npc.Id, "JunctionResolved",
            $"NearestJunction={Trace.FormatJunction(nearest)} Pos={Trace.FormatPos(npc.Position)}");
        return nearest;
    }
}

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
            // Restraint gate (spec 29B.2): don't harvest food you don't need,
            // or the ground stock never survives until the productionless night.
            var getFoodHungerThreshold = SimBalance.GetFoodHungerThreshold;

            var eatAvail = hasFoodInInventory || hasCoconutMeal;
            var getFoodAvail = !hasFoodInInventory && !hasCoconutMeal && npc.Inventory.HasSpace &&
                npc.Needs.Hunger >= getFoodHungerThreshold &&
                (HasReachableFoodForCurrentTools(npc, world) || KnowsReachableProducer(npc, world));
            // Spec 29G: the land itself is furniture — a bed is better, but
            // sleep never blocks on owning one. Still, nobody naps at noon
            // out of boredom: sleep is for the tired or for the dark hours
            // (without this gate the first soak showed 73 ground naps eating
            // every idle minute — no explores, the fire never lit).
            var sleepAvail = npc.Needs.Energy < SimBalance.SleepEnergyThreshold ||
                world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
            // Spec 31C.7A: sit because you need it — and never settle into a
            // chair on an empty stomach. Sitting yields to sleep hours (the
            // Sit->Sleep churn was 47 interrupts/soak before this gate).
            var sitAvail = npc.Needs.Comfort < SimBalance.SitComfortThreshold &&
                npc.Needs.Hunger < SimBalance.SitNeedGate && npc.Needs.Thirst < SimBalance.SitNeedGate &&
                !sleepAvail;
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
                if (!agent.IsReachable || agent.IsBusy || agent.IsMoving)
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
                            aidAvail = true;
                        }
                    }
                }
                // An in-flight aid (walking to the sufferer or mid-care) keeps its
                // goal available so the availability scan can't zero a live plan.
                var curIt = npc.Execution.CurrentInteraction;
                var inAidExec = curIt == InteractionType.FeedOther ||
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
            AddGoalScore(npc, world.Tick, GoalType.Aid,
                bestSuffering * npc.CompassionTrait * Spec53.AidWeight +
                    (1f - npc.Needs.Compassion) * Spec53.PressureWeight,
                aidAvail, social: System.Math.Max(0f, bestSuffererAffinity) * 0.05f);

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
            var hasKnife = npc.Inventory.Items.Contains("tool.knife");

            // Spec §52: the furniture build-site chain. A site is an intent point
            // every NPC knows; materials are hauled in (deposited into it) over
            // many trips, then a builder with a hammer raises the piece. Build
            // work is peacetime (like the hut): it pauses when hungry/thirsty.
            var buildSite = FindBuildSite(npc, world);
            var hasHammer = npc.Inventory.Items.Contains("tool.hammer");
            var buildPeacetime = npc.Needs.Hunger < 0.55f && npc.Needs.Thirst < 0.55f &&
                npc.Memory.Dangers.Count == 0;
            // Spec §54 cold start: raising the FIRST hearth is survival-critical
            // (no fire ⇒ no warmth, no cooking, no crafting), so building the
            // campfire-site bypasses the peacetime gate and outranks everything —
            // the colony must pile the stones and light up before it can thrive.
            var noCampfireYet = !HasReachableWithTag(npc, world, "Campfire");
            var siteIsHearth = buildSite != null && buildSite.BuildProduct == "campfire.spot";
            var hearthUrgent = siteIsHearth && noCampfireYet;
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
            var siteWaivesHammer = siteIsHearth || buildSite?.BuildProduct == "bed.leaf";
            var buildFurnitureAvail = buildSite != null && buildWindow &&
                (CarriesSiteMaterial(npc, buildSite) ||
                 (BuildSiteMath.IsStocked(buildSite) && (hasHammer || siteWaivesHammer)));

            // Spec 29E: the fire chain still needs these — a pot/lighter/wood
            // and a seen campfire drive the fuel/craft goals further below.
            var hasLighter = npc.Inventory.Items.Contains("tool.lighter");
            var hasPot = npc.Inventory.Items.Contains("tool.pot");
            // Spec §54: "wood in hand" for fire/craft now means a STICK.
            var hasWood = npc.Inventory.Items.Contains("resource.stick");
            var (campfireSeen, campfireFuel) = FindCampfire(npc, world);
            var drinkAvail = npc.Needs.Thirst >= 0.35f && hasCoconutWater;
            var getWaterAvail = hasCoconutBlade && npc.Needs.Thirst >= 0.35f && !hasCoconutWater &&
                npc.Inventory.HasSpace && HasReachableDefinition(npc, world, "food.coconut");
            var coconutToolPressure = !hasCoconutBlade &&
                (npc.Needs.Thirst >= 0.35f || npc.Needs.Hunger >= getFoodHungerThreshold) &&
                HasCoconutOpportunity(npc, world);
            var coconutToolBoost = coconutToolPressure
                ? System.MathF.Max(npc.Needs.Thirst, npc.Needs.Hunger)
                : 0f;
            // §55: boiling water is retired — the fire chain no longer earns a
            // "boil" bonus, only warmth/cooking motivate it now.
            var wantsBoil = false;
            // Spec 35.2: any reachable Tool not carried (saw, dropped gear).
            var gatherToolsAvail = npc.Inventory.HasSpace &&
                HasMissingToolReachable(npc, world);
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
            var gatherWoodAvail = ((fuelLow && carriedSticks == 0 && carriedLogs == 0) ||
                    (piece is { } pLog && carriedLogs < pLog.Logs) ||
                    siteNeedsLogs ||
                    raftWoodDemand ||
                    (coconutToolPressure && carriedSticks < SimBalance.KnifeStickCost)) &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Wood");
            // §45 r5: a genuinely cold girl can start the fire WITHOUT the
            // lighter (friction/hand-drill). The freeze probe showed 60-75%
            // of all freezing npc-ticks were "dead fire + wood in hand + no
            // lighter" — one lighter per colony and a half-day burn time
            // meant the carrier was almost never the one freezing at the
            // pit, and seed 777 died of hypothermia around that lock. The
            // threshold (-0.35, before the -0.85 damage band) keeps the
            // lighter meaningful in mild weather.
            var canFrictionLight = npc.Needs.ThermalComfort < -0.35f;
            var tendFireAvail = hasWood && fuelLow &&
                (campfireFuel > 0f || hasLighter || canFrictionLight);

            AddGoalScore(npc, world.Tick, GoalType.Drink, npc.Needs.Thirst, drinkAvail, drinkBoost);
            AddGoalScore(npc, world.Tick, GoalType.GetWater, npc.Needs.Thirst, getWaterAvail, drinkBoost);
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
                0.25f + 0.2f * npc.Needs.Thirst + coldChain + boilChain, gatherToolsAvail);
            // The raft pull mirrors BuildRaft's weight: stocking logs for the
            // coast run must win the auction as often as the run itself, or
            // the demand flag never turns into wood in hand (soak: GatherWood
            // won 8-10 times in 15 days while the raft starved).
            AddGoalScore(npc, world.Tick, GoalType.GatherWood,
                0.2f + 0.3f * npc.Needs.Thirst + coldChain + boilChain +
                (raftWoodDemand ? 0.3f : 0f) + coconutToolBoost, gatherWoodAvail);
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
            var craftBandageAvail = herbLeaves >= 2 && npc.Needs.Bandages < 2 && campfireSeen;
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
            var canWield2Handed = npc.Body.IntactHands >= 2;
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
            var craftSpearAvail = !hasSpear && hasWood && campfireSeen;
            var cookAvail = hasRawMeat && campfireSeen && campfireFuel > 0f;
            var craftLeatherAvail = hideCount >= 1 && campfireSeen &&
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
            var craftBowAvail = !hasBow && carriedSticks >= 2 && hideCount >= 1 &&
                carriedRope >= 1 && campfireSeen && !craftLeatherAvail;
            var craftArrowsAvail = hasBow && arrowCount == 0 && hasWood && campfireSeen;
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
            var hasAxe = npc.Inventory.Items.Contains("tool.axe_stone");
            var hasSaw = npc.Inventory.Items.Contains("tool.saw");
            var hasPickaxe = npc.Inventory.Items.Contains("tool.pickaxe_stone");
            var stoneCount = CountInventory(npc, "resource.stone");
            var stonesNeeded = (!hasAxe && !hasSaw ? 1 : 0) + (!hasPickaxe ? 2 : 0);
            var gatherStoneAvail = (stoneCount < stonesNeeded ||
                    (coconutToolPressure && stoneCount < SimBalance.KnifeStoneCost) ||
                    (piece is { } pStone && stoneCount < pStone.Stones) ||
                    (siteNeedsStones && stoneCount < 1)) &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Stone");
            var craftAxeAvail = !hasAxe && !hasSaw && hasWood && stoneCount >= 1 && campfireSeen;
            var craftPickaxeAvail = !hasPickaxe && hasWood && stoneCount >= 2 && campfireSeen;
            var canChop = hasAxe || hasSaw;
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
            var mineBoulderAvail = hasPickaxe && stoneCount < 2 && npc.Inventory.HasSpace &&
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
                (hearthUrgent ? 0.9f : 0.25f) + freeHands + coconutToolBoost, gatherStoneAvail);
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
            // site needs them, not just the 2-stick fuel reserve.
            var stickCap = siteNeedsSticks ? SimBalance.BedLeafBillSticks : 2;
            var splitLogAvail = carriedSticks < stickCap &&
                (hasAxe || hasSaw) && npc.Inventory.HasSpace &&
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
            var wantsLeaves = (bedDeficit && carriedLeaves < 3) ||
                (siteNeedsLeaves && carriedLeaves < SimBalance.BedLeafBillLeaves) ||
                (piece is { } pcrown && carriedLeaves < pcrown.Leaves) ||
                (world.Environment.UvIndex > 0.4f && carriedLeaves < 4);
            var chopCrownAvail = wantsLeaves && (hasAxe || hasSaw) &&
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
            var fiberNeed = (wantRope ? SimBalance.RopeFiberCost : 0) +
                (wantCloth ? SimBalance.ClothFiberCost : 0);
            // Fiber now comes from CUTTING a yucca with a blade (knife/axe); the
            // cut fibers scatter, then get picked up. So: cut yucca → gather fiber.
            var harvestYuccaAvail = carriedFiber < fiberNeed && npc.Inventory.HasSpace &&
                (hasKnife || hasAxe) && HasReachableWithTag(npc, world, "Yucca");
            var gatherFiberAvail = carriedFiber < fiberNeed && npc.Inventory.HasSpace &&
                HasReachableWithTag(npc, world, "Fiber");
            var craftRopeAvail = wantRope && carriedFiber >= SimBalance.RopeFiberCost && campfireSeen;
            var craftClothAvail = wantCloth && carriedFiber >= SimBalance.ClothFiberCost && campfireSeen;
            var craftKnifeAvail = !hasKnife && carriedSticks >= SimBalance.KnifeStickCost &&
                stoneCount >= SimBalance.KnifeStoneCost && campfireSeen;
            AddGoalScore(npc, world.Tick, GoalType.HarvestYucca, 0.26f + freeHands + bedRopePull, harvestYuccaAvail);
            AddGoalScore(npc, world.Tick, GoalType.GatherFiber, 0.24f + freeHands + bedRopePull, gatherFiberAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftRope, 0.28f + freeHands + bedRopePull, craftRopeAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftCloth, 0.28f + freeHands, craftClothAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftKnife,
                0.34f + freeHands + coconutToolBoost, craftKnifeAvail);

            // Spec §54: butcher a carcass (or, starving, a housemate's body) with
            // a knife — hunger-driven, since the payoff is meat.
            var butcherAvail = hasKnife &&
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
            var preyAvail = SimBalance.PredationEnabled && hasKnife &&
                npc.CompassionTrait <= SimBalance.PredationCompassionCeiling &&
                npc.Needs.Hunger >= SimBalance.PredationHungerGate &&
                NoOtherFoodReachable(npc, world) &&
                NearestPreyVictim(npc, world) is not null;
            AddGoalScore(npc, world.Tick, GoalType.Prey,
                SimBalance.PredationBaseScore + npc.Needs.Hunger, preyAvail);

            // Spec 35.3 + §52: build a hut piece when the full bill is carried
            // AND a hammer is in hand — raising a wall now needs the tool.
            var buildAvail = piece is { } needNow &&
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
            var buildFurniturePull = (siteNeedsLeaves || siteNeedsSticks || siteNeedsRope) ? 0.2f : 0f;
            AddGoalScore(npc, world.Tick, GoalType.BuildFurniture,
                (hearthUrgent ? 0.95f : 0.55f) + freeHands + buildFurniturePull, buildFurnitureAvail);

            // Spec §52: free a slot by carrying a low-value item to the fireside
            // stockpile — but only in peace. Life-threatening pressure (a dog, a
            // stat below 40 %) cancels the errand: you don't tidy your pockets
            // while something is trying to eat you.
            var lifeThreatened = npc.IsFighting || npc.Memory.Dangers.Count > 0 ||
                npc.Health < 0.4f || npc.Needs.Hunger >= 0.6f || npc.Needs.Thirst >= 0.6f;
            var haulVictim = InventoryMath.LowestImportanceDroppable(world, npc);
            var haulToFireAvail = !npc.Inventory.HasSpace && !lifeThreatened && campfireSeen &&
                haulVictim != null &&
                InventoryMath.Importance(world, haulVictim.DefinitionId) <= 25;
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

            // Spec 35.5: rain, wet clothes, and the drying chain.
            var wornWetness = 0f;
            foreach (var wornItem in npc.WornItems)
            {
                wornWetness = System.MathF.Max(wornWetness, wornItem.Wetness);
            }

            var craftRackAvail = !RackExists(world) && carriedSticks >= 2 && campfireSeen;

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
            var craftTentAvail = world.Environment.UvIndex > 0.4f &&
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
            var buildRaftAvail = carriedLogs >= 1 && world.RaftProgress < WorldState.RaftTarget &&
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
            AddGoalScore(npc, world.Tick, GoalType.CraftRack,
                0.3f + (world.Environment.IsRaining || wornWetness > 0.5f ? 0.2f : 0f),
                craftRackAvail);

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

    internal static bool HasCoconutBlade(NPCState npc) =>
        npc.Inventory.Items.Contains("tool.knife") ||
        npc.Inventory.Items.Contains("tool.axe_stone");

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
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
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

    private static bool HasMissingToolReachable(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && ObjectUsableBy(obj, npc.Id) &&
                !npc.Inventory.Items.Contains(obj.DefinitionId) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains("Tool"))
            {
                return true;
            }
        }

        return false;
    }

    internal static (bool Seen, float Fuel) FindCampfire(NPCState npc, WorldState world)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            var fuel = world.Entities.Objects.TryGetValue(obj.Id, out var worldObject)
                ? worldObject.ResourceAmount
                : 0f;
            return (true, fuel);
        }

        return (false, 0f);
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

            if (site.BuildProduct == "campfire.spot")
            {
                return site;
            }

            firstSite ??= site;
        }

        return firstSite;
    }

    // Does the NPC carry at least one material this site still needs?
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

// Spec 23.17 interrupt semantics: abort an active plan cleanly, releasing
// everything the plan owns (object occupancy, junction occupancy/reservation)
// so the next decision pass can replan without leaks.
public static class PlanInterruption
{
    public static void Abort(WorldState world, NPCState npc, string reason)
    {
        ExecutionSystem.ReleaseClaims(world, npc);
        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Plan.TargetObjectId is { } objId &&
            world.Entities.Objects.TryGetValue(objId, out var worldObject) &&
            worldObject.CurrentUser == npc.Id)
        {
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
        }

        // Release a talk invitation this plan placed on its target (spec 28.8).
        if (npc.Plan.TargetAgentId is { } invitedId &&
            world.Entities.Npcs.TryGetValue(invitedId, out var invited) &&
            invited.Mind.PendingTalkFrom is { } inviter && inviter.Equals(npc.Id))
        {
            invited.Mind.PendingTalkFrom = null;
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        npc.Plan.Status = PlanStatus.Invalid;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;

        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;

        Trace.Emit(world, npc.Id, "GoalInterrupted", reason);
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
                if (BuildCoconutEatPlan(world, npc))
                {
                    continue;
                }

                // Eating happens in place from inventory (spec 29B.3):
                // no target object, no junction reservation.
                var foodDefinitionId = npc.Inventory.FindFirstFood(world.Content);
                if (foodDefinitionId is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "PlanFailed",
                        "Goal=Eat but no food in inventory");
                    continue;
                }

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

            if (npc.Mind.CurrentGoal == GoalType.Drink)
            {
                if (BuildCoconutDrinkPlan(world, npc))
                {
                    continue;
                }

                var drinkDefinitionId = npc.Inventory.FindFirstDrink(world.Content);
                if (drinkDefinitionId is null)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "PlanFailed",
                        "Goal=Drink but nothing drinkable in inventory");
                    continue;
                }

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
        if (TryFindCoconutObject(npc, world, "food.coconut_pierced", requireWater: true, out var pierced))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Drink, pierced, InteractionType.Drink);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut", requireWater: false, out var whole))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Drink, whole,
                InteractionType.Process, InteractionType.Drink);
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut", out var carriedWhole))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Drink, carriedWhole,
                InteractionType.Process, InteractionType.Drink);
        }

        if (TryFindInventoryItem(npc, "food.coconut_pierced", out var carriedPierced))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Drink, carriedPierced,
                InteractionType.Drink);
        }

        return false;
    }

    private static bool BuildCoconutEatPlan(WorldState world, NPCState npc)
    {
        if (TryFindCoconutObject(npc, world, "food.coconut_open", requireWater: false, out var open))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, open, InteractionType.Eat);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut_pierced", requireWater: false, out var pierced))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, pierced,
                InteractionType.Process, InteractionType.Eat);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut", requireWater: false, out var whole))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, whole,
                InteractionType.Process, InteractionType.Process, InteractionType.Eat);
        }

        if (TryFindInventoryItem(npc, "food.coconut_open", out var carriedOpen))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedOpen,
                InteractionType.Eat);
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut_pierced", out var carriedPierced))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedPierced,
                InteractionType.Process, InteractionType.Eat);
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut", out var carriedWhole))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedWhole,
                InteractionType.Process, InteractionType.Process, InteractionType.Eat);
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

        var targetJunction = worldObject.Junctions[0];
        if (npc.CurrentJunction is not { } current || !current.Equals(targetJunction))
        {
            if (!SpatialMutations.TryReserveJunction(world, targetJunction, npc.Id, world.Tick, 48))
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, goal);
                Trace.Emit(world, npc.Id, "ReservationFailed",
                    $"Coconut Junction={targetJunction.Value} already reserved or occupied");
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
            $"Steps=[MoveToJunction,{FormatInteractions(interactions)}]");
        return true;
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
        foreach (var carried in npc.Inventory.Items)
        {
            if (carried.DefinitionId == definitionId)
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

    private static bool HasCoconutBlade(NPCState npc) =>
        npc.Inventory.Items.Contains("tool.knife") ||
        npc.Inventory.Items.Contains("tool.axe_stone");

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
        Junction best = null;
        var bestDist = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
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

    // Spec 29G: sit on the land — a ledge with the legs over the edge when
    // one is close, any free junction otherwise.
    private void BuildGroundSitPlan(WorldState world, NPCState npc)
    {
        JunctionId? spot = null;

        // Prefer a scenic ledge within ~4 tiles.
        if (npc.CurrentJunction is { } from)
        {
            var bestDist = float.MaxValue;
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (junction.Blocked || junction.Tiles.Count < 2 ||
                    !IsLedge(world, junction) ||
                    !SpatialQueries.IsJunctionFree(world, junction.Id))
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

        // Otherwise: sit right where she stands (or the nearest free spot).
        spot ??= npc.CurrentJunction;
        if (spot is not { } sitSpot ||
            !SpatialMutations.TryReserveJunction(world, sitSpot, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Sit);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Sit NoGroundSpot");
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

    // Spec 35.5: is a free drying rack within reach?
    private static bool HasFreeRackCandidate(WorldState world, NPCState npc)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition) &&
                definition.Tags.Contains("Rack") &&
                world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                !ExecutionSystem.RackHoldsItem(world, rack))
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
            if (!agent.IsReachable || agent.IsBusy || agent.IsMoving)
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
    private static bool IsValidTargetFor(WorldState world, NPCState npc, GoalType goal, PerceivedObject perceived)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition))
        {
            return false;
        }

        switch (goal)
        {
            case GoalType.GetFood:
                return definition.Tags.Contains("Food") &&
                    (!definition.Tags.Contains("Coconut") || HasCoconutBlade(npc));
            case GoalType.GatherWood:
                // Spec §54: any wood on the ground — a log or a stick.
                return definition.Tags.Contains("Wood");
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
                return definition.Tags.Contains("Tool") &&
                    !npc.Inventory.Items.Contains(perceived.DefinitionId);
            case GoalType.GatherHerb:
                return definition.Tags.Contains("Herb");
            case GoalType.HarvestYucca:
                return definition.Tags.Contains("Yucca");
            case GoalType.GatherFiber:
                return definition.Tags.Contains("Fiber");
            case GoalType.CraftRope:
            case GoalType.CraftCloth:
            case GoalType.CraftKnife:
            case GoalType.CraftBandage:
            case GoalType.TendFire:
            case GoalType.CraftSpear:
            case GoalType.CookMeat:
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
                // Spec 35.5: only a free rack (nothing hanging on it yet).
                return definition.Tags.Contains("Rack") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                    !ExecutionSystem.RackHoldsItem(world, rack);
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

public sealed class PathfindingSystem : ISimulationSystem
{
    public string Name => nameof(PathfindingSystem);

    private static readonly System.Collections.Generic.HashSet<JunctionId> _avoidScratch = new();

    // Spec 24.3: the junctions other housemates currently stand on.
    // NOTE (spec 34, climb): soft-avoiding elevation-step "climb seams" here
    // was tried to make hillside routes prefer the flat way around, but a hard
    // avoid over-penalizes (forces long detours) and whack-a-moled the fragile
    // economy across seeds. The correct form is a WEIGHTED path cost (climb =
    // 2x, per the user), which needs the BFS turned into a cost-aware search —
    // deferred to a focused pass (with the climb animation in Unity).
    internal static System.Collections.Generic.HashSet<JunctionId> OtherNpcJunctions(
        WorldState world, NPCState self)
    {
        _avoidScratch.Clear();
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Value == self.Id.Value)
            {
                continue;
            }

            if (other.CurrentJunction is { } standing)
            {
                _avoidScratch.Add(standing);
            }

            foreach (var claimed in other.ClaimedJunctions)
            {
                _avoidScratch.Add(claimed);
            }
        }

        return _avoidScratch;
    }

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active || npc.Plan.TargetJunctionId is null)
            {
                continue;
            }

            if (npc.Movement.IsMoving && npc.Movement.JunctionPath.Count > 0)
            {
                continue;
            }

            if (npc.CurrentJunction.HasValue && npc.CurrentJunction.Value.Equals(npc.Plan.TargetJunctionId.Value))
            {
                Trace.Emit(world, npc.Id, "PathAlreadyAtTarget",
                    $"Junction={npc.CurrentJunction.Value.Value} (already at destination)");
                continue;
            }

            var startJunction = npc.CurrentJunction ?? SpatialQueries.FindNearestJunction(world, npc.Position);
            if (startJunction is null)
            {
                npc.Movement.Status = MovementStatus.Blocked;
                npc.Movement.StopReason = "No current junction";
                Trace.Emit(world, npc.Id, "PathBlocked",
                    $"No current junction found at Pos={Trace.FormatPos(npc.Position)}");
                continue;
            }

            Trace.Emit(world, npc.Id, "PathSearching",
                $"From={startJunction.Value.Value} To={npc.Plan.TargetJunctionId.Value.Value} " +
                $"Pos={Trace.FormatPos(npc.Position)}");

            // Spec 40.17: comfortable NPCs prefer the flat detour; hungry/thirsty
            // ones take the short route to food/water (else they starve, 12345).
            var preferFlat = npc.Needs.Hunger < 0.5f && npc.Needs.Thirst < 0.5f;
            var path = HexPathfinder.FindPath(world, startJunction.Value, npc.Plan.TargetJunctionId.Value,
                OtherNpcJunctions(world, npc), preferFlat, npc.Body.CanJump);
            if (path.Count == 0)
            {
                npc.Movement.Status = MovementStatus.Blocked;
                npc.Movement.StopReason = "No path";
                Trace.Emit(world, npc.Id, "PathFailed",
                    $"No route from Junction={startJunction.Value.Value} to Junction={npc.Plan.TargetJunctionId.Value.Value}");
                continue;
            }

            npc.Movement.JunctionPath.Clear();
            foreach (var step in path)
            {
                npc.Movement.JunctionPath.Add(step);
            }

            npc.Movement.PathIndex = 1;
            npc.Movement.IsMoving = path.Count > 1;
            npc.Movement.Status = npc.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived;
            npc.Movement.StopReason = string.Empty;

            var pathJunctions = new System.Text.StringBuilder();
            for (var i = 0; i < path.Count; i++)
            {
                if (i > 0) pathJunctions.Append("->");
                pathJunctions.Append(path[i].Value);
            }
            Trace.Emit(world, npc.Id, "PathBuilt",
                $"Length={path.Count} Route=[{pathJunctions}] IsMoving={npc.Movement.IsMoving}");
        }
    }
}

public sealed class MovementSystem : ISimulationSystem
{
    public string Name => nameof(MovementSystem);

    public TickLayer Layer => TickLayer.Fast;

    // §21.21B hex-step hop: all timing lives in HexHopTuning — one number
    // drives the sim traversal AND the presentation's clip speed and arc.

    // §40.18-B swim TUNING KNOBS: plunging into deep water holds the swimmer
    // treading in place for a beat before the strokes start, and deep-water
    // strokes move slower than a walk. Public statics (not consts) so the
    // swim test scene can tune them live from the inspector.
    public static float SwimEntryPauseSeconds = 0.75f;
    public static float SwimSpeedFactor = 0.6f;

    // Deep water = swim tile; walkable river shallows are waded, not swum.
    private static bool IsSwimTile(Tile tile) =>
        (tile.Flags & TileFlags.Water) != 0 && (tile.Flags & TileFlags.Walkable) == 0;

    // §40.18-B: crossing from land INTO deep water treads a beat in place before
    // stroking off. One definition shared by the two tile-switch sites (hop
    // landing + ordinary walk step) so the entry timing/condition lives once.
    private static void TryBeginSwimEntry(
        WorldState world, NPCState npc, TileCoord fromTile, TileCoord toTile)
    {
        if (world.Tiles.Items.TryGetValue(toTile, out var landedTile) &&
            world.Tiles.Items.TryGetValue(fromTile, out var leftTile) &&
            IsSwimTile(landedTile) && !IsSwimTile(leftTile))
        {
            npc.Movement.ClimbPauseTimer = SwimEntryPauseSeconds;
            Trace.Emit(world, npc.Id, "SwimEnter",
                $"Pause={SwimEntryPauseSeconds:F2}s Tile={toTile.Q},{toTile.R}");
        }
    }

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!npc.Movement.IsMoving || npc.Movement.JunctionPath.Count == 0)
            {
                continue;
            }

            var targetIndex = npc.Movement.PathIndex;
            if (targetIndex >= npc.Movement.JunctionPath.Count)
            {
                npc.Movement.IsMoving = false;
                npc.Movement.Status = MovementStatus.Arrived;
                Trace.Emit(world, npc.Id, "MovementPathExhausted",
                    $"PathIndex={targetIndex} >= PathCount={npc.Movement.JunctionPath.Count}");
                continue;
            }

            var targetJunctionId = npc.Movement.JunctionPath[targetIndex];
            if (!world.Junctions.Items.TryGetValue(targetJunctionId, out var targetJunction))
            {
                npc.Movement.IsMoving = false;
                npc.Movement.Status = MovementStatus.Invalid;
                Trace.Emit(world, npc.Id, "MovementInvalidJunction",
                    $"Junction={targetJunctionId.Value} not found in world");
                continue;
            }

            // Spec 24.3: someone is standing on my next step — wait like a
            // polite housemate; after 40 ticks give up and re-path around.
            var stepOccupied = false;
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id.Value != npc.Id.Value && other.CurrentJunction is { } oj &&
                    oj.Equals(targetJunctionId) && !other.Movement.IsMoving)
                {
                    stepOccupied = true;
                    break;
                }
            }

            if (stepOccupied)
            {
                npc.Movement.BlockedWaitTicks++;
                if (npc.Movement.BlockedWaitTicks > 40)
                {
                    npc.Movement.BlockedWaitTicks = 0;
                    npc.Movement.JunctionPath.Clear();
                    npc.Movement.IsMoving = false;
                    npc.Movement.Status = MovementStatus.Waiting;
                    // §21.21B: a hop must not survive its path — stale hop
                    // state over a NEW path is a mid-air teleport waiting to
                    // happen. Clearing these three DISARMS it: HopTimer=0 stops
                    // the flight branch, HopArmed=false stops the walk-to-takeoff,
                    // HopPathIndex=-1 lets the scan re-arm on the new path. The
                    // remaining hop fields (HopFrom/HopTo/HopCrossed/
                    // HopLandingIndex/HopTargetTile/HopUp) are read ONLY by an
                    // active hop, and the next arm+launch overwrites every one of
                    // them, so their stale values can never fire.
                    npc.Movement.HopTimer = 0f;
                    npc.Movement.HopArmed = false;
                    npc.Movement.HopPathIndex = -1;
                    Trace.Emit(world, npc.Id, "MovementRepath",
                        $"Junction={targetJunctionId.Value} held by a housemate");
                }

                continue;
            }

            npc.Movement.BlockedWaitTicks = 0;

            var target = targetJunction.WorldPosition;

            // §21.21B v9 — ONE TARGET. If an elevation-edge wall is close
            // ahead, OVERRIDE the walk target to the fixed TAKEOFF point
            // (EdgePadding before the wall). Everything below — rotation,
            // pacing, arrival — then aims at that single point, so the walk
            // and the hop can never pull her two ways (the v8 freeze/jitter
            // was exactly that tug-of-war). The takeoff/landing are computed
            // from FIXED lattice points, not her live position, so they don't
            // drift as she approaches. hopApproach makes arrival launch the
            // hop instead of a normal junction crossing.
            var hopApproach = npc.Movement.HopArmed;
            if (hopApproach)
            {
                // Already committed — walk to the FIXED takeoff, no rescan.
                target = npc.Movement.HopFrom;
            }
            else if (npc.Movement.HopTimer <= 0f &&
                npc.Movement.HopPathIndex != npc.Movement.PathIndex &&
                world.Tiles.Items.TryGetValue(npc.Tile, out var hopStandTile))
            {
                // NOTE: swimmers are NO LONGER excluded. The water->land seam is
                // an elevation border like any wall, so climbing OUT arms a
                // clearance hop (takeoff EdgePadding out in the water, land on
                // the shore) instead of a flush walk-up — she stops swimming
                // right against the bank. All water is one elevation, so the
                // scan below (Tiles[0] steps to a different level) can only fire
                // on the climb-out; it never hops WITHIN the water.
                var wallIndex = -1;
                Tile wallTile = default;
                var scanDist = 0f;
                var scanFrom = npc.Position;
                for (var i = npc.Movement.PathIndex;
                     i < npc.Movement.JunctionPath.Count && scanDist < HexHopTuning.EdgePadding + 1.2f;
                     i++)
                {
                    if (!world.Junctions.Items.TryGetValue(npc.Movement.JunctionPath[i], out var jn) ||
                        jn.Tiles.Count == 0)
                    {
                        break;
                    }

                    scanDist += HexSpatialMath.Distance(scanFrom, jn.WorldPosition);
                    scanFrom = jn.WorldPosition;

                    // The tile she STEPS ONTO crossing this junction is Tiles[0]
                    // — the exact rule normal walking uses (see the walk tile
                    // update ~line 3102). Hop detection MUST use the same tile,
                    // so it fires only when the tile she actually walks onto
                    // steps to a different elevation (a drop into water counts).
                    // Testing every tile of the junction instead fired at seam
                    // junctions she merely walks ALONG — each borders both levels
                    // — and she bounced hop-after-hop down the seam.
                    if (world.Tiles.Items.TryGetValue(jn.Tiles[0], out var jt) &&
                        jt.Elevation != hopStandTile.Elevation &&
                        (!IsSwimTile(jt) || jt.Elevation < hopStandTile.Elevation))
                    {
                        wallIndex = i;
                        wallTile = jt;
                        break;
                    }
                }

                if (wallIndex >= 0)
                {
                    // The hop crosses exactly ONE elevation border: she leaves
                    // the tile just BEFORE the wall junction and lands on the
                    // wall tile (Tiles[0]) — the same tile normal walking would
                    // put her on — so the sim bookkeeping and the visual arc
                    // agree and she never re-arms the same crossing.
                    var nearCoord = npc.Tile;
                    if (wallIndex > npc.Movement.PathIndex &&
                        world.Junctions.Items.TryGetValue(
                            npc.Movement.JunctionPath[wallIndex - 1], out var beforeJn) &&
                        beforeJn.Tiles.Count > 0)
                    {
                        nearCoord = beforeJn.Tiles[0];
                    }

                    // Fly straight across the shared edge, near CENTRE -> wall
                    // CENTRE, symmetric EdgePadding before/after the border.
                    // (Building the direction from consecutive path junctions
                    // zig-zagged at corners and launched her at the wrong hex;
                    // building it from npc.Tile broke when the wall was a step
                    // ahead. Tile centres are robust for both.)
                    var nearCenter = HexSpatialMath.TileToWorld(nearCoord);
                    var targetCenter = HexSpatialMath.TileToWorld(wallTile.Coord);
                    var crossing = (nearCenter + targetCenter) * 0.5f;
                    var flightDir = HexSpatialMath.Normalize(targetCenter - nearCenter);
                    var stepAlong = HexHopTuning.EdgePadding;
                    var takeoff = crossing - flightDir * stepAlong;
                    var landing = crossing + flightDir * stepAlong;

                    target = takeoff;                 // ONE target for the walk
                    hopApproach = true;
                    npc.Movement.HopArmed = true;     // commit — freeze the plan
                    npc.Movement.HopLandingIndex = wallIndex;
                    npc.Movement.HopFrom = takeoff;
                    npc.Movement.HopTo = landing;
                    npc.Movement.HopUp = wallTile.Elevation > hopStandTile.Elevation;
                    npc.Movement.HopTargetTile = wallTile.Coord;
                }
            }

            var delta = new Float2(target.X - npc.Position.X, target.Y - npc.Position.Y);
            var direction = HexSpatialMath.Normalize(delta);
            var turnPerTick = npc.TurnSpeed * world.TickDeltaTime;

            // §21.21B v6: while the hop window runs, the HOP owns rotation
            // and pacing — the walk aiming below would re-target the path
            // junction every tick (mid-air spin to -150° and back, measured
            // in the t=46..62 probe trace) and its facing-error gate would
            // freeze flight ticks while the view clock kept running.
            var facingError = 0f;
            const float alignmentThreshold = 30f;
            if (npc.Movement.HopTimer <= 0f)
            {
                npc.Movement.DesiredDirection = direction;
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(direction);

                var prevRotation = npc.RotationDegrees;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);

                facingError = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));

                if (facingError > alignmentThreshold)
                {
                    npc.Movement.Status = MovementStatus.Rotating;
                    npc.Movement.PostTurnTimer = npc.PostTurnPause;
                    Trace.Emit(world, npc.Id, "MovementRotating",
                        $"Rot={prevRotation:F1}->{npc.RotationDegrees:F1} Desired={npc.Movement.DesiredRotationDegrees:F1} " +
                        $"Error={facingError:F1}>{alignmentThreshold} ToJunction={targetJunctionId.Value} " +
                        $"Step={targetIndex}/{npc.Movement.JunctionPath.Count}");
                    continue;
                }

                if (npc.Movement.PostTurnTimer > 0f)
                {
                    npc.Movement.PostTurnTimer -= world.TickDeltaTime;
                    npc.Movement.Status = MovementStatus.Rotating;
                    Trace.Emit(world, npc.Id, "MovementPostTurnPause",
                        $"Timer={npc.Movement.PostTurnTimer:F2}s remaining");
                    continue;
                }
            }

            // Standing pause (swim-entry treading, §40.18-B). Deferred while
            // a hop is flying — the hop owns its own timeline; a pending
            // tread pause plays after the landing beat.
            if (npc.Movement.ClimbPauseTimer > 0f && npc.Movement.HopTimer <= 0f)
            {
                npc.Movement.ClimbPauseTimer -= world.TickDeltaTime;
                npc.Movement.Status = MovementStatus.Waiting;
                Trace.Emit(world, npc.Id, "ClimbPause",
                    $"Timer={npc.Movement.ClimbPauseTimer:F2}s remaining");
                continue;
            }

            // §21.21B v6: the hop OWNS its window — it runs BEFORE the walk
            // rotation/alignment code (which would otherwise re-aim her at
            // the excluded edge junction every tick and even SKIP flight
            // ticks through the facing-error gate: the mid-air spinning and
            // the sim-vs-view clock drift the user saw).
            if (npc.Movement.HopTimer > 0f)
            {
                npc.Movement.HopTimer -= world.TickDeltaTime;
                // §21.21B: up and down can run on different windows (down faster).
                // beatScale is 1 for an up-jump (byte-identical) and shrinks the
                // takeoff/landing beats in step with the shorter down window.
                var hopWindow = HexHopTuning.WindowSeconds(npc.Movement.HopUp);
                var beatScale = npc.Movement.HopUp ? 1f : HexHopTuning.DownBeatScale;
                var hopTakeoff = HexHopTuning.TakeoffSeconds * beatScale;
                var hopLanding = HexHopTuning.LandingSeconds * beatScale;
                var hopElapsed = hopWindow - npc.Movement.HopTimer;
                var flightSpan = System.MathF.Max(0.05f, hopWindow - hopTakeoff - hopLanding);

                if (hopElapsed <= hopTakeoff)
                {
                    // Push-off beat: she already STOPPED at the takeoff point
                    // (v8 stop-short) — just hold there and crouch, turning to
                    // face the flight. No gather, no slide.
                    npc.Position = npc.Movement.HopFrom;
                    npc.RotationDegrees = MathUtil.RotateTowards(
                        npc.RotationDegrees, npc.Movement.DesiredRotationDegrees,
                        npc.TurnSpeed * world.TickDeltaTime);
                    npc.Movement.Status = MovementStatus.Waiting;
                    continue;
                }

                // Airborne: straight lattice-point-to-lattice-point flight.
                var flightT = MathUtil.Clamp01(
                    (hopElapsed - hopTakeoff) / flightSpan);
                npc.Position = npc.Movement.HopFrom +
                    (npc.Movement.HopTo - npc.Movement.HopFrom) * flightT;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees, npc.Movement.DesiredRotationDegrees,
                    npc.TurnSpeed * world.TickDeltaTime);
                npc.Movement.Status = flightT < 1f
                    ? MovementStatus.Moving
                    : MovementStatus.Waiting; // landing beat: feet planting

                // Landing beat: pre-face the NEXT waypoint while the feet
                // plant, so she stands up already in the right turn instead
                // of landing, pausing and spinning afterwards.
                if (flightT >= 1f && npc.Movement.HopCrossed &&
                    npc.Movement.PathIndex < npc.Movement.JunctionPath.Count &&
                    world.Junctions.Items.TryGetValue(
                        npc.Movement.JunctionPath[npc.Movement.PathIndex], out var nextAfterHop))
                {
                    var toNext = nextAfterHop.WorldPosition - npc.Position;
                    if (HexSpatialMath.Distance(nextAfterHop.WorldPosition, npc.Position) > 0.05f)
                    {
                        npc.Movement.DesiredRotationDegrees =
                            HexSpatialMath.AngleDegrees(HexSpatialMath.Normalize(toNext));
                    }
                }

                if (flightT >= 1f && !npc.Movement.HopCrossed)
                {
                    // A re-plan may have REPLACED the path mid-flight — the
                    // landing index then points into a stale list. Land where
                    // she is, close the hop, let pathfinding re-route.
                    if (npc.Movement.HopLandingIndex >= npc.Movement.JunctionPath.Count)
                    {
                        npc.Movement.HopCrossed = true;
                        npc.Movement.HopTimer = 0f;
                        Trace.Emit(world, npc.Id, "HopAborted",
                            "Path replaced mid-flight — landed in place");
                        continue;
                    }

                    // Touched down on the landing lattice point: do the
                    // bookkeeping for it AND the excluded edge junction.
                    npc.Movement.HopCrossed = true;
                    npc.CurrentJunction =
                        npc.Movement.JunctionPath[npc.Movement.HopLandingIndex];

                    // Tile: §21.21B v11 resolves HopTargetTile as the EXACT tile
                    // she flies into (from the wall junction + her path) and lands
                    // HopTo inside it — so commit it directly. Deriving the tile
                    // from the landing junction's Tiles[0] instead picked a
                    // neighbour (often her OWN previous tile), so npc.Tile never
                    // updated, the scan kept seeing the wall ahead and re-armed
                    // the SAME hop — she bounced on the border ("double jump",
                    // and the wrong tile fed the view a wrong ground Y so she
                    // sank into the hex).
                    var hopLandTile = npc.Movement.HopTargetTile;

                    var hopPreviousTile = npc.Tile;
                    if (hopLandTile != hopPreviousTile)
                    {
                        npc.Tile = hopLandTile;
                        SpatialMutations.MoveEntityToTile(world, npc.Id, hopPreviousTile, npc.Tile);
                        Trace.Emit(world, npc.Id, "EnteredTile",
                            $"From={hopPreviousTile.Q},{hopPreviousTile.R} To={npc.Tile.Q},{npc.Tile.R}");

                        // §40.18-B: dove into deep water — tread a beat (plays
                        // after the landing beat; the pause block defers while
                        // the hop window runs).
                        TryBeginSwimEntry(world, npc, hopPreviousTile, npc.Tile);
                    }

                    npc.Movement.PathIndex = npc.Movement.HopLandingIndex + 1;
                    Trace.Emit(world, npc.Id, "HopLanded",
                        $"Tile={npc.Tile.Q},{npc.Tile.R} Pos={Trace.FormatPos(npc.Position)}");
                    if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
                    {
                        // The path ends on this landing — close the hop window
                        // too, or the (now skipped) movement loop would leave
                        // it dangling and HopKind stuck for the view.
                        npc.Movement.HopTimer = 0f;
                        npc.Movement.ClimbPauseTimer = System.MathF.Max(
                            npc.Movement.ClimbPauseTimer,
                            HexHopTuning.LandingSeconds *
                                (npc.Movement.HopUp ? 1f : HexHopTuning.DownBeatScale));
                        npc.Movement.IsMoving = false;
                        npc.Movement.Status = MovementStatus.Arrived;
                        Trace.Emit(world, npc.Id, "MovementCompleted",
                            $"HopLanding Tile={npc.Tile.Q},{npc.Tile.R} " +
                            $"Pos={Trace.FormatPos(npc.Position)}");
                    }
                }

                continue;
            }


            var alignmentFactor = 1f - (facingError / alignmentThreshold) * 0.5f;
            // Spec 19.3C: mauled legs mean hobbling.
            var movementPerTick = npc.MoveSpeed * npc.Body.MobilityFactor() *
                EquipmentMath.WetMovementFactor(world, npc) *
                alignmentFactor * world.TickDeltaTime;

            // §40.18-B: deep-water strokes are slower than a walk on land.
            // Keyed off the SWIMMER's tile, so the slowdown starts once she is
            // in the water and ends when she has climbed out.
            if (world.Tiles.Items.TryGetValue(npc.Tile, out var swimStandTile) &&
                IsSwimTile(swimStandTile))
            {
                movementPerTick *= SwimSpeedFactor;
            }

            var distance = HexSpatialMath.Distance(npc.Position, target);

            // §21.21B v9: reached the takeoff point — launch the hop (the
            // flight block owns the window from here). No tile switch / path
            // advance now; that happens on touchdown.
            if (hopApproach && distance <= movementPerTick)
            {
                npc.Position = npc.Movement.HopFrom;
                npc.Movement.HopArmed = false;
                npc.Movement.HopPathIndex = npc.Movement.PathIndex;
                npc.Movement.HopCrossed = false;
                npc.Movement.HopTimer = HexHopTuning.WindowSeconds(npc.Movement.HopUp);
                npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(
                    HexSpatialMath.Normalize(npc.Movement.HopTo - npc.Movement.HopFrom));
                npc.Movement.Status = MovementStatus.Waiting;
                Trace.Emit(world, npc.Id, "HopStarted",
                    $"{(npc.Movement.HopUp ? "Up" : "Down")} " +
                    $"From={Trace.FormatPos(npc.Movement.HopFrom)} To={Trace.FormatPos(npc.Movement.HopTo)}");
                continue;
            }

            if (distance <= movementPerTick)
            {
                npc.Position = target;
                npc.CurrentJunction = targetJunctionId;

                var previousTile = npc.Tile;
                if (targetJunction.Tiles.Count > 0)
                {
                    var newTile = targetJunction.Tiles[0];
                    if (newTile != previousTile)
                    {
                        npc.Tile = newTile;
                        SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, npc.Tile);
                        Trace.Emit(world, npc.Id, "EnteredTile",
                            $"From={previousTile.Q},{previousTile.R} To={npc.Tile.Q},{npc.Tile.R}");

                        // §40.18-B: plunged from land into deep water — tread in
                        // place for a beat before stroking off (the view plays
                        // the jump-in + treading idle).
                        TryBeginSwimEntry(world, npc, previousTile, newTile);
                    }
                }

                npc.Movement.PathIndex++;

                Trace.Emit(world, npc.Id, "JunctionReached",
                    $"Junction={targetJunctionId.Value} Pos={Trace.FormatPos(target)} " +
                    $"Step={npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}");

                if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
                {
                    npc.Movement.IsMoving = false;
                    npc.Movement.Status = MovementStatus.Arrived;
                    Trace.Emit(world, npc.Id, "MovementCompleted",
                        $"FinalJunction={targetJunctionId.Value} Tile={npc.Tile.Q},{npc.Tile.R} " +
                        $"Pos={Trace.FormatPos(npc.Position)}");
                }
            }
            else
            {
                npc.Position += direction * movementPerTick;
                npc.Movement.Status = MovementStatus.Moving;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);
                Trace.Emit(world, npc.Id, "MovementStep",
                    $"Pos={Trace.FormatPos(npc.Position)} -> Junction={targetJunctionId.Value} " +
                    $"Dist={distance:F3} Speed={movementPerTick:F3} Align={alignmentFactor:F2} " +
                    $"Rot={npc.RotationDegrees:F1}");
            }
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

            // Spec 29G: ground rest plans have no target object — the last
            // step says what to do once the walk (if any) is over.
            var lastStep = npc.Plan.Steps.Count > 0 ? npc.Plan.Steps[npc.Plan.Steps.Count - 1] : null;
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
                    if (wetWorn is null || wetWorn.Wetness <= 0.5f || RackHoldsItem(world, worldObject))
                    {
                        PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                        PlanInterruption.Abort(world, npc,
                            "Cannot hang (nothing wet or rack occupied)");
                        npc.Mind.CurrentGoal = GoalType.None;
                        continue;
                    }
                }

                // Spec 29F.3: recipe ingredients validated at start.
                if (interaction.Type == InteractionType.Craft)
                {
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
                    var isBoulder = definition.Tags.Contains("Boulder");
                    // Spec §54: yucca is cut with a BLADE — a knife or an axe (not
                    // a saw or pickaxe); trees still need an axe/saw; boulders the
                    // pickaxe.
                    var isYucca = definition.Tags.Contains("Yucca");
                    var hasChopTool = npc.Inventory.Items.Contains("tool.axe_stone") ||
                        npc.Inventory.Items.Contains("tool.saw");
                    var hasBlade = npc.Inventory.Items.Contains("tool.knife") ||
                        npc.Inventory.Items.Contains("tool.axe_stone");
                    var toolOk = isBoulder
                        ? npc.Inventory.Items.Contains("tool.pickaxe_stone")
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

                    // The saw fells trees twice as fast (spec 35.2).
                    if (!isBoulder && npc.Inventory.Items.Contains("tool.saw"))
                    {
                        harvestDurationDivisor = 2;
                    }
                }

                // Spec §54: splitting a log into sticks needs a chopping tool.
                if (interaction.Type == InteractionType.Process)
                {
                    var isCoconut = definition.Tags.Contains("Coconut");
                    var hasChopTool = npc.Inventory.Items.Contains("tool.axe_stone") ||
                        npc.Inventory.Items.Contains("tool.saw");
                    var hasCoconutBlade = npc.Inventory.Items.Contains("tool.knife") ||
                        npc.Inventory.Items.Contains("tool.axe_stone");
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
                if (interaction.Type == InteractionType.Butcher &&
                    !npc.Inventory.Items.Contains("tool.knife"))
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
                    var canFrictionLight = npc.Needs.ThermalComfort < -0.35f;
                    var missingLighter = worldObject.ResourceAmount <= 0f &&
                        !npc.Inventory.Items.Contains("tool.lighter") &&
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

                    Trace.Emit(world, npc.Id, "ExecProgress",
                        $"{npc.Execution.CurrentInteraction} Progress={progress:P0} " +
                        $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
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
                    // Item moves from world to inventory; the world object is gone,
                    // so occupancy flags die with it (spec 29B.2).
                    npc.Inventory.Items.Add(new ItemInstance(worldObject.DefinitionId)
                    {
                        Wetness = worldObject.Wetness,
                        Durability = worldObject.Durability
                    });
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    Trace.Emit(world, npc.Id, "ItemPickedUp",
                        $"Def={worldObject.DefinitionId} Obj={worldObject.Id.Value} " +
                        $"Inventory=[{string.Join(",", npc.Inventory.Items)}] ({npc.Inventory.Items.Count}/{npc.Inventory.Capacity})");
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
                        Durability = worldObject.Durability
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
                else if (completedInteraction.Type == InteractionType.Craft)
                {
                    // Spec 29F.3 / §54 (R2): inputs consumed from RecipeCatalog
                    // via ConsumeRecipeInputs; the per-goal arm below only places
                    // the OUTPUT (tool / worn / furniture / side-effect). The
                    // firewood→stick rewire (§54 phase 1) edits the catalog, not
                    // these arms.
                    ConsumeRecipeInputs(npc, npc.Plan.Goal);
                    switch (npc.Plan.Goal)
                    {
                        case GoalType.CraftBandage:
                            npc.Needs.Bandages++;
                            npc.Needs.HerbalBandages++; // spec 44: this one is gathered plantain -> leaf-wrap decal
                            Trace.Emit(world, npc.Id, "BandageCrafted",
                                $"Bandages={npc.Needs.Bandages} Herbal={npc.Needs.HerbalBandages}");
                            break;
                        case GoalType.CraftSpear:
                            GiveOrDrop(world, npc, "tool.spear");
                            Trace.Emit(world, npc.Id, "CraftedSpear",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CookMeat:
                            GiveOrDrop(world, npc, "food.meat_cooked");
                            Trace.Emit(world, npc.Id, "MeatCooked",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftLeather:
                            ResolveWearConflicts(world, npc, "clothing.leather_pants");
                            npc.WornItems.Add("clothing.leather_pants");
                            EquipmentMath.Recalculate(world, npc);
                            Trace.Emit(world, npc.Id, "CraftedLeather",
                                $"Pants worn. Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                            break;
                        case GoalType.CraftAxe:
                            GiveOrDrop(world, npc, "tool.axe_stone");
                            Trace.Emit(world, npc.Id, "CraftedAxe",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftPickaxe:
                            GiveOrDrop(world, npc, "tool.pickaxe_stone");
                            Trace.Emit(world, npc.Id, "CraftedPickaxe",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
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
                        case GoalType.CraftBow:
                            GiveOrDrop(world, npc, "tool.bow");
                            Trace.Emit(world, npc.Id, "CraftedBow",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftArrows:
                            GiveOrDrop(world, npc, "resource.arrow");
                            GiveOrDrop(world, npc, "resource.arrow");
                            GiveOrDrop(world, npc, "resource.arrow");
                            Trace.Emit(world, npc.Id, "CraftedArrows",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftRope:
                            GiveOrDrop(world, npc, "resource.rope");
                            Trace.Emit(world, npc.Id, "CraftedRope",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftCloth:
                            GiveOrDrop(world, npc, "resource.cloth");
                            Trace.Emit(world, npc.Id, "CraftedCloth",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftKnife:
                            GiveOrDrop(world, npc, "tool.knife");
                            Trace.Emit(world, npc.Id, "CraftedKnife",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
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
                    worldObject.ResourceAmount = 0f;
                    Trace.Emit(world, npc.Id, "CoconutDrained",
                        $"{worldObject.DefinitionId} water consumed");

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

        // Raise it if it is now stocked and a hammer is at hand — carried, or
        // simply lying at the build site (the tool waits at the workbench). This
        // keeps the hammer a real requirement without demanding the one girl who
        // stocks the last stone also happen to be carrying it.
        var hammerAtSite = false;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId != "tool.hammer")
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

        // Spec §54: a campfire is piled from stones, and the leaf mat is
        // hand-lashed. Rigid furniture still needs the builder's hammer.
        var needsHammer = site.BuildProduct != "campfire.spot" && site.BuildProduct != "bed.leaf";
        if (BuildSiteMath.IsStocked(site) &&
            (!needsHammer || npc.Inventory.Items.Contains("tool.hammer") || hammerAtSite))
        {
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
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

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
    private const float TalkRelationshipGain = 0.15f;

    // Spec 28.15B: quarrels and refusal-by-dislike.
    private const float QuarrelInitiatorSocialGain = 0.15f;
    private const float QuarrelListenerSocialGain = 0.10f;
    private const float QuarrelAffinityLoss = 0.36f;
    private const float QuarrelEmbarrassment = 0.30f;
    private const float RefusalAffinityThreshold = -0.25f;
    private const float LonelinessOverrideThreshold = 0.25f;
    private const float RejectionAffinityPenalty = 0.15f;

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

            var targetBusy = target.Execution.Status == ExecutionStatus.InProgress &&
                target.Execution.CurrentInteraction != InteractionType.Talk;
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
                }

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
        var feedSev = t.Needs.Hunger >= 0.55f ? t.Needs.Hunger : 0f;
        var consoleSev = tick < t.Mind.GrievingUntilTick ? 0.5f : 0f;
        if (t.Needs.Stress > 0.6f)
        {
            consoleSev = System.Math.Max(consoleSev, t.Needs.Stress * 0.6f);
        }

        var kind = AidKind.Treat;
        severity = treatSev;
        if (medSev > severity) { severity = medSev; kind = AidKind.Medicate; }
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

            Trace.Emit(world, npc.Id, "AidStarted",
                $"Kind={kindNow} With NPC{targetId.Value} Severity={severity:F2} " +
                $"Duration={Spec53.AidDuration}ticks");
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
                $"Aff={helperRel.Affinity:F2} (+{gain:F2}) MyCompassion={npc.Needs.Compassion:F2}");
            Trace.Emit(world, target.Id, "RelationshipChanged",
                $"NPC{target.Id.Value}->NPC{npc.Id.Value} Aff={wardRel.Affinity:F2} (aided)");

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

    // Spec 35.5: the rack holds one item — a wearable at its junction.
    internal static bool RackHoldsItem(WorldState world, WorldObjectState rack)
    {
        if (rack.Junctions.Count == 0)
        {
            return false;
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.Id.Value != rack.Id.Value && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(rack.Junctions[0]) &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Layer is not null)
            {
                return true;
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
        var spot = FindSpacedFurnitureSpot(world, campfire) ?? npc.CurrentJunction;
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

    private static JunctionId? FindSpacedFurnitureSpot(WorldState world, WorldObjectState campfire)
    {
        if (campfire.Junctions.Count == 0)
        {
            return null;
        }

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
                !IsNearOtherFurniture(world, j.Tiles[0]))
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
                !IsNearOtherFurniture(world, junction.Tiles[0]))
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
        var spot = FindSpacedFurnitureSpot(world, campfire) ?? npc.CurrentJunction;
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
                    GiveOrDrop(world, npc, new ItemInstance(drop.DefinitionId));
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
                    GiveOrDrop(world, npc, new ItemInstance(drop.DefinitionId));
                }
            }
        }
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
                        GiveOrDrop(world, npc, new ItemInstance(drop.DefinitionId));
                        continue;
                    }

                    spawnTile = scatter.Item1;
                    spawnJunction = freeJunction;
                    used.Add(freeJunction);
                }

                var spawned = WorldObjectMutations.SpawnObject(world, drop.DefinitionId, fragment, spawnTile, spawnJunction);
                spawned.SpawnTick = world.Tick;
                spawned.ResourceAmount = drop.DefinitionId == "food.coconut_pierced" ? 1f : 0f;
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
        npc.Plan.TargetJunctionId = target.Junctions.Count > 0 ? target.Junctions[0] : npc.Plan.TargetJunctionId;
        for (var i = nextInteract; i < npc.Plan.Steps.Count; i++)
        {
            if (npc.Plan.Steps[i].Type == PlanStepType.Interact)
            {
                npc.Plan.Steps[i].TargetObject = target.Id;
                npc.Plan.Steps[i].TargetJunction = npc.Plan.TargetJunctionId;
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
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kind;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);

                if (kind == InteractionType.Sit &&
                    world.Junctions.Items.TryGetValue(spot, out var ledge) && PlanningSystem.IsLedge(world, ledge))
                {
                    FaceLowerSide(world, npc, ledge);
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

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
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
        if (npc.Memory.Dangers.Count > 0 ||
            npc.Needs.Hunger >= SleepInterruptHunger ||
            npc.Needs.Thirst >= SleepInterruptThirst)
        {
            return false;
        }

        // Spec §49: sleep THROUGH the night in one lie (the user's ask — "let
        // them sleep more") — no energy cap after dark. By day, only nap while
        // genuinely tired.
        var night = world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
        return night || npc.Needs.Energy < SleepWakeEnergyDay;
    }

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
                $"CoolOff on the ground Duration={Spec49.CoolOffDwellTicks}ticks");
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
            ShouldKeepCooling(world, npc))
        {
            npc.Mind.CoolRearmCount++;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;
            Trace.Emit(world, npc.Id, "CoolContinued",
                $"Rearm={npc.Mind.CoolRearmCount} Thermal={npc.Needs.ThermalDiscomfort:F2} Sun={npc.SunExposure:F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, "CooledOff",
            $"Thermal={npc.Needs.ThermalDiscomfort:F2} Sun={npc.SunExposure:F2} Rearms={npc.Mind.CoolRearmCount}");

        // A short refractory window so she doesn't instantly re-select CoolOff even
        // if discomfort still hovers just under the clear edge (mirrors Sit's 240t).
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = GoalType.CoolOff,
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
    private static void ClaimLyingFootprint(WorldState world, NPCState npc, JunctionId center)
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

    // Spec 29G: legs over the edge. Face straight out into the drop: use the
    // high-to-low tile-center normal, which is perpendicular to the hex edge.
    private static void FaceLowerSide(WorldState world, NPCState npc, Junction ledge)
    {
        var minElevation = int.MaxValue;
        var maxElevation = int.MinValue;
        var lowCenterSum = Float2.Zero;
        var highCenterSum = Float2.Zero;
        var lowCount = 0;
        var highCount = 0;
        foreach (var coord in ledge.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            var center = HexSpatialMath.TileToWorld(tile.Coord);
            if (tile.Elevation < minElevation)
            {
                minElevation = tile.Elevation;
                lowCenterSum = center;
                lowCount = 1;
            }
            else if (tile.Elevation == minElevation)
            {
                lowCenterSum += center;
                lowCount++;
            }

            if (tile.Elevation > maxElevation)
            {
                maxElevation = tile.Elevation;
                highCenterSum = center;
                highCount = 1;
            }
            else if (tile.Elevation == maxElevation)
            {
                highCenterSum += center;
                highCount++;
            }
        }

        if (lowCount == 0 || highCount == 0 || minElevation == maxElevation)
        {
            return;
        }

        var lowCenter = lowCenterSum * (1f / lowCount);
        var highCenter = highCenterSum * (1f / highCount);
        var direction = HexSpatialMath.Normalize(lowCenter - highCenter);
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(direction);
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
        if (itemId is null || !npc.Inventory.Items.Contains(itemId) ||
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

                Trace.Emit(world, npc.Id, "ExecProgress",
                    $"{verb} (inventory) Progress={progress:P0} " +
                    $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                return;
            }

            var needsBefore = Trace.FormatNeeds(npc.Needs);
            ApplyEffectsScaled(npc, interaction.Effects, total > 0 ? 1f / total : 1f);
            npc.Inventory.Items.Remove(itemId);
            // §55: a consumed item may transform rather than vanish — cracking a
            // coconut (Drink) yields the opened husk straight into the hand.
            foreach (var yield in interaction.Yields)
            {
                for (var n = 0; n < yield.Count; n++)
                {
                    npc.Inventory.Items.Add(yield.DefinitionId);
                }
            }
            var needsAfter = Trace.FormatNeeds(npc.Needs);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;

            Trace.Emit(world, npc.Id, "ItemConsumed",
                $"{itemId} {verb} from inventory " +
                $"NeedsBefore=[{needsBefore}] NeedsAfter=[{needsAfter}] " +
                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");

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

// Spec 19.7A: derives the clock from the tick and drives the temperature
// sinusoid. Must run before needs/temperature systems within the Slow layer.
public sealed class EnvironmentSystem : ISimulationSystem
{
    public string Name => nameof(EnvironmentSystem);

    public TickLayer Layer => TickLayer.Slow;

    public const int DayLengthTicks = 2400;
    // Spec 42: a real tropical swing — 25° at the 15:00 peak (dressed girls
    // cross the >24 undress gate and strip for the day), 6° at 03:00 (layers
    // and the campfire earn their keep at night).
    private static float BaseTemperature => SimBalance.BaseTemperature;
    private static float TemperatureAmplitude => SimBalance.TemperatureAmplitude;

    public void Run(WorldState world)
    {
        var progress = (world.Tick % DayLengthTicks) / (float)DayLengthTicks;
        var previousPhase = world.Environment.Phase;

        world.Environment.TimeOfDayNormalized = progress;
        world.Environment.Phase = progress switch
        {
            < 0.25f => DayPhase.Morning,
            < 0.5f => DayPhase.Day,
            < 0.75f => DayPhase.Evening,
            _ => DayPhase.Night
        };

        // Warmest at 15:00 (progress 0.375), coldest at 03:00 (progress 0.875).
        world.Environment.GlobalTemperature = BaseTemperature +
            TemperatureAmplitude * System.MathF.Sin((progress - 0.125f) * 2f * System.MathF.PI);

        // Spec 35.4/35.5: UV over the daylight half, peaking 0.9 at midday;
        // rain halves it and cools the air 3 degrees.
        world.Environment.UvIndex = progress < 0.5f
            ? 0.9f * System.MathF.Sin(System.MathF.PI * progress / 0.5f)
            : 0f;
        if (world.Environment.IsRaining)
        {
            world.Environment.UvIndex *= 0.5f;
            world.Environment.GlobalTemperature -= SimBalance.RainTempDrop;
        }

        if (world.Environment.Phase != previousPhase)
        {
            Trace.EmitSystem(world, "PhaseChanged",
                $"{previousPhase}->{world.Environment.Phase} " +
                $"Clock={FormatClock(progress)} Temp={world.Environment.GlobalTemperature:F1}");
        }

        RebuildShadows(world, progress);
    }

    // Spec 43: cast shadows. The sun rises east (p=0, 06:00), peaks south at
    // noon (p=0.25) and sets west (p=0.5); elevation follows the same sine
    // (8° at the horizons, 65° at noon). Every tile marches a short ray
    // TOWARD the sun: a blocker (tall hex, +2 for canopy/indoor walls)
    // shades it when its silhouette clears the sun line. Dawn/dusk throw
    // 3-5 tile shadows off a cliff; at noon a 1-step ledge shades nothing.
    private const int ShadowRaySteps = 5;
    private const float ElevationWorldStep = 0.55f; // renderer's step height
    private const float CanopyVirtualSteps = 2f;

    private static void RebuildShadows(WorldState world, float progress)
    {
        world.ShadedTiles.Clear();
        if (progress >= 0.5f)
        {
            world.SunElevationDegrees = 0f;
            world.SunDirection = Float2.Zero;
            return; // night — no sun, shade is moot (UV is 0 anyway)
        }

        var arc = System.MathF.Sin(System.MathF.PI * progress / 0.5f); // 0..1..0
        var azimuth = System.MathF.PI * (progress / 0.5f); // east -> west
        var sunDir = new Float2(System.MathF.Cos(azimuth), -System.MathF.Sin(azimuth));
        var elevationDeg = 8f + 57f * arc;
        world.SunDirection = sunDir;
        world.SunElevationDegrees = elevationDeg;

        // Rise of the sun line per horizontal tile step, in ELEVATION units.
        var stepWorld = HexSpatialMath.HexRadius * HexSpatialMath.Sqrt3;
        var risePerStep = System.MathF.Tan(elevationDeg * System.MathF.PI / 180f)
            * (stepWorld / ElevationWorldStep);

        // Canopy/wall blockers: +2 virtual steps on their tile; a canopy tile
        // is also always shaded itself (standing under the palm).
        _shadowExtra.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.Tags.Contains("Shade"))
            {
                _shadowExtra[obj.Tile] = CanopyVirtualSteps;
                world.ShadedTiles.Add(obj.Tile);
            }
        }

        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (tile.Flags.HasFlag(TileFlags.Indoor))
            {
                // Roofed: always out of the sun, and the walls block others.
                world.ShadedTiles.Add(pair.Key);
                _shadowExtra.TryGetValue(pair.Key, out var prior);
                _shadowExtra[pair.Key] = System.Math.Max(prior, CanopyVirtualSteps);
            }
        }

        foreach (var pair in world.Tiles.Items)
        {
            if (world.ShadedTiles.Contains(pair.Key))
            {
                continue;
            }

            var origin = HexSpatialMath.TileToWorld(pair.Key);
            var myElev = (float)pair.Value.Elevation;
            for (var k = 1; k <= ShadowRaySteps; k++)
            {
                var sample = new Float2(
                    origin.X + sunDir.X * stepWorld * k,
                    origin.Y + sunDir.Y * stepWorld * k);
                var blockerCoord = WorldToTile(sample);
                if (!world.Tiles.Items.TryGetValue(blockerCoord, out var blocker))
                {
                    continue;
                }

                _shadowExtra.TryGetValue(blockerCoord, out var extra);
                var blockerHeight = blocker.Elevation + extra;
                if (blockerHeight >= myElev + risePerStep * k)
                {
                    world.ShadedTiles.Add(pair.Key);
                    break;
                }
            }
        }
    }

    private static readonly System.Collections.Generic.Dictionary<TileCoord, float> _shadowExtra = new();

    // Inverse of HexSpatialMath.TileToWorld (linear) with axial rounding.
    private static TileCoord WorldToTile(Float2 world)
    {
        var r = world.Y / (HexSpatialMath.HexRadius * HexSpatialMath.HexRowStepFactor);
        var q = world.X / (HexSpatialMath.HexRadius * HexSpatialMath.HexWidthFactor) - r * 0.5f;
        // Cube rounding (s = -q-r) picks the nearest hex.
        var s = -q - r;
        var rq = System.MathF.Round(q);
        var rr = System.MathF.Round(r);
        var rs = System.MathF.Round(s);
        var dq = System.MathF.Abs(rq - q);
        var dr = System.MathF.Abs(rr - r);
        var ds = System.MathF.Abs(rs - s);
        if (dq > dr && dq > ds)
        {
            rq = -rr - rs;
        }
        else if (dr > ds)
        {
            rr = -rq - rs;
        }

        return new TileCoord((int)rq, (int)rr);
    }

    public static string FormatClock(float progress)
    {
        var hours = (6f + progress * 24f) % 24f;
        var h = (int)hours;
        var m = (int)((hours - h) * 60f);
        return $"{h:D2}:{m:D2}";
    }
}

public sealed class NeedsDecaySystem : ISimulationSystem
{
    public string Name => nameof(NeedsDecaySystem);

    public TickLayer Layer => TickLayer.Slow;

    // Balance knobs (SimBalance / HexTuningConfig). The old const names are
    // kept as live shims so every call site below is untouched.
    private static float HungerRate => SimBalance.HungerRate; // pond removal + hex-hop ceremony rebalance: water/food trips got longer
    private static float EnergyRate => SimBalance.EnergyRate; // spec 42: ~1 bar/day
    private static float ComfortRate => SimBalance.ComfortRate;
    private static float SocialRate => SimBalance.SocialRate; // spec 28.15A
    // Spec 42.A: base eased 0.020 -> 0.018 as the compensating loosening for
    // the sweat multiplier below — same multi-dimensional-budget lesson as
    // §40.18 (0.020 + factor 0.25 broke seed 777; 0.05 alone was homeopathy).
    private static float ThirstRate => SimBalance.ThirstRate; // spec 29E.1; pond removal + §21.21B v5 clamp rebalance

    // Spec 42.A: extra thirst per unit of positive ThermalComfort (sweat).
    private static float SweatThirstFactor => SimBalance.SweatThirstFactor;

    // Spec §49 knobs (moved to HexTuningConfig in the tuning pass).
    private static float SickTorsoPerSlowTick => SimBalance.SickTorsoPerSlowTick;   // pace the budget pay-down (~0.08 over ~40 slow ticks)
    private static float SickTorsoFloor => SimBalance.SickTorsoFloor;          // sickness can't grind the torso below this
    private static float SickComfortPerSlowTick => SimBalance.SickComfortPerSlowTick; // feeling lousy while sick
    private static float AmbientSocialGain => Spec49.AmbientGain; // near company loneliness slowly reverses
    private const float AmbientSocialCap = 0.6f;         // ...but only a real chat lifts you past this (§49 ambient social)
    private const float SleepComfortNightSlowTicks = 75f; // Evening+Night ≈ 75 slow ticks

    // Spec §49: comfort gained per slow tick while sleeping, from surface +
    // fireside + sun/rain. A full ~75-slow-tick night sums to the design
    // targets; penalties shave the gain but never invert it (a bed in the rain
    // still nets ~0.70, grass in the sun just nets ~0).
    private static float SleepComfortPerSlowTick(WorldState world, NPCState npc)
    {
        var perNight = Spec49.SleepComfortGrassNight;
        var onBed = false;
        if (npc.Execution.TargetObject is { } objId &&
            world.Entities.Objects.TryGetValue(objId, out var obj))
        {
            perNight = obj.DefinitionId switch
            {
                "bed.basic" => Spec49.SleepComfortBedNight,
                "bed.leaf" => Spec49.SleepComfortLeafNight,
                _ => perNight
            };
            onBed = obj.DefinitionId is "bed.basic" or "bed.leaf";
        }

        // A worn jacket/coat padding the bare ground beats sleeping on plain
        // dirt (bunched under the body). A bed already provides its own
        // surface, so this only sweetens the groundless case.
        if (!onBed && WearsJacketOrCoat(world, npc))
        {
            perNight += Spec49.SleepComfortJacketPadNight;
        }

        if (TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f)
        {
            perNight += Spec49.SleepComfortFireBonusNight;
        }

        var roofed = world.Tiles.Items.TryGetValue(npc.Tile, out var tile) &&
            tile.Flags.HasFlag(TileFlags.Indoor);
        var daytime = world.Environment.Phase is DayPhase.Day or DayPhase.Morning;
        if (daytime && !roofed && !TemperatureSystem.IsShaded(world, npc.Tile))
        {
            perNight -= Spec49.SleepComfortSunPenaltyNight;
        }

        if (world.Environment.IsRaining && !roofed)
        {
            perNight -= Spec49.SleepComfortRainPenaltyNight;
        }

        if (perNight < 0f)
        {
            perNight = 0f;
        }

        return perNight / SleepComfortNightSlowTicks;
    }

    // A "jacket/coat" for padding = a torso-covering outer garment (the coat
    // and the leather/heavy jackets qualify; bikini tops, tees and boots do
    // not). Any future outer torso layer is picked up automatically.
    private static bool WearsJacketOrCoat(WorldState world, NPCState npc)
    {
        foreach (var item in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) ||
                !def.Covers.Contains(BodyPart.Torso))
            {
                continue;
            }

            if (def.Layer == WearLayer.Outerwear || def.Id == "clothing.coat")
            {
                return true;
            }
        }

        return false;
    }

    // Spec §49: is there an awake, settled housemate within perception right
    // now? Powers the passive ambient-social trickle.
    private static bool HasNearbyCompanion(NPCState npc)
    {
        foreach (var agent in npc.Perception.Agents)
        {
            if (agent.IsReachable && !agent.IsMoving)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly BodyPart[] AllBodyParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    // Spec 40.6: standing on or next to a water tile (the bank you drink from).
    private static bool IsAtOrBesideWater(WorldState world, TileCoord tile)
    {
        if (world.Tiles.Items.TryGetValue(tile, out var here) && here.Flags.HasFlag(TileFlags.Water))
        {
            return true;
        }

        foreach (var dir in HexDirection.All)
        {
            if (world.Tiles.Items.TryGetValue(new TileCoord(tile.Q + dir.DQ, tile.R + dir.DR), out var n) &&
                n.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
    }

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var prevHunger = npc.Needs.Hunger;
            var prevEnergy = npc.Needs.Energy;
            var prevComfort = npc.Needs.Comfort;
            var prevSocial = npc.Needs.Social;

            // Spec 31C.7A: a sleeping body burns less — hour-long sleep
            // blocks must not guarantee a starving wake-up.
            var sleeping = npc.Execution.CurrentInteraction == InteractionType.Sleep;
            var metabolism = sleeping ? 0.4f : 1f;
            // Spec 42.A: sweating burns water — overheating scales thirst by
            // up to +25% at heatstroke-level heat (ThermalComfort +1). Reads
            // the previous slow tick's signed comfort; cold side is free (a
            // shivering body does not sweat). SOFT knob: SweatThirstFactor.
            var sweat = 1f + SweatThirstFactor * System.Math.Max(0f, npc.Needs.ThermalComfort);
            npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger + HungerRate * metabolism);
            npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst + ThirstRate * metabolism * sweat);
            npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy - EnergyRate);

            // §54.11: faster sleep recovery — a base lift (shorter nights) plus a
            // fireside bonus and a bed bonus, so a bed built by the fire pays off
            // in time awake. One place, both sleep paths (ground + bed) — `sleeping`
            // is true for either; the bed bonus keys off the slept-on object.
            if (sleeping)
            {
                var wake = SimBalance.SleepEnergyBaseBonus;
                if (TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f)
                {
                    wake += SimBalance.SleepEnergyFireBonus;
                }

                if (npc.Execution.TargetObject is { } bedId &&
                    world.Entities.Objects.TryGetValue(bedId, out var bedObj))
                {
                    wake += bedObj.DefinitionId switch
                    {
                        "bed.basic" => SimBalance.SleepEnergyBasicBedBonus,
                        "bed.leaf" => SimBalance.SleepEnergyLeafBedBonus,
                        _ => 0f
                    };
                }

                npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + wake);
            }

            // Spec §49: unified sleep-comfort. Asleep, comfort no longer drains —
            // the surface + fire + sun + rain formula fills it (bare grass
            // ~0.05/night, +fire ~0.05, a bed ~1.0 minus sun/rain penalties).
            // Awake, the usual slow drain applies — UNLESS she's by a lit fire,
            // whose cosy warmth reverses the drain into a small comfort gain
            // (spec §49.8; drives the "Cozy" status chip too).
            if (sleeping && Spec49.SleepComfort)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(
                    npc.Needs.Comfort + SleepComfortPerSlowTick(world, npc));
            }
            else if (TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(
                    npc.Needs.Comfort + Spec49.AwakeFireComfortGain);
            }
            else
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - ComfortRate);
            }

            // §49.7: wet clothes cling — being soaked shaves a little comfort on
            // top (wet underwear counts here too: it doesn't slow you, but it's
            // still miserable). Just the fact of being wet.
            var maxWornWet = 0f;
            foreach (var worn in npc.WornItems)
            {
                if (worn.Wetness > maxWornWet)
                {
                    maxWornWet = worn.Wetness;
                }
            }
            if (maxWornWet > 0.5f)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - Spec49.WetComfortPenalty);
            }

            // Spec §49: passive "second action" socialising — being near an
            // awake, settled housemate while you do your own thing (eat, sit,
            // tend the fire) eases loneliness a touch, Sims-style. A trickle, not
            // a substitute: it can't lift Social past a modest cap, so a real
            // chat is still wanted to feel truly social.
            npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social - SocialRate);
            if (Spec49.AmbientSocial && !sleeping && npc.Needs.Social < AmbientSocialCap && HasNearbyCompanion(npc))
            {
                npc.Needs.Social = System.Math.Min(
                    AmbientSocialCap, npc.Needs.Social + AmbientSocialGain);
            }

            // Spec §53: compassion is SPENT witnessing un-helped suffering nearby
            // — the drain scales with the worst reachable neighbour's plight and
            // this girl's own CompassionTrait — and it recovers toward full when
            // the colony around her is well. (A completed aid tops it up directly
            // in RunAid.) Uses the perception snapshot's per-neighbour Suffering,
            // refreshed earlier this tick.
            if (Spec53.Enabled && !sleeping)
            {
                var worstNearby = 0f;
                foreach (var agent in npc.Perception.Agents)
                {
                    if (agent.IsReachable && agent.Suffering > worstNearby)
                    {
                        worstNearby = agent.Suffering;
                    }
                }
                npc.Needs.Compassion = worstNearby >= Spec53.SufferingThreshold
                    ? MathUtil.Clamp01(npc.Needs.Compassion -
                        Spec53.CompassionRate * worstNearby * npc.CompassionTrait)
                    : MathUtil.Clamp01(npc.Needs.Compassion + Spec53.RecoverRate);
            }

            // Spec §49: raw-water gut-rot damage-over-time — pay down the bounded
            // sickness budget a little each slow tick, floored at SickTorsoFloor
            // (sickness alone still can't kill; it leaves you fragile). Comfort
            // malaise rides the visible window so being ill feels bad.
            if (npc.Mind.SicknessDamageRemaining > 0f)
            {
                if (npc.Body.Parts[BodyPart.Torso] > SickTorsoFloor)
                {
                    var dock = System.Math.Min(SickTorsoPerSlowTick, npc.Mind.SicknessDamageRemaining);
                    npc.Body.Parts[BodyPart.Torso] =
                        System.Math.Max(SickTorsoFloor, npc.Body.Parts[BodyPart.Torso] - dock);
                    npc.Mind.SicknessDamageRemaining -= dock;
                    npc.Health = npc.Body.Mean();
                    if (npc.Body.VitalDestroyed(out var sickVital))
                    {
                        npc.Health = 0f;
                        Trace.Emit(world, npc.Id, "VitalPartDestroyed",
                            $"{sickVital} destroyed by sickness");
                    }
                }
                else
                {
                    // torso already at the floor — the rest of the budget is a
                    // no-op (sickness can't push a mauled body under), drain it.
                    npc.Mind.SicknessDamageRemaining = 0f;
                }
            }

            if (world.Tick < npc.Mind.SickUntilTick)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - SickComfortPerSlowTick);
            }

            // Spec 40.1: stamina. Its ceiling is how fed/rested/comfortable the
            // body is (you can't be spry starving). It drains while working or
            // moving, recovers fast while resting (sit/sleep), slowly while
            // idle — and moves toward that ceiling either way. Soft in v1: it
            // does NOT gate actions (that would collapse the economy); it only
            // colours the UI and nudges the rest goals (below).
            var staminaCeiling = MathUtil.Clamp01(
                0.30f + 0.35f * (1f - npc.Needs.Hunger) + 0.25f * npc.Needs.Energy +
                0.10f * npc.Needs.Comfort);
            var resting = npc.Execution.CurrentInteraction is
                InteractionType.Sit or InteractionType.Sleep;
            var working = npc.Execution.Status == ExecutionStatus.InProgress && !resting;
            var staminaDelta = resting ? SimBalance.StaminaRestGain
                : working ? -SimBalance.StaminaWorkDrain : SimBalance.StaminaIdleGain;
            npc.Needs.Stamina = MathUtil.Clamp(
                npc.Needs.Stamina + staminaDelta, 0f, staminaCeiling);

            // Spec 40.13: stress rises with danger/combat/pain/starvation and
            // ebbs in calm. A UI param, and a third path to collapse.
            var stressUp = npc.IsFighting || npc.Memory.Dangers.Count > 0 ||
                npc.Health < 0.6f || npc.Needs.Hunger >= 0.85f || npc.Needs.Thirst >= 0.85f;
            npc.Needs.Stress = MathUtil.Clamp01(npc.Needs.Stress + (stressUp ? SimBalance.StressUpRate : -SimBalance.StressDownRate));

            // Spec 40.13: collapse. Utterly spent stamina AND a body pushed to
            // the edge (starving, bleeding, or stress-overwhelmed) drops the
            // NPC unconscious — it lies helpless for ~80 ticks, then rises.
            // Rare by construction, so it barely perturbs the colony.
            if (world.Tick >= npc.Mind.FaintedUntilTick && npc.Needs.Stamina <= 0.01f &&
                (npc.Needs.Hunger >= 0.9f || npc.Needs.Blood < 0.25f || npc.Needs.Stress >= 0.95f) &&
                npc.Health > 0f)
            {
                npc.Mind.FaintedUntilTick = world.Tick + 80;
                PlanInterruption.Abort(world, npc, "Collapsed — unconscious");
                npc.Mind.CurrentGoal = GoalType.None;
                Trace.Emit(world, npc.Id, "Fainted",
                    $"Stamina={npc.Needs.Stamina:F2} Hunger={npc.Needs.Hunger:F2} Blood={npc.Needs.Blood:F2}");
            }

            // Spec 40.6: hygiene drifts down with living, up at the waterside
            // (washing while drinking/filling). Soft v1 — tracked for the UI,
            // no dedicated Bathe goal yet (that reshuffles the fragile colony).
            // Grubbying takes ~10 game days from clean to filthy (0.0004/slow
            // tick; was 0.004 — a single day, way too fast once dirt got real
            // smudge decals).
            npc.Needs.Hygiene = MathUtil.Clamp01(
                npc.Needs.Hygiene + (IsAtOrBesideWater(world, npc.Tile) ? SimBalance.HygieneWashGain : -SimBalance.HygieneDriftLoss));

            // Spec 40.2: blood. A badly wounded part (< 0.4) bleeds — the worse
            // the wound, the faster; blood refills slowly while fed and rested.
            // Gentle rates so the healthy colony is unaffected: only a mauled
            // NPC bleeds, and it's survivable if the wounds close. At zero the
            // NPC dies of blood loss.
            var worstPart = 1f;
            foreach (var part in AllBodyParts)
            {
                if (npc.Body.Parts[part] < worstPart)
                {
                    worstPart = npc.Body.Parts[part];
                }
            }

            // Spec 44: clotting — only a FRESH wound (heal01 < 0.3) bleeds;
            // once it starts closing the blood stops, so the deadly window is
            // the first hours after the mauling, not the whole two-day heal.
            var freshWound = false;
            foreach (var wound in npc.Wounds)
            {
                if (wound.Heal01 < 0.3f && wound.Severity >= 0.05f)
                {
                    freshWound = true;
                    break;
                }
            }

            if (worstPart < 0.4f && freshWound)
            {
                // Spec 40.3: a bandage in the pack dresses the worst wound —
                // patch it up, stem the blood, and it's consumed. First aid
                // that turns a mauling from fatal into survivable.
                // Last resort: only when actually bleeding out (blood < 0.35),
                // so it saves a life without re-shuffling the colony over
                // every scratch (every mauling survivor would otherwise shift
                // the deterministic dog-dance and tip fragile seeds).
                if (npc.Needs.Bandages > 0 && npc.Needs.Blood < SimBalance.BandageBloodThreshold)
                {
                    // Spec 44: spend the pre-made medkit bandages (spec 40.3)
                    // first; only a HERBAL dressing — crafted from gathered
                    // plantain leaves — leaves the leaf-wrap decal, so the
                    // plantain visual always means she actually gathered the
                    // leaves. When all remaining bandages are herbal, this one is.
                    bool herbal = npc.Needs.HerbalBandages >= npc.Needs.Bandages;
                    npc.Needs.Bandages--;
                    if (herbal) npc.Needs.HerbalBandages--;
                    foreach (var part in AllBodyParts)
                    {
                        if (npc.Body.IsSevered(part)) continue; // §50: a severed zone can't be dressed or healed
                        if (npc.Body.Parts[part] < 0.4f)
                        {
                            npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + 0.15f); // spec 42
                            // Spec 44: herbal -> leaf-wrap decal (gathered plantain);
                            // medkit -> plain gauze decal. A zone shows one or the
                            // other, never both.
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
                        }
                    }

                    npc.Health = npc.Body.Mean();
                    npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + 0.25f); // spec 42
                    Trace.Emit(world, npc.Id, "Bandaged",
                        $"Dressed the wounds (Health={npc.Health:F2})");
                }
                else if (npc.Needs.Pills > 0 && npc.Health < 0.3f)
                {
                    // Spec 40.3: pills — the last-resort backup to the bandage.
                    // Only at the brink (Health < 0.3, no bandage fired): spend
                    // a pill to lift the wounded parts and HP a step and stem
                    // the blood a little. Fires only for an NPC about to die, so
                    // it can save a life without shifting the healthy colony.
                    npc.Needs.Pills--;
                    foreach (var part in AllBodyParts)
                    {
                        if (npc.Body.IsSevered(part)) continue; // §50: a severed zone can't be healed
                        if (npc.Body.Parts[part] < 0.4f)
                        {
                            npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + 0.10f); // spec 42
                        }
                    }

                    npc.Health = npc.Body.Mean();
                    npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + 0.15f); // spec 42
                    Trace.Emit(world, npc.Id, "Medicated",
                        $"Took a pill at the brink (Health={npc.Health:F2})");
                }
                else
                {
                    // §46 difficulty pass: 0.06 -> 0.09. With the r4/r5
                    // survival fixes the colony won 6/6 — the game needs
                    // teeth back, and bleeding is the "sharp" death channel
                    // (dramatic, fightable with bandages) rather than the
                    // slow-grind ones we deliberately softened.
                    npc.Needs.Blood = System.Math.Max(0f, npc.Needs.Blood - (0.4f - worstPart) * SimBalance.BleedRateFactor);
                    if (npc.Needs.Blood <= 0f)
                    {
                        npc.Health = 0f;
                        Trace.Emit(world, npc.Id, "BledOut", $"Worst part {worstPart:F2} — blood loss");
                    }
                    else
                    {
                        Trace.Emit(world, npc.Id, "Bleeding",
                            $"Worst={worstPart:F2} Blood={npc.Needs.Blood:F2}");
                    }
                }
            }
            else if (npc.Needs.Blood < 1f && npc.Needs.Hunger < SimBalance.HealHungerGate)
            {
                // Spec 44: bed rest — sleeping knits blood x3, huddling by a
                // burning fire x2; a fed girl who lies low pulls through.
                var bloodPace = npc.Execution.CurrentInteraction == InteractionType.Sleep ? 3f
                    : TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f ? 2f
                    : 1f;
                npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + SimBalance.BloodRefillPerTick * bloodPace); // spec 44
            }

            // Spec 28.15B: post-quarrel embarrassment fades with time.
            npc.Social.Embarrassment = MathUtil.Clamp01(npc.Social.Embarrassment - 0.02f);

            // Spec 28.15B: affinity drifts toward neutral asymmetrically —
            // grudges fade fast, friendships cool slowly (a symmetric drift
            // would outrun talk gains and cap warmth at ~+0.2).
            foreach (var relationship in npc.Social.Relationships.Values)
            {
                var driftRate = relationship.Affinity < 0f ? 0.003f : 0.001f;
                relationship.Affinity = MathUtil.MoveTowards(relationship.Affinity, 0f, driftRate);
            }

            // §45 r5: emergency unload — the raft/hearth stockpile must never
            // cost a life. getFoodAvail requires inventory SPACE, and §45 r3
            // fills packs (3 raft logs + leaves + tools) that nothing ever
            // empties: on the r5 25-day soak 6 of 8 starvation deaths died at
            // Hunger=1.00 with 10/10 slots of logs/leaves and ZERO food —
            // coconuts abundant (25-39 on the ground, producers at cap) but
            // un-pick-up-able. A genuinely starving girl with a full pack and
            // no food in it now drops one carried resource per slow tick
            // (wood, then leaves, then stone — never tools) at her feet, so
            // GetFood can fire again. Last-resort by construction (hunger
            // >= 0.8), like food-sharing/theft — the healthy colony never
            // sees it.
            if (npc.Needs.Hunger >= 0.8f && !npc.Inventory.HasSpace &&
                npc.Inventory.FindFirstFood(world.Content) is null)
            {
                foreach (var junk in new[] { "resource.log", "resource.stick", "resource.palm_leaf", "resource.stone" })
                {
                    var idx = npc.Inventory.Items.FindIndex(i => i.DefinitionId == junk);
                    if (idx >= 0)
                    {
                        var item = npc.Inventory.Items[idx];
                        npc.Inventory.Items.RemoveAt(idx);
                        ExecutionSystem.DropItemAtFeet(world, npc, item);
                        Trace.Emit(world, npc.Id, "EmergencyUnload",
                            $"Dropped {junk} (Hunger={npc.Needs.Hunger:F2}, full pack, no food)");
                        break;
                    }
                }
            }

            // Spec 29C.2: starvation / dehydration cost HP. Without this an
            // NPC whose needs maxed out (food/water unreachable) hangs forever
            // — Health never falls, it never dies, its slot never frees. The
            // 0.95 gate sits above the 0.85 starving appraisal, so healthy
            // colonies that briefly spike lose nothing; only a truly stuck
            // agent drains to death.
            var starved = npc.Needs.Hunger >= SimBalance.StarveDeathThreshold;
            var parched = npc.Needs.Thirst >= SimBalance.StarveDeathThreshold;

            // Spec 40.5: emergent cooperation — a well-fed housemate already
            // standing beside someone starving at the death-brink hands over a
            // spare food item. Passive last-resort (no goal, no reroute): it
            // fires only on this exact adjacency, so like the pill it saves a
            // life without disturbing the healthy colony's routine.
            if (starved && npc.CurrentJunction is { } hungryJct)
            {
                foreach (var other in world.Entities.Npcs.Values)
                {
                    if (other.Id.Equals(npc.Id) || other.Health <= 0f ||
                        other.Needs.Hunger >= 0.4f || other.IsFighting ||
                        other.Mind.CurrentGoal == GoalType.Flee ||
                        other.CurrentJunction is not { } giverJct)
                    {
                        continue;
                    }

                    var adjacent = giverJct.Equals(hungryJct) ||
                        (world.Junctions.Items.TryGetValue(giverJct, out var gj) &&
                         gj.Neighbors.Contains(hungryJct));
                    if (!adjacent)
                    {
                        continue;
                    }

                    var food = other.Inventory.FindFirstFood(world.Content);
                    if (food is null)
                    {
                        continue;
                    }

                    other.Inventory.Items.Remove(food);
                    npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger - 0.5f);
                    starved = npc.Needs.Hunger >= SimBalance.StarveDeathThreshold;
                    Trace.Emit(world, npc.Id, "FoodShared",
                        $"Given {food} by NPC{other.Id.Value} (Hunger={npc.Needs.Hunger:F2})");
                    break;
                }
            }

            // Spec 40.5: theft — if still starving at the brink and nobody
            // shared, take food from an adjacent housemate who has some (a hard
            // choice under scarcity; the victim loses the meal). Mirrors the
            // food-sharing block but takes regardless of the victim's own state.
            if (starved && npc.CurrentJunction is { } thiefJct)
            {
                foreach (var victim in world.Entities.Npcs.Values)
                {
                    if (victim.Id.Equals(npc.Id) || victim.Health <= 0f ||
                        victim.CurrentJunction is not { } victimJct)
                    {
                        continue;
                    }

                    var adjacent = victimJct.Equals(thiefJct) ||
                        (world.Junctions.Items.TryGetValue(victimJct, out var vj) &&
                         vj.Neighbors.Contains(thiefJct));
                    if (!adjacent)
                    {
                        continue;
                    }

                    var loot = victim.Inventory.FindFirstFood(world.Content);
                    if (loot is null)
                    {
                        continue;
                    }

                    victim.Inventory.Items.Remove(loot);
                    npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger - 0.5f);
                    starved = npc.Needs.Hunger >= SimBalance.StarveDeathThreshold;
                    Trace.EmitSystem(world, "FoodStolen",
                        $"NPC{npc.Id.Value} stole {loot} from NPC{victim.Id.Value}");
                    break;
                }
            }

            if (starved || parched)
            {
                // §45 r5: attrition eased 0.03/0.05 -> 0.02/0.035. The 25-day
                // baseline showed every colony losing 1-2 girls to ACUTE
                // starvation episodes (a pinned need grinds a full body in
                // ~2.2 game hours — faster than the recovery loop can respond).
                // Death stays certain for a truly stuck agent; a girl who
                // reaches food/water mid-episode now lives to eat it.
                var damage = starved && parched ? SimBalance.StarveDamageBoth : SimBalance.StarveDamageOne; // §46 difficulty: restored to pre-r5 — safe now that sickness/fire/cold are fixed; at 0.025/0.045 the colony still won 10/12
                foreach (var part in AllBodyParts)
                {
                    npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] - damage);
                }

                npc.Health = npc.Body.Mean();
                if (npc.Body.VitalDestroyed(out _))
                {
                    npc.Health = 0f;
                }

                Trace.Emit(world, npc.Id, npc.Health <= 0f ? "StarvedToDeath" : "StarvationDamage",
                    $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                    $"Damage=-{damage:F2} Health={npc.Health:F2}");
            }
            // Spec 29C.2/19.3C + 40.8B: eat and rest to heal — but only damage
            // NOT held by open wounds. Each wound keeps its Severity "hostage":
            // the zone can regen up to (1 − open wound damage) and no further,
            // so a couple of coconuts never insta-heals a mauling.
            else if (npc.Health < 1f && npc.Needs.Hunger < SimBalance.HealHungerGate)
            {
                // §45 r5: regen 0.0018 -> 0.0030 (~0.45/day) and the gate
                // eased 0.5 -> 0.6 — the long-run colony hovers at hunger
                // ~0.5-0.6, so the old gate barely ever opened and bodies
                // never recovered between sickness/cold/hunger episodes;
                // every r5-baseline death was a body ground down over days
                // 15-24 with no regen in between. Still days, not hours.
                foreach (var part in AllBodyParts)
                {
                    if (npc.Body.IsSevered(part)) continue; // §50: severed zones never regen
                    var ceiling = MathUtil.Clamp01(1f - WoundMath.OpenWoundDamage(npc, part));
                    if (npc.Body.Parts[part] < ceiling)
                    {
                        npc.Body.Parts[part] = System.Math.Min(ceiling, npc.Body.Parts[part] + SimBalance.HealthRegenPerTick);
                    }

                    // Spec 44: the dressing (leaf wrap or gauze) comes off once
                    // the zone has healed.
                    if (npc.Body.Parts[part] > 0.7f)
                    {
                        npc.BandagedZones.Remove(part);
                        npc.GauzeZones.Remove(part);
                    }
                }

                npc.Health = npc.Body.Mean();
            }

            // Spec 40.8B: every wound closes on its own clock, PACED BY
            // ACTIVITY — sleeping knits flesh twice as fast, marching halves
            // it. Each healed slice returns its share of the zone's HP, so
            // health comes back exactly as the wounds close, wound by wound.
            if (npc.Wounds.Count > 0)
            {
                var pace = npc.Execution.CurrentInteraction == InteractionType.Sleep ? 2f
                    : npc.Movement.Status == MovementStatus.Moving ? 0.5f
                    : 1f;

                for (var wi = npc.Wounds.Count - 1; wi >= 0; wi--)
                {
                    var wound = npc.Wounds[wi];
                    var slice = System.Math.Min(WoundMath.HealPerSlowTick * pace, 1f - wound.Heal01);
                    wound.Heal01 += slice;
                    // §50: the stump wound on a severed zone still clots (heal01
                    // climbs so the bleed eventually stops), but its HP never
                    // returns — the limb is gone, not mending.
                    if (!npc.Body.IsSevered(wound.Zone))
                    {
                        npc.Body.Parts[wound.Zone] = MathUtil.Clamp01(
                            npc.Body.Parts[wound.Zone] + wound.Severity * slice);
                    }

                    if (wound.Heal01 >= 1f)
                    {
                        Trace.Emit(world, npc.Id, "WoundHealed",
                            $"{wound.Zone} wound #{wound.Id} closed");
                        npc.Wounds.RemoveAt(wi);
                    }
                }

                npc.Health = npc.Body.Mean();
            }

            Trace.Emit(world, npc.Id, "NeedsDecay",
                $"Hunger={prevHunger:F3}->{npc.Needs.Hunger:F3}(+{HungerRate}) " +
                $"Energy={prevEnergy:F3}->{npc.Needs.Energy:F3}(-{EnergyRate}) " +
                $"Comfort={prevComfort:F3}->{npc.Needs.Comfort:F3}(-{ComfortRate}) " +
                $"Social={prevSocial:F3}->{npc.Needs.Social:F3}(-{SocialRate}) " +
                $"Sweat={sweat:F2}");
        }

        // Spec 40.16: joint-plan advisor trigger. On the rising edge of a
        // colony-wide crisis, consult the advisor (a null-object by default, so
        // this is inert) and trace the onset. Formalizes the trigger + I/O; a
        // host swaps DireStraits.Advisor for an LLM-backed one to act on it.
        var crisis = AI.DireStraits.Assess(world);
        if (crisis is not null && !world.ColonyInDireStraits)
        {
            world.ColonyInDireStraits = true;
            var advice = AI.DireStraits.Advisor.Advise(crisis);
            Trace.EmitSystem(world, "DireStraits",
                $"starving={crisis.StarvingCount} wounded={crisis.WoundedCount}/{crisis.LivingCount}" +
                (string.IsNullOrEmpty(advice) ? "" : $" advice={advice}"));
        }
        else if (crisis is null)
        {
            world.ColonyInDireStraits = false;
        }
    }
}

public sealed class TemperatureSystem : ISimulationSystem
{
    public string Name => nameof(TemperatureSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var prevThermal = npc.Needs.ThermalDiscomfort;

            // Spec 29C.4: warmth is no longer a pure good — graded pressure,
            // clothes shift the effective temperature both ways.
            // Spec 35.3/35.4: the house protects from cold; shade and the
            // river cool; Indoor/Water block UV, shade cuts it to 20 %.
            var isIndoor = world.Tiles.Items.TryGetValue(npc.Tile, out var npcTile) &&
                npcTile.Flags.HasFlag(TileFlags.Indoor);
            var isInWater = npcTile is not null && npcTile.Flags.HasFlag(TileFlags.Water);
            var isShaded = IsShaded(world, npc.Tile);
            var indoorBonus = isIndoor ? SimBalance.IndoorWarmthBonus : 0f;
            // Shade is applied below as a capped heat-SHIELD, not here — a flat
            // cool bonus chilled girls resting in shade on mild days (18°→11°)
            // into cold damage. Water still cools unconditionally (wet + current).
            var coolBonus = isInWater ? -SimBalance.WaterCoolBonus : 0f;
            // Spec 29C.10: a lit campfire warms the tiles around it (the
            // colder it is, the more worth huddling by the fire), but only
            // chases away COLD — it never overheats a warm body.
            var fireWarmth = NearbyFireWarmth(world, npc.Tile, out var onFire);
            // Spec 42 (WarmUp era): the campfire is a REAL heat source now —
            // +8° at range 1, +4° at range 2, clamped so it only chases away
            // cold, never overheats. The girls start near-naked and the
            // wardrobe is scarce; the designed loop is light the fire, huddle
            // by it, and let rain douse it (iter-31's display-only caution is
            // retired together with the knife-edge balance).
            var baseTemp = world.Environment.GlobalTemperature + npc.EquippedWarmth * 10f +
                indoorBonus + coolBonus;
            var fireRelief = System.Math.Min(fireWarmth, System.Math.Max(0f, SimBalance.HotBandTemp - baseTemp));
            baseTemp += fireRelief;
            // Tier B: shade as a heat-SHIELD — cools only the heat ABOVE the
            // comfy band (down toward ~22°), never chills a cool body. On a 35°
            // day, shade → 28° (the user's example); on an 18° day, no effect.
            if (isShaded)
            {
                baseTemp -= System.Math.Min(-Spec49.ShadeCooling, System.Math.Max(0f, baseTemp - SimBalance.HotBandTemp));
            }
            // Spec 42: realistic cold — 10°C in underwear (warmth ~0.02) is
            // genuinely cold; pressure bites below 14°C (a merely-cool girl at
            // ~15° doesn't accumulate — no wardrobe-circling), naked at 10°
            // racks up 0.11+/slow tick unless she's warming by the fire.
            float pressure;
            if (baseTemp < SimBalance.ColdBandTemp)
            {
                pressure = System.Math.Min(SimBalance.ThermalPressureCap,
                    (SimBalance.ColdBandTemp - baseTemp) * SimBalance.ColdPressureSlope); // cold
            }
            else if (baseTemp > SimBalance.HotBandTemp)
            {
                pressure = System.Math.Min(SimBalance.ThermalPressureCap,
                    (baseTemp - SimBalance.HotBandTemp) * SimBalance.HeatPressureSlope); // overheating
            }
            else
            {
                pressure = -SimBalance.ThermalComfyRecovery; // comfortable band
            }

            // Tier B: a sleeping body accrues cold/heat discomfort more slowly
            // (only the rising side is slowed; recovery in the comfy band stays
            // full). Lets her sleep through a mild night without spiralling.
            var sleeping = npc.Execution.CurrentInteraction == InteractionType.Sleep;
            if (sleeping && pressure > 0f)
            {
                pressure *= Spec49.ThermalSleepFactor;
            }

            npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + pressure);

            // Signed comfort for the UI — fire already folded into baseTemp.
            var effectiveTemp = baseTemp;

            // Spec 42: signed comfort for the UI — 0 in the ideal [16,22]
            // band (matches the decision pressure above, so the bar never
            // shows "fine" while the body is freezing), -1 over a ~12 span.
            float signed;
            if (effectiveTemp < 16f)
            {
                signed = System.Math.Max(-1f, (effectiveTemp - 16f) / 12f);
            }
            else if (effectiveTemp > 22f)
            {
                signed = System.Math.Min(1f, (effectiveTemp - 22f) / 12f);
            }
            else
            {
                signed = 0f;
            }

            npc.Needs.ThermalComfort = signed;
            var magnitude = System.Math.Abs(signed);

            // Spec 29C.10: "the fire burns you if you stand in it" is DEFERRED
            // to the campfire-as-obstacle pass — any HP/comfort hit here
            // reshuffles the dog-fragile colony (NPCs constantly path across
            // the central fire tile) and wipes seeds. onFire is computed and
            // traced so the mechanic is ready to wire once nobody stands on
            // the flames by construction.
            if (onFire)
            {
                Trace.Emit(world, npc.Id, "FireBurn", "On the fire tile (no HP hit yet)");
            }

            if (magnitude >= SimBalance.ThermalDamageGate && !isInWater)
            {
                // §45 r5: 0.02 -> 0.012. A rainy 6° night (rain also douses
                // the fire 4x) killed a near-naked girl from FULL health in
                // one night (~37 slow ticks x 0.02 = 0.74) — both 25-day
                // wipes (777, 42) started as day-4/5 hypothermia deaths.
                // At 0.012 a single bad night hurts (~0.44) but leaves dawn
                // to dress/warm up; two exposed nights still kill.
                // Tier B: a sleeping body takes the cold/heat HP hit at the same
                // slowed factor — this is what lets her sleep THROUGH a cold
                // night (re-arm) without it being a death sentence.
                var thermalHpHit = SimBalance.ThermalHpHit * (sleeping ? Spec49.ThermalSleepFactor : 1f);
                foreach (var part in AllTemperatureParts)
                {
                    npc.Body.Parts[part] = System.Math.Max(0f, npc.Body.Parts[part] - thermalHpHit);
                }

                npc.Health = npc.Body.Mean();
                if (npc.Body.VitalDestroyed(out _))
                {
                    npc.Health = 0f;
                }

                Trace.Emit(world, npc.Id, signed > 0f ? "Heatstroke" : "Hypothermia",
                    $"ThermalComfort={signed:+0.00;-0.00} Health={npc.Health:F2}");
            }

            Trace.Emit(world, npc.Id, "TemperatureUpdate",
                $"Thermal={prevThermal:F3}->{npc.Needs.ThermalDiscomfort:F3} " +
                $"Signed={signed:+0.00;-0.00} " +
                $"EffectiveTemp={effectiveTemp:F1} (Global={world.Environment.GlobalTemperature:F1} " +
                $"Warmth={npc.EquippedWarmth:F2} Fire={fireWarmth:F1}) Pressure={pressure:+0.00;-0.00}");

            // Spec 35.4: sun exposure and sunburn on uncovered parts.
            var effectiveUv = isIndoor || isInWater
                ? 0f
                : world.Environment.UvIndex * (isShaded ? 0.2f : 1f);
            var uncovered = CollectUncoveredParts(world, npc);
            if (effectiveUv > 0.5f && uncovered.Count > 0)
            {
                // Spec 40.7: bare skin under the sun slowly tans (weathered
                // survivor). effectiveUv already carries the shade penalty
                // (isShaded -> x0.2), so you tan LESS in shade. Rate tuned for
                // ~10 game days to full tan at open-sun exposure.
                npc.Needs.TanLevel = MathUtil.Clamp01(
                    npc.Needs.TanLevel + (effectiveUv - 0.5f) * SimBalance.TanRate * uncovered.Count);
                // Spec 40.7: acute redness rises faster than the tan settles —
                // bare skin goes red first, then browns as it heals below.
                npc.Needs.Sunburn = MathUtil.Clamp01(
                    npc.Needs.Sunburn + (effectiveUv - 0.5f) * SimBalance.SunburnRate * uncovered.Count);
                npc.SunExposure += (effectiveUv - 0.5f) * SimBalance.SunExposureRate;
                if (npc.SunExposure > 0.5f)
                {
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.02f);
                }

                if (npc.SunExposure >= 1f)
                {
                    var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 2203) * uncovered.Count);
                    pick = System.Math.Min(pick, uncovered.Count - 1);
                    var burntPart = uncovered[pick];
                    npc.Body.Parts[burntPart] = System.Math.Max(0f, npc.Body.Parts[burntPart] - SimBalance.SunburnBurnDamage);
                    npc.Health = npc.Body.Mean();
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.15f);
                    npc.SunExposure = 0.5f;
                    if (npc.Body.VitalDestroyed(out var burntVital))
                    {
                        npc.Health = 0f;
                        Trace.Emit(world, npc.Id, "VitalPartDestroyed",
                            $"{burntVital} destroyed by sunstroke");
                    }

                    Trace.Emit(world, npc.Id, "Sunburn",
                        $"{burntPart} burnt (UV={effectiveUv:F2}) Part={npc.Body.Parts[burntPart]:F2}");
                }
            }
            else
            {
                npc.SunExposure = System.Math.Max(0f, npc.SunExposure - 0.05f);
            }

            // Spec 40.7: out of the sun (or fully covered), the acute burn heals
            // and a fraction of it settles into permanent tan — red browns down.
            if (npc.Needs.Sunburn > 0f && (effectiveUv <= 0.5f || uncovered.Count == 0))
            {
                var heal = System.Math.Min(npc.Needs.Sunburn, 0.0025f);
                npc.Needs.Sunburn -= heal;
                npc.Needs.TanLevel = MathUtil.Clamp01(npc.Needs.TanLevel + heal * 0.4f);
            }
        }
    }

    private static readonly BodyPart[] AllTemperatureParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    // Spec 29C.10: warmth radiated by nearby LIT campfires. On the fire's own
    // tile it is agony (onFire = true); a tile or two away it gently warms.
    internal static float NearbyFireWarmth(WorldState world, TileCoord tile, out bool onFire)
    {
        onFire = false;
        var warmth = 0f;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.ResourceAmount <= 0f ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            var dist = HexSpatialMath.HexDistance(tile, obj.Tile);
            if (dist == 0)
            {
                onFire = true;
            }
            else if (dist <= 2)
            {
                warmth = System.Math.Max(warmth, dist == 1 ? SimBalance.FireWarmthRange1 : SimBalance.FireWarmthRange2);
            }
        }

        return warmth;
    }

    // Spec 35.4: within 1 tile of a Shade-tagged object (big tree / palm).
    internal static bool IsShaded(WorldState world, TileCoord tile)
    {
        // Spec 43: real cast shadows — the map is rebuilt from the sun path
        // every medium tick (terrain silhouettes + canopy + hut walls), so
        // shade is directional now: long at dawn/dusk, tight at noon.
        return world.ShadedTiles.Contains(tile);
    }

    private static readonly System.Collections.Generic.List<BodyPart> _uncoveredScratch = new();

    private static System.Collections.Generic.List<BodyPart> CollectUncoveredParts(WorldState world, NPCState npc)
    {
        _uncoveredScratch.Clear();
        foreach (var part in npc.Body.Parts.Keys)
        {
            if (!EquipmentMath.IsPartCovered(world, npc, part))
            {
                _uncoveredScratch.Add(part);
            }
        }

        return _uncoveredScratch;
    }
}

// Spec 29A: producers (e.g. apple trees) periodically drop their produce
// on a free junction of a nearby walkable tile.
public sealed class FruitProductionSystem : ISimulationSystem
{
    private readonly System.Collections.Generic.List<ObjectId> _rotted = new();

    public string Name => nameof(FruitProductionSystem);

    public TickLayer Layer => TickLayer.Slow;

    private readonly System.Collections.Generic.List<WorldObjectState> _producers = new();

    public void Run(WorldState world)
    {
        // Spec 31C.1: unclaimed fruit rots after 2400 ticks — drops on
        // unreachable junctions no longer litter the world forever.
        _rotted.Clear();
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if ((candidate.DefinitionId == "food.coconut" ||
                 candidate.DefinitionId == "food.coconut_pierced" ||
                 candidate.DefinitionId == "food.coconut_open") &&
                candidate.SpawnTick > 0 && world.Tick - candidate.SpawnTick > 2400 &&
                !candidate.IsOccupied)
            {
                _rotted.Add(candidate.Id);
            }
        }

        foreach (var rottedId in _rotted)
        {
            WorldObjectMutations.DespawnObject(world, rottedId);
            Trace.EmitSystem(world, "ProduceRotted", $"Obj={rottedId.Value}");
        }

        // Spec 29A.2/19.7A: production only in daylight. Timers are left
        // untouched overnight, so overdue producers fire at dawn.
        if (world.Environment.Phase is DayPhase.Evening or DayPhase.Night)
        {
            return;
        }

        // Snapshot producers first: spawning mutates Entities.Objects mid-iteration.
        _producers.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce != null)
            {
                _producers.Add(obj);
            }
        }

        foreach (var producer in _producers)
        {
            var produce = world.Content.ObjectDefinitions[producer.DefinitionId].Produce;
            if (produce is null || world.Tick < producer.NextProductionTick)
            {
                continue;
            }

            // A failed drop also waits the full interval (spec 29A.2).
            producer.NextProductionTick = world.Tick + produce.IntervalTicks;

            producer.ProducedItems.RemoveAll(id => !world.Entities.Objects.ContainsKey(id));
            if (producer.ProducedItems.Count >= produce.MaxConcurrent)
            {
                Trace.EmitSystem(world, "ProduceSkipped",
                    $"Obj={producer.Id.Value} Def={producer.DefinitionId} CapReached " +
                    $"({producer.ProducedItems.Count}/{produce.MaxConcurrent})");
                continue;
            }

            var (dropTile, dropJunction) = FindDropSpot(world, producer);
            if (dropJunction is null)
            {
                Trace.EmitSystem(world, "ProduceSkipped",
                    $"Obj={producer.Id.Value} Def={producer.DefinitionId} NoFreeSpot " +
                    $"(retry at tick {producer.NextProductionTick})");
                continue;
            }

            var spawned = WorldObjectMutations.SpawnObject(
                world, produce.ProducedDefinitionId, producer.Fragment, dropTile, dropJunction.Value);
            producer.ProducedItems.Add(spawned.Id);

            Trace.EmitSystem(world, "ProduceDropped",
                $"Producer={producer.Id.Value} Spawned={spawned.Id.Value} Def={produce.ProducedDefinitionId} " +
                $"Tile={dropTile.Q},{dropTile.R} Junction={dropJunction.Value.Value} " +
                $"Concurrent={producer.ProducedItems.Count}/{produce.MaxConcurrent}");
        }
    }

    // Deterministic: producer tile first, then hex neighbors in fixed direction
    // order; within a tile, junctions in slot order (spec 29A.2, v1 distance 1).
    private static (TileCoord, JunctionId?) FindDropSpot(WorldState world, WorldObjectState producer)
    {
        var candidateTiles = new System.Collections.Generic.List<TileCoord> { producer.Tile };
        foreach (var neighbor in SpatialQueries.GetNeighbors(world, producer.Tile))
        {
            candidateTiles.Add(neighbor);
        }

        foreach (var tileCoord in candidateTiles)
        {
            if (!SpatialQueries.IsTileWalkable(world, tileCoord) ||
                !world.Tiles.Items.TryGetValue(tileCoord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!SpatialQueries.IsJunctionPassable(world, junctionId) ||
                    !SpatialQueries.IsJunctionFree(world, junctionId) ||
                    IsObjectAnchor(world, tileCoord, junctionId))
                {
                    continue;
                }

                return (tileCoord, junctionId);
            }
        }

        return (producer.Tile, null);
    }

    private static bool IsObjectAnchor(WorldState world, TileCoord tile, JunctionId junctionId)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objectIds))
        {
            return false;
        }

        foreach (var objectId in objectIds)
        {
            if (world.Entities.Objects.TryGetValue(objectId, out var obj) &&
                obj.Junctions.Contains(junctionId))
            {
                return true;
            }
        }

        return false;
    }
}

// Spec 29E.3: campfires burn their fuel down; FireOut when it runs dry.
public sealed class FireSystem : ISimulationSystem
{
    public string Name => nameof(FireSystem);

    public TickLayer Layer => TickLayer.Slow;

    private const float BurnPerSlowTick = 16f;

    public void Run(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.ResourceAmount <= 0f ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            // Spec 42: rain douses the fire — not instantly, but a downpour
            // eats fuel 4x faster, so a full stack dies in ~40 game minutes.
            // A dry night by the fire is the warm-up plan; a wet one isn't.
            var burn = BurnPerSlowTick * (world.Environment.IsRaining ? 4f : 1f);
            obj.ResourceAmount = System.Math.Max(0f, obj.ResourceAmount - burn);
            if (obj.ResourceAmount <= 0f)
            {
                Trace.EmitSystem(world, "FireOut",
                    $"{obj.DefinitionId} at Tile={obj.Tile.Q},{obj.Tile.R} burned out" +
                    (world.Environment.IsRaining ? " (doused by rain)" : ""));
            }
        }
    }
}

// Spec 29C.3: dogs — roam, aggro, chase, bite. Combat is mutual and
// reactive: the bitten NPC is held in place and strikes back automatically.
public sealed class DogSystem : ISimulationSystem
{
    public string Name => nameof(DogSystem);

    public TickLayer Layer => TickLayer.Medium;

    private const int MaxDogs = 3; // §46 difficulty pass: 2 -> 3 (12/12 wins at 2 — armed girls out-fought the pair)
    private const int RespawnCheckTicks = 3600; // §46: every 1.5 game days (was 3) — sustained pack pressure, not one skirmish per arc

    // §46 v2: night-raid catastrophe knobs.
    private static float RaidChancePerDay => SimBalance.RaidChancePerDay; // §21.21B v3.2: hop has NO survival logic at all (user: elegance everywhere) — the exposure tax is paid with bite 0.06 + hunger 0.012 + this raid notch; 12-seed soak = 6/12 (50%) // §47 recalibration: 0.25 -> 0.20 — the comfort chain (ember ring + bed-per-girl) spends real auction time, wins fell 6/12 -> 3/12; one notch of raid pressure buys it back
    private static int RaidPackSize => SimBalance.RaidPackSize;
    private const int RaidDuskOffsetTicks = 1800;
    private const int SpawnMinDistanceFromNpc = 5;
    private static float RoamChance => SimBalance.RoamChance;
    private static int AggroRadiusTiles => SimBalance.AggroRadiusTiles;
    private static float BiteDamagePerPass => SimBalance.BiteDamagePerPass; // §46 difficulty pass: 0.06 -> 0.09 in two steps — at 0.07 the pack only STALLED colonies (7/12 wins, 5 timeouts, 4 dog kills); the loss condition should be blood, not the clock
    private static float NpcStrikePerPass => SimBalance.NpcStrikePerPass;

    private readonly System.Collections.Generic.List<Wildlife.DogState> _deadDogs = new();
    private readonly System.Collections.Generic.List<EntityId> _deadNpcs = new();
    private readonly System.Collections.Generic.List<JunctionId> _spawnCandidates = new();

    public void Run(WorldState world)
    {
        // Spec 41.2 v2: the timer lives in WorldState so it survives a save.
        if (world.Tick >= world.NextDogSpawnCheckTick)
        {
            world.NextDogSpawnCheckTick = world.Tick + RespawnCheckTicks;
            while (world.Dogs.Count < MaxDogs)
            {
                if (!TrySpawnDog(world))
                {
                    break;
                }
            }
        }

        // §46 v2: the NIGHT RAID — a seeded swing catastrophe. Constant
        // damage knobs saturated at ~10-15% colony losses (the homeostat
        // absorbs steady pressure); real 50/50 tension needs rare spikes.
        // Roll is a pure function of (seed, day); dusk hits the colony
        // when the girls are cold, tired and scattered. Days 0-1 are a
        // grace period — a raid on an unestablished camp is a coin-flip
        // wipe with no story. Raid dogs are ordinary dogs: they can be
        // fought, fled, and they linger until killed.
        var raidDay = world.Tick / EnvironmentSystem.DayLengthTicks;
        var raidDusk = raidDay * EnvironmentSystem.DayLengthTicks + RaidDuskOffsetTicks;
        if (raidDay >= 2 && world.Tick >= raidDusk && world.Tick < raidDusk + 4 &&
            MathUtil.Hash01(world.Seed, raidDay, 4646) < RaidChancePerDay)
        {
            var spawned = 0;
            for (var i = 0; i < RaidPackSize; i++)
            {
                if (TrySpawnDog(world))
                {
                    spawned++;
                }
            }

            if (spawned > 0)
            {
                Trace.EmitSystem(world, "NightRaid",
                    $"{spawned} dogs at dusk of day {raidDay}");
            }
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.IsFighting = false;
        }

        _deadDogs.Clear();
        _deadNpcs.Clear();

        foreach (var dog in world.Dogs)
        {
            RunDog(world, dog);
            if (dog.Health <= 0f)
            {
                _deadDogs.Add(dog);
            }
        }

        foreach (var dead in _deadDogs)
        {
            world.Dogs.Remove(dead);
            Trace.EmitSystem(world, "DogKilled",
                $"Dog={dead.Id} at Tile={dead.Tile.Q},{dead.Tile.R}");
            // Spec §54: the fallen dog leaves a butcherable carcass.
            ExecutionSystem.SpawnCarcass(world, dead.Tile, dead.Junction, "dog");
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f)
            {
                _deadNpcs.Add(npc.Id);
            }
        }

        foreach (var deadId in _deadNpcs)
        {
            RemoveDeadNpc(world, deadId);
        }
    }

    private void RunDog(WorldState world, Wildlife.DogState dog)
    {
        // Acquire/validate target.
        NPCState? target = null;
        if (dog.TargetNpc is { } targetId)
        {
            world.Entities.Npcs.TryGetValue(targetId, out target);
        }

        if (target is null)
        {
            dog.TargetNpc = null;
            dog.Status = Wildlife.DogStatus.Roaming;

            var bestDistance = int.MaxValue;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                // Sanctuary (spec 29C.4A): indoor NPCs are never targets.
                if (IsIndoorTile(world, npc.Tile))
                {
                    continue;
                }

                var distance = HexSpatialMath.HexDistance(dog.Tile, npc.Tile);
                if (distance <= AggroRadiusTiles && distance < bestDistance)
                {
                    bestDistance = distance;
                    target = npc;
                }
            }

            if (target is not null)
            {
                dog.TargetNpc = target.Id;
                dog.Status = Wildlife.DogStatus.Chasing;
                RememberDanger(world, target);
                Trace.EmitSystem(world, "DogAggro",
                    $"Dog={dog.Id} targets NPC{target.Id.Value} " +
                    $"(Dist={HexSpatialMath.HexDistance(dog.Tile, target.Tile)})");
            }
        }
        else if (HexSpatialMath.HexDistance(dog.Tile, target.Tile) > AggroRadiusTiles + 3 ||
                 IsIndoorTile(world, target.Tile))
        {
            // Lost interest — target got far away or reached sanctuary
            // (spec 29C.4A: dogs give up at the door).
            Trace.EmitSystem(world, "DogLostTarget",
                $"Dog={dog.Id} lost NPC{target.Id.Value}" +
                $"{(IsIndoorTile(world, target.Tile) ? " (went indoors)" : "")}");
            dog.TargetNpc = null;
            dog.Status = Wildlife.DogStatus.Roaming;
            target = null;
        }

        if (target is null)
        {
            Roam(world, dog);
            return;
        }

        // In range? Same or adjacent junction = melee.
        var inMelee = target.CurrentJunction is { } npcJunction &&
            (npcJunction.Equals(dog.Junction) ||
             (world.Junctions.Items.TryGetValue(dog.Junction, out var dogJunction) &&
              dogJunction.Neighbors.Contains(npcJunction)));

        if (!inMelee)
        {
            dog.Status = Wildlife.DogStatus.Chasing;
            ChaseStep(world, dog, target);
            TryCoverFire(world, dog, target);
            return;
        }

        // Fight: dog bites (armor absorbs). Spec 29C.4A: the NPC assesses —
        // outnumbered or badly hurt means run, otherwise stand and strike back.
        dog.Status = Wildlife.DogStatus.Fighting;
        RememberDanger(world, target);

        var fleeing = target.Mind.CurrentGoal == GoalType.Flee;
        if (!fleeing)
        {
            var attackers = CountAdjacentDogs(world, target);
            if (target.Health < 0.6f || WorstPartHealth(target) < 0.35f || attackers >= 2)
            {
                fleeing = TryStartFlee(world, target, attackers);
            }
        }

        // Spec 19.3C: the bite lands on a specific part; only garments
        // covering that part absorb it.
        var bitPart = PickBitePart(world, dog.Id);
        var partArmor = EquipmentMath.ArmorForPart(world, target, bitPart);
        var damage = BiteDamagePerPass * (1f - partArmor);
        target.Body.Parts[bitPart] = System.Math.Max(0f, target.Body.Parts[bitPart] - damage);
        target.Health = target.Body.Mean();
        // Spec 40.8B: the landed bite leaves a wound record (drives the decal;
        // heals & fades on its own clock). Starvation/heat never create these.
        WoundMath.Inflict(world, target, bitPart, damage);

        // Spec §50: a bite that finishes off a mauled limb may tear it away.
        AmputateSystemHelpers.TrySeverOnBite(world, target, bitPart, damage);

        // Spec 35.6: the cloth gets chewed either way — every garment
        // covering the bitten part loses durability; rags fall apart.
        EquipmentMath.WearCoveringItems(world, target, bitPart, 0.05f);

        if (target.Body.VitalDestroyed(out var vitalPart))
        {
            target.Health = 0f;
            Trace.Emit(world, target.Id, "VitalPartDestroyed",
                $"{vitalPart} destroyed by Dog={dog.Id}");
        }

        if (fleeing)
        {
            // A running NPC keeps moving and does not trade hits.
            Trace.Emit(world, target.Id, "DogFight",
                $"Dog={dog.Id} bit fleeing NPC: {bitPart} -{damage:F3} " +
                $"(PartArmor={partArmor:F2}) Part={target.Body.Parts[bitPart]:F2} " +
                $"NpcHealth={target.Health:F2}");
        }
        else
        {
            var wasFighting = target.IsFighting;
            target.IsFighting = true;
            if (target.Plan.Status == PlanStatus.Active ||
                target.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, target, $"Attacked by dog {dog.Id}");
                target.Mind.CurrentGoal = GoalType.None;
            }

            // Spec §52: at the first strike, drop the load to ready the weapon —
            // "quick, throw down the firewood and grab the spear." Both hands are
            // needed for the spear, so bulky resources in hand hit the ground
            // (recoverable after the fight). Only when actually spear-armed and
            // two-handed; a bare-handed girl keeps whatever she carries.
            if (!wasFighting && target.Inventory.Items.Contains("tool.spear") &&
                target.Body.IntactHands >= 2)
            {
                ReadySpearHands(world, target);
            }

            // Spec 19.3C: hurt arms strike weaker. Weapon damage is per-hit
            // (knife as shipped, axe 1.5x knife, spear 2x knife); attack speed
            // controls how often the counter-blow is ready.
            var weaponId = SimBalance.BestMeleeWeapon(target.Inventory.Items, target.Body.IntactHands);
            var weaponMult = SimBalance.MeleeStrikeBonus(weaponId);
            var attackSpeed = SimBalance.MeleeAttackSpeed(weaponId);
            var strikeReady = SimBalance.MeleeStrikeReady(world.Tick, target.Id.Value, weaponId);
            var weaponTag = string.IsNullOrEmpty(weaponId) ? string.Empty : $" {weaponId}";

            var strike = strikeReady
                ? NpcStrikePerPass * target.Body.StrikeFactor() * weaponMult
                : 0f;
            if (strike > 0f)
            {
                dog.Health -= strike;
            }

            Trace.Emit(world, target.Id, "DogFight",
                $"Dog={dog.Id} bit: {bitPart} -{damage:F3} (PartArmor={partArmor:F2}) " +
                $"Part={target.Body.Parts[bitPart]:F2} NpcHealth={target.Health:F2} " +
                $"Strike={strike:F3}{weaponTag}{(strikeReady ? string.Empty : " recovering")} " +
                $"Speed={attackSpeed:F1} " +
                $"DogHealth={System.Math.Max(0f, dog.Health):F2}");
        }

        if (target.Health <= 0f)
        {
            dog.TargetNpc = null;
            dog.Status = Wildlife.DogStatus.Roaming;
        }
    }

    // Spec 19.3C: dogs bite low — legs most, head rarely.
    private static BodyPart PickBitePart(WorldState world, int dogId)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick, dogId, 555);
        if (roll < 0.30f) return BodyPart.LegL;
        if (roll < 0.60f) return BodyPart.LegR;
        if (roll < 0.725f) return BodyPart.ArmL;
        if (roll < 0.85f) return BodyPart.ArmR;
        if (roll < 0.95f) return BodyPart.Torso;
        if (roll < 0.98f) return BodyPart.Pelvis;
        return BodyPart.Head;
    }

    internal static float WorstPartHealth(NPCState npc)
    {
        var worst = 1f;
        foreach (var value in npc.Body.Parts.Values)
        {
            worst = System.Math.Min(worst, value);
        }

        return worst;
    }

    private static bool IsIndoorTile(WorldState world, TileCoord tile)
    {
        return world.Tiles.Items.TryGetValue(tile, out var t) &&
            t.Flags.HasFlag(TileFlags.Indoor);
    }

    private static bool IsIndoorJunction(WorldState world, JunctionId junctionId)
    {
        return world.Junctions.Items.TryGetValue(junctionId, out var junction) &&
            junction.Tiles.Count > 0 && IsIndoorTile(world, junction.Tiles[0]);
    }

    private static int CountAdjacentDogs(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } npcJunction)
        {
            return 0;
        }

        var count = 0;
        foreach (var dog in world.Dogs)
        {
            if (dog.Health <= 0f)
            {
                continue;
            }

            if (dog.Junction.Equals(npcJunction) ||
                (world.Junctions.Items.TryGetValue(dog.Junction, out var dogJunction) &&
                 dogJunction.Neighbors.Contains(npcJunction)))
            {
                count++;
            }
        }

        return count;
    }

    // Spec §52 / §54: drop the bulky load to free both hands for the spear.
    // Resources (logs/sticks/stone/leaves) hit the ground at the NPC's feet —
    // recoverable after the fight; the spear/bottle/tools/food stay.
    private static readonly string[] _bulkyHandItems =
    {
        "resource.log", "resource.stick", "resource.stone", "resource.palm_leaf"
    };

    private static void ReadySpearHands(WorldState world, NPCState npc)
    {
        var dropped = 0;
        foreach (var mat in _bulkyHandItems)
        {
            while (npc.Inventory.Items.Find(i => i.DefinitionId == mat) is { } item)
            {
                npc.Inventory.Items.Remove(item);
                ExecutionSystem.DropItemAtFeet(world, npc, item);
                dropped++;
            }
        }

        if (dropped > 0)
        {
            Trace.Emit(world, npc.Id, "SpearReadied",
                $"Dropped {dropped} to grab the spear");
        }
    }

    // Spec 29C.4A: record the attack site (deduped by tile, capped).
    // §56: also used by PredationSystem so a preyed-on victim flags the danger.
    internal static void RememberDanger(WorldState world, NPCState npc)
    {
        foreach (var danger in npc.Memory.Dangers)
        {
            if (danger.Tile == npc.Tile)
            {
                danger.Tick = world.Tick;
                return;
            }
        }

        npc.Memory.Dangers.Add(new Memory.DangerMemory { Tile = npc.Tile, Tick = world.Tick });
        if (npc.Memory.Dangers.Count > 8)
        {
            npc.Memory.Dangers.RemoveAt(0);
        }

        Trace.Emit(world, npc.Id, "DangerRemembered",
            $"Tile={npc.Tile.Q},{npc.Tile.R} (dogs)");
    }

    // Spec 29C.4A: run for the nearest reachable indoor junction.
    // §56: also used by PredationSystem so a preyed-on victim can bolt.
    internal static bool TryStartFlee(WorldState world, NPCState npc, int attackers)
    {
        if (npc.CurrentJunction is not { } startJunction)
        {
            return false;
        }

        JunctionId? best = null;
        var bestDistance = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !IsIndoorTile(world, junction.Tiles[0]))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(npc.Position, junction.WorldPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = junction.Id;
            }
        }

        if (best is not { } refuge ||
            !Connectivity.Reachable(world, startJunction, refuge))
        {
            return false; // nowhere to run — keep fighting
        }

        PlanInterruption.Abort(world, npc, $"Fleeing from dogs (attackers={attackers})");
        npc.IsFighting = false;
        npc.Mind.CurrentGoal = GoalType.Flee;
        npc.Plan.Goal = GoalType.Flee;
        npc.Plan.TargetJunctionId = refuge;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = refuge
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;

        Trace.Emit(world, npc.Id, "FleeStarted",
            $"To indoor Junction={refuge.Value} (Health={npc.Health:F2} Attackers={attackers})");
        return true;
    }

    private static void Roam(WorldState world, Wildlife.DogState dog)
    {
        if (MathUtil.Hash01(world.Seed, world.Tick, dog.Id, 313) > RoamChance)
        {
            return;
        }

        if (!world.Junctions.Items.TryGetValue(dog.Junction, out var junction) ||
            junction.Neighbors.Count == 0)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, dog.Id, 719) * junction.Neighbors.Count);
        pick = System.Math.Min(pick, junction.Neighbors.Count - 1);
        var nextId = junction.Neighbors[pick];
        if (!world.Junctions.Items.TryGetValue(nextId, out var next) || next.Blocked || next.Door ||
            IsIndoorJunction(world, nextId) || SpatialQueries.IsAllWaterJunction(world, nextId))
        {
            return;
        }

        MoveDogTo(dog, next);
    }

    private static void ChaseStep(WorldState world, Wildlife.DogState dog, NPCState target)
    {
        if (target.CurrentJunction is not { } targetJunction)
        {
            return;
        }

        var path = HexPathfinder.FindPath(world, dog.Junction, targetJunction);
        if (path.Count < 2)
        {
            return;
        }

        if (world.Junctions.Items.TryGetValue(path[1], out var next) && !next.Blocked && !next.Door &&
            !IsIndoorJunction(world, path[1]) && !SpatialQueries.IsAllWaterJunction(world, path[1]))
        {
            MoveDogTo(dog, next);
        }
    }

    // Spec 35.6: the fear arc gains an answer — an archer housemate (not
    // the one being chased, not in a fight) covers the flight from range.
    private static void TryCoverFire(WorldState world, Wildlife.DogState dog, NPCState quarry)
    {
        foreach (var archer in world.Entities.Npcs.Values)
        {
            if (archer.Id.Value == quarry.Id.Value || archer.IsFighting ||
                !archer.Inventory.Items.Contains("tool.bow") ||
                !archer.Inventory.Items.Contains("resource.arrow") ||
                HexSpatialMath.HexDistance(archer.Tile, dog.Tile) > 3)
            {
                continue;
            }

            archer.Inventory.Items.Remove("resource.arrow");
            var roll = MathUtil.Hash01(world.Seed, world.Tick, dog.Id * 191 + archer.Id.Value, 907);
            if (roll < 0.5f)
            {
                dog.Health -= 0.35f;
                Trace.Emit(world, archer.Id, "DogShot",
                    $"Dog={dog.Id} hit (Roll={roll:F2}) DogHealth={System.Math.Max(0f, dog.Health):F2}");
            }
            else
            {
                Trace.Emit(world, archer.Id, "DogShot",
                    $"Dog={dog.Id} missed (Roll={roll:F2})");
            }

            return;
        }
    }

    private static void MoveDogTo(Wildlife.DogState dog, Junction next)
    {
        dog.Junction = next.Id;
        dog.Position = next.WorldPosition;
        if (next.Tiles.Count > 0)
        {
            dog.Tile = next.Tiles[0];
        }
    }

    private bool TrySpawnDog(WorldState world)
    {
        _spawnCandidates.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                IsIndoorTile(world, junction.Tiles[0]) ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            var tile = junction.Tiles[0];
            var farEnough = true;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (HexSpatialMath.HexDistance(tile, npc.Tile) < SpawnMinDistanceFromNpc)
                {
                    farEnough = false;
                    break;
                }
            }

            if (farEnough)
            {
                _spawnCandidates.Add(junction.Id);
            }
        }

        if (_spawnCandidates.Count == 0)
        {
            return false;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, world.NextDogId, 431) * _spawnCandidates.Count);
        pick = System.Math.Min(pick, _spawnCandidates.Count - 1);
        var spawnJunction = world.Junctions.Items[_spawnCandidates[pick]];

        var dog = new Wildlife.DogState
        {
            Id = world.NextDogId++,
            Junction = spawnJunction.Id,
            Position = spawnJunction.WorldPosition,
            Tile = spawnJunction.Tiles[0]
        };
        world.Dogs.Add(dog);
        Trace.EmitSystem(world, "DogSpawned",
            $"Dog={dog.Id} at Tile={dog.Tile.Q},{dog.Tile.R} Junction={dog.Junction.Value}");
        return true;
    }

    // Spec 29C.2: death cleanup must be total.
    // §56: also invoked by PredationSystem when a stalked victim is killed.
    internal static void RemoveDeadNpc(WorldState world, EntityId deadId)
    {
        if (world.Entities.Npcs.TryGetValue(deadId, out var dying))
        {
            ExecutionSystem.ReleaseClaims(world, dying);
        }

        if (!world.Entities.Npcs.TryGetValue(deadId, out var npc))
        {
            return;
        }

        PlanInterruption.Abort(world, npc, "Died");

        world.Entities.Npcs.Remove(deadId);

        if (world.Occupancy.EntitiesInTile.TryGetValue(npc.Tile, out var tileEntities))
        {
            tileEntities.Remove(deadId);
        }

        if (world.Caches.EntitiesByTile.TryGetValue(npc.Tile, out var cachedTile))
        {
            cachedTile.Remove(deadId);
        }

        if (world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var cachedFragment))
        {
            cachedFragment.Remove(deadId);
        }

        // Release anything the NPC still owns anywhere in the world.
        var reservationKeys = new System.Collections.Generic.List<JunctionId>();
        foreach (var pair in world.Reservations.Junctions)
        {
            if (pair.Value.Owner == deadId)
            {
                reservationKeys.Add(pair.Key);
            }
        }

        foreach (var key in reservationKeys)
        {
            world.Reservations.Junctions.Remove(key);
        }

        var occupiedKeys = new System.Collections.Generic.List<JunctionId>();
        foreach (var pair in world.Occupancy.JunctionOwner)
        {
            if (pair.Value == deadId)
            {
                occupiedKeys.Add(pair.Key);
            }
        }

        foreach (var key in occupiedKeys)
        {
            world.Occupancy.JunctionOwner[key] = null;
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.CurrentUser == deadId)
            {
                obj.CurrentUser = null;
                obj.IsOccupied = false;
            }
        }

        // Spec 31A.5A: everything worn/carried drops at the death site through
        // the same ground-drop path as inventory overflow and explicit drops.
        var dropJunction = npc.CurrentJunction;
        if (dropJunction is null &&
            world.Tiles.Items.TryGetValue(npc.Tile, out var deathTile) &&
            deathTile.Junctions.Count > 0)
        {
            dropJunction = deathTile.Junctions[0];
        }

        foreach (var item in npc.WornItems)
        {
            ExecutionSystem.DropItemAtFeet(world, npc, item);
        }

        foreach (var item in npc.Inventory.Items)
        {
            // Spec 29H: the bottle is a personal effect — it stays with
            // its owner, never litters the world (and never lets a
            // survivor hoard empty bottles via GatherTools).
            if (item.DefinitionId == "tool.bottle")
            {
                continue;
            }

            ExecutionSystem.DropItemAtFeet(world, npc, item);
        }

        // Spec 28.15C: the body remains; witnesses grieve immediately.
        if (dropJunction is { } corpseJunction)
        {
            var corpse = WorldObjectMutations.SpawnObject(
                world, "corpse.npc", npc.Fragment, npc.Tile, corpseJunction);
            corpse.CurrentUser = deadId; // whose body this is
            corpse.ResourceAmount = 4800f; // decay timer (2 days)

            foreach (var witness in world.Entities.Npcs.Values)
            {
                if (HexSpatialMath.HexDistance(witness.Tile, npc.Tile) <= 6)
                {
                    GriefSystemHelpers.TriggerGrief(world, witness, corpse);
                }
            }
        }

        Trace.EmitSystem(world, "NpcDied",
            $"NPC{deadId.Value} died at Tile={npc.Tile.Q},{npc.Tile.R} " +
            $"dropping worn=[{string.Join(",", npc.WornItems)}] " +
            $"inventory=[{string.Join(",", npc.Inventory.Items)}]");
    }
}

// Spec 28.15C: grief mechanics shared by the death handler (witnessing)
// and the decision pass (discovery).
internal static class GriefSystemHelpers
{
    public static void TriggerGrief(WorldState world, NPCState npc, WorldObjectState corpse)
    {
        if (npc.Mind.GrievedCorpses.Contains(corpse.Id))
        {
            return;
        }

        npc.Mind.GrievedCorpses.Add(corpse.Id);

        var affinity = corpse.CurrentUser is { } deadId
            ? npc.Social.GetOrCreate(deadId).Affinity
            : 0f;
        var socialLoss = System.Math.Max(0.15f, 0.3f + 0.3f * affinity);
        npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social - socialLoss);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.2f);
        npc.Mind.GrievingUntilTick = world.Tick + 2400;

        // The death site is frightening (spec 29C.4A reuse).
        var alreadyRemembered = false;
        foreach (var danger in npc.Memory.Dangers)
        {
            if (danger.Tile == corpse.Tile)
            {
                danger.Tick = world.Tick;
                alreadyRemembered = true;
                break;
            }
        }

        if (!alreadyRemembered)
        {
            npc.Memory.Dangers.Add(new Memory.DangerMemory { Tile = corpse.Tile, Tick = world.Tick });
            if (npc.Memory.Dangers.Count > 8)
            {
                npc.Memory.Dangers.RemoveAt(0);
            }
        }

        // A witness knows where they fell (spatial memory, 27.18A) —
        // otherwise Mourn could never be planned.
        if (!npc.Memory.KnownObjects.ContainsKey(corpse.Id))
        {
            npc.Memory.KnownObjects[corpse.Id] = new Memory.ObjectMemory
            {
                Id = corpse.Id,
                DefinitionId = corpse.DefinitionId,
                Tile = corpse.Tile,
                Junction = corpse.Junctions.Count > 0 ? corpse.Junctions[0] : null,
                LastSeenTick = world.Tick
            };
        }

        Trace.Emit(world, npc.Id, "Grieving",
            $"For NPC{corpse.CurrentUser?.Value.ToString() ?? "?"} " +
            $"(Affinity={affinity:F2} SocialLoss={socialLoss:F2}) " +
            $"Mourning until tick {npc.Mind.GrievingUntilTick}");
    }
}

// Spec §50: severing a limb. Shared by the emergent triggers (dog/shark bites
// that overwhelm an already-mauled limb) and the prepared HazardSystem.
public static class AmputateSystemHelpers
{
    // Only arms and legs come off — never Head/Torso/Pelvis (those kill via the
    // existing VitalDestroyed path instead).
    public static bool CanSever(BodyPart part) =>
        part == BodyPart.ArmL || part == BodyPart.ArmR ||
        part == BodyPart.LegL || part == BodyPart.LegR;

    // §50: the emergent (bite) trigger. Call right after a bite has applied its
    // damage and filed its wound. Only a fully-destroyed limb (0 HP) comes off,
    // and only on a big blow or a rare grind roll — so a leg ground to 0 usually
    // just hobbles, and every so often is torn away entirely.
    public static void TrySeverOnBite(WorldState world, NPCState npc, BodyPart part, float blowDamage)
    {
        if (!Spec50.Enabled || !CanSever(part) || npc.Body.IsSevered(part) ||
            npc.Body.Parts[part] > 0f)
        {
            return;
        }

        var bigBlow = blowDamage >= Spec50.LimbSeverThreshold;
        var grind = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 733) < Spec50.GrindSeverChance;
        if (bigBlow || grind)
        {
            Sever(world, npc, part);
        }
    }

    // §50 dev/test entry: land ONE bite on a part exactly like a dog/shark —
    // dock the zone's HP, bleed a little, file the wound decal, then run the
    // sever-on-bite check. Used by the AmputationTest scene's damage buttons so
    // a limb tears off "on damage when it should" through the real path.
    public static void DebugBite(WorldState world, NPCState npc, BodyPart part, float damage)
    {
        if (!npc.Body.Parts.ContainsKey(part))
        {
            return;
        }

        npc.Body.Parts[part] = System.Math.Max(0f, npc.Body.Parts[part] - damage);
        npc.Health = npc.Body.Mean();
        npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - damage * 0.5f);
        WoundMath.Inflict(world, npc, part, damage);

        // Head/Torso at 0 kill outright (as in the sim); limbs may tear off.
        if (npc.Body.VitalDestroyed(out var vital))
        {
            npc.Health = 0f;
            Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"{vital} destroyed (debug)");
        }

        TrySeverOnBite(world, npc, part, damage);
    }

    // Take the limb off for good: pin the zone to 0 (never regenerates), dump
    // blood, file a deep slow-clotting stump wound, drop the limb in the world
    // as a decaying object, and interrupt whatever she was doing. If she dies,
    // it's through blood loss over the following ticks — not instantly here.
    public static void Sever(WorldState world, NPCState npc, BodyPart part)
    {
        if (!Spec50.Enabled || !CanSever(part) || npc.Body.IsSevered(part))
        {
            return;
        }

        npc.Body.Sever(part);
        npc.Health = npc.Body.Mean();
        npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - Spec50.LimbSeverBloodLoss);

        // Spec §52: a lost arm is a lost hand slot — the pack shrinks. Anything
        // that no longer fits spills to the ground (handled by SpillOverflow).
        if (part == BodyPart.ArmL || part == BodyPart.ArmR)
        {
            EquipmentMath.RecalculateCapacity(world, npc);
            InventoryMath.SpillOverflow(world, npc);
        }

        // The stump bleeds: a deep fresh wound §44 clotting keeps open a while,
        // driving the ongoing Blood drain through the low-part bleed path.
        WoundMath.Inflict(world, npc, part, Spec50.LimbSeverWoundSeverity);

        // Drop the limb at her feet as a decaying world object (mirrors corpse).
        var dropJunction = npc.CurrentJunction;
        if (dropJunction is null &&
            world.Tiles.Items.TryGetValue(npc.Tile, out var tile) && tile.Junctions.Count > 0)
        {
            dropJunction = tile.Junctions[0];
        }

        if (dropJunction is { } junction)
        {
            var limb = WorldObjectMutations.SpawnObject(
                world, "body.limb_severed", npc.Fragment, npc.Tile, junction);
            limb.CurrentUser = npc.Id;          // whose limb (which actor mesh to bake)
            limb.Variant = part.ToString();     // which limb — the renderer bakes this chain
            limb.ResourceAmount = Spec50.SeveredLimbDecayTicks;
        }

        // Whatever she was mid-doing is over.
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, npc, $"Lost {part}");
            npc.Mind.CurrentGoal = GoalType.None;
        }

        Trace.Emit(world, npc.Id, "LimbSevered",
            $"{part} severed (Blood={npc.Needs.Blood:F2} Health={npc.Health:F2})");
    }
}

// Spec 28.15C: bodies decay away after two days.
public sealed class CorpseSystem : ISimulationSystem
{
    public string Name => nameof(CorpseSystem);

    public TickLayer Layer => TickLayer.Slow;

    private readonly System.Collections.Generic.List<ObjectId> _decayed = new();

    public void Run(WorldState world)
    {
        _decayed.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            // §50: a severed limb ("Decays" tag) rots away on the same clock as
            // a body, but without the mourn/bury interactions a Corpse carries.
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !(definition.Tags.Contains("Corpse") || definition.Tags.Contains("Decays")))
            {
                continue;
            }

            obj.ResourceAmount -= 16f;
            if (obj.ResourceAmount <= 0f)
            {
                _decayed.Add(obj.Id);
            }
        }

        foreach (var id in _decayed)
        {
            WorldObjectMutations.DespawnObject(world, id);
            Trace.EmitSystem(world, "CorpseGone", $"Obj={id.Value} decayed");
        }
    }
}

// Spec §54: meat left on the ground spoils. Raw rots fast, cooked lasts longer
// (cooking is preservation). Uses the SpawnTick-since-landing pattern (like
// coconut rot); carried meat is out of scope for v1 (assumed eaten/cooked in
// time). A carcass being butchered (IsOccupied) is left alone.
public sealed class MeatSpoilageSystem : ISimulationSystem
{
    public string Name => nameof(MeatSpoilageSystem);

    public TickLayer Layer => TickLayer.Slow;

    private readonly System.Collections.Generic.List<ObjectId> _spoiled = new();

    public void Run(WorldState world)
    {
        _spoiled.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.SpawnTick <= 0 || obj.IsOccupied)
            {
                continue;
            }

            int spoilTicks;
            if (obj.DefinitionId == "food.meat_raw")
            {
                spoilTicks = SimBalance.MeatRawSpoilTicks;
            }
            else if (obj.DefinitionId == "food.meat_cooked")
            {
                spoilTicks = SimBalance.MeatCookedSpoilTicks;
            }
            else
            {
                continue;
            }

            if (world.Tick - obj.SpawnTick >= spoilTicks)
            {
                _spoiled.Add(obj.Id);
            }
        }

        foreach (var id in _spoiled)
        {
            WorldObjectMutations.DespawnObject(world, id);
            Trace.EmitSystem(world, "MeatSpoiled", $"Obj={id.Value} rotted on the ground");
        }
    }
}

// Spec §54.2: beds are raised at a PROGRESSIVE build-site — hauled leaf/stick
// pieces accrete into the mat (rendered growing via BedFactory), then a hammer
// finishes it. This system is the site PLACER: once the colony has a lit hearth
// but fewer beds than living girls, and no bed is currently under construction,
// it stakes ONE bed.leaf site by the fire. One at a time, so a build reads as a
// clear "this bed, now" project; the finished bed bumps the count and the next
// site follows. Placement is a colony intent (like the bootstrap campfire site)
// — deliberately OUTSIDE the per-NPC auction so the fragile survival balance is
// untouched; the hauling/raising is the existing BuildFurniture chain.
public sealed class BedSiteSystem : ISimulationSystem
{
    public string Name => nameof(BedSiteSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        var livingGirls = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f)
            {
                livingGirls++;
            }
        }

        if (livingGirls == 0)
        {
            return;
        }

        // Count finished beds + beds already under construction; find the hearth.
        var beds = 0;
        var bedSitesInProgress = 0;
        WorldObjectState hearth = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (BuildSiteMath.IsSite(obj))
            {
                if (obj.BuildProduct is "bed.leaf" or "bed.basic")
                {
                    bedSitesInProgress++;
                }

                continue;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            if (def.Tags.Contains("Bed"))
            {
                beds++;
            }
            else if (hearth is null && def.Tags.Contains("Campfire"))
            {
                hearth = obj;
            }
        }

        // Fire first (a lit hearth, not the cold pit-site). One bed at a time.
        // Cap at one bed per living girl.
        if (hearth is null || bedSitesInProgress > 0 || beds >= livingGirls)
        {
            return;
        }

        var spot = FindFiresideSpot(world, hearth);
        if (spot is not { } placement)
        {
            return;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, "build.site", new FragmentId(1), placement.Tile, placement.Junction);
        site.BuildProduct = "bed.leaf";
        site.BillLeaves = SimBalance.BedLeafBillLeaves;
        site.BillSticks = SimBalance.BedLeafBillSticks;
        site.BillRope = SimBalance.BedLeafBillRope;
        RememberSiteForColony(world, site);
        Trace.EmitSystem(world, "BedSitePlaced",
            $"bed.leaf site staked by the hearth ({beds}/{livingGirls} beds)");
    }

    private static void RememberSiteForColony(WorldState world, WorldObjectState site)
    {
        var junction = site.Junctions.Count > 0 ? site.Junctions[0] : (JunctionId?)null;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Memory.KnownObjects[site.Id] = new ObjectMemory
            {
                Id = site.Id,
                DefinitionId = site.DefinitionId,
                Tile = site.Tile,
                Junction = junction,
                IsPermanent = true,
                LastSeenTick = world.Tick
            };
        }
    }

    // A free junction on a dry tile neighbouring the hearth.
    private static (TileCoord Tile, JunctionId Junction)? FindFiresideSpot(WorldState world, WorldObjectState hearth)
    {
        foreach (var dir in HexDirection.All)
        {
            var coord = new TileCoord(hearth.Tile.Q + dir.DQ, hearth.Tile.R + dir.DR);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            foreach (var jid in tile.Junctions)
            {
                if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked &&
                    SpatialQueries.IsJunctionFree(world, jid))
                {
                    return (coord, jid);
                }
            }
        }

        return null;
    }
}

// Spec §50: a prepared amputation hazard — a reef, a bear-trap, a set spot on
// the map an author places. A survivor standing on a tile holding a "Hazard"
// object loses a leg (deterministically at chance 1, or by a tuned roll). Like
// the shark, it's a fixed dangerous place rather than an emergent bite.
public sealed class HazardSystem : ISimulationSystem
{
    public string Name => nameof(HazardSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        if (!Spec50.Enabled)
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f ||
                !world.Caches.ObjectsByTile.TryGetValue(npc.Tile, out var objects))
            {
                continue;
            }

            var onHazard = false;
            foreach (var objId in objects)
            {
                if (world.Entities.Objects.TryGetValue(objId, out var obj) &&
                    world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                    def.Tags.Contains("Hazard"))
                {
                    onHazard = true;
                    break;
                }
            }

            if (!onHazard)
            {
                continue;
            }

            // Take a leg that's still attached (right first, then left).
            BodyPart? leg =
                !npc.Body.IsSevered(BodyPart.LegR) ? BodyPart.LegR
                : !npc.Body.IsSevered(BodyPart.LegL) ? BodyPart.LegL
                : (BodyPart?)null;

            if (leg is { } target &&
                MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 851) < Spec50.HazardSeverChance)
            {
                AmputateSystemHelpers.Sever(world, npc, target);
            }
        }
    }
}

// Spec 31A.5A/31A.5B: equipment values derive from the worn list —
// warmth stacks across layers (sum), armor is per covered part (max).
// Spec 40.8B: wounds as first-class records — creation & healing constants.
// Only landed bites/hits call Inflict; starvation, heat, sunburn and sickness
// drain HP without ever creating a wound (no phantom decals while starving).
internal static class WoundMath
{
    // Full close in ~2 game days (2 x 150 slow ticks) at neutral pace;
    // sleeping doubles it, marching halves it.
    public static float HealPerSlowTick => SimBalance.HealPerSlowTick;

    // Raised 12 → 36 alongside multi-gash hits (one bite files three
    // records): at low HP the body should read MAULED all over — a dozen
    // bites' worth of marks before the reopen path freezes the count.
    private static int MaxWounds => SimBalance.MaxWounds;

    // Spec 40.8-E: a landed bite tears SEVERAL gashes, not one — each hit
    // splits into this many records (same zone, distinct seeds → distinct
    // painted marks). The DAMAGE is split too, so total hostage HP, healing
    // duration and the dog balance stay exactly as before; only the visual
    // density changes.
    private static int GashesPerHit => SimBalance.GashesPerHit;

    // Hits below this don't split — three sub-0.03 records are invisible
    // clutter that burns the cap for nothing.
    private static float MinSplittableDamage => SimBalance.MinSplittableDamage;

    // HP still held hostage by open wounds in a zone: Σ severity·(1−heal).
    // Generic fed-regen may not raise the zone above 1 − this value; the HP
    // returns only as each wound closes.
    public static float OpenWoundDamage(NPCState npc, BodyPart zone)
    {
        var held = 0f;
        foreach (var wound in npc.Wounds)
        {
            if (wound.Zone == zone)
            {
                held += wound.Severity * (1f - wound.Heal01);
            }
        }

        return held;
    }

    public static void Inflict(WorldState world, NPCState npc, BodyPart zone, float damage)
    {
        var pieces = damage < MinSplittableDamage ? 1 : GashesPerHit;
        var share = damage / pieces;
        for (var i = 0; i < pieces; i++)
        {
            InflictOne(world, npc, zone, share);
        }
    }

    private static void InflictOne(WorldState world, NPCState npc, BodyPart zone, float damage)
    {
        // At the cap the next bite never EVICTS (dropping a record would
        // strand its hostage HP forever — the zone could stick at 0). It
        // REOPENS an existing wound instead: same-zone if possible (the bite
        // tears the old scar deeper — same spot, same decal, fade resets),
        // else the most-healed wound anywhere hands its held HP back to its
        // own zone and the record is repurposed for the new hit.
        if (npc.Wounds.Count >= MaxWounds)
        {
            WoundState reuse = null;
            foreach (var wound in npc.Wounds)
            {
                if (wound.Zone == zone && (reuse == null || wound.Heal01 > reuse.Heal01))
                {
                    reuse = wound;
                }
            }

            if (reuse != null)
            {
                // Deepen: the combined hostage = what it still held + new hit.
                reuse.Severity = reuse.Severity * (1f - reuse.Heal01) + damage;
                reuse.Heal01 = 0f;
            }
            else
            {
                foreach (var wound in npc.Wounds)
                {
                    if (reuse == null || wound.Heal01 > reuse.Heal01)
                    {
                        reuse = wound;
                    }
                }

                // Close the donor instantly: return its held HP to ITS zone,
                // then repurpose the record as a fresh wound at the new spot.
                // §50: a severed donor zone keeps its HP at 0 — it's gone.
                if (!npc.Body.IsSevered(reuse.Zone))
                {
                    npc.Body.Parts[reuse.Zone] = MathUtil.Clamp01(
                        npc.Body.Parts[reuse.Zone] + reuse.Severity * (1f - reuse.Heal01));
                }
                reuse.Zone = zone;
                reuse.Severity = damage;
                reuse.Heal01 = 0f;
                reuse.Id = npc.NextWoundId++;
                reuse.Seed = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 911 + npc.NextWoundId) * int.MaxValue);
            }

            Trace.Emit(world, npc.Id, "WoundInflicted",
                $"{zone} damage={damage:F2} wounds={npc.Wounds.Count} (reopened #{reuse.Id})");
            return;
        }

        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = zone,
            Severity = damage,
            Heal01 = 0f,
            // Deterministic per (seed, tick, npc, wound#): the decal's spot and
            // look replay identically after a save-restore (spec 41.2 replays
            // the same seed to the same tick).
            Seed = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 911 + npc.NextWoundId) * int.MaxValue)
        });

        Trace.Emit(world, npc.Id, "WoundInflicted",
            $"{zone} damage={damage:F2} wounds={npc.Wounds.Count}");
    }
}

// Spec §52: pack bookkeeping keyed on item importance. Water/food outrank
// resources, so a full pack sheds a stone before a coconut. Centralizes the
// "what do I drop / keep" decisions shared by overflow, haul-to-fire and stash.
internal static class InventoryMath
{
    public static int Importance(WorldState world, string definitionId) =>
        world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def)
            ? ItemCatalog.Importance(def)
            : ItemCatalog.ImportanceById(definitionId);

    // The least-wanted pocket item — the first to go when room is tight.
    // Personal effects (the bottle) are never candidates.
    public static ItemInstance LowestImportanceDroppable(WorldState world, NPCState npc)
    {
        ItemInstance worst = null;
        var worstImp = int.MaxValue;
        foreach (var item in npc.Inventory.Items)
        {
            if (InventoryState.IsPersonalEffect(item.DefinitionId))
            {
                continue;
            }

            var imp = Importance(world, item.DefinitionId);
            if (imp < worstImp)
            {
                worstImp = imp;
                worst = item;
            }
        }

        return worst;
    }

    // Drop the lowest-importance pocket items until the pack fits again. Used
    // whenever capacity shrinks under a full load (undress, arm severed). Items
    // land at the NPC's feet with their instance state (wetness/durability).
    public static void SpillOverflow(WorldState world, NPCState npc)
    {
        var inv = npc.Inventory;
        var guard = 0;
        while (inv.UsedSlots > inv.Capacity && guard++ < 64)
        {
            var victim = LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            inv.Items.Remove(victim);
            ExecutionSystem.DropItemAtFeet(world, npc, victim);
            Trace.Emit(world, npc.Id, "ItemSpilled", $"{victim.DefinitionId} (no pocket room)");
        }
    }
}

// Spec §52: material accounting for a furniture build-site. Materials hauled
// in live in the object's Contents; the bill lives in Bill* fields; a hammer
// raises it once the bill is met. Product/bill are per-instance so one def
// ("build.site") covers every furniture type.
internal static class BuildSiteMath
{
    public const string MaterialLogs = "resource.log"; // spec §54: builds are log-framed
    public const string MaterialStones = "resource.stone";
    public const string MaterialLeaves = "resource.palm_leaf";
    public const string MaterialSticks = "resource.stick"; // spec §54.2: bed rails/slats
    public const string MaterialRope = "resource.rope";    // spec §54.2: bedroll binding

    // Every material a furniture site can bill for — iterate this instead of a
    // hardcoded trio so deposit/read-back cover sticks and rope too.
    public static readonly string[] AllMaterials =
    {
        MaterialLogs, MaterialStones, MaterialLeaves, MaterialSticks, MaterialRope
    };

    public static int Delivered(WorldObjectState site, string materialId)
    {
        var n = 0;
        foreach (var item in site.Contents)
        {
            if (item.DefinitionId == materialId)
            {
                n++;
            }
        }

        return n;
    }

    public static int Remaining(WorldObjectState site, string materialId) => materialId switch
    {
        MaterialLogs => System.Math.Max(0, site.BillLogs - Delivered(site, materialId)),
        MaterialStones => System.Math.Max(0, site.BillStones - Delivered(site, materialId)),
        MaterialLeaves => System.Math.Max(0, site.BillLeaves - Delivered(site, materialId)),
        MaterialSticks => System.Math.Max(0, site.BillSticks - Delivered(site, materialId)),
        MaterialRope => System.Math.Max(0, site.BillRope - Delivered(site, materialId)),
        _ => 0
    };

    public static bool Needs(WorldObjectState site, string materialId) =>
        Remaining(site, materialId) > 0;

    public static bool IsStocked(WorldObjectState site)
    {
        foreach (var mat in AllMaterials)
        {
            if (Remaining(site, mat) > 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSite(WorldObjectState obj) =>
        obj != null && !string.IsNullOrEmpty(obj.BuildProduct);

    // The tag a Gather* goal filters on to fetch what a site still needs.
    public static string TagForMaterial(string materialId) => materialId switch
    {
        MaterialLogs => "Log",
        MaterialStones => "Stone",
        MaterialLeaves => "PalmLeaf",
        MaterialSticks => "Stick",
        MaterialRope => "Rope",
        _ => null
    };
}

internal static class EquipmentMath
{
    public static void Recalculate(WorldState world, NPCState npc)
    {
        var warmth = 0f;
        var armor = 0f;
        foreach (var item in npc.WornItems)
        {
            var (itemWarmth, itemArmor) = ItemValues(world, item.DefinitionId);
            // Spec 35.5: wet cloth loses insulation GRADUALLY — up to −90% at
            // fully soaked (was a hard cliff: 100% until 0.5, then zero; the
            // first minutes of rain changed nothing and the cutoff felt broken).
            warmth += itemWarmth * (1f - 0.9f * MathUtil.Clamp01(item.Wetness));

            armor = System.Math.Max(armor, itemArmor);
        }

        npc.EquippedWarmth = MathUtil.Clamp01(warmth);
        npc.EquippedArmor = armor;

        RecalculateCapacity(world, npc);
    }

    // Spec §52: the pack is only as big as what you wear. Base = the two hands;
    // each worn garment adds its own pockets. Recomputed whenever WornItems
    // changes (every Recalculate caller) and once at bootstrap. Naked ⇒ 2.
    public static void RecalculateCapacity(WorldState world, NPCState npc)
    {
        // Spec §52: hand slots = intact hands (arms severed via §50 remove them),
        // capped by the tunable HandSlots (normally 2 — the pair everyone has).
        var slots = System.Math.Min(npc.Body.IntactHands, SimBalance.HandSlots);
        foreach (var item in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def))
            {
                slots += def.InventoryCapacity;
            }
        }

        npc.Inventory.Capacity = slots;
    }

    // Spec 31A.5B: protection has anatomy — only garments covering the
    // bitten part absorb its damage.
    public static float ArmorForPart(WorldState world, NPCState npc, BodyPart part)
    {
        var best = 0f;
        foreach (var itemId in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(itemId, out var definition) ||
                !definition.Covers.Contains(part))
            {
                continue;
            }

            var (_, itemArmor) = ItemValues(world, itemId);
            best = System.Math.Max(best, itemArmor);
        }

        return best;
    }

    // Spec 35.4: is this body part covered by any worn garment?
    public static bool IsPartCovered(WorldState world, NPCState npc, BodyPart part)
    {
        foreach (var itemId in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(itemId, out var definition) &&
                definition.Covers.Contains(part))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.List<ItemInstance> _destroyedScratch = new();

    // Spec 35.6: durability loss on every garment covering the struck part;
    // at zero the item is rags — removed outright.
    public static void WearCoveringItems(WorldState world, NPCState npc, BodyPart part, float wear)
    {
        _destroyedScratch.Clear();
        foreach (var item in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
                definition.Covers.Contains(part))
            {
                item.Durability -= wear;
                if (item.Durability <= 0f)
                {
                    _destroyedScratch.Add(item);
                }
            }
        }

        DestroyWornItems(world, npc, _destroyedScratch);
    }

    public static void DestroyWornItems(
        WorldState world, NPCState npc,
        System.Collections.Generic.List<ItemInstance> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            npc.WornItems.Remove(item);
            npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.1f);
            Trace.Emit(world, npc.Id, "ItemDestroyed", $"{item.DefinitionId} fell apart");
        }

        Recalculate(world, npc);
        // Spec §52: rags destroy the garment, NOT the pockets' contents. Losing
        // the slots a torn piece provided spills whatever no longer fits onto the
        // ground at the NPC's feet — the bottle in the ripped panties just drops.
        InventoryMath.SpillOverflow(world, npc);
    }

    // Spec 35.5 / §49.7: soggy clothes drag. Only REAL garments (Wear/Outerwear)
    // count — a wet bra/panties/bikini (Underwear) is too light to slow you, so
    // a girl in just underwear (or naked) keeps full speed even soaked.
    public static float WetMovementFactor(WorldState world, NPCState npc)
    {
        var factor = 1f;
        foreach (var item in npc.WornItems)
        {
            if (item.Wetness <= 0.5f)
            {
                continue;
            }

            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) &&
                def.Layer == WearLayer.Underwear)
            {
                continue; // underwear doesn't drag
            }

            factor *= Spec49.WetDragPerGarment;
        }

        return System.MathF.Max(Spec49.WetDragFloor, factor);
    }

    public static (float Warmth, float Armor) ItemValues(WorldState world, string definitionId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition))
        {
            return (0f, 0f);
        }

        var warmth = 0f;
        var armor = 0f;
        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type != InteractionType.Dress)
            {
                continue;
            }

            warmth = System.Math.Max(warmth, interaction.Effects.WarmthDelta);
            armor = System.Math.Max(armor, interaction.Effects.ArmorDelta);
        }

        return (warmth, armor);
    }
}

// Spec 29F.1/29F.2: rabbits graze, hop, and flee; kill attempts resolve
// automatically when a spear-carrying NPC gets adjacent.
public sealed class RabbitSystem : ISimulationSystem
{
    public string Name => nameof(RabbitSystem);

    public TickLayer Layer => TickLayer.Medium;

    private const int MaxRabbits = 4;
    private const int RespawnCheckTicks = 2400; // rabbits breed fast

    private const int SpawnMinDistanceFromNpc = 3;
    private const int FleeRadiusTiles = 2;
    private const float HopChance = 0.2f; // crabs scuttle, not sprint (spec 31C.1)
    private const float KillChance = 0.5f;
    private const int SpookTicks = 150;

    private readonly System.Collections.Generic.List<Wildlife.RabbitState> _deadRabbits = new();
    private readonly System.Collections.Generic.List<JunctionId> _spawnCandidates = new();

    public void Run(WorldState world)
    {
        // Spec 41.2 v2: the timer lives in WorldState so it survives a save.
        if (world.Tick >= world.NextRabbitSpawnCheckTick)
        {
            world.NextRabbitSpawnCheckTick = world.Tick + RespawnCheckTicks;
            while (world.Rabbits.Count < MaxRabbits && TrySpawnRabbit(world))
            {
            }
        }

        _deadRabbits.Clear();
        foreach (var rabbit in world.Rabbits)
        {
            RunRabbit(world, rabbit);
        }

        foreach (var dead in _deadRabbits)
        {
            world.Rabbits.Remove(dead);
            // Spec §54: the kill leaves a carcass to be butchered (no instant loot).
            ExecutionSystem.SpawnCarcass(world, dead.Tile, dead.Junction, "rabbit");
        }
    }

    private void RunRabbit(WorldState world, Wildlife.RabbitState rabbit)
    {
        // Movement: flee from the nearest close NPC, otherwise hop around.
        NPCState? nearest = null;
        var nearestDistance = int.MaxValue;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var distance = HexSpatialMath.HexDistance(rabbit.Tile, npc.Tile);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = npc;
            }
        }

        if (nearest is not null && nearestDistance <= FleeRadiusTiles)
        {
            FleeHop(world, rabbit, nearest);
        }
        else if (MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id, 217) < HopChance)
        {
            RandomHop(world, rabbit);
        }

        // Hunt resolution (spec 29F.2).
        if (world.Tick < rabbit.SpookedUntilTick)
        {
            return;
        }

        // Spec 35.6: bow first — a hunter with an arrow shoots from <= 3
        // tiles, no adjacency chase needed.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CurrentGoal != GoalType.Hunt ||
                !npc.Inventory.Items.Contains("tool.bow") ||
                !npc.Inventory.Items.Contains("resource.arrow") ||
                HexSpatialMath.HexDistance(npc.Tile, rabbit.Tile) > 3)
            {
                continue;
            }

            npc.Inventory.Items.Remove("resource.arrow");
            var hitRoll = MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 173 + npc.Id.Value, 806);
            Trace.Emit(world, npc.Id, "BowShot",
                $"Rabbit={rabbit.Id} Dist={HexSpatialMath.HexDistance(npc.Tile, rabbit.Tile)} Roll={hitRoll:F2}");
            if (hitRoll < 0.6f)
            {
                // Spec §54: no instant loot — the kill drops a carcass to butcher.
                if (MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 211 + npc.Id.Value, 807) < 0.4f)
                {
                    ExecutionSystem.GiveOrDrop(world, npc, "resource.arrow");
                    Trace.Emit(world, npc.Id, "ArrowRecovered", $"From rabbit {rabbit.Id}");
                }

                _deadRabbits.Add(rabbit);
                Trace.Emit(world, npc.Id, "CrabKilled",
                    $"Crab={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R} " +
                    $"(bow, Roll={hitRoll:F2}) -> carcass");
            }
            else
            {
                rabbit.SpookedUntilTick = world.Tick + SpookTicks;
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Hunt);
                Trace.Emit(world, npc.Id, "HuntMissed",
                    $"Rabbit={rabbit.Id} arrow lost in the grass (Roll={hitRoll:F2})");
            }

            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!npc.Inventory.Items.Contains("tool.spear") ||
                npc.CurrentJunction is not { } npcJunction)
            {
                continue;
            }

            var adjacent = npcJunction.Equals(rabbit.Junction) ||
                (world.Junctions.Items.TryGetValue(rabbit.Junction, out var rabbitJunction) &&
                 rabbitJunction.Neighbors.Contains(npcJunction));
            if (!adjacent)
            {
                continue;
            }

            var roll = MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 131 + npc.Id.Value, 605);
            if (roll < KillChance)
            {
                // Spec §54: no instant loot — the kill drops a carcass to butcher.
                _deadRabbits.Add(rabbit);
                Trace.Emit(world, npc.Id, "CrabKilled",
                    $"Crab={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R} " +
                    $"(Roll={roll:F2}) -> carcass");
            }
            else
            {
                rabbit.SpookedUntilTick = world.Tick + SpookTicks;
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Hunt);
                Trace.Emit(world, npc.Id, "HuntMissed",
                    $"Rabbit={rabbit.Id} escaped (Roll={roll:F2})");
            }

            break;
        }
    }

    private static void FleeHop(WorldState world, Wildlife.RabbitState rabbit, NPCState threat)
    {
        if (!world.Junctions.Items.TryGetValue(rabbit.Junction, out var junction))
        {
            return;
        }

        Junction? best = null;
        var bestDistance = -1f;
        foreach (var neighborId in junction.Neighbors)
        {
            if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor) ||
                neighbor.Blocked || neighbor.Door || IsIndoor(world, neighbor) ||
                SpatialQueries.IsAllWaterJunction(world, neighborId))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(neighbor.WorldPosition, threat.Position);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = neighbor;
            }
        }

        if (best is not null)
        {
            MoveRabbitTo(rabbit, best);
        }
    }

    private static void RandomHop(WorldState world, Wildlife.RabbitState rabbit)
    {
        if (!world.Junctions.Items.TryGetValue(rabbit.Junction, out var junction) ||
            junction.Neighbors.Count == 0)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id, 419) * junction.Neighbors.Count);
        pick = System.Math.Min(pick, junction.Neighbors.Count - 1);
        if (world.Junctions.Items.TryGetValue(junction.Neighbors[pick], out var next) &&
            !next.Blocked && !next.Door && !IsIndoor(world, next) &&
            !SpatialQueries.IsAllWaterJunction(world, next.Id))
        {
            MoveRabbitTo(rabbit, next);
        }
    }

    private static bool IsIndoor(WorldState world, Junction junction)
    {
        return junction.Tiles.Count > 0 &&
            world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) &&
            tile.Flags.HasFlag(TileFlags.Indoor);
    }

    private static void MoveRabbitTo(Wildlife.RabbitState rabbit, Junction next)
    {
        rabbit.Junction = next.Id;
        rabbit.Position = next.WorldPosition;
        if (next.Tiles.Count > 0)
        {
            rabbit.Tile = next.Tiles[0];
        }
    }

    private readonly System.Collections.Generic.List<TileCoord> _waterTiles = new();

    private bool TrySpawnRabbit(WorldState world)
    {
        _spawnCandidates.Clear();
        _waterTiles.Clear();
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (tile.Flags.HasFlag(TileFlags.Water))
            {
                _waterTiles.Add(tile.Coord);
            }
        }

        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 || IsIndoor(world, junction) ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            // Spec 31C.1: crabs live on the river bank — within 2 tiles of water.
            var nearWater = false;
            foreach (var waterCoord in _waterTiles)
            {
                if (HexSpatialMath.HexDistance(junction.Tiles[0], waterCoord) <= 2)
                {
                    nearWater = true;
                    break;
                }
            }

            if (!nearWater)
            {
                continue;
            }

            var farEnough = true;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (HexSpatialMath.HexDistance(junction.Tiles[0], npc.Tile) < SpawnMinDistanceFromNpc)
                {
                    farEnough = false;
                    break;
                }
            }

            if (farEnough)
            {
                _spawnCandidates.Add(junction.Id);
            }
        }

        if (_spawnCandidates.Count == 0)
        {
            return false;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, world.NextRabbitId, 947) * _spawnCandidates.Count);
        pick = System.Math.Min(pick, _spawnCandidates.Count - 1);
        var spawnJunction = world.Junctions.Items[_spawnCandidates[pick]];

        var rabbit = new Wildlife.RabbitState
        {
            Id = world.NextRabbitId++,
            Junction = spawnJunction.Id,
            Position = spawnJunction.WorldPosition,
            Tile = spawnJunction.Tiles[0]
        };
        world.Rabbits.Add(rabbit);
        Trace.EmitSystem(world, "CrabSpawned",
            $"Rabbit={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R}");
        return true;
    }
}

// §56 Predation cannibalism: resolves the KILL half of "kill a housemate to eat
// them" (the EAT half is the existing §54 Butcher→meat→Eat chain). A predator is
// an NPC whose DecisionSystem picked GoalType.Prey — already gated to a starving,
// low-compassion survivor with no softer food. This system deals the strikes:
// once adjacent to the weakest housemate it wounds them each tick via WoundMath
// until they fall, then routes the death through the shared RemoveDeadNpc (which
// spawns the corpse and makes witnesses grieve) and applies the heavy social
// fallout. Mirrors DogSystem/RabbitSystem's adjacency-strike + death-sweep shape.
public sealed class PredationSystem : ISimulationSystem
{
    public string Name => nameof(PredationSystem);

    public TickLayer Layer => TickLayer.Medium;

    private readonly System.Collections.Generic.List<EntityId> _deadVictims = new();
    private readonly System.Collections.Generic.List<EntityId> _deadAttackers = new();

    public void Run(WorldState world)
    {
        if (!SimBalance.PredationEnabled)
        {
            return;
        }

        _deadVictims.Clear();
        _deadAttackers.Clear();
        foreach (var predator in world.Entities.Npcs.Values)
        {
            if (predator.Mind.CurrentGoal != GoalType.Prey ||
                predator.CurrentJunction is not { } predJunction)
            {
                continue;
            }

            // Strike whichever living housemate we're adjacent to — the pursued
            // weakest ends up here. Adjacency = same or neighbouring junction.
            NPCState? victim = null;
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id == predator.Id || other.Health <= 0f ||
                    other.CurrentJunction is not { } otherJunction)
                {
                    continue;
                }

                var adjacent = predJunction.Equals(otherJunction) ||
                    (world.Junctions.Items.TryGetValue(otherJunction, out var oj) &&
                     oj.Neighbors.Contains(predJunction));
                if (adjacent && (victim is null || other.Health < victim.Health))
                {
                    victim = other;
                }
            }

            if (victim is null)
            {
                continue;
            }

            // A starved attacker is weak (StrikeFactor); weapons bite deeper
            // while heavy weapons strike less often.
            var weaponId = SimBalance.BestMeleeWeapon(predator.Inventory.Items, predator.Body.IntactHands);
            var weaponMult = SimBalance.MeleeStrikeBonus(weaponId);
            var attackSpeed = SimBalance.MeleeAttackSpeed(weaponId);
            if (!SimBalance.MeleeStrikeReady(world.Tick, predator.Id.Value, weaponId))
            {
                Trace.Emit(world, predator.Id, "PreyWindup",
                    $"Victim={victim.Id.Value} Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)} " +
                    $"Speed={attackSpeed:F1}");
                continue;
            }

            // Apply the strike exactly like a dog's bite (SimulationSystems dog
            // path): garment on the struck part absorbs, the part loses HP,
            // Health is the body mean, the hit files a wound decal, and a
            // destroyed vital is instant death. This mirrors the established
            // damage flow so the kill is detectable in the same tick.
            var part = PickKillPart(world, predator.Id.Value);
            var partArmor = EquipmentMath.ArmorForPart(world, victim, part);
            var damage = SimBalance.PredationStrikePerPass *
                predator.Body.StrikeFactor() * weaponMult * (1f - partArmor);
            victim.Body.Parts[part] = System.Math.Max(0f, victim.Body.Parts[part] - damage);
            victim.Health = victim.Body.Mean();
            WoundMath.Inflict(world, victim, part, damage);
            if (victim.Body.VitalDestroyed(out _))
            {
                victim.Health = 0f;
            }

            Trace.Emit(world, predator.Id, "Preyed",
                $"Victim={victim.Id.Value} {part} -{damage:F3} (armor={partArmor:F2}) " +
                $"Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)} Speed={attackSpeed:F1} " +
                $"VictimHealth={victim.Health:F2}");

            if (victim.Health <= 0f)
            {
                if (!_deadVictims.Contains(victim.Id))
                {
                    _deadVictims.Add(victim.Id);
                    ApplyKillConsequences(world, predator, victim);
                }

                continue; // the victim is down — no defence this tick
            }

            // --- The victim DEFENDS: this is a real fight, not an execution.
            // It reuses the dog-fight model (Spec 29C.4A) — remember the danger,
            // then either BOLT for an indoor refuge if badly hurt, or STAND and
            // trade a real blow at the attacker. An armed, healthy victim can
            // wound, kill, or outrun a starved predator — so predation can fail.
            DogSystem.RememberDanger(world, victim);

            var victimFleeing = victim.Mind.CurrentGoal == GoalType.Flee;
            if (!victimFleeing &&
                (victim.Health < SimBalance.PredationFleeHealth ||
                 DogSystem.WorstPartHealth(victim) < 0.35f))
            {
                victimFleeing = DogSystem.TryStartFlee(world, victim, 1);
            }

            if (victimFleeing)
            {
                Trace.Emit(world, victim.Id, "PreyFled",
                    $"From NPC{predator.Id.Value} (Health={victim.Health:F2})");
                continue;
            }

            // Stand and fight back — drop the chores, swing at the attacker.
            victim.IsFighting = true;
            if (victim.Plan.Status == PlanStatus.Active ||
                victim.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, victim, $"Fighting off NPC{predator.Id.Value}");
                victim.Mind.CurrentGoal = GoalType.None;
            }

            var defWeaponId = SimBalance.BestMeleeWeapon(victim.Inventory.Items, victim.Body.IntactHands);
            var defWeapon = SimBalance.MeleeStrikeBonus(defWeaponId);
            var defAttackSpeed = SimBalance.MeleeAttackSpeed(defWeaponId);
            var defStrikeReady = SimBalance.MeleeStrikeReady(world.Tick, victim.Id.Value, defWeaponId);

            // The counter-blow uses the same NPC strike-back value that fends off
            // dogs, scaled by the defender's own StrikeFactor()/weapon and the
            // attacker's armor.
            var defPart = PickKillPart(world, victim.Id.Value + 7919);
            var defArmor = EquipmentMath.ArmorForPart(world, predator, defPart);
            var defDamage = defStrikeReady
                ? SimBalance.NpcStrikePerPass * victim.Body.StrikeFactor() * defWeapon * (1f - defArmor)
                : 0f;
            if (defDamage > 0f)
            {
                predator.Body.Parts[defPart] = System.Math.Max(0f, predator.Body.Parts[defPart] - defDamage);
                predator.Health = predator.Body.Mean();
                WoundMath.Inflict(world, predator, defPart, defDamage);
                if (predator.Body.VitalDestroyed(out _))
                {
                    predator.Health = 0f;
                }
            }

            Trace.Emit(world, victim.Id, "PreyFoughtBack",
                $"Attacker=NPC{predator.Id.Value} {defPart} -{defDamage:F3} " +
                $"Weapon={(string.IsNullOrEmpty(defWeaponId) ? "fists" : defWeaponId)}" +
                $"{(defStrikeReady ? string.Empty : " recovering")} Speed={defAttackSpeed:F1} " +
                $"AttackerHealth={predator.Health:F2}");

            if (predator.Health <= 0f && !_deadAttackers.Contains(predator.Id))
            {
                _deadAttackers.Add(predator.Id);
                Trace.EmitSystem(world, "PredatorKilled",
                    $"NPC{victim.Id.Value} killed attacker NPC{predator.Id.Value} in self-defence");
            }
        }

        foreach (var deadId in _deadVictims)
        {
            // Shared death path: spawns corpse.npc (butcherable) + grieves witnesses.
            DogSystem.RemoveDeadNpc(world, deadId);
        }

        foreach (var deadId in _deadAttackers)
        {
            // A predator felled by its intended prey — an ordinary death (no
            // "Murdered" fallout; self-defence isn't a colony crime).
            if (!_deadVictims.Contains(deadId))
            {
                DogSystem.RemoveDeadNpc(world, deadId);
            }
        }
    }

    // Lethal intent: aim for the vitals far more than a dog's leg-first bite, so
    // the kill actually comes rather than merely maiming.
    private static BodyPart PickKillPart(WorldState world, int predatorId)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick, predatorId, 561);
        if (roll < 0.55f) return BodyPart.Torso;
        if (roll < 0.80f) return BodyPart.Head;
        if (roll < 0.90f) return BodyPart.Pelvis;
        if (roll < 0.95f) return BodyPart.ArmR;
        return BodyPart.LegR;
    }

    // §56: killing to eat is a colony trauma — a heavy comfort hit on the killer
    // and a sharp relationship collapse toward them from every witness. (Grief on
    // witnesses is already triggered by the shared death sweep, RemoveDeadNpc.)
    private static void ApplyKillConsequences(WorldState world, NPCState killer, NPCState victim)
    {
        killer.Needs.Comfort = MathUtil.Clamp(
            killer.Needs.Comfort - SimBalance.PredationComfortPenalty, 0f, 1f);

        foreach (var witness in world.Entities.Npcs.Values)
        {
            if (witness.Id == killer.Id || witness.Id == victim.Id ||
                HexSpatialMath.HexDistance(witness.Tile, victim.Tile) > 6)
            {
                continue;
            }

            var rel = witness.Social.GetOrCreate(killer.Id);
            rel.Affinity = MathUtil.Clamp(
                rel.Affinity - SimBalance.PredationWitnessAffinityLoss, -1f, 1f);
        }

        Trace.EmitSystem(world, "Murdered",
            $"NPC{killer.Id.Value} killed NPC{victim.Id.Value} for meat [predation] " +
            $"KillerComfort={killer.Needs.Comfort:F2}");
    }
}

// Spec 40.18: sharks — simple water roamers (like dogs, not NPCs) that bite
// any NPC caught swimming. Dormant against the land colony: they can't leave
// the water and NPCs don't yet swim (the ring is a dead-end), so the bite
// never fires until a second island gives the ring a far shore.
public sealed class SharkSystem : ISimulationSystem
{
    public string Name => nameof(SharkSystem);

    public TickLayer Layer => TickLayer.Medium;

    private const int MaxSharks = 2;

    private static readonly System.Collections.Generic.List<Common.JunctionId> _waterScratch = new();

    public void Run(WorldState world)
    {
        if (world.Sharks.Count < MaxSharks && world.SwimJunctions.Count > 0)
        {
            var swims = new System.Collections.Generic.List<Common.JunctionId>(world.SwimJunctions);
            var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, world.Sharks.Count, 6101) * swims.Count);
            pick = System.Math.Min(pick, swims.Count - 1);
            if (world.Junctions.Items.TryGetValue(swims[pick], out var jn))
            {
                world.Sharks.Add(new Wildlife.SharkState
                {
                    Id = 900 + world.Sharks.Count,
                    Junction = swims[pick],
                    Tile = jn.Tiles.Count > 0 ? jn.Tiles[0] : default,
                    Position = jn.WorldPosition
                });
            }
        }

        foreach (var shark in world.Sharks)
        {
            RoamShark(world, shark);
            BiteSwimmers(world, shark);
        }
    }

    // Patrol among water junctions (the swim ring + open sea).
    private static void RoamShark(WorldState world, Wildlife.SharkState shark)
    {
        if (!world.Junctions.Items.TryGetValue(shark.Junction, out var junction) ||
            junction.Neighbors.Count == 0)
        {
            return;
        }

        _waterScratch.Clear();
        foreach (var nid in junction.Neighbors)
        {
            if (SpatialQueries.IsAllWaterJunction(world, nid) || world.SwimJunctions.Contains(nid))
            {
                _waterScratch.Add(nid);
            }
        }

        if (_waterScratch.Count == 0)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, shark.Id, 6203) * _waterScratch.Count);
        pick = System.Math.Min(pick, _waterScratch.Count - 1);
        if (world.Junctions.Items.TryGetValue(_waterScratch[pick], out var nj))
        {
            shark.Junction = _waterScratch[pick];
            shark.Tile = nj.Tiles.Count > 0 ? nj.Tiles[0] : shark.Tile;
            shark.Position = nj.WorldPosition;
        }
    }

    // Bite an NPC that's swimming on/next to the shark (dormant until swimming).
    private static void BiteSwimmers(WorldState world, Wildlife.SharkState shark)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f || npc.CurrentJunction is not { } njct ||
                !world.SwimJunctions.Contains(njct))
            {
                continue;
            }

            var adjacent = njct.Equals(shark.Junction) ||
                (world.Junctions.Items.TryGetValue(shark.Junction, out var sj) &&
                 sj.Neighbors.Contains(njct));
            if (!adjacent)
            {
                continue;
            }

            npc.Body.Parts[BodyPart.LegR] =
                System.Math.Max(0f, npc.Body.Parts[BodyPart.LegR] - SimBalance.SharkBiteDamage);
            npc.Health = npc.Body.Mean();
            npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - 0.15f);
            WoundMath.Inflict(world, npc, BodyPart.LegR, SimBalance.SharkBiteDamage);
            // Spec §50: a shark's 0.2 bite clears the big-blow threshold — if it
            // takes the leg to 0, it comes off.
            AmputateSystemHelpers.TrySeverOnBite(world, npc, BodyPart.LegR, SimBalance.SharkBiteDamage);
            Trace.Emit(world, npc.Id, "SharkBite", $"NPC{npc.Id.Value} bitten by shark {shark.Id}");
            break;
        }
    }
}

// Spec 35.1: O(1) reachability via connected components. Path BFS remains
// only for actual movement; every "can I get there at all" check uses this.
// Spec 35.5: seeded rain fronts — no state machine beyond a deadline tick.
public sealed class WeatherSystem : ISimulationSystem
{
    public string Name => nameof(WeatherSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        // The schedule is a pure function of (seed, day) — a per-tick
        // Bernoulli roll mixed badly on the 16-tick stride (spec 35.5).
        var env = world.Environment;
        var day = world.Tick / EnvironmentSystem.DayLengthTicks;
        var raining = false;
        if (MathUtil.Hash01(world.Seed, day, 17, 3301) < 0.45f)
        {
            var start = day * EnvironmentSystem.DayLengthTicks +
                (int)(MathUtil.Hash01(world.Seed, day, 18, 3301) * 2100f);
            var duration = 300 + (int)(600f * MathUtil.Hash01(world.Seed, day, 19, 3302));
            raining = world.Tick >= start && world.Tick < start + duration;
            env.RainUntilTick = start + duration;
        }

        if (raining != env.IsRaining)
        {
            env.IsRaining = raining;
            Trace.EmitSystem(world, raining ? "RainStarted" : "RainStopped",
                raining ? $"Until={env.RainUntilTick}" : $"Tick={world.Tick}");
        }

        // §46 v2: the STORM SURGE — the sea claws logs back off the raft.
        // A seeded swing catastrophe (pure function of seed+day, like rain):
        // losing progress stretches the run, and a longer run means more
        // night-raid rolls — the two catastrophes compound into real 50/50
        // tension without making daily survival harsher.
        if (world.RaftProgress > 0 &&
            world.Tick == day * EnvironmentSystem.DayLengthTicks + StormSurgeOffsetTicks &&
            MathUtil.Hash01(world.Seed, day, 5151) < StormChancePerDay)
        {
            var washed = System.Math.Min(world.RaftProgress, StormRaftLogLoss);
            world.RaftProgress -= washed;
            Trace.EmitSystem(world, "StormSurge",
                $"-{washed} raft logs -> {world.RaftProgress}/{WorldState.RaftTarget} (day {day})");
        }
    }

    // §46 v2: storm-surge catastrophe knobs. Offset 1600 keeps the tick on
    // the Slow (16-tick) grid this system runs on.
    private const float StormChancePerDay = 0.08f; // §21.21B v4 recalibration: circle-climb reshuffle left 4/12 lone-survivor TIMEOUTS (6-10 storms outpaced a solo raft rebuild) — fewer surges converts stalls into decided runs
    private const int StormRaftLogLoss = 2;
    private const int StormSurgeOffsetTicks = 1600;
}

// Spec 35.5: wetting and drying for every item instance in the world —
// worn, carried, and wearables lying on the ground (incl. the rack).
public sealed class MoistureSystem : ISimulationSystem
{
    public string Name => nameof(MoistureSystem);

    public TickLayer Layer => TickLayer.Slow;

    private static readonly System.Collections.Generic.List<ItemInstance> _wornOutScratch = new();

    private const float RainWetRate = 0.04f;
    private const float RiverWetRate = 0.15f;
    private const float DryBase = 0.02f;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var indoor = world.Tiles.Items.TryGetValue(npc.Tile, out var tile) &&
                tile.Flags.HasFlag(TileFlags.Indoor);
            var onWater = tile is not null && tile.Flags.HasFlag(TileFlags.Water);
            var wetting = onWater ? RiverWetRate :
                world.Environment.IsRaining && !indoor ? RainWetRate : 0f;
            var dryRate = DryBase * DryMultiplier(world, npc.Tile, indoor, rackBoost: false);

            UpdateItems(world, npc, npc.WornItems, wetting, dryRate, worn: true);
            UpdateItems(world, npc, npc.Inventory.Items, wetting, dryRate, worn: false);

            // Spec 35.6: worn cloth wears 0.02 per game-day (150 slow ticks) —
            // doubled so natural wear VISIBLY frays clothes within ~a week
            // (holes start below durability 0.85; rags fall apart ~day 50).
            _wornOutScratch.Clear();
            foreach (var item in npc.WornItems)
            {
                item.Durability -= 0.02f / 150f;
                if (item.Durability <= 0f)
                {
                    _wornOutScratch.Add(item);
                }
            }

            EquipmentMath.DestroyWornItems(world, npc, _wornOutScratch);
            EquipmentMath.Recalculate(world, npc);
        }

        // Ground wearables: rained on outdoors, dry otherwise; x5 on the rack.
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Layer is null)
            {
                continue;
            }

            var indoor = world.Tiles.Items.TryGetValue(obj.Tile, out var tile) &&
                tile.Flags.HasFlag(TileFlags.Indoor);
            if (world.Environment.IsRaining && !indoor)
            {
                obj.Wetness = MathUtil.Clamp01(obj.Wetness + RainWetRate);
                continue;
            }

            var onRack = IsOnRack(world, obj);
            obj.Wetness = System.MathF.Max(0f,
                obj.Wetness - DryBase * DryMultiplier(world, obj.Tile, indoor, onRack));
        }
    }

    private static void UpdateItems(
        WorldState world, NPCState npc,
        System.Collections.Generic.List<ItemInstance> items,
        float wetting, float dryRate, bool worn)
    {
        foreach (var item in items)
        {
            if (wetting > 0f)
            {
                var before = item.Wetness;
                item.Wetness = MathUtil.Clamp01(item.Wetness + wetting);
                if (worn && before <= 0.5f && item.Wetness > 0.5f)
                {
                    Trace.Emit(world, npc.Id, "SoakedThrough",
                        $"{item.DefinitionId} Wetness={item.Wetness:F2}");
                }
            }
            else
            {
                item.Wetness = System.MathF.Max(0f, item.Wetness - dryRate);
            }
        }
    }

    // Spec 35.5: best of sun x3 / lit campfire x4 / rack x5, else x1.
    private static float DryMultiplier(WorldState world, TileCoord tile, bool indoor, bool rackBoost)
    {
        var best = 1f;
        if (rackBoost)
        {
            best = 5f;
        }
        else if (NearLitCampfire(world, tile))
        {
            best = 4f;
        }

        if (best < 3f && !indoor && world.Environment.UvIndex > 0.3f &&
            !TemperatureSystem.IsShaded(world, tile))
        {
            best = 3f;
        }

        return best;
    }

    private static bool NearLitCampfire(WorldState world, TileCoord tile)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == "campfire.spot" && obj.ResourceAmount > 0f &&
                HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOnRack(WorldState world, WorldObjectState item)
    {
        if (item.Junctions.Count == 0)
        {
            return false;
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == "station.drying_rack" && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(item.Junctions[0]))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class Connectivity
{
    // Spec 31C.7: "can I stand next to it" — blocked/water anchors are
    // reachable through any passable dry neighbor (solid furniture and
    // river-water drink spots must stay visible to planning).
    public static bool ReachableBeside(WorldState world, JunctionId from, JunctionId anchor, bool canJump = true)
    {
        var anchorBlocked = !world.Junctions.Items.TryGetValue(anchor, out var junction) ||
            junction.Blocked || SpatialQueries.IsAllWaterJunction(world, anchor);
        if (!anchorBlocked)
        {
            return Reachable(world, from, anchor, canJump);
        }

        SpatialQueries.CollectStandableAround(world, anchor, _besideScratch);
        foreach (var rim in _besideScratch)
        {
            if (Reachable(world, from, rim, canJump))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _besideScratch = new();

    public static bool Reachable(WorldState world, JunctionId a, JunctionId b, bool canJump = true)
    {
        // Spec §50: a legless survivor reads the graph WITHOUT elevation-step
        // edges — a higher ledge or the water is a separate component to her.
        if (!canJump)
        {
            if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
            {
                RebuildFlat(world);
            }

            return world.JunctionComponentsFlat.TryGetValue(a, out var fa) && fa >= 0 &&
                   world.JunctionComponentsFlat.TryGetValue(b, out var fb) &&
                   fa == fb;
        }

        if (world.ComponentsBuiltVersion != world.TopologyVersion)
        {
            Rebuild(world);
        }

        return world.JunctionComponents.TryGetValue(a, out var ca) && ca >= 0 &&
               world.JunctionComponents.TryGetValue(b, out var cb) &&
               ca == cb;
    }

    private static readonly System.Collections.Generic.Queue<JunctionId> _queue = new();

    private static void Rebuild(WorldState world)
    {
        world.JunctionComponents.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            world.JunctionComponents[junction.Id] = junction.Blocked ? -1 : 0;
        }

        var component = 0;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || world.JunctionComponents[junction.Id] != 0)
            {
                continue;
            }

            component++;
            world.JunctionComponents[junction.Id] = component;
            _queue.Clear();
            _queue.Enqueue(junction.Id);
            while (_queue.Count > 0)
            {
                var currentId = _queue.Dequeue();
                var current = world.Junctions.Items[currentId];
                foreach (var neighborId in current.Neighbors)
                {
                    if (world.JunctionComponents.TryGetValue(neighborId, out var mark) && mark == 0 &&
                        world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked)
                    {
                        world.JunctionComponents[neighborId] = component;
                        _queue.Enqueue(neighborId);
                    }
                }
            }
        }

        world.ComponentsBuiltVersion = world.TopologyVersion;
        Trace.EmitSystem(world, "ConnectivityRebuilt",
            $"Components={component} Junctions={world.Junctions.Items.Count}");
    }

    // Spec §50: the no-jump connectivity graph — identical to Rebuild but an
    // edge is only followed when it stays on one elevation (RequiresJump false),
    // so each elevation shelf (and the water) is its own component. A survivor
    // who lost a leg reads reachability through this map.
    private static void RebuildFlat(WorldState world)
    {
        world.JunctionComponentsFlat.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            world.JunctionComponentsFlat[junction.Id] = junction.Blocked ? -1 : 0;
        }

        var component = 0;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || world.JunctionComponentsFlat[junction.Id] != 0)
            {
                continue;
            }

            component++;
            world.JunctionComponentsFlat[junction.Id] = component;
            _queue.Clear();
            _queue.Enqueue(junction.Id);
            while (_queue.Count > 0)
            {
                var currentId = _queue.Dequeue();
                var current = world.Junctions.Items[currentId];
                foreach (var neighborId in current.Neighbors)
                {
                    if (world.JunctionComponentsFlat.TryGetValue(neighborId, out var mark) && mark == 0 &&
                        world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked &&
                        !Navigation.HexPathfinder.RequiresJump(world, currentId, neighborId))
                    {
                        world.JunctionComponentsFlat[neighborId] = component;
                        _queue.Enqueue(neighborId);
                    }
                }
            }
        }

        world.ComponentsFlatBuiltVersion = world.TopologyVersion;
        Trace.EmitSystem(world, "ConnectivityFlatRebuilt",
            $"Components={component} Junctions={world.Junctions.Items.Count}");
    }
}

internal static class Trace
{
    public static void Emit(WorldState world, EntityId entityId, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = entityId.Value,
            Type = type,
            Message = message
        });
    }

    public static void EmitSystem(WorldState world, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = null,
            Type = type,
            Message = message
        });
    }

    public static string FormatTile(TileCoord? tile) => tile is null ? "-" : $"{tile.Value.Q},{tile.Value.R}";

    public static string FormatNeeds(NPCNeeds n) =>
        $"H={n.Hunger:F2} W={n.Thirst:F2} E={n.Energy:F2} C={n.Comfort:F2} S={n.Social:F2} T={n.ThermalDiscomfort:F2}";

    public static string FormatPos(Float2 p) => $"({p.X:F2},{p.Y:F2})";

    public static string FormatJunction(JunctionId? j) => j is null ? "-" : j.Value.Value.ToString();
}

}
