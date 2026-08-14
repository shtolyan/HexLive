#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.History;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{

/// <summary>
/// The scene's single handle on the simulation, and the only MonoBehaviour that
/// knows one exists.
/// <para>
/// It owns no world of its own: it holds an <see cref="ISimulationBackend"/> and
/// forwards to it. Today that is always <see cref="LocalEngineBackend"/> — the
/// world runs in this process exactly as it always has. A networked backend
/// slots in at the same place without touching the sixteen components that fetch
/// this behaviour with <c>FindAnyObjectByType</c> (which cannot be given an
/// interface — Unity requires a UnityEngine.Object).
/// </para>
/// <para>
/// What stays HERE rather than in the backend is everything that is presentation
/// even in single-player: the colony history log, fanning events out to sound and
/// the console, autosave cadence, and the two watchdogs that catch a loading
/// screen dying mid-curtain.
/// </para>
/// </summary>
public sealed class SimulationRunnerBehaviour : MonoBehaviour, ISimulationSource
{
    [SerializeField] private WorldBootstrapAsset? _bootstrapAsset;
    [SerializeField] private bool _startPaused = true;
    [SerializeField] private float _initialSpeed = 1f;

    // Perf: mirroring EVERY sim trace event into Debug.Log was an editor
    // killer — each entry captures a stack trace (frame cost at 50x speed)
    // and the console/Editor.log accumulate across play sessions, so the
    // editor got slower with every run. The debug panel reads the event
    // buffer directly; keep the console on high-signal events by default and
    // flip full trace on only when deep console tracing is needed.
    [SerializeField] private bool _logImportantEventsToConsole = true;
    [SerializeField] private bool _logTraceEventsToConsole;

    // Holds the prewarmed object prefabs alive. Resources.LoadAll hands back
    // assets nothing references, which a later UnloadUnusedAssets would be free
    // to drop again — and the whole point of loading them was to not read them
    // from disk mid-tick.
    private static GameObject[]? _objectPrefabPin;

    private ISimulationBackend? _backend;
    private long _lastLoggedSeq;
    private readonly List<SimulationEvent> _drainedEvents = new();
    private readonly GameHistoryLog _gameHistory = new();

    /// <summary>§41.3: экран загрузки берёт прогрев арта СЕБЕ, чтобы крутить
    /// его параллельно намотке офлайна — иначе восемь мегабайт префабов и все
    /// сэмплы FMOD читаются с диска ДО первого тика, и намотка ждёт их зря.
    /// Все остальные вызывающие (dev-сцены, мир из JSON) ничего не замечают:
    /// при выключенном флаге <see cref="Bootstrap(WorldBootstrapDefinition)"/>
    /// греет всё сам, ровно как раньше.</summary>
    public static bool DeferContentPrewarm { get; set; }

    // Что именно греть — СОБИРАЕТСЯ в Bootstrap, на главном потоке, до первого
    // шага мира. Отложить можно загрузку, но не этот обход: он читает
    // Entities.Npcs/Corpses/Objects, а их в это время уже мотает воркер.
    private readonly List<string> _pendingWear = new();
    private bool _contentWarmed;

    /// <summary>
    /// The live engine — LOCAL MODE ONLY, null otherwise.
    /// <para>
    /// This is the dev/test-scene escape hatch: AmputationTest, BedBuildTest,
    /// SwimTest and WolfFightTest build throwaway worlds and mutate them
    /// directly, which no interface should make comfortable. Shipped
    /// presentation must read <see cref="ISimulationSource"/> instead — a new
    /// <c>Engine.World</c> reference in a UI or rendering file is a bug that
    /// only shows up the day the world stops being local.
    /// </para>
    /// </summary>
    public SimulationEngine? Engine => (_backend as LocalEngineBackend)?.Engine;

    public GameHistoryLog GameHistory => _gameHistory;

    public bool IsReady => _backend?.IsReady ?? false;

    public SimulationLink Link => _backend?.Link ?? SimulationLink.Local;

    public bool IsCompleted => _backend?.IsCompleted ?? false;

    public int Seed => _backend?.Seed ?? 0;

    public int CurrentTick => _backend?.CurrentTick ?? 0;

    public bool IsPaused => _backend?.IsPaused ?? true;

    public float SpeedMultiplier => _backend?.SpeedMultiplier ?? 1f;

    /// <summary>
    /// Fraction [0..1) of progress toward the next simulation tick.
    /// Used by renderers to interpolate between discrete tick states.
    /// </summary>
    public float TickAlpha => _backend?.TickAlpha ?? 0f;

    public bool SupportsDirectWorldMutation => _backend?.SupportsDirectWorldMutation ?? false;

    public bool SupportsClientSave => _backend?.SupportsClientSave ?? false;

    public bool SupportsNpcCommands => _backend?.SupportsNpcCommands ?? false;

    public void EnqueueCommand(ISimulationCommand command) => _backend?.EnqueueCommand(command);

    /// <summary>§41.3: пока идёт намотка офлайна, мир принадлежит воркеру, и
    /// экспорт снапшота — обход КАЖДОЙ сущности — споткнулся бы о правку на
    /// полушаге. Отказ ровно здесь закрывает вопрос для всех потребителей
    /// сразу: снапшот и так бывает null (до Configure мира ещё нет), и это все
    /// уже умеют. Гейт по IsReplaying в каждой панели пришлось бы помнить в
    /// каждой новой панели.</summary>
    public WorldSnapshot? CreateSnapshot() =>
        UI.LoadingScreen.IsReplaying ? null : _backend?.CreateSnapshot();

    public bool TryGetObjectDefinition(string id, out ObjectDefinition? definition)
    {
        if (_backend is not null)
        {
            return _backend.TryGetObjectDefinition(id, out definition);
        }

        definition = null;
        return false;
    }

    public long DrainEvents(long sinceSeq, List<SimulationEvent> into) =>
        _backend?.DrainEvents(sinceSeq, into) ?? sinceSeq;

    /// <summary>§30.17: -hexlive-trace — «мне нужна диагностика с первого
    /// тика» (иначе её включают тумблером панели уже в игре).</summary>
    private static bool TraceRequestedOnCommandLine()
    {
        try
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "-hexlive-trace", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (System.Exception)
        {
            // Платформа не отдаёт командную строку — молчим, как и по умолчанию.
        }

        return false;
    }

    private void Awake()
    {
        SimulationSource.Current = this;
        if (_backend is null)
        {
            Bootstrap();
        }
    }

    // Dead-world watchdog: with no world JSON, SOMETHING must call Configure
    // shortly after startup (normally the LoadingScreen, in dev scenes their
    // test bootstrap). If nothing has after a grace period and there is no
    // LoadingScreen around to eventually do it, the game would just sit on a
    // black screen forever — that is an ERROR, not a warning.
    private const float UnconfiguredErrorAfterSeconds = 3f;
    private float _unconfiguredTimer;
    private bool _unconfiguredReported;

    private void Update()
    {
        if (_backend is null)
        {
            _unconfiguredTimer += Time.unscaledDeltaTime;
            if (!_unconfiguredReported &&
                _unconfiguredTimer >= UnconfiguredErrorAfterSeconds &&
                FindAnyObjectByType<HexLive.UnityPresentation.UI.LoadingScreen>(FindObjectsInactive.Include) == null)
            {
                _unconfiguredReported = true;
                Debug.LogError(
                    "Simulation runner is still UNCONFIGURED after " +
                    $"{UnconfiguredErrorAfterSeconds:0}s: no world JSON asset, nobody called Configure, " +
                    "and no LoadingScreen exists to do it — the world will never be created.", this);
            }

            return;
        }

        _backend.Tick(Time.unscaledDeltaTime);

        // Not while paused — matching the old loop, which early-returned before
        // either of these. It matters most during Continue's offline wind: the
        // loading screen steps the engine with the clock PAUSED, and flushing
        // there would pour the entire multi-day chronicle into the console and
        // the history file mid-load (the old behavior delivered only the ring's
        // last 2048 after unpause). It also kept autosave from rewriting an
        // identical world every minute while the game sits on pause.
        // StepSingleTick flushes explicitly, so debug stepping still logs.
        if (_backend.IsPaused)
        {
            return;
        }

        FlushEvents();
        AutosaveTick();
    }

    // Spec 41.2 v2: autosave — every 60 real seconds while enabled (the
    // loading screen turns this on once the world is presented), plus on quit
    // and on leaving play mode (OnDestroy). The save is the FULL model blob
    // (tens of KB) — still trivial next to a 60-second cadence.
    private const float AutosaveIntervalSeconds = 60f;
    private float _autosaveTimer;

    public bool AutosaveEnabled { get; set; }

    // Dev/test scenes set this: their throwaway worlds must NEVER touch the
    // real hexlive_save.dat — not from the 60s tick, not on quit/play-mode
    // exit, and not via the loader watchdog below (which otherwise flips
    // AutosaveEnabled on in any scene that starts paused without a loading
    // screen — exactly what a test bootstrap does).
    public bool AutosaveSuppressed { get; set; }

    // Spec 41.1 safety net: if the loading coroutine dies without unpausing
    // (an in-play domain reload kills coroutines silently), the game must not
    // stay frozen behind a dead curtain — resume once the loader is gone.
    private float _loaderWatchdog;

    private void LateUpdate()
    {
        if (_backend is null || !_backend.IsPaused || AutosaveEnabled || AutosaveSuppressed)
        {
            return;
        }

        _loaderWatchdog += Time.unscaledDeltaTime;
        if (_loaderWatchdog < 2f)
        {
            return;
        }

        _loaderWatchdog = 0f;
        if (FindAnyObjectByType<UI.LoadingScreen>() == null)
        {
            Debug.LogWarning("[HexLive] Loading screen died without finishing — resuming.");
            _backend.Resume();
            AutosaveEnabled = !AutosaveSuppressed;
        }
    }

    private void AutosaveTick()
    {
        if (!AutosaveEnabled || AutosaveSuppressed || _backend is not { SupportsClientSave: true })
        {
            return;
        }

        _autosaveTimer += Time.unscaledDeltaTime;
        if (_autosaveTimer < AutosaveIntervalSeconds)
        {
            return;
        }

        _autosaveTimer = 0f;
        WriteSaveNow();
    }

    public void WriteSaveNow()
    {
        // Nothing to save when someone else owns the world.
        if (_backend is { SupportsClientSave: true })
        {
            _backend.WriteSaveNow();
        }
    }

    private void OnApplicationQuit()
    {
        if (AutosaveEnabled && !AutosaveSuppressed)
        {
            WriteSaveNow();
        }

        _gameHistory.Flush();
    }

    private void OnDestroy()
    {
        // Editor play-mode exit skips OnApplicationQuit — save here too.
        if (AutosaveEnabled && !AutosaveSuppressed)
        {
            WriteSaveNow();
        }

        _backend?.Shutdown();
        if (ReferenceEquals(SimulationSource.Current, this))
        {
            SimulationSource.Current = null;
        }

        _gameHistory.Dispose();
    }

    /// <summary>
    /// Pulls new events off the backend and fans them out to the three sinks
    /// that consume them: the colony history file, the sound layer (§67 voices
    /// discrete world moments straight off the trace stream) and the console.
    /// </summary>
    private void FlushEvents()
    {
        if (_backend is null)
        {
            return;
        }

        _drainedEvents.Clear();
        _lastLoggedSeq = _backend.DrainEvents(_lastLoggedSeq, _drainedEvents);

        var logAllTrace = _logTraceEventsToConsole;
        var logImportant = _logImportantEventsToConsole;

        for (var i = 0; i < _drainedEvents.Count; i++)
        {
            var e = _drainedEvents[i];
            var isGameHistoryEvent = GameHistoryLog.IsGameHistoryEvent(e);
            if (isGameHistoryEvent)
            {
                _gameHistory.Record(e);
            }

            Audio.SoundManager.Instance?.OnSimEvent(e);

            // §121: отказ на приказ игрока — короткая подпись в панели
            // персонажа. В историю колонии он не идёт нарочно: это ответ на
            // клик, а не событие в жизни острова.
            if (e.Type == "ManualOrderRejected" && e.EntityId is { } rejectedNpc)
            {
                Input.ManualOrderFeedback.Report(rejectedNpc, e.Message);
            }
            else if (e.Type == "GroupOrderResult")
            {
                Input.GroupOrderFeedback.Report(e.Message);
            }

            if (!logAllTrace && !(logImportant && isGameHistoryEvent))
            {
                continue;
            }

            var entityTag = e.EntityId.HasValue ? $"NPC#{e.EntityId.Value}" : "SYS";
            Debug.Log($"[HexLive T{e.Tick}] [{entityTag}] {e.Type}: {e.Message}");
        }

        _gameHistory.Tick();
    }

    public void Pause() => _backend?.Pause();

    public void Resume() => _backend?.Resume();

    public void TogglePause() => _backend?.TogglePause();

    public void SetSpeed(float speedMultiplier) => _backend?.SetSpeed(speedMultiplier);

    public void StepSingleTick()
    {
        _backend?.StepSingleTick();
        FlushEvents();
    }

    public void Configure(TextAsset worldJson, bool startPaused = true, float initialSpeed = 1f)
    {
        _bootstrapAsset ??= gameObject.GetComponent<WorldBootstrapAsset>() ?? gameObject.AddComponent<WorldBootstrapAsset>();
        _bootstrapAsset.SetWorldJson(worldJson);
        _startPaused = startPaused;
        _initialSpeed = initialSpeed;
        Bootstrap();
    }

    public void Configure(WorldBootstrapDefinition definition, bool startPaused = true, float initialSpeed = 1f)
    {
        _startPaused = startPaused;
        _initialSpeed = initialSpeed;
        Bootstrap(definition);
    }

    private void Bootstrap()
    {
        if (_bootstrapAsset?.WorldJson is null)
        {
            // Expected at Awake in the shipped flow: PrototypeRuntimeBootstrap
            // adds the runner FIRST (Awake fires inside AddComponent) and only
            // then creates the LoadingScreen, which calls Configure(definition)
            // after the menu. A dead world (nothing ever configures us) is
            // detected by the watchdog in Update instead.
            return;
        }

        var definition = WorldBootstrapJsonParser.Parse(_bootstrapAsset.WorldJson.text);
        Bootstrap(definition);
    }

    private void Bootstrap(WorldBootstrapDefinition definition)
    {
        var factory = new WorldStateFactory();
        var world = factory.Create(definition);
        var saveHeader = SaveGame.TryReadHeader();
        _gameHistory.OpenForWorld(world.Seed, saveHeader != null && saveHeader.seed == world.Seed);

        var settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
            MaxTicksPerFrame = 64
        };

        var clock = new SimulationClock();
        clock.SetSpeed(_initialSpeed);
        if (_startPaused)
        {
            clock.Pause();
        }
        else
        {
            clock.Resume();
        }

        // §30.17: диагностическая трасса МОЛЧИТ ПО УМОЛЧАНИЮ везде — и в
        // билде, и в редакторе: она стоит ~21 МБ аллокаций на тик на большой
        // карте, а читают её только тогда, когда что-то разбирают. Включают
        // осознанно: ключом -hexlive-trace при запуске или тумблером
        // «Trace» в дебаг-панели прямо во время игры.
        HexLive.Simulation.Runtime.SimTrace.Enabled = TraceRequestedOnCommandLine();

        _pendingWear.Clear();
        CollectWornWear(world, _pendingWear);
        _contentWarmed = false;
        if (!DeferContentPrewarm)
        {
            WarmContent();
        }

        // Loopback runs the local world through the wire codec, which interns
        // definition ids — build the table here so that path works too.
        HexLive.Simulation.Wire.DefinitionIdTable.Build(world.Content);

        var engine = new SimulationEngine(world, settings, clock);
        // The list itself lives in the simulation assembly so the game, the server
        // and headless probes cannot drift apart — see SimulationSystemRegistry.
        SimulationSystemRegistry.RegisterDefaults(engine);

        // §30.14: бортовой самописец — по кольцу последних событий на каждого
        // NPC, чтобы дебаг-панель могла показать, ЧТО эта делала до того, как
        // застряла. Общее кольцо на 2048 записей на такой вопрос не отвечает:
        // при ~200 событиях в тик оно живёт около одиннадцати тиков. Только в
        // редакторе и development-сборках — там же, где включён полный трейс.
        if (Application.isEditor || UnityEngine.Debug.isDebugBuild)
        {
            engine.World.FlightRecorder = HexLive.Simulation.Runtime.FlightRecorder.ForBehavior();
        }

        _backend?.Shutdown();
        _backend = CreateBackend(new LocalEngineBackend(engine, clock, settings));
        SimulationSource.Current = this;
        _lastLoggedSeq = 0;
    }

    /// <summary>
    /// Кто из гардероба нужен ЭТОМУ миру прямо сейчас: надетое на живых и на
    /// мёртвых плюс одежда, валяющаяся на земле.
    ///
    /// Спрашивать каталог целиком нельзя — это 712 бандлов и 1.99 ГБ, и занавес
    /// честно ждал бы весь гардероб ради четырёх видимых девушек. Всё остальное
    /// остаётся ленивым: ради этого арт и уехал из Resources.
    ///
    /// ⚠️ Читает Entities.* — значит, только с главного потока и только когда
    /// мир не мотается воркером (§41.3).
    /// </summary>
    private static void CollectWornWear(
        HexLive.Simulation.Core.WorldState world, ICollection<string> into)
    {
        var seen = new HashSet<string>();

        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var item in npc.WornItems)
            {
                seen.Add(item.DefinitionId);
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            foreach (var item in corpse.WornItems)
            {
                seen.Add(item.DefinitionId);
            }
        }

        var garmentIds = new HashSet<string>();
        foreach (var garment in GarmentLibrary.Active)
        {
            garmentIds.Add(garment.Id);
        }

        foreach (var worldObject in world.Entities.Objects.Values)
        {
            if (garmentIds.Contains(worldObject.DefinitionId))
            {
                seen.Add(worldObject.DefinitionId);
            }
        }

        foreach (var id in seen)
        {
            into.Add(id);
        }
    }

    /// <summary>
    /// §41.3: догреть гардероб ПОСЛЕ намотки офлайна.
    ///
    /// Список, собранный до намотки, к её концу устарел: за игровые сутки
    /// колонистка могла переодеться, раздеться, умереть в другом, а на землю —
    /// выпасть то, чего в мире не было вовсе. Прогрев по старому списку оставил
    /// бы такую вещь на ленивом пути, то есть на блокирующем GetVisuals внутри
    /// первого же кадра игры.
    ///
    /// Повторный проход дешёвый: <c>PrewarmAsync</c> молча выходит на всём, что
    /// уже в кэше, так что заявки уходят только на разницу.
    /// </summary>
    public void WarmWornWear()
    {
        if (Engine is not { } engine)
        {
            return;
        }

        var ids = new List<string>();
        CollectWornWear(engine.World, ids);
        foreach (var id in ids)
        {
            Wearing.GarmentDropFactory.Prewarm(id);
        }
    }

    /// <summary>
    /// Прогрев арта: всё, что иначе прочиталось бы с диска в первый раз посреди
    /// игры, внутри тика, на главном потоке. Вызывается либо из
    /// <c>Bootstrap</c> (обычный путь), либо экраном загрузки параллельно
    /// намотке офлайна (<see cref="DeferContentPrewarm"/>).
    ///
    /// Главный поток блокируется — это чтения с диска, и они блокировали его и
    /// раньше. Смысл переноса в том, ЧТО происходит в это время: не «намотка
    /// ждёт», а «намотка идёт на воркере». Идемпотентен: второй вызов молчит.
    ///
    /// ⚠️ Ничего из этого не читает состояние мира — только каталоги и диск.
    /// Список одежды собран заранее, в <c>Bootstrap</c>. Так и должно остаться:
    /// в момент вызова мир может принадлежать другому потоку.
    /// </summary>
    public void WarmContent()
    {
        if (_contentWarmed)
        {
            return;
        }

        _contentWarmed = true;

        // Spec 40.8-G: pull all wound/blood art into memory NOW, behind the
        // loading curtain — lazily loading it on the first landed bite cost a
        // ~2.4 s File.Read burst mid-combat (frame #20 of the deep capture).
        Wearing.SkinTexturePainter.Prewarm();
        Wearing.GarmentWearPainter.Prewarm();
        Rendering.MobWoundPainter.Prewarm();
        _ = Rendering.BloodSplashVfx.Prefabs;
        // Spec §67: every SFX sample loads NOW for the same reason — the FMOD
        // createSound file reads must not land on the first mid-game chop.
        Audio.FmodSfx.Prewarm();
        // Mob prefabs too (wolf FBX + pelt textures): the view spawns the
        // moment the sim spawns the mob — mid-game, typically right before
        // the first fight — and the lazy Resources.Load there was the
        // "small freeze just before the first combat".
        foreach (var mobId in Config.MobLibrary.Ids)
        {
            Config.MobLibrary.LoadPrefab(mobId);
        }

        // PERF (profiling, Aug-2026) — the same trap, one level out: a world
        // OBJECT's model is loaded the first time one of its kind appears, which
        // happens mid-game, inside the tick, on the main thread. The deep capture
        // caught a 205 ms frame whose HexWorldRenderer.Update spent 67 ms in a
        // single blocking File.Read. The whole folder goes in one call on
        // purpose: a hand-kept id list here would drift the moment someone adds
        // a prefab (8.3 MB / 64 assets, so there is nothing to ration).
        _objectPrefabPin = Resources.LoadAll<GameObject>("HexLive/Objects");

        // Одежда — асинхронная: заявки уходят в Addressables и учитываются
        // ContentQueue, ждать их здесь НЕЛЬЗЯ (WaitForCompletion из корутины —
        // тот самый тупик из 105199ce). Экран загрузки дожидается очереди.
        //
        // Список собран ДО намотки офлайна; то, что появилось за неё, догревает
        // WarmWornWear, когда мир возвращается на главный поток.
        foreach (var id in _pendingWear)
        {
            Wearing.GarmentDropFactory.Prewarm(id);
        }

        // Иконки — наоборот, все и сразу: один бандл на 0.73 МБ, разбираться,
        // какие понадобятся, дороже, чем взять их целиком.
        Wearing.Garments.ItemIcons.PrewarmAll();
    }

    /// <summary>
    /// Picks the backend for this session. The local engine is built either way —
    /// it costs one worldgen and it means a failed connection can still fall back
    /// to a playable game instead of a black screen.
    /// </summary>
    private static ISimulationBackend CreateBackend(LocalEngineBackend local)
    {
        switch (SessionConfig.Mode)
        {
            case SimulationMode.Loopback:
                return new LoopbackBackend(local);

            case SimulationMode.Remote:
                var url = SessionConfig.ServerUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    Debug.LogError("[HexLive] Remote session requested with no server URL — staying local.");
                    return local;
                }

                local.Shutdown();
                var remote = new Remote.RemoteSocketBackend(url);
                remote.Connect();
                return remote;

            default:
                return local;
        }
    }
}

}
