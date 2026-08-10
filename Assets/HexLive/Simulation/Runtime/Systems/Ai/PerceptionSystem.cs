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

    // Живой скан идёт по гекс-кольцу через ObjectsByTile, а не по всем
    // объектам острова: кольцо радиуса r — это 1+3r(r+1) тайлов (19 при r=2)
    // против ~2 300 проверок дистанции на карте 16x. Порядок обхода фиксируем
    // сортировкой по id: словарный порядок Entities.Objects зависит от
    // переиспользования слотов после despawn и не воспроизводим по смыслу.
    private readonly System.Collections.Generic.List<ObjectId> _visibleScratch = new();

    // §22.7 NPC×NPC: оценка страдания соседки — чистая функция ЕЁ состояния
    // (AidAssessment.Assess не читает CurrentJunction и ничего не пишет),
    // поэтому считается один раз за прогон на агента (O(N)), а не в каждой
    // паре наблюдатель×наблюдаемая (O(N^2)). Словарь переиспользуется.
    private readonly System.Collections.Generic.Dictionary<EntityId, (AidKind Kind, float Suffering)>
        _aidScratch = new();

    public void Run(WorldState world)
    {
        _aidScratch.Clear();
        foreach (var someone in world.Entities.Npcs.Values)
        {
            var kind = AidAssessment.Assess(someone, world.Tick, out var suffering);
            _aidScratch[someone.Id] = (kind, suffering);
        }

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
            _visibleScratch.Clear();
            var radius = PerceptionRadiusTiles;
            for (var dq = -radius; dq <= radius; dq++)
            {
                var lo = System.Math.Max(-radius, -dq - radius);
                var hi = System.Math.Min(radius, -dq + radius);
                for (var dr = lo; dr <= hi; dr++)
                {
                    var coord = new Common.TileCoord(npc.Tile.Q + dq, npc.Tile.R + dr);
                    if (world.Caches.ObjectsByTile.TryGetValue(coord, out var onTile))
                    {
                        _visibleScratch.AddRange(onTile);
                    }
                }
            }

            _visibleScratch.Sort(static (a, b) => a.Value.CompareTo(b.Value));

            foreach (var objId in _visibleScratch)
            {
                if (!world.Entities.Objects.TryGetValue(objId, out var obj))
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
                    Connectivity.ReachableBeside(world, npcJunction.Value, objJunction.Value,
                        npc.Body.CanJump, obj);

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
                    npc.Memory.Version++;
                    Trace.Emit(world, npc.Id, "MemoryAdded",
                        $"Obj={obj.Id.Value} Def={obj.DefinitionId} Tile={obj.Tile.Q},{obj.Tile.R}");
                }

                record.DefinitionId = obj.DefinitionId;
                record.Tile = obj.Tile;
                record.Junction = objJunction;
                record.LastSeenTick = world.Tick;
            }

            // Memory maintenance (spec 27.18A): negative evidence inside the
            // sight radius and the discovery TTL run every medium tick — they
            // are cheap integer checks and must fire even standing still.
            _forgottenScratch.Clear();
            foreach (var record in npc.Memory.KnownObjects.Values)
            {
                var withinSight = HexSpatialMath.HexDistance(npc.Tile, record.Tile) <= PerceptionRadiusTiles;

                if (withinSight && !world.Entities.Objects.ContainsKey(record.Id))
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
                }
            }

            foreach (var forgottenId in _forgottenScratch)
            {
                npc.Memory.KnownObjects.Remove(forgottenId);
                npc.Memory.Version++;
            }

            // §22.7: remembered-but-unseen objects join the perceived list
            // flagged FromMemory — through a cached view. ReachableBeside is a
            // component-id comparison, so for a fixed topology its answers can
            // only change together with the keys below; everything else in a
            // FromMemory entry is a pure function of the memory record. Only
            // Distance moves continuously — it is refreshed live further down.
            var canJump = npc.Body.CanJump;
            var component = npcJunction.HasValue
                ? Connectivity.ComponentOf(world, npcJunction.Value, canJump)
                : int.MinValue;
            var view = npc.Perception;
            if (!view.MemoryViewBuilt ||
                !view.MemoryViewTile.Equals(npc.Tile) ||
                view.MemoryViewComponent != component ||
                view.MemoryViewTopology != world.TopologyVersion ||
                view.MemoryViewMemoryVersion != npc.Memory.Version ||
                view.MemoryViewCanJump != canJump)
            {
                view.MemoryView.Clear();
                foreach (var record in npc.Memory.KnownObjects.Values)
                {
                    if (HexSpatialMath.HexDistance(npc.Tile, record.Tile) <= PerceptionRadiusTiles)
                    {
                        continue; // live entry already covers it
                    }

                    if (!world.Content.ObjectDefinitions.TryGetValue(record.DefinitionId, out var definition))
                    {
                        continue;
                    }

                    // §26.6A r5: the remembered object usually still exists —
                    // hand it over so its own footprint stays crossable. Gone
                    // means its blocked junctions are gone too, so null is the
                    // right answer.
                    world.Entities.Objects.TryGetValue(record.Id, out var liveRemembered);
                    var isReachable = npcJunction.HasValue && record.Junction.HasValue &&
                        Connectivity.ReachableBeside(world, npcJunction.Value, record.Junction.Value,
                            canJump, liveRemembered);

                    var remembered = new PerceivedObject
                    {
                        Id = record.Id,
                        DefinitionId = record.DefinitionId,
                        FromMemory = true,
                        Tile = record.Tile,
                        IsReachable = isReachable,
                        IsOccupied = false, // assumed free until seen (spec 27.18A)
                        OccupiedBy = null
                    };

                    foreach (var interaction in definition.Interactions)
                    {
                        remembered.AvailableInteractions.Add(interaction.Type);
                    }

                    view.MemoryView.Add(remembered);
                }

                view.MemoryViewBuilt = true;
                view.MemoryViewTile = npc.Tile;
                view.MemoryViewComponent = component;
                view.MemoryViewTopology = world.TopologyVersion;
                view.MemoryViewMemoryVersion = npc.Memory.Version;
                view.MemoryViewCanJump = canJump;
            }

            foreach (var remembered in view.MemoryView)
            {
                remembered.Distance = HexSpatialMath.Distance(
                    npc.Position, HexSpatialMath.TileToWorld(remembered.Tile));
                npc.Perception.Objects.Add(remembered);
            }

            // Spec 28.3: perceived agents with reachability and relationship summary.
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id == npc.Id)
                {
                    continue;
                }

                var agentDistance = HexSpatialMath.Distance(npc.Position, other.Position);
                // Джанкшен соседки читается В ПАРЕ, не из сводки: ResolveCurrentJunction
                // может пере-якорить её посреди этого же прогона, и наблюдатели до/после
                // обязаны видеть разное — как и до оптимизации.
                var otherJunction = other.CurrentJunction;
                // Reachable(a,b) — это сравнение компонент; компонента наблюдателя
                // (component) уже посчитана для ключа кэша памяти выше. Семантика
                // старого вызова сохранена точно: недостижимо, если любой из узлов
                // выпал из словаря компонент или заблокирован (comp < 0).
                var agentReachable = npcJunction.HasValue && otherJunction.HasValue &&
                    (npcJunction.Value.Equals(otherJunction.Value) ||
                     (component >= 0 &&
                      component == Connectivity.ComponentOf(world, otherJunction.Value, canJump)));
                // §72: which pile this one goes on. Hostiles skip the whole §53
                // suffering assessment — nobody reads another faction's plight.
                var isAlly = FactionRelations.AreAllies(npc, other);

                var relationship = npc.Social.GetOrCreate(other.Id);

                if (!view.AgentPool.TryGetValue(other.Id, out var perceivedAgent))
                {
                    perceivedAgent = new PerceivedAgent();
                    view.AgentPool[other.Id] = perceivedAgent;
                }

                // Пул: запись переживает тик, поэтому переустанавливается КАЖДОЕ
                // поле — оставленное «как было» поле читалось бы как прошлотиковое.
                perceivedAgent.Faction = other.Faction;
                perceivedAgent.Id = other.Id;
                perceivedAgent.Tile = other.Tile;
                perceivedAgent.Distance = agentDistance;
                perceivedAgent.CanSee = true;
                perceivedAgent.CanHear = true;
                perceivedAgent.Junction = otherJunction;
                perceivedAgent.IsReachable = agentReachable;
                perceivedAgent.IsBusy = other.IsFighting ||
                    (other.Execution.Status == ExecutionStatus.InProgress &&
                     other.Execution.CurrentInteraction != InteractionType.Talk);
                perceivedAgent.IsMoving = other.Movement.IsMoving;
                perceivedAgent.IsUnconscious = other.IsUnconscious(world.Tick); // §60: no chatting with a body
                perceivedAgent.Relationship.Trust = relationship.Trust;
                perceivedAgent.Relationship.Affinity = relationship.Affinity;

                // Spec §53: read how badly this neighbour needs help and the
                // single most-urgent HELPABLE kind, so the Aid goal can bid on
                // and route to the worst-off without re-scanning full state.
                // §53.3/§105: сама формула живёт в AidAssessment — та же, по
                // которой помощница переоценивает подопечную по прибытии.
                // Значение снято один раз за прогон (_aidScratch, O(N)).
                var (aidKind, suffering) = _aidScratch[other.Id];
                perceivedAgent.Suffering = isAlly ? suffering : 0f;
                perceivedAgent.AidKind = isAlly ? aidKind : AidKind.None;
                perceivedAgent.IsDying = other.IsDying; // §105

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
