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

    private ISimulationBackend? _backend;
    private long _lastLoggedSeq;
    private readonly List<SimulationEvent> _drainedEvents = new();
    private readonly GameHistoryLog _gameHistory = new();

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

    public WorldSnapshot? CreateSnapshot() => _backend?.CreateSnapshot();

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
        if (_backend is null || !_backend.IsPaused || AutosaveEnabled)
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

        // Player builds drop the per-tick trace chatter (TickStart/Movement*/
        // ExecProgress… — nobody consumes it in-game; GameHistory has its own
        // whitelist). Editor + development builds keep the full stream for
        // debugging and the sim harness.
        HexLive.Simulation.Runtime.SimTrace.Verbose =
            Application.isEditor || UnityEngine.Debug.isDebugBuild;

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
