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
}

}
