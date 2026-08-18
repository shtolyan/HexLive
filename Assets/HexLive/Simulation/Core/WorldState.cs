using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Core
{

public sealed class WorldState
{
    /// <summary>
    /// Runtime-only monotonic clock. The engine replaces the default instance
    /// with its clock; saves and snapshots deliberately do not carry it.
    /// </summary>
    internal SimulationClock RuntimeClock { get; set; } = new();

    public int Tick { get; set; }

    public float TickDeltaTime { get; set; }

    public FragmentMap Fragments { get; } = new();

    public TileMap Tiles { get; } = new();

    public JunctionMap Junctions { get; } = new();

    public EntityRepository Entities { get; } = new();

    public ReservationState Reservations { get; } = new();

    public OccupancyState Occupancy { get; } = new();

    public EnvironmentState Environment { get; } = new();

    public RuntimeCaches Caches { get; } = new();

    public SimulationEventBuffer Events { get; } = new();

    /// <summary>
    /// Spec §30.14. Отладочный бортовой самописец: последние ~64 события каждого
    /// NPC. Null в сборке игрока и вообще везде, где его не включили явно.
    /// <para>
    /// Намеренно НЕ сохраняется и НЕ едет в снапшоте: это инструмент наблюдения,
    /// а не состояние мира. Значит ни <c>WorldSaveSerializer</c>, ни
    /// <c>WorldSnapshotCodec</c> о нём не знают, и гейт покрытия провода его не
    /// касается.
    /// </para>
    /// </summary>
    public Runtime.FlightRecorder FlightRecorder { get; set; }

    /// <summary>
    /// Spec §122. Реестр намерений: что каждая NPC хотела и чем это КОНЧИЛОСЬ —
    /// единственное место, где в симуляции записан ПРОГРЕСС.
    /// <para>
    /// В отличие от самописца выше он НЕ nullable и включён всегда: его кормит
    /// сравнение трёх полей за тик, а не поток событий, и петля обязана
    /// обнаруживаться в релизной сборке игрока, а не только в редакторе.
    /// </para>
    /// <para>
    /// Так же НЕ сохраняется и НЕ едет в снапшоте: инструмент наблюдения, а не
    /// состояние мира. После загрузки отсчёт начинается заново — безобидно.
    /// </para>
    /// </summary>
    public Runtime.IntentLedger IntentLedger { get; } = new();

    public ContentCatalog Content { get; } = new();

    // Runtime-spawned objects allocate ids from here; bootstrap ids stay below 1000.
    public int NextRuntimeObjectId { get; set; } = 1000;

    /// <summary>
    /// §120.8: произвольные чертежи игрока, размеченные в ЭТОМ мире, по id.
    /// Id 0 зарезервирован за встроенным committed-планом
    /// (<c>CommittedBuildingPlans.PlayerHut</c>) и в словаре не живёт.
    /// Чертёж после стейка неизменяем; запись удаляется вместе со снятой
    /// пустой площадкой. Едет в сейве (v50); на проводе НЕ едет — клиент
    /// рендерит модульные объекты, а чертёж отправляет только в команде.
    /// </summary>
    public System.Collections.Generic.Dictionary<int, Runtime.Blueprints.BuildingBlueprintDraft>
        PlayerBlueprints { get; } = new();

    public int NextPlayerBlueprintId { get; set; } = 1;

    // §72.14/§132: how many post-start raid BOUNDARIES were processed.
    // A boundary can spawn a raider or be consumed by a full camp; persisting
    // the latter is what prevents a death tomorrow from releasing a backlog.
    // The authored opening outsider is not a wave; this counts 1, 2, ... only.
    public int RaidWavesSpawned { get; set; }

    // §132: same schedule cursor for the colony's weekly arrival. It counts
    // processed opportunities, not living arrivals, for the same no-backlog rule.
    public int ColonyArrivalsProcessed { get; set; }

    // Spec 40.15: logs hauled to the escape raft (target 20). At the target the
    // colony can sail off the island — the global goal.
    public int RaftProgress { get; set; }
    public const int RaftTarget = 10; // spec 45 r2: reachable endgame

    // The colony has reached the scenario ending (currently: launched the raft).
    // Runners and presentation use this as the stable "show results / stop time"
    // latch, while saves keep loading back into the finished state.
    public bool Completed { get; set; }

    // Permanent end-screen history. The trace buffer is intentionally bounded,
    // so deaths are also recorded here for the final summary and saved games.
    public System.Collections.Generic.List<DeathRecord> DeathRecords { get; } = new();

    // Spec 40.16: latch for the joint-plan advisor's dire-straits trigger — set
    // while the colony is in crisis so the advisor is consulted once per onset,
    // not every tick.
    public bool ColonyInDireStraits { get; set; }

    // Spec 40.17: walkable junctions that straddle a one-level elevation step —
    // "climb seams". Tagged at world-gen; the pathfinder charges 2x to cross
    // one so routes prefer the flat detour but still climb when it's shorter.
    public System.Collections.Generic.HashSet<Common.JunctionId> ClimbSeams { get; } = new();

    // Spec 40.18: sea junctions opened for swimming — a shallow ring the
    // pathfinder may cross at ~4x cost (a slow, shark-risked last resort).
    public System.Collections.Generic.HashSet<Common.JunctionId> SwimJunctions { get; } = new();

    // Spec 40.18 step 4: the strait crossing to the second island — swim
    // junctions the pathfinder charges a reduced cost so a foraging NPC can
    // afford the hop for an island-exclusive resource (the wider ring stays 4x).
    public System.Collections.Generic.HashSet<Common.JunctionId> StraitJunctions { get; } = new();

    // Spec 43: tiles currently in cast shadow (terrain + canopy), rebuilt by
    // EnvironmentSystem every medium tick from the sun path. DERIVED — never
    // serialized; a loaded world repopulates it on its first tick.
    public System.Collections.Generic.HashSet<Common.TileCoord> ShadedTiles { get; } = new();

    // §54.2 r2 (#168): перепись тегов ВСЕХ объектов мира, посчитанная не чаще
    // раза в тик (ColonyQueries.WorldCountWithTag). Запас рощи и «сначала
    // подбери, что лежит» — утверждения про остров, а не про поле зрения.
    // DERIVED — не сериализуется, пересчитывается при первом же вопросе.
    public System.Collections.Generic.Dictionary<string, int> TagCensus { get; } = new();

    public int TagCensusTick { get; set; } = -1;

    // Spec 43: sun horizontal direction (world XZ) and elevation in degrees,
    // exported so the rendered light matches the sim's shadow math exactly.
    public Common.Float2 SunDirection { get; set; }
    public float SunElevationDegrees { get; set; }

    // Spec 29C.1: all chance rolls mix this seed.
    public int Seed { get; set; } = 12345;

    // Spec 29C.3: lightweight wildlife, not NPCs.
    public System.Collections.Generic.List<Wildlife.MobState> Mobs { get; } = new();

    public int NextMobId { get; set; } = 1;

    // Spec 41.2 v2: wildlife respawn-check timers live in the MODEL — as
    // system-local fields they silently reset on load (an off-schedule
    // respawn roll right after every restore).
    public int NextMobSpawnCheckTick { get; set; }

    public int NextRabbitSpawnCheckTick { get; set; }

    // Spec 35.1: junction connected-components cache for O(1) reachability.
    // TopologyVersion increments whenever junction blocking changes (walls).
    public int TopologyVersion { get; set; } = 1;

    // §129: increments whenever a door opens or closes. Deliberately SEPARATE
    // from TopologyVersion: a closed door is behaviour, not topology (the
    // portal junction is never Blocked), so swinging a door must not force a
    // rebuild of the connectivity/component caches above. Keys only the door
    // caches in RuntimeCaches. DERIVED-cache key — never serialized; a loaded
    // world starts at 1 and every dependent cache starts dirty.
    public int DoorStateVersion { get; set; } = 1;

    public int ComponentsBuiltVersion { get; set; }

    public System.Collections.Generic.Dictionary<JunctionId, int> JunctionComponents { get; } = new();

    // Spec §50: a second connectivity graph for survivors who CAN'T jump (a lost
    // leg) — it omits every elevation-step edge, so a legless girl reads a higher
    // ledge / the water as unreachable and never plans a route she can't crawl.
    public int ComponentsFlatBuiltVersion { get; set; }

    public System.Collections.Generic.Dictionary<JunctionId, int> JunctionComponentsFlat { get; } = new();

    // §50/§: размеры плоских компонент (id → число узлов) и id самой большой.
    // Считаются вместе с JunctionComponentsFlat и нужны инстинкту «спуститься
    // на большую землю»: раненая на крошечном уступе должна ВИДЕТЬ, что её
    // мир сжался до горстки узлов, пока прыжок ещё возможен. Кэш, не сейв.
    public System.Collections.Generic.Dictionary<int, int> JunctionComponentsFlatSizes { get; } = new();

    public int LargestFlatComponentId { get; set; } = -1;

    // §57.11: замыкание СПУСКОВ по плоским компонентам. Ключ — компонента,
    // значение — все компоненты, куда из неё можно попасть, идя только по
    // плоским рёбрам и вниз (правила пафйндера для canJump=false). Мир калеки
    // направленный: попасть вниз можно, вернуться — нет, поэтому Reachable
    // без прыжка = та же компонента ИЛИ членство в этом замыкании.
    public System.Collections.Generic.Dictionary<int,
        System.Collections.Generic.HashSet<int>> FlatDescendClosure { get; } = new();

    // Spec §26.6A r4: the junctions closed by an OBJECT FOOTPRINT (a palm trunk,
    // the fire's ember ring, a bed) — as opposed to TERRAIN (a cliff face, a hut
    // wall, the open sea). Both read `Junction.Blocked`, and the ROUTE question
    // ("is there a way to stand beside it at all") still tells them apart this
    // way: a body is walked AROUND, a cliff never is. The REACH question is
    // stricter and per-reacher — see SpatialQueries.IsBarrierFor.
    // DERIVED from every object's BlockedJunctions — rebuilt whenever
    // TopologyVersion moves, exactly like the component caches above.
    public int ObjectBlockBuiltVersion { get; set; }

    public System.Collections.Generic.HashSet<JunctionId> ObjectBlockedJunctions { get; } = new();

    // Spec 35.3: the communal hut project (null once cleanup removes it).
    public BuildProject? Project { get; set; }

    // Spec §64: the colony's ordered dream queue (campfire → own bed → …).
    // Seeded lazily by DreamSystem from SpecDream.DefaultQueue, so fresh and
    // loaded worlds both self-heal without touching the factory or serializer.
    public System.Collections.Generic.List<DreamType> DreamQueue { get; } = new();

    // Spec §64: monotonic latch — set the first tick a lit campfire is seen,
    // never cleared. Deriving the campfire dream from the LIVE lit-state would
    // flip the dream back every time a fire burns out; the latch is what keeps
    // the colony's aspiration advancing. Serialized (a night reload with a dead
    // fire must not re-activate the campfire dream).
    public bool CampfireDreamDone { get; set; }

    // Spec §64: the colony's current dream (first queue entry not colony-met),
    // recomputed each Slow tick by DreamSystem. A plain field for cheap gate
    // reads (BedSiteSystem, UI). DERIVED — not serialized; recomputed on load.
    public DreamType ActiveDream { get; set; } = DreamType.Campfire;

    // §72: where each faction's camp is anchored, authored at bootstrap and
    // serialized. It is the ONE piece of per-faction world state stage 1 needs:
    // it scopes "our hearth" (otherwise the outsider lighting a fire first would
    // satisfy the COLONY's §64 campfire dream), it bounds the home knowledge an
    // NPC is seeded with, and it is where a beaten raider retreats to.
    //
    // Everything else that reads as "colony" is already per-camp for free —
    // objects are perceived within 2 tiles, so the fire she tends and the pile
    // she hauls to are her own by construction.
    public System.Collections.Generic.Dictionary<Agents.Faction, Common.TileCoord> FactionHomes { get; } = new();

    // Spec 29F.1: prey.
    public System.Collections.Generic.List<Wildlife.RabbitState> Rabbits { get; } = new();

    // Spec 40.18: sharks patrolling the swim ring — bite swimmers.
    public System.Collections.Generic.List<Wildlife.SharkState> Sharks { get; } = new();

    public int NextRabbitId { get; set; } = 1;
}

// Spec 35.3: one communal hut — floor, five walls, one home-facing door.
public sealed class BuildProject
{
    public Common.TileCoord Tile { get; set; }

    public int DoorEdge { get; set; }

    public bool FloorDone { get; set; }

    public bool[] EdgeDone { get; } = new bool[6];

    public bool Completed { get; set; }
}

public sealed class DeathRecord
{
    public EntityId EntityId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int Tick { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public string Cause { get; set; } = string.Empty;
}

}
