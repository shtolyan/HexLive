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

    public WorldSnapshot? CreateSnapshot()
    {
        return _engine is null ? null : WorldSnapshotExporter.Export(_engine.World);
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
        var maxTicks = Mathf.Max(1, _settings.MaxTicksPerFrame);

        for (var i = 0; i < maxTicks && _accumulator >= _settings.TickDeltaTime; i++)
        {
            _accumulator -= _settings.TickDeltaTime;
            _engine.Step();
        }

        FlushEventsToConsole();
    }

    private void FlushEventsToConsole()
    {
        if (_engine is null) return;

        var events = _engine.World.Events.Items;
        if (events.Count == 0) return;

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
    }

    private static void RegisterDefaultSystems(SimulationEngine engine)
    {
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        engine.Register(new PerceptionSystem());
        engine.Register(new DecisionSystem());
        engine.Register(new PlanningSystem());
        engine.Register(new NeedsDecaySystem());
        engine.Register(new TemperatureSystem());
    }
}

}
