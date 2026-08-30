#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.TwoPeopleTest;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

public sealed class HexWorldRenderer : MonoBehaviour
{
    // The authored ClothingSlot marks the hook centre. Garments must sit at
    // the shoulder apex below it rather than appearing to float from the hook.
    private const float WardrobeGarmentShoulderDrop = 0.120f;

    [SerializeField] private SimulationRunnerBehaviour? _runner;

    // Spec 31.17: ~14k junction spheres are ~10M triangles — debug only.
    [SerializeField] private bool _showJunctionMarkers;

    // Spec 20.16: terrain-style grass tufts on grass tiles.
    [SerializeField] private bool _grassDetail = true;
    [SerializeField, Range(0, 30)] private int _grassBladesPerTile = 12;

    private const float JunctionMarkerScaleFactor = 1f / 25f;
    private const float EdgeLineHeight = 0.01f;

    private const float NpcRadiusFactor = 17f / 75f;
    private const float NpcHeightFactor = 11f / 30f;
    // At 4 Hz this gives a fresh sever three seconds to reach a viewer even
    // across remote/delta batching. Distance is checked too, so an old limb
    // never follows an owner who has already crawled away.
    private const int FreshLimbPoseWindowTicks = 12;

    private const float BedWidthFactor = 7f / 25f;
    private const float BedHeightFactor = 2f / 25f;
    private const float BedDepthFactor = 14f / 75f;

    // The lying clips are ground-authored (the body's underside baked at root
    // Y=0), but the visible back rests a touch below the root plane, so pinning
    // the root straight onto the mattress top makes the sleeper sink in. Lift
    // the pin by this much (world units) so the body rests on top of the bed.
    private const float SleepBodyLift = 0.12f;

    private const float ChairRadiusFactor = 3f / 25f;
    private const float ChairHeightFactor = 7f / 75f;

    private const float ClothingRadiusFactor = 7f / 75f;
    private const float ClothingHeightFactor = 2f / 15f;

    private const float FoodRadiusFactor = 7f / 75f;

    private const float TreeRadiusFactor = 4f / 25f;
    private const float TreeHeightFactor = 1f / 2f;

    private readonly Dictionary<TileCoord, GameObject> _tileViews = new();
    private readonly Dictionary<int, GameObject> _junctionViews = new();

    // Spec 40.17: a persistent "точечка на шве" — a small amber dot marking a
    // climb seam (a one-step ledge you clamber up), always shown (not the
    // debug-only junction spheres).
    private readonly Dictionary<int, GameObject> _seamMarkers = new();
    private readonly Dictionary<int, GameObject> _objectViews = new();

    // §120: which owner objects are houses raised from a player PLAN (site or
    // finished building). Such a building owns no mesh at all — every module is
    // an ordinary world object that draws itself through its own view, so this
    // is the one question the object loop has to answer about an architecture
    // piece. The canonical hut_1hex is NOT here: its pieces are slots of one
    // shared HutAssembly mesh, and their views stay bare id markers.
    private readonly HashSet<int> _planBuildingOwners = new();
    private readonly HashSet<int> _unfinishedPlanBuildingOwners = new();

    // §120: the ANCHOR junction of every plan building, by owner id. A module's
    // LocalX/LocalZ is measured from it, and one module — the door — no longer
    // stands on it: RepairPlanTopology rebinds the door piece's single junction
    // to the navigation throat (BuildingBootstrap.RepairPlanTopology), because
    // DoorTopology indexes a door by exactly one junction. Drawing that piece
    // from its own junction added the door's offset twice and pushed it a
    // measured 1.299 wu straight out through the wall line.
    private readonly Dictionary<int, Float2> _planOwnerAnchors = new();

    // §120: plan buildings that are RAISED (not still a site) — the cutaway
    // owners, matching the monolith, whose _hutCutawayViews are only collected
    // for a finished building.hut_1hex.
    private readonly HashSet<int> _planRaisedOwners = new();

    // §120: every hex a raised plan building covers. Furniture standing here is
    // the plan's OWN furniture, so it is drawn on its footprint centre and in
    // the six-way footprint basis, exactly as the constructor preview draws it.
    private readonly HashSet<TileCoord> _planFootprintTiles = new();

    // PERF: the per-tick object loop used to re-ask every view for the same
    // handful of components (campfire, assembly, spit, garment, build pile).
    // ~5 GetComponent × ~218 objects × 4 Hz, and the MISSES are the expensive
    // half — Unity builds a null-error message for each one. The components a
    // view owns are decided once, when the view is built, and never change
    // afterwards, so they are resolved there and remembered here.
    private struct ObjectViewParts
    {
        public HexLive.UnityPresentation.Environment.CampfireEffect Fire;
        public HexLive.UnityPresentation.Environment.BedAssembly Assembly;
        public HexLive.UnityPresentation.Environment.CampfireSpitMeat SpitMeat;
        public GarmentWorldCondition Garment;
        public HexLive.UnityPresentation.Environment.BuildSitePile Pile;
        public HexLive.UnityPresentation.Environment.CraftProjectVisual CraftProject;
        public HexLive.UnityPresentation.Environment.HutAssembly Hut;
        public HexLive.UnityPresentation.Environment.ArchitectureModuleView Module;
        public Views.WorldObjectView ObjectView;
    }

    private readonly Dictionary<int, ObjectViewParts> _objectViewParts = new();

    // Bug #268: all decorative vegetation is advanced by this renderer's one
    // frame loop. Hundreds of grass tiles must not each own an Update method.
    private readonly List<VegetationWindTarget> _vegetationWindTargets = new();

    private struct VegetationWindTarget
    {
        public Transform Transform;
        public Quaternion BaseRotation;
        public float Phase;
        public float MaxTiltDegrees;
    }

    private readonly struct HutCutawayView
    {
        public readonly TileCoord Tile;
        public readonly HexLive.UnityPresentation.Environment.HutAssembly Assembly;

        public HutCutawayView(
            TileCoord tile, HexLive.UnityPresentation.Environment.HutAssembly assembly)
        {
            Tile = tile;
            Assembly = assembly;
        }
    }

    private readonly List<HutCutawayView> _hutCutawayViews = new();

    // §120: the same feature for a house that HAS no monolith. One entry per
    // module the cutaway may take away — walls, windows, doors, corner supports
    // and roof panels; floor sectors never hide, exactly as the monolith never
    // hides its deck.
    private readonly struct PlanCutawayModule
    {
        public readonly int OwnerId;
        public readonly bool Roof;
        public readonly Vector2 Position;
        public readonly HexLive.UnityPresentation.Environment.ArchitectureModuleView View;

        public PlanCutawayModule(
            int ownerId, bool roof, Vector2 position,
            HexLive.UnityPresentation.Environment.ArchitectureModuleView view)
        {
            OwnerId = ownerId;
            Roof = roof;
            Position = position;
            View = view;
        }
    }

    private readonly List<PlanCutawayModule> _planCutawayModules = new();
    private readonly List<int> _planCutawayIndexScratch = new();
    private readonly List<Vector2> _planCutawayPositionScratch = new();
    private readonly List<int> _planCutawayHiddenScratch = new();
    private Camera? _cutawayCamera;

    // PERF: junction id -> world position. Worldgen output, so it is built once
    // per world instead of every tick (see RenderSnapshot). _junctionMarkersBuilt
    // remembers which _showJunctionMarkers state the walk was made under, so
    // flipping the debug toggle at runtime still spawns the spheres.
    private readonly Dictionary<int, Float2> _junctionPositions = new();
    private bool _junctionMarkersBuilt;

    // Per-tick scratch for the snapshot-diff despawn — reused instead of
    // reallocated (this runs 4×/s for the life of the game).
    private readonly HashSet<int> _liveObjectIdScratch = new();
    private readonly List<int> _staleObjectKeyScratch = new();

    // Spec §54: object keys whose view is a tree, so despawn (a chop) plays a
    // fall animation + leaves a stump instead of a hard cut.
    private readonly HashSet<int> _treeViewKeys = new();

    // World objects hidden on the ground this frame because their owner has
    // picked the same object up into hand for the active animation beat.
    // Must match ExecutionSystem.WardrobeHandoffFraction.
    private const float WardrobeHandoffFraction = 0.5f;
    private readonly HashSet<int> _wardrobeHiddenObjects = new();

    // FOG-OF-WAR EXPERIMENT (visual only, sim untouched): hide object views no
    // colony NPC has in her object memory, and mobs beyond the spot radius of
    // every colonist. Object memory does not travel the wire, so this reads
    // Engine.World directly — a deliberate, experiment-only breach of the
    // "no Engine in rendering" rule; on a remote/loopback backend Engine is
    // null and the fog silently stays off.
    private readonly HashSet<int> _fogKnownObjects = new();
    // §125.5: пары (тайл наблюдателя, ЕЁ радиус восприятия). Радиус приезжает
    // готовым числом в снапшоте — прежнее локальное зеркало константы 6 больше
    // не имеет смысла: зоркость у каждой своя.
    private readonly List<(TileCoord Tile, int Radius)> _fogEyes = new();
    private bool _fogActive;

    // PERCEPTION-CULLING EXPERIMENT (player request, big-island perf): the
    // live world is the union of the LIVING colonists' §125 perception disks.
    // Everything outside it FREEZES rather than disappears — a memory fog:
    // tiles and object views stay exactly as last seen (no per-tick sync, no
    // despawn, no new views), and only the movers (NPCs, mobs, corpses) go
    // dark, because a wolf frozen mid-stride would be a statue, not a memory.
    // Purely presentation: the simulation runs the whole island as before.
    // Eyes and radii come from the snapshot (PerceptionRadiusTiles), so this
    // works on remote/loopback backends too, unlike the fog experiment above.
    private readonly List<(TileCoord Tile, int Radius)> _cullEyes = new();
    private readonly HashSet<TileCoord> _cullVisibleTiles = new();

    // §148: КАРТА ПАМЯТИ ИГРОКА — гексы, которые наш лагерь когда-либо видел.
    // Игрок смотрит на остров глазами своих девушек: никогда не виденный гекс
    // не нарисован вовсе (ни земли, ни деревьев), виденный, но не видимый
    // сейчас, стоит замороженной картинкой «как в последний раз».
    private readonly HashSet<TileCoord> _everSeenTiles = new();

    // §148: где мы в последний раз видели ЧУЖОГО человека. Пока он вне
    // восприятия, на этом месте висит «?» — тело не рисуется и не кликается.
    private readonly Dictionary<int, TileCoord> _lastSeenNpcTiles = new();

    private readonly Dictionary<int, GameObject> _unknownNpcMarkers = new();

    // §148: тайлы, чью затенённость уже применили — перекрашиваем только те,
    // у кого состояние сменилось (иначе это тысячи перекрасок за тик).
    private readonly Dictionary<TileCoord, int> _tileShadeApplied = new();

    // B0: подмена на общий тонированный материал вместо MaterialPropertyBlock —
    // MPB выбивает рендерер из SRP Batcher, а тайлов тысячи. Хранится исходный
    // массив sharedMaterials и возвращается при возврате гекса в восприятие.
    private readonly Dictionary<Renderer, Material[]> _tileShadeSaved = new();

    // PERF (B0): набор рендереров тайла окончателен после CreateTileView, так
    // что обход GetComponentsInChildren выполняется один раз на тайл.
    private readonly Dictionary<GameObject, Renderer[]> _tileRendererCache = new();
    // The frozen hexes as their OWN set, by design: a future pass will restyle
    // them (grayscale/whiteout shader) by iterating exactly this collection.
    private readonly HashSet<TileCoord> _cullFrozenTiles = new();
    // Last tile each object view was synced on — a vanished object keeps its
    // ghost while that tile is frozen (nobody saw it go), and the ghost dies
    // the first tick the tile is looked at again.
    private readonly Dictionary<int, TileCoord> _objectViewTiles = new();
    private bool _cullActive;
    // The FIRST render of a world builds every object view, frozen hexes
    // included — "frozen as last seen" needs a last-seen picture, and a bald
    // island outside the camps would be a hole, not a memory. After that
    // build, an object spawned on a frozen hex stays invisible until seen.
    private bool _cullInitialBuildDone;

    // PERF: пространственный индекс для SnapshotMovementRoute — ключуется
    // штампом Blocked из снапшота, живёт жизнь мира.
    private readonly SnapshotMovementRoute.RouteIndex _routeIndex = new();

    /// <summary>The hexes currently outside every colonist's perception —
    /// frozen as last seen. Exposed for the future "memory" restyle pass.</summary>
    public IReadOnlyCollection<TileCoord> CullFrozenTiles => _cullFrozenTiles;

    // §125.5: кого прячет туман среди ЛЮДЕЙ. Пусто, когда режим «выбранная»
    // выключен: без выбранной прятать людей не от чьего лица.
    private readonly HashSet<int> _fogHiddenNpcs = new();
    private bool _fogHidesNpcs;
    // Bug #146: relation-card selection may point at a hidden outsider. The
    // selection still opens her card, but only a living colonist may become
    // the selected-only observer whose knowledge reveals the world.
    private int _fogObserverNpcId = -1;

    // §125.5: подсвеченная граница радиуса выбранной — «докуда она видит».
    // B0: кольцо тоже красится подменой на общий тонированный материал (MPB
    // ломал SRP-батчинг); исходный массив sharedMaterials хранится и
    // возвращается при снятии.
    // Unity API cannot run while this MonoBehaviour's static fields initialize.
    private static int FogRingBaseColor;
    private static int FogRingLegacyColor;
    private static bool _fogRingShaderIdsReady;
    private readonly Dictionary<Renderer, Material[]> _fogRingSaved = new();
    private readonly List<TileCoord> _fogRingTiles = new();
    private TileCoord _fogRingCenter = TileCoord.Zero;
    private int _fogRingRadius = -1;

    // §28.15C v4: СВЕЖИЕ ТЕЛА. Ключ — id самой погибшей. Реестр живёт ровно
    // двое игровых суток или до ножа; после истлевания его заменяет лёгкий
    // ground-sprite в обычном _objectViews.
    private readonly Dictionary<int, GameObject> _corpseViews = new();
    private readonly Dictionary<int, NpcActorView> _corpseActorViews = new();
    private readonly HashSet<int> _liveCorpseIds = new();
    private readonly List<int> _staleCorpseKeys = new();
    private readonly Dictionary<int, GameObject> _npcViews = new();

    // Spec 31B.5: actor-backed views (Marta/Molly/Jana bodies); primitive
    // capsules remain the fallback when no actor prefab matches.
    private readonly Dictionary<int, NpcActorView> _actorViews = new();
    private readonly Dictionary<int, RomancePairView> _romancePairs = new();
    private readonly HashSet<int> _activeRomancePairs = new();
    private readonly List<int> _staleRomancePairs = new();

    /// <summary>
    /// Построены ли уже тела для всех этих колонисток. Нужно экрану загрузки:
    /// выбрать «первую» можно только когда она есть, а с переходом на
    /// Из атомарного кэша её одежда и причёска приезжают не мгновенно. Раньше выбор
    /// успевал сработать по счастливой случайности — теперь его надо дождаться.
    /// </summary>
    public bool ActorsReady(IEnumerable<int> ids)
    {
        var snapshot = _lastSnapshot ?? (_runner != null && _runner.IsReady
            ? _runner.CreateSnapshot()
            : null);
        if (snapshot == null)
        {
            return false;
        }

        foreach (var id in ids)
        {
            if (!_actorViews.TryGetValue(id, out var view) || view == null)
            {
                return false;
            }

            NpcSnapshot npc = null;
            for (var i = 0; i < snapshot.Npcs.Count; i++)
            {
                if (snapshot.Npcs[i].Id.Value == id)
                {
                    npc = snapshot.Npcs[i];
                    break;
                }
            }

            if (npc == null)
            {
                return false;
            }

            // A paused world does not produce another snapshot tick. Content
            // can therefore finish after the one RenderSnapshot call which
            // created the actor. Re-apply the cheap idempotent wardrobe sync
            // here so readiness means "stitched onto the body", not merely
            // "the AssetBundle callback ran".
            view.SyncWorn(npc.WornItems);
            if (!view.IsPresentationReady(
                    npc.WornItems,
                    npc.SeveredParts,
                    npc.BodyPartConditions))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Кто именно из этих колонисток ещё не готова показаться — и почему.
    /// Пустая строка = все готовы. Экран загрузки печатает это, когда ждёт
    /// подозрительно долго: «шторка не поднялась» без имени виноватого —
    /// симптом, на который уже трижды отвечали правдоподобной догадкой
    /// (§109.16), а на живом мире сервера ждать можно и вечно.
    /// </summary>
    public string DescribeActorsNotReady(IEnumerable<int> ids)
    {
        var snapshot = _lastSnapshot ?? (_runner != null && _runner.IsReady
            ? _runner.CreateSnapshot()
            : null);
        if (snapshot == null)
        {
            return "снимка мира ещё нет";
        }

        var parts = new List<string>();
        foreach (var id in ids)
        {
            if (!_actorViews.TryGetValue(id, out var view) || view == null)
            {
                parts.Add(_corpseActorViews.ContainsKey(id)
                    ? $"NPC#{id}: умерла и уехала в трупы"
                    : $"NPC#{id}: вида нет");
                continue;
            }

            NpcSnapshot npc = null;
            for (var i = 0; i < snapshot.Npcs.Count; i++)
            {
                if (snapshot.Npcs[i].Id.Value == id)
                {
                    npc = snapshot.Npcs[i];
                    break;
                }
            }

            if (npc == null)
            {
                parts.Add($"NPC#{id}: пропала из снимка");
                continue;
            }

            var why = view.DescribePresentationBlocker(
                npc.WornItems, npc.SeveredParts, npc.BodyPartConditions);
            if (why.Length > 0)
            {
                parts.Add($"NPC#{id} {npc.DisplayName}: {why}");
            }
        }

        return string.Join("; ", parts);
    }

    /// <summary>Живые id из переданных — те, кто ещё есть в текущем снимке.
    /// На сервере мир не ждёт загрузчика: пока он греет панели, колонистка
    /// может умереть, и её вид переезжает из живого реестра в трупный. Ждать
    /// её готовности после этого можно вечно.</summary>
    public void KeepLiveNpcIds(List<int> ids)
    {
        var snapshot = _lastSnapshot ?? (_runner != null && _runner.IsReady
            ? _runner.CreateSnapshot()
            : null);
        if (snapshot == null)
        {
            return;
        }

        for (var i = ids.Count - 1; i >= 0; i--)
        {
            var alive = false;
            for (var j = 0; j < snapshot.Npcs.Count; j++)
            {
                if (snapshot.Npcs[j].Id.Value == ids[i])
                {
                    alive = true;
                    break;
                }
            }

            if (!alive)
            {
                Debug.Log($"[Loading] NPC#{ids[i]} исчезла из мира, пока грелась " +
                    "презентация — снята с ожидания.");
                ids.RemoveAt(i);
            }
        }
    }

    // Spec 28.15E: the last talk-outcome tick popped per NPC, so the "+/-"
    // relationship glyph fires exactly once when a fresh outcome arrives.
    private readonly Dictionary<int, int> _lastTalkResultTick = new();
    private readonly Dictionary<int, string> _lastSocialCueKey = new();
    private readonly Dictionary<int, string> _pendingSocialCueItemKey = new();

    // §80: снимки лиц. Ставится бутстрапом; без него всё работает по-старому,
    // на эмодзи, — поэтому все обращения через ?. и без проверок у вызывающих.
    private HexLive.UnityPresentation.UI.NpcPortraitCache? _portraitCache;
    private readonly List<int> _portraitIds = new();

    // Spec 31C: the fauna is finally visible.
    private readonly Dictionary<int, GameObject> _mobViews = new();
    private readonly Dictionary<int, GameObject> _crabViews = new();
    private readonly Dictionary<int, Pose> _prevAnimalPoses = new();
    private readonly Dictionary<int, Pose> _currAnimalPoses = new();
    private const float ActorSourceHeightMeters = 1.7f;

    /// <summary>§71.5: the scale colonist bodies are rendered at. The locomotion
    /// tuning scene has to spawn hers at EXACTLY this: a stride is calibrated in
    /// body heights per second, so a differently-sized body calibrates a
    /// different number and the game would then slide by the ratio.</summary>
    public static float ActorScale =>
        Spatial.SimulationUnityMapper.HexRadius * NpcHeightFactor * 2.4f / ActorSourceHeightMeters;

    private Transform? _tilesRoot;
    private Transform? _junctionsRoot;
    private Transform? _objectsRoot;
    private Transform? _npcsRoot;
    private int _lastRenderedTick = -1;

    /// <summary>
    /// §155.5/§41.4: принудительный повторный проход синка на ТОМ ЖЕ тике.
    /// Под шторкой сим стоит на паузе (тик 0), а RenderSnapshot выполняется
    /// только на новый тик: первый проход заказывал тела асинхронно, второго
    /// прохода не наступало никогда — «NPC#1: вида нет» до перезапуска.
    /// WaitForActors дёргает это, пока актрисы не собраны.
    /// </summary>
    public void RequestActorBuildPass() => _lastRenderedTick = -1;
    // Bug #287: личность мира, а не только его часы — реконнект к НОВОМУ миру
    // с тем же адресом сервера различим лишь по seed (тик мог и не прыгнуть).
    private int _lastWorldSeed;

    private WorldSnapshot? _lastSnapshot;

    // §150: distant overview keeps every authored world object and suppresses
    // only decorative grass. forceRenderingOff is orthogonal to Renderer.enabled.
    private bool _overviewGrassHidden;
    private readonly HashSet<Renderer> _overviewGrassRenderers = new();
    private readonly Dictionary<Renderer, bool> _overviewSavedForceRenderingOff = new();
    private readonly List<Renderer> _overviewRendererScratch = new(64);

    // §150.4: world-space импосторы дальнего обзора. Флора и лежащие вещи на
    // отдалении заменяются квадами в тех же мировых якорях; люди — портретными
    // дисками. Список компактится лениво (виды умирают вместе с объектами).
    private bool _overviewImpostorsActive;
    private readonly List<Views.ObjectImpostor> _impostors = new();
    private readonly Dictionary<int, Views.ObjectImpostor> _npcImpostors = new();
    private Quaternion _impostorFacing = Quaternion.identity;
    private bool _impostorFacingValid;

    private readonly struct Pose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public Pose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    // Ticks of gap beyond which two snapshots are treated as unrelated rather
    // than as consecutive states to interpolate between. A few ticks is ordinary
    // fast-forward; a dozen means the world moved on without us.
    private const int PoseSnapTicks = 12;

    // §109.12 / bug #53: a non-walking simulation relocation is a teleport,
    // not a quarter-second glide in the currently active body pose.
    private const float TeleportSnapWorldUnits = 0.25f;

    private readonly Dictionary<int, Pose> _prevNpcPoses = new();
    private readonly Dictionary<int, Pose> _currNpcPoses = new();
    // §71.10: sparse snapshots can skip a junction bend. When the direct
    // chord intersects blocked topology, retain the legal graph polyline so
    // presentation follows the same doorway as the simulation.
    private readonly Dictionary<int, List<Float2>> _npcMovementRoutes = new();

    // §40.18-B: NPCs currently standing on a water tile ride the live wave
    // swell every render frame (WaterWave), so their Y stays glued to the same
    // surface the shader draws — no static bob coefficients needed.
    private readonly Dictionary<int, bool> _npcOnWater = new();
    private readonly Dictionary<int, Vector3> _prevObjectPositions = new();
    private readonly Dictionary<int, Vector3> _currObjectPositions = new();

    // §35.5B: drying-rack hanging — the junctions occupied by racks this
    // snapshot, and each hung garment's hanger rank (object id → slot index).
    private readonly HashSet<JunctionId> _rackJunctions = new();

    // §133: wardrobe junctions and their furniture yaw. Hung garments share
    // both the wardrobe's local slot coordinates and its six-way rotation.
    private readonly HashSet<JunctionId> _wardrobeJunctions = new();
    private readonly Dictionary<JunctionId, float> _wardrobeYawByJunction = new();
    // §54.15: junctions occupied by water collectors — a tool.bottle sharing
    // one is PARKED in the vessel slot (drawn on the stone stand, not scattered).
    private readonly HashSet<JunctionId> _collectorJunctions = new();
    private readonly Dictionary<int, int> _rackHangRank = new();
    private readonly List<ObjectSnapshot> _rackHangScratch = new();
    private readonly Dictionary<JunctionId, int> _rackRankScratch = new();

    private float HexRadius => SimulationUnityMapper.HexRadius;

    private float JunctionMarkerScale => HexRadius * JunctionMarkerScaleFactor;

    public void SetRunner(SimulationRunnerBehaviour runner)
    {
        _runner = runner;
    }

    // The NPC's current interpolated world position (feet), so the orbit
    // camera can follow the smooth visual position instead of the raw,
    // tick-stepped simulation position.
    public bool TryGetNpcViewPosition(int npcId, out Vector3 position)
    {
        if ((_npcViews.TryGetValue(npcId, out var view) ||
             _corpseViews.TryGetValue(npcId, out view)) && view != null)
        {
            position = view.transform.position;
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    // Bug #146: UI may keep a hidden relation target selected so its card can
    // stay open. Camera code must ask the same authoritative fog state before
    // using the still-alive (but inactive) actor transform as a framing target.
    public bool IsNpcHiddenByFog(int npcId) =>
        _fogActive && _fogHidesNpcs && _fogHiddenNpcs.Contains(npcId);

    // §67.10: the actor itself, so a sim event (death cry, "it's built!") can
    // be spoken by the right mouth instead of a disembodied sfx.
    public bool TryGetActorView(int npcId, out NpcActorView view)
    {
        if ((_actorViews.TryGetValue(npcId, out var found) ||
             _corpseActorViews.TryGetValue(npcId, out found)) && found != null)
        {
            view = found;
            return true;
        }

        view = null;
        return false;
    }

    // Orbit pivot: the pose-aware visual center of the NPC's body (chest when
    // standing, following the body down when sitting/lying).
    public bool TryGetNpcBodyCenter(int npcId, out Vector3 center)
    {
        if ((_actorViews.TryGetValue(npcId, out var actorView) ||
             _corpseActorViews.TryGetValue(npcId, out actorView)) && actorView != null)
        {
            return actorView.TryGetBodyCenter(out center);
        }

        if ((_npcViews.TryGetValue(npcId, out var view) ||
             _corpseViews.TryGetValue(npcId, out view)) && view != null)
        {
            center = view.transform.position + Vector3.up * (HexRadius * NpcHeightFactor);
            return true;
        }

        center = Vector3.zero;
        return false;
    }

    // Spec §67: mob position for wolf growl/bite/death sounds — the system
    // wolf events carry only the mob id.
    public bool TryGetMobViewPosition(int mobId, out Vector3 position)
    {
        if (_mobViews.TryGetValue(mobId, out var view) && view != null)
        {
            position = view.transform.position;
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    public bool TryGetAnimalViewPosition(int id, bool crab, out Vector3 position)
    {
        var views = crab ? _crabViews : _mobViews;
        if (views.TryGetValue(id, out var view) && view != null)
        {
            position = view.transform.position;
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    // Spec §67.3: the surf emitter slides along the waterline — nearest sand
    // tile to the camera. Sand IS the shore by construction (spec 31C.4: land
    // within one hex of water), so the tile-build loop collects the points.
    public bool TryGetNearestShorePoint(Vector3 near, out Vector3 point)
    {
        point = Vector3.zero;
        var bestSq = float.MaxValue;
        foreach (var shore in _shorePoints)
        {
            var dx = shore.x - near.x;
            var dz = shore.z - near.z;
            var sq = dx * dx + dz * dz;
            if (sq < bestSq)
            {
                bestSq = sq;
                point = shore;
            }
        }

        return bestSq < float.MaxValue;
    }

    private readonly List<Vector3> _shorePoints = new();
    private readonly HashSet<TileCoord> _sandCoords = new();

    // Face anchor of the LIVE in-world character (real dirt/tan/clothes) for
    // the portrait camera. Falls back to the primitive view's head sphere.
    // §80: кэш лиц. Необязателен — без него пузыри рисуют прежние эмодзи.
    public void SetPortraitCache(HexLive.UnityPresentation.UI.NpcPortraitCache cache)
    {
        _portraitCache = cache;
    }

    public bool TryGetNpcFace(
        int npcId, out Vector3 faceCenter, out Vector3 faceForward, out Vector3 faceUp, out float scale)
    {
        if (_actorViews.TryGetValue(npcId, out var actorView) && actorView != null)
        {
            return actorView.TryGetFace(out faceCenter, out faceForward, out faceUp, out scale);
        }

        if (_npcViews.TryGetValue(npcId, out var view) && view != null)
        {
            var bodyHeight = HexRadius * NpcHeightFactor;
            faceCenter = view.transform.position + Vector3.up * (bodyHeight * 2.2f);
            faceForward = view.transform.forward;
            faceUp = view.transform.up;
            scale = bodyHeight * 2.4f / 1.7f;
            return true;
        }

        faceCenter = Vector3.zero;
        faceForward = Vector3.forward;
        faceUp = Vector3.up;
        scale = 1f;
        return false;
    }

    // §80 r2: можно ли снимать её прямо сейчас. Без актёрского вью (примитивный
    // вид на дальнем плане) позировать некому — снимок пропускаем.
    public bool IsNpcPhotogenic(int npcId)
    {
        // §80 r3: сверх фотогеничности — ещё и СТОИТ. Съёмка на ходу давала
        // разные позы у разных колонисток; ждать нечего, кадр раз в сутки.
        // §130 (взгляд в игровую камеру) спрашивает IsPhotogenic напрямую и
        // этой добавки не видит: там ходьба помехой не является.
        return _actorViews.TryGetValue(npcId, out var actorView) && actorView != null &&
               actorView.IsPhotogenic && actorView.IsPortraitPoseSettled;
    }

    // §130: ближайшее к объективу лицо живой NPC, которой сейчас уместно
    // посмотреть в камеру (не лежит, не плывёт, не в бою, не рыдает, не
    // ragdoll). Трупы не кандидаты по построению — только _actorViews.
    public bool TryGetNearestCameraGazeCandidate(
        Vector3 near, out int npcId, out NpcActorView? view, out Vector3 faceCenter)
    {
        npcId = -1;
        view = null;
        faceCenter = Vector3.zero;
        var bestSq = float.MaxValue;
        foreach (var pair in _actorViews)
        {
            var actor = pair.Value;
            if (actor == null || !actor.IsCameraGazeEligible ||
                !actor.TryGetFace(out var center, out _, out _, out _))
            {
                continue;
            }

            var sq = (center - near).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                npcId = pair.Key;
                view = actor;
                faceCenter = center;
            }
        }

        return npcId >= 0;
    }

    // §80: на время съёмки портрета отдать взгляд камере. Возвращает false,
    // если тела нет или оно не в состоянии позировать (ragdoll, кома).
    public bool TryBeginPortraitGaze(int npcId, Vector3 eyeWorldPos)
    {
        return _actorViews.TryGetValue(npcId, out var actorView) && actorView != null &&
               actorView.BeginPortraitGaze(eyeWorldPos);
    }

    public void EndPortraitGaze(int npcId)
    {
        if (_actorViews.TryGetValue(npcId, out var actorView) && actorView != null)
        {
            actorView.EndPortraitGaze();
        }
    }

    private void Awake()
    {
        EnsureFogRingShaderIds();
    }

    private void OnDisable()
    {
        SetOverviewGrassHidden(false);
        SetOverviewImpostorsActive(false);
    }

    private void Update()
    {
        // While offline ticks wind forward, stay dark: winding is pure headless
        // simulation and painting each intermediate world (skin decals, actor
        // sync) behind the loading curtain only starves the tick budget. Views
        // build once, afterward, in loading phase 3.
        if (HexLive.UnityPresentation.UI.LoadingScreen.IsReplaying)
        {
            return;
        }

        UpdateVegetationWind();

        _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
        if (_runner is null || !_runner.IsReady)
        {
            return;
        }

        UnityEngine.Profiling.Profiler.BeginSample("Hex.CreateSnapshot");
        var snapshot = _runner.CreateSnapshot();
        UnityEngine.Profiling.Profiler.EndSample();
        if (snapshot is null)
        {
            return;
        }

        EnsureRoots();

        if (snapshot.Tick != _lastRenderedTick)
        {
            // A jump this big is not one tick of movement — it is a reconnect, a
            // save restored, or a fast-forward. Interpolating across it would lerp
            // every colonist in a straight line over the island. Drop the previous
            // poses so this frame SNAPS instead.
            if (_lastRenderedTick >= 0 &&
                (snapshot.Seed != _lastWorldSeed ||
                 snapshot.Tick < _lastRenderedTick ||
                 snapshot.Tick - _lastRenderedTick > PoseSnapTicks))
            {
                _prevNpcPoses.Clear();
                _currNpcPoses.Clear();
                _npcMovementRoutes.Clear();
                _prevObjectPositions.Clear();
                _currObjectPositions.Clear();
                // Animals too — a wolf lerping across the island after a restore
                // is the same wrongness as a colonist doing it.
                _prevAnimalPoses.Clear();
                _currAnimalPoses.Clear();

                // The junction lookup is cached for the life of a WORLD (see
                // RebuildJunctionLookup). This branch is exactly "the world
                // under us is not the one we cached" — a restore, a reconnect,
                // a new island — so drop it and let the next line rebuild it.
                // Without this a same-size island would keep the old positions.
                _junctionPositions.Clear();

                // New world: the next RenderSnapshot must build every object
                // view once before the memory fog may freeze anything.
                _cullInitialBuildDone = false;
                _objectViewTiles.Clear();

                // §148 / bug #287: карта памяти тумана — ЗЕРКАЛО снапшота, а
                // не биография вида. Мир сменился (другой seed или часы пошли
                // назад) — забыть всё; иначе новая колония наследует
                // разведанность предыдущего мира: провод честно присылает
                // пустой Explored, но _everSeenTiles только копил Add и
                // никогда не чистился. RebuildPerceptionCulling в этом же
                // кадре наполнит множество заново из снапшота.
                _everSeenTiles.Clear();
                _cullVisibleTiles.Clear();
                _cullFrozenTiles.Clear();
                // §148.3: «?» на местах чужаков прошлого мира тоже забыть.
                _lastSeenNpcTiles.Clear();
                // Перекрасить memory-shade с нуля (оригиналы в _tileShadeSaved).
                _tileShadeApplied.Clear();
            }

            UnityEngine.Profiling.Profiler.BeginSample("Hex.RenderSnapshot");
            RenderSnapshot(snapshot);
            UnityEngine.Profiling.Profiler.EndSample();
            _lastRenderedTick = snapshot.Tick;
            _lastWorldSeed = snapshot.Seed;
            _lastSnapshot = snapshot;
        }

        UnityEngine.Profiling.Profiler.BeginSample("Hex.PerFrame");
        UpdateRain(snapshot.IsRaining);
        UpdateEnvironmentWetness(snapshot.IsRaining);
        // §40.18-B: our owned wave params -> the water material, so the mesh
        // swell always matches what WaterWave.Height gives the swimmer.
        WaterWave.PushToShader();
        InterpolateMovables(_runner.TickAlpha);
        UpdateHutCutaways(snapshot);
        UnityEngine.Profiling.Profiler.EndSample();
    }

    private static void EnsureFogRingShaderIds()
    {
        if (_fogRingShaderIdsReady)
        {
            return;
        }

        FogRingBaseColor = Shader.PropertyToID("_BaseColor");
        FogRingLegacyColor = Shader.PropertyToID("_Color");
        _fogRingShaderIdsReady = true;
    }

    private void UpdateHutCutaways(WorldSnapshot snapshot)
    {
        // §120: a house raised from a player plan has no HutAssembly at all —
        // it IS its 51 modules — so the same feature has to be able to run with
        // _hutCutawayViews empty.
        if (_hutCutawayViews.Count == 0 && _planCutawayModules.Count == 0) return;

        _cutawayCamera ??= Camera.main;
        var hasInteriorSelection = false;
        var selectedTile = TileCoord.Zero;
        if (Input.NpcSelection.HasSelection)
        {
            var selectedId = Input.NpcSelection.SelectedId;
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value != selectedId) continue;
                // §141: «она в доме» — про ТЕЛО, а не про поле тайла. Дом
                // раскрывался не в тот момент, когда она входит: поле
                // догоняет тело, и на пороге получалось «и в доме, и не в
                // доме» — при том, что высоту вид брал оттуда же.
                selectedTile = GroundTileUnder(npc);
                hasInteriorSelection = _indoorCoords.Contains(selectedTile);
                break;
            }
        }

        var cameraPosition = _cutawayCamera != null
            ? _cutawayCamera.transform.position
            : Vector3.zero;
        foreach (var hut in _hutCutawayViews)
        {
            var reveal = hasInteriorSelection && hut.Tile.Equals(selectedTile) &&
                _cutawayCamera != null;
            hut.Assembly.SetInteriorCutaway(reveal, cameraPosition);
        }

        UpdatePlanCutaways(hasInteriorSelection, selectedTile, cameraPosition);
    }

    /// <summary>
    /// §120: the monolith's interior cutaway, for a house that has no monolith.
    ///
    /// <para>
    /// Same trigger as <see cref="UpdateHutCutaways"/>'s own loop — a selected
    /// colonist standing indoors — but "this house" is the FOOTPRINT, because a
    /// plan spans several hexes and the selected tile is only ever one of them.
    /// Same removal, too: the modules that stand between the camera and the room
    /// go to <c>ShadowsOnly</c> and the roof comes off whole, which is exactly
    /// what <c>HutAssembly.SetCutawayRenderers</c> does with its roof stage and
    /// the wall pair it picks.
    /// </para>
    /// </summary>
    private void UpdatePlanCutaways(
        bool hasInteriorSelection, TileCoord selectedTile, Vector3 cameraPosition)
    {
        if (_planCutawayModules.Count == 0) return;
        var hasCamera = _cutawayCamera != null;

        foreach (var ownerKey in _planRaisedOwners)
        {
            // Which modules are this building's, in snapshot order.
            var owned = _planCutawayIndexScratch;
            owned.Clear();
            for (var i = 0; i < _planCutawayModules.Count; i++)
            {
                if (_planCutawayModules[i].OwnerId == ownerKey) owned.Add(i);
            }

            if (owned.Count == 0) continue;

            var reveal = hasInteriorSelection && hasCamera &&
                _planFootprints.TryGetValue(ownerKey, out var footprint) &&
                Covers(footprint, selectedTile);
            if (!reveal)
            {
                for (var i = 0; i < owned.Count; i++)
                {
                    var view = _planCutawayModules[owned[i]].View;
                    if (view != null) view.SetCutawayHidden(false);
                }

                continue;
            }

            // The envelope is ranked; the roof comes off whole either way.
            var positions = _planCutawayPositionScratch;
            positions.Clear();
            for (var i = 0; i < owned.Count; i++)
            {
                if (_planCutawayModules[owned[i]].Roof) continue;
                positions.Add(_planCutawayModules[owned[i]].Position);
            }

            var centre = _planOwnerAnchors.TryGetValue(ownerKey, out var anchor)
                ? new Vector2(anchor.X, anchor.Y)
                : Vector2.zero;
            HexLive.UnityPresentation.Environment.ArchitectureCutaway.RankByCameraFacing(
                positions, centre,
                new Vector2(cameraPosition.x, cameraPosition.z),
                _planCutawayHiddenScratch);

            var envelopeIndex = 0;
            for (var i = 0; i < owned.Count; i++)
            {
                var module = _planCutawayModules[owned[i]];
                bool hidden;
                if (module.Roof)
                {
                    hidden = true;
                }
                else
                {
                    hidden = _planCutawayHiddenScratch.Contains(envelopeIndex);
                    envelopeIndex++;
                }

                if (module.View != null) module.View.SetCutawayHidden(hidden);
            }
        }
    }

    private static bool Covers(IReadOnlyList<TileCoord> footprint, TileCoord tile)
    {
        for (var i = 0; i < footprint.Count; i++)
        {
            if (footprint[i].Equals(tile)) return true;
        }

        return false;
    }

    /// <summary>
    /// §120: remembers one module the plan cutaway may take away, with the world
    /// point it is scored by. A floor sector is never collected — you look down
    /// on the deck, never through it — which mirrors the monolith, whose scan
    /// only ever indexes its roof stage, its wall bays and their posts.
    /// </summary>
    private void CollectPlanCutawayModule(
        ObjectSnapshot piece,
        HexLive.UnityPresentation.Environment.ArchitectureModuleView view)
    {
        if (piece.ArchitectureOwnerObjectId is not { } ownerKey) return;
        if (!_planRaisedOwners.Contains(ownerKey)) return;
        if (piece.ArchitectureElements.Count != 1) return;
        var element = piece.ArchitectureElements[0];
        var roof = element.DefinitionId.StartsWith(
            "architecture.roof.", System.StringComparison.Ordinal);
        if (!roof && System.Array.IndexOf(CutawayEnvelopeIds, element.DefinitionId) < 0) return;
        if (!_planOwnerAnchors.TryGetValue(ownerKey, out var anchor)) return;

        // The module's own point: its local offset turned by the building's yaw
        // about the anchor. Simulation yaw is CCW about +X, which is the plane
        // the offset lives in — the same conversion CollectArchitectureFloorTile
        // makes for the same numbers.
        var radians = piece.RotationDegrees * Mathf.Deg2Rad;
        var cos = Mathf.Cos(radians);
        var sin = Mathf.Sin(radians);
        var x = anchor.X + element.LocalX * cos - element.LocalZ * sin;
        var z = anchor.Y + element.LocalX * sin + element.LocalZ * cos;
        _planCutawayModules.Add(new PlanCutawayModule(ownerKey, roof, new Vector2(x, z), view));
    }

    /// <summary>
    /// The full-height pieces a cutaway may take away. Corner supports belong
    /// here for the reason the constructor preview learned: they are full-height
    /// posts, and leaving them out left one standing in front of the colonist
    /// the player had just selected.
    /// </summary>
    private static readonly string[] CutawayEnvelopeIds =
    {
        "architecture.wall.wood", "architecture.window.wood", "architecture.door.wood",
        "architecture.support.wood"
    };

    // Bug #321: сколько видов памяти тумана строится за один снапшот-пасс —
    // большая разведка после загрузки раскатывается за несколько секунд, а не
    // одним фризом кадра.
    private const int FrozenViewBudget = 64;

    // Spec 40.2-B: ground blood stains manager (lazy — lives under the
    // renderer, cleared with it on scene teardown).
    private const int CorpseBleedTicks = 120; // 30 sim seconds after death
    private HexLive.UnityPresentation.Environment.GroundBloodStains _bloodStains;

    private HexLive.UnityPresentation.Environment.GroundBloodStains EnsureBloodStains()
    {
        if (_bloodStains == null)
        {
            var go = new GameObject("BloodStains");
            go.transform.SetParent(transform, false);
            _bloodStains = go.AddComponent<HexLive.UnityPresentation.Environment.GroundBloodStains>();
        }

        return _bloodStains;
    }

    // §127: the completion splat is independent from the washable projector
    // on the woman's skin. It stays at the scene location on the terrain.
    private HexLive.UnityPresentation.Environment.GroundIntimacyStains? _intimacyStains;

    private HexLive.UnityPresentation.Environment.GroundIntimacyStains EnsureIntimacyStains()
    {
        if (_intimacyStains == null)
        {
            var go = new GameObject("IntimacyGroundStains");
            go.transform.SetParent(transform, false);
            _intimacyStains =
                go.AddComponent<HexLive.UnityPresentation.Environment.GroundIntimacyStains>();
        }

        return _intimacyStains;
    }

    // Spec 40.2-C: blood spilled while in the water billows on the surface
    // instead of pooling on the ground (see WaterBloodStains).
    private HexLive.UnityPresentation.Environment.WaterBloodStains _waterBlood;

    private HexLive.UnityPresentation.Environment.WaterBloodStains EnsureWaterBlood()
    {
        if (_waterBlood == null)
        {
            var go = new GameObject("WaterBloodStains");
            go.transform.SetParent(transform, false);
            _waterBlood = go.AddComponent<HexLive.UnityPresentation.Environment.WaterBloodStains>();
        }

        return _waterBlood;
    }

    // Spec 33.2 (iter 33): cartoon rain — a shower of little droplet particles
    // over the island whenever the weather says it's raining.
    private ParticleSystem _rain;
    private ParticleSystem _rainSplash;
    private ParticleSystem.Particle[] _rainParticles = Array.Empty<ParticleSystem.Particle>();

    private void UpdateRain(bool raining)
    {
        if (_rain == null)
        {
            var go = new GameObject("Rain");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 30f, 0f);
            // Particles emit along local +Z — aim it straight DOWN, or the
            // "rain" sprays sideways 30 units above the island (the bug that
            // made rain invisible in play).
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _rain = go.AddComponent<ParticleSystem>();
            _rain.Stop();

            var main = _rain.main;
            main.startSpeed = 22f;
            // Streak width comes from startSize in stretch mode. Bug #298:
            // укрупнение штриха до 8-14 см читалось игроком как «огромный
            // дождь» — размер возвращён к исходным тонким каплям, а
            // различимость с игровой камеры (30-50 wu) несёт только альфа
            // (0.55 -> 0.8): при ней тонкий штрих уже не суб-пиксельно бледный.
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.06f);
            main.startLifetime = 2.2f;
            main.startColor = new Color(0.65f, 0.78f, 0.95f, 0.8f);
            main.maxParticles = 9000;
            main.gravityModifier = 1.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            // Bug #301: эмиттер висит в 30 wu над землёй, и при близкой камере
            // его баунды вне кадра — Automatic culling СТАВИТ систему на паузу,
            // капли не симулируются вовсе («дождь появился только когда
            // открутил камеру»). Дождь обязан идти независимо от фрустума.
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            var emission = _rain.emission;
            emission.rateOverTime = 2600f; // lots of small drops

            var shape = _rain.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(60f, 60f, 1f); // XY box, perpendicular to fall

            // Bug #257: an infinite collision plane can represent only one
            // elevation band. Resolve every drop against the actual hex below
            // it instead; ResolveRainGroundContacts also follows the animated
            // water surface. The tiny splash remains the authored system, but
            // is emitted manually at that exact contact point.
            _rainSplash = CreateSplashSystem(go.transform);
            _rainParticles = new ParticleSystem.Particle[main.maxParticles];

            var renderer = _rain.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.velocityScale = 0.018f;
                renderer.lengthScale = 5f;
                renderer.sharedMaterial = GetRainMaterial();
            }
        }

        // Bug #189: BigIsland is about 256×127 wu, while this deliberately
        // dense emitter is only 60×60 wu. Keeping that volume at world origin
        // made rain visible over the old island centre and nowhere near a camp
        // at the expanded edges. Follow the viewing camera instead of inflating
        // one island-wide particle box (which would multiply live particles by
        // roughly nine). Existing particles stay in world space, so moving the
        // emitter only changes where NEW drops are born and never drags rain
        // sideways across the terrain.
        if (_cutawayCamera == null) _cutawayCamera = Camera.main;
        var focus = _cutawayCamera != null
            ? _cutawayCamera.transform.position
            : transform.position;
        var emitterY = transform.TransformPoint(new Vector3(0f, 30f, 0f)).y;
        _rain.transform.position = new Vector3(focus.x, emitterY, focus.z);

        if (raining && !_rain.isPlaying)
        {
            _rain.Play();
            // Диагностика «дождь идёт, а капель не видно»: строка в Player.log
            // отделяет «система не играет» от «играет, но не различима».
            Debug.Log($"[Rain] particles playing (emitter y={_rain.transform.position.y:F1}, " +
                      $"cam={(_cutawayCamera != null ? _cutawayCamera.transform.position.ToString() : "none")})");
        }
        else if (!raining && _rain.isPlaying)
        {
            _rain.Stop();
            Debug.Log("[Rain] particles stopped");
        }

        if (_rain.isPlaying)
        {
            ResolveRainGroundContacts();
        }
    }

    private void ResolveRainGroundContacts()
    {
        if (_rain == null || _rainSplash == null || _rainParticles.Length == 0)
        {
            return;
        }

        var count = _rain.GetParticles(_rainParticles);
        var changed = false;
        for (var i = 0; i < count; i++)
        {
            var particle = _rainParticles[i];
            var position = particle.position;
            var tile = HexSpatialMath.WorldToTile(new Float2(position.x, position.z));
            if (!_tileElevations.ContainsKey(tile))
            {
                continue;
            }

            var surfaceY = GroundY(tile);
            if (_waterCoords.Contains(tile))
            {
                surfaceY += ElevationStep * SwimVisuals.SurfaceStepOffset;
                surfaceY += WaterWave.HeightNow(position.x, position.z);
            }
            if (position.y > surfaceY + 0.03f)
            {
                continue;
            }

            EmitRainSplash(new Vector3(position.x, surfaceY + 0.03f, position.z),
                particle.randomSeed);
            particle.remainingLifetime = 0f;
            _rainParticles[i] = particle;
            changed = true;
        }

        if (changed)
        {
            _rain.SetParticles(_rainParticles, count);
        }
    }

    private void EmitRainSplash(Vector3 position, uint seed)
    {
        for (var i = 0; i < 3; i++)
        {
            var angle = (seed % 6283u) * 0.001f + i * (Mathf.PI * 2f / 3f);
            var speed = 0.6f + ((seed >> (i * 5)) & 31u) / 31f * 1.1f;
            var emit = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = new Vector3(
                    Mathf.Cos(angle) * speed,
                    speed * 0.85f,
                    Mathf.Sin(angle) * speed)
            };
            _rainSplash.Emit(emit, 1);
        }
    }

    // Tiny droplet burst where a raindrop hits the ground. Must live as a
    // child of the rain system (Unity sub-emitter rule); the local rotation
    // cancels the parent's 90° so the hemisphere sprays world-up.
    private ParticleSystem CreateSplashSystem(Transform rainRoot)
    {
        var go = new GameObject("RainSplash");
        go.transform.SetParent(rainRoot, false);
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        var splash = go.AddComponent<ParticleSystem>();
        splash.Stop();

        var main = splash.main;
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.7f);
        // Bug #298: брызги вернулись к прежним 2-5 см вместе с каплями.
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.38f);
        main.startColor = new Color(0.75f, 0.86f, 1f, 0.85f);
        main.maxParticles = 6000;
        main.gravityModifier = 1.4f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        // Bug #301: брызги живут тем же правилом, что и капли — симулировать
        // всегда, иначе пауза за кадром съедает первые всплески.
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

        var emission = splash.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(Array.Empty<ParticleSystem.Burst>());

        var shape = splash.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.02f;

        var renderer = splash.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = GetRainMaterial();
        }

        return splash;
    }

    private static Material _rainMaterial;

    // A particle-capable unlit transparent material — URP Lit renders
    // stretched billboards black/invisible.
    private static Material GetRainMaterial()
    {
        if (_rainMaterial != null)
        {
            return _rainMaterial;
        }

        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            _rainMaterial = CreateWaterMaterial();
            return _rainMaterial;
        }

        var material = new Material(shader);
        material.SetFloat("_Surface", 1f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.color = new Color(0.65f, 0.78f, 0.95f, 0.6f);
        _rainMaterial = material;
        return material;
    }

    // ---- Rain mood: cloud-dimmed light + wet darkened glossy terrain ----

    private float _wetness;                 // 0 dry .. 1 soaked (visual only)
    private Light _sun;
    private bool _sunCached;
    private float _sunBaseIntensity;
    private Color _sunBaseColor;
    private float _ambientBase;
    private float _appliedWetness = -1f;

    private void UpdateEnvironmentWetness(bool raining)
    {
        // Soak fast (~6 s), dry out slowly (~25 s).
        var target = raining ? 1f : 0f;
        var rate = raining ? Time.deltaTime / 6f : Time.deltaTime / 25f;
        _wetness = Mathf.MoveTowards(_wetness, target, rate);

        if (Mathf.Abs(_wetness - _appliedWetness) < 0.004f)
        {
            return;
        }

        _appliedWetness = _wetness;
        ApplyWetness(_wetness);
    }

    private void ApplyWetness(float w)
    {
        // Cloud light: dimmer, cooler, flatter.
        if (!_sunCached)
        {
            _sun = RenderSettings.sun;
            if (_sun == null)
            {
                // Fallback: the scene's directional light that lights the world
                // (skip specials like the portrait stage's dedicated light).
                foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (light.type == LightType.Directional && (light.cullingMask & 1) != 0)
                    {
                        _sun = light;
                        break;
                    }
                }
            }

            if (_sun != null)
            {
                _sunBaseIntensity = _sun.intensity;
                _sunBaseColor = _sun.color;
            }

            _ambientBase = RenderSettings.ambientIntensity;
            _sunCached = true;
        }

        if (_sun != null)
        {
            _sun.intensity = _sunBaseIntensity * Mathf.Lerp(1f, 0.45f, w);
            _sun.color = Color.Lerp(_sunBaseColor, new Color(0.62f, 0.68f, 0.78f), w * 0.6f);
        }

        RenderSettings.ambientIntensity = _ambientBase * Mathf.Lerp(1f, 0.65f, w);

        // Wet ground: darker and glossier. The flat-material cache is shared
        // by every tile, so this touches a few dozen materials, not thousands.
        foreach (var pair in _flatMaterials)
        {
            if (pair.Value == null || !_flatBaseColors.TryGetValue(pair.Key, out var baseColor))
            {
                continue;
            }

            pair.Value.color = baseColor * Mathf.Lerp(1f, 0.68f, w);
            pair.Value.SetFloat("_Smoothness", 0.75f * w);
        }

        if (_grassMaterial != null)
        {
            _grassMaterial.color = _grassBaseColor * Mathf.Lerp(1f, 0.68f, w);
        }
    }

    private GameObject _seaPlane;

    private void EnsureSeaPlane()
    {
        if (_seaPlane != null)
        {
            return;
        }

        // Slightly deeper than the tile water surfaces (−0.05 step) so the
        // infinite sea never z-fights the playable water sheet. Rides the
        // shared shore-anchored surface offset (§31C.4).
        var waterY = SimulationUnityMapper.TileHeight
                   + ElevationStep * (SwimVisuals.SurfaceStepOffset - 0.05f);

        // Spec 20.16: an opaque deep sea floor well below the surface. It gives
        // the stylized water real depth everywhere (so open sea reads deep and
        // foam-free); the only shallow intersections left are the land walls
        // rising through the surface — i.e. foam hugs the shore, nothing else.
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "SeaFloor";
        floor.transform.SetParent(transform, false);
        floor.transform.position = new Vector3(0f, waterY - 1.6f, 0f);
        floor.transform.localScale = new Vector3(40f, 1f, 40f);
        var floorRenderer = floor.GetComponent<MeshRenderer>();
        if (floorRenderer is not null)
        {
            floorRenderer.sharedMaterial = CreateMaterial(new Color(0.06f, 0.14f, 0.26f));
        }

        // Spec 20.16: the ocean surface extends past the playable bounds.
        _seaPlane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _seaPlane.name = "Sea";
        _seaPlane.transform.SetParent(transform, false);
        _seaPlane.transform.position = new Vector3(0f, waterY, 0f);
        _seaPlane.transform.localScale = new Vector3(40f, 1f, 40f); // 400x400 units
        var renderer = _seaPlane.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateWaterMaterial();
        }
    }

    private void EnsureRoots()
    {
        EnsureSeaPlane();
        _tilesRoot ??= CreateRoot("Tiles");
        _junctionsRoot ??= CreateRoot("Junctions");
        _objectsRoot ??= CreateRoot("Objects");
        _npcsRoot ??= CreateRoot("NPCs");
    }

    private Transform CreateRoot(string label)
    {
        var child = transform.Find(label);
        if (child is not null)
        {
            return child;
        }

        var go = new GameObject(label);
        go.transform.SetParent(transform, false);
        return go.transform;
    }

    // One walk of the ~14 000 junctions per WORLD: the position lookup plus the
    // two marker sets that are keyed off immutable junction data. Called from
    // RenderSnapshot only when the junction set changed (new world, restore) or
    // when the debug-marker toggle flipped.
    private void RebuildJunctionLookup(WorldSnapshot snapshot)
    {
        _junctionPositions.Clear();
        foreach (var junction in snapshot.Junctions)
        {
            var key = junction.Id.Value;
            _junctionPositions[key] = junction.WorldPosition;

            if (_showJunctionMarkers && !_junctionViews.ContainsKey(key))
            {
                _junctionViews[key] = CreateJunctionView(junction);
            }

            // Spec 40.17: climb-seam dots — always shown, once per seam.
            if (junction.IsClimbSeam && !_seamMarkers.ContainsKey(key))
            {
                _seamMarkers[key] = CreateSeamMarker(junction);
            }
        }

        _junctionMarkersBuilt = _showJunctionMarkers;
    }

    private void RenderSnapshot(WorldSnapshot snapshot)
    {
        // §152: the live snapshot is also the incremental content working set.
        // Existing ids are cache hits; a newly crafted/looted/spawned owner
        // starts one main+icon bundle load before any panel can reveal it.
        ScenePrewarm.ForSnapshot(snapshot);

        UnityEngine.Profiling.Profiler.BeginSample("Hex.RS.TilesWater");
        // §148: что наш лагерь видит и что помнит — считается ПЕРВЫМ делом:
        // от этого зависит, какие гексы вообще строить.
        RebuildPerceptionCulling(snapshot);

        // Spec 31C.4: the river gets banks — tiles adjacent to water are sand.
        _waterCoords.Clear();
        _swimCoords.Clear();
        _tileElevations.Clear();
        _indoorCoords.Clear();
        _floorTiles.Clear();
        foreach (var tile in snapshot.Tiles)
        {
            _tileElevations[tile.Coord] = tile.Elevation;
            if (tile.Water)
            {
                _waterCoords.Add(tile.Coord);

                // §40.18-B: deep (unwalkable) water is swum, not waded — the
                // actor's root sinks below the surface there.
                if (!tile.Walkable)
                {
                    _swimCoords.Add(tile.Coord);
                }
            }

            if (tile.Indoor)
            {
                _indoorCoords.Add(tile.Coord);
            }
            if (tile.HasFloor)
            {
                _floorTiles.Add(tile.Coord);
            }
        }

        foreach (var tile in snapshot.Tiles)
        {
            // §148: земля появляется, только когда её впервые увидели свои.
            if (!_tileViews.ContainsKey(tile.Coord) && TileDiscovered(tile.Coord))
            {
                var sand = IsSandTile(tile);
                _tileViews[tile.Coord] = CreateTileView(tile, sand);
                if (sand)
                {
                    // §67.3: собранная при постройке тайлов кромка — опорные
                    // точки для 3D-эмиттера прибоя.
                    _shorePoints.Add(SimulationUnityMapper.ToUnityTilePosition(
                        tile.Coord, GroundY(tile.Coord)));
                    _sandCoords.Add(tile.Coord);
                }
            }
        }

        // Spec 20.16: one merged, finely-tessellated water surface for an even
        // seam-free wave across every water hex (see EnsureWaterSurface).
        EnsureWaterSurface(snapshot);

        // PERF: a junction's id, WorldPosition and IsClimbSeam are worldgen
        // output and never move — the exporter refreshes only the mutable flags
        // (WorldSnapshotExporter.RefreshJunctionFlags). So walking ~14 000 of
        // them EVERY tick to rebuild the identical lookup cost ~0.9 MB of
        // garbage per tick for a map only CreateObjectView ever reads. Build it
        // once per world; _junctionPositions is cleared on a world swap (see
        // Update's snap branch), which is what forces the rebuild.
        if (_junctionPositions.Count != snapshot.Junctions.Count ||
            _junctionMarkersBuilt != _showJunctionMarkers)
        {
            RebuildJunctionLookup(snapshot);
        }

        UnityEngine.Profiling.Profiler.EndSample();
        UnityEngine.Profiling.Profiler.BeginSample("Hex.RS.Prep");

        // Snapshot-diff despawn (spec 31.15): destroy views whose object
        // disappeared from the simulation (eaten/picked-up apples).
        var liveObjectIds = _liveObjectIdScratch;
        liveObjectIds.Clear();
        _hutCutawayViews.Clear();
        _planBuildingOwners.Clear();
        _unfinishedPlanBuildingOwners.Clear();
        _planOwnerAnchors.Clear();
        _planRaisedOwners.Clear();
        _planFootprintTiles.Clear();
        _planCutawayModules.Clear();
        _architectureFloorTiles.Clear();
        _planFootprints.Clear();
        foreach (var worldObject in snapshot.Objects)
        {
            liveObjectIds.Add(worldObject.Id.Value);

            // §120: known before any view is created, because whether an
            // architecture piece draws itself depends on it.
            if (worldObject.DefinitionId == ContentIds.HutPlan ||
                worldObject.BuildProduct == ContentIds.HutPlan)
            {
                var ownerKey = worldObject.Id.Value;
                _planBuildingOwners.Add(ownerKey);
                if (worldObject.BuildProduct == ContentIds.HutPlan)
                {
                    _unfinishedPlanBuildingOwners.Add(ownerKey);
                }
                // Every module of this building is placed from THIS point; the
                // module's own junction is not it (see _planOwnerAnchors).
                _planOwnerAnchors[ownerKey] =
                    GetObjectAnchorFromJunctions(worldObject, _junctionPositions);
                var footprint = HexLive.Simulation.Bootstrap.BuildingBootstrap.FootprintTiles(
                    ContentIds.HutPlan, worldObject.Tile, worldObject.RotationDegrees);
                _planFootprints[ownerKey] = footprint;
                // A site is not a house yet: the monolith only cuts away a
                // finished building.hut_1hex, and the furniture of a plan is
                // staked only once the plan is raised.
                if (worldObject.DefinitionId == ContentIds.HutPlan)
                {
                    _planRaisedOwners.Add(ownerKey);
                    for (var i = 0; i < footprint.Count; i++) _planFootprintTiles.Add(footprint[i]);
                }
            }
        }

        var staleObjectKeys = _staleObjectKeyScratch;
        staleObjectKeys.Clear();
        foreach (var key in _objectViews.Keys)
        {
            if (!liveObjectIds.Contains(key))
            {
                // Memory fog: an object that vanished on a FROZEN tile keeps
                // its ghost — nobody saw it go. The first tick its tile is
                // looked at again, this test fails and the ghost dies normally.
                if (_cullActive &&
                    _objectViewTiles.TryGetValue(key, out var ghostTile) &&
                    _cullFrozenTiles.Contains(ghostTile))
                {
                    continue;
                }

                staleObjectKeys.Add(key);
            }
        }

        foreach (var key in staleObjectKeys)
        {
            var view = _objectViews[key];
            _objectViews.Remove(key);
            _objectViewParts.Remove(key);
            _objectViewTiles.Remove(key);
            _prevObjectPositions.Remove(key);
            _currObjectPositions.Remove(key);
            ContentResidency.ForgetObject(key);

            // Spec §54: a felled tree tilts over and leaves a stump instead of
            // vanishing — the logs the sim scattered land around it in the same
            // frame, so it reads as "chopped down".
            if (_treeViewKeys.Remove(key) && view != null)
            {
                UnregisterVegetationWind(view.transform);
                var fall = view.AddComponent<HexLive.UnityPresentation.Environment.TreeFall>();
                fall.Fell(HexRadius);
            }
            else if (view != null)
            {
                Destroy(view);
            }
        }

        // §28.15C v3: тела больше не привязаны к времени жизни объекта-якоря —
        // они уходят вместе со своей записью в snapshot.Corpses (SyncCorpseViews).

        // Items picked up only for the animation beat vanish from the ground,
        // so we never show the same piece both on the floor and in the hand.
        _wardrobeHiddenObjects.Clear();
        foreach (var n in snapshot.Npcs)
        {
            if (n.CurrentInteraction == "Dress" &&
                n.ProgressAt(snapshot.Tick) >= WardrobeHandoffFraction &&
                n.TargetObjectId is { } hiddenId)
            {
                _wardrobeHiddenObjects.Add(hiddenId);
            }
            else if (n.CurrentInteraction == "WashClothes" &&
                     n.TargetObjectId is { } washingId)
            {
                _wardrobeHiddenObjects.Add(washingId);
            }
            else if (IsGroundCoconutHandInteraction(snapshot, n) &&
                     n.TargetObjectId is { } coconutId)
            {
                _wardrobeHiddenObjects.Add(coconutId);
            }
        }

        RebuildFogState(snapshot);
        SyncFogRing(snapshot);

        // §35.5B: map rack junctions and rank the garments hanging at each one
        // (sorted by object id) so every hung garment gets a stable hanger slot.
        _rackJunctions.Clear();
        _wardrobeJunctions.Clear();
        _wardrobeYawByJunction.Clear();
        _rackHangRank.Clear();
        _collectorJunctions.Clear();
        foreach (var worldObject in snapshot.Objects)
        {
            if (worldObject.DefinitionId == "station.drying_rack" && worldObject.Junctions.Count > 0)
            {
                _rackJunctions.Add(worldObject.Junctions[0]);
            }

            // §133: гардероб развешивает одежду тем же механизмом, что и
            // сушилка, но на двенадцати собственных плечиках.
            if (worldObject.DefinitionId == "furniture.wardrobe" && worldObject.Junctions.Count > 0)
            {
                _rackJunctions.Add(worldObject.Junctions[0]);
                _wardrobeJunctions.Add(worldObject.Junctions[0]);
                _wardrobeYawByJunction[worldObject.Junctions[0]] =
                    SimulationUnityMapper.ToUnityFootprintYawDegrees(worldObject.RotationDegrees);
            }

            if (worldObject.DefinitionId == "station.water_collector" && worldObject.Junctions.Count > 0)
            {
                _collectorJunctions.Add(worldObject.Junctions[0]);
            }
        }

        if (_rackJunctions.Count > 0)
        {
            _rackHangScratch.Clear();
            foreach (var worldObject in snapshot.Objects)
            {
                if (worldObject.Junctions.Count > 0 &&
                    _rackJunctions.Contains(worldObject.Junctions[0]) &&
                    worldObject.DefinitionId != "station.drying_rack" &&
                    worldObject.DefinitionId != "furniture.wardrobe" &&
                    GarmentDropFactory.IsGarment(worldObject.DefinitionId))
                {
                    _rackHangScratch.Add(worldObject);
                }
            }

            _rackHangScratch.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            _rackRankScratch.Clear();
            var rankPerJunction = _rackRankScratch;
            foreach (var hung in _rackHangScratch)
            {
                rankPerJunction.TryGetValue(hung.Junctions[0], out var rank);
                _rackHangRank[hung.Id.Value] = rank;
                rankPerJunction[hung.Junctions[0]] = rank + 1;
            }
        }

        UnityEngine.Profiling.Profiler.EndSample();
        UnityEngine.Profiling.Profiler.BeginSample("Hex.RS.Objects");
        var frozenViewsBuilt = 0; // #321: бюджет памяти тумана на один пасс
        foreach (var worldObject in snapshot.Objects)
        {
            var key = worldObject.Id.Value;

            // Memory fog: an object on a frozen hex is left EXACTLY as last
            // seen — no sync, no repositioning, no state flips. One spawned
            // out there gets no view at all until someone looks. This skip is
            // also the perf point of the experiment: the per-tick cost shrinks
            // to the hexes the colony actually watches.
            var hasView = _objectViews.TryGetValue(key, out var objectView);

            // §148: на НИКОГДА не виденной земле вида нет вовсе — игрок не
            // знает ни про эту пальму, ни про этот кокос, пока кто-то из
            // наших туда не дойдёт. Это же и самая крупная экономия: вью
            // строятся по мере разведки, а не на весь остров сразу.
            if (!TileDiscovered(worldObject.Tile))
            {
                continue;
            }

            // Виденная, но не видимая сейчас земля ЗАМОРОЖЕНА как память:
            // существующий вид не трогаем (он и есть «как в последний раз»).
            // Bug #321 (вердикт игрока): ПАМЯТЬ ОБЯЗАНА БЫТЬ НАРИСОВАНА — на
            // разведанном гексе камни/пальмы видны и под туманом. Поэтому вид
            // без вида СОЗДАЁТСЯ и здесь (не больше FrozenViewBudget за пасс,
            // чтобы не дёрнуть кадр при загрузке большой разведки), а вот
            // потикового синка у замороженных по-прежнему нет — это и есть
            // экономия §148.
            var frozenMemory = _cullActive && _cullFrozenTiles.Contains(worldObject.Tile);
            if (frozenMemory && (hasView || frozenViewsBuilt >= FrozenViewBudget))
            {
                continue;
            }

            if (frozenMemory)
            {
                frozenViewsBuilt++;
            }

            _objectViewTiles[key] = worldObject.Tile;

            // §28.15C v3: у corpse.npc нет своего вида — CreateObjectView отдаёт
            // за него невидимый якорь. Тело рисует сама погибшая
            // (SyncCorpseViews), а объект нужен лишь для «подойти и что-то
            // сделать»: оплакать, обобрать, разделать.
            if (!hasView)
            {
                objectView = CreateObjectView(worldObject, _junctionPositions, snapshot.Tick);
                if (objectView == null)
                {
                    // The object's independent bundle is still travelling — or
                    // its last attempt ended Missing/Failed and waits for a
                    // healthy registry refresh to be forgiven
                    // (ContentPrefabCache). Do not enter it into _objectViews
                    // until the authored view exists: the next snapshot
                    // retries this exact object.
                    _objectViewTiles.Remove(key);
                    continue;
                }
                _objectViews[key] = objectView;
                // §150.4: импостор создаётся ДО WorldObjectView.Init ниже,
                // чтобы его квад попал в аналитический пикинг вида.
                TryAttachOverviewImpostor(worldObject, objectView);
                // PERF: resolve the optional per-view components ONCE, here.
                // Which of them a view owns is fixed by the prefab it was built
                // from, so the per-tick loop below just reads the record.
                var craftProject = worldObject.CraftWorkRequired > 0
                    ? objectView.AddComponent<HexLive.UnityPresentation.Environment.CraftProjectVisual>()
                    : null;
                craftProject?.Sync(worldObject);
                _objectViewParts[key] = new ObjectViewParts
                {
                    // The compact hut hearth keeps its effect on the authored
                    // fire_point child; outdoor campfires keep it on the root.
                    // Cache either form so the same ResourceAmount state drives
                    // heat and visible flames.
                    Fire = objectView.GetComponentInChildren<
                        HexLive.UnityPresentation.Environment.CampfireEffect>(true),
                    Assembly = objectView.GetComponent<HexLive.UnityPresentation.Environment.BedAssembly>(),
                    SpitMeat = objectView.GetComponent<HexLive.UnityPresentation.Environment.CampfireSpitMeat>(),
                    Garment = objectView.GetComponent<GarmentWorldCondition>(),
                    Pile = objectView.GetComponent<HexLive.UnityPresentation.Environment.BuildSitePile>(),
                    CraftProject = craftProject,
                    Hut = objectView.GetComponent<HexLive.UnityPresentation.Environment.HutAssembly>(),
                    // §120: present exactly on a plan-built module's own view.
                    Module = objectView.GetComponent<
                        HexLive.UnityPresentation.Environment.ArchitectureModuleView>(),
                };
                // Spec §54: remember trees so felling them animates.
                if (worldObject.DefinitionId.Contains("tree"))
                {
                    _treeViewKeys.Add(key);
                }

                // §121: тот же приём для наведения мышью — маркер несёт номер
                // объекта, и ввод находит его по статическому списку, а не
                // поиском по сцене.
                // §120: a building is not clickable as a lump — its modules are
                // the objects the player picks, and the aggregate owns no mesh
                // of its own to pick anyway.
                var selectableAggregate = worldObject.DefinitionId != ContentIds.Hut1Hex &&
                    worldObject.BuildProduct != ContentIds.Hut1Hex &&
                    worldObject.DefinitionId != ContentIds.HutPlan &&
                    worldObject.BuildProduct != ContentIds.HutPlan;
                var contextView = objectView.GetComponent<Views.WorldObjectView>();
                if (!selectableAggregate && contextView != null)
                    contextView.enabled = false;
                if (selectableAggregate && contextView == null)
                {
                    contextView = objectView.AddComponent<Views.WorldObjectView>();
                    contextView.Init(key, worldObject.DefinitionId);
                }
                var cachedParts = _objectViewParts[key];
                cachedParts.ObjectView = contextView;
                _objectViewParts[key] = cachedParts;
            }

            // Bug #321: замороженная память построена — потикового синка нет.
            if (frozenMemory)
            {
                continue;
            }

            _objectViewParts.TryGetValue(key, out var parts);

            // Bug #198: a visible module of an unfinished plan is the click
            // surface for its invisible footprint-owner. Refreshing the proxy
            // from the snapshot also removes the Build action at completion.
            if (parts.ObjectView != null)
            {
                if (FreeArchitectureRules.IsFreePiece(worldObject) &&
                    (!string.IsNullOrEmpty(worldObject.BuildProduct) ||
                     FreeArchitectureRules.IsDemolitionSite(worldObject)))
                {
                    parts.ObjectView.SetContextProxy(worldObject.Id.Value, ContentIds.BuildSite);
                }
                else if (worldObject.ArchitectureOwnerObjectId is { } ownerId &&
                    _unfinishedPlanBuildingOwners.Contains(ownerId))
                {
                    parts.ObjectView.SetContextProxy(ownerId, ContentIds.BuildSite);
                }
                else
                {
                    parts.ObjectView.ClearContextProxy();
                }
            }

            if (worldObject.DefinitionId == ContentIds.Hut1Hex && parts.Hut != null)
            {
                _hutCutawayViews.Add(new HutCutawayView(worldObject.Tile, parts.Hut));
            }

            // §Wardrobe-anim: hide/show the ground garment as its owner picks it
            // up / drops it (SetActive is idempotent, so this is cheap per frame).
            // FOG-OF-WAR EXPERIMENT: also hide objects no colonist remembers.
            var shouldHide = _wardrobeHiddenObjects.Contains(key)
                || (_fogActive && !_fogKnownObjects.Contains(key));
            if (objectView.activeSelf == shouldHide)
            {
                objectView.SetActive(!shouldHide);
            }

            // §155: лежащая/висящая одежда — тяжёлый wear-комплект; её вью
            // участвует в буфере резидентности наравне с девушками.
            if (GarmentDropFactory.IsGarment(worldObject.DefinitionId))
            {
                ContentResidency.ReportGroundGarment(
                    key, worldObject.DefinitionId, visible: !shouldHide);
            }

            // Spec 29E.3: the campfire burns only while it has fuel.
            if (worldObject.DefinitionId == "campfire.spot")
            {
                var fire = parts.Fire;
                if (fire != null)
                {
                    fire.SetLit(worldObject.ResourceAmount > 0f);
                }

                // §54.14: grow the staged pieces (stone ring, spit) as upgrade
                // materials land; show everything once the bill is closed.
                // Cheap per frame — Apply only flips pieces whose state changed.
                var fireAsm = parts.Assembly;
                if (fireAsm != null)
                {
                    if (string.IsNullOrEmpty(worldObject.BuildProduct))
                    {
                        fireAsm.ApplyAll();
                    }
                    else
                    {
                        fireAsm.Apply(worldObject.DeliveredLogs, worldObject.DeliveredSticks,
                            worldObject.DeliveredRope, worldObject.DeliveredLeaves,
                            worldObject.DeliveredStones);
                    }
                }

                // §54.14 (r2): meat hanging on the roasting spit — raw chunks
                // roasting, cooked ones waiting to be taken.
                var spitMeat = parts.SpitMeat;
                if (spitMeat != null)
                {
                    spitMeat.Refresh(worldObject.RoastingRaw, worldObject.RoastingCooked);
                }
            }

            var garmentCondition = parts.Garment;
            if (garmentCondition != null)
            {
                garmentCondition.Sync(worldObject.Durability, worldObject.Dirtiness,
                    worldObject.Bloodiness, worldObject.Wetness);
            }

            // Spec §54: re-pile a build-site as its delivered materials grow.
            if (!string.IsNullOrEmpty(worldObject.BuildProduct))
            {
                var pile = parts.Pile;
                if (pile != null)
                {
                    pile.Refresh(worldObject);
                }
            }

            parts.CraftProject?.Sync(worldObject);

            // §120: a module of a plan-built house grows in place — the model is
            // built once, with the view, and only mutated afterwards (revealed
            // stage by stage as its own bill is delivered). Nothing here rebuilds
            // or re-parents it, so the owner's per-tick record cannot blink it.
            var module = parts.Module;
            if (module != null)
            {
                module.Sync(worldObject);
                CollectArchitectureFloorTile(worldObject);
                CollectPlanCutawayModule(worldObject, module);
            }

            // §35.5B: keep a hung garment on its (possibly re-ranked) hanger
            // slot — when a neighbour is dressed off the rack the rest slide
            // one slot over. Cheap: one localPosition write per hung garment.
            if (_rackHangRank.TryGetValue(key, out var hangSlot) && objectView.transform.childCount > 0)
            {
                var hungChild = objectView.transform.GetChild(0);
                var inWardrobe = worldObject.Junctions.Count > 0 &&
                    _wardrobeJunctions.Contains(worldObject.Junctions[0]);
                var footwearOnWardrobeShelf = inWardrobe &&
                    GarmentDropFactory.IsFootwear(worldObject.DefinitionId);
                var wardrobeSocket = HexLive.UnityPresentation.Environment.WardrobeHangers.Slot(hangSlot);
                if (footwearOnWardrobeShelf)
                {
                    wardrobeSocket = HexLive.UnityPresentation.Environment.WardrobeHangers
                        .GroundFootwearOnShelf(hungChild, hangSlot);
                }
                else if (inWardrobe)
                {
                    HexLive.UnityPresentation.Environment.WardrobeAssembly.TryGetClothingSlotLocal(
                        hangSlot, out wardrobeSocket);
                }
                hungChild.localPosition = footwearOnWardrobeShelf
                    ? wardrobeSocket
                    : HangSlotOffset(hungChild, hangSlot, inWardrobe, wardrobeSocket);
                // A rack ranks its garments by id every sync. The old code moved
                // only the cloth when that rank changed, leaving the procedural
                // hanger at its former socket. Move both children from the same
                // authored socket, otherwise a reload or a neighbour dressing
                // off the rail visibly splits a hanger from its garment.
                if (inWardrobe)
                {
                    for (var childIndex = 1; childIndex < objectView.transform.childCount; childIndex++)
                    {
                        var child = objectView.transform.GetChild(childIndex);
                        if (child.name == "Occupied wardrobe hanger")
                        {
                            // Shoes never own a hanger; an old visual can
                            // survive one sync after a hot reload, so hide it
                            // until the object view is rebuilt.
                            child.gameObject.SetActive(!footwearOnWardrobeShelf);
                            child.localPosition = wardrobeSocket;
                            child.localRotation = Quaternion.identity;
                        }
                    }
                }
                if (inWardrobe && _wardrobeYawByJunction.TryGetValue(
                        worldObject.Junctions[0], out var wardrobeYaw))
                    objectView.transform.rotation = Quaternion.Euler(0f, wardrobeYaw, 0f);
            }

            // Spec §66: a BUILT piece stands at the yaw the sim staked it with —
            // a bed lies side-on to the fire so the sleeper warms her flank. The
            // yaw never changes for a given object, but writing it here (instead
            // of only at view creation) also covers a view rebuilt mid-build.
            // Exactly 0 = no §66 facing (loose props, pre-§66 worlds) — those
            // keep the identity rotation they have always had.
            // §120: a plan-built module carries its building's yaw, but it is
            // not the building's own forward — its model child composes that
            // footprint yaw with the module's local yaw (ArchitectureModuleView),
            // so writing it onto the module's root here would apply it twice.
            if (worldObject.RotationDegrees != 0f && parts.Module == null &&
                !HexLive.UnityPresentation.Environment.PalmTreeFactory.IsPalm(
                    worldObject.DefinitionId))
            {
                var architectureFootprint =
                    worldObject.DefinitionId == ContentIds.Hut1Hex ||
                    worldObject.BuildProduct == ContentIds.Hut1Hex ||
                    // §120: a plan-built house is geometry authored directly in
                    // the simulation X/Y footprint plane, exactly like the hut.
                    worldObject.DefinitionId == ContentIds.HutPlan ||
                    worldObject.BuildProduct == ContentIds.HutPlan ||
                    IsIntegratedHutBed(worldObject) ||
                    IsIntegratedHutHearth(worldObject) ||
                    worldObject.DefinitionId == ContentIds.Wardrobe ||
                    // §120: the plan's OWN furniture. Its yaw is a yawStep 0..5
                    // the constructor authored (BuildingBootstrap
                    // .StakePlanFurnitureSites composes it with the building's
                    // rotation), so it belongs in the six-way FOOTPRINT basis —
                    // the character convention would turn every piece 90° off
                    // its own junction row. The three types above happen to be
                    // covered already; a rack, a workbench or a collector the
                    // player put in a plan were not, and neither was a piece
                    // still standing as its build-site.
                    IsPlanFurniture(worldObject) ||
                    // A hung garment is spatially owned by the wardrobe, not
                    // by its original loose-item yaw.  It must share the
                    // wardrobe's six-way footprint basis with its socket and
                    // procedural hanger; otherwise this generic §66 sync
                    // overwrites the yaw assigned above by 90°.
                    (_rackHangRank.ContainsKey(key) && worldObject.Junctions.Count > 0 &&
                     _wardrobeJunctions.Contains(worldObject.Junctions[0]));
                var builtRot = Quaternion.Euler(
                    0f, architectureFootprint
                        ? SimulationUnityMapper.ToUnityFootprintYawDegrees(worldObject.RotationDegrees)
                        : SimulationUnityMapper.ToUnityYawDegrees(worldObject.RotationDegrees),
                    0f);
                if (objectView.transform.rotation != builtRot)
                {
                    objectView.transform.rotation = builtRot;
                }
            }

            var objPos = GetObjectAnchorPosition(snapshot, worldObject);

            if (_currObjectPositions.TryGetValue(key, out var oldObjPos))
            {
                _prevObjectPositions[key] = oldObjPos;
            }
            else
            {
                _prevObjectPositions[key] = objPos;
            }

            _currObjectPositions[key] = objPos;
        }


        UnityEngine.Profiling.Profiler.EndSample();
        UnityEngine.Profiling.Profiler.BeginSample("Hex.RS.Actors");
        // PERF: пространственный индекс маршрутов интерполяции — пересборка
        // только когда сменился мир или чей-то Blocked (стройка/дверь).
        if (!_routeIndex.IsCurrent(snapshot.Junctions, snapshot.JunctionsBlockedStamp))
        {
            _routeIndex.Rebuild(snapshot.Junctions, snapshot.JunctionsBlockedStamp);
        }

        UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.Architecture");
        BindArchitectureElementViews(snapshot);
        UnityEngine.Profiling.Profiler.EndSample();

        // §155.5: вне шторки за один снапшот-пасс собирается не больше ОДНОЙ
        // новой девушки — сборка вью (тело + пришивка одежды + ткань) стоит
        // сотни миллисекунд, и караван из нескольких появившихся разом клал
        // кадр на секунды (капча 2026-08-28: кадр 829 = 1.7 с).
        var appearanceBudget = UI.LoadingScreen.IsActive || !_cullInitialBuildDone
            ? int.MaxValue
            : 1;

        foreach (var npc in snapshot.Npcs)
        {
            var key = npc.Id.Value;
            // §155: видимость считается ДО создания вью — судьба и создания,
            // и выгрузки решается одним и тем же предикатом.
            // §148: чужой человек виден, только пока кто-то из наших
            // держит его гекс в восприятии. Память о нём — это «?» на
            // последнем известном месте (SyncUnknownNpcMarkers), а не
            // застывшее тело: тело ушло бы оттуда, и картинка врала бы.
            var isOurs = IsPlayerOwned(npc);
            var strangerVisible = isOurs || TileVisibleNow(npc.Tile);
            if (!isOurs && strangerVisible)
            {
                _lastSeenNpcTiles[key] = npc.Tile;
            }

            var hiddenByFog = (_fogActive && _fogHidesNpcs && _fogHiddenNpcs.Contains(key))
                // Гейт на ВСЁ ВРЕМЯ ЗАГРУЗКИ, а не на первую сборку: экран
                // ждёт, пока каждое тело ростера дошьётся, а выключенный
                // актёр дошиться не может (зависание #146). Одной первой
                // сборки не хватило (баг #182): причёска приезжает много
                // кадров спустя, и девушки соседних лагерей (§146) успевали
                // погаснуть прямо посреди своей загрузки — занавес ждал их
                // вечно. Под занавесом их всё равно никто не видит.
                || (_cullInitialBuildDone && !UI.LoadingScreen.IsActive && !strangerVisible);

            if (!_npcViews.TryGetValue(key, out var npcView) || npcView == null)
            {
                // §155: вью скрытой ЧУЖОЙ не строится вовсе — это вторая
                // половина выгрузки по восприятию (первая — вытеснение из
                // буфера в EvictResidencyView): без этого гейта следующий же
                // снапшот пересобрал бы только что вытесненный комплект. Под
                // занавесом строим всех (гейт занавеса ждёт тела ростера), и
                // своих строим всегда: дебаг-туман §125.5 прячет и своих, а их
                // вью нужны панелям, портретам и сторожку #146.
                if (hiddenByFog && !isOurs && ContentResidency.Enabled &&
                    _cullInitialBuildDone && !UI.LoadingScreen.IsActive)
                {
                    ContentResidency.ForgetNpc(key);
                    continue;
                }

                // §155.5: тёплое появление. Вне шторки вью не собирается, пока
                // тяжёлые двери (тело, причёска, надетое) не отдадут из кэша:
                // сборка на холодных дверях читала ассеты с диска прямо в
                // кадре (Loading.ReadObject 463 мс в кадре-фризе). Пассы до
                // готовности греют двери асинхронно и выходят.
                if (_cullInitialBuildDone && !UI.LoadingScreen.IsActive)
                {
                    if (appearanceBudget <= 0 || !NpcAppearanceWarm(npc))
                    {
                        continue;
                    }

                    appearanceBudget--;
                }

                npcView = CreateNpcView(npc);
                if (npcView == null)
                {
                    // The actor bundle is asynchronous. Never cache a body made
                    // from primitives while it travels: the next snapshot asks
                    // for this exact actor again and installs only authored art.
                    continue;
                }
                _npcViews[key] = npcView;
                if (npcView != null)
                {
                    // §150.4: человек в дальнем обзоре — портретный диск.
                    var npcImpostor = Views.ObjectImpostor.AttachNpc(
                        npcView, NpcImpostorTint(key));
                    _impostors.Add(npcImpostor);
                    _npcImpostors[key] = npcImpostor;
                    if (_overviewImpostorsActive)
                    {
                        npcImpostor.SetDistant(true, CurrentGameDay());
                    }
                }
            }

            // ActorGroundY is the STATIC surface (tile top − sink); the live
            // wave swell is added per render frame in InterpolateViews so the
            // swimmer bobs with the exact surface under her, not a snapshot.
            // §125.5: туман прячет тех, кого выбранная не видит. Вью живёт и
            // двигается как обычно — гаснет только его картинка, ровно как у
            // мобов; симуляция об этом не знает.
            if (npcView != null)
            {
                if (npcView.activeSelf == hiddenByFog)
                {
                    npcView.SetActive(!hiddenByFog);
                }

                // §155: учёт резидентности — какая девушка с живым вью какие
                // комплекты (тело/волосы/одежда) держит и видима ли она.
                // Пин: своя и выбранная не вытесняются даже скрытыми — карточка
                // отношений может держать выбор на скрытом чужаке (#146), и её
                // портрету нужен живой актёр.
                ContentResidency.ReportNpc(key, npc, visible: !hiddenByFog,
                    pinned: isOurs || Input.NpcSelection.Contains(key));
            }

            // §125.5/§148: спрятанную туманом не только не видно — её не надо
            // и КРАСИТЬ. Бейки кожи (раны/грязь/пот) для невидимых — чистый
            // расход тикового кадра; painter диффит по _paintedKeys, а
            // раскрытие происходит в этом же цикле, так что на тике появления
            // полный SyncActorView дорисует всё недостающее без разрыва.
            var paintHidden = npcView == null || !npcView.activeSelf;

            _npcOnWater[key] = _waterCoords.Contains(npc.Tile);
            var targetRot = Quaternion.Euler(0f, SimulationUnityMapper.ToUnityYawDegrees(npc.RotationDegrees), 0f);
            var targetPose = TryGetCarriedPose(snapshot, npc, targetRot, out var carriedPose)
                ? carriedPose
                : TryGetFurnitureSeatPose(snapshot, npc, targetRot, out var furnitureSeatPose)
                    ? furnitureSeatPose
                    : new Pose(
                        // §141: земля берётся из-под ТЕЛА, а не из отстающего
                        // поля тайла — см. GroundTileUnder.
                        SimulationUnityMapper.ToUnityPosition(
                            npc.Position, ActorGroundY(GroundTileUnder(npc))),
                        targetRot);
            var targetPos = targetPose.Position;

            var hadPreviousPose = _currNpcPoses.TryGetValue(key, out var oldPose);
            if (hadPreviousPose)
            {
                // A carried patient does not own a MovementStatus of her own,
                // but her carrier is still moving her continuously. Keep that
                // legitimate transport interpolated; only uncarried idle pose
                // jumps are simulation teleports/collapse snaps.
                var locomoting = npc.MovementStatus is "Moving" or "Arrived" ||
                    npc.CarriedByNpcId is not null;
                var previousPose = !locomoting &&
                    Vector3.Distance(oldPose.Position, targetPose.Position) > TeleportSnapWorldUnits
                        ? targetPose
                        : oldPose;

                if (locomoting)
                {
                    UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.Route");
                    if (!_npcMovementRoutes.TryGetValue(key, out var route))
                    {
                        route = new List<Float2>();
                    }

                    // PERF: индексированная перегрузка SnapshotMovementRoute.Build(
                    // без неё каждая идущая девушка каждый тик сканировала все
                    // ~194k джанкшенов на «пересекаю ли я стену» — это было
                    // ~59 мс из 63 мс тикового кадра на большом острове.
                    var routeKind = SnapshotMovementRoute.Build(
                        _routeIndex,
                        new Float2(oldPose.Position.x, oldPose.Position.z),
                        new Float2(targetPose.Position.x, targetPose.Position.z),
                        route);
                    if (routeKind == SnapshotMovementRoute.RouteKind.Routed)
                    {
                        _npcMovementRoutes[key] = route;
                    }
                    else
                    {
                        _npcMovementRoutes.Remove(key);
                        if (routeKind == SnapshotMovementRoute.RouteKind.Disconnected)
                        {
                            // A relocation with no passable graph connection is
                            // never safe to animate as travel through solid art.
                            previousPose = targetPose;
                        }
                    }
                    UnityEngine.Profiling.Profiler.EndSample();
                }
                else
                {
                    _npcMovementRoutes.Remove(key);
                }

                _prevNpcPoses[key] = previousPose;

                // §45: stepping onto a tile one level up/down is a visible
                // hop, not a glide — the actor plays JumpUp/JumpDown and its
                // own Y-offset curve carries the body to the exact new level.
                var stepDy = targetPos.y - oldPose.Position.y;
                if (locomoting && Mathf.Abs(stepDy) > ElevationStep * 0.5f &&
                    _actorViews.TryGetValue(key, out var jumper) && jumper != null)
                {
                    jumper.TriggerHexStepJump(stepDy);
                }
            }
            else
            {
                _prevNpcPoses[key] = targetPose;
                _npcMovementRoutes.Remove(key);
            }

            _currNpcPoses[key] = targetPose;

            // Feed locomotion cadence from the simulation's actual horizontal
            // displacement, not from a render-frame Transform delta. This
            // includes Agility, wet-clothes drag, leg mobility, carry weight,
            // turn penalties and every future movement multiplier exactly once.
            var simGroundSpeed = 0f;
            var simYawSpeed = 0f;
            if (_prevNpcPoses.TryGetValue(key, out var previousPoseForSpeed) &&
                snapshot.TickDeltaTime > 0f)
            {
                var simDelta = targetPose.Position - previousPoseForSpeed.Position;
                simDelta.y = 0f;
                simGroundSpeed = simDelta.magnitude / snapshot.TickDeltaTime;
                simYawSpeed = Mathf.DeltaAngle(
                    previousPoseForSpeed.Rotation.eulerAngles.y,
                    targetPose.Rotation.eulerAngles.y) / snapshot.TickDeltaTime;
            }
            if (_actorViews.TryGetValue(key, out var speedActor) && speedActor != null)
            {
                speedActor.SetSimulationGroundSpeed(simGroundSpeed);
                speedActor.SetSimulationYawSpeed(simYawSpeed);
                // §71.8: "Moving" stays true through blocked-step queues (the
                // §71.3 latch), which is exactly the stall the long walk-hold
                // must bridge; Arrived/Idle mean the journey is over and the
                // walk should be admitted finished promptly.
                speedActor.SetSimulationMovementIntent(npc.MovementStatus == "Moving");
            }

            UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.SyncActor");
            SyncActorView(snapshot, npc, paintHidden);
            UnityEngine.Profiling.Profiler.EndSample();

            // §127: the sim's completion cue is the one-shot edge. Sampling
            // the live hip bone here preserves the authored pose offset, then
            // only Y is lowered to the ground directly beneath that pelvis.
            _actorViews.TryGetValue(key, out var intimacyActor);
            SyncGroundIntimacyStain(snapshot, npc, intimacyActor, targetPos);

            // Spec 40.2-B/C: a bleeding girl drips blood. On land it pools at
            // her feet; IN THE WATER (iteration 1: detected via _npcOnWater) it
            // billows on the surface around her instead — different spawn, no
            // ground puddle underwater.
            UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.Blood");
            if (_npcOnWater.TryGetValue(key, out var onWaterNow) && onWaterNow)
            {
                var waterSurfaceY = GroundY(npc.Tile) + ElevationStep * SwimVisuals.SurfaceStepOffset;
                EnsureWaterBlood().OnNpcTick(key, npc.Blood, targetPos, waterSurfaceY, snapshot.Tick);
            }
            else
            {
                EnsureBloodStains().OnNpcTick(key, npc.Blood, targetPos, snapshot.Tick);
            }
            UnityEngine.Profiling.Profiler.EndSample();
        }

        SyncRomancePairs(snapshot);

        // Spec 40.2-B/C r2 / bug #22: a body with an open wound keeps feeding
        // the existing cosmetic blood pipeline for a finite period after the
        // death tick. DeathRecords makes the window deterministic across a
        // reconnect/save load; no duplicate sim entity or persisted VFX object
        // is needed. Land and water use their normal respective emitters.
        foreach (var body in snapshot.Corpses)
        {
            if (body.Wounds.Count == 0 || body.Blood <= 0.0001f ||
                !TryGetDeathTick(snapshot, body.Id.Value, out var deathTick))
            {
                continue;
            }

            var age = snapshot.Tick - deathTick;
            if (age < 0 || age > CorpseBleedTicks)
            {
                continue;
            }

            var key = body.Id.Value;
            var bodyPos = SimulationUnityMapper.ToUnityPosition(
                body.Position, ActorGroundY(body.Tile));
            if (_waterCoords.Contains(body.Tile))
            {
                var waterSurfaceY = GroundY(body.Tile) +
                    ElevationStep * SwimVisuals.SurfaceStepOffset;
                EnsureWaterBlood().OnBleedingSourceTick(
                    key, bodyPos, waterSurfaceY, snapshot.Tick);
            }
            else
            {
                EnsureBloodStains().OnBleedingSourceTick(key, bodyPos, snapshot.Tick);
            }
        }

        _bloodStains?.Advance(snapshot.Tick);
        _waterBlood?.Advance(snapshot.Tick);
        _intimacyStains?.Advance(snapshot.Tick, snapshot.IsRaining);

        UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.Grass");
        UpdateGrassFlattening(snapshot);
        UnityEngine.Profiling.Profiler.EndSample();

        // §80/§107.4: фотосессия — сразу на входе в мир, дальше раз в игровые
        // сутки; свет даёт вспышка, поэтому часа суток тут нет. В снапшоте
        // лежат только живые (мёртвых убирает MobSystem.RemoveDeadNpc), а их
        // снимки остаются в кэше — лицо погибшей во вкладке отношений должно
        // жить дальше, тела-то уже нет.
        if (_portraitCache != null)
        {
            _portraitIds.Clear();
            foreach (var npc in snapshot.Npcs)
            {
                _portraitIds.Add(npc.Id.Value);
            }

            _portraitCache.Sweep(
                snapshot.Tick, HexLive.Simulation.Runtime.WorldBalance.DayLengthTicks, _portraitIds);
        }

        UnityEngine.Profiling.Profiler.EndSample();
        UnityEngine.Profiling.Profiler.BeginSample("Hex.RS.AnimalsCorpses");
        SyncAnimalViews(snapshot);

        // A dead housemate leaves a corpse object - her walking view goes.
        var staleNpcKeys = new List<int>();
        foreach (var key in _npcViews.Keys)
        {
            var alive = false;
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value == key)
                {
                    alive = true;
                    break;
                }
            }

            if (!alive)
            {
                staleNpcKeys.Add(key);
            }
        }

        foreach (var key in staleNpcKeys)
        {
            // §28.15C v3: она УПАЛА ЗДЕСЬ. Живой вид не уничтожается и не
            // заменяется — он переезжает в реестр тел как есть, тем же телом,
            // в той же одежде, с теми же ранами, и падает на месте. Создать
            // вместо него новый вид значило бы моргнуть телом ровно в тот кадр,
            // на который смотрит игрок.
            //
            // Прежняя версия усыновляла вид ОБЪЕКТУ corpse.npc и ключевалась
            // его id. Теперь ключ — id самой погибшей: тело это она, а объект
            // рядом с ней всего лишь якорь для «подойти и что-то сделать».
            var handedOver = false;
            if (FindCorpse(snapshot, key) is { } fallen &&
                _actorViews.TryGetValue(key, out var deadActor) && deadActor != null)
            {
                deadActor.SetLedgeSit(false);
                deadActor.ClearGaze();
                deadActor.ClearActionTarget();
                // Spec 40.13 v2: death is a quiet fall, then the pose
                // freezes (SetDead) — NO ragdoll: the hex tiles carry no
                // colliders, so physics bodies spun out and fell through.
                deadActor.SetRagdoll(false);
                deadActor.SetDead(ActorGroundY(fallen.Tile), fallen.DeathAnimVariant, fresh: true);

                // Симуляция кладёт тело в ЦЕНТР гекса (§60.2a), а упасть она
                // могла на ободе — интерполяция живых видов для неё больше не
                // работает, так что позицию надо поставить здесь и сейчас.
                // Иначе тело замрёт на ободе, а якорь, к которому подходят
                // обирать, останется в середине.
                _npcViews[key].transform.SetPositionAndRotation(
                    SimulationUnityMapper.ToUnityPosition(fallen.Position, ActorGroundY(fallen.Tile)),
                    Quaternion.Euler(0f, SimulationUnityMapper.ToUnityYawDegrees(fallen.RotationDegrees), 0f));

                _corpseViews[key] = _npcViews[key];
                _corpseActorViews[key] = deadActor;
                handedOver = true;
                // §155: комплекты переезжают вместе с телом и пинятся — труп
                // не вытесняется (их мало, а моргнуть телом хуже экономии).
                ContentResidency.TransferNpcToCorpse(key);
            }

            if (!handedOver)
            {
                Destroy(_npcViews[key]);
                ContentResidency.ForgetNpc(key);
            }
            else if (_npcImpostors.TryGetValue(key, out var deadImpostor) &&
                deadImpostor != null)
            {
                // §150.4: трупы живут настоящими мешами — переехавший в реестр
                // тел вид сдаёт свой портретный диск (с возвратом рендереров,
                // если дальний режим был активен).
                deadImpostor.SetDistant(false, 0);
                Destroy(deadImpostor);
            }

            _npcImpostors.Remove(key);
            _npcViews.Remove(key);
            _actorViews.Remove(key);
            _lastTalkResultTick.Remove(key);
            _lastSocialCueKey.Remove(key);
            _pendingSocialCueItemKey.Remove(key);
            _prevNpcPoses.Remove(key);
            _currNpcPoses.Remove(key);
            _npcMovementRoutes.Remove(key);
            _npcOnWater.Remove(key);
        }

        SyncCorpseViews(snapshot);
        AuditNpcRenderState(snapshot);
        // §148: «?» вместо тел, которых мы сейчас не видим, и затенение
        // гексов памяти. Обе строки — чистая презентация поверх готового кадра.
        SyncUnknownNpcMarkers(snapshot);
        ApplyMemoryShade();
        // A-1: overview-группы пополняются при создании рендерера (см.
        // BuildGrassClump), пер-тикового переобхода всех тайлов больше нет.
        // The distant profile is grass suppression plus §150.4 world-space
        // impostors in the same anchors. Buildings, stations, furniture,
        // corpses, mobs and VFX keep their real renderers at any zoom, so
        // visible geometry and the analytic click path cannot diverge.
        UnityEngine.Profiling.Profiler.EndSample();

        // §155: учёт прохода закрыт — если буфер скрывшихся переполнен,
        // вытесняем самых давних (вью рушит EvictResidencyView, ссылки на
        // комплекты снимает сам учёт).
        ContentResidency.EndPass(EvictResidencyView);

        // Memory fog: the full first build of this world is behind us — from
        // here on, frozen hexes get no new object views until seen again.
        _cullInitialBuildDone = true;
    }

    /// <summary>§155: снос вью вытесненной сущности. Девушка сносится как
    /// «не виденная вовсе» — следующий снапшот, когда она вернётся в
    /// восприятие, соберёт её тем же путём, что и первый взгляд. Ключи —
    /// формата ContentResidency ("npc/12", "obj/4711").</summary>
    private bool EvictResidencyView(string entityKey)
    {
        if (entityKey.StartsWith("npc/", StringComparison.Ordinal) &&
            int.TryParse(entityKey.Substring(4), out var npcId))
        {
            if (_npcViews.TryGetValue(npcId, out var view) && view != null)
            {
                Destroy(view);
            }
            if (_npcImpostors.TryGetValue(npcId, out var impostor) && impostor != null)
            {
                // §150.4: диск возвращает рендереры перед смертью, как при
                // гибели — иначе дальний режим остался бы с холостой записью.
                impostor.SetDistant(false, 0);
                Destroy(impostor);
            }
            _npcImpostors.Remove(npcId);
            _npcViews.Remove(npcId);
            _actorViews.Remove(npcId);
            _prevNpcPoses.Remove(npcId);
            _currNpcPoses.Remove(npcId);
            _npcMovementRoutes.Remove(npcId);
            _npcOnWater.Remove(npcId);
            return true;
        }

        if (entityKey.StartsWith("obj/", StringComparison.Ordinal) &&
            int.TryParse(entityKey.Substring(4), out var objectId))
        {
            if (_objectViews.TryGetValue(objectId, out var view))
            {
                _objectViews.Remove(objectId);
                _objectViewParts.Remove(objectId);
                _objectViewTiles.Remove(objectId);
                _prevObjectPositions.Remove(objectId);
                _currObjectPositions.Remove(objectId);
                _treeViewKeys.Remove(objectId);
                if (view != null)
                {
                    Destroy(view);
                }
            }
            return true;
        }

        return false;
    }

    // ⭐ Bug #146 watchdog: «девушки не рендерятся, пока не кликнешь». Три
    // правдоподобных диагноза подряд (туман §125.5, вырез §120, GRD occlusion)
    // не подтвердились у игрока — дальше только замер по живой сессии, §109.16.
    // Раз в 2 секунды каждая живая колонистка обязана: (1) иметь вид, (2) вид
    // активен, (3) вид стоит там, где тело в симуляции, (4) если тело в кадре
    // камеры — хотя бы один скин реально нарисован. Нарушение пишет в
    // Player.log полное состояние рендереров. Снять после подтверждения #146.
    private float _renderWatchdogNextTime;
    private readonly Dictionary<string, float> _renderWatchdogLastByKey = new();

    private void AuditNpcRenderState(WorldSnapshot snapshot)
    {
        if (Time.unscaledTime < _renderWatchdogNextTime) return;
        _renderWatchdogNextTime = Time.unscaledTime + 2f;

        var cam = Camera.main;
        if (cam == null) return;
        var planes = GeometryUtility.CalculateFrustumPlanes(cam);

        foreach (var npc in snapshot.Npcs)
        {
            // §149: сторож смотрит за СВОИМИ, а не за фракцией `Colony`. На
            // сервере чужая колонистка законно погашена вырезом §150 (глаза
            // выреза — только управляемые), и сторож писал про неё «вид
            // НЕАКТИВЕН» как про поломку — ровно в том логе, по которому
            // разбирают настоящую.
            if (!IsPlayerOwned(npc)) continue;
            var id = npc.Id.Value;

            if (!_npcViews.TryGetValue(id, out var view) || view == null)
            {
                WatchdogReport($"noview:{id}",
                    $"[NpcRenderWatchdog] npc={id} {npc.DisplayName}: в снапшоте есть, вида НЕТ " +
                    $"(corpseView={_corpseViews.ContainsKey(id)})");
                continue;
            }

            if (!view.activeInHierarchy)
            {
                WatchdogReport($"inactive:{id}",
                    $"[NpcRenderWatchdog] npc={id} {npc.DisplayName}: вид НЕАКТИВЕН " +
                    $"(activeSelf={view.activeSelf} fogActive={_fogActive} fogHidesNpcs={_fogHidesNpcs} " +
                    $"fogHidden={_fogHiddenNpcs.Contains(id)})");
                continue;
            }

            var expected = _currNpcPoses.TryGetValue(id, out var pose)
                ? pose.Position
                : SimulationUnityMapper.ToUnityPosition(
                    npc.Position, ActorGroundY(GroundTileUnder(npc)));
            var actual = view.transform.position;
            var drift = Vector3.Distance(actual, expected);
            if (drift > 3f)
            {
                WatchdogReport($"drift:{id}",
                    $"[NpcRenderWatchdog] npc={id} {npc.DisplayName}: вид в {drift:0.0} wu от симуляции " +
                    $"(view=({actual.x:0.0},{actual.y:0.0},{actual.z:0.0}) " +
                    $"sim=({expected.x:0.0},{expected.y:0.0},{expected.z:0.0}) " +
                    $"move={npc.MovementStatus} carriedBy={npc.CarriedByNpcId?.ToString() ?? "-"})");
            }

            // Тело в кадре — но ни один его скин не нарисован этой сценой.
            var inFrustum = GeometryUtility.TestPlanesAABB(planes,
                new Bounds(actual + Vector3.up * 0.9f, new Vector3(1.2f, 2.0f, 1.2f)));
            if (inFrustum && _actorViews.TryGetValue(id, out var actor) && actor != null &&
                !actor.AnyBodySkinVisible())
            {
                WatchdogReport($"culled:{id}",
                    $"[NpcRenderWatchdog] npc={id} {npc.DisplayName}: тело в кадре, но ни один скин " +
                    $"не рисуется -> {actor.DescribeRenderState()}");
            }
        }
    }

    private void WatchdogReport(string key, string message)
    {
        if (_renderWatchdogLastByKey.TryGetValue(key, out var last) &&
            Time.unscaledTime - last < 10f)
        {
            return;
        }

        _renderWatchdogLastByKey[key] = Time.unscaledTime;
        UnityEngine.Debug.LogWarning(message);
    }

    private void BindArchitectureElementViews(WorldSnapshot snapshot)
    {
        var byOwner = new Dictionary<int, List<ObjectSnapshot>>();
        foreach (var piece in snapshot.Objects)
        {
            if (!piece.ArchitectureOwnerObjectId.HasValue || piece.ArchitectureElements.Count != 1)
                continue;
            if (!byOwner.TryGetValue(piece.ArchitectureOwnerObjectId.Value, out var list))
            {
                list = new List<ObjectSnapshot>();
                byOwner[piece.ArchitectureOwnerObjectId.Value] = list;
            }
            list.Add(piece);
        }

        foreach (var pair in byOwner)
        {
            if (!_objectViews.TryGetValue(pair.Key, out var ownerView)) continue;
            var assembly = ownerView.GetComponentInChildren<HexLive.UnityPresentation.Environment.HutAssembly>(true);
            // §120: a building with no monolithic assembly (a house raised from
            // a player plan) is nothing but its modules, and each of them is an
            // ordinary world object that draws itself. Nothing to bind here.
            if (assembly == null) continue;

            var components = new List<ArchitectureElementSnapshot>(pair.Value.Count);
            foreach (var piece in pair.Value) components.Add(piece.ArchitectureElements[0]);
            assembly.ApplyElements(components);
            var doorPiece = pair.Value.Find(piece =>
                piece.DefinitionId == "architecture.door.wood");
            if (doorPiece != null) assembly.SetDoorOpen(doorPiece.IsDoorOpen);

            foreach (var piece in pair.Value)
            {
                if (!_objectViews.TryGetValue(piece.Id.Value, out var marker)) continue;
                var view = marker.GetComponent<Views.WorldObjectView>() ??
                    marker.AddComponent<Views.WorldObjectView>();
                var component = piece.ArchitectureElements[0];
                view.Init(piece.Id.Value, piece.DefinitionId,
                    assembly.RenderersForElement(component.SlotKey));
            }
        }
    }

    /// <summary>Запись тела в снапшоте по id погибшей, или null.</summary>
    private static NpcSnapshot FindCorpse(WorldSnapshot snapshot, int npcId)
    {
        foreach (var body in snapshot.Corpses)
        {
            if (body.Id.Value == npcId)
            {
                return body;
            }
        }

        return null;
    }

    private static bool TryGetDeathTick(WorldSnapshot snapshot, int npcId, out int deathTick)
    {
        foreach (var death in snapshot.DeathRecords)
        {
            if (death.EntityId == npcId)
            {
                deathTick = death.Tick;
                return true;
            }
        }

        deathTick = 0;
        return false;
    }

    /// <summary>
    /// §28.15C v5: ТЕЛА. На земле поза замерла (аниматор выключен в
    /// <c>NpcActorView.SetDead</c>); переносимое тело включает только граф
    /// BeingCarried. Позиция и связь перечитываются каждый кадр.
    ///
    /// <para>
    /// Гардероб обязателен именно потому, что вещи остались на теле: когда
    /// живая приходит и снимает с покойной куртку (§28.15F), это должно быть
    /// ВИДНО. Иначе одежда живёт в двух версиях — в симуляции её уже унесли, а
    /// на теле она всё ещё надета.
    /// </para>
    /// <para>
    /// Тело пропадает из списка после разделки (§56) либо по окончании первой
    /// стадии гниения; во втором случае объект останков и мешок лута ещё два
    /// игровых дня остаются на острове.
    /// </para>
    /// </summary>
    private void SyncCorpseViews(WorldSnapshot snapshot)
    {
        _liveCorpseIds.Clear();
        foreach (var body in snapshot.Corpses)
        {
            var key = body.Id.Value;
            _liveCorpseIds.Add(key);

            if (!_corpseViews.TryGetValue(key, out var view) || view == null)
            {
                // Тело, которого этот зритель не видел падающим: загруженный
                // сейв или только что подключившийся клиент. Оно обязано
                // появиться СРАЗУ лежачим — падение уже случилось, и
                // проигрывать его заново значило бы врать о том, когда.
                view = CreateNpcView(body);
                if (view == null)
                {
                    continue;
                }
                _corpseViews[key] = view;
                if (_actorViews.TryGetValue(key, out var restored) && restored != null)
                {
                    // CreateNpcView регистрирует вид в живом реестре — забрать
                    // его оттуда, иначе живой пасс станет ходить по трупу.
                    _actorViews.Remove(key);
                    _corpseActorViews[key] = restored;
                    restored.SetRagdoll(false);
                    restored.SetDead(ActorGroundY(body.Tile), body.DeathAnimVariant, fresh: false);
                }

            }

            // Perception culling: a body that is not OURS goes dark with its
            // hex (a carried body rides its carrier's tile, so it stays
            // visible exactly while she is). Our own dead stay visible — the
            // player owns that grief the way she owns the living girls.
            var cullHideCorpse = _cullInitialBuildDone &&
                !IsPlayerOwned(body) &&
                CullHidesTile(body.Tile);
            if (view.activeSelf == cullHideCorpse)
            {
                view.SetActive(!cullHideCorpse);
            }

            // §155: тело из сейва/подключения строится здесь, минуя
            // TransferNpcToCorpse, — без учёта его комплекты остались бы с
            // нулём ссылок, и вытеснение выдернуло бы общие ассеты из-под
            // лежащего тела.
            ContentResidency.ReportCorpse(key, body);

            var targetRotation = Quaternion.Euler(
                0f, SimulationUnityMapper.ToUnityYawDegrees(body.RotationDegrees), 0f);
            var targetPose = TryGetCarriedPose(snapshot, body, targetRotation, out var carriedPose)
                ? carriedPose
                : new Pose(
                    SimulationUnityMapper.ToUnityPosition(body.Position, ActorGroundY(body.Tile)),
                    targetRotation);
            view.transform.SetPositionAndRotation(targetPose.Position, targetPose.Rotation);

            if (_corpseActorViews.TryGetValue(key, out var actor) && actor != null)
            {
                // баг #170: земля под телом — свойство места, где оно лежит СЕЙЧАС.
                // Тело носят (§118.4 спасение, ручной перенос), и без этой
                // строки оно садилось на высоту тайла, где человек умер.
                actor.SetDeathSurfaceY(ActorGroundY(body.Tile));
                actor.SyncWorn(body.WornItems);
                var follower = actor.GetComponent<CarriedPoseFollower>();
                if (body.CarriedByNpcId is { } carrierNpcId)
                {
                    if (follower == null)
                    {
                        follower = actor.gameObject.AddComponent<CarriedPoseFollower>();
                    }

                    follower.Bind(carrierNpcId);
                    actor.SetCorpseCarried(true);
                }
                else
                {
                    follower?.Unbind();
                    actor.SetCorpseCarried(false);
                }

                var decay = TryGetDeathTick(snapshot, key, out var deathTick)
                    ? Mathf.Clamp01((snapshot.Tick - deathTick) /
                        (float)CorpseSystem.HumanCorpseLifetimeTicks)
                    : 0f;
                actor.SetSkinWeathering(body.TanLevel, body.Sunburn, 0f,
                    Mathf.Lerp(body.Hygiene, 0f, decay));
            }
        }

        _staleCorpseKeys.Clear();
        foreach (var key in _corpseViews.Keys)
        {
            if (!_liveCorpseIds.Contains(key))
            {
                _staleCorpseKeys.Add(key);
            }
        }

        foreach (var key in _staleCorpseKeys)
        {
            Destroy(_corpseViews[key]);
            _corpseViews.Remove(key);
            _corpseActorViews.Remove(key);
            ContentResidency.ForgetCorpse(key);
        }
    }

    // Spec 31B.5: wardrobe, walk animation, and gaze follow the snapshot.
    private void SyncRomancePairs(WorldSnapshot snapshot)
    {
        _activeRomancePairs.Clear();
        var catalog = HexLive.UnityPresentation.Content.AtomicResources.Load<RomancePoseCatalog>(
            RomancePoseCatalog.ResourcesPath);
        if (catalog != null)
        {
            foreach (var female in snapshot.Npcs)
            {
                if (female.CurrentInteraction != "Romance" ||
                    female.RomancePartnerNpcId is not { } partnerId ||
                    female.ActorMesh is "Kshishtof" or "Tonny" ||
                    !catalog.TryGet(female.RomanceClipKey, out var entry) ||
                    entry.femaleLoop == null || entry.maleLoop == null ||
                    !_actorViews.TryGetValue(female.Id.Value, out var femaleView) ||
                    !_actorViews.TryGetValue(partnerId, out var maleView))
                {
                    continue;
                }

                NpcSnapshot? male = null;
                foreach (var candidate in snapshot.Npcs)
                {
                    if (candidate.Id.Value == partnerId)
                    {
                        male = candidate;
                        break;
                    }
                }
                if (male == null || male.CurrentInteraction != "Romance") continue;

                if (!_romancePairs.TryGetValue(female.Id.Value, out var pair) ||
                    pair == null)
                {
                    pair = femaleView.GetComponent<RomancePairView>();
                    if (pair == null)
                    {
                        pair = femaleView.gameObject.AddComponent<RomancePairView>();
                    }
                    _romancePairs[female.Id.Value] = pair;
                }

                var anchor = SimulationUnityMapper.ToUnityPosition(
                    new Float2(female.RomanceAnchorX, female.RomanceAnchorY),
                    ActorGroundY(female.Tile));
                var rotation = Quaternion.Euler(0f,
                    SimulationUnityMapper.ToUnityYawDegrees(
                        female.RomanceFacingDegrees), 0f);
                pair.Sync(femaleView, maleView, entry,
                    female.Id.Value, partnerId,
                    snapshot.Tick, female.ExecutionStartTick,
                    female.ExecutionEndTick, snapshot.TickDeltaTime,
                    anchor, rotation,
                    _runner != null && !_runner.IsPaused
                        ? _runner.SpeedMultiplier : 1f,
                    female.RomanceForced);
                _activeRomancePairs.Add(female.Id.Value);
            }
        }

        _staleRomancePairs.Clear();
        foreach (var existing in _romancePairs)
        {
            if (!_activeRomancePairs.Contains(existing.Key))
            {
                existing.Value?.Stop();
                _staleRomancePairs.Add(existing.Key);
            }
        }
        foreach (var stale in _staleRomancePairs)
        {
            _romancePairs.Remove(stale);
        }
    }

    private readonly Dictionary<int, int> _lastIntimacyGroundCueTick = new();

    private void SyncGroundIntimacyStain(WorldSnapshot snapshot, NpcSnapshot npc,
        NpcActorView? actorView, Vector3 fallbackPosition)
    {
        var npcId = npc.Id.Value;
        if (!_lastIntimacyGroundCueTick.TryGetValue(npcId, out var seenTick))
        {
            // Loading an old cue must not create a puddle under wherever the
            // woman happens to stand after loading; only a new completion edge
            // in this renderer session owns a precise animated pelvis pose.
            _lastIntimacyGroundCueTick[npcId] = npc.SocialCueTick;
            return;
        }

        if (npc.SocialCueTick == seenTick)
        {
            return;
        }

        _lastIntimacyGroundCueTick[npcId] = npc.SocialCueTick;
        var completed = npc.SocialCueKind == "RomanceCompleted" ||
            npc.SocialCueKind == "RomanceTraumatized";
        if (!completed || npc.SocialCueTick != snapshot.Tick ||
            npc.ActorMesh is "Kshishtof" or "Tonny" ||
            _waterCoords.Contains(npc.Tile))
        {
            return;
        }

        var pelvis = fallbackPosition;
        if (actorView != null && actorView.TryGetBodyCenter(out var animatedPelvis))
        {
            pelvis.x = animatedPelvis.x;
            pelvis.z = animatedPelvis.z;
        }

        pelvis.y = ActorGroundY(GroundTileUnder(npc));
        EnsureIntimacyStains().Spawn(npcId, pelvis, snapshot.Tick,
            !_indoorCoords.Contains(npc.Tile));
    }

    private void SyncActorView(WorldSnapshot snapshot, NpcSnapshot npc, bool paintHidden = false)
    {
        if (!_actorViews.TryGetValue(npc.Id.Value, out var actorView) || actorView == null)
        {
            return;
        }

        // Fast-forward: animations play at the sim's speed multiplier, so 2× /
        // 4× / 50× worlds move bodies 2× / 4× / 50× faster too (1× on pause).
        actorView.SetSimSpeed(_runner != null && !_runner.IsPaused ? _runner.SpeedMultiplier : 1f);
        // §40.18-B: in deep water the animator swims (tread idle / strokes).
        actorView.SetSwimming(_swimCoords.Contains(npc.Tile));
        // Spec §67: what the feet land on — wading water beats sand beats
        // grass — so the view picks the right footstep sample.
        actorView.SetGroundSurface(
            _waterCoords.Contains(npc.Tile) ? NpcActorView.GroundSurface.Water
            : _sandCoords.Contains(npc.Tile) ? NpcActorView.GroundSurface.Sand
            : NpcActorView.GroundSurface.Grass);
        // §21.21B: sim-driven hex-step jump — the view flies its ballistic
        // arc (and compresses the jump clip) over the sim's hop window. The
        // height delta is the EXACT root-level difference, so a dive into
        // water lands at swim depth (ActorGroundY handles the sink), not one
        // dry step down.
        // §21.21B v4 circle climbing: the sim itself steps her back onto the
        // hex inner circle during the takeoff beat and flies circle-to-circle
        // — the view only needs the exact height delta and the water flag.
        // §21.21B v15: BOTH ends come from the hop's own tiles. Deriving the
        // start from npc.Tile made the delta ZERO whenever the frame first saw
        // the hop after the sim had already committed the landing tile (it does
        // so mid-window) — the body then stayed at the old level for the rest of
        // the window and snapped a whole step when the arc was cut: the "she is
        // either above the ground or suddenly under it" report.
        // §21.21B v22: the drop's vertical is synced to the VISIBLE crossing of
        // the border between the two hop tiles — hand the view that border's
        // midpoint and the flight direction (tile centres are robust for both).
        var hopFromCenter = SimulationUnityMapper.ToUnityTilePosition(npc.HopFromTile);
        var hopTargetCenter = SimulationUnityMapper.ToUnityTilePosition(npc.HopTargetTile);
        var hopFlightDirection = Vector3.ProjectOnPlane(
            hopTargetCenter - hopFromCenter, Vector3.up).normalized;
        var hopEdgePoint = (hopFromCenter + hopTargetCenter) * 0.5f;
        actorView.SetHopSignal(npc.HopKind,
            npc.HopKind.Length > 0
                ? ActorGroundY(npc.HopTargetTile) - ActorGroundY(npc.HopFromTile)
                : 0f,
            npc.HopKind.Length > 0 ? ActorGroundY(npc.HopFromTile) : 0f,
            npc.HopKind.Length > 0 && _swimCoords.Contains(npc.HopTargetTile),
            npc.HopStartTick,
            // How much of the hop already happened before this frame saw it —
            // fast-forward can step past several ticks between renders.
            Mathf.Max(0f, (snapshot.Tick - npc.HopStartTick) * snapshot.TickDeltaTime),
            hopEdgePoint,
            hopFlightDirection,
            snapshot.TickDeltaTime);
        UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.SyncWorn");
        actorView.SyncWorn(npc.WornItems);
        UnityEngine.Profiling.Profiler.EndSample();
        var earlyThermalForSweat = UI.DebugControlsPanel.SweatOverride ?? npc.ThermalComfort;
        var earlyUncoveredForDecals = UI.DebugControlsPanel.HideClothing ? AllBodyZones : npc.UncoveredParts;
        var earlyRainWet = snapshot.IsRaining && !_indoorCoords.Contains(npc.Tile) ? 1f : 0f;
        var earlyWaterWet = _waterCoords.Contains(npc.Tile) ? 1f : 0f;
        // Спрятанная туманом не красится: бейки кожи невидимой — расход
        // тикового кадра впустую. Painter диффит по уже нанесённому, поэтому
        // на тике появления (paintHidden=false в том же цикле, что и
        // SetActive) один вызов дорисовывает всё накопившееся.
        if (!paintHidden)
        {
            UnityEngine.Profiling.Profiler.BeginSample("Hex.RSA.BodyCondition");
            actorView.SetBodyCondition(npc.BodyParts, earlyUncoveredForDecals, npc.Hygiene, earlyThermalForSweat,
                earlyRainWet, earlyWaterWet, npc.WornWetness, npc.WornDirtiness, npc.WornBloodiness,
                npc.Wounds, npc.BandagedZones, npc.SeveredParts, npc.BodyPartConditions);
            UnityEngine.Profiling.Profiler.EndSample();
        }
        var heldItemId = IsProne(npc) && IsToolOrWeapon(npc.HeldItemId) ? string.Empty : npc.HeldItemId;
        // §77.5: the interaction window goes with the verb — the view fits one
        // playthrough of the work clip into it.
        actorView.SetInteraction(npc.CurrentInteraction, heldItemId, npc.AidTargetLyingDown,
            npc.InteractionSeconds, npc.LyingStationSlot);
        // §119/#83: one progress indicator belongs to the working person, not
        // to the table/project. Its component follows the animated head bone in
        // LateUpdate, so sitting and lying poses need no renderer-side offsets.
        var showWorldProgress = false;
        var worldProgress = 0f;
        if (npc.ExecutionStatus == "InProgress" && npc.TargetObjectId is { } progressTargetId)
        {
            foreach (var progressObject in snapshot.Objects)
            {
                if (progressObject.Id.Value != progressTargetId ||
                    progressObject.CraftWorkRequired <= 0)
                {
                    continue;
                }

                showWorldProgress = progressObject.CraftWorkDone < progressObject.CraftWorkRequired;
                worldProgress = Mathf.Clamp01(progressObject.CraftWorkDone /
                    (float)Mathf.Max(1, progressObject.CraftWorkRequired));
                break;
            }
        }
        actorView.SetWorldProgress(worldProgress, showWorldProgress);
        // Spec §52.8: leg-slung tools — the holster shows a carried axe/knife/
        // hammer on the thigh whenever that tool is not the one in her hand.
        actorView.SyncHolster(npc.HolsteredItems, heldItemId);
        // §Wardrobe-anim: the two-beat dress/undress sequence (gather + garment
        // in hand). Runs after SetInteraction, which it overrides for these verbs.
        actorView.SetWardrobeAction(npc.CurrentInteraction, npc.ProgressAt(snapshot.Tick), npc.HeldGarmentId,
            npc.HeldGarmentDurability, npc.HeldGarmentDirt, npc.HeldGarmentBlood, npc.HeldGarmentWet);
        // Spec 28.15E: overhead chat bubble — show the talk's emoji, and pop a
        // "+/-" once when a talk outcome resolves (new TalkResultTick).
        // §67.10: the same bubble is now the mouth of every utterance — the
        // director also needs her body (for self-talk) and her current verb
        // (for work beats). Presentation-only: nothing here feeds the sim.
        // §108: тема разговора может быть ЧЕЛОВЕКОМ — тогда в пузыре его
        // круглое лицо, а не значок. Портрет уже с маской (§90), поэтому
        // достаточно взять его по id; снимка ещё нет — просим снять вне
        // очереди и в этот раз показываем значок (та же дорожка, что у кьюшек).
        Sprite topicFace = null;
        if (npc.TalkTopicPeerId is { } topicPeerId && _portraitCache != null)
        {
            topicFace = _portraitCache.SpriteFor(topicPeerId);
            if (topicFace == null)
            {
                _portraitCache.RequestNow(topicPeerId);
            }
        }

        actorView.SetTalkTopic(npc.TalkTopic, topicFace);
        actorView.SetSpeechState(new UI.SpeechCatalog.BodyState
        {
            Hunger = npc.Hunger,
            Thirst = npc.Thirst,
            Energy = npc.Energy,
            ThermalComfort = npc.ThermalComfort,
            Hygiene = npc.Hygiene,
            Social = npc.Social,
            Wetness = Mathf.Max(earlyRainWet, earlyWaterWet),
            Wounded = npc.Wounds.Count > 0,
            Sick = HasEffect(npc, "Sick"),
            Asleep = npc.CurrentInteraction == "Sleep",
            // §105: умирающая для речи и мимики — такое же выключенное тело,
            // как потерявшая сознание: реплик не подаёт, пузырей не рисует.
            // §105.14: притворяющаяся молчит по своей воле — труп не болтает.
            Fainted = npc.IsFainted || npc.IsUnconscious || npc.IsDying || npc.IsPlayingDead,
            // §110: рыдающая, наоборот, ГОВОРИТ — всхлипы и есть смысл сцены.
            Crying = npc.IsCrying
        });
        actorView.SetSpeechInteraction(npc.CurrentInteraction);
        if (npc.SocialCueTick > 0 && !string.IsNullOrEmpty(npc.SocialCueKind))
        {
            var cueKey = $"{npc.SocialCueTick}:{npc.SocialCueKind}:" +
                $"{npc.SocialCuePeerId ?? -1}:{npc.SocialCueItemId}";

            // Owner-bundle icons are intentionally non-blocking. Keep polling
            // only while the same cue still owns the bubble; the director
            // rejects a late result after any newer alarm or conversation.
            if (_pendingSocialCueItemKey.TryGetValue(npc.Id.Value, out var pendingKey))
            {
                if (pendingKey != cueKey || snapshot.Tick - npc.SocialCueTick > 40)
                {
                    _pendingSocialCueItemKey.Remove(npc.Id.Value);
                }
                else
                {
                    var loadedItem = HexLive.UnityPresentation.Wearing.Garments.ItemIcons.Load(
                        npc.SocialCueItemId);
                    if (loadedItem != null)
                    {
                        actorView.TryRefreshSocialCuePicture(npc.SocialCueKind, loadedItem);
                        _pendingSocialCueItemKey.Remove(npc.Id.Value);
                    }
                }
            }

            if (!_lastSocialCueKey.TryGetValue(npc.Id.Value, out var seenCue) || seenCue != cueKey)
            {
                _lastSocialCueKey[npc.Id.Value] = cueKey;
                _pendingSocialCueItemKey.Remove(npc.Id.Value);
                // §80: peer — тот, О КОМ кьюшка, и его лицо едет в пузырь.
                // Раньше id молча выбрасывался, и все кьюшки выглядели
                // одинаково: ⚠️ есть, а кого испугалась — непонятно.
                Sprite cuePicture = null;
                if (!string.IsNullOrEmpty(npc.SocialCueItemId))
                {
                    cuePicture = HexLive.UnityPresentation.Wearing.Garments.ItemIcons.Load(
                        npc.SocialCueItemId);
                }
                else if (npc.SocialCuePeerId is { } peerId && _portraitCache != null)
                {
                    cuePicture = _portraitCache.SpriteFor(peerId);
                    if (cuePicture == null)
                    {
                        // Снимка ещё нет (первая встреча). Показываем эмодзи,
                        // а лицо просим снять вне очереди — ко второму испугу
                        // оно будет.
                        _portraitCache.RequestNow(peerId);
                    }
                }

                actorView.PopSocialCue(npc.SocialCueKind, cuePicture);
                if (!string.IsNullOrEmpty(npc.SocialCueItemId) && cuePicture == null)
                {
                    _pendingSocialCueItemKey[npc.Id.Value] = cueKey;
                }
            }
        }

        if (npc.TalkResultTick > 0 &&
            (!_lastTalkResultTick.TryGetValue(npc.Id.Value, out var seenTick) ||
             seenTick != npc.TalkResultTick))
        {
            _lastTalkResultTick[npc.Id.Value] = npc.TalkResultTick;
            actorView.PopRelationship(npc.TalkResultDelta);
        }
        // Edge pose applies to objectless ground sitting and shore washing.
        // Furniture/object seats (including palm stumps) own their own anchor.
        actorView.SetLedgeSit(npc.IsLedgeSit && !HasObjectSitTarget(snapshot, npc),
            npc.LedgeSeatStepsUp, npc.CurrentInteraction == "WashClothes");
        // Spec 20.16: hunting/combat shows the weapon and drives a draw/thrust.
        // Timed melee: IsSwinging spans the sim's attack-animation window.
        actorView.SetCombat(npc.IsFighting, WeaponFor(npc), npc.IsSwinging, npc.StrikeIndex,
            npc.SwingStartTick);
        // ⭐ §104 r5: ВОТ СЕЙЧАС по ней попали — кровь, вздрагивание и звук
        // удара одним кадром, по штампу из симуляции.
        actorView.SignalHit(npc.HitStampTick, npc.HitWeaponId, npc.HitPart,
            SimulationUnityMapper.ToUnityPosition(npc.HitFrom, ActorGroundY(npc.Tile)));
        // §29C.3-hit: a health drop staggers her — only while standing still.
        // Остаётся фолбэком для урона НЕ от удара (падение, огонь): там
        // хит-штампа нет, а вздрогнуть всё равно надо.
        actorView.SignalHealth(npc.Health);
        // Spec 33.1: a carried weapon rides slung on the back when it isn't in
        // the hand (SetBackWeapon hides it if it's the current hand prop).
        actorView.SetBackWeapon(BackWeaponFor(npc));
        // Spec 40.7: shiver when cold, fan when hot (signed thermal comfort).
        actorView.SetThermal(npc.ThermalComfort);
        // Face mood: the expression mirrors overall wellbeing — fed needs and
        // health lift it, hunger/thirst/exhaustion drag it down; a fight
        // switches the face to anger.
        var wellbeing =
            ((1f - npc.Hunger) + (1f - npc.Thirst) + npc.Energy +
             npc.Comfort + npc.Social + npc.Health) / 6f;
        actorView.SetFaceMood(wellbeing, npc.IsFighting);
        // A fresh bleeding wound makes her wince in pain. A wound bleeds only
        // while fresh (heal01 < 0.3, spec 44); pain scales with the freshest
        // open wound and fades to nothing as they clot/heal.
        var pain = 0f;
        if (npc.Wounds != null)
        {
            foreach (var w in npc.Wounds)
            {
                var wparts = w.Split('|');
                if (wparts.Length >= 3 && float.TryParse(wparts[2],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var heal))
                {
                    pain = Mathf.Max(pain, Mathf.Clamp01((0.3f - heal) / 0.3f));
                }
            }
        }

        actorView.SetFacePain(pain);
        // Spec 40.9 / 40.1: injury posture (limp/crawl/arm-hang/head-clutch)
        // and the winded panting, both derived sim-side and exported.
        actorView.SetPosture(npc.PostureHint, npc.Winded, npc.LegsLost);
        actorView.SetCarryingPerson(npc.CarriedNpcId is not null);
        // §118.4: the carried body follows the carrier's HANDS, posed by the
        // imported BeingCarried clip — bind/unbind the follower off the same
        // carry link the shoulder-height ride pose already reads.
        var carriedFollower = actorView.GetComponent<CarriedPoseFollower>();
        if (npc.CarriedByNpcId is { } carrierNpcId)
        {
            if (carriedFollower == null)
            {
                carriedFollower = actorView.gameObject.AddComponent<CarriedPoseFollower>();
            }

            carriedFollower.Bind(carrierNpcId);
        }
        else if (carriedFollower != null)
        {
            carriedFollower.Unbind();
        }
        // §71: the gait comes from the SIM, not from measured speed — she walks
        // unless the sim gave her a reason to run.
        actorView.SetRunning(npc.IsRunning);
        actorView.SetSadWalk(npc.IsSadWalk); // §81.10
        // Spec 40.7/40.8: weather the bare skin — tan browns it, sunburn
        // reddens it. (The old low-HP bruised-red whole-body flush was
        // retired: the painted wound marks carry the injury look on their
        // own — the flush just muddied them.)
        actorView.SetSkinWeathering(npc.TanLevel, npc.Sunburn, 0f, npc.Hygiene);
        // Spec 40.8/40.6: persistent skin decals — wound marks per hurt zone,
        // dust as hygiene drops, sweat droplets in the heat. Bare zones only.
        // Debug overrides: forced sweat, and clothes-off exposes every zone.
        var thermalForSweat = UI.DebugControlsPanel.SweatOverride ?? npc.ThermalComfort;
        var uncoveredForDecals = UI.DebugControlsPanel.HideClothing ? AllBodyZones : npc.UncoveredParts;
        // Spec 35.5: rain reuses the sweat tech — an NPC standing outdoors in
        // the rain glistens and beads exactly like sweating; garments carry
        // their own sim wetness (soaked cloth shines/darkens, dries back).
        // Water tiles (swim AND wade — the sim's TileFlags.Water) dunk the
        // skin outright, so she climbs out of the sea wet and dries slowly.
        var rainWet = snapshot.IsRaining && !_indoorCoords.Contains(npc.Tile) ? 1f : 0f;
        var waterWet = _waterCoords.Contains(npc.Tile) ? 1f : 0f;
        actorView.SetBodyCondition(npc.BodyParts, uncoveredForDecals, npc.Hygiene, thermalForSweat,
            rainWet, waterWet, npc.WornWetness, npc.WornDirtiness, npc.WornBloodiness,
            npc.Wounds, npc.BandagedZones, npc.SeveredParts, npc.BodyPartConditions);
        actorView.SetClothingHidden(UI.DebugControlsPanel.HideClothing);
        // Portrait isolation: keep the whole actor hierarchy (incl. garments,
        // props and decals spawned this tick) on the Actors layer.
        actorView.EnsureActorLayer();
        // Spec 40.10: tear worn-out garments — cutoff erosion by durability.
        foreach (var entry in npc.WornDurability)
        {
            var tab = entry.IndexOf('\t');
            if (tab > 0 && float.TryParse(
                    entry.Substring(tab + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var durability))
            {
                actorView.SetGarmentWear(entry.Substring(0, tab), durability);
            }
        }

        // Spec 31C.2: sleeping happens lying on the bed's attach point.
        //
        // §105: ПАДЕНИЕ — своя цепочка клипов. Умирающая (§105), потерявшая
        // сознание от кровопотери (§60) и сбитая с ног обмороком (§40.13)
        // теперь ВАЛЯТСЯ: FallDown → FallenIdle → StandUp. Раньше все трое
        // ложились сонным LieDown, и «упала замертво» читалось как «прилегла»
        // — тело аккуратно опускалось на землю в позе спящей.
        //
        // Сон остался на прежней цепочке: он уходит в кровать, к точке
        // крепления, и держит свою позу. Крах от истощения (§60 r2) экспортёр
        // намеренно выдаёт за сон — она и правда просто заснула, где стояла, —
        // так что он тоже остаётся здесь.
        // §105.14: притворяется мёртвой — той же цепочкой падения, что и
        // умирающая. Клип не нужен: FallenIdle и есть застывшее лежачее тело,
        // притворство — это просто «лежит дольше». Сонную цепочку взять
        // нельзя: она читалась бы как «прилегла», а вся суть в том, что для
        // волка она труп.
        //
        // §113: ветки ниже — разбор ПО ПОЗАМ; их союз обязан совпадать с
        // IsLyingDown (там же мнётся трава). Добавил лежачее состояние — добавь
        // его в оба места, иначе тело ляжет в нетронутую траву.
        if (npc.IsDying || npc.IsUnconscious || npc.IsPlayingDead)
        {
            // Spec 40.13 v2: collapse lies down with the baked laying clip —
            // ragdoll physics is retired (no tile colliders to land on).
            // §105: умирает или без сознания от кровопотери — валится и ЛЕЖИТ
            // безвольно, пока её не поднимут.
            actorView.SetRagdoll(false);
            actorView.SetCrying(false);
            var fallenSurfaceY = ActorGroundY(npc.Tile);
            Transform fallenAttach = null;
            if (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress" &&
                npc.TargetObjectId is not null)
            {
                fallenAttach = FindBedAttachPoint(snapshot, npc, out fallenSurfaceY);
            }
            actorView.SetFallen(true, sleepAfter: false, fallenSurfaceY, fallenAttach);
        }
        else if (npc.IsFainted)
        {
            // §40.13/§105: потеряла сознание — рухнула тем же клипом, но дальше
            // просто спит: обморок это не кома, и вставать ей обычным GetUp.
            actorView.SetRagdoll(false);
            actorView.SetCrying(false);
            actorView.SetFallen(true, sleepAfter: true, ActorGroundY(npc.Tile));
        }
        else if (npc.IsCrying)
        {
            // §110: сломалась от стресса — она НЕ падает, а ложится: сонная
            // цепочка LieDown → Sleep, на земле, где стояла (кровати тут нет —
            // она не дошла бы). Позу и лицо доводит NpcActorView.SetCrying.
            actorView.SetRagdoll(false);
            actorView.SetCrying(true);
            actorView.SetLaying(true, null, ActorGroundY(npc.Tile));
        }
        else if (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress")
        {
            actorView.SetRagdoll(false);
            actorView.SetCrying(false);
            actorView.SetLaying(true, FindBedAttachPoint(snapshot, npc, out var bedSurfaceY), bedSurfaceY);
        }
        else
        {
            actorView.SetRagdoll(false);
            actorView.SetCrying(false);
            // Снимает ОБА флага разом (см. ApplyLying): «не лежит» одинаково
            // верно для обеих цепочек, и оставленный висеть второй bool держал
            // бы её на земле уже на ногах.
            actorView.SetLaying(false, null);
        }

        // §137: «просто отдыхает» — теперь это поза, а не пустая строка
        // состояния. Стоит ПОСЛЕ разбора лежачих веток намеренно: сидение
        // взаимоисключается с лежанием, и NpcActorView сверяется со своим
        // свежим флагом лежания, а не с тем, что было тиком раньше.
        actorView.SetResting(
            npc.CurrentInteraction == "Rest" && npc.ExecutionStatus == "InProgress");

        var hasTargetObject = TryGetTargetObject(snapshot, npc, out var targetObject);
        if (TryGetActionTargetPoint(snapshot, npc, hasTargetObject, targetObject, out var actionTarget))
        {
            actorView.SetActionTargetPoint(actionTarget);
        }
        else
        {
            actorView.ClearActionTarget();
        }

        if (npc.CurrentInteraction == "Talk")
        {
            var partner = FindNearestOtherNpc(snapshot, npc);
            if (partner is not null)
            {
                var head = SimulationUnityMapper.ToUnityPosition(partner.Position, GroundY(partner.Tile));
                head.y += HexRadius * NpcHeightFactor * 1.8f;
                actorView.LookAtPoint(head);
                return;
            }
        }

        if (ShouldLookAtInteractionTarget(npc) && hasTargetObject)
        {
            var targetPoint = GetObjectAnchorPosition(snapshot, targetObject);
            targetPoint.y += HexRadius * NpcHeightFactor * 0.8f;
            actorView.LookAtPoint(targetPoint);
            return;
        }

        if (npc.MovementStatus == "Moving" && npc.TargetTile is { } target)
        {
            var ahead = SimulationUnityMapper.ToUnityPosition(
                HexSpatialMath.TileToWorld(target), GroundY(target));
            ahead.y += HexRadius * NpcHeightFactor * 1.5f;
            actorView.LookAtPoint(ahead);
            return;
        }

        actorView.ClearGaze();
    }

    // §118: the simulation pins the patient's XZ and yaw to the carrier. The
    // presentation adds only the shoulder height: the existing fallen pose is
    // already a limp horizontal body, so it rides across the walking carrier
    // without an external animation asset.
    private bool TryGetCarriedPose(
        WorldSnapshot snapshot, NpcSnapshot patient, Quaternion patientRotation,
        out Pose pose)
    {
        pose = default;
        if (patient.CarriedByNpcId is not { } carrierId)
        {
            return false;
        }

        NpcSnapshot carrier = null;
        foreach (var candidate in snapshot.Npcs)
        {
            if (candidate.Id.Value == carrierId)
            {
                carrier = candidate;
                break;
            }
        }

        if (carrier == null || carrier.CarriedNpcId != patient.Id.Value)
        {
            return false;
        }

        var position = SimulationUnityMapper.ToUnityPosition(
            carrier.Position, ActorGroundY(carrier.Tile));
        position.y += HexRadius * NpcHeightFactor * 1.55f;
        pose = new Pose(position, patientRotation);
        return true;
    }

    private bool IsGroundCoconutHandInteraction(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        if ((npc.CurrentInteraction != "Eat" && npc.CurrentInteraction != "Drink") ||
            string.IsNullOrEmpty(npc.HeldItemId) ||
            !IsCoconutDefinition(npc.HeldItemId) ||
            !TryGetTargetObject(snapshot, npc, out var targetObject))
        {
            return false;
        }

        return npc.HeldItemId == targetObject.DefinitionId &&
            IsCoconutDefinition(targetObject.DefinitionId);
    }

    private static bool ShouldLookAtInteractionTarget(NpcSnapshot npc) =>
        npc.CurrentInteraction is "Process" or "Harvest" or "Butcher";

    private bool TryGetActionTargetPoint(
        WorldSnapshot snapshot,
        NpcSnapshot npc,
        bool hasTargetObject,
        ObjectSnapshot targetObject,
        out Vector3 point)
    {
        if (hasTargetObject && ShouldUseObjectActionTarget(npc))
        {
            point = GetObjectActionTargetPosition(snapshot, targetObject);
            return true;
        }

        if (npc.IsFighting && TryGetCombatActionTargetPosition(snapshot, npc, out point))
        {
            return true;
        }

        point = default;
        return false;
    }

    private static bool ShouldUseObjectActionTarget(NpcSnapshot npc) =>
        npc.CurrentInteraction is "Harvest" or "Process" or "Butcher";

    private static bool IsCoconutDefinition(string definitionId) =>
        definitionId.StartsWith("food.coconut", System.StringComparison.Ordinal);

    private Vector3 GetObjectActionTargetPosition(WorldSnapshot snapshot, ObjectSnapshot worldObject)
    {
        var point = GetObjectAnchorPosition(snapshot, worldObject);
        point.y += ActionTargetLift(worldObject.DefinitionId);
        return point;
    }

    private float ActionTargetLift(string definitionId)
    {
        if (definitionId == "tree.palm")
        {
            return HexRadius * TreeHeightFactor * 0.65f;
        }

        if (definitionId == "tree.palm_small")
        {
            return HexRadius * TreeHeightFactor * 0.45f;
        }

        if (definitionId == "rock.boulder")
        {
            return HexRadius * 0.28f;
        }

        if (definitionId == "carcass.animal" || definitionId == "corpse.npc")
        {
            return HexRadius * 0.18f;
        }

        return HexRadius * FoodRadiusFactor * 1.8f;
    }

    /// <summary>
    /// ⭐ §104 r10: КУДА ТЯНЕТСЯ РУКА В БОЮ — по НАЗВАННОМУ противнику, а не по
    /// «ближайшему кому-нибудь».
    ///
    /// <para>
    /// Здесь стояло «ближайшая живая собака на всём острове, иначе ближайший
    /// дерущийся NPC» — без единого ограничения по расстоянию. Кшиштоф бил
    /// колонистку кулаком в упор, а тело уезжало к волку за полкарты: IK честно
    /// тянул руку к цели, которую вид выбрал сам. Симуляция при этом знала
    /// точный ответ (<c>Mind.CombatOpponentNpcId</c>, у собаки — её
    /// <c>TargetNpcId</c>), просто её не спрашивали. Шестая копия правила «кто
    /// мой противник», и та же болезнь, что «бьёт ножом, а урон как рукой».
    /// </para>
    /// <para>
    /// Потолок дистанции остаётся страховкой на случай рассинхрона: цель дальше
    /// удара — значит цели нет, и рука опускается, а не тянется через остров.
    /// </para>
    /// </summary>
    private bool TryGetCombatActionTargetPosition(
        WorldSnapshot snapshot, NpcSnapshot npc, out Vector3 point)
    {
        // Собака, которая дерётся ИМЕННО С НЕЙ.
        foreach (var dog in snapshot.Mobs)
        {
            if (dog.Health > 0f && dog.TargetNpcId == npc.Id.Value &&
                WithinStrikeReach(npc, dog.Position))
            {
                point = SimulationUnityMapper.ToUnityPosition(dog.Position, GroundY(dog.Tile));
                point.y += HexRadius * NpcHeightFactor * 0.6f;
                return true;
            }
        }

        // Человек, с которым она в паре — так её назвала симуляция.
        if (npc.CombatOpponentNpcId >= 0)
        {
            foreach (var other in snapshot.Npcs)
            {
                if (other.Id.Value != npc.CombatOpponentNpcId ||
                    !WithinStrikeReach(npc, other.Position))
                {
                    continue;
                }

                point = SimulationUnityMapper.ToUnityPosition(
                    other.Position, ActorGroundY(other.Tile));
                point.y += HexRadius * NpcHeightFactor * 1.1f;
                return true;
            }
        }

        point = default;
        return false;
    }

    // Потолок в МИРОВЫХ ЕДИНИЦАХ симуляции — тех же, в которых лежит
    // NpcSnapshot.Position. Соседний тайл отстоит на 2.25-2.6 wu
    // (HexRadius 1.5 × шаг), так что 3.5 накрывает бой в упор и через
    // границу тайла, но не тянет руку через остров. Это НЕ гейт удара
    // (он в §26.6A InteractionReach, по соседству узлов) — только страховка
    // прицела на случай, когда пара в снапшоте разъехалась с картинкой.
    private const float StrikeReachWorldUnits = 3.5f;

    private static bool WithinStrikeReach(NpcSnapshot npc, Float2 target)
    {
        var dx = target.X - npc.Position.X;
        var dy = target.Y - npc.Position.Y;
        return dx * dx + dy * dy <= StrikeReachWorldUnits * StrikeReachWorldUnits;
    }

    // Spec 33.1 / §75A: personal taste chooses the carried melee weapon for
    // the existing upper-back slot. SetBackWeapon hides it while it is held.
    private static string BackWeaponFor(NpcSnapshot npc)
    {
        if (IsProne(npc))
        {
            return null;
        }

        return ItemAffinity.FavoriteWeapon(npc.Id.Value, npc.InventoryItems);
    }

    // Debug clothes-off mode treats the whole body as bare for skin decals.
    private static readonly List<string> AllBodyZones = new()
    {
        "Head", "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR"
    };

    // Spec 20.16/weapon balance: the weapon an NPC fights/hunts with — bow
    // (with arrows, ranged special-case) preferred, then the SAME melee pick
    // the sim makes (GearCatalog priority, hands-aware) — the view can never
    // show a weapon the sim wouldn't actually draw (e.g. a two-handed spear
    // on a one-armed girl). Null means fists.
    private static string WeaponFor(NpcSnapshot npc)
    {
        if (IsProne(npc))
        {
            return null;
        }

        // §103 r5: ⭐ ЧЕМ ОНА БЬЁТ — спрашиваем СИМУЛЯЦИЮ, а не считаем сами.
        //
        // Здесь стоял свой расчёт «лучшее оружие из рюкзака», и он не знал про
        // оружие, назначенное сценой: наезд §97 начинается РУКОПАШКОЙ, а в руке
        // на картинке оставался нож. Снаружи это читалось как «бьёт ножом, а
        // урон как рукой» — он и правда бил кулаком, врала картинка.
        //
        // Пустая строка это кулаки (сцена так и говорит), поэтому null здесь
        // означает ровно одно: бить нечем.
        // ⚠️ Запасного расчёта здесь БОЛЬШЕ НЕТ, и это принципиально: пустая
        // строка — не «сим промолчал», а «кулаки». Оставь фолбэк на лучшее
        // оружие из рюкзака — и он вернёт нож ровно в той сцене, ради которой
        // всё это чинилось. Вне драки симуляция сама подставляет лучшее, так
        // что ответ здесь полный всегда.
        return string.IsNullOrEmpty(npc.MeleeWeaponId) ? null : npc.MeleeWeaponId;
    }

    // §50/§118: a bare stump is prone, a fitted non-broken leg is functional.
    // The typed snapshot is the presentation-side equivalent of
    // BodyState.LimbFunction; never infer this from SeveredParts alone.
    private static bool IsProne(NpcSnapshot npc) =>
        IsBareStump(npc, BodyPart.LegL, "LegL") ||
        IsBareStump(npc, BodyPart.LegR, "LegR");

    private static bool IsBareStump(NpcSnapshot npc, BodyPart part, string legacyName)
    {
        if (!npc.SeveredParts.Contains(legacyName))
        {
            return false;
        }

        foreach (var condition in npc.BodyPartConditions)
        {
            if (condition.Part != part || condition.Prosthetic == null)
            {
                continue;
            }

            var device = condition.Prosthetic;
            return device.MaxCondition <= 0f || device.Condition <= 0f || device.Function <= 0f;
        }

        return true;
    }

    private static bool IsToolOrWeapon(string itemId) =>
        !string.IsNullOrEmpty(itemId) &&
        itemId.StartsWith("tool.", System.StringComparison.Ordinal);

    // §67.10: the snapshot ships effects as "<Kind>\t<intensity>" rows.
    private static bool HasEffect(NpcSnapshot npc, string kind)
    {
        for (var i = 0; i < npc.Effects.Count; i++)
        {
            if (npc.Effects[i].StartsWith(kind, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The bed she is sleeping on: nearest bed object view within ~a tile.
    private Transform? FindBedAttachPoint(WorldSnapshot snapshot, NpcSnapshot npc, out float surfaceY)
    {
        // Default: the sleeper's own tile top, used when there is no bed.
        surfaceY = ActorGroundY(npc.Tile);

        // §66: beds sit at hex centres now, so the sleeper always stands on a
        // NEIGHBOURING tile. The bed comes ONLY from the sim's own target —
        // that is the bed she entered (BedSleep.TryEnter держит его в
        // Execution.TargetObject весь сон; сон на земле несёт null).
        //
        // ⭐ Баг #122: цель ОБЯЗАНА пройти проверку соседства. Без неё вид
        // прижимал тело к кровати, до которой полкарты: симуляция держала
        // лежачую на улице (мокла под дождём, кровь на земле), а на экране она
        // лежала в кровати. И это не только «криво нарисовано» — камера
        // кадрирует КОРЕНЬ вида (он на позиции симуляции), вырез крыш и дождь
        // идут по npc.Tile, поэтому один необеспеченный прижим читался как три
        // разных бага. Рассинхрон обязан быть ВИДЕН: тело рисуется там, где
        // оно есть.
        //
        // ⭐ Баг #174: здесь была ещё и запасная ветка «ближайшая кровать в
        // радиусе гекса» для спящих БЕЗ цели. Она приклеивала к кровати ту,
        // кто по симуляции лёг на землю РЯДОМ (кровать занята соседкой, §29G
        // кладёт вторую на соседний джанкшен) — на экране две девушки лежали
        // в одной кровати, а на деле одна из них мокла на земле. Спящая без
        // TargetObjectId лежит там, где её держит симуляция.
        ObjectSnapshot? bed = null;
        if (npc.TargetObjectId is { } sleepTargetId)
        {
            foreach (var worldObject in snapshot.Objects)
            {
                if (worldObject.Id.Value == sleepTargetId &&
                    worldObject.DefinitionId.Contains("bed") &&
                    HexSpatialMath.HexDistance(worldObject.Tile, npc.Tile) <= 1)
                {
                    bed = worldObject;
                    break;
                }
            }
        }

        if (bed is null || !_objectViews.TryGetValue(bed.Id.Value, out var bedView))
        {
            return null;
        }

        // The body's underside should rest on the bed's top surface.
        var bedRenderers = bedView.GetComponentsInChildren<Renderer>();
        if (bedRenderers.Length > 0)
        {
            var bedBounds = bedRenderers[0].bounds;
            for (var i = 1; i < bedRenderers.Length; i++)
            {
                bedBounds.Encapsulate(bedRenderers[i].bounds);
            }

            surfaceY = bedBounds.max.y + SleepBodyLift;
        }
        else
        {
            surfaceY = ActorGroundY(bed.Tile);
        }

        var point = bedView.transform.Find("point");
        if (point == null)
        {
            foreach (var t in bedView.GetComponentsInChildren<Transform>())
            {
                if (t.name == "point")
                {
                    point = t;
                    break;
                }
            }
        }

        // Spec §54.2: an explicit "point" marker (our assembled beds place one)
        // also defines the sleep HEIGHT — the body lies at the point's Y, not the
        // bed's bbox top, so each bed's tuned lift is honoured.
        if (point != null)
        {
            surfaceY = point.position.y;
        }

        return point != null ? point : bedView.transform;
    }

    private bool TryGetFurnitureSeatPose(
        WorldSnapshot snapshot, NpcSnapshot npc, Quaternion rotation, out Pose pose)
    {
        pose = default;
        if (npc.CurrentInteraction != "Sit" ||
            !TryGetTargetObject(snapshot, npc, out var seat) ||
            (seat.DefinitionId != "stump.palm" && seat.DefinitionId != "chair.basic"))
        {
            return false;
        }

        pose = new Pose(GetObjectAnchorPosition(snapshot, seat), rotation);
        return true;
    }

    private static bool HasObjectSitTarget(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        return npc.CurrentInteraction == "Sit" &&
            TryGetTargetObject(snapshot, npc, out _);
    }

    private static bool TryGetTargetObject(
        WorldSnapshot snapshot, NpcSnapshot npc, out ObjectSnapshot target)
    {
        if (npc.TargetObjectId is { } targetId)
        {
            foreach (var worldObject in snapshot.Objects)
            {
                if (worldObject.Id.Value == targetId)
                {
                    target = worldObject;
                    return true;
                }
            }
        }

        target = null!;
        return false;
    }

    private static NpcSnapshot? FindNearestOtherNpc(WorldSnapshot snapshot, NpcSnapshot npc)
    {
        NpcSnapshot? best = null;
        var bestSq = float.MaxValue;
        foreach (var other in snapshot.Npcs)
        {
            if (other.Id.Value == npc.Id.Value)
            {
                continue;
            }

            var dx = other.Position.X - npc.Position.X;
            var dy = other.Position.Y - npc.Position.Y;
            var sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = other;
            }
        }

        return best;
    }

    private void InterpolateMovables(float alpha)
    {
        foreach (var kvp in _mobViews)
        {
            InterpolateAnimal(kvp.Value, kvp.Key, alpha);
        }

        foreach (var kvp in _crabViews)
        {
            InterpolateAnimal(kvp.Value, -kvp.Key - 1, alpha);
        }

        foreach (var kvp in _npcViews)
        {
            var key = kvp.Key;
            var view = kvp.Value;

            if (!_currNpcPoses.TryGetValue(key, out var curr))
            {
                continue;
            }

            var onWater = _npcOnWater.TryGetValue(key, out var w) && w;

            if (_prevNpcPoses.TryGetValue(key, out var prev))
            {
                var pos = Vector3.Lerp(prev.Position, curr.Position, alpha);
                if (_npcMovementRoutes.TryGetValue(key, out var route))
                {
                    var routed = SnapshotMovementRoute.Evaluate(route, alpha);
                    pos.x = routed.X;
                    pos.z = routed.Y;
                }
                // §45 hex-step jump: don't ALSO lerp the root's Y across the
                // step — the actor animates the vertical itself (jump offset),
                // so the root snaps straight to the new ground level.
                if (Mathf.Abs(curr.Position.y - prev.Position.y) > ElevationStep * 0.5f)
                {
                    pos.y = curr.Position.y;
                }

                // §40.18-B: ride the live wave swell at her exact XZ — same
                // formula the shader displaces the surface with (WaterWave).
                if (onWater)
                {
                    pos.y += WaterWave.HeightNow(pos.x, pos.z);
                }

                view.transform.position = pos;
                view.transform.rotation = Quaternion.Slerp(prev.Rotation, curr.Rotation, alpha);
            }
            else
            {
                var pos = curr.Position;
                if (onWater)
                {
                    pos.y += WaterWave.HeightNow(pos.x, pos.z);
                }

                view.transform.position = pos;
                view.transform.rotation = curr.Rotation;
            }
        }

        foreach (var kvp in _objectViews)
        {
            var key = kvp.Key;
            var view = kvp.Value;

            if (!_currObjectPositions.TryGetValue(key, out var curr))
            {
                continue;
            }

            if (_prevObjectPositions.TryGetValue(key, out var prev))
            {
                view.transform.position = Vector3.Lerp(prev, curr, alpha);
            }
            else
            {
                view.transform.position = curr;
            }
        }
    }

    private readonly HashSet<TileCoord> _waterCoords = new();
    private readonly HashSet<TileCoord> _swimCoords = new();

    // Spec 35.5: rain wets only NPCs standing outdoors.
    private readonly HashSet<TileCoord> _indoorCoords = new();

    // Spec 20.16: elevation registry — every movable and object view takes
    // its Y from its tile's top.
    private const float ElevationStep = 0.55f;
    private readonly Dictionary<TileCoord, int> _tileElevations = new();

    // Spec 20.16: prisms skirt down to a shared base so tall tiles read as
    // solid columns rooted below every neighbour — no floating tops.
    private const float TerrainBaseY = -2.5f;

    // World-space UV scales: tops sample a biome texture continuously across
    // tiles; walls stretch a cliff texture along the perimeter and vertically.
    private const float TopUvScale = 0.42f;
    private const float WallUvScaleU = 0.5f;
    private const float WallUvScaleV = 0.45f;

    private float GroundY(TileCoord coord)
    {
        var elevation = _tileElevations.TryGetValue(coord, out var e) ? e : 1;
        return SimulationUnityMapper.TileHeight + elevation * ElevationStep;
    }

    // §120.7: режим стройки и колышки стройплощадок стоят на тех же опорных
    // точках, что и собственные виды рендерера, — словарь junction'ов и высота
    // тайла у рендерера уже есть, второй копии топологии им не положено.
    public Float2 ObjectAnchorPosition(ObjectSnapshot worldObject) =>
        GetObjectAnchorFromJunctions(worldObject, _junctionPositions);

    public float GroundTopY(TileCoord coord) => GroundY(coord);

    private float ObjectGroundY(ObjectSnapshot worldObject)
    {
        var y = GroundY(worldObject.Tile);
        if (IsIntegratedHutBed(worldObject))
        {
            y += HexLive.UnityPresentation.Environment.HutFurnitureFactory.BedRootLift;
        }
        else if (IsIntegratedHutHearth(worldObject))
        {
            y += HexLive.UnityPresentation.Environment.HutAssembly.FloorSurfaceLift;
        }
        else if (_floorTiles.Contains(worldObject.Tile) &&
                 !OwnsRaisedFloorGeometry(worldObject))
        {
            // §118.2: loose items in a hut stand on the authored top of its
            // raised floor, not on the terrain hidden 0.107475 wu below it.
            // Bug #155: the hut/architecture root already carries that rise in
            // its authored local geometry. Lifting the root as well raises the
            // visible floor twice and leaves every correctly seated piece of
            // furniture buried by exactly FloorSurfaceLift.
            y += HexLive.UnityPresentation.Environment.HutAssembly.FloorSurfaceLift;
        }

        return y;
    }

    private static bool OwnsRaisedFloorGeometry(ObjectSnapshot worldObject) =>
        worldObject.DefinitionId == ContentIds.Hut1Hex ||
        worldObject.ArchitectureOwnerObjectId.HasValue;

    // §40.18-B: where an ACTOR's root sits on a tile. On land that is the
    // ground; in deep water she hangs SinkDepth below the water surface; in
    // walkable shallows (the river) she wades WadeDepth under it — knee-deep,
    // not walking ON the water.
    // §141: КАКОЙ ТАЙЛ ПОД ТЕЛОМ — а не какой записан в снапшоте.
    //
    // Вид рисует её по непрерывной Position, а высоту и «в доме ли она» брал
    // из дискретного npc.Tile. Поле ОТСТАЁТ от тела: замер на трёх сидах
    // (18 000 NPC-тиков) — в 944 переездах тайла из 955 тело уже находится
    // внутри нового гекса к моменту, когда поле только щёлкает. Стоя
    // расхождения нет ни разу, оно живёт только на ходу.
    //
    // На ровном месте это незаметно. На пороге хижины — нет: пол поднят на
    // HutAssembly.FloorSurfaceLift, а в модели мира этой ступеньки не
    // существует вовсе (те же замеры: все 21 переходов «земля ↔ пол» идут
    // между тайлами ОДИНАКОВОЙ высоты). Пока поле догоняет, тело уже стоит над
    // полом, а высота ему считается по уличному тайлу — она проходит сквозь
    // ступеньку. Крыша по той же причине раскрывалась не в тот момент, когда
    // она вошла.
    //
    // ⭐ ПРЫЖОК ИСКЛЮЧЁН НАМЕРЕННО. Там тело обязано быть между тайлами, и
    // дугой правит своя выверенная машина (§21.21B v15/v23), которая держит
    // базу на тайле ВЗЛЁТА. Сэмплировать под телом в полёте значило бы спорить
    // с ней за одну и ту же величину.
    private TileCoord GroundTileUnder(NpcSnapshot npc)
    {
        if (npc.HopKind.Length > 0)
        {
            return npc.Tile;
        }

        var under = HexSpatialMath.WorldToTile(npc.Position);
        if (!_tileElevations.ContainsKey(under))
        {
            return npc.Tile;
        }

        // ⭐ ВОДА ТОЖЕ ИСКЛЮЧЕНА. У берега высота считается не по тайлу, а по
        // отдельной привязке (SurfaceStepOffset/ShoreLevelSteps, глубина
        // погружения), и она выверялась отдельно. Менять там ИСТОЧНИК тайла
        // ради порога хижины значит чинить одно и трогать другое: суша под
        // ногами и уровень воды — разные вопросы к одному методу.
        if (_waterCoords.Contains(under) || _waterCoords.Contains(npc.Tile))
        {
            return npc.Tile;
        }

        return under;
    }

    private float ActorGroundY(TileCoord coord)
    {
        if (!_waterCoords.Contains(coord))
        {
            return GroundY(coord) + (_floorTiles.Contains(coord)
                ? HexLive.UnityPresentation.Environment.HutAssembly.FloorSurfaceLift
                : 0f);
        }

        // Water tiles render their surface at the shore level minus the
        // documented drop (spec 31C.4) — mirror CreateTileView's formula.
        var surfaceY = GroundY(coord) + ElevationStep * SwimVisuals.SurfaceStepOffset;
        return surfaceY - (_swimCoords.Contains(coord)
            ? SwimVisuals.SinkDepth
            : SwimVisuals.WadeDepth);
    }


    private bool IsSandTile(TileSnapshot tile)
    {
        if (tile.Water || tile.Indoor)
        {
            return false;
        }

        foreach (var water in _waterCoords)
        {
            if (HexSpatialMath.HexDistance(tile.Coord, water) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    private GameObject CreateTileView(TileSnapshot tile, bool sand)
    {
        var go = new GameObject($"Hex {tile.Coord.Q},{tile.Coord.R}");
        go.transform.SetParent(_tilesRoot, false);
        var position = SimulationUnityMapper.ToUnityTilePosition(tile.Coord);
        var world = HexSpatialMath.TileToWorld(tile.Coord);

        // Spec 20.16: hexes are prisms — top at the tile's elevation, skirt
        // to a shared base so hills read as solid terrain, not floating caps.
        var topHeight = SimulationUnityMapper.TileHeight + tile.Elevation * ElevationStep;
        if (tile.Water)
        {
            // Shore-anchored surface: lift to the bank, then sink the drop.
            topHeight += ElevationStep * SwimVisuals.SurfaceStepOffset;
        }

        if (tile.Water)
        {
            // Spec 20.16: the wavy water SURFACE is NOT built here. Per-tile
            // hex tops each tented independently around their centre vertex
            // (7 verts/hex under-sampled the world-space wave) and their
            // separate transparent draws double-blended along shared edges —
            // both read as visible seams between hexes. EnsureWaterSurface()
            // merges every water tile into ONE finely-tessellated mesh with a
            // single transparent draw, so the wave is one smooth even sheet.
            // Only the opaque per-tile riverbed stays here.

            // §31C.4/§40.18-B: wadable shallows get a visible RIVERBED hex
            // exactly WadeDepth under the surface — the same height the
            // actors sink to, so a knee-deep girl reads as standing on the
            // bottom instead of hovering inside translucent water.
            if (tile.Walkable)
            {
                var bed = new GameObject("Riverbed");
                bed.transform.SetParent(go.transform, false);
                var bedFilter = bed.AddComponent<MeshFilter>();
                bedFilter.sharedMesh = BuildHexPrismMesh(
                    HexRadius, topHeight - SwimVisuals.WadeDepth, TerrainBaseY,
                    world.X, world.Y, includeSkirt: true);
                var bedRenderer = bed.AddComponent<MeshRenderer>();
                // Queue 2999: drawn before the water (which tints it), but
                // ABOVE the opaque cutoff (2500) — so the bed never enters
                // _CameraDepthTexture. The stylized water reads depth from
                // that texture: with the bed visible to it the whole river
                // became "2 cm shallow" and foamed edge to edge. This way the
                // water sees the deep SeaFloor and the river renders exactly
                // like the sea, while the girls still stand on a real bottom.
                bedRenderer.sharedMaterials = new[]
                {
                    RiverbedMaterial(Jitter(BiomeColor("sand"), tile.Coord, 0.06f)),
                    RiverbedMaterial(Jitter(BiomeColor("cliff"), tile.Coord, 0.05f))
                };
            }
        }
        else
        {
            // Submesh 0 = flat top, submesh 1 = the perimeter skirt/cliff.
            var meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = BuildHexPrismMesh(
                HexRadius, topHeight, TerrainBaseY, world.X, world.Y, includeSkirt: true);
            var meshRenderer = go.AddComponent<MeshRenderer>();

            var topBiome = sand ? "sand" : TopBiome(tile.Elevation);
            // Low-poly look: flat facet colours, a subtle per-tile shade
            // jitter for a hand-placed patchwork, darker earthy cliffs.
            meshRenderer.sharedMaterials = new[]
            {
                GetFlatMaterial(Jitter(BiomeColor(topBiome), tile.Coord, 0.07f)),
                GetFlatMaterial(Jitter(BiomeColor("cliff"), tile.Coord, 0.05f))
            };

            // Spec 20.16: leafy tufts stand on grass tops — terrain grass.
            if (_grassDetail && _grassBladesPerTile > 0 &&
                (topBiome == "grass" || topBiome == "grass_dry"))
            {
                BuildGrassClump(go.transform, HexRadius, topHeight, tile.Coord);
            }
        }

        go.transform.position = position;
        return go;
    }

    // Spec 20.16: biome key by elevation band — grass lowland, dry grass,
    // brown-green hills, bare rock, mountain caps.
    private static string TopBiome(int elevation)
    {
        return elevation switch
        {
            <= 1 => "grass",
            2 => "grass_dry",
            3 => "hill",
            4 => "rock",
            _ => "mountain"
        };
    }

    private static Material _waterMaterial;

    // The live water material (sea plane + water tiles share it). The
    // day/night controller darkens its colours with the directional light so
    // the sea stops glowing at night.
    public static Material ActiveWaterMaterial { get; private set; }

    private static Material CreateWaterMaterial()
    {
        if (_waterMaterial != null)
        {
            return _waterMaterial;
        }

        // Spec 20.16: prefer the imported "Definitive Stylized Water URP"
        // material (depth gradient, animated foam, fresnel, refraction) — the
        // tuned asset carries its distortion/foam texture with it. A runtime
        // COPY: the controller tints it per frame, and tinting the loaded
        // asset directly would dirty the .mat on disk in the editor.
        //
        // ⭐ ДОАТОМАРНАЯ СХЕМА, восстановлена дословно (июль, 51eded378):
        // материал лежит в Player Resources и грузится СИНХРОННО. Через
        // атомарный бандл воду не возить: ленивый AtomicResources.Load
        // отдавал null на первом кадре, статика кэшировала fallback — и
        // definitive был мёртв с самой миграции.
        var definitive = Resources.Load<Material>("HexLive/Water/StylizedWaterDefinitive");
        if (definitive != null)
        {
            _waterMaterial = new Material(definitive);
            ActiveWaterMaterial = _waterMaterial;
            return _waterMaterial;
        }

        // Fallback: my hand-written stylized water shader.
        var stylized = Shader.Find("HexLive/StylizedWater");
        if (stylized != null)
        {
            _waterMaterial = new Material(stylized);
            ActiveWaterMaterial = _waterMaterial;
            return _waterMaterial;
        }

        // Fallback: plain transparent URP/Lit if the shader failed to import.
        var material = new Material(RequireRuntimeLitShader());
        material.SetFloat("_Surface", 1f); // transparent
        material.SetFloat("_Blend", 0f);   // alpha
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.color = new Color(0.20f, 0.45f, 0.75f, 0.6f);
        _waterMaterial = material;
        return material;
    }

    private GameObject CreateJunctionView(JunctionSnapshot junction)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = $"Junction {junction.Id.Value}";
        go.transform.SetParent(_junctionsRoot, false);
        var scale = junction.Blocked ? JunctionMarkerScale * 4f : JunctionMarkerScale;
        go.transform.localScale = Vector3.one * scale;
        go.transform.position = SimulationUnityMapper.ToUnityPosition(
            junction.WorldPosition,
            SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift);

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateMaterial(GetJunctionColor(junction));
        }

        return go;
    }

    // Spec 40.17: a small amber dot sitting on a climb seam — where an NPC
    // clambers up a one-step ledge. Scaled below the debug junction sphere so
    // it reads as a marker, lifted a touch above the ground.
    private GameObject CreateSeamMarker(JunctionSnapshot junction)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = $"ClimbSeam {junction.Id.Value}";
        go.transform.SetParent(_junctionsRoot, false);
        go.transform.localScale = Vector3.one * (JunctionMarkerScale * 1.6f);
        go.transform.position = SimulationUnityMapper.ToUnityPosition(
            junction.WorldPosition,
            SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift * 2f);

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            // Warm amber, distinct from terrain — reads as a climb hint.
            renderer.sharedMaterial = CreateMaterial(new Color(0.88f, 0.54f, 0.24f));
        }

        return go;
    }

    private GameObject CreateObjectView(
        ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions, int snapshotTick)
    {
        // Architecture pieces of the CANONICAL hut render through their owner's
        // one shared hut mesh, but remain real top-level simulation objects.
        // This marker receives a WorldObjectView bound only to its slot
        // renderers in BindArchitectureElementViews; it must never instantiate a
        // fallback primitive of its own.
        //
        // §120: a module of a house raised from a player PLAN has no such shared
        // mesh — it IS its own piece of the world, so it draws itself here, once,
        // like any other object. Its renderers exist before the WorldObjectView
        // below asks for them.
        if (worldObject.ArchitectureOwnerObjectId.HasValue)
        {
            var marker = new GameObject($"Architecture {worldObject.DefinitionId} #{worldObject.Id.Value}");
            marker.transform.SetParent(_objectsRoot, false);
            if (IsPlanBuildingModule(worldObject) ||
                FreeArchitectureRules.IsFreePiece(worldObject))
            {
                marker.AddComponent<HexLive.UnityPresentation.Environment.ArchitectureModuleView>()
                    .Sync(worldObject);
            }

            return marker;
        }

        // §28.15C v4: через двое суток тяжёлый актёр трупа уходит из снапшота.
        // Вместо него один плоский ground-sprite рисует скелет и единственный
        // мешок лута. Длина 1.32 wu совпадает с расчётным телом §113; курс приходит из сима.
        if (worldObject.DefinitionId == ContentIds.HumanRemains)
        {
            return CreateHumanRemainsView(worldObject, junctionPositions);
        }

        // Spec 31C.4: water interaction anchors have no gizmo — the river
        // and pond tiles ARE the visual; NPCs just come and drink.
        // Spec 40.13 v2: corpse.npc has no blob either — the dead actor's own
        // body (adopted, lying asleep) IS the corpse; and death spawns no
        // visible grave marker at all.
        // Spec §52 build-site presentation invariant: a build-site is a SIM-ONLY
        // intent marker. With no product/materials assigned yet it draws NOTHING —
        // no placeholder ball, no gizmo (an unmatched id would otherwise fall to
        // the grey PrimitiveType.Sphere fallback far below). Once materials are
        // hauled in it takes the assembling-pile / staged-prefab path below, which
        // shows the delivered resources — never an abstract site marker. Do NOT
        // re-add a visual here; this has regressed repeatedly.
        // §120: a RAISED plan-built house owns no mesh either. It is the
        // footprint aggregate its modules hang off — every board, post and palm
        // panel is a separate world object with its own ArchitectureModuleView —
        // so it must not fall through to a prefab lookup or the grey sphere.
        if (worldObject.DefinitionId.StartsWith("water.") ||
            worldObject.DefinitionId == "corpse.npc" ||
            worldObject.DefinitionId == "grave.npc" ||
            worldObject.DefinitionId == ContentIds.HutPlan ||
            (worldObject.DefinitionId == "build.site" && string.IsNullOrEmpty(worldObject.BuildProduct)))
        {
            var invisible = new GameObject($"Object {worldObject.DefinitionId} (anchor)");
            invisible.transform.SetParent(_objectsRoot, false);
            return invisible;
        }

        // §54.14: the campfire is the assembled staged prefab (stick pile →
        // stone ring → roasting spit), authored 1:1 like the beds — partial
        // while its upgrade bill is open, complete once it closes. The flame
        // and light attach here so a mid-upgrade fire still burns.
        if (worldObject.DefinitionId == "campfire.spot")
        {
            var isHutHearth = IsIntegratedHutHearth(worldObject);
            var fireGo = isHutHearth
                ? HexLive.UnityPresentation.Environment.HutFurnitureFactory.BuildHearth()
                : string.IsNullOrEmpty(worldObject.BuildProduct)
                    ? HexLive.UnityPresentation.Environment.BedAssembly.BuildFinished("campfire.spot")
                    : HexLive.UnityPresentation.Environment.BedAssembly.BuildPartial(
                        "campfire.spot", worldObject.DeliveredLogs, worldObject.DeliveredSticks,
                        worldObject.DeliveredRope, worldObject.DeliveredLeaves, worldObject.DeliveredStones);
            if (fireGo != null)
            {
                fireGo.transform.SetParent(_objectsRoot, false);
                var fireAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                fireGo.transform.position = SimulationUnityMapper.ToUnityPosition(
                    fireAnchor, ObjectGroundY(worldObject));
                var fireEffectHost = isHutHearth
                    ? fireGo.transform.Find("fire_point")?.gameObject ?? fireGo
                    : fireGo;
                var fireEffect = fireEffectHost.AddComponent<HexLive.UnityPresentation.Environment.CampfireEffect>();
                fireEffect.Construct(isHutHearth ? HexRadius * 0.42f : HexRadius);
                // §54.14 (r2): the spit-meat view rides on the same root; the
                // per-frame sync feeds it the hanging raw/cooked counts.
                var spitView = fireGo.AddComponent<HexLive.UnityPresentation.Environment.CampfireSpitMeat>();
                spitView.Refresh(worldObject.RoastingRaw, worldObject.RoastingCooked);
                return fireGo;
            }
            return null;
        }

        // Spec §54: a build-site shows the piece ASSEMBLING from its delivered
        // materials — a pile of hauled stones/logs/leaves growing toward the
        // bill, instead of a flat pad. Refreshed each frame as more is delivered.
        if (!string.IsNullOrEmpty(worldObject.BuildProduct))
        {
            var siteRoot = new GameObject($"Object {worldObject.DefinitionId} (site {worldObject.BuildProduct})");
            siteRoot.transform.SetParent(_objectsRoot, false);
            var siteAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            siteRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                siteAnchor, GroundY(worldObject.Tile));
            var pile = siteRoot.AddComponent<HexLive.UnityPresentation.Environment.BuildSitePile>();
            pile.Rebuild(worldObject);
            return siteRoot;
        }

        // Spec §50: a severed limb. A fresh object waits until the owner's
        // fallen/prone pose has been evaluated, bakes the readable FBX on a
        // bone-only pose clone, and stays exactly where that limb was. The
        // persisted junction remains the interaction/root anchor. Restored
        // objects use a reference-pose slice at that anchor.
        if (worldObject.DefinitionId == "body.limb_severed")
        {
            NpcActorView owner = null;
            if (worldObject.OwnerNpcId is { } ownerId)
            {
                if (!_actorViews.TryGetValue(ownerId, out owner))
                {
                    _corpseActorViews.TryGetValue(ownerId, out owner);
                }
            }

            var anchorPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            var worldPos = SimulationUnityMapper.ToUnityPosition(anchorPos, GroundY(worldObject.Tile));
            var limbRoot = new GameObject(
                $"Object {worldObject.DefinitionId} {worldObject.Variant}");
            limbRoot.transform.SetParent(_objectsRoot, false);
            limbRoot.transform.SetPositionAndRotation(
                worldPos,
                worldObject.RotationDegrees != 0f
                    ? Quaternion.Euler(0f,
                        SimulationUnityMapper.ToUnityYawDegrees(worldObject.RotationDegrees), 0f)
                    : Quaternion.identity);

            var age = snapshotTick - worldObject.SpawnTick;
            var fresh = age >= 0 && age <= FreshLimbPoseWindowTicks;
            limbRoot.AddComponent<SeveredLimbDropView>().Construct(
                owner,
                worldObject.OwnerNpcId,
                worldObject.Variant,
                worldObject.Id.Value,
                captureCurrentPose: fresh,
                referenceScale: HexRadius * NpcHeightFactor * 2.4f / ActorSourceHeightMeters,
                fallbackScale: HexRadius * NpcHeightFactor / ActorSourceHeightMeters,
                maxCaptureDistance: HexRadius * 1.5f);
            return limbRoot;
        }

        // Spec §54.2: a palm is ASSEMBLED from N trunk-segment logs + a crown, so
        // it visibly matches the logs/crown that drop when it's felled. Already
        // absolute-sized (each segment = a dropped log), so no FitObjectPrefab.
        if (HexLive.UnityPresentation.Environment.PalmTreeFactory.IsPalm(worldObject.DefinitionId))
        {
            var palm = HexLive.UnityPresentation.Environment.PalmTreeFactory.Build(worldObject.DefinitionId);
            if (palm != null)
            {
                // The imported FBX root owns its axis-conversion rotation.
                // Never write yaw onto that transform: doing so lays the whole
                // source model on its side. A presentation pivot owns natural
                // yaw, wind and TreeFall while the authored child stays intact.
                var palmRoot = new GameObject($"Object {worldObject.DefinitionId}");
                palmRoot.transform.SetParent(_objectsRoot, false);
                var palmAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                palmRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    palmAnchor, GroundY(worldObject.Tile));
                // Natural palms have no authored footprint yaw. Give every
                // object id one stable full-circle yaw, so fog unload/reload
                // and save/load never reshuffle the grove.
                palmRoot.transform.localRotation = Quaternion.Euler(
                    0f, DeterministicVegetationYaw(worldObject.Id.Value), 0f);
                palm.transform.SetParent(palmRoot.transform, false);
                RegisterVegetationWind(
                    palmRoot.transform, worldObject.Id.Value, maxTiltDegrees: 1.35f);
                // §54.2: fell it with the same tilt+stump animation as any tree.
                return palmRoot;
            }
        }

        // §54.2: the felled-palm stump — a short standing ring-cut log you can
        // perch on (a sim obstacle with a Sit interaction).
        if (worldObject.DefinitionId == "stump.palm")
        {
            var stump = HexLive.UnityPresentation.Environment.StumpFactory.Build(HexRadius);
            if (stump == null) return null;
            var stumpRoot = new GameObject("Object stump.palm");
            stumpRoot.transform.SetParent(_objectsRoot, false);
            stump.transform.SetParent(stumpRoot.transform, false);
            stump.transform.localPosition = new Vector3(
                0f, HexLive.UnityPresentation.Environment.StumpFactory.BaseLift, 0f);
            var stumpAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            stumpRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                stumpAnchor, GroundY(worldObject.Tile));
            return stumpRoot;
        }

        // Spec §54.2: the dropped palm crown is a fluffy cluster of leaves — no
        // trunk. Same builder the standing palm's top uses; the big palm's crown
        // is fuller than the small one's (matching its leaf drop).
        if (worldObject.DefinitionId == "resource.palm_crown" ||
            worldObject.DefinitionId == "resource.palm_crown_small")
        {
            var frondCount = worldObject.DefinitionId == "resource.palm_crown_small"
                ? HexLive.Simulation.Runtime.SimBalance.SmallPalmCrownLeaves
                : HexLive.Simulation.Runtime.SimBalance.BigPalmCrownLeaves;
            var crown = HexLive.UnityPresentation.Environment.PalmCrownFactory.Build(
                worldObject.DefinitionId, HexRadius * 0.85f, frondCount);
            if (crown != null)
            {
                var crownRoot = new GameObject("Object resource.palm_crown");
                crownRoot.transform.SetParent(_objectsRoot, false);
                crown.transform.SetParent(crownRoot.transform, false);
                var cAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                crownRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    cAnchor, GroundY(worldObject.Tile));
                return crownRoot;
            }
        }

        // Spec §54.2/§35.5B/§54.15: the beds, the drying rack and the water
        // collector are the assembled prefab with every piece toggled on —
        // the same prefab a build-site grows piece by piece, so finished and
        // in-progress match. All are authored 1:1, so NO ObjectFit sizing.
        if (worldObject.DefinitionId == ContentIds.BedBasic ||
            worldObject.DefinitionId == "station.drying_rack" ||
            worldObject.DefinitionId == "station.water_collector" ||
            worldObject.DefinitionId == ContentIds.Workbench)
        {
            var isHutCot = IsIntegratedHutBed(worldObject);
            var bed = isHutCot
                ? HexLive.UnityPresentation.Environment.HutFurnitureFactory.BuildBed()
                : HexLive.UnityPresentation.Environment.BedAssembly.BuildFinished(worldObject.DefinitionId);
            if (bed != null)
            {
                bed.transform.SetParent(_objectsRoot, false);
                var bedAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                bed.transform.position = SimulationUnityMapper.ToUnityPosition(
                    bedAnchor, ObjectGroundY(worldObject));
                return bed;
            }
            return null;
        }


        if (worldObject.DefinitionId == ContentIds.Hut1Hex)
        {
            var hut = HexLive.UnityPresentation.Environment.HutAssembly.BuildFinished(worldObject);
            if (hut == null) return null;
            hut.transform.SetParent(_objectsRoot, false);
            var hutAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            hut.transform.position = SimulationUnityMapper.ToUnityPosition(
                hutAnchor, GroundY(worldObject.Tile));
            return hut;
        }

        // Spec §54/29C.3: a slain mob's carcass is its own model — no carcass
        // asset exists on purpose. A mob whose animator has a Death state
        // (wolf) lies in it: a fresh kill plays the dying clip once, a carcass
        // restored from a save skips straight to the final frame. A mob with
        // no death clip at all (crab — a static FBX) is the SAME model flipped
        // belly-up. Variant holds the mob id (the sim writes dead.MobId);
        // CarcassMobId maps the legacy "rabbit" variant of old saves/servers
        // to the crab it always meant.
        if (worldObject.DefinitionId == "carcass.animal")
        {
            var carcassMobId = Config.MobLibrary.CarcassMobId(worldObject.Variant);
            var deadMobConfig = Config.MobLibrary.Get(carcassMobId);
            var deadMobPrefab = Config.MobLibrary.LoadPrefab(carcassMobId);
            if (deadMobPrefab != null)
            {
                var carcassRoot = new GameObject($"Object {worldObject.DefinitionId} ({carcassMobId})");
                carcassRoot.transform.SetParent(_objectsRoot, false);
                var deadMob = Instantiate(deadMobPrefab, carcassRoot.transform);
                deadMob.name = "Body";
                // Краб — статичный меш: масштаб меряется по ЛЮБОМУ рендереру,
                // не только по скиннингу.
                var deadSkinned = deadMob.GetComponentInChildren<SkinnedMeshRenderer>();
                Renderer deadRenderer = deadSkinned != null
                    ? deadSkinned
                    : deadMob.GetComponentInChildren<Renderer>();
                if (deadRenderer != null)
                {
                    var deadSize = deadRenderer.bounds.size;
                    var deadLength = Mathf.Max(deadSize.x, deadSize.z);
                    var footprint = deadMobConfig != null ? deadMobConfig.footprintFraction : 0.84f;
                    if (deadLength > 0.001f)
                    {
                        deadMob.transform.localScale *= HexRadius * footprint / deadLength;
                    }
                }

                if (deadSkinned != null)
                {
                    // The death pose collapses far outside the bind-pose AABB.
                    deadSkinned.updateWhenOffscreen = true;
                }

                var deadYaw = (worldObject.Id.Value * 61) % 360;
                var deadAnimator = deadMob.GetComponent<Animator>();
                var hasDeathState = deadAnimator != null &&
                    deadAnimator.runtimeAnimatorController != null &&
                    deadAnimator.HasState(0, Animator.StringToHash("Death"));
                if (hasDeathState)
                {
                    deadMob.transform.localRotation = Quaternion.Euler(0f, deadYaw, 0f);
                    var fresh = snapshotTick - worldObject.SpawnTick <= 10;
                    deadAnimator.SetBool("Dead", true);
                    deadAnimator.Play("Death", 0, fresh ? 0f : 1f);
                }
                else
                {
                    // Пузом кверху. Аниматор без контроллера глушится, чтобы
                    // не сбрасывать позу на bind.
                    if (deadAnimator != null)
                    {
                        deadAnimator.enabled = false;
                    }
                    deadMob.transform.localRotation =
                        Quaternion.Euler(0f, deadYaw, 0f) * Quaternion.Euler(180f, 0f, 0f);
                }

                var carcassAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                carcassRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    carcassAnchor, GroundY(worldObject.Tile));

                if (!hasDeathState && deadRenderer != null)
                {
                    // Флип идёт вокруг пивота (он у ног живой модели), поэтому
                    // перевёрнутое тело оказывается под землёй — прижать
                    // панцирем к грунту по фактическим границам.
                    var lift = carcassRoot.transform.position.y - deadRenderer.bounds.min.y;
                    deadMob.transform.position += Vector3.up * lift;
                }
                return carcassRoot;
            }
            return null;
        }

        // §118.5 / bug #103: loose prosthetics live in the same external
        // live registry as fitted devices. Return an async anchor now;
        // ProstheticWorldDropView fills it when the selected L/R model arrives.
        // A missing bundle stays visibly absent and logged — it must never fall
        // through to Resources or the generic diagnostic sphere.
        if (ProstheticWorldDropView.IsSupported(worldObject.DefinitionId))
        {
            var prostheticRoot = new GameObject($"Object {worldObject.DefinitionId}");
            prostheticRoot.transform.SetParent(_objectsRoot, false);
            var prostheticAnchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            prostheticRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                prostheticAnchor, GroundY(worldObject.Tile));
            prostheticRoot.AddComponent<ProstheticWorldDropView>()
                .Construct(worldObject.DefinitionId, worldObject.Id.Value);
            return prostheticRoot;
        }

        // §133: the wardrobe is authored at the exact 0.375-wu junction scale.
        // Keep its central-junction pivot and 1:1 size; generic ObjectFit would
        // incorrectly shrink furniture to the default portable-prop size.
        if (worldObject.DefinitionId == ContentIds.Wardrobe)
        {
            var wardrobe = HexLive.UnityPresentation.Environment.WardrobeAssembly.BuildFinished();
            if (wardrobe != null && ObjectFit.HasRenderableGeometry(wardrobe))
            {
                var wardrobeRoot = new GameObject("Object furniture.wardrobe");
                wardrobeRoot.transform.SetParent(_objectsRoot, false);
                wardrobe.transform.SetParent(wardrobeRoot.transform, false);
                // WardrobeAssembly follows the same identity-root contract as
                // BedAssembly. Only seat its central-junction pivot here; the
                // common footprint yaw is applied by SyncObjects.
                wardrobe.transform.localPosition = Vector3.zero;
                var wardrobePos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                wardrobeRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    wardrobePos, GroundY(worldObject.Tile));
                return wardrobeRoot;
            }
            if (wardrobe != null) Destroy(wardrobe);
            return null;
        }

        // One logical owner, one request. Garment discovery itself is async;
        // consult the live record too so a not-yet-loaded wear definition can
        // never be misrouted to object/<wear-id> and terminally cached missing.
        var isGarment = GarmentDropFactory.IsGarment(worldObject.DefinitionId) ||
            HexLive.UnityPresentation.Content.ContentAssetService.Instance.TryGetRecord(
                "wear",
                HexLive.UnityPresentation.Wearing.WearArtAliases.ArtId(
                    worldObject.DefinitionId), out _);
        var objectPrefab = isGarment
            ? null
            : HexLive.UnityPresentation.Environment.WorldPropResources.Load(
                worldObject.DefinitionId);
        if (objectPrefab != null)
        {
            var prefabRoot = new GameObject($"Object {worldObject.DefinitionId}");
            prefabRoot.transform.SetParent(_objectsRoot, false);
            var instance = Instantiate(objectPrefab, prefabRoot.transform);
            // Each native source carries its own authored material contract.
            // Never replace rock slots with a material from a different backup
            // mesh — that produced the rainbow-rock failure from bug #55.
            // A non-null empty root is not a visual. Reject it explicitly; the
            // exact logical owner must be repaired instead of being replaced by
            // another mesh or a procedural primitive.
            if (ObjectFit.HasRenderableGeometry(instance))
            {
                var parkedBottle = worldObject.DefinitionId == "tool.bottle" &&
                    worldObject.Junctions.Count > 0 &&
                    _collectorJunctions.Contains(worldObject.Junctions[0]);
                FitObjectPrefab(instance, worldObject.DefinitionId, worldObject.Id.Value,
                    scatter: !parkedBottle && worldObject.RotationDegrees == 0f);
                if (parkedBottle)
                {
                    // Не сбрасывать поворот в identity: префаб бутылки несёт
                    // Z-up→Y-up обёртку FBX-импортёра (X=-90), без неё бутылка
                    // ложится на бок (баги #106/#280). FitObjectPrefab со
                    // scatter:false поворот не трогает — авторская вертикальная
                    // поза уже стоит.
                    instance.transform.localPosition += Vector3.up * 0.12f;
                    prefabRoot.name = $"Object {worldObject.DefinitionId} (parked)";
                }
                var anchorPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                prefabRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    anchorPos, GroundY(worldObject.Tile));
                MaybeAttachCampfire(prefabRoot, worldObject.DefinitionId);
                return prefabRoot;
            }

            Destroy(prefabRoot);
            Debug.LogError($"[AtomicContent] object/{worldObject.DefinitionId} main не содержит renderable geometry.");
            return null;
        }

        // §35.5B: a garment at a drying-rack junction HANGS on the rack instead
        // of lying in the grass — upright on one of the 8 invisible hanger
        // slots along the rails. The root stays on the shared junction anchor
        // (interpolation repositions roots), the slot offset lives on the child.
        if (worldObject.Junctions.Count > 0 && _rackJunctions.Contains(worldObject.Junctions[0]) &&
            GarmentDropFactory.IsGarment(worldObject.DefinitionId))
        {
            var inWardrobe = _wardrobeJunctions.Contains(worldObject.Junctions[0]);
            var footwearOnWardrobeShelf = inWardrobe &&
                GarmentDropFactory.IsFootwear(worldObject.DefinitionId);
            var hung = footwearOnWardrobeShelf
                ? GarmentDropFactory.Build(worldObject.DefinitionId)
                : GarmentDropFactory.BuildHanging(worldObject.DefinitionId);
            if (hung != null)
            {
                var hungRoot = new GameObject($"Object {worldObject.DefinitionId} (hung)");
                hungRoot.transform.SetParent(_objectsRoot, false);
                hung.transform.SetParent(hungRoot.transform, false);
                var hungScale = HexRadius * NpcHeightFactor * 2.4f / ActorSourceHeightMeters;
                hung.transform.localScale = Vector3.one * hungScale;
                _rackHangRank.TryGetValue(worldObject.Id.Value, out var slot);
                var wardrobeSocket = HexLive.UnityPresentation.Environment.WardrobeHangers.Slot(slot);
                if (footwearOnWardrobeShelf)
                {
                    wardrobeSocket = HexLive.UnityPresentation.Environment.WardrobeHangers
                        .GroundFootwearOnShelf(hung.transform, slot);
                }
                else if (inWardrobe)
                {
                    HexLive.UnityPresentation.Environment.WardrobeAssembly.TryGetClothingSlotLocal(
                        slot, out wardrobeSocket);
                }
                hung.transform.localPosition = footwearOnWardrobeShelf
                    ? wardrobeSocket
                    : HangSlotOffset(hung.transform, slot, inWardrobe, wardrobeSocket);
                // One slot owns one orientation. Garment categories (including
                // underwear) must not add another 90° or random yaw here.
                hung.transform.localRotation = Quaternion.identity;
                var hungPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
                hungRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                    hungPos, GroundY(worldObject.Tile));
                if (inWardrobe && _wardrobeYawByJunction.TryGetValue(
                        worldObject.Junctions[0], out var wardrobeYaw))
                    hungRoot.transform.rotation = Quaternion.Euler(0f, wardrobeYaw, 0f);
                if (inWardrobe && !footwearOnWardrobeShelf)
                {
                    var hanger = HexLive.UnityPresentation.Environment.WardrobeHangerFactory.Build();
                    if (hanger != null)
                    {
                        hanger.transform.SetParent(hungRoot.transform, false);
                        hanger.transform.localPosition = wardrobeSocket;
                        hanger.transform.localRotation = Quaternion.identity;
                    }
                }
                AttachGarmentCondition(hungRoot, hung, worldObject);
                return hungRoot;
            }
        }

        // Spec 40.19: dropped clothing shows the real garment lying flat on
        // the ground (footwear stands as-is). The factory's pivot is the
        // garment's own centre, so the drop sits exactly on its anchor.
        var garment = HexLive.UnityPresentation.Wearing.GarmentDropFactory.Build(worldObject.DefinitionId);
        if (garment != null)
        {
            var garmentRoot = new GameObject($"Object {worldObject.DefinitionId}");
            garmentRoot.transform.SetParent(_objectsRoot, false);
            garment.transform.SetParent(garmentRoot.transform, false);
            // Same world scale the girls wear it at, so the drop reads
            // proportional to its former owner; deterministic scatter yaw.
            var garmentScale = HexRadius * NpcHeightFactor * 2.4f / ActorSourceHeightMeters;
            garment.transform.localScale = Vector3.one * garmentScale;
            garment.transform.localRotation = Quaternion.Euler(0f, (worldObject.Id.Value * 73) % 360, 0f);
            GroundVisual(garment, lift: 0.01f); // epsilon: thin cloth vs tile z-fight
            SuppressSmallPropShadows(garment); // flat cloth on the ground — see above
            var garmentPos = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
            garmentRoot.transform.position = SimulationUnityMapper.ToUnityPosition(
                garmentPos, GroundY(worldObject.Tile));
            AttachGarmentCondition(garmentRoot, garment, worldObject);
            return garmentRoot;
        }
        if (GarmentDropFactory.IsGarment(worldObject.DefinitionId))
        {
            return null;
        }
        // A record-backed object that is not ready stays absent for this frame.
        // Caching a sphere/cube here made the first asynchronous miss permanent.
        return null;
    }

    private GameObject CreateHumanRemainsView(
        ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions)
    {
        var root = new GameObject($"Object {worldObject.DefinitionId}");
        root.transform.SetParent(_objectsRoot, false);

        var variant = worldObject.Variant == "1" ? "b" : "a";
        var sprite = HexLive.UnityPresentation.Content.AtomicResources.Load<Sprite>($"HexLive/Remains/human_remains_{variant}");
        if (sprite != null)
        {
            var visual = new GameObject("SkeletonAndLootBag");
            visual.transform.SetParent(root.transform, false);
            var spriteRenderer = visual.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
            spriteRenderer.sortingOrder = 2;
            spriteRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            spriteRenderer.receiveShadows = false;

            var authoredBodyLength = HexRadius * NpcHeightFactor * 2.4f;
            var sourceLength = Mathf.Max(0.001f, sprite.bounds.size.y);
            visual.transform.localScale = Vector3.one * (authoredBodyLength / sourceLength);
            // Sprite local +Y points to the head. A lying actor's +forward points
            // head->feet (LyingSpot), so turn the image around before laying XY on XZ.
            visual.transform.localRotation = Quaternion.Euler(90f, 0f, 180f);
            visual.transform.localPosition = Vector3.up * 0.025f;
        }
        else
        {
            // AtomicResources is asynchronous. Leave this frame empty and let
            // SyncObjects retry; a first null must not become a permanent
            // capsule in the saved world's presentation.
            Destroy(root);
            return null;
        }

        var anchor = GetObjectAnchorFromJunctions(worldObject, junctionPositions);
        root.transform.position = SimulationUnityMapper.ToUnityPosition(
            anchor, GroundY(worldObject.Tile));
        return root;
    }

    private static void AttachGarmentCondition(GameObject root, GameObject visual,
        ObjectSnapshot snapshot)
    {
        var condition = root.AddComponent<GarmentWorldCondition>();
        condition.Construct(visual);
        condition.Sync(snapshot.Durability, snapshot.Dirtiness, snapshot.Bloodiness, snapshot.Wetness);
    }

    // §35.5B: where a hung garment's CENTRE goes so its top edge drapes just
    // over the hanger slot's rail — dropped by the garment's own half-height
    // (bounds are translation-invariant, so this works before placement too),
    // clamped so a long garment on the low rail doesn't clip the ground.
    private static Vector3 HangSlotOffset(Transform hung, int slot, bool inWardrobe,
        Vector3 wardrobeSocket)
    {
        // §133: authored wardrobe hanger centres.
        if (inWardrobe)
        {
            var wardrobeHalf = 0.15f;
            var wardrobeRenderers = hung.GetComponentsInChildren<Renderer>();
            if (wardrobeRenderers.Length > 0)
            {
                var wardrobeBounds = wardrobeRenderers[0].bounds;
                for (var i = 1; i < wardrobeRenderers.Length; i++)
                    wardrobeBounds.Encapsulate(wardrobeRenderers[i].bounds);
                wardrobeHalf = Mathf.Max(0.02f, wardrobeBounds.extents.y);
            }
            return new Vector3(wardrobeSocket.x,
                Mathf.Max(0.18f + wardrobeHalf,
                    wardrobeSocket.y - wardrobeHalf - WardrobeGarmentShoulderDrop),
                wardrobeSocket.z);
        }

        var attach = HexLive.UnityPresentation.Environment.DryingRackHangers.Slot(slot);
        var half = 0.15f;
        var renderers = hung.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            half = Mathf.Max(0.02f, bounds.extents.y);
        }

        var y = attach.y + HexLive.UnityPresentation.Environment.DryingRackHangers.DrapeOverlap - half;
        y = Mathf.Max(y, HexLive.UnityPresentation.Environment.DryingRackHangers.GroundClearance + half);
        return new Vector3(attach.x, y, attach.z);
    }

    // Spec 20.16: the campfire actually burns — flame particles + a warm,
    // flickering point light that lights nearby terrain and actors.
    // §54.14: the stone ring is no longer code-built — it is stage 2 of the
    // campfire_final staged prefab. This fallback only fires when that prefab
    // is missing and the legacy fbx path renders the fire.
    private void MaybeAttachCampfire(GameObject root, string definitionId)
    {
        if (definitionId != "campfire.spot")
        {
            return;
        }

        var effect = root.AddComponent<HexLive.UnityPresentation.Environment.CampfireEffect>();
        effect.Construct(HexRadius);
    }

    // FOG-OF-WAR EXPERIMENT: once per rendered tick, gather what the colony
    // knows. Object knowledge = union of every living colonist's persistent
    // object memory (perception upserts a sighted object into memory the same
    // tick, so memory covers "sees right now" too). Colonist tiles feed the
    // mob spot-radius check. Reading Engine.World here is local-mode only.
    private void RebuildFogState(WorldSnapshot snapshot)
    {
        var engine = UI.DebugControlsPanel.FogOfWar ? _runner?.Engine : null;
        _fogActive = engine != null;
        _fogKnownObjects.Clear();
        _fogEyes.Clear();
        _fogHiddenNpcs.Clear();
        _fogHidesNpcs = false;
        if (engine == null)
        {
            return;
        }

        // "Selected only": the fog narrows to what the selected NPC herself
        // knows/sees. With nothing selected it falls back to the colony union,
        // matching the debug panel's "target: everyone" convention.
        var selectedOnly = UI.DebugControlsPanel.FogOfWarSelectedOnly &&
            Input.NpcSelection.HasSelection;
        var requestedId = selectedOnly ? Input.NpcSelection.SelectedId : -1;
        var selectedId = selectedOnly
            ? ResolveFogObserverId(snapshot, requestedId, _fogObserverNpcId)
            : -1;
        _fogObserverNpcId = selectedId;

        foreach (var npc in engine.World.Entities.Npcs.Values)
        {
            var include = selectedOnly
                ? npc.Id.Value == selectedId
                : npc.Faction == HexLive.Simulation.Agents.Faction.Colony;
            if (!include)
            {
                continue;
            }

            foreach (var knownId in npc.Memory.KnownObjects.Keys)
            {
                _fogKnownObjects.Add(knownId.Value);
            }
        }

        // Глаза берём из снапшота, чтобы проверка совпадала с тем, что
        // нарисовано в этом кадре, а радиус — тот же, что считает симуляция
        // (§125.5: вид не выводит формулу повторно).
        foreach (var npc in snapshot.Npcs)
        {
            var include = selectedOnly
                ? npc.Id.Value == selectedId
                : !npc.IsHostileToColony;
            if (include)
            {
                _fogEyes.Add((npc.Tile, npc.PerceptionRadiusTiles));
            }
        }

        // §125.5: людей прячем только от лица ВЫБРАННОЙ — «чего не видит вся
        // колония сразу» смысла не имеет, там всегда видно всех.
        _fogHidesNpcs = selectedOnly;
        if (!_fogHidesNpcs)
        {
            return;
        }

        foreach (var npc in snapshot.Npcs)
        {
            // #146 rework: own colonists are player-owned bodies, not fog
            // contacts. Hiding housemates made a talk target disappear while
            // her selected partner spoke to empty space; clicking the missing
            // roster entry then exempted that id and looked like a spawn.
            if (!IsPlayerOwnedNpc(npc) &&
                npc.Id.Value != selectedId && !FogSeesTile(npc.Tile))
            {
                _fogHiddenNpcs.Add(npc.Id.Value);
            }
        }
    }

    // A relationship chip is allowed to SELECT a stranger for her card, but
    // that does not grant the stranger camera/vision authority. Keep the last
    // valid colony observer; on first use choose the lowest-id living colonist
    // deterministically. With no living colonist return -1: selected-only then
    // has no eyes and cannot reveal the selected outsider by accident.
    // §149: приоритет у той, КЕМ ИГРОК УПРАВЛЯЕТ. Иначе на сервере глазами
    // мира становилась колонистка чужого лагеря с наименьшим id, а собственная
    // выданная девушка оставалась в тумане. Локально и у анонимного зрителя
    // поведение прежнее: там управляемые и есть колония, либо владения нет
    // вовсе и работает запасной путь по Faction.Colony.
    private int ResolveFogObserverId(
        WorldSnapshot snapshot, int requestedId, int previousId)
    {
        var firstOwnedId = int.MaxValue;
        var firstColonyId = int.MaxValue;
        var previousIsValid = false;
        foreach (var npc in snapshot.Npcs)
        {
            if (npc.Health <= 0f)
            {
                continue;
            }

            var controllable = _runner != null && _runner.CanControlNpc(npc.Id);
            var colony = npc.Faction == HexLive.Simulation.Agents.Faction.Colony;
            if (!controllable && !colony)
            {
                continue;
            }

            if (npc.Id.Value == requestedId)
            {
                return requestedId;
            }

            previousIsValid |= npc.Id.Value == previousId;
            if (controllable)
            {
                firstOwnedId = System.Math.Min(firstOwnedId, npc.Id.Value);
            }

            if (colony)
            {
                firstColonyId = System.Math.Min(firstColonyId, npc.Id.Value);
            }
        }

        if (previousIsValid)
        {
            return previousId;
        }

        if (firstOwnedId != int.MaxValue)
        {
            return firstOwnedId;
        }

        return firstColonyId == int.MaxValue ? -1 : firstColonyId;
    }

    /// <summary>§125.5: кольцо гексов на самой границе восприятия выбранной.
    /// Перестраивается только когда она сменилась, сдвинулась или её радиус
    /// изменился — иначе это была бы перекраска сотни рендереров каждый кадр.</summary>
    private void SyncFogRing(WorldSnapshot snapshot)
    {
        var show = _fogActive && _fogHidesNpcs && _fogObserverNpcId >= 0;
        if (!show)
        {
            ClearFogRing();
            return;
        }

        var selectedId = _fogObserverNpcId;
        var centre = TileCoord.Zero;
        var radius = -1;
        foreach (var npc in snapshot.Npcs)
        {
            if (npc.Id.Value == selectedId)
            {
                centre = npc.Tile;
                radius = npc.PerceptionRadiusTiles;
                break;
            }
        }

        if (radius <= 0)
        {
            ClearFogRing(); // слепая — рисовать нечего
            return;
        }

        if (radius == _fogRingRadius && centre.Equals(_fogRingCenter) && _fogRingSaved.Count > 0)
        {
            return;
        }

        ClearFogRing();
        _fogRingCenter = centre;
        _fogRingRadius = radius;

        _fogRingTiles.Clear();
        for (var dq = -radius; dq <= radius; dq++)
        {
            var lo = Mathf.Max(-radius, -dq - radius);
            var hi = Mathf.Min(radius, -dq + radius);
            for (var dr = lo; dr <= hi; dr++)
            {
                // Только ОБОД кольца: заливать всю зону значило бы перекрасить
                // пол-острова и потерять сам рисунок границы.
                if (Mathf.Max(Mathf.Abs(dq), Mathf.Max(Mathf.Abs(dr), Mathf.Abs(dq + dr))) != radius)
                {
                    continue;
                }

                _fogRingTiles.Add(new TileCoord(centre.Q + dq, centre.R + dr));
            }
        }

        foreach (var coord in _fogRingTiles)
        {
            if (!_tileViews.TryGetValue(coord, out var view) || view == null)
            {
                continue;
            }

            foreach (var renderer in TileRenderers(view))
            {
                if (renderer == null || _fogRingSaved.ContainsKey(renderer))
                {
                    continue;
                }

                var saved = renderer.sharedMaterials;
                _fogRingSaved[renderer] = saved;

                var ringed = new Material[saved.Length];
                for (var i = 0; i < ringed.Length; i++)
                {
                    ringed[i] = ShadeCounterpart(saved[i], Color.Lerp(
                        DryBaseColor(saved[i]), new Color(0.35f, 0.75f, 1f), 0.55f));
                }

                renderer.sharedMaterials = ringed;
            }
        }
    }

    private void ClearFogRing()
    {
        foreach (var pair in _fogRingSaved)
        {
            if (pair.Key != null)
            {
                pair.Key.sharedMaterials = pair.Value;
            }
        }

        _fogRingSaved.Clear();
        _fogRingRadius = -1;
    }

    /// <summary>B0: набор рендереров тайла (гекс, обрыв, трава, riverbed)
    /// окончателен после <see cref="CreateTileView"/> — кэшируем, чтобы
    /// перекраски не гоняли GetComponentsInChildren по тысячам тайлов.</summary>
    private Renderer[] TileRenderers(GameObject view)
    {
        if (!_tileRendererCache.TryGetValue(view, out var renderers))
        {
            renderers = view.GetComponentsInChildren<Renderer>(true);
            _tileRendererCache[view] = renderers;
        }

        return renderers;
    }

    /// <summary>Perception culling: rebuilds the visible-tile union and the
    /// frozen complement. Runs once per tick, before the object sync loop, so
    /// <see cref="CullHidesTile"/> answers for this tick's snapshot. Tiles are
    /// never deactivated — the frozen ones simply stop being updated (and a
    /// future pass will restyle them from <see cref="CullFrozenTiles"/>).
    /// Dead colonists contribute no eyes — a wiped camp freezes whole.</summary>
    private void RebuildPerceptionCulling(WorldSnapshot snapshot)
    {
        var active = UI.DebugControlsPanel.PerceptionCulling;
        if (!active)
        {
            if (_cullActive)
            {
                // Toggled off mid-play: nothing to reactivate (tiles never went
                // dark). The object loop resyncs ghosts and creates the views
                // it skipped this same tick; movers recover in their loops too.
                _cullActive = false;
                _cullEyes.Clear();
                _cullVisibleTiles.Clear();
                _cullFrozenTiles.Clear();
            }

            return;
        }

        _cullActive = true;
        _cullEyes.Clear();
        foreach (var npc in snapshot.Npcs)
        {
            // Eyes are OUR girls only (player request): the world exists for
            // the player exactly as far as her own camp perceives it. The
            // §146 rival camps (Colony2/Colony3) reveal nothing and are
            // themselves hidden below until one of ours actually sees them.
            if (IsPlayerOwned(npc) && npc.Health > 0f)
            {
                _cullEyes.Add((npc.Tile, npc.PerceptionRadiusTiles));
            }
        }

        // §148: РАЗВЕДАННОЕ ПРИХОДИТ ИЗ СИМУЛЯЦИИ — она его копит и кладёт в
        // сейв, поэтому после перезапуска карта остаётся открытой. Своего
        // мнения о разведке у вида нет: иначе оно бы разошлось с сейвом (и с
        // сервером, где вид вообще не видит мира).
        foreach (var tile in snapshot.Tiles)
        {
            if (tile.Explored)
            {
                _everSeenTiles.Add(tile.Coord);
            }
        }

        // §148: видимое множество собирается ОТ ГЛАЗ кольцами, а не обходом
        // всех тайлов острова: дисков единицы, а тайлов тысячи. Заодно это
        // единственный способ узнать про гекс, вью которого ещё не создан —
        // на никогда не виденной земле его и не должно быть.
        _cullVisibleTiles.Clear();
        foreach (var (eyeTile, radius) in _cullEyes)
        {
            for (var dq = -radius; dq <= radius; dq++)
            {
                var lo = Mathf.Max(-radius, -dq - radius);
                var hi = Mathf.Min(radius, -dq + radius);
                for (var dr = lo; dr <= hi; dr++)
                {
                    var coord = new TileCoord(eyeTile.Q + dq, eyeTile.R + dr);
                    if (_cullVisibleTiles.Add(coord))
                    {
                        _everSeenTiles.Add(coord);
                    }
                }
            }
        }

        // Замороженное = «видели когда-то, но не видим сейчас». Никогда не
        // виденные гексы не попадают ни туда, ни туда: их просто нет.
        _cullFrozenTiles.Clear();
        foreach (var coord in _everSeenTiles)
        {
            if (!_cullVisibleTiles.Contains(coord))
            {
                _cullFrozenTiles.Add(coord);
            }
        }
    }

    /// <summary>§150: можно ли ЩЁЛКНУТЬ по этому человеку. Назначенные этому
    /// игроку — всегда; любой другой — только пока он в восприятии его глаз. По «?» на последнем
    /// известном месте клика нет: игрок не знает, там ли он ещё.</summary>
    public bool IsNpcPickable(int npcId, TileCoord tile, bool isPlayerOwned) =>
        isPlayerOwned || (PlayerVisibilityReady &&
            (!_cullActive || _cullVisibleTiles.Contains(tile)));

    /// <summary>§150: renderer completed at least one perception pass. Until
    /// then every non-owned contact and every resource tile stays closed: UI
    /// execution order must never turn the authoritative first snapshot into
    /// one frame of omniscience (or permanent map memory).</summary>
    public bool PlayerVisibilityReady => _lastRenderedTick >= 0;

    /// <summary>§150: тот же live-предикат гекса для мира, ростера и карты.</summary>
    public bool IsTileVisibleToPlayer(TileCoord tile) =>
        PlayerVisibilityReady && (!_cullActive || _cullVisibleTiles.Contains(tile));

    // §149 r2 (#232): «свой» — мой персонаж и весь его лагерь, иначе на
    // сервере соседки выданной девушки жили как fog-контакты: пропадали из
    // виду и оставляли «?», а глазами тумана была она одна.
    private bool IsPlayerOwned(NpcSnapshot npc) =>
        _runner != null
            ? PlayerCampView.IsMine(_runner, _lastSnapshot, npc)
            : npc.Faction == HexLive.Simulation.Agents.Faction.Colony;

    /// <summary>§150: suppresses decorative grass without replacing or hiding
    /// any interactive world geometry.</summary>
    public void SetOverviewGrassHidden(bool hidden)
    {
        if (_overviewGrassHidden == hidden)
        {
            return;
        }

        _overviewGrassHidden = hidden;
        RefreshOverviewSuppression();
    }

    /// <summary>§148/§150: last honest tile of a currently lost outsider.</summary>
    public bool TryGetLastSeenNpcTile(int npcId, out TileCoord tile) =>
        _lastSeenNpcTiles.TryGetValue(npcId, out tile);

    /// <summary>§150.4: читает отсечка кликов SmallProps — вид с живым
    /// импостором остаётся кликабельным на любом отдалении.</summary>
    public bool OverviewImpostorsActive => _overviewImpostorsActive;

    /// <summary>§150.4: включить/выключить импосторы дальнего обзора. На
    /// переднем фронте каждый импостор при нужде печёт текстуру из ещё
    /// видимых мешей своего definition, затем меши гасятся forceRenderingOff.</summary>
    public void SetOverviewImpostorsActive(bool active)
    {
        if (_overviewImpostorsActive == active)
        {
            return;
        }

        _overviewImpostorsActive = active;
        var day = CurrentGameDay();
        if (active)
        {
            RefreshNpcImpostorPortraits();
        }

        for (var i = _impostors.Count - 1; i >= 0; i--)
        {
            var impostor = _impostors[i];
            if (impostor == null)
            {
                _impostors.RemoveAt(i);
                continue;
            }

            impostor.SetDistant(active, day);
        }
    }

    private int CurrentGameDay() =>
        _lastRenderedTick <= 0
            ? 0
            : _lastRenderedTick /
              Mathf.Max(1, HexLive.Simulation.Runtime.WorldBalance.DayLengthTicks);

    /// <summary>§150.4: диск человека несёт портрет дня из NpcPortraitCache;
    /// пока снимка нет — цвет фракции (и заказ снимка вне очереди).</summary>
    private void RefreshNpcImpostorPortraits()
    {
        if (_portraitCache == null)
        {
            return;
        }

        foreach (var pair in _npcImpostors)
        {
            if (pair.Value == null)
            {
                continue;
            }

            if (_portraitCache.TryGet(pair.Key, out var portrait))
            {
                pair.Value.SetNpcPortrait(portrait, NpcImpostorTint(pair.Key));
            }
            else
            {
                _portraitCache.RequestNow(pair.Key);
                pair.Value.SetNpcPortrait(null, NpcImpostorTint(pair.Key));
            }
        }
    }

    private Color NpcImpostorTint(int npcId)
    {
        if (_lastSnapshot != null)
        {
            foreach (var npc in _lastSnapshot.Npcs)
            {
                if (npc.Id.Value == npcId)
                {
                    return IsPlayerOwned(npc)
                        ? new Color(0.42f, 0.86f, 0.55f)
                        : new Color(0.92f, 0.62f, 0.35f);
                }
            }
        }

        return new Color(0.75f, 0.78f, 0.8f);
    }

    /// <summary>§150.4: все квады смотрят в камеру одним общим кватернионом —
    /// один цикл за кадр вместо тысяч per-object Update.</summary>
    private void LateUpdate()
    {
        if (!_overviewImpostorsActive)
        {
            _impostorFacingValid = false;
            return;
        }

        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }

        var rotation = camera.transform.rotation;
        if (_impostorFacingValid &&
            Quaternion.Angle(rotation, _impostorFacing) < 0.05f)
        {
            return;
        }

        _impostorFacing = rotation;
        _impostorFacingValid = true;
        for (var i = _impostors.Count - 1; i >= 0; i--)
        {
            var impostor = _impostors[i];
            if (impostor == null)
            {
                _impostors.RemoveAt(i);
                continue;
            }

            impostor.FaceCamera(rotation);
        }
    }

    /// <summary>§150.4: кто получает импостор. Флора и лежащие переносимые
    /// вещи; постройки, станции, мебель, костры, трупы и звери живут мешами.
    /// Предикаты подняты из до-bug-230 версии (df75043cd^).</summary>
    private void TryAttachOverviewImpostor(ObjectSnapshot worldObject, GameObject view)
    {
        if (view == null)
        {
            return;
        }

        var flora = IsOverviewFlora(worldObject.DefinitionId);
        if (!flora && !IsOverviewLooseItem(worldObject))
        {
            return;
        }

        var impostor = Views.ObjectImpostor.Attach(
            view,
            worldObject.DefinitionId,
            flora ? 2.6f : 0.8f,
            flora ? new Color(0.32f, 0.52f, 0.3f) : new Color(0.62f, 0.58f, 0.5f));
        _impostors.Add(impostor);
        if (_overviewImpostorsActive)
        {
            impostor.SetDistant(true, CurrentGameDay());
        }
    }

    private bool IsOverviewLooseItem(ObjectSnapshot worldObject)
    {
        if (worldObject.Junctions.Count > 0 &&
            _rackJunctions.Contains(worldObject.Junctions[0]) &&
            GarmentDropFactory.IsGarment(worldObject.DefinitionId))
        {
            return false;
        }

        if (_runner != null &&
            _runner.TryGetObjectDefinition(worldObject.DefinitionId, out var definition) &&
            definition != null)
        {
            if (definition.Layer.HasValue || ItemCatalog.Classify(definition) != ItemCategory.Misc)
            {
                return true;
            }

            for (var i = 0; i < definition.Interactions.Count; i++)
            {
                if (definition.Interactions[i].Type == InteractionType.PickUp)
                {
                    return true;
                }
            }
        }

        var id = worldObject.DefinitionId;
        return id.StartsWith("food.", StringComparison.Ordinal) ||
            id.StartsWith("tool.", StringComparison.Ordinal) ||
            id.StartsWith("item.", StringComparison.Ordinal) ||
            id.StartsWith("resource.", StringComparison.Ordinal) ||
            id.StartsWith("clothing.", StringComparison.Ordinal) ||
            id.StartsWith("underwear.", StringComparison.Ordinal) ||
            id.StartsWith("armor.", StringComparison.Ordinal);
    }

    private static bool IsOverviewFlora(string definitionId) =>
        definitionId.StartsWith("tree.", StringComparison.Ordinal) ||
        definitionId.StartsWith("plant.", StringComparison.Ordinal);

    private void RefreshOverviewSuppression()
    {
        _overviewGrassRenderers.RemoveWhere(renderer => renderer == null);
        _overviewRendererScratch.Clear();
        foreach (var pair in _overviewSavedForceRenderingOff)
        {
            if (pair.Key == null)
            {
                _overviewRendererScratch.Add(pair.Key);
            }
        }
        for (var i = 0; i < _overviewRendererScratch.Count; i++)
        {
            _overviewSavedForceRenderingOff.Remove(_overviewRendererScratch[i]);
        }
        _overviewRendererScratch.Clear();

        foreach (var renderer in _overviewGrassRenderers)
        {
            ApplyOverviewRenderer(renderer);
        }
    }

    private void ApplyOverviewRenderer(Renderer renderer)
    {
        if (renderer == null)
        {
            return;
        }

        var shouldHide = _overviewGrassHidden && _overviewGrassRenderers.Contains(renderer);
        if (shouldHide)
        {
            if (!_overviewSavedForceRenderingOff.ContainsKey(renderer))
            {
                _overviewSavedForceRenderingOff[renderer] = renderer.forceRenderingOff;
            }

            renderer.forceRenderingOff = true;
        }
        else if (_overviewSavedForceRenderingOff.TryGetValue(renderer, out var previous))
        {
            renderer.forceRenderingOff = previous;
            _overviewSavedForceRenderingOff.Remove(renderer);
        }
    }

    private void RegisterOverviewRenderers(GameObject root, HashSet<Renderer> group)
    {
        if (root == null)
        {
            return;
        }

        root.GetComponentsInChildren(true, _overviewRendererScratch);
        for (var i = 0; i < _overviewRendererScratch.Count; i++)
        {
            var renderer = _overviewRendererScratch[i];
            if (renderer != null)
            {
                group.Add(renderer);
                ApplyOverviewRenderer(renderer);
            }
        }
        _overviewRendererScratch.Clear();
    }

    private const float UnknownMarkerLift = 1.15f;

    private readonly HashSet<int> _unknownMarkerLive = new();

    /// <summary>§148: «?» на последнем месте, где наши видели чужого. Тело в
    /// это время не нарисовано (см. цикл NPC): память о человеке — это метка
    /// «здесь кто-то был», а не застывшая фигура, которая врала бы о том, что
    /// он всё ещё стоит там.</summary>
    private void SyncUnknownNpcMarkers(WorldSnapshot snapshot)
    {
        _unknownMarkerLive.Clear();
        if (_cullActive)
        {
            foreach (var npc in snapshot.Npcs)
            {
                var key = npc.Id.Value;
                if (IsPlayerOwned(npc) ||
                    _cullVisibleTiles.Contains(npc.Tile) ||
                    !_lastSeenNpcTiles.TryGetValue(key, out var lastSeen))
                {
                    continue; // свои, видимые сейчас и никогда не встреченные — не метки
                }

                _unknownMarkerLive.Add(key);
                if (!_unknownNpcMarkers.TryGetValue(key, out var marker) || marker == null)
                {
                    marker = CreateUnknownNpcMarker();
                    _unknownNpcMarkers[key] = marker;
                }

                if (!marker.activeSelf)
                {
                    marker.SetActive(true);
                }

                marker.transform.position = SimulationUnityMapper.ToUnityTilePosition(
                    lastSeen, GroundY(lastSeen)) + Vector3.up * UnknownMarkerLift;
            }
        }

        foreach (var pair in _unknownNpcMarkers)
        {
            if (pair.Value != null && pair.Value.activeSelf &&
                !_unknownMarkerLive.Contains(pair.Key))
            {
                pair.Value.SetActive(false);
            }
        }
    }

    private GameObject CreateUnknownNpcMarker()
    {
        var root = new GameObject("Unknown NPC ?");
        root.transform.SetParent(transform, false);
        // Обычный TextMesh: «?» — ASCII, и беды эмодзи (те рисуются белым
        // квадратом и требуют спрайтов, §28.15E) у него нет.
        var text = root.AddComponent<TextMesh>();
        text.text = "?";
        text.characterSize = 0.3f;
        text.fontSize = 96;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(0.96f, 0.88f, 0.45f);
        if (root.TryGetComponent<MeshRenderer>(out var renderer))
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        root.AddComponent<Views.CameraBillboard>();
        return root;
    }

    /// <summary>§148: видел ли наш лагерь этот гекс хоть раз. Пока нет — там
    /// не рисуется ничего: ни земля, ни деревья, ни лежащие вещи.</summary>
    private bool TileDiscovered(TileCoord tile) =>
        !_cullActive || _everSeenTiles.Contains(tile);

    /// <summary>§148: видим ли мы гекс ПРЯМО СЕЙЧАС (кто-то из наших держит его
    /// в восприятии). Живые тела и звери показываются только по этому
    /// правилу — замороженной картинкой может быть только неподвижный мир.</summary>
    private bool TileVisibleNow(TileCoord tile) =>
        !_cullActive || _cullVisibleTiles.Contains(tile);

    // §148: варианты затенения памяти — игрок выбирает на глаз (кнопка в
    // дебаг-панели). Множитель цвета и подмешиваемый оттенок.
    internal enum MemoryShadeStyle
    {
        Off,        // память светится как живое — видно только по движению
        Dim,        // просто темнее
        Grayscale,  // обесцвеченная память
        Blueprint,  // холодный синий «чертёж»
        Sepia,      // выцветшая карта
    }

    internal static MemoryShadeStyle ShadeStyle = MemoryShadeStyle.Grayscale;

    /// <summary>§148: перекрасить гексы памяти. Красим ТОЛЬКО те, чьё
    /// состояние сменилось: тайлов тысячи. B0: вместо MaterialPropertyBlock
    /// (он выбивает рендерер из SRP Batcher) подменяем sharedMaterials на
    /// общий тонированный материал из тех же квантованных кэшей, что красят
    /// сам террейн; исходный массив хранится и возвращается при возврате
    /// гекса в живое восприятие.</summary>
    private void ApplyMemoryShade()
    {
        var styleKey = (int)ShadeStyle;
        foreach (var pair in _tileViews)
        {
            var view = pair.Value;
            if (view == null)
            {
                continue;
            }

            var wanted = _cullActive && ShadeStyle != MemoryShadeStyle.Off &&
                _cullFrozenTiles.Contains(pair.Key)
                    ? styleKey
                    : 0;
            if (_tileShadeApplied.TryGetValue(pair.Key, out var applied) && applied == wanted)
            {
                continue;
            }

            _tileShadeApplied[pair.Key] = wanted;
            foreach (var renderer in TileRenderers(view))
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!_tileShadeSaved.TryGetValue(renderer, out var saved))
                {
                    saved = renderer.sharedMaterials;
                    _tileShadeSaved[renderer] = saved;
                }

                if (wanted == 0)
                {
                    renderer.sharedMaterials = saved;
                    continue;
                }

                var tinted = new Material[saved.Length];
                for (var i = 0; i < tinted.Length; i++)
                {
                    tinted[i] = ShadeCounterpart(
                        saved[i], MemoryTint(DryBaseColor(saved[i])));
                }

                renderer.sharedMaterials = tinted;
            }
        }
    }

    /// <summary>B0: общий тонированный собрат материала тайла. Идёт через те
    /// же кэши, что и сам террейн, поэтому дождевая мокрость и SRP-батчинг
    /// действуют и на перекрашенные гексы. Riverbed (queue 2999) сохраняет
    /// свою очередь — обычный opaque в _CameraDepthTexture превратил бы реку
    /// в «мелкую пену» (см. комментарий у RiverbedMaterial).</summary>
    private static Material ShadeCounterpart(Material source, Color tint) =>
        source != null && source.renderQueue == 2999
            ? RiverbedMaterial(tint)
            : GetFlatMaterial(tint);

    /// <summary>B0: СУХОЙ базовый цвет материала. Читать текущий color нельзя:
    /// дождь темнит общие материалы, и тон, снятый в дождь, остался бы тёмным
    /// навсегда в квантованном кэше.</summary>
    private static Color DryBaseColor(Material material)
    {
        if (material == null)
        {
            return Color.white;
        }

        if (_materialDryColors.TryGetValue(material, out var dry))
        {
            return dry;
        }

        if (material.HasProperty(FogRingBaseColor))
        {
            return material.GetColor(FogRingBaseColor);
        }

        return material.HasProperty(FogRingLegacyColor)
            ? material.GetColor(FogRingLegacyColor)
            : Color.white;
    }

    private static Color MemoryTint(Color source)
    {
        var gray = source.r * 0.299f + source.g * 0.587f + source.b * 0.114f;
        return ShadeStyle switch
        {
            MemoryShadeStyle.Dim => new Color(source.r * 0.45f, source.g * 0.45f, source.b * 0.5f, source.a),
            MemoryShadeStyle.Grayscale => new Color(gray * 0.55f, gray * 0.57f, gray * 0.62f, source.a),
            MemoryShadeStyle.Blueprint => new Color(gray * 0.30f, gray * 0.45f, gray * 0.75f, source.a),
            MemoryShadeStyle.Sepia => new Color(gray * 0.62f, gray * 0.52f, gray * 0.38f, source.a),
            _ => source,
        };
    }

    /// <summary>True when perception culling is on and no living colonist's
    /// disk covers this tile. A colonist herself is never affected by it — her
    /// own tile is distance 0 inside her own disk. Movers (NPCs/mobs/corpses)
    /// on such a tile are hidden; static object views on it are FROZEN
    /// (skipped by the sync loop) instead.</summary>
    private bool CullHidesTile(TileCoord tile) =>
        _cullActive && !_cullVisibleTiles.Contains(tile);

    /// <summary>
    /// §149: «моя» — та, КЕМ Я УПРАВЛЯЮ, а не та, чья фракция `Colony`. На
    /// сервере в HugeIsland/Maniac выдаётся девушка любого лагеря, и завязка на
    /// одну фракцию прятала выданную соседку в тумане и уводила выбор по
    /// умолчанию на колонистку, которой игрок командовать не может.
    /// `Faction.Colony` остаётся рядом ради локальной игры и анонимного
    /// зрителя: там владения нет вовсе, а клан показывать надо.
    /// </summary>
    private bool IsPlayerOwnedNpc(NpcSnapshot npc) =>
        // §149 r2 (#232): вторым слагаемым — ЛАГЕРЬ выданной девушки, а не
        // буквальная Colony (фолбэк PlayerCampView для локальной игры и
        // анонимного зрителя — та же Colony).
        npc.Faction == PlayerCampView.Of(_runner, _lastSnapshot) ||
        (_runner != null && _runner.CanControlNpc(npc.Id));

    private bool FogSeesTile(TileCoord tile)
    {
        foreach (var (eyeTile, radius) in _fogEyes)
        {
            if (HexSpatialMath.HexDistance(eyeTile, tile) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    // Animal keys share one pose map: dogs get positive ids, crabs negative.
    private void SyncAnimalViews(WorldSnapshot snapshot)
    {
        var liveKeys = new HashSet<int>();
        foreach (var dog in snapshot.Mobs)
        {
            var key = dog.Id;
            liveKeys.Add(key);
            if (!_mobViews.TryGetValue(key, out var mobView) || mobView == null)
            {
                mobView = CreateMobView(dog.MobId, dog.Id);
                if (mobView == null)
                {
                    continue;
                }
                _mobViews[key] = mobView;
            }

            // FOG-OF-WAR EXPERIMENT: a mob exists for the player only while a
            // colonist would notice it (the sim's spot-scan radius).
            var fogHideMob = (_fogActive && !FogSeesTile(dog.Tile)) || CullHidesTile(dog.Tile);
            if (mobView.activeSelf == fogHideMob)
            {
                mobView.SetActive(!fogHideMob);
            }

            if (mobView.TryGetComponent<MobView>(out var wolfView))
            {
                wolfView.SetStatus(dog.Status);
                // Timed melee: pulse the bite snap exactly while the sim
                // winds up; between bites the wolf stands recovering.
                wolfView.SetAttacking(dog.IsAttacking, dog.AttackStartTick);
                // §29C.3-hit: a health drop = the quarry's strike landed —
                // the view fires the short flinch.
                wolfView.SignalHealth(dog.Health);

                // Wound stamps catch up to lost HP (idempotent, seeded).
                if (mobView.TryGetComponent<MobWoundPainter>(out var woundPainter))
                {
                    woundPainter.SetHealth(dog.Health);
                }

                // 29C.3 v2: fighters square up — both turn to face each other.
                Transform? fightTarget = null;
                if (dog.Status == "Fighting" && dog.TargetNpcId >= 0 &&
                    _npcViews.TryGetValue(dog.TargetNpcId, out var quarryView) &&
                    quarryView != null)
                {
                    fightTarget = quarryView.transform;
                }

                wolfView.SetFightTarget(fightTarget);
            }

            // §135: добыча в зубах. Компонент появляется только у зверя, который
            // хоть раз что-то нёс, — обычному волку он не нужен ни кадра.
            if (!string.IsNullOrEmpty(dog.CarriedLimbPart))
            {
                if (!mobView.TryGetComponent<Wearing.MobCarriedLimbView>(out var carried))
                {
                    carried = mobView.AddComponent<Wearing.MobCarriedLimbView>();
                    carried.Configure(ActorScale, HexRadius);
                }

                carried.SetCarried(dog.CarriedLimbOwnerNpcId, dog.CarriedLimbPart);
            }
            else if (mobView.TryGetComponent<Wearing.MobCarriedLimbView>(out var empty))
            {
                empty.SetCarried(-1, string.Empty);
            }

            UpdateAnimalPose(key, dog.Position, dog.Tile);
        }

        foreach (var crab in snapshot.Crabs)
        {
            var key = -crab.Id - 1;
            liveKeys.Add(key);
            if (!_crabViews.TryGetValue(crab.Id, out var crabView) || crabView == null)
            {
                crabView = CreateMobView(HexLive.Simulation.Content.MobIds.Crab, crab.Id);
                if (crabView == null)
                {
                    continue;
                }
                _crabViews[crab.Id] = crabView;
            }

            // FOG-OF-WAR EXPERIMENT: same spot-radius rule as the dogs above.
            var fogHideCrab = (_fogActive && !FogSeesTile(crab.Tile)) || CullHidesTile(crab.Tile);
            if (crabView.activeSelf == fogHideCrab)
            {
                crabView.SetActive(!fogHideCrab);
            }

            UpdateAnimalPose(key, crab.Position, crab.Tile);
        }

        // §147.5: превью виртуальных слотов. Поза считается ЗДЕСЬ из
        // (Seed, Tick) той же чистой функцией, что проверяет материализацию
        // сим — ноль байт wire; вью делит ключ с будущим живым зверем
        // (ReservedMobId), поэтому переход превью→живой бесшовен: объект не
        // пересоздаётся, поворот и поза продолжаются.
        foreach (var slot in snapshot.MobSlots)
        {
            if (slot.State != (int)HexLive.Simulation.Wildlife.MobSlotState.Virtual ||
                slot.Ring.Count == 0)
            {
                continue; // Live ведёт запись живого зверя, Cooldown невидим
            }

            var step = HexLive.Simulation.Wildlife.MobPreview.ComputeStep(
                snapshot.Seed, slot.SlotId, slot.CycleIndex, slot.Ring.Count,
                snapshot.Tick,
                HexLive.Simulation.Runtime.WildlifeBalance.MobPreviewSegmentTicks,
                HexLive.Simulation.Runtime.WildlifeBalance.MobPreviewPauseChance);
            var from = slot.Ring[step.From];
            var to = slot.Ring[step.To];
            var position = step.Paused
                ? from.Position
                : HexLive.Simulation.Wildlife.MobPreview.Lerp(
                    from.Position, to.Position, step.Alpha);
            var tile = step.Paused || step.Alpha < 0.5f ? from.Tile : to.Tile;

            var isCrab = slot.MobId == HexLive.Simulation.Content.MobIds.Crab;
            var key = isCrab ? -slot.ReservedMobId - 1 : slot.ReservedMobId;
            liveKeys.Add(key);
            var views = isCrab ? _crabViews : _mobViews;
            if (!views.TryGetValue(slot.ReservedMobId, out var previewView) || previewView == null)
            {
                previewView = CreateMobView(slot.MobId, slot.ReservedMobId);
                if (previewView == null)
                {
                    continue;
                }
                views[slot.ReservedMobId] = previewView;
            }

            // FOG-OF-WAR EXPERIMENT: то же правило радиуса, что у живых.
            var fogHidePreview = (_fogActive && !FogSeesTile(tile)) || CullHidesTile(tile);
            if (previewView.activeSelf == fogHidePreview)
            {
                previewView.SetActive(!fogHidePreview);
            }

            UpdateAnimalPose(key, position, tile);
        }

        PruneAnimalViews(_mobViews, liveKeys, negate: false);
        PruneAnimalViews(_crabViews, liveKeys, negate: true);
    }

    private void UpdateAnimalPose(int key, Float2 position, TileCoord tile)
    {
        var target = SimulationUnityMapper.ToUnityPosition(position, GroundY(tile));
        var pose = new Pose(target, Quaternion.identity);
        _prevAnimalPoses[key] = _currAnimalPoses.TryGetValue(key, out var old) ? old : pose;
        _currAnimalPoses[key] = pose;
    }

    private void PruneAnimalViews(Dictionary<int, GameObject> views, HashSet<int> liveKeys, bool negate)
    {
        var stale = new List<int>();
        foreach (var id in views.Keys)
        {
            var key = negate ? -id - 1 : id;
            if (!liveKeys.Contains(key))
            {
                stale.Add(id);
            }
        }

        foreach (var id in stale)
        {
            Destroy(views[id]);
            views.Remove(id);
            var key = negate ? -id - 1 : id;
            _prevAnimalPoses.Remove(key);
            _currAnimalPoses.Remove(key);
        }
    }

    // Identical model to the NPC interpolation above: the sim now moves the
    // dog's Position a small delta each fast tick (AnimalCombatSystem.GlideDog
    // mirrors MovementSystem), so the pose pair spans one sim tick and a plain
    // lerp with the shared TickAlpha is perfectly smooth — no more per-view
    // glide bookkeeping. Facing follows the actual travel direction.
    private void InterpolateAnimal(GameObject view, int key, float alpha)
    {
        if (!_currAnimalPoses.TryGetValue(key, out var curr))
        {
            return;
        }

        if (_prevAnimalPoses.TryGetValue(key, out var prev))
        {
            var pos = Vector3.Lerp(prev.Position, curr.Position, alpha);
            var direction = curr.Position - prev.Position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.00001f)
            {
                view.transform.rotation = Quaternion.Slerp(
                    view.transform.rotation, Quaternion.LookRotation(direction),
                    1f - Mathf.Exp(-10f * Time.deltaTime));
            }

            view.transform.position = pos;
        }
        else
        {
            view.transform.position = curr.Position;
        }
    }

    // ONE view factory for every mob/wildlife body (spec §29C.3): the prefab
    // and every per-mob view number come from the mob's MobConfig asset
    // (footprint on the hex, stride tuning, blood splash, wound stamps).
    // Adding a tiger = drop a MobConfig asset with a prefab path — no new
    // view code. A missing or travelling bundle never becomes a primitive.
    private GameObject CreateMobView(string mobId, int id)
    {
        var root = new GameObject($"Mob {mobId} #{id}");
        root.transform.SetParent(_npcsRoot, false);

        var config = Config.MobLibrary.Get(mobId);
        // The prefab comes ONLY from the mob's atomic config/main payload — no
        // hardcoded path and no primitive body when it is still travelling.
        var prefab = Config.MobLibrary.LoadPrefab(mobId);
        if (prefab != null)
        {
            var body = Instantiate(prefab, root.transform);
            body.name = "Body";
            if (config != null && Mathf.Abs(config.visualYawOffsetDegrees) > 0.001f)
            {
                // The root keeps facing the actual travel vector for combat,
                // animation speed and effects. Species such as crabs rotate
                // only their authored body so locomotion reads as sideways.
                body.transform.localRotation *= Quaternion.Euler(
                    0f, config.visualYawOffsetDegrees, 0f);
            }
            if (ObjectFit.WorldBounds(body, out var bounds))
            {
                // Pack models are authored at real-world size; normalize the
                // footprint on the hex per config (wolf: 0.84×HexRadius).
                var size = bounds.size;
                var length = Mathf.Max(size.x, size.z);
                var footprint = config != null ? config.footprintFraction : 0.84f;
                if (length > 0.001f)
                {
                    body.transform.localScale *= HexRadius * footprint / length;
                }
            }

            root.AddComponent<MobView>().Configure(config);

            // Persistent wounds painted into the pelt (NPC stamp tech,
            // random-spot edition) — seeded by id so save/load repaints the
            // same pattern.
            if (config == null || config.woundStamps)
            {
                root.AddComponent<MobWoundPainter>()
                    .Configure(id, config != null ? config.maxWoundStamps : 8);
            }

            return root;
        }

        Destroy(root);
        return null;
    }

    /// <summary>§155.5: греет тяжёлые двери появляющейся девушки и отвечает,
    /// можно ли собирать вью БЕЗ дисковых чтений в кадре. Каждый вызов на
    /// непрогретой двери стартует её асинхронную загрузку; готовность
    /// наступает через пассы, когда всё в кэшах. Карты покраски появление не
    /// гейтят — их промах дорог, но одноразов, поэтому они только греются.</summary>
    private bool NpcAppearanceWarm(NpcSnapshot npc)
    {
        var ready = !string.IsNullOrEmpty(npc.ActorMesh) &&
            ContentPrefabCache.Request("actor", npc.ActorMesh, out _) ==
            ContentPrefabCache.Availability.Ready;

        if (!Wearing.HairContent.IsCached(npc.Hairstyle))
        {
            Wearing.HairContent.Prewarm(npc.Hairstyle);
            ready = false;
        }

        foreach (var worn in npc.WornItems)
        {
            // TryGetVisuals сам стартует PrewarmAsync на промахе.
            if (!string.IsNullOrEmpty(worn) &&
                !Wearing.ActorWardrobe.TryGetVisuals(worn, out _))
            {
                ready = false;
            }
        }

        HexLive.UnityPresentation.Content.AtomicResources.Prewarm(
            "HexLive/PaintMaps/skin_" + npc.ActorMesh);
        HexLive.UnityPresentation.Content.AtomicResources.Prewarm(
            "HexLive/PaintMaps/skinpos_" + npc.ActorMesh);
        return ready;
    }

    private GameObject CreateNpcView(NpcSnapshot npc)
    {
        // Spec 31B.5/§152: the girls get their exact actor payload; a pending or
        // missing actor never turns into a permanent primitive body.
        if (!string.IsNullOrEmpty(npc.ActorMesh))
        {
            var actorPrefab = ContentPrefabCache.GetOrRequest("actor", npc.ActorMesh);
            if (actorPrefab != null)
            {
                var actorRoot = new GameObject($"NPC {npc.Id.Value} ({npc.DisplayName})");
                actorRoot.transform.SetParent(_npcsRoot, false);

                var actorBody = Instantiate(actorPrefab, actorRoot.transform);
                actorBody.name = npc.ActorMesh;
                var targetHeight = HexRadius * NpcHeightFactor * 2.4f;
                var scale = targetHeight / ActorSourceHeightMeters;
                actorBody.transform.localScale = Vector3.one * scale;
                actorBody.transform.localPosition = Vector3.zero;

                var view = actorRoot.AddComponent<NpcActorView>();
                // §74: the body is the mesh, but the face, the hair and the
                // voice are hers alone — the simulation rolled them from the
                // seed and saved them, so a reload rebuilds the same woman.
                // §85: and her eyes, on an axis of their own.
                view.Construct(npc.ActorMesh, npc.Id.Value,
                    npc.SkinSet, npc.EyeColor, npc.Hairstyle, npc.VoiceBank);
                _actorViews[npc.Id.Value] = view;
                _lastTalkResultTick[npc.Id.Value] = npc.TalkResultTick;
                _lastSocialCueKey[npc.Id.Value] =
                    $"{npc.SocialCueTick}:{npc.SocialCueKind}:" +
                    $"{npc.SocialCuePeerId ?? -1}:{npc.SocialCueItemId}";
                return actorRoot;
            }
        }

        if (string.IsNullOrEmpty(npc.ActorMesh))
        {
            Debug.LogError($"[AtomicContent] NPC {npc.Id.Value} has no actor content id.");
        }
        return null;
    }

    // Junction positions are immutable after worldgen — one id→position
    // dictionary replaces the old linear scan of all ~14k snapshot junctions
    // per object (~84 objects × full scan every tick was the single biggest
    // self-time item in the 2026-07-19 deep capture).
    private Dictionary<int, Float2> _junctionAnchorById;

    private Vector3 GetObjectAnchorPosition(WorldSnapshot snapshot, ObjectSnapshot worldObject)
    {
        if (worldObject.Junctions.Count > 0)
        {
            if (_junctionAnchorById == null ||
                _junctionAnchorById.Count != snapshot.Junctions.Count)
            {
                _junctionAnchorById ??= new Dictionary<int, Float2>(snapshot.Junctions.Count);
                _junctionAnchorById.Clear();
                foreach (var junction in snapshot.Junctions)
                {
                    _junctionAnchorById[junction.Id.Value] = junction.WorldPosition;
                }
            }

            if (_junctionAnchorById.TryGetValue(worldObject.Junctions[0].Value, out var pos))
            {
                // §120: a MODULE is drawn from its building's anchor junction —
                // that is the point its LocalX/LocalZ was measured from, and
                // ArchitectureModuleView adds the offset on top of this
                // transform. Its own Junctions[0] is normally that same anchor,
                // but the door's is not: RepairPlanTopology rebinds the door
                // piece to the navigation throat, and drawing from there applied
                // the door's own offset twice (measured 1.299 wu straight out
                // through the wall line, and only on the door).
                if (FreeArchitectureRules.IsFreePiece(worldObject))
                {
                    // A direct LEGO object owns itself. Its LocalX/LocalZ were
                    // measured from this tile centre; a door may rebind its one
                    // Junction to the throat for pathfinding, but that must not
                    // move the visible leaf a second time.
                    pos = HexSpatialMath.TileToWorld(worldObject.Tile);
                }
                else if (worldObject.ArchitectureOwnerObjectId is { } planOwnerKey &&
                    IsPlanBuildingModule(worldObject) &&
                    _planOwnerAnchors.TryGetValue(planOwnerKey, out var ownerAnchor))
                {
                    pos = ownerAnchor;
                }
                else if (IsIntegratedHutBed(worldObject))
                {
                    pos = IntegratedHutBedVisualPosition(snapshot, worldObject, pos);
                }

                // §120: the plan's own furniture stands on its footprint CENTRE,
                // never on its anchor junction — the same correction the
                // constructor preview draws with (BlueprintFurnitureFootprints
                // .CentroidOffset). For a bed that is 0.1875 wu along its length;
                // without it every bed slid that far off the wall it was laid
                // against. The anchor itself stays the route/interaction point.
                if (TryPlanFurnitureCentroidOffset(worldObject, out var centroid))
                {
                    pos = new Float2(pos.X + centroid.X, pos.Y + centroid.Y);
                }

                return SimulationUnityMapper.ToUnityPosition(pos, ObjectGroundY(worldObject));
            }
        }

        return SimulationUnityMapper.ToUnityTilePosition(worldObject.Tile, ObjectGroundY(worldObject));
    }

    private static Float2 IntegratedHutBedVisualPosition(
        WorldSnapshot snapshot, ObjectSnapshot bed, Float2 anchor)
    {
        ObjectSnapshot? hut = null;
        foreach (var candidate in snapshot.Objects)
        {
            if (candidate.DefinitionId == ContentIds.Hut1Hex && candidate.Tile.Equals(bed.Tile))
            {
                hut = candidate;
                break;
            }
        }
        if (hut == null) return anchor;

        var center = HexSpatialMath.TileToWorld(bed.Tile);
        return BuildingRules.HutBedVisualPosition(
            center, hut.RotationDegrees, anchor);
    }

    /// <summary>
    /// §120: is this world object a module of a house raised from a player PLAN
    /// (as opposed to a slot of the canonical hut's single assembly)? Such a
    /// module draws itself, through its own <c>ArchitectureModuleView</c>.
    /// </summary>
    private bool IsPlanBuildingModule(ObjectSnapshot worldObject) =>
        worldObject.ArchitectureElements.Count == 1 &&
        worldObject.ArchitectureOwnerObjectId.HasValue &&
        _planBuildingOwners.Contains(worldObject.ArchitectureOwnerObjectId.Value);

    /// <summary>
    /// §120: is this world object a piece of the plan's OWN furniture — a bed,
    /// the hearth, the wardrobe the player put on the drawing board, whether it
    /// is already raised or still standing as its build-site?
    ///
    /// <para>
    /// The test is the hex it stands on: <c>BuildingBootstrap
    /// .StakePlanFurnitureSites</c> only ever stakes inside the building's own
    /// footprint, and the canonical <c>hut_1hex</c> can never appear there. It
    /// deliberately accepts the SITE too (<c>BuildProduct</c>), so the growing
    /// pile stands exactly where the finished piece will.
    /// </para>
    /// </summary>
    private bool IsPlanFurniture(ObjectSnapshot worldObject) =>
        _planFootprintTiles.Count > 0 &&
        !worldObject.ArchitectureOwnerObjectId.HasValue &&
        _planFootprintTiles.Contains(worldObject.Tile) &&
        HexLive.Simulation.Runtime.Blueprints.BlueprintFurnitureFootprints.HasFootprint(
            PlanFurnitureDefinitionId(worldObject));

    private static string PlanFurnitureDefinitionId(ObjectSnapshot worldObject) =>
        string.IsNullOrEmpty(worldObject.BuildProduct)
            ? worldObject.DefinitionId
            : worldObject.BuildProduct;

    /// <summary>
    /// §120: anchor junction -> footprint centre for the plan's own furniture,
    /// the one correction that makes a raised bed land where the constructor
    /// preview draws it. Zero (and false) for a symmetric footprint — a hearth,
    /// a wardrobe — so nothing moves that has no reason to.
    /// </summary>
    private bool TryPlanFurnitureCentroidOffset(ObjectSnapshot worldObject, out Float2 offset)
    {
        offset = default;
        if (!IsPlanFurniture(worldObject)) return false;
        var yawStep = Mathf.RoundToInt(worldObject.RotationDegrees / 60f);
        var centroid = HexLive.Simulation.Runtime.Blueprints.BlueprintFurnitureFootprints
            .CentroidOffset(PlanFurnitureDefinitionId(worldObject), yawStep);
        if (Mathf.Abs(centroid.X) < 0.0001f && Mathf.Abs(centroid.Y) < 0.0001f) return false;
        offset = centroid;
        return true;
    }

    private bool IsIntegratedHutBed(ObjectSnapshot worldObject) =>
        worldObject.DefinitionId == ContentIds.BedBasic &&
        (worldObject.Variant == ContentIds.HutBedVariant || _floorTiles.Contains(worldObject.Tile));

    private bool IsIntegratedHutHearth(ObjectSnapshot worldObject) =>
        worldObject.DefinitionId == ContentIds.Campfire &&
        (worldObject.Variant == BuildingRules.HutHearthVariant || _floorTiles.Contains(worldObject.Tile));

    private static Float2 GetObjectAnchorFromJunctions(ObjectSnapshot worldObject, Dictionary<int, Float2> junctionPositions)
    {
        if (worldObject.Junctions.Count > 0 && junctionPositions.TryGetValue(worldObject.Junctions[0].Value, out var pos))
        {
            return pos;
        }

        return HexSpatialMath.TileToWorld(worldObject.Tile);
    }

    // Spec 20.16: a solid hex prism — flat top (submesh 0) plus a skirt of
    // six quads down to a shared base (submesh 1) so elevation reads as rock
    // columns that visually meet their lower neighbours. UVs are world-space
    // so the biome texture flows continuously from tile to tile.
    private static Mesh BuildHexPrismMesh(float radius, float top, float baseY, float worldX, float worldZ,
        bool includeSkirt = true)
    {
        var mesh = new Mesh { name = "HexPrism" };

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var topTris = new List<int>();
        var wallTris = new List<int>();

        // --- top face (fan around the centre) ---
        vertices.Add(new Vector3(0f, top, 0f));
        uvs.Add(new Vector2(worldX, worldZ) * TopUvScale);

        var rim = new Vector3[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = Mathf.Deg2Rad * (60f * i - 30f);
            rim[i] = new Vector3(radius * Mathf.Cos(angle), top, radius * Mathf.Sin(angle));
            vertices.Add(rim[i]);
            uvs.Add(new Vector2(worldX + rim[i].x, worldZ + rim[i].z) * TopUvScale);
        }

        for (var i = 0; i < 6; i++)
        {
            var a = 1 + i;
            var b = 1 + (i + 1) % 6;
            topTris.Add(0);
            topTris.Add(b);
            topTris.Add(a);
        }

        // --- perimeter skirt (own vertices for hard-edged cliff shading) ---
        // Spec 20.16: water tiles skip the skirt so no opaque wall reaches the
        // surface between two water hexes — otherwise the stylized water's
        // depth-intersection foam would ring every hex, not just the shore.
        if (includeSkirt)
        {
            for (var i = 0; i < 6; i++)
            {
                var a = rim[i];
                var b = rim[(i + 1) % 6];
                var start = vertices.Count;

                vertices.Add(new Vector3(a.x, top, a.z));
                vertices.Add(new Vector3(b.x, top, b.z));
                vertices.Add(new Vector3(b.x, baseY, b.z));
                vertices.Add(new Vector3(a.x, baseY, a.z));

                var uA = i * WallUvScaleU;
                var uB = (i + 1) * WallUvScaleU;
                uvs.Add(new Vector2(uA, top * WallUvScaleV));
                uvs.Add(new Vector2(uB, top * WallUvScaleV));
                uvs.Add(new Vector2(uB, baseY * WallUvScaleV));
                uvs.Add(new Vector2(uA, baseY * WallUvScaleV));

                wallTris.Add(start + 0);
                wallTris.Add(start + 1);
                wallTris.Add(start + 2);
                wallTris.Add(start + 0);
                wallTris.Add(start + 2);
                wallTris.Add(start + 3);
            }
        }

        mesh.subMeshCount = includeSkirt ? 2 : 1;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(topTris, 0);
        if (includeSkirt)
        {
            mesh.SetTriangles(wallTris, 1);
        }

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private GameObject _waterSurface;

    // Spec 20.16: how many times each of a hex's 6 wedges is subdivided. The
    // Definitive water shader offsets vertices by a UV-driven wave; a bare hex
    // top (7 verts spanning ~3u) sampled it far too coarsely, so each hex
    // tented on its own. Level 4 drops edge length to ~0.37u so the merged
    // sheet reads as one continuous wave. NOTE: the wave frequency is set by
    // the material's _WavesAmplitude (it multiplies UV inside the sine) — if
    // the surface still looks choppy, lower _WavesAmplitude for a gentler,
    // longer wave rather than piling on more triangles here.
    private const int WaterSubdivisions = 4;

    // Merge every water tile's top into a SINGLE tessellated, world-space mesh
    // with one transparent draw. This kills both seam sources at once: the
    // per-hex tenting (now finely sampled + shared across the whole sheet) and
    // the double-blend where separate transparent tile draws overlapped along
    // shared edges. Vertices are baked in world XZ; the object sits at the
    // origin so posWS == the baked coords and the shader wave stays continuous.
    // §148: ВОДА В ТУМАН НЕ ПОПАДАЕТ — решение игрока. Море рисуется целиком
    // с первого кадра: так красивее, а «остров-силуэт» никого не смущает —
    // разведывать всё равно нужно землю, деревья и людей на ней.
    private void EnsureWaterSurface(WorldSnapshot snapshot)
    {
        if (_waterSurface != null)
        {
            return;
        }

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        foreach (var tile in snapshot.Tiles)
        {
            if (!tile.Water)
            {
                continue;
            }

            var world = HexSpatialMath.TileToWorld(tile.Coord);
            var topHeight = SimulationUnityMapper.TileHeight
                          + tile.Elevation * ElevationStep
                          + ElevationStep * SwimVisuals.SurfaceStepOffset; // shore-anchored surface
            AppendSubdividedHexTop(vertices, uvs, tris, world.X, world.Y, topHeight, WaterSubdivisions);
        }

        if (vertices.Count == 0)
        {
            return;
        }

        var mesh = new Mesh
        {
            name = "WaterSurface",
            // Many tiles × subdivided verts easily clears the 16-bit ceiling.
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        _waterSurface = new GameObject("WaterSurface");
        _waterSurface.transform.SetParent(_tilesRoot, false);
        _waterSurface.transform.position = Vector3.zero; // verts already world XZ
        _waterSurface.AddComponent<MeshFilter>().sharedMesh = mesh;
        _waterSurface.AddComponent<MeshRenderer>().sharedMaterial = CreateWaterMaterial();
    }

    // Tessellate one hexagon (centred at world cx,cz, top at y) into a
    // triangular grid and append it to the shared lists. Each of the 6 wedges
    // (centre → rim[i] → rim[i+1]) is split into n² sub-triangles via
    // barycentric subdivision. UVs mirror BuildHexPrismMesh (world XZ scaled).
    private void AppendSubdividedHexTop(
        List<Vector3> vertices, List<Vector2> uvs, List<int> tris,
        float cx, float cz, float y, int n)
    {
        var centre = new Vector3(cx, y, cz);

        var rim = new Vector3[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = Mathf.Deg2Rad * (60f * i - 30f);
            rim[i] = new Vector3(cx + HexRadius * Mathf.Cos(angle), y, cz + HexRadius * Mathf.Sin(angle));
        }

        for (var i = 0; i < 6; i++)
        {
            // Wind centre → rim[i+1] → rim[i] so the top faces +Y (matches
            // BuildHexPrismMesh); the water shader is Cull Off regardless.
            AppendSubdividedTriangle(vertices, uvs, tris, centre, rim[(i + 1) % 6], rim[i], n);
        }
    }

    private void AppendSubdividedTriangle(
        List<Vector3> vertices, List<Vector2> uvs, List<int> tris,
        Vector3 p0, Vector3 p1, Vector3 p2, int n)
    {
        var baseIndex = vertices.Count;

        // Row r (0..n) holds r+1 points along the p0→p1 / p0→p2 fronts.
        for (var r = 0; r <= n; r++)
        {
            for (var c = 0; c <= r; c++)
            {
                var w0 = (n - r) / (float)n;
                var w2 = r == 0 ? 0f : c / (float)n;
                var w1 = 1f - w0 - w2;
                var p = p0 * w0 + p1 * w1 + p2 * w2;
                vertices.Add(p);
                uvs.Add(new Vector2(p.x, p.z) * TopUvScale);
            }
        }

        // Triangulate the barycentric grid (index of (r,c) = r*(r+1)/2 + c).
        for (var r = 1; r <= n; r++)
        {
            var rowStart = baseIndex + r * (r - 1) / 2;       // start of row r-1
            var nextStart = baseIndex + r * (r + 1) / 2;      // start of row r
            for (var c = 0; c < r; c++)
            {
                tris.Add(rowStart + c);
                tris.Add(nextStart + c);
                tris.Add(nextStart + c + 1);
                if (c < r - 1)
                {
                    tris.Add(rowStart + c);
                    tris.Add(nextStart + c + 1);
                    tris.Add(rowStart + c + 1);
                }
            }
        }
    }

    // ---- Flat low-poly biome colours ----
    // No textures: each tile is a flat-shaded facet with a small, stable
    // per-tile shade jitter so a field of grass reads as a hand-placed
    // patchwork rather than one dead-flat sheet. Materials are cached by
    // quantised colour so the SRP batcher still groups most tiles.

    private static Color BiomeColor(string biome)
    {
        return biome switch
        {
            "grass" => new Color(0.42f, 0.63f, 0.30f),
            "grass_dry" => new Color(0.56f, 0.63f, 0.33f),
            "hill" => new Color(0.53f, 0.47f, 0.30f),
            "rock" => new Color(0.56f, 0.51f, 0.45f),
            "mountain" => new Color(0.60f, 0.60f, 0.62f),
            "sand" => new Color(0.89f, 0.81f, 0.58f),
            "cliff" => new Color(0.47f, 0.35f, 0.26f),
            _ => new Color(0.5f, 0.5f, 0.5f)
        };
    }

    // Deterministic ±amount brightness wobble keyed off the tile coord.
    private static Color Jitter(Color color, TileCoord coord, float amount)
    {
        var seed = (uint)(coord.Q * 73856093 ^ coord.R * 19349663) ^ 0x85EBCA6Bu;
        var k = 1f + (NextRand(ref seed) - 0.5f) * 2f * amount;
        return new Color(color.r * k, color.g * k, color.b * k);
    }

    private static readonly Dictionary<int, Material> _flatMaterials = new();

    // Dry base colours for the wet-terrain effect (rain darkens the cache).
    private static readonly Dictionary<int, Color> _flatBaseColors = new();
    // B0: обратная карта материал → сухой цвет для перекрасок памяти/кольца.
    private static readonly Dictionary<Material, Color> _materialDryColors = new();
    private static Color _grassBaseColor = Color.white;

    // Riverbed variant of the flat terrain material: identical look, but
    // renderQueue 2999 keeps it OUT of _CameraDepthTexture (opaques ≤ 2500)
    // while still drawing under the transparent water — the stylized water
    // must not "see" the bed or the whole river reads as foamy shallows.
    private static readonly Dictionary<int, Material> _riverbedMaterials = new();

    private static Material RiverbedMaterial(Color color)
    {
        var key = (Mathf.RoundToInt(color.r * 24f) << 16)
                  | (Mathf.RoundToInt(color.g * 24f) << 8)
                  | Mathf.RoundToInt(color.b * 24f);
        if (_riverbedMaterials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var material = new Material(GetFlatMaterial(color))
        {
            renderQueue = 2999
        };
        _riverbedMaterials[key] = material;
        _materialDryColors[material] = color;
        return material;
    }

    private static Material GetFlatMaterial(Color color)
    {
        // Quantise to ~24 levels per channel to keep the material count low.
        var key = (Mathf.RoundToInt(color.r * 24f) << 16)
                  | (Mathf.RoundToInt(color.g * 24f) << 8)
                  | Mathf.RoundToInt(color.b * 24f);
        if (_flatMaterials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = color
        };
        material.SetFloat("_Smoothness", 0f);
        material.SetFloat("_Cull", 0f); // double-sided: skirt interiors never show
        _flatMaterials[key] = material;
        _flatBaseColors[key] = color;
        _materialDryColors[material] = color;
        return material;
    }

    // ---- Terrain grass tufts ----

    private static Material _grassMaterial;

    private static Material GetGrassMaterial()
    {
        if (_grassMaterial != null)
        {
            return _grassMaterial;
        }

        // Flat solid-green blades — low-poly, no texture, no alpha seams.
        _grassBaseColor = new Color(0.36f, 0.56f, 0.24f);
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = _grassBaseColor
        };
        material.SetFloat("_Smoothness", 0f);
        material.SetFloat("_Cull", 0f); // single triangles seen from both sides
        _grassMaterial = material;
        _materialDryColors[material] = _grassBaseColor;
        return material;
    }

    private void BuildGrassClump(Transform parent, float radius, float topY, TileCoord coord)
    {
        var go = new GameObject("Grass");
        go.transform.SetParent(parent, false);
        var meshFilter = go.AddComponent<MeshFilter>();
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = GetGrassMaterial();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshFilter.sharedMesh = BuildGrassMesh(radius, topY, coord);
        RegisterVegetationWind(
            go.transform, coord.Q * 73856093 ^ coord.R * 19349663,
            maxTiltDegrees: 2.2f);
        _grassByTile[coord] = go;
        // A-1: регистрация при создании (не пер-тиковый переобход всех тайлов);
        // ApplyOverviewRenderer внутри сразу выдаёт клочку текущий профиль
        // обзора, даже если он создан уже на отдалении.
        RegisterOverviewRenderers(go, _overviewGrassRenderers);
    }

    private void RegisterVegetationWind(Transform target, int phaseSeed, float maxTiltDegrees)
    {
        _vegetationWindTargets.Add(new VegetationWindTarget
        {
            Transform = target,
            BaseRotation = target.localRotation,
            Phase = Hash01(PositiveHash(phaseSeed) ^ 0xA511E9B3u) * Mathf.PI * 2f,
            MaxTiltDegrees = maxTiltDegrees,
        });
    }

    private void UnregisterVegetationWind(Transform target)
    {
        for (var i = _vegetationWindTargets.Count - 1; i >= 0; i--)
        {
            if (_vegetationWindTargets[i].Transform == target)
            {
                _vegetationWindTargets.RemoveAt(i);
            }
        }
    }

    private void UpdateVegetationWind()
    {
        if (_vegetationWindTargets.Count == 0)
        {
            return;
        }

        // One deterministic weather band lasts long enough to read as weather,
        // not jitter. The last four seconds blend into the next weak/medium/
        // strong band without a snap.
        const float bandSeconds = 18f;
        const float transitionSeconds = 4f;
        var time = Time.time;
        var band = Mathf.FloorToInt(time / bandSeconds);
        var withinBand = time - band * bandSeconds;
        var blend = Mathf.SmoothStep(0f, 1f,
            Mathf.InverseLerp(bandSeconds - transitionSeconds, bandSeconds, withinBand));
        var strength = Mathf.Lerp(WindBandStrength(band), WindBandStrength(band + 1), blend);
        var direction = Mathf.LerpAngle(WindBandDirection(band), WindBandDirection(band + 1), blend) *
                        Mathf.Deg2Rad;
        var directionX = Mathf.Sin(direction);
        var directionZ = -Mathf.Cos(direction);

        for (var i = _vegetationWindTargets.Count - 1; i >= 0; i--)
        {
            var target = _vegetationWindTargets[i];
            if (target.Transform == null)
            {
                _vegetationWindTargets.RemoveAt(i);
                continue;
            }

            var flutter = 0.76f + 0.24f * Mathf.Sin(time * 1.55f + target.Phase);
            var tilt = target.MaxTiltDegrees * strength * flutter;
            target.Transform.localRotation = target.BaseRotation * Quaternion.Euler(
                directionX * tilt, 0f, directionZ * tilt);
        }
    }

    private static float WindBandStrength(int band)
    {
        return (PositiveHash(band) % 3) switch
        {
            0 => 0.22f,
            1 => 0.55f,
            _ => 1f,
        };
    }

    private static float WindBandDirection(int band) =>
        Hash01(PositiveHash(band) ^ 0x68BC21EBu) * 360f;

    private static float DeterministicVegetationYaw(int objectId) =>
        Hash01(PositiveHash(objectId) ^ 0xC2B2AE35u) * 360f;

    private static uint PositiveHash(int value)
    {
        var hash = unchecked((uint)value) + 0x9E3779B9u;
        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35u;
        return hash ^ (hash >> 16);
    }

    private static float Hash01(uint value) =>
        (value & 0x00FFFFFFu) / 16777215f;

    // A lying body flattens the grass under it: the tile's tuft clump hides
    // while someone sleeps/faints/lies dead there and pops back after. Cost is
    // O(lying bodies) per tick — only the affected tiles ever toggle.
    private readonly Dictionary<TileCoord, GameObject> _grassByTile = new();
    private readonly HashSet<TileCoord> _lyingTiles = new();
    private readonly HashSet<TileCoord> _floorTiles = new();

    // §120: hexes that already carry at least ONE raised floor sector of a
    // plan-built house. Deliberately NOT merged into _floorTiles: that set is
    // the simulation's TileFlags.HasFloor, which also lifts an actor onto the
    // deck and decides whether a bed/hearth is the canonical hut's built-in
    // furniture. This one answers the grass question only, and it answers it
    // the moment the first sector stands rather than when the floor is done.
    private readonly HashSet<TileCoord> _architectureFloorTiles = new();

    // One footprint list per plan building per tick, not per module: every
    // module of a building shares its owner's tile and yaw.
    private readonly Dictionary<int, IReadOnlyList<TileCoord>> _planFootprints = new();
    private readonly HashSet<TileCoord> _hiddenGrassTiles = new();
    private readonly List<TileCoord> _grassToggleScratch = new();

    private const string ArchitectureFloorId = "architecture.floor.board";

    /// <summary>
    /// §120: the grass on a hex goes as soon as ONE floor sector is raised on
    /// it — not when the room's floor is finished and not when the house is.
    ///
    /// <para>
    /// Which hex that is cannot be read off the module's own <c>Tile</c>: every
    /// module of a building is spawned on the building's anchor tile
    /// (<c>BuildingRules.EnsureHutElements</c>), and a player plan spans several
    /// hexes. A floor module's local offset IS its hex CENTRE relative to the
    /// plan anchor (<c>BlueprintBuildingPlan.Modules</c> places a sector at
    /// <c>HexCenter</c>), so turning it by the building's yaw lands it exactly
    /// on one of the footprint hexes the simulation already computes.
    /// </para>
    /// </summary>
    private void CollectArchitectureFloorTile(ObjectSnapshot worldObject)
    {
        if (worldObject.ArchitectureElements.Count != 1) return;
        var element = worldObject.ArchitectureElements[0];
        if (element.DefinitionId != ArchitectureFloorId || !element.Complete) return;
        if (!worldObject.ArchitectureOwnerObjectId.HasValue) return;

        var ownerKey = worldObject.ArchitectureOwnerObjectId.Value;
        if (!_planFootprints.TryGetValue(ownerKey, out var footprint))
        {
            footprint = HexLive.Simulation.Bootstrap.BuildingBootstrap.FootprintTiles(
                ContentIds.HutPlan, worldObject.Tile, worldObject.RotationDegrees);
            _planFootprints[ownerKey] = footprint;
        }

        // Simulation yaw is CCW about +X, exactly the plane the offset lives in.
        var radians = worldObject.RotationDegrees * Mathf.Deg2Rad;
        var cos = Mathf.Cos(radians);
        var sin = Mathf.Sin(radians);
        var offsetX = element.LocalX * cos - element.LocalZ * sin;
        var offsetZ = element.LocalX * sin + element.LocalZ * cos;
        var anchor = HexSpatialMath.TileToWorld(worldObject.Tile);
        var best = worldObject.Tile;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < footprint.Count; i++)
        {
            var centre = HexSpatialMath.TileToWorld(footprint[i]);
            var dx = centre.X - anchor.X - offsetX;
            var dz = centre.Y - anchor.Y - offsetZ;
            var distance = dx * dx + dz * dz;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = footprint[i];
            }
        }

        _architectureFloorTiles.Add(best);
    }

    // §113: ОДИН ответ на «она сейчас на земле?». Разбор ПО ПОЗАМ (какой
    // цепочкой её ронять) живёт в SyncActorView и остаётся там — здесь союз
    // всех его лежачих веток. Пока этот союз был переписан от руки во второй
    // раз, он разошёлся ровно там, где такое расхождение и незаметно: рыдающая
    // (§110) ложилась на землю, а трава под ней стояла торчком — сон, кома и
    // обморок траву гасили, слёзы нет.
    //
    // Ползущая (§50, обе ноги) сюда входит намеренно: она тоже волочится по
    // земле, просто ещё и движется — гекс под ней гаснет, пройденный отрастает.
    internal static bool IsLyingDown(NpcSnapshot npc) =>
        npc.IsFainted ||          // §40.13 обморок
        npc.IsUnconscious ||      // §60 кома
        npc.IsDying ||            // §105 лежащая на грани
        npc.IsPlayingDead ||      // §105.14 притворяющаяся
        npc.IsCrying ||           // §110 стресс-крах: лежит и рыдает
        npc.PostureHint == "Crawl" ||
        (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress");

    private void UpdateGrassFlattening(WorldSnapshot snapshot)
    {
        _lyingTiles.Clear();
        foreach (var npc in snapshot.Npcs)
        {
            // Баг #125: несомая на руках НЕ приминает траву. Её несут именно
            // потому, что она в обмороке или коме, так что IsLyingDown для неё
            // истинно, а Tile у неё — тайл носильщика. Без этой проверки за
            // парой тянулся след гаснущей травы по всему маршруту, хотя тело
            // ни одного из этих гексов не касалось.
            if (IsLyingDown(npc) && npc.CarriedByNpcId is null)
            {
                _lyingTiles.Add(npc.Tile);
            }
        }

        foreach (var obj in snapshot.Objects)
        {
            if (obj.DefinitionId == ContentIds.CorpseNpc ||
                obj.DefinitionId == ContentIds.HumanRemains)
            {
                _lyingTiles.Add(obj.Tile);
            }
        }

        // Re-grow where nobody lies anymore.
        _grassToggleScratch.Clear();
        foreach (var coord in _hiddenGrassTiles)
        {
            if (!_lyingTiles.Contains(coord) && !_floorTiles.Contains(coord) &&
                !_architectureFloorTiles.Contains(coord))
            {
                _grassToggleScratch.Add(coord);
            }
        }

        foreach (var coord in _grassToggleScratch)
        {
            _hiddenGrassTiles.Remove(coord);
            if (_grassByTile.TryGetValue(coord, out var grass) && grass != null)
            {
                grass.SetActive(true);
            }
        }

        // Flatten under the newly lying.
        HideGrassOn(_lyingTiles);

        // An architectural floor covers the terrain tuft: the simulation's own
        // floored tiles (a finished hut), and — §120 — any hex where a single
        // floor sector of a plan has been raised, long before the building is.
        // This deliberately shares the same renderer toggle as flattening: no
        // hidden duplicate mesh remains poking through the floor, and the sleep
        // path cannot re-grow it when a character gets up.
        HideGrassOn(_floorTiles);
        HideGrassOn(_architectureFloorTiles);
    }

    private void HideGrassOn(HashSet<TileCoord> tiles)
    {
        foreach (var coord in tiles)
        {
            if (!_hiddenGrassTiles.Contains(coord) &&
                _grassByTile.TryGetValue(coord, out var grass) && grass != null)
            {
                grass.SetActive(false);
                _hiddenGrassTiles.Add(coord);
            }
        }
    }

    private Mesh BuildGrassMesh(float radius, float topY, TileCoord coord)
    {
        var mesh = new Mesh { name = "GrassClump" };
        var vertices = new List<Vector3>();
        var tris = new List<int>();

        // Deterministic per-tile scatter — same tile always looks the same.
        var seed = (uint)(coord.Q * 73856093 ^ coord.R * 19349663) ^ 0x9E3779B9u;
        var apothem = radius * 0.78f;

        for (var b = 0; b < _grassBladesPerTile; b++)
        {
            var ang = NextRand(ref seed) * Mathf.PI * 2f;
            var rad = Mathf.Sqrt(NextRand(ref seed)) * apothem;
            var px = Mathf.Cos(ang) * rad;
            var pz = Mathf.Sin(ang) * rad;
            var h = radius * (0.14f + NextRand(ref seed) * 0.10f);
            var w = radius * 0.05f;
            var yaw = NextRand(ref seed) * Mathf.PI;
            // A tiny lean so tufts aren't rigidly vertical.
            var lean = radius * (NextRand(ref seed) - 0.5f) * 0.06f;

            AddGrassBlade(vertices, tris, px, pz, topY, w, h, yaw, lean);
            AddGrassBlade(vertices, tris, px, pz, topY, w, h, yaw + Mathf.PI * 0.5f, lean);
        }

        mesh.SetVertices(vertices);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // One low-poly blade: a triangle tapering from a base edge to a tip.
    private static void AddGrassBlade(List<Vector3> vertices, List<int> tris,
        float px, float pz, float baseY, float halfWidth, float height, float yaw, float lean)
    {
        var dx = Mathf.Cos(yaw) * halfWidth;
        var dz = Mathf.Sin(yaw) * halfWidth;
        var start = vertices.Count;

        vertices.Add(new Vector3(px - dx, baseY, pz - dz));
        vertices.Add(new Vector3(px + dx, baseY, pz + dz));
        vertices.Add(new Vector3(px + lean, baseY + height, pz + lean));

        tris.Add(start + 0);
        tris.Add(start + 1);
        tris.Add(start + 2);
    }

    // Cheap deterministic LCG in [0,1); avoids UnityEngine.Random global state.
    private static float NextRand(ref uint state)
    {
        state = state * 1664525u + 1013904223u;
        return (state >> 8) / 16777216f;
    }

    // Spec 31C.3: normalize any downloaded/transferred model to the hex
    // metric by its rendered bounds — no per-asset scale guessing.
    private void FitObjectPrefab(GameObject instance, string definitionId, int idValue, bool scatter = true)
    {
        // Size comes from the shared ObjectFit table (same one the in-hand prop
        // uses in NpcActorView.SetHandProp) so a tool/coconut is the same physical
        // size on the ground and in the hand.
        instance.transform.localScale *= ObjectFit.FitScaleFactor(instance, definitionId);
        // A prefab root authored with a stray offset (meat was lifted 0.3 — it
        // hovered over the grass) must not survive into the drop pose: the fit
        // owns the pose completely, so start from a clean origin. GroundVisual
        // seats the bounds bottom AT the pivot height, so a lifted pivot floats.
        instance.transform.localPosition = Vector3.zero;
        // Scattered ground pose BEFORE grounding so the drop rests on its rotated
        // bounds (a lain-flat tool sits on its side, not floating at its old height).
        // §66: a BUILT piece is placed, not dropped — its yaw is the sim's, so the
        // scatter must not fight it (the root already carries the staked rotation).
        if (scatter)
        {
            instance.transform.localRotation = GroundScatterRotation(
                definitionId,
                idValue,
                instance.transform.localRotation);
        }

        GroundVisual(instance);
        SuppressSmallPropShadows(instance);
    }

    // PERF (profiling, Aug-2026): the shadow pass was drawing ~2 300 casters a
    // frame and cost ~3 ms on the render thread. A prop this short throws a
    // shadow a few pixels wide — nothing you can see at play distance — while
    // still costing a draw call in every cascade it falls into. Measured by the
    // fitted bounds rather than by an id list, so a new item classifies itself;
    // trees, beds, fires and the girls are all far above the line and keep
    // their shadows. The same short props also move to the SmallProps layer,
    // which the camera distance-culls (PrototypeRuntimeBootstrap.InstallCamera):
    // a dropped knife two screens away is sub-pixel and needs no draw call.
    // Colliders move with the layer — the default raycast mask still includes
    // it, so hover/click picking is unaffected.
    private const float ShadowCastMinHeightFactor = 0.30f;

    private void SuppressSmallPropShadows(GameObject instance)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return;
        }

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        if (bounds.size.y > HexRadius * ShadowCastMinHeightFactor)
        {
            return;
        }

        for (var i = 0; i < renderers.Length; i++)
        {
            renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        var smallPropLayer = LayerMask.NameToLayer("SmallProps");
        if (smallPropLayer >= 0)
        {
            SetLayerRecursive(instance.transform, smallPropLayer);
        }
    }

    private static void SetLayerRecursive(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (var i = 0; i < root.childCount; i++)
        {
            SetLayerRecursive(root.GetChild(i), layer);
        }
    }

    // A dropped prop's ground pose: a deterministic random yaw so the map doesn't
    // read as a rigid grid, and — for items authored standing — a tip onto the
    // side so they lie flat like something dropped, not stuck upright in the soil.
    private static Quaternion GroundScatterRotation(
        string definitionId,
        int idValue,
        Quaternion authoredRotation)
    {
        // Deterministic per-object yaw in [0,360): stable across every view
        // rebuild, unlike UnityEngine.Random which would pop on each refresh.
        uint state = (uint)idValue * 2654435761u;
        var yaw = NextRand(ref state) * 360f;

        if (LiesFlatOnGround(definitionId))
        {
            // Authored handle-+Y (TOOL_GENERATION_SPEC); 90° about X tips that long
            // axis onto the ground, then the yaw scatters which way it points.
            return Quaternion.Euler(90f, yaw, 0f);
        }

        // The approved palm frond FBX carries the Blender-to-Unity correction
        // on its imported root (currently X ~= 270 degrees). Replacing that
        // rotation with yaw stood dropped leaves on edge and made their pose
        // depend on whichever source/fallback happened to load. Preserve the
        // exact imported pose and scatter only around world up.
        if (definitionId == "resource.palm_leaf" || definitionId == "tool.bottle")
        {
            return Quaternion.Euler(0f, yaw, 0f) * authoredRotation;
        }

        return Quaternion.Euler(0f, yaw, 0f);
    }

    // Spec tools are authored standing (handle +Y). On the ground they should lie
    // on their side like a dropped tool; the bottle is a container that rests
    // upright.
    private static bool LiesFlatOnGround(string definitionId)
    {
        return definitionId.StartsWith("tool.") && definitionId != "tool.bottle";
    }

    // Ground the model: bottom of its renderer bounds sits on the tile top
    // (plus an optional epsilon for near-flat meshes that would z-fight).
    private static void GroundVisual(GameObject instance, float lift = 0f)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return;
        }

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        var offset = instance.transform.position.y - bounds.min.y + lift;
        instance.transform.localPosition += new Vector3(0f, offset, 0f);
    }

    private static PrimitiveType GetObjectPrimitive(string definitionId)
    {
        // Spec 31C.5: legible silhouettes — no more mystery spheres.
        if (definitionId.StartsWith("water.") || definitionId == "campfire.spot" ||
            definitionId.Contains("stone") || definitionId.Contains("boulder") ||
            definitionId == "grave.npc" || definitionId == "station.drying_rack")
        {
            return PrimitiveType.Cylinder;
        }

        if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource.") ||
            definitionId == "construction.site" ||
            definitionId == "furniture.wardrobe")
        {
            return PrimitiveType.Cube;
        }

        if (definitionId == "corpse.npc")
        {
            return PrimitiveType.Capsule;
        }

        if (definitionId.Contains("tree"))
        {
            return PrimitiveType.Cylinder;
        }

        if (definitionId.Contains("bed"))
        {
            return PrimitiveType.Cube;
        }

        if (definitionId.Contains("chair"))
        {
            return PrimitiveType.Cylinder;
        }

        if (definitionId.Contains("clothing"))
        {
            return PrimitiveType.Capsule;
        }

        return PrimitiveType.Sphere;
    }

    private static Vector3 GetObjectScale(string definitionId)
    {
        var r = HexSpatialMath.HexRadius;
        if (definitionId.StartsWith("water."))
        {
            return new Vector3(r * 0.5f, r * 0.015f, r * 0.5f); // flat pool disc
        }

        if (definitionId == "campfire.spot")
        {
            return new Vector3(r * 0.3f, r * 0.04f, r * 0.3f); // ember ring
        }

        if (definitionId.Contains("boulder"))
        {
            return new Vector3(r * 0.35f, r * 0.22f, r * 0.35f);
        }

        if (definitionId.Contains("stone"))
        {
            return new Vector3(r * 0.12f, r * 0.06f, r * 0.12f);
        }

        if (definitionId == "grave.npc")
        {
            return new Vector3(r * 0.22f, r * 0.05f, r * 0.35f);
        }

        if (definitionId == "corpse.npc")
        {
            return new Vector3(r * 0.14f, r * 0.05f, r * 0.3f); // lying blob
        }

        if (definitionId == "station.drying_rack")
        {
            return new Vector3(r * 0.05f, r * 0.35f, r * 0.05f); // post
        }

        if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource."))
        {
            return Vector3.one * r * 0.11f;
        }

        if (definitionId == "construction.site")
        {
            return new Vector3(r * 0.5f, r * 0.1f, r * 0.5f);
        }

        if (definitionId.Contains("tree"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * TreeRadiusFactor,
                HexSpatialMath.HexRadius * TreeHeightFactor,
                HexSpatialMath.HexRadius * TreeRadiusFactor);
        }

        if (definitionId.Contains("bed"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * BedWidthFactor,
                HexSpatialMath.HexRadius * BedHeightFactor,
                HexSpatialMath.HexRadius * BedDepthFactor);
        }

        if (definitionId.Contains("chair"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * ChairRadiusFactor,
                HexSpatialMath.HexRadius * ChairHeightFactor,
                HexSpatialMath.HexRadius * ChairRadiusFactor);
        }

        if (definitionId.Contains("clothing"))
        {
            return new Vector3(
                HexSpatialMath.HexRadius * ClothingRadiusFactor,
                HexSpatialMath.HexRadius * ClothingHeightFactor,
                HexSpatialMath.HexRadius * ClothingRadiusFactor);
        }

        return Vector3.one * (HexSpatialMath.HexRadius * FoodRadiusFactor);
    }

    private static GameObject CreatePrimitiveVisual(Transform parent, PrimitiveType primitiveType, Vector3 scale, Color color)
    {
        var visual = GameObject.CreatePrimitive(primitiveType);
        visual.transform.SetParent(parent, false);
        visual.transform.localScale = scale;
        visual.transform.localPosition = new Vector3(0f, GetBottomOffset(primitiveType, scale), 0f);

        var renderer = visual.GetComponent<MeshRenderer>();
        if (renderer is not null)
        {
            renderer.sharedMaterial = CreateMaterial(color);
        }

        return visual;
    }

    private static float GetBottomOffset(PrimitiveType primitiveType, Vector3 scale)
    {
        switch (primitiveType)
        {
            case PrimitiveType.Capsule:
            case PrimitiveType.Cylinder:
                return scale.y;
            case PrimitiveType.Cube:
            case PrimitiveType.Sphere:
            default:
                return scale.y * 0.5f;
        }
    }

    private static Color GetTileColor(TileSnapshot tile)
    {
        if (tile.Water)
        {
            return new Color(0.25f, 0.45f, 0.75f);
        }

        if (tile.Blocked)
        {
            return new Color(0.38f, 0.29f, 0.24f);
        }

        if (!tile.Walkable)
        {
            return new Color(0.27f, 0.26f, 0.25f);
        }

        return tile.Indoor
            ? new Color(0.72f, 0.67f, 0.56f)
            : new Color(0.49f, 0.61f, 0.47f);
    }

    private static Color GetJunctionColor(JunctionSnapshot junction)
    {
        if (junction.Occupied)
        {
            return new Color(0.83f, 0.19f, 0.19f);
        }

        if (junction.Reserved)
        {
            return new Color(0.95f, 0.45f, 0.12f);
        }

        if (junction.Blocked)
        {
            return new Color(0.45f, 0.15f, 0.15f);
        }

        return junction.Tiles.Count > 1
            ? new Color(0.24f, 0.78f, 0.34f)
            : new Color(0.94f, 0.94f, 0.94f);
    }

    // Distinct body tints so multiple NPCs are tellable apart in play mode.
    private static Color GetNpcBodyColor(int npcId)
    {
        return (npcId % 4) switch
        {
            1 => new Color(0.88f, 0.84f, 0.72f), // warm beige
            2 => new Color(0.70f, 0.80f, 0.90f), // pale blue
            3 => new Color(0.76f, 0.88f, 0.70f), // pale green
            _ => new Color(0.90f, 0.78f, 0.86f)  // pale pink
        };
    }

    private static Color GetObjectColor(string definitionId)
    {
        if (definitionId == "food.coconut" || definitionId == "food.coconut_pierced")
        {
            return new Color(0.42f, 0.28f, 0.15f); // coconut husk
        }

        if (definitionId.Contains("tree"))
        {
            return new Color(0.18f, 0.42f, 0.16f);
        }

        if (definitionId.Contains("bed"))
        {
            return new Color(0.39f, 0.52f, 0.78f);
        }

        if (definitionId.Contains("chair"))
        {
            return new Color(0.75f, 0.48f, 0.22f);
        }

        if (definitionId.Contains("clothing"))
        {
            return new Color(0.22f, 0.48f, 0.82f);
        }

        if (definitionId.StartsWith("water."))
        {
            return new Color(0.25f, 0.5f, 0.8f);
        }

        if (definitionId == "campfire.spot")
        {
            return new Color(0.9f, 0.45f, 0.1f);
        }

        if (definitionId.Contains("stone") || definitionId.Contains("boulder"))
        {
            return new Color(0.5f, 0.5f, 0.52f);
        }

        if (definitionId == "grave.npc")
        {
            return new Color(0.35f, 0.35f, 0.38f);
        }

        if (definitionId == "corpse.npc")
        {
            return new Color(0.8f, 0.65f, 0.55f);
        }

        if (definitionId.StartsWith("tool."))
        {
            return new Color(0.45f, 0.32f, 0.18f);
        }

        if (definitionId.StartsWith("resource."))
        {
            return new Color(0.6f, 0.45f, 0.25f);
        }

        if (definitionId.StartsWith("food."))
        {
            return new Color(0.85f, 0.3f, 0.25f);
        }

        if (definitionId == "construction.site")
        {
            return new Color(0.8f, 0.7f, 0.3f);
        }

        return new Color(0.6f, 0.6f, 0.6f); // neutral unknown, not alarm-red
    }

    private static Material CreateMaterial(Color color)
    {
        var material = new Material(RequireRuntimeLitShader())
        {
            color = color
        };

        return material;
    }

    private static Shader RequireRuntimeLitShader()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            throw new System.InvalidOperationException(
                "Universal Render Pipeline/Lit was stripped from Player. " +
                "HexLiveReleaseBuilder must retain the runtime-generated world shader.");
        }

        return shader;
    }
}

}
