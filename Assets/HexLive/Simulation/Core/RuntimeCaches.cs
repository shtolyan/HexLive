using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Core
{

public sealed class RuntimeCaches
{
    public Dictionary<TileCoord, List<EntityId>> EntitiesByTile { get; } = new();

    public Dictionary<FragmentId, List<EntityId>> EntitiesByFragment { get; } = new();

    public Dictionary<TileCoord, List<ObjectId>> ObjectsByTile { get; } = new();

    // Spec 29C.3 (chase-path fix): junctions a ground mob may never STEP on —
    // indoor (sanctuary), doors, all-water. Chase pathfinding feeds these into
    // FindPath's avoid set so a dog plans routes it can actually walk; without
    // this it planned the girls' shortest path THROUGH the hut, refused the
    // first indoor step every pass, and stood frozen mid-camp forever (seed
    // 521091321 day 43, dog 13). Rebuilt lazily when TopologyVersion moves
    // (build completions bump it when walls/doors/blocks change).
    public HashSet<JunctionId> MobForbiddenJunctions { get; } = new();

    public int MobForbiddenBuiltVersion { get; set; }

    // §129: portal junction → the door piece standing in it. Doors appear and
    // disappear only with construction/demolition, which bumps TopologyVersion,
    // so that is the key. Rebuilt lazily by DoorTopology.
    public Dictionary<JunctionId, ObjectId> DoorByPortal { get; } = new();

    public int DoorByPortalBuiltVersion { get; set; }

    // §129: portals whose door leaf is currently CLOSED, plus the per-faction
    // hard-avoid sets derived from them (for faction F: closed portals whose
    // door is hostile to F). Both depend on door STATE, so they key on the
    // (TopologyVersion, DoorStateVersion) pair. An absent/empty faction entry
    // means "no bans" and callers get null — the colony fast path.
    public HashSet<JunctionId> ClosedDoorPortals { get; } = new();

    public Dictionary<Faction, HashSet<JunctionId>> FactionForbiddenJunctions { get; } = new();

    public int DoorStateBuiltTopologyVersion { get; set; }

    public int DoorStateBuiltDoorVersion { get; set; }

    // §135: оторванные конечности, лежащие в мире. Индекс существует ровно для
    // того, чтобы поиск падали НИЧЕГО не стоил в обычной игре: конечностей на
    // острове нет почти всегда, и вся проверка зверя сводится к сравнению
    // счётчика с нулём. Без индекса каждый зверь каждый средний тик перебирал
    // бы все ~218 объектов мира ради пустого множества.
    //
    // Пополняется в момент появления (§50.4 Sever и §135 DropAtDeath), чистится
    // ЛЕНИВО при обходе: конечность может исчезнуть тремя разными путями
    // (сгнила по CorpseSystem, её съели, её забрали), и ловить каждый значило бы
    // три места, которые можно забыть. Строится один раз на загруженный мир.
    public List<ObjectId> SeveredLimbs { get; } = new();

    public bool SeveredLimbsIndexed { get; set; }

    // §135.5: длина самого длинного ребра графа джанкшенов — знаменатель
    // эвристики A* в HexPathfinder. Позиции узлов это вывод worldgen и после
    // него не меняются (Blocked двигает проходимость, не геометрию), поэтому
    // значение считается один раз на мир. 0 = ещё не считали, -1 = граф
    // вырожденный, эвристика выключена.
    public float LongestJunctionEdge { get; set; }

    // §54.12 / §30.16: ledges belong to ONE world. The first implementation
    // cached Junction object references in a static DecisionSystem list keyed
    // only by the numeric TopologyVersion. Fresh worlds normally all start at
    // version 1, so a multi-seed soak silently queried the previous island's
    // junctions. Keep ids in the existing per-world derived-cache container;
    // no cross-world reference can survive, even when versions are equal.
    public List<JunctionId> LedgeJunctions { get; } = new();

    public int LedgeJunctionsBuiltVersion { get; set; } = -1;

    // §30.16 r2: path-derived scratch and tick caches are world-owned for the
    // same reason as ledges. A static tick key can make a second world reuse
    // another island's danger/hostile ring at an equal simulation tick.
    public HashSet<JunctionId> OtherActorJunctionsScratch { get; } = new();

    public HashSet<JunctionId> DangerRingJunctions { get; } = new();

    public Queue<JunctionId> DangerRingQueue { get; } = new();

    public int DangerRingBuiltTick { get; set; } = -1;

    public Dictionary<Faction, HashSet<JunctionId>> HostileRings { get; } = new();

    public Dictionary<Faction, int> HostileRingBuiltTicks { get; } = new();

    public Queue<JunctionId> HostileRingQueue { get; } = new();

    public HashSet<JunctionId> CombinedDangerRingScratch { get; } = new();

    // §30.16 r3: generic object-planning and its availability mirror walk
    // exactly the same interaction rim.  The scratch list must belong to the
    // world: parallel/multi-seed soaks may plan two islands on the same process,
    // and a static list lets one planner clear the other's candidates.
    public List<JunctionId> ObjectApproachJunctionsScratch { get; } = new();

    // §40.6 r7: voluntary swimming is allowed only when the bather has a
    // short, door-independent round trip.  These are per-world because a
    // multi-seed soak runs several islands in one process; shared scratch
    // would mix numeric junction ids from unrelated graphs.
    public List<(JunctionId Junction, float Distance)> BathWaterCandidatesScratch { get; } = new();

    public List<(JunctionId Junction, float Distance)> BathShoreCandidatesScratch { get; } = new();

    // §35.4: exact cool-off availability and planning share the same bounded
    // nearest-candidate buffer. World-owned for the same multi-seed isolation
    // reason as the bathing buffers above.
    public List<(JunctionId Junction, float Distance)> CoolingCandidatesScratch { get; } = new();

    public HashSet<JunctionId> VoluntaryWaterDoorAvoidScratch { get; } = new();
}

}
