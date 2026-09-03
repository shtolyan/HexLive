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

    // §156: чанки, которые симулируются на этом тике — объединение дисков
    // пробуждения всех живых NPC. Пересобирается движком перед слоем Slow,
    // поэтому НЕ сериализуется и не едет в снапшоте: это вывод из позиций
    // колонисток, а не состояние мира. Пуст, пока механика выключена.
    public HashSet<ChunkCoord> ActiveChunks { get; } = new();

    // Тот же набор списком и в порядке (Cq, Cr). Множество отвечает на вопрос
    // «спит ли», список — задаёт ПОРЯДОК обхода: у HashSet его нет, а обход по
    // нему решал бы, в каком порядке мир получает свои плоды и трупы.
    public List<ChunkCoord> ActiveChunksOrdered { get; } = new();

    // ⭐ §156: посчитан ли набор ВООБЩЕ. Пустой набор и НЕсчитанный — разные
    // вещи, и различать их обязательно: набор считает движок, а систему можно
    // запустить и без него (так живёт половина поведенческих тестов и стенды
    // сцен). Без этого флага такой прогон означал бы «не бодрствует ничто», и
    // система молча не делала бы НИЧЕГО — костёр не горит, вещи не сохнут, и
    // никакой ошибки при этом не видно.
    //
    // Отказ направлен в безопасную сторону: не посчитан — значит фильтра нет и
    // мир живёт целиком, как до §156. Потерять можно только экономию, но не
    // саму жизнь мира.
    public bool ActiveChunksComputed { get; set; }

    // §156: объекты по чанкам. Ради этого индекса всё и затевалось: пока
    // системы обходили ВЕСЬ словарь и спрашивали IsAwake про каждого, фильтр
    // платился за 100% объектов, а экономил на 15-26% (замер §156.9) — то есть
    // сон стоил дороже, чем экономил. По этому индексу спящий объект не стоит
    // даже обращения к хешу.
    //
    // Держится в тех же ТРЁХ местах, что и ObjectsByTile (Spawn, Despawn,
    // MoveObjectTile), и восстанавливается из ростера при загрузке — своего
    // формата в сейве у него нет, потому что он целиком выводится из Tile.
    public Dictionary<ChunkCoord, List<ObjectId>> ObjectsByChunk { get; } = new();

    // Сторона чанка, на которой индекс выше построен. ChunkSizeTiles — ручка
    // баланса: её правка на живом мире обязана перестроить индекс, иначе он
    // молча указывает в клетки, которых больше нет.
    public int ObjectsByChunkSize { get; set; }

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

    // §158.3: сколько раз связность пришлось перестроить целиком (первое
    // построение, загрузка, InvalidateAll, неразрешимый раскол). Метрика
    // прогона: на бодром мире это единицы, а не «по разу на бревно».
    public int ConnectivityFullRebuilds { get; set; }

    // §158.4: скретчи локального перечисления узлов (LocalSearch).
    public HashSet<JunctionId> LocalSearchSeenScratch { get; } = new();

    public List<HexLive.Simulation.Spatial.Junction> LocalSearchScratch { get; } = new();

    public List<HexLive.Simulation.Spatial.Junction> LocalSearchRingScratch { get; } = new();

    // §158.5: узлы, все тайлы которых — глубокая вода. Геометрия worldgen,
    // строится один раз на мир (раньше — на КАЖДЫЙ критический маршрут).
    public HashSet<JunctionId> DeepWaterJunctions { get; } = new();

    public bool DeepWaterJunctionsBuilt { get; set; }

    // §158.5: курсоры журнала топологии для кэшей, что раньше перестраивались
    // полным обходом на каждую смену TopologyVersion.
    public int MobForbiddenJournalCursor { get; set; }

    public int LandSlotHomeBaseJournalCursor { get; set; }

    public int CrabSlotHomeBaseJournalCursor { get; set; }

    public List<JunctionId> TopologyChangedScratch { get; } = new();

    // §30.16 r2: path-derived scratch and tick caches are world-owned. The
    // first ledge cache (§54.12, since replaced by the §158.4 local search)
    // lived in a static DecisionSystem list keyed only by the numeric
    // TopologyVersion, and a multi-seed soak silently queried the previous
    // island's junctions; a static tick key can likewise make a second world
    // reuse another island's danger/hostile ring at an equal simulation tick.
    public HashSet<JunctionId> OtherActorJunctionsScratch { get; } = new();

    public HashSet<JunctionId> DangerRingJunctions { get; } = new();

    public Queue<JunctionId> DangerRingQueue { get; } = new();

    public int DangerRingBuiltTick { get; set; } = -1;

    public Dictionary<Faction, HashSet<JunctionId>> HostileRings { get; } = new();

    public Dictionary<Faction, int> HostileRingBuiltTicks { get; } = new();

    // §146.12: solo-camp hostility is directed per person, so a faction-keyed
    // ring would make one woman's grudge force every camp-mate to detour.
    public Dictionary<EntityId, HashSet<JunctionId>> PersonalHostileRings { get; } = new();

    public Dictionary<EntityId, int> PersonalHostileRingBuiltTicks { get; } = new();

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

    // §147 PERF (Aug-2026, BigIsland): slot-home candidates. The naive build
    // scanned all ~194k junctions — and for crabs ran a per-junction NearWater
    // that itself scanned all 4032 tiles with a boxing HasFlag: 8.9 s and
    // 19.6 GB of garbage on the FIRST medium tick, repeated in full by every
    // Rehome after a crab death. The static part of the filter (blocked /
    // indoor / all-water / near-water) is topology, so it is cached here and
    // keyed on TopologyVersion like MobForbiddenJunctions above; the dynamic
    // part (distance to NPCs and camps) is applied per call into the scratch.
    public List<JunctionId> CrabSlotHomeBase { get; } = new();

    public int CrabSlotHomeBaseBuiltVersion { get; set; } = -1;

    public List<JunctionId> LandSlotHomeBase { get; } = new();

    public int LandSlotHomeBaseBuiltVersion { get; set; } = -1;

    // The per-call candidate list EnsureSlots mutates (RemoveAt): reused, not
    // reallocated on every medium tick.

    // PERF (Aug-2026): позиции джанкшенов — вывод worldgen и не меняются;
    // сетка ячеек для FindNearestJunction строится один раз на мир. Старый
    // линейный проход по всем ~194k узлам оказался одним из самых горячих
    // мест скоринга ИИ (ExploreRejectionFor и промахи кэша CurrentJunction).
    public Dictionary<long, List<JunctionId>> JunctionPosGrid { get; } = new();

    public bool JunctionPosGridBuilt { get; set; }

    public int JunctionGridMinX { get; set; }

    public int JunctionGridMaxX { get; set; }

    public int JunctionGridMinY { get; set; }

    public int JunctionGridMaxY { get; set; }

    // BakeRing BFS scratch — a ring bake allocated a List+HashSet+Queue per
    // attempt (×144 on a shortfall tick). World-owned, same reason as above.
    public List<HexLive.Simulation.Spatial.Junction> RingReachScratch { get; } = new();

    public HashSet<JunctionId> RingVisitedScratch { get; } = new();

    public Queue<HexLive.Simulation.Spatial.Junction> RingQueueScratch { get; } = new();

    public List<HexLive.Simulation.Spatial.Junction> RingStepScratch { get; } = new();
}

}
