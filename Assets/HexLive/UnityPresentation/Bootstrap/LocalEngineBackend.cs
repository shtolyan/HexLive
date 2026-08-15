#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using UnityEngine;
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.UnityPresentation.Bootstrap
{

/// <summary>
/// The world lives in THIS process: our own <see cref="SimulationEngine"/>,
/// stepped on a fixed-timestep accumulator off the frame clock. This is the
/// default and is what single-player has always done — the code below is the
/// old <c>SimulationRunnerBehaviour.Update</c> loop, moved behind
/// <see cref="ISimulationBackend"/> so a second implementation can exist
/// without the renderer, the UI or the audio layer noticing.
/// </summary>
public sealed class LocalEngineBackend : ISimulationBackend
{
    private readonly SimulationEngine _engine;
    private readonly SimulationClock _clock;
    private readonly SimulationSettings _settings;

    private float _accumulator;

    private WorldSnapshot? _cachedSnapshot;
    private int _cachedSnapshotTick = -1;
    private bool _cachedSnapshotDetailed;

    public LocalEngineBackend(SimulationEngine engine, SimulationClock clock, SimulationSettings settings)
    {
        _engine = engine;
        _clock = clock;
        _settings = settings;
    }

    /// <summary>
    /// The live world. Local-only escape hatch for the dev/test scenes, which
    /// build throwaway worlds and mutate them directly. Shipped presentation
    /// must go through <see cref="ISimulationSource"/> instead.
    /// </summary>
    public SimulationEngine Engine => _engine;

    public bool IsReady => true;

    // Nothing between us and the world.
    public SimulationLink Link => SimulationLink.Local;

    public bool IsCompleted => _engine.World.Completed;

    public int Seed => _engine.World.Seed;

    public int CurrentTick => _engine.World.Tick;

    public float TickAlpha =>
        _settings.TickDeltaTime > 0f
            ? Mathf.Clamp01(_accumulator / _settings.TickDeltaTime)
            : 0f;

    public bool IsPaused => _clock.IsPaused;

    public float SpeedMultiplier => _clock.SpeedMultiplier;

    public bool SupportsDirectWorldMutation => true;

    public bool SupportsClientSave => true;

    public bool SupportsNpcCommands => true;

    public bool TryGetCraftingOptions(EntityId npc, List<CraftRecipeOption> into) =>
        CraftingOptions.TryFill(_engine.World, npc, into);

    // §121: очередь опустошается в начале Step, то есть тем же главным потоком,
    // который сюда кладёт. Замка нет и не нужно.
    public void EnqueueCommand(ISimulationCommand command) => _engine.Commands.Enqueue(command);

    // Spec 31.17: consumers poll every frame; the world serializes once per
    // simulation tick. The cached snapshot is handed back to the exporter for
    // in-place reuse, so consumers must not hold it across ticks (they all
    // re-poll every frame).
    public WorldSnapshot CreateSnapshot()
    {
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

    // MAX-speed wall-clock budget: at MAX the sim steps as fast as the CPU
    // allows, but no single frame may spend more than this ticking, so the
    // frame still renders and the UI stays live. One tick always runs.
    private const double MaxSpeedFrameBudgetMs = 25.0;
    private static readonly Stopwatch _maxSpeedWatch = new();

    public void Tick(float unscaledDeltaTime)
    {
        if (_clock.IsPaused)
        {
            return;
        }

        if (_engine.World.Completed)
        {
            _clock.Pause();
            _accumulator = 0f;
            return;
        }

        // MAX speed: step the sim at CPU speed, bypassing the fixed-timestep
        // accumulator entirely. A wall-clock budget still caps how long one
        // frame may spend ticking so the render thread gets a turn and the UI
        // (including the speed bar) stays responsive.
        if (float.IsPositiveInfinity(_clock.SpeedMultiplier))
        {
            _accumulator = 0f;
            RunUncappedTicks();
            return;
        }

        _accumulator += unscaledDeltaTime * _clock.SpeedMultiplier;

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
    }

    private void RunUncappedTicks()
    {
        _maxSpeedWatch.Restart();
        do
        {
            _engine.Step();
            if (_engine.World.Completed)
            {
                _clock.Pause();
                break;
            }
        }
        while (_maxSpeedWatch.Elapsed.TotalMilliseconds < MaxSpeedFrameBudgetMs);
    }

    public void Pause() => _clock.Pause();

    public void Resume() => _clock.Resume();

    public void TogglePause()
    {
        if (_clock.IsPaused)
        {
            _clock.Resume();
            return;
        }

        _clock.Pause();
    }

    public void SetSpeed(float speedMultiplier) => _clock.SetSpeed(speedMultiplier);

    public void StepSingleTick()
    {
        if (_engine.World.Completed)
        {
            return;
        }

        _engine.Step();
    }

    public bool TryGetObjectDefinition(string id, out ObjectDefinition? definition)
    {
        if (id != null && _engine.World.Content.ObjectDefinitions.TryGetValue(id, out var def))
        {
            definition = def;
            return true;
        }

        definition = null;
        return false;
    }

    public long DrainEvents(long sinceSeq, List<SimulationEvent> into)
    {
        var buffer = _engine.World.Events;
        var events = buffer.Items;
        if (events.Count == 0)
        {
            return sinceSeq;
        }

        // Events trimmed before we got to them: skip the gap rather than
        // replaying what survived — GameHistoryLog has no dedup, so a replay
        // duplicates colony history. With verbose trace on, the 2048-entry ring
        // wraps roughly every 11 ticks, which one MAX-speed frame easily covers.
        if (sinceSeq < buffer.LowestSeq - 1)
        {
            sinceSeq = buffer.LowestSeq - 1;
        }

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Seq > sinceSeq)
            {
                into.Add(e);
            }
        }

        return buffer.HighestSeq;
    }

    public void WriteSaveNow() => SaveGame.Write(_engine.World, SpeedMultiplier);

    public void Shutdown()
    {
    }
}

}
