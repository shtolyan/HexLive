#nullable enable
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
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
    // buffer directly; flip this on only when console tracing is needed.
    [SerializeField] private bool _logTraceEventsToConsole;

    private float _accumulator;
    private SimulationEngine? _engine;
    private SimulationClock? _clock;
    private SimulationSettings? _settings;
    private int _lastLoggedEventIndex;

    public SimulationEngine? Engine => _engine;

    public bool IsReady => _engine is not null;

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

    private void Update()
    {
        if (_engine is null || _clock is null || _settings is null || _clock.IsPaused)
        {
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
            AutosaveEnabled = true;
        }
    }

    private void AutosaveTick()
    {
        if (!AutosaveEnabled)
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
        if (AutosaveEnabled)
        {
            WriteSaveNow();
        }
    }

    private void OnDestroy()
    {
        // Editor play-mode exit skips OnApplicationQuit — save here too.
        if (AutosaveEnabled)
        {
            WriteSaveNow();
        }
    }

    private void FlushEventsToConsole()
    {
        if (_engine is null) return;

        var events = _engine.World.Events.Items;
        if (events.Count == 0) return;

        // Keep the index moving even while logging is off, so enabling the
        // toggle mid-run starts from "now" instead of dumping the backlog.
        if (!_logTraceEventsToConsole)
        {
            _lastLoggedEventIndex = events.Count;
            return;
        }

        // If buffer was trimmed and our index is beyond start, reset
        if (_lastLoggedEventIndex > events.Count)
        {
            _lastLoggedEventIndex = 0;
        }

        for (var i = _lastLoggedEventIndex; i < events.Count; i++)
        {
            var e = events[i];
            var entityTag = e.EntityId.HasValue ? $"NPC#{e.EntityId.Value}" : "SYS";
            Debug.Log($"[HexLive T{e.Tick}] [{entityTag}] {e.Type}: {e.Message}");
        }

        _lastLoggedEventIndex = events.Count;
    }

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
        if (_engine is null)
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
            Debug.LogWarning("Simulation bootstrap skipped: no world JSON asset assigned.", this);
            return;
        }

        var definition = WorldBootstrapJsonParser.Parse(_bootstrapAsset.WorldJson.text);
        Bootstrap(definition);
    }

    private void Bootstrap(WorldBootstrapDefinition definition)
    {
        var factory = new WorldStateFactory();
        var world = factory.Create(definition);

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

        _engine = new SimulationEngine(world, _settings, _clock);
        RegisterDefaultSystems(_engine);
        _accumulator = 0f;
        _lastLoggedEventIndex = 0;
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
        engine.Register(new DogSystem());
        engine.Register(new PredationSystem()); // §56: kill-a-housemate-to-eat
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
        engine.Register(new HazardSystem()); // §50: prepared amputation hazards
    }
}

}
