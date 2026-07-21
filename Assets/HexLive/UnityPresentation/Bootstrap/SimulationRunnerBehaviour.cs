#nullable enable
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.History;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{

public sealed class SimulationRunnerBehaviour : MonoBehaviour
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

    private float _accumulator;
    private SimulationEngine? _engine;
    private SimulationClock? _clock;
    private SimulationSettings? _settings;
    private SimulationEvent? _lastLoggedEvent;
    private readonly GameHistoryLog _gameHistory = new();

    public SimulationEngine? Engine => _engine;

    public GameHistoryLog GameHistory => _gameHistory;

    public bool IsReady => _engine is not null;

    public bool IsCompleted => _engine?.World.Completed ?? false;

    public int CurrentTick => _engine?.World.Tick ?? 0;

    public bool IsPaused => _clock?.IsPaused ?? true;

    public float SpeedMultiplier => _clock?.SpeedMultiplier ?? 1f;

    /// <summary>
    /// Fraction [0..1) of progress toward the next simulation tick.
    /// Used by renderers to interpolate between discrete tick states.
    /// </summary>
    public float TickAlpha =>
        _settings is not null && _settings.TickDeltaTime > 0f
            ? Mathf.Clamp01(_accumulator / _settings.TickDeltaTime)
            : 0f;

    private WorldSnapshot? _cachedSnapshot;
    private int _cachedSnapshotTick = -1;
    private bool _cachedSnapshotDetailed;

    // Spec 31.17: consumers poll every frame; the world serializes once
    // per simulation tick. The cached snapshot is handed back to the
    // exporter for in-place reuse, so consumers must not hold it across
    // ticks (they all re-poll every frame).
    public WorldSnapshot? CreateSnapshot()
    {
        if (_engine is null)
        {
            return null;
        }

        var detailed = WorldSnapshotExporter.IncludeDebugDetails;
        if (_cachedSnapshot is not null &&
            _engine.World.Tick == _cachedSnapshotTick &&
            _cachedSnapshotDetailed == detailed)
        {
            return _cachedSnapshot;
        }

        _cachedSnapshot = WorldSnapshotExporter.Export(_engine.World, _cachedSnapshot);
        _cachedSnapshotTick = _engine.World.Tick;
        _cachedSnapshotDetailed = detailed;
        return _cachedSnapshot;
    }

    private void Awake()
    {
        if (_engine is null)
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
        if (_engine is null)
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
        }

        if (_engine is null || _clock is null || _settings is null || _clock.IsPaused)
        {
            return;
        }

        if (_engine.World.Completed)
        {
            _clock.Pause();
            _accumulator = 0f;
            return;
        }

        _accumulator += Time.unscaledDeltaTime * _clock.SpeedMultiplier;

        // Spec 41.1: a frame hitch (spawning, GC, shader compile) must never
        // turn into a catch-up burst of ticks. ~3 game-seconds of backlog max;
        // at 50x the per-frame budget (~0.8 s) stays far below the clamp.
        _accumulator = Mathf.Min(_accumulator, _settings.TickDeltaTime * 12f);

        var maxTicks = Mathf.Max(1, _settings.MaxTicksPerFrame);

        for (var i = 0; i < maxTicks && _accumulator >= _settings.TickDeltaTime; i++)
        {
            _accumulator -= _settings.TickDeltaTime;
            _engine.Step();
            if (_engine.World.Completed)
            {
                _clock.Pause();
                _accumulator = 0f;
                break;
            }
        }

        FlushEventsToConsole();
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
        if (_clock is null || !_clock.IsPaused || AutosaveEnabled)
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
            _clock.Resume();
            AutosaveEnabled = !AutosaveSuppressed;
        }
    }

    private void AutosaveTick()
    {
        if (!AutosaveEnabled || AutosaveSuppressed)
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
        if (_engine is null)
        {
            return;
        }

        SaveGame.Write(_engine.World, SpeedMultiplier);
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

        _gameHistory.Dispose();
    }

    private void FlushEventsToConsole()
    {
        if (_engine is null)
        {
            return;
        }

        var events = _engine.World.Events.Items;
        if (events.Count == 0)
        {
            _gameHistory.Tick();
            return;
        }

        var logAllTrace = _logTraceEventsToConsole;
        var logImportant = _logImportantEventsToConsole;

        var startIndex = 0;
        if (_lastLoggedEvent is not null)
        {
            startIndex = -1;
            for (var i = events.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(events[i], _lastLoggedEvent))
                {
                    continue;
                }

                startIndex = i + 1;
                break;
            }

            if (startIndex < 0)
            {
                startIndex = 0;
            }
        }

        for (var i = startIndex; i < events.Count; i++)
        {
            var e = events[i];
            var isGameHistoryEvent = GameHistoryLog.IsGameHistoryEvent(e);
            if (isGameHistoryEvent)
            {
                RecordGameHistoryEvent(e);
            }

            if (!logAllTrace && !(logImportant && isGameHistoryEvent))
            {
                continue;
            }

            var entityTag = e.EntityId.HasValue ? $"NPC#{e.EntityId.Value}" : "SYS";
            Debug.Log($"[HexLive T{e.Tick}] [{entityTag}] {e.Type}: {e.Message}");
        }

        _lastLoggedEvent = events[events.Count - 1];
        _gameHistory.Tick();
    }

    private void RecordGameHistoryEvent(SimulationEvent simulationEvent) =>
        _gameHistory.Record(simulationEvent);

    public void Pause() => _clock?.Pause();

    public void Resume() => _clock?.Resume();

    public void TogglePause()
    {
        if (_clock is null)
        {
            return;
        }

        if (_clock.IsPaused)
        {
            _clock.Resume();
            return;
        }

        _clock.Pause();
    }

    public void StepSingleTick()
    {
        if (_engine is null || _engine.World.Completed)
        {
            return;
        }

        _engine.Step();
        FlushEventsToConsole();
    }

    public void SetSpeed(float speedMultiplier) => _clock?.SetSpeed(speedMultiplier);

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

        _settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
            MaxTicksPerFrame = 64
        };

        _clock = new SimulationClock();
        _clock.SetSpeed(_initialSpeed);
        if (_startPaused)
        {
            _clock.Pause();
        }
        else
        {
            _clock.Resume();
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
        // Mob prefabs too (wolf FBX + pelt textures): the view spawns the
        // moment the sim spawns the mob — mid-game, typically right before
        // the first fight — and the lazy Resources.Load there was the
        // "small freeze just before the first combat".
        foreach (var mobId in Config.MobLibrary.Ids)
        {
            Config.MobLibrary.LoadPrefab(mobId);
        }

        _engine = new SimulationEngine(world, _settings, _clock);
        RegisterDefaultSystems(_engine);
        _accumulator = 0f;
        _lastLoggedEvent = null;
        _cachedSnapshot = null;
        _cachedSnapshotTick = -1;
    }

    private static void RegisterDefaultSystems(SimulationEngine engine)
    {
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        engine.Register(new PerceptionSystem());
        engine.Register(new DecisionSystem());
        engine.Register(new PlanningSystem());
        engine.Register(new MobSystem());
        engine.Register(new AnimalCombatSystem()); // 29C.3 v2: timed windup→hit→cooldown blows
        engine.Register(new PredationSystem()); // §56: kill-a-housemate-to-eat
        engine.Register(new ThreatAlertSystem()); // §62: spot the wolf early — ⚠️ cue, attack-first or detour
        engine.Register(new RabbitSystem());
        engine.Register(new WeatherSystem());
        engine.Register(new EnvironmentSystem());
        engine.Register(new NeedsDecaySystem());
        engine.Register(new TemperatureSystem());
        engine.Register(new MoistureSystem());
        engine.Register(new FruitProductionSystem());
        engine.Register(new FireSystem());
        engine.Register(new CorpseSystem());
        engine.Register(new MeatSpoilageSystem()); // §54: ground meat rots
        engine.Register(new DreamSystem()); // §64: advances the colony dream queue (campfire → own bed); before BedSiteSystem
        engine.Register(new BedSiteSystem()); // §54.2: stakes progressive bed build-sites
        engine.Register(new HazardSystem()); // §50: prepared amputation hazards
    }
}

}
