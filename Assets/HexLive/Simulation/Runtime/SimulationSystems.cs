using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

public interface ISimulationSystem
{
    string Name { get; }

    TickLayer Layer { get; }

    void Run(WorldState world);
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
                    Connectivity.ReachableBeside(world, npcJunction.Value, objJunction.Value);

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
                    Connectivity.ReachableBeside(world, npcJunction.Value, record.Junction.Value);

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
                     Connectivity.Reachable(world, npcJunction.Value, otherJunction.Value));
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
        if (npc.CurrentJunction.HasValue && world.Junctions.Items.ContainsKey(npc.CurrentJunction.Value))
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
    private const float StarvingEnterThreshold = 0.85f;
    private const float StarvingClearThreshold = 0.60f;
    private const float StarvingBoost = 1f;

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

            // Eat consumes from inventory; GetFood acquires from the world (spec 29B).
            var hasFoodInInventory = npc.Inventory.FindFirstFood(world.Content) != null;
            // Restraint gate (spec 29B.2): don't harvest food you don't need,
            // or the ground stock never survives until the productionless night.
            const float GetFoodHungerThreshold = 0.35f;

            var eatAvail = hasFoodInInventory;
            var getFoodAvail = !hasFoodInInventory && npc.Inventory.HasSpace &&
                npc.Needs.Hunger >= GetFoodHungerThreshold &&
                (HasInteraction(npc, InteractionType.PickUp) || KnowsReachableProducer(npc, world));
            // Spec 29G: the land itself is furniture — a bed is better, but
            // sleep never blocks on owning one. Still, nobody naps at noon
            // out of boredom: sleep is for the tired or for the dark hours
            // (without this gate the first soak showed 73 ground naps eating
            // every idle minute — no explores, the fire never lit).
            var sleepAvail = npc.Needs.Energy < 0.45f ||
                world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
            // Spec 31C.7A: sit because you need it — and never settle into a
            // chair on an empty stomach. Sitting yields to sleep hours (the
            // Sit->Sleep churn was 47 interrupts/soak before this gate).
            var sitAvail = npc.Needs.Comfort < 0.6f &&
                npc.Needs.Hunger < 0.6f && npc.Needs.Thirst < 0.6f &&
                !sleepAvail;
            // Spec 29C.4 restraint: dress only against cold — an overheated
            // NPC reaching for more clothes is a doom loop.
            // Spec 29C.4A: fresh danger overrides the weather — arm up.
            var effectiveTemp = world.Environment.GlobalTemperature + npc.EquippedWarmth * 10f;
            var wantsArmor = npc.Memory.Dangers.Count > 0 && npc.EquippedArmor < 0.3f &&
                KnowsReachableArmor(npc, world);
            var dressAvail = wantsArmor ||
                (npc.Needs.ThermalDiscomfort >= 0.3f &&
                 effectiveTemp < 14f &&
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
            var carriedLogs = CountInventory(npc, "resource.firewood");
            var carriedLeaves = CountInventory(npc, "resource.palm_leaf");

            // Spec 29E: the water chain — drink (boiled if possible, raw as a
            // gamble), and the logistics goals that make boiled possible.
            var hasLighter = npc.Inventory.Items.Contains("tool.lighter");
            var hasPot = npc.Inventory.Items.Contains("tool.pot");
            var hasWood = npc.Inventory.Items.Contains("resource.firewood");
            var (campfireSeen, campfireFuel) = FindCampfire(npc, world);
            var pondReachable = HasReachableWithTag(npc, world, "RawWater");
            var boiledReady = campfireSeen && campfireFuel > 0f && hasPot;
            // Spec 29H: the two-step water chain — fill the bottle, then drink.
            var bottleEmpty = npc.BottleWater == WaterKind.None;
            var getWaterAvail = npc.Needs.Thirst >= 0.35f && bottleEmpty &&
                (boiledReady || pondReachable);
            var drinkAvail = npc.Needs.Thirst >= 0.35f && !bottleEmpty;
            // Spec 35.2: any reachable Tool not carried (saw, dropped gear).
            var gatherToolsAvail = npc.Inventory.HasSpace &&
                HasMissingToolReachable(npc, world);
            var fuelLow = campfireSeen && campfireFuel < 600f;
            var gatherWoodAvail = (!hasWood && fuelLow ||
                    (piece is { } pLog && carriedLogs < pLog.Logs)) &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Firewood");
            var tendFireAvail = hasWood && fuelLow && (campfireFuel > 0f || hasLighter);

            AddGoalScore(npc, world.Tick, GoalType.Drink, npc.Needs.Thirst, drinkAvail, drinkBoost);
            AddGoalScore(npc, world.Tick, GoalType.GetWater, npc.Needs.Thirst, getWaterAvail, drinkBoost);
            AddGoalScore(npc, world.Tick, GoalType.GatherTools,
                0.25f + 0.2f * npc.Needs.Thirst, gatherToolsAvail);
            AddGoalScore(npc, world.Tick, GoalType.GatherWood,
                0.2f + 0.3f * npc.Needs.Thirst, gatherWoodAvail);
            AddGoalScore(npc, world.Tick, GoalType.TendFire,
                0.25f + 0.3f * npc.Needs.Thirst, tendFireAvail);

            // Spec 29F: hunting & crafting.
            var hasSpear = npc.Inventory.Items.Contains("tool.spear");
            var hasRawMeat = npc.Inventory.Items.Contains("food.meat_raw");
            var hideCount = CountInventory(npc, "resource.hide");
            // Spec 35.6: ranged hunters need no spear.
            var hasBow = npc.Inventory.Items.Contains("tool.bow");
            var arrowCount = CountInventory(npc, "resource.arrow");
            var armed = hasSpear || (hasBow && arrowCount > 0);
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
            var craftBowAvail = !hasBow && carriedLogs >= 2 && hideCount >= 1 &&
                campfireSeen && !craftLeatherAvail;
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
                    (piece is { } pStone && stoneCount < pStone.Stones)) &&
                npc.Inventory.HasSpace && HasReachableWithTag(npc, world, "Stone");
            var craftAxeAvail = !hasAxe && !hasSaw && hasWood && stoneCount >= 1 && campfireSeen;
            var craftPickaxeAvail = !hasPickaxe && hasWood && stoneCount >= 2 && campfireSeen;
            var canChop = hasAxe || hasSaw;
            var harvestTreeAvail = canChop && npc.Inventory.HasSpace &&
                ((fuelLow && !HasReachableWithTag(npc, world, "Firewood") &&
                  (HasReachableWithTag(npc, world, "BigTree") || HasReachableWithTag(npc, world, "Palm"))) ||
                 ((CountInventory(npc, "resource.palm_leaf") == 0 ||
                   (piece is { } pLeaf && carriedLeaves < pLeaf.Leaves)) &&
                  HasReachableWithTag(npc, world, "Palm")));
            var mineBoulderAvail = hasPickaxe && stoneCount < 2 && npc.Inventory.HasSpace &&
                HasReachableWithTag(npc, world, "Boulder");

            AddGoalScore(npc, world.Tick, GoalType.GatherStone, 0.25f, gatherStoneAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftAxe, 0.3f, craftAxeAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftPickaxe, 0.25f, craftPickaxeAvail);
            AddGoalScore(npc, world.Tick, GoalType.HarvestTree, 0.3f, harvestTreeAvail);
            AddGoalScore(npc, world.Tick, GoalType.MineBoulder, 0.25f, mineBoulderAvail);

            // Spec 35.3: build when the full bill for the pending piece is carried.
            var buildAvail = piece is { } needNow &&
                carriedLogs >= needNow.Logs && stoneCount >= needNow.Stones &&
                carriedLeaves >= needNow.Leaves &&
                HasReachableWithTag(npc, world, "BuildSite");
            AddGoalScore(npc, world.Tick, GoalType.Build, 0.4f, buildAvail);

            // Spec 35.4: overheating drives a trip to shade or the river.
            var coolOffUrge = System.MathF.Max(npc.Needs.ThermalDiscomfort, npc.SunExposure - 0.4f);
            var coolOffAvail = ((effectiveTemp > 20f && npc.Needs.ThermalDiscomfort >= 0.35f) ||
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

            var craftRackAvail = !RackExists(world) && carriedLogs >= 2 && campfireSeen;

            // Spec 29G: the bed must be earned — 2 logs + 3 palm leaves.
            // The hearth outranks the mattress: never spend logs on a bed
            // while the fire is hungry (777 soak: the bed ate the only wood
            // and the fire never burned again — boiledDrinks=0 all game).
            // Spec 40.14: 3 leaves alone weave the cheap leaf mat; 2 spare logs
            // upgrade it to the solid bedroll (chosen at craft time). The fire
            // must still be alive — its logs are never robbed for the bedroll.
            var craftBedAvail = !HasReachableWithTag(npc, world, "Bed") &&
                carriedLeaves >= 3 && campfireSeen && campfireFuel > 0f;
            // 0.6: with the full kit in hand and the fire alive, the bed
            // must outbid TendFire (<=0.55) — at 0.35 the kit's logs were
            // always eaten by the hearth and the bed never happened.
            AddGoalScore(npc, world.Tick, GoalType.CraftBed, 0.6f, craftBedAvail);

            // Spec 40.14: a sun shelter — woven from 4 spare palm leaves at the
            // fire when the sun bites and there's no shade near home yet. Score
            // scales with the current UV so it's a fair-weather project, not a
            // constant pull (keeps the fragile colony from reshuffling).
            var craftTentAvail = world.Environment.UvIndex > 0.4f &&
                carriedLeaves >= 4 && campfireSeen &&
                !HasReachableWithTag(npc, world, "Shelter");
            AddGoalScore(npc, world.Tick, GoalType.CraftTent,
                0.25f + 0.3f * world.Environment.UvIndex, craftTentAvail);

            // Spec 40.15: the escape raft — a low-priority background project.
            // Only when survival is handled (fed, fire fine, no danger) does a
            // log get carried to the coast; the way off the island is earned
            // slowly, never at the expense of staying alive.
            var buildRaftAvail = carriedLogs >= 1 && world.RaftProgress < WorldState.RaftTarget &&
                !fuelLow && npc.Needs.Hunger < 0.5f && npc.Needs.Thirst < 0.5f &&
                npc.Memory.Dangers.Count == 0 && KnowsReachableWithTag(npc, world, "Raft");
            AddGoalScore(npc, world.Tick, GoalType.BuildRaft, 0.28f, buildRaftAvail);
            AddGoalScore(npc, world.Tick, GoalType.CraftRack,
                0.3f + (world.Environment.IsRaining || wornWetness > 0.5f ? 0.2f : 0f),
                craftRackAvail);

            var dryAvail = wornWetness > 0.5f && !world.Environment.IsRaining &&
                (HasReachableWithTag(npc, world, "Rack") ||
                 (campfireSeen && campfireFuel > 0f));
            AddGoalScore(npc, world.Tick, GoalType.DryClothes,
                0.15f + 0.4f * wornWetness, dryAvail);


            // Spec 31A.5A: hot and safe → take something off. If everything
            // warm is also armor under fresh danger, keep sweating.
            var undressAvail = effectiveTemp > 20f && npc.Needs.ThermalDiscomfort >= 0.4f &&
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
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce != null)
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
                // Spec 29H: drinking happens in place from the bottle — no
                // target object, no junction reservation (mirrors Eat).
                if (npc.BottleWater == WaterKind.None)
                {
                    npc.Plan.Status = PlanStatus.Failed;
                    Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Drink but the bottle is empty");
                    continue;
                }

                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.DrinkBottle,
                    Interaction = InteractionType.Drink
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PlanBuilt",
                    $"Goal=Drink Water={npc.BottleWater} Steps=[DrinkBottle]");
                continue;
            }

            if (npc.Mind.CurrentGoal == GoalType.Socialize)
            {
                BuildTalkPlan(world, npc);
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
            // Spec 29E.2/29H: fill boiled over raw whenever actually available.
            var preferBoiled = npc.Mind.CurrentGoal == GoalType.GetWater;

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
            // trunk, not inside it.
            var anchorIsWater = targetJunction is { } wetId &&
                SpatialQueries.IsAllWaterJunction(world, wetId);
            if (targetJunction is { } anchorId &&
                (anchorIsWater ||
                 (world.Junctions.Items.TryGetValue(anchorId, out var anchorJunction) &&
                  anchorJunction.Blocked)))
            {
                // Reserve in-loop: the first free neighbor is the same for
                // every claimant — without reserving here two sleepers fight
                // over one spot forever (ReservationFailed loop).
                JunctionId? beside = null;
                SpatialQueries.CollectStandableAround(world, anchorId, _rimScratch);
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

    // Spec 35.4: a move-only trip to the nearest shade tree or water spot.
    private void BuildCoolOffPlan(WorldState world, NPCState npc)
    {
        PerceivedObject? spot = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                (!definition.Tags.Contains("Shade") && !definition.Tags.Contains("Water")))
            {
                continue;
            }

            if (spot is null || obj.Distance < spot.Distance)
            {
                spot = obj;
            }
        }

        JunctionId? anchor = null;
        if (spot is not null)
        {
            if (world.Entities.Objects.TryGetValue(spot.Id, out var spotObject))
            {
                anchor = spotObject.Junctions.Count > 0 ? spotObject.Junctions[0] : null;
            }
            else if (npc.Memory.KnownObjects.TryGetValue(spot.Id, out var remembered))
            {
                anchor = remembered.Junction;
            }
        }

        if (anchor is not { } target)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.CoolOff);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=CoolOff NoCoolSpot");
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
            SetGoalCooldown(world, npc, GoalType.CoolOff);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=CoolOff NoFreeApproach");
            return;
        }

        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = spot!.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "CoolOffPlanned",
            $"To {spot.DefinitionId} Tile={spot.Tile.Q},{spot.Tile.R}");
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
            GoalType.GetWater => InteractionType.FillBottle,
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
            GoalType.Build => InteractionType.Build,
            GoalType.Mourn => InteractionType.Observe,
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
                return definition.Tags.Contains("Food");
            case GoalType.GatherWood:
                return definition.Tags.Contains("Firewood");
            case GoalType.GatherTools:
                return definition.Tags.Contains("Tool") &&
                    !npc.Inventory.Items.Contains(perceived.DefinitionId);
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
                return definition.Tags.Contains("BigTree") || definition.Tags.Contains("Palm");
            case GoalType.MineBoulder:
                return definition.Tags.Contains("Boulder");
            case GoalType.Build:
                return definition.Tags.Contains("BuildSite");
            case GoalType.BuildRaft:
                return definition.Tags.Contains("Raft");
            case GoalType.Mourn:
                return definition.Tags.Contains("Corpse") || definition.Tags.Contains("Grave");
            case GoalType.Bury:
                return definition.Tags.Contains("Corpse");
            case GoalType.GetWater:
                // Spec 29H: fill at a raw bank, or at a lit campfire with a pot.
                if (definition.Tags.Contains("RawWater"))
                {
                    return true;
                }

                return definition.Tags.Contains("Campfire") &&
                    npc.Inventory.Items.Contains("tool.pot") &&
                    world.Entities.Objects.TryGetValue(perceived.Id, out var campfire) &&
                    campfire.ResourceAmount > 0f;
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

            var path = HexPathfinder.FindPath(world, startJunction.Value, npc.Plan.TargetJunctionId.Value, OtherNpcJunctions(world, npc));
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
                    Trace.Emit(world, npc.Id, "MovementRepath",
                        $"Junction={targetJunctionId.Value} held by a housemate");
                }

                continue;
            }

            npc.Movement.BlockedWaitTicks = 0;

            var target = targetJunction.WorldPosition;
            var delta = new Float2(target.X - npc.Position.X, target.Y - npc.Position.Y);
            var direction = HexSpatialMath.Normalize(delta);
            npc.Movement.DesiredDirection = direction;
            npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(direction);

            var turnPerTick = npc.TurnSpeed * world.TickDeltaTime;
            var prevRotation = npc.RotationDegrees;
            npc.RotationDegrees = MathUtil.RotateTowards(
                npc.RotationDegrees,
                npc.Movement.DesiredRotationDegrees,
                turnPerTick);

            var facingError = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
            const float alignmentThreshold = 30f;

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

            var alignmentFactor = 1f - (facingError / alignmentThreshold) * 0.5f;
            // Spec 19.3C: mauled legs mean hobbling.
            var movementPerTick = npc.MoveSpeed * npc.Body.MobilityFactor() *
                EquipmentMath.WetMovementFactor(npc) *
                alignmentFactor * world.TickDeltaTime;
            var distance = HexSpatialMath.Distance(npc.Position, target);

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
            if (lastStep is { Type: PlanStepType.GroundSit or PlanStepType.GroundSleep })
            {
                RunGroundRestPlan(world, npc, lastStep);
                continue;
            }

            if (npc.Plan.TargetAgentId is not null)
            {
                RunTalk(world, npc);
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
                    var craftOk = npc.Plan.Goal switch
                    {
                        GoalType.CraftSpear => npc.Inventory.Items.Contains("resource.firewood"),
                        GoalType.CookMeat => npc.Inventory.Items.Contains("food.meat_raw") &&
                            worldObject.ResourceAmount > 0f,
                        GoalType.CraftLeather => DecisionSystem.CountInventory(npc, "resource.hide") >= 1,
                        GoalType.CraftAxe => npc.Inventory.Items.Contains("resource.firewood") &&
                            DecisionSystem.CountInventory(npc, "resource.stone") >= 1,
                        GoalType.CraftPickaxe => npc.Inventory.Items.Contains("resource.firewood") &&
                            DecisionSystem.CountInventory(npc, "resource.stone") >= 2,
                        GoalType.CraftRack => DecisionSystem.CountInventory(npc, "resource.firewood") >= 2 &&
                            !DecisionSystem.RackExists(world),
                        GoalType.CraftBed => DecisionSystem.CountInventory(npc, "resource.palm_leaf") >= 3,
                        GoalType.CraftTent => DecisionSystem.CountInventory(npc, "resource.palm_leaf") >= 4,
                        GoalType.CraftBow => DecisionSystem.CountInventory(npc, "resource.firewood") >= 2 &&
                            DecisionSystem.CountInventory(npc, "resource.hide") >= 1,
                        GoalType.CraftArrows => npc.Inventory.Items.Contains("resource.firewood"),
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
                if (interaction.Type == InteractionType.Build)
                {
                    var pendingPiece = DecisionSystem.NextBuildPiece(world);
                    var billOk = pendingPiece is { } bill &&
                        DecisionSystem.CountInventory(npc, "resource.firewood") >= bill.Logs &&
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
                    var hasChopTool = npc.Inventory.Items.Contains("tool.axe_stone") ||
                        npc.Inventory.Items.Contains("tool.saw");
                    var toolOk = isBoulder
                        ? npc.Inventory.Items.Contains("tool.pickaxe_stone")
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

                // Spec 29E.3: fueling needs a carried log; lighting a dead
                // fire additionally needs the lighter.
                if (interaction.Type == InteractionType.Fuel)
                {
                    var hasWoodNow = npc.Inventory.Items.Contains("resource.firewood");
                    var missingLighter = worldObject.ResourceAmount <= 0f &&
                        !npc.Inventory.Items.Contains("tool.lighter");
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
                    Trace.Emit(world, npc.Id, "BottleFilled",
                        $"{npc.BottleWater} from {worldObject.DefinitionId}");
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
                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                    EquipmentMath.Recalculate(world, npc);
                    Trace.Emit(world, npc.Id, "ItemWorn",
                        $"Def={worldObject.DefinitionId} Worn=[{string.Join(",", npc.WornItems)}] " +
                        $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                }
                else if (completedInteraction.Type == InteractionType.Craft)
                {
                    // Spec 29F.3: recipe by goal.
                    switch (npc.Plan.Goal)
                    {
                        case GoalType.CraftSpear:
                            npc.Inventory.Items.Remove("resource.firewood");
                            GiveOrDrop(world, npc, "tool.spear");
                            Trace.Emit(world, npc.Id, "CraftedSpear",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CookMeat:
                            npc.Inventory.Items.Remove("food.meat_raw");
                            GiveOrDrop(world, npc, "food.meat_cooked");
                            Trace.Emit(world, npc.Id, "MeatCooked",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftLeather:
                            npc.Inventory.Items.Remove("resource.hide");
                            ResolveWearConflicts(world, npc, "clothing.leather_pants");
                            npc.WornItems.Add("clothing.leather_pants");
                            EquipmentMath.Recalculate(world, npc);
                            Trace.Emit(world, npc.Id, "CraftedLeather",
                                $"Pants worn. Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
                            break;
                        case GoalType.CraftAxe:
                            npc.Inventory.Items.Remove("resource.firewood");
                            npc.Inventory.Items.Remove("resource.stone");
                            GiveOrDrop(world, npc, "tool.axe_stone");
                            Trace.Emit(world, npc.Id, "CraftedAxe",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftPickaxe:
                            npc.Inventory.Items.Remove("resource.firewood");
                            npc.Inventory.Items.Remove("resource.stone");
                            npc.Inventory.Items.Remove("resource.stone");
                            GiveOrDrop(world, npc, "tool.pickaxe_stone");
                            Trace.Emit(world, npc.Id, "CraftedPickaxe",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftRack:
                            npc.Inventory.Items.Remove("resource.firewood");
                            npc.Inventory.Items.Remove("resource.firewood");
                            PlaceRack(world, npc, worldObject);
                            break;
                        case GoalType.CraftBed:
                        {
                            // Spec 40.14: with 2 logs spare she weaves the solid
                            // bedroll; with only leaves, the cheap tier-1 mat.
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            var bedKind = "bed.leaf";
                            if (DecisionSystem.CountInventory(npc, "resource.firewood") >= 2)
                            {
                                npc.Inventory.Items.Remove("resource.firewood");
                                npc.Inventory.Items.Remove("resource.firewood");
                                bedKind = "bed.basic";
                            }

                            PlaceCraftedFurniture(world, npc, worldObject, bedKind);
                            Trace.Emit(world, npc.Id, "BedCrafted",
                                bedKind == "bed.basic" ? "A bedroll of her own" : "A leaf sleeping-mat");
                            break;
                        }
                        case GoalType.CraftTent:
                            // Spec 40.14: 4 leaves woven into a shade canopy.
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            npc.Inventory.Items.Remove("resource.palm_leaf");
                            PlaceCraftedFurniture(world, npc, worldObject, "shelter.tent");
                            Trace.Emit(world, npc.Id, "TentCrafted", "A leaf sun shelter");
                            break;
                        case GoalType.CraftBow:
                            npc.Inventory.Items.Remove("resource.firewood");
                            npc.Inventory.Items.Remove("resource.firewood");
                            npc.Inventory.Items.Remove("resource.hide");
                            GiveOrDrop(world, npc, "tool.bow");
                            Trace.Emit(world, npc.Id, "CraftedBow",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                        case GoalType.CraftArrows:
                            npc.Inventory.Items.Remove("resource.firewood");
                            GiveOrDrop(world, npc, "resource.arrow");
                            GiveOrDrop(world, npc, "resource.arrow");
                            GiveOrDrop(world, npc, "resource.arrow");
                            Trace.Emit(world, npc.Id, "CraftedArrows",
                                $"Inventory=[{string.Join(",", npc.Inventory.Items)}]");
                            break;
                    }

                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.Build)
                {
                    ApplyBuildPiece(world, npc);
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
                }
                else if (completedInteraction.Type == InteractionType.BuildRaft)
                {
                    // Spec 40.15: every carried log goes into the raft; at the
                    // target the colony can sail off the island.
                    var deposited = DecisionSystem.CountInventory(npc, "resource.firewood");
                    npc.Inventory.Items.RemoveAll(i => i.DefinitionId == "resource.firewood");
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
                    // Spec 35.2: the object is consumed; loot by kind.
                    if (definition.Tags.Contains("Boulder"))
                    {
                        for (var l = 0; l < 4; l++) GiveOrDrop(world, npc, "resource.stone");
                        Trace.Emit(world, npc.Id, "BoulderBroken",
                            $"{worldObject.DefinitionId} at Tile={worldObject.Tile.Q},{worldObject.Tile.R} -> 4 stones");
                    }
                    else if (definition.Tags.Contains("Palm"))
                    {
                        for (var l = 0; l < 2; l++) GiveOrDrop(world, npc, "resource.firewood");
                        for (var l = 0; l < 3; l++) GiveOrDrop(world, npc, "resource.palm_leaf");
                        Trace.Emit(world, npc.Id, "TreeChopped",
                            $"{worldObject.DefinitionId} -> 2 logs + 3 palm leaves");
                    }
                    else
                    {
                        for (var l = 0; l < 4; l++) GiveOrDrop(world, npc, "resource.firewood");
                        Trace.Emit(world, npc.Id, "TreeChopped",
                            $"{worldObject.DefinitionId} -> 4 logs");
                    }

                    WorldObjectMutations.DespawnObject(world, worldObject.Id);
                }
                else if (completedInteraction.Type == InteractionType.Fuel)
                {
                    // Spec 29E.3: one log per fueling, half a day of fire.
                    npc.Inventory.Items.Remove("resource.firewood");
                    var wasLit = worldObject.ResourceAmount > 0f;
                    worldObject.ResourceAmount += 1200f;
                    Trace.Emit(world, npc.Id, wasLit ? "FireFueled" : "FireLit",
                        $"{worldObject.DefinitionId} Fuel={worldObject.ResourceAmount:F0} ticks");
                    worldObject.IsOccupied = false;
                    worldObject.CurrentUser = null;
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

        for (var i = 0; i < bill.Logs; i++) npc.Inventory.Items.Remove("resource.firewood");
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
    private const int TalkDurationTicks = 40;
    private const float TalkInitiatorSocialGain = 0.40f;
    private const float TalkListenerSocialGain = 0.25f;
    private const float TalkRelationshipGain = 0.05f;

    // Spec 28.15B: quarrels and refusal-by-dislike.
    private const float QuarrelInitiatorSocialGain = 0.15f;
    private const float QuarrelListenerSocialGain = 0.10f;
    private const float QuarrelAffinityLoss = 0.12f;
    private const float QuarrelEmbarrassment = 0.30f;
    private const float RefusalAffinityThreshold = -0.25f;
    private const float LonelinessOverrideThreshold = 0.25f;
    private const float RejectionAffinityPenalty = 0.05f;

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
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            Trace.Emit(world, npc.Id, "TalkStarted",
                $"With NPC{targetId.Value} Duration={TalkDurationTicks}ticks " +
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

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;
            // Spec 31C.8: the snapshot must not report a finished interaction —
            // the view would keep the pose while the body walks away.
            npc.Execution.CurrentInteraction = null;

            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.FreeJunction(world, jId, npc.Id);
                SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
            }
            Trace.Emit(world, npc.Id, "RelationshipChanged",
                $"NPC{npc.Id.Value}->NPC{target.Id.Value} " +
                $"Fam={initiatorRel.Familiarity:F2} Aff={initiatorRel.Affinity:F2} (+{TalkRelationshipGain:F2})");
            Trace.Emit(world, target.Id, "RelationshipChanged",
                $"NPC{target.Id.Value}->NPC{npc.Id.Value} " +
                $"Fam={listenerRel.Familiarity:F2} Aff={listenerRel.Affinity:F2} (+{TalkRelationshipGain:F2})");

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
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Socialize);
        PlanInterruption.Abort(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
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
        // Pass 1: a free fireside junction clear of other furniture.
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, anchor))
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

        // Pass 3: fall back to any free fireside junction (heap beats nowhere).
        if (ring is null)
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, anchor))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor))
                {
                    return neighbor;
                }
            }
        }

        return ring;
    }

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
            GiveOrDrop(world, npc, "resource.firewood");
            GiveOrDrop(world, npc, "resource.firewood");
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

    // Spec 35.5: dropped items keep their instance state on the ground.
    internal static void DropItemAtFeet(WorldState world, NPCState npc, ItemInstance item)
    {
        var dropJunction = npc.CurrentJunction;
        if (dropJunction is null &&
            world.Tiles.Items.TryGetValue(npc.Tile, out var tile) && tile.Junctions.Count > 0)
        {
            dropJunction = tile.Junctions[0];
        }

        if (dropJunction is { } junction)
        {
            var dropped = WorldObjectMutations.SpawnObject(
                world, item.DefinitionId, npc.Fragment, npc.Tile, junction);
            dropped.Wetness = item.Wetness;
            dropped.Durability = item.Durability;
        }
    }

    // Spec 31A.5A: take off a worn item in place; it drops to the world at
    // the NPC's feet, retrievable by anyone.
    private const int UndressDurationTicks = 6;

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
                step.TargetJunction is { } lg && PlanningSystem.IsLedgeId(world, lg) ? 0.25f : 0.15f,
                0.05f);
        }
        else
        {
            // Sleep restores as well as a bed (a night is a night) — the
            // bed's edge is comfort, not energy. +0.35 energy here produced
            // a poverty trap: 160 naps/soak and no time to live.
            RunGroundRest(world, npc, step, InteractionType.Sleep, 100, 0f, 0.5f);
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

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, kind == InteractionType.Sleep ? "GroundSleptWell" : "GroundSatDown",
            $"Comfort+{comfort:F2} Energy+{energy:F2}");

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

    // Spec 29G: legs over the edge — face the lowest neighboring tile.
    private static void FaceLowerSide(WorldState world, NPCState npc, Junction ledge)
    {
        Tile lowest = null;
        foreach (var coord in ledge.Tiles)
        {
            if (world.Tiles.Items.TryGetValue(coord, out var tile) &&
                (lowest == null || tile.Elevation < lowest.Elevation))
            {
                lowest = tile;
            }
        }

        if (lowest == null)
        {
            return;
        }

        var center = HexSpatialMath.TileToWorld(lowest.Coord);
        var direction = HexSpatialMath.Normalize(new Float2(
            center.X - npc.Position.X, center.Y - npc.Position.Y));
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(direction);
    }

    private static void RunUndressItem(WorldState world, NPCState npc)
    {
        var itemId = npc.Plan.TargetItemDefinitionId;
        if (itemId is null || !npc.WornItems.Contains(itemId))
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"UndressItem: '{itemId ?? "-"}' is not worn");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Undress -> {itemId} Duration={UndressDurationTicks}ticks");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress)
        {
            if (npc.Execution.EndTick - world.Tick > 0)
            {
                return;
            }

            var wornItem = npc.WornItems.Find(i => i.DefinitionId == itemId) ??
                new ItemInstance(itemId);
            npc.WornItems.Remove(wornItem);
            EquipmentMath.Recalculate(world, npc);
            DropItemAtFeet(world, npc, wornItem);

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

        var interaction = ResolveInteraction(itemDefinition, InteractionType.Eat);
        if (interaction is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "ExecFailed",
                $"ConsumeInventoryItem: '{itemId}' has no Eat interaction");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Eat;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + interaction.DurationTicks;

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"Eat (inventory) -> {itemId} " +
                $"Duration={interaction.DurationTicks}ticks ({interaction.DurationTicks * world.TickDeltaTime:F1}s) " +
                $"EndTick={npc.Execution.EndTick} " +
                $"Effects=[H={interaction.Effects.HungerDelta:+0.00;-0.00} " +
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
                    $"Eat (inventory) Progress={progress:P0} " +
                    $"Remaining={remaining}ticks ({remaining * world.TickDeltaTime:F1}s)");
                return;
            }

            var needsBefore = Trace.FormatNeeds(npc.Needs);
            ApplyEffectsScaled(npc, interaction.Effects, total > 0 ? 1f / total : 1f);
            npc.Inventory.Items.Remove(itemId);
            var needsAfter = Trace.FormatNeeds(npc.Needs);

            npc.Execution.Status = ExecutionStatus.Completed;
            npc.Execution.LastCompletedTick = world.Tick;

            Trace.Emit(world, npc.Id, "ItemConsumed",
                $"{itemId} eaten from inventory " +
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
    private const int DrinkBottleDurationTicks = 16;

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
        var thirstTotal = boiled ? 0.85f : 0.7f;
        var comfortTotal = boiled ? 0.05f : 0f;
        var share = 1f / DrinkBottleDurationTicks;
        npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst - thirstTotal * share);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfortTotal * share);

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Spec 29H: raw water is a gamble — 30 % sickness (moved here from
        // the old water-edge Drink now that filling and drinking are split).
        if (!boiled)
        {
            var sickRoll = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 833);
            if (sickRoll < 0.30f)
            {
                npc.Body.Parts[BodyPart.Torso] =
                    System.Math.Max(0f, npc.Body.Parts[BodyPart.Torso] - 0.15f);
                npc.Health = npc.Body.Mean();
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.2f);
                if (npc.Body.VitalDestroyed(out var sickVital))
                {
                    npc.Health = 0f;
                    Trace.Emit(world, npc.Id, "VitalPartDestroyed",
                        $"{sickVital} destroyed by sickness");
                }

                Trace.Emit(world, npc.Id, "GotSick",
                    $"Raw water (Roll={sickRoll:F2}) Torso={npc.Body.Parts[BodyPart.Torso]:F2}");
            }
        }

        Trace.Emit(world, npc.Id, "DrankBottle",
            $"{(boiled ? "Boiled" : "Raw")} water Thirst={npc.Needs.Thirst:F2}");
        npc.BottleWater = WaterKind.None;

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
        foreach (var step in plan.Steps)
        {
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
    private const float BaseTemperature = 12f;
    private const float TemperatureAmplitude = 6f;

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
            world.Environment.GlobalTemperature -= 3f;
        }

        if (world.Environment.Phase != previousPhase)
        {
            Trace.EmitSystem(world, "PhaseChanged",
                $"{previousPhase}->{world.Environment.Phase} " +
                $"Clock={FormatClock(progress)} Temp={world.Environment.GlobalTemperature:F1}");
        }
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

    private const float HungerRate = 0.02f;
    private const float EnergyRate = 0.015f;
    private const float ComfortRate = 0.01f;
    private const float SocialRate = 0.008f; // spec 28.15A
    private const float ThirstRate = 0.025f; // spec 29E.1

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
            var metabolism = npc.Execution.CurrentInteraction == InteractionType.Sleep ? 0.4f : 1f;
            npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger + HungerRate * metabolism);
            npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst + ThirstRate * metabolism);
            npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy - EnergyRate);
            npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - ComfortRate);
            npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social - SocialRate);

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
            var staminaDelta = resting ? 0.06f : working ? -0.05f : 0.015f;
            npc.Needs.Stamina = MathUtil.Clamp(
                npc.Needs.Stamina + staminaDelta, 0f, staminaCeiling);

            // Spec 40.13: stress rises with danger/combat/pain/starvation and
            // ebbs in calm. A UI param, and a third path to collapse.
            var stressUp = npc.IsFighting || npc.Memory.Dangers.Count > 0 ||
                npc.Health < 0.6f || npc.Needs.Hunger >= 0.85f || npc.Needs.Thirst >= 0.85f;
            npc.Needs.Stress = MathUtil.Clamp01(npc.Needs.Stress + (stressUp ? 0.05f : -0.03f));

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
            npc.Needs.Hygiene = MathUtil.Clamp01(
                npc.Needs.Hygiene + (IsAtOrBesideWater(world, npc.Tile) ? 0.05f : -0.004f));

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

            if (worstPart < 0.4f)
            {
                // Spec 40.3: a bandage in the pack dresses the worst wound —
                // patch it up, stem the blood, and it's consumed. First aid
                // that turns a mauling from fatal into survivable.
                // Last resort: only when actually bleeding out (blood < 0.35),
                // so it saves a life without re-shuffling the colony over
                // every scratch (every mauling survivor would otherwise shift
                // the deterministic dog-dance and tip fragile seeds).
                if (npc.Needs.Bandages > 0 && npc.Needs.Blood < 0.35f)
                {
                    npc.Needs.Bandages--;
                    foreach (var part in AllBodyParts)
                    {
                        if (npc.Body.Parts[part] < 0.4f)
                        {
                            npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + 0.25f);
                        }
                    }

                    npc.Health = npc.Body.Mean();
                    npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + 0.4f);
                    Trace.Emit(world, npc.Id, "Bandaged",
                        $"Dressed the wounds (Health={npc.Health:F2})");
                }
                else
                {
                    npc.Needs.Blood = System.Math.Max(0f, npc.Needs.Blood - (0.4f - worstPart) * 0.06f);
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
            else if (npc.Needs.Blood < 1f && npc.Needs.Hunger < 0.6f)
            {
                npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + 0.02f);
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

            // Spec 29C.2: starvation / dehydration cost HP. Without this an
            // NPC whose needs maxed out (food/water unreachable) hangs forever
            // — Health never falls, it never dies, its slot never frees. The
            // 0.95 gate sits above the 0.85 starving appraisal, so healthy
            // colonies that briefly spike lose nothing; only a truly stuck
            // agent drains to death.
            var starved = npc.Needs.Hunger >= 0.95f;
            var parched = npc.Needs.Thirst >= 0.95f;
            if (starved || parched)
            {
                var damage = starved && parched ? 0.05f : 0.03f;
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
            // Spec 29C.2/19.3C: eat and rest to heal — part by part.
            else if (npc.Health < 1f && npc.Needs.Hunger < 0.5f)
            {
                foreach (var part in AllBodyParts)
                {
                    npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + 0.02f);
                }

                npc.Health = npc.Body.Mean();
            }

            Trace.Emit(world, npc.Id, "NeedsDecay",
                $"Hunger={prevHunger:F3}->{npc.Needs.Hunger:F3}(+{HungerRate}) " +
                $"Energy={prevEnergy:F3}->{npc.Needs.Energy:F3}(-{EnergyRate}) " +
                $"Comfort={prevComfort:F3}->{npc.Needs.Comfort:F3}(-{ComfortRate}) " +
                $"Social={prevSocial:F3}->{npc.Needs.Social:F3}(-{SocialRate})");
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
            var indoorBonus = isIndoor ? 4f : 0f;
            var coolBonus = (isShaded ? -2f : 0f) + (isInWater ? -3f : 0f);
            // Spec 29C.10: a lit campfire warms the tiles around it (the
            // colder it is, the more worth huddling by the fire), but only
            // chases away COLD — it never overheats a warm body.
            var fireWarmth = NearbyFireWarmth(world, npc.Tile, out var onFire);
            var baseTemp = world.Environment.GlobalTemperature + npc.EquippedWarmth * 10f +
                indoorBonus + coolBonus;
            // The accumulating NEED (decisions score this) uses the base temp,
            // NOT the fire warmth — a survival-relevant reshuffle here (the fire
            // making everyone comfortable, so nobody dresses/cools) tipped
            // dog-fragile seeds into wipes. The fire's warmth is a DISPLAY-only
            // comfort for iteration 31; making it a real thermal source waits
            // for the campfire-as-obstacle pass so it can't reposition fatally.
            float pressure;
            if (baseTemp < 12f)
            {
                pressure = System.Math.Min(0.09f, (12f - baseTemp) * 0.02f); // cold
            }
            else if (baseTemp > 20f)
            {
                pressure = System.Math.Min(0.09f, (baseTemp - 20f) * 0.02f); // overheating
            }
            else
            {
                pressure = -0.03f; // comfortable band
            }

            npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + pressure);

            // Signed comfort for the UI DOES fold in the fire's warmth (clamped
            // so it only removes cold): the player sees the fire pull the dial
            // toward "ideal" on a cold night, even though the sim's decisions
            // stay on the base temperature this iteration.
            var effectiveTemp = baseTemp + fireWarmth;
            if (baseTemp <= 20f && effectiveTemp > 20f)
            {
                effectiveTemp = 20f;
            }

            // Spec 29C.10: signed comfort for the UI — 0 in the ideal [12,20]
            // band, scaling to -1 (freezing) / +1 (boiling) over a ~15 span.
            float signed;
            if (effectiveTemp < 12f)
            {
                signed = System.Math.Max(-1f, (effectiveTemp - 12f) / 15f);
            }
            else if (effectiveTemp > 20f)
            {
                signed = System.Math.Min(1f, (effectiveTemp - 20f) / 15f);
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

            if (magnitude >= 0.85f && !isInWater)
            {
                foreach (var part in AllTemperatureParts)
                {
                    npc.Body.Parts[part] = System.Math.Max(0f, npc.Body.Parts[part] - 0.02f);
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
                // survivor). More exposed skin, stronger sun → faster. The
                // burn→tan colour is painted from this in presentation.
                npc.Needs.TanLevel = MathUtil.Clamp01(
                    npc.Needs.TanLevel + (effectiveUv - 0.5f) * 0.0015f * uncovered.Count);
                npc.SunExposure += (effectiveUv - 0.5f) * 0.3f;
                if (npc.SunExposure > 0.5f)
                {
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.02f);
                }

                if (npc.SunExposure >= 1f)
                {
                    var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 2203) * uncovered.Count);
                    pick = System.Math.Min(pick, uncovered.Count - 1);
                    var burntPart = uncovered[pick];
                    npc.Body.Parts[burntPart] = System.Math.Max(0f, npc.Body.Parts[burntPart] - 0.08f);
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
        }
    }

    private static readonly BodyPart[] AllTemperatureParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    // Spec 29C.10: warmth radiated by nearby LIT campfires. On the fire's own
    // tile it is agony (onFire = true); a tile or two away it gently warms.
    private static float NearbyFireWarmth(WorldState world, TileCoord tile, out bool onFire)
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
                warmth = System.Math.Max(warmth, dist == 1 ? 8f : 4f);
            }
        }

        return warmth;
    }

    // Spec 35.4: within 1 tile of a Shade-tagged object (big tree / palm).
    internal static bool IsShaded(WorldState world, TileCoord tile)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains("Shade") &&
                HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
            {
                return true;
            }
        }

        return false;
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
            if (candidate.DefinitionId == "food.coconut" &&
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

            obj.ResourceAmount = System.Math.Max(0f, obj.ResourceAmount - BurnPerSlowTick);
            if (obj.ResourceAmount <= 0f)
            {
                Trace.EmitSystem(world, "FireOut",
                    $"{obj.DefinitionId} at Tile={obj.Tile.Q},{obj.Tile.R} burned out");
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

    private const int MaxDogs = 2;
    private const int RespawnCheckTicks = 7200; // every 3 game days
    private const int SpawnMinDistanceFromNpc = 5;
    private const float RoamChance = 0.2f;
    private const int AggroRadiusTiles = 2;
    private const float BiteDamagePerPass = 0.2f; // to the bitten part (spec 19.3C)
    private const float NpcStrikePerPass = 0.15f;

    private int _nextSpawnCheckTick;

    private readonly System.Collections.Generic.List<Wildlife.DogState> _deadDogs = new();
    private readonly System.Collections.Generic.List<EntityId> _deadNpcs = new();
    private readonly System.Collections.Generic.List<JunctionId> _spawnCandidates = new();

    public void Run(WorldState world)
    {
        if (world.Tick >= _nextSpawnCheckTick)
        {
            _nextSpawnCheckTick = world.Tick + RespawnCheckTicks;
            while (world.Dogs.Count < MaxDogs)
            {
                if (!TrySpawnDog(world))
                {
                    break;
                }
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
            target.IsFighting = true;
            if (target.Plan.Status == PlanStatus.Active ||
                target.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, target, $"Attacked by dog {dog.Id}");
                target.Mind.CurrentGoal = GoalType.None;
            }

            // Spec 19.3C: hurt arms strike weaker.
            dog.Health -= NpcStrikePerPass * target.Body.StrikeFactor();
            Trace.Emit(world, target.Id, "DogFight",
                $"Dog={dog.Id} bit: {bitPart} -{damage:F3} (PartArmor={partArmor:F2}) " +
                $"Part={target.Body.Parts[bitPart]:F2} NpcHealth={target.Health:F2} " +
                $"Strike={NpcStrikePerPass * target.Body.StrikeFactor():F3} " +
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

    private static float WorstPartHealth(NPCState npc)
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

    // Spec 29C.4A: record the attack site (deduped by tile, capped).
    private static void RememberDanger(WorldState world, NPCState npc)
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
    private static bool TryStartFlee(WorldState world, NPCState npc, int attackers)
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
    private static void RemoveDeadNpc(WorldState world, EntityId deadId)
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

        // Spec 31A.5A: everything worn drops at the death site.
        var dropJunction = npc.CurrentJunction;
        if (dropJunction is null &&
            world.Tiles.Items.TryGetValue(npc.Tile, out var deathTile) &&
            deathTile.Junctions.Count > 0)
        {
            dropJunction = deathTile.Junctions[0];
        }

        if (dropJunction is { } junction)
        {
            foreach (var item in npc.WornItems)
            {
                var droppedWorn = WorldObjectMutations.SpawnObject(
                    world, item.DefinitionId, npc.Fragment, npc.Tile, junction);
                droppedWorn.Wetness = item.Wetness;
                droppedWorn.Durability = item.Durability;
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

                var droppedCarried = WorldObjectMutations.SpawnObject(
                    world, item.DefinitionId, npc.Fragment, npc.Tile, junction);
                droppedCarried.Wetness = item.Wetness;
                droppedCarried.Durability = item.Durability;
            }
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
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Corpse"))
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

// Spec 31A.5A/31A.5B: equipment values derive from the worn list —
// warmth stacks across layers (sum), armor is per covered part (max).
internal static class EquipmentMath
{
    public static void Recalculate(WorldState world, NPCState npc)
    {
        var warmth = 0f;
        var armor = 0f;
        foreach (var item in npc.WornItems)
        {
            var (itemWarmth, itemArmor) = ItemValues(world, item.DefinitionId);
            // Spec 35.5: a soaked garment insulates nothing.
            if (item.Wetness <= 0.5f)
            {
                warmth += itemWarmth;
            }

            armor = System.Math.Max(armor, itemArmor);
        }

        npc.EquippedWarmth = MathUtil.Clamp01(warmth);
        npc.EquippedArmor = armor;
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
    }

    // Spec 35.5: soggy clothes drag — x0.9 per wet worn item, floor x0.8.
    public static float WetMovementFactor(NPCState npc)
    {
        var factor = 1f;
        foreach (var item in npc.WornItems)
        {
            if (item.Wetness > 0.5f)
            {
                factor *= 0.9f;
            }
        }

        return System.MathF.Max(0.8f, factor);
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

    private int _nextSpawnCheckTick;
    private readonly System.Collections.Generic.List<Wildlife.RabbitState> _deadRabbits = new();
    private readonly System.Collections.Generic.List<JunctionId> _spawnCandidates = new();

    public void Run(WorldState world)
    {
        if (world.Tick >= _nextSpawnCheckTick)
        {
            _nextSpawnCheckTick = world.Tick + RespawnCheckTicks;
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
                ExecutionSystem.GiveOrDrop(world, npc, "food.meat_raw");
                ExecutionSystem.GiveOrDrop(world, npc, "resource.hide");
                if (MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 211 + npc.Id.Value, 807) < 0.4f)
                {
                    ExecutionSystem.GiveOrDrop(world, npc, "resource.arrow");
                    Trace.Emit(world, npc.Id, "ArrowRecovered", $"From rabbit {rabbit.Id}");
                }

                _deadRabbits.Add(rabbit);
                Trace.Emit(world, npc.Id, "CrabKilled",
                    $"Crab={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R} " +
                    $"(bow, Roll={hitRoll:F2}) loot: meat+hide");
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
                ExecutionSystem.GiveOrDrop(world, npc, "food.meat_raw");
                ExecutionSystem.GiveOrDrop(world, npc, "resource.hide");
                _deadRabbits.Add(rabbit);
                Trace.Emit(world, npc.Id, "CrabKilled",
                    $"Crab={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R} " +
                    $"(Roll={roll:F2}) loot: meat+hide");
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
    }
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

            // Spec 35.6: worn cloth wears 0.01 per game-day (150 slow ticks).
            _wornOutScratch.Clear();
            foreach (var item in npc.WornItems)
            {
                item.Durability -= 0.01f / 150f;
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
    public static bool ReachableBeside(WorldState world, JunctionId from, JunctionId anchor)
    {
        var anchorBlocked = !world.Junctions.Items.TryGetValue(anchor, out var junction) ||
            junction.Blocked || SpatialQueries.IsAllWaterJunction(world, anchor);
        if (!anchorBlocked)
        {
            return Reachable(world, from, anchor);
        }

        SpatialQueries.CollectStandableAround(world, anchor, _besideScratch);
        foreach (var rim in _besideScratch)
        {
            if (Reachable(world, from, rim))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _besideScratch = new();

    public static bool Reachable(WorldState world, JunctionId a, JunctionId b)
    {
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
