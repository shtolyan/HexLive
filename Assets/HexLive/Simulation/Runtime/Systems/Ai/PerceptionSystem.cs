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

    // §125.2: кандидаты сенсора — кто попал в кольцо восприятия. Порядок
    // фиксируется сортировкой по id: списки EntitiesByTile отражают порядок
    // прихода на тайл и упорядоченными не являются.
    private readonly System.Collections.Generic.List<EntityId> _agentScratch = new();

    // §125.3: кого забыть после прохода — правка словаря во время его же
    // обхода бросает.
    private readonly System.Collections.Generic.List<EntityId> _forgottenAgentScratch = new();

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
            npc.Perception.Remembered.Clear(); // §125.7
            npc.Perception.Self.Hunger = npc.Needs.Hunger;
            npc.Perception.Self.Energy = npc.Needs.Energy;
            npc.Perception.Self.Comfort = npc.Needs.Comfort;
            npc.Perception.Self.Social = npc.Needs.Social;
            npc.Perception.Self.ThermalDiscomfort = npc.Needs.ThermalDiscomfort;
            npc.Perception.Self.Tile = npc.Tile;
            npc.Perception.Self.Fragment = npc.Fragment;
            npc.Perception.Environment.Temperature = world.Environment.GlobalTemperature;
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
                    if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "MemoryAdded",
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
                    if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "MemoryForgotten",
                        $"Obj={record.Id.Value} Def={record.DefinitionId} Gone (negative evidence)");
                    continue;
                }

                if (!record.IsPermanent && world.Tick - record.LastSeenTick > MemoryTtlTicks)
                {
                    _forgottenScratch.Add(record.Id);
                    if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "MemoryForgotten",
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

            // §125.2 СЕНСОР: люди попадают в восприятие только из кольца
            // радиуса RadiusTiles(npc). Кольцо (1+3r(r+1) тайлов) читается из
            // EntitiesByTile; когда оно шире, чем весь остров людей, дешевле
            // прежний полный перебор с фильтром дистанции — это ветка ПЕРФА,
            // обязанная давать тот же список (обе сортируются по id).
            var agentRadius = PerceptionMath.RadiusTiles(npc);
            CollectAgentsInRadius(world, npc, agentRadius, _agentScratch);

            var allies = 0;
            foreach (var otherId in _agentScratch)
            {
                if (!world.Entities.Npcs.TryGetValue(otherId, out var other))
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
                    allies++;
                }
                else
                {
                    npc.Perception.Hostiles.Add(perceivedAgent);
                }

                // §125.3: живой взгляд обновляет память встречи.
                if (!npc.Memory.KnownAgents.TryGetValue(other.Id, out var met))
                {
                    met = new Memory.AgentMemory { Id = other.Id };
                    npc.Memory.KnownAgents[other.Id] = met;
                    if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "AgentMemoryAdded",
                        $"NPC{other.Id.Value} Tile={other.Tile.Q},{other.Tile.R}");
                }

                met.Faction = other.Faction;
                met.Tile = other.Tile;
                met.Junction = otherJunction;
                met.LastSeenTick = world.Tick;
                // §125.7: запоминается ВЕРДИКТ (чем помочь и насколько плохо),
                // а не улики — он уже посчитан выше на этот тик и стоит одного
                // присваивания. Чужую беду не запоминаем, как и не воспринимаем:
                // зеркало фракционного гейта строкой ниже.
                met.Suffering = isAlly ? suffering : 0f;
                met.AidKind = isAlly ? aidKind : AidKind.None;
                met.Helpless = other.IsDying || other.IsUnconscious(world.Tick);
            }

            UpdateAgentMemory(world, npc);
            BuildRememberedAgents(world, npc);

            // §72 «компания» = СОЮЗНИЦЫ, и с §125 — только те, кого она видит:
            // подруга на другом конце острова больше не согревает.
            npc.Perception.Environment.NearbyAgentsCount = allies;
            npc.Perception.Environment.IsCrowded = allies > 1;
            npc.Perception.Environment.IsPrivate = allies <= 0;

            var reachableCount = 0;
            var occupiedCount = 0;
            foreach (var obj in npc.Perception.Objects)
            {
                if (obj.IsReachable) reachableCount++;
                if (obj.IsOccupied) occupiedCount++;
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PerceptionUpdated",
                $"Objects={npc.Perception.Objects.Count} Reachable={reachableCount} Occupied={occupiedCount} " +
                $"Needs=[{Trace.FormatNeeds(npc.Needs)}] Tile={npc.Tile.Q},{npc.Tile.R} " +
                $"Pos={Trace.FormatPos(npc.Position)} Junction={Trace.FormatJunction(npcJunction)} " +
                $"Env=[Temp={world.Environment.GlobalTemperature:F1} " +
                $"Agents={npc.Perception.Agents.Count + npc.Perception.Hostiles.Count} " +
                $"Radius={agentRadius}]");
            }

            // §30.17: 78% всего потока событий — эта одна точка. Свой подканал:
            // даже включённая диагностика её не поднимает, пока не попросят.
            if (SimTrace.Perception)
            {
                foreach (var obj in npc.Perception.Objects)
                {
                    var interactions = string.Join(",", obj.AvailableInteractions);
                    Trace.Debug(world, npc.Id, "PerceivedObject",
                        $"Obj={obj.Id.Value} Tile={obj.Tile.Q},{obj.Tile.R} Dist={obj.Distance:F2} " +
                        $"Reachable={obj.IsReachable} Occupied={obj.IsOccupied} Interactions=[{interactions}]");
                }
            }
        }
    }

    /// <summary>§125.3: уборка памяти встреч. ТОЛЬКО по сроку — negative
    /// evidence объектов (27.18A) здесь НЕ работает и намеренно не применяется:
    /// пустой тайл означает, что предмет исчез, но что человек УШЁЛ. Правило
    /// объектов стирало бы запись о каждой уходящей на первом же её шаге (её
    /// последний видимый тайл всегда остаётся в радиусе), и память была бы
    /// пуста всегда — ровно наоборот тому, зачем она заведена.</summary>
    private void UpdateAgentMemory(WorldState world, NPCState npc)
    {
        if (npc.Memory.KnownAgents.Count == 0)
        {
            return;
        }

        _forgottenAgentScratch.Clear();
        foreach (var met in npc.Memory.KnownAgents.Values)
        {
            if (world.Tick - met.LastSeenTick > MemoryTtlTicks)
            {
                _forgottenAgentScratch.Add(met.Id);
                if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "AgentMemoryForgotten",
                    $"NPC{met.Id.Value} Expired (unseen for {world.Tick - met.LastSeenTick} ticks)");
            }
        }

        foreach (var forgotten in _forgottenAgentScratch)
        {
            npc.Memory.KnownAgents.Remove(forgotten);
        }
    }

    /// <summary>§125.7: список «помню, но не вижу» — из него §53 берёт право
    /// пойти проверить подругу, оставшуюся дома раненой. Живые записи сюда не
    /// попадают: если она перед глазами, о ней говорит зрение, а не вера.
    /// <para>
    /// Записи живут в пуле наблюдателя — как PerceivedAgent, и по той же
    /// причине: без пула каждый medium-тик аллоцировал бы по объекту на
    /// каждого когда-либо виденного.
    /// </para></summary>
    private void BuildRememberedAgents(WorldState world, NPCState npc)
    {
        foreach (var met in npc.Memory.KnownAgents.Values)
        {
            if (met.LastSeenTick == world.Tick)
            {
                continue; // видит прямо сейчас — это не память
            }

            if (!npc.Perception.RememberedPool.TryGetValue(met.Id, out var entry))
            {
                entry = new RememberedAgent();
                npc.Perception.RememberedPool[met.Id] = entry;
            }

            // Пул переживает тик, поэтому переустанавливается КАЖДОЕ поле.
            entry.Id = met.Id;
            entry.Tile = met.Tile;
            entry.Junction = met.Junction;
            entry.Age = world.Tick - met.LastSeenTick;
            entry.Suffering = met.Suffering;
            entry.AidKind = met.AidKind;
            entry.Helpless = met.Helpless;
            npc.Perception.Remembered.Add(entry);
        }
    }

    /// <summary>§125.2: кто из людей стоит в кольце восприятия наблюдателя.
    /// Две ветки дают ОДИН И ТОТ ЖЕ список (обе кончаются сортировкой по id) —
    /// выбор между ними чисто по цене: кольцо радиуса r стоит 1+3r(r+1)
    /// обращений к индексу, полный перебор — по человеку на острове.</summary>
    private static void CollectAgentsInRadius(
        WorldState world, NPCState npc, int radius,
        System.Collections.Generic.List<EntityId> into)
    {
        into.Clear();
        if (radius < 0)
        {
            return;
        }

        var ringTiles = 1 + 3 * radius * (radius + 1);
        if (ringTiles <= world.Entities.Npcs.Count)
        {
            for (var dq = -radius; dq <= radius; dq++)
            {
                var lo = System.Math.Max(-radius, -dq - radius);
                var hi = System.Math.Min(radius, -dq + radius);
                for (var dr = lo; dr <= hi; dr++)
                {
                    var coord = new Common.TileCoord(npc.Tile.Q + dq, npc.Tile.R + dr);
                    if (!world.Caches.EntitiesByTile.TryGetValue(coord, out var onTile))
                    {
                        continue;
                    }

                    foreach (var id in onTile)
                    {
                        // Индекс держит и мёртвых на тик смерти, и саму
                        // наблюдательницу — фильтруем по живому реестру.
                        if (!id.Equals(npc.Id) && world.Entities.Npcs.ContainsKey(id))
                        {
                            into.Add(id);
                        }
                    }
                }
            }
        }
        else
        {
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (!other.Id.Equals(npc.Id) &&
                    HexSpatialMath.HexDistance(npc.Tile, other.Tile) <= radius)
                {
                    into.Add(other.Id);
                }
            }
        }

        into.Sort(static (a, b) => a.Value.CompareTo(b.Value));
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
        if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "JunctionResolved",
            $"NearestJunction={Trace.FormatJunction(nearest)} Pos={Trace.FormatPos(npc.Position)}");
        return nearest;
    }
}

}
