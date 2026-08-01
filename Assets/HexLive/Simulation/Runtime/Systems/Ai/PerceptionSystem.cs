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

public sealed class PerceptionSystem : ISimulationSystem
{
    public string Name => nameof(PerceptionSystem);

    public TickLayer Layer => TickLayer.Medium;

    // Spec 22.7 / 27.18A: live sight radius and memory TTL for discoveries.
    private static int PerceptionRadiusTiles => AiBalance.PerceptionRadiusTiles;
    private static int MemoryTtlTicks => AiBalance.MemoryTtlTicks;

    private readonly System.Collections.Generic.List<ObjectId> _forgottenScratch = new();

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Perception.Objects.Clear();
            npc.Perception.Agents.Clear();
            npc.Perception.Hostiles.Clear(); // §72
            npc.Perception.Self.Hunger = npc.Needs.Hunger;
            npc.Perception.Self.Energy = npc.Needs.Energy;
            npc.Perception.Self.Comfort = npc.Needs.Comfort;
            npc.Perception.Self.Social = npc.Needs.Social;
            npc.Perception.Self.ThermalDiscomfort = npc.Needs.ThermalDiscomfort;
            npc.Perception.Self.Tile = npc.Tile;
            npc.Perception.Self.Fragment = npc.Fragment;
            npc.Perception.Environment.Temperature = world.Environment.GlobalTemperature;
            // §72: "company" means ALLIES. An outsider lurking on the island is
            // not someone she is less lonely for — and with the pre-§72 global
            // count he silently made all four girls feel less alone.
            var allies = 0;
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id != npc.Id && FactionRelations.AreAllies(npc, other))
                {
                    allies++;
                }
            }

            npc.Perception.Environment.NearbyAgentsCount = allies;
            npc.Perception.Environment.IsCrowded = allies > 1;
            npc.Perception.Environment.IsPrivate = allies <= 0;
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
                // §72: which pile this one goes on. Hostiles skip the whole §53
                // suffering assessment below — nobody reads another faction's
                // plight, and it is ~30 lines of arithmetic per pair per tick.
                var isAlly = FactionRelations.AreAllies(npc, other);

                var relationship = npc.Social.GetOrCreate(other.Id);

                var perceivedAgent = new PerceivedAgent
                {
                    Faction = other.Faction,
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
                    IsMoving = other.Movement.IsMoving,
                    IsUnconscious = other.IsUnconscious(world.Tick) // §60: no chatting with a body
                };
                perceivedAgent.Relationship.Trust = relationship.Trust;
                perceivedAgent.Relationship.Affinity = relationship.Affinity;

                // Spec §53: read how badly this neighbour needs help and the
                // single most-urgent HELPABLE kind, so the Aid goal can bid on
                // and route to the worst-off without re-scanning full state.
                // Severity is 0..1; a bleed-out clock outranks mere hunger.
                var aidKind = AidKind.None;
                var suffering = 0f;
                if (isAlly && other.Health > 0f)
                {
                    // Treat — open wounds / blood loss (a bleed-out is on a clock).
                    var treatSev = other.Wounds.Count > 0 || other.Needs.Blood < 0.6f
                        ? System.Math.Max(1f - other.Needs.Blood, 1f - other.Health)
                        : 0f;
                    // Medicate — actively sick, or gravely weak with nothing to dress.
                    var medSev = other.Mind.SickUntilTick > world.Tick
                        ? 0.6f
                        : (other.Health < 0.4f && other.Wounds.Count == 0 ? 1f - other.Health : 0f);
                    // Hydrate — parched (thirst kills faster than hunger, so it
                    // is checked before Feed and wins ties). Without this a
                    // dehydrating housemate registered NO helpable suffering
                    // and got fed while dying of thirst (seed 1104049673).
                    var hydrateSev = other.Needs.Thirst >= 0.55f ? other.Needs.Thirst : 0f;
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
                    if (hydrateSev > suffering) { suffering = hydrateSev; aidKind = AidKind.Hydrate; }
                    if (feedSev > suffering) { suffering = feedSev; aidKind = AidKind.Feed; }
                    if (consoleSev > suffering) { suffering = consoleSev; aidKind = AidKind.Console; }
                    if (suffering <= 0f) { aidKind = AidKind.None; }
                }
                perceivedAgent.Suffering = suffering;
                perceivedAgent.AidKind = aidKind;

                if (isAlly)
                {
                    npc.Perception.Agents.Add(perceivedAgent);
                }
                else
                {
                    npc.Perception.Hostiles.Add(perceivedAgent);
                }
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

}
