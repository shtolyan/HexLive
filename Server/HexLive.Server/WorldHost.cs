using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using HexLive.Server.Llm;

namespace HexLive.Server
{

/// <summary>
/// The world, ticking on its own clock, whether or not anybody is watching.
/// <para>
/// This is the whole point of the exercise: <c>SimulationClock</c> never asks
/// what time it is — whoever calls <c>Step()</c> decides the pace — so hosting
/// the colony outside Unity needs no change to the simulation at all. What lives
/// here is only the three things Unity used to provide: a clock to drive the
/// tick, a place to put the save, and a lock so a connecting viewer never reads
/// a half-stepped world.
/// </para>
/// </summary>
public sealed partial class WorldHost : IDisposable
{
    /// <summary>
    /// The one lock in the process. The tick thread holds it while stepping;
    /// connection threads hold it while serialising a frame. Snapshots are taken
    /// mid-lock precisely so a viewer can never observe a world halfway through
    /// a system pass.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// §144.6. Зеркало хроники для MCP. Заводится только при <c>--mcp</c>: дверь,
    /// которой не просили, не должна стоить ни байта.
    /// </summary>
    private Mcp.McpEventLog? _mcpEvents;

    private long _mcpDrainSeq;

    private readonly SimulationEngine _engine;
    private readonly SimulationClock _clock;
    private readonly SimulationSettings _settings;
    private readonly string _savePath;
    private readonly LlmControlSystem? _llmControlSystem;
    private readonly LlmProviderDiagnostics? _llmProviderDiagnostics;

    private WorldSnapshot? _snapshot;
    private int _snapshotTick = -1;

    private long _ticksRun;
    private double _busyMs;

    /// <summary>
    /// §83: tick-completion broadcast. Viewer send loops await this instead of
    /// polling <see cref="Tick"/> on a timer — the old 62.5 ms poll added up to
    /// a quarter-tick of send-time jitter to every frame, which the client's
    /// playhead then had to buffer against. Swapped whole on every step;
    /// RunContinuationsAsynchronously so a completing tick never runs viewer
    /// serialisation on the tick thread.
    /// </summary>
    private TaskCompletionSource<bool> _tickSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorldHost(int seed, GameMode mode, string savePath, string simDataPath, bool verboseTrace,
        bool includeDebugDetails = false, LlmHostOptions? llmOptions = null,
        string? companionProfile = null, WorldCreationConfig? creationConfig = null, string worldId = "")
    {
        // The codec flag alone is not enough: the EXPORTER only fills the per-NPC
        // debug lists (relationships, goal scores, known objects) behind this
        // static. Without it, --debug-details dutifully shipped empty sections.
        WorldSnapshotExporter.IncludeDebugDetails = includeDebugDetails;

        // Spec §59.3, and it is not optional: without the exported catalogs the
        // server would run on code defaults and drift silently away from the
        // tuned game it is supposed to be hosting. Require() throws.
        SimDataFile.Require(simDataPath);

        // Off by default, matching what a player build does.
        //
        // Do not expect much from it: MEASURED at 177.8 events/tick on, 174.4
        // off — 2%. The flag is only checked at a handful of call sites (the
        // engine's TickStart among them); the overwhelming majority of
        // Trace.Emit calls are unconditional, so the knob's own doc comment
        // ("per-tick chatter … nothing consumes them") oversells it.
        //
        // That matters here because on a server those events are not log noise,
        // they are bandwidth: ~174/tick × 4 ticks/s ≈ 66 KB/s to every viewer,
        // a third of the stream. The real lever would be filtering by event TYPE
        // to the set consumers actually use (sound, speech, colony history) —
        // worth doing, but it is a change to what the simulation considers a
        // gameplay-visible event, not a server-side tweak.
        SimTrace.Verbose = verboseTrace;

        _savePath = savePath;

        WorldId = worldId;
        if (!string.IsNullOrEmpty(worldId))
        {
            var header = ServerSaveHeader.ReadIfPresent(savePath);
            if (header is { } saved && (saved.Seed != seed || saved.Mode != mode))
                throw new InvalidDataException("World metadata and save header disagree; refusing to overwrite the save.");
        }
        var definition = creationConfig == null ? PrototypeWorldDefinitionFactory.Create(seed, mode) : WorldCreation.Definition(creationConfig);
        uint topologyChecksum = 0;
        var world = new WorldStateFactory().Create(definition, topology =>
            topologyChecksum = HexLive.Simulation.Wire.TopologyChecksum.Compute(topology));
        TopologyChecksum = topologyChecksum;

        _settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
        };

        _clock = new SimulationClock();
        _clock.Resume();

        _engine = new SimulationEngine(world, _settings, _clock);
        if (llmOptions is { Enabled: true })
        {
            _llmProviderDiagnostics = new LlmProviderDiagnostics(Console.Error.WriteLine);
            _llmControlSystem = new LlmControlSystem(
                enabled: true,
                eligibleNpcIds: llmOptions.SelectedNpcIds,
                provider: new LlmHttpControlProvider(llmOptions, _llmProviderDiagnostics),
                decisionCooldownTicks: SpecLlmControl.DecisionCooldownTicks,
                requestTimeoutTicks: SpecLlmControl.RequestTimeoutTicks,
                maxInFlightRequests: SpecLlmControl.MaxInFlightRequests);
            Console.WriteLine(
                $"[llm] enabled for {llmOptions.SelectedNpcIds.Count} NPC(s), HTTP provider configured");
        }

        SimulationSystemRegistry.RegisterDefaults(_engine, _llmControlSystem);

        // §30.14: самописец едет вместе с остальной отладкой — за тем же флагом,
        // что и per-NPC дампы в снапшоте. По проводу он пока не ездит: смотреть
        // его можно на самом сервере, из подключённой Unity — нет.
        if (includeDebugDetails)
        {
            world.FlightRecorder = HexLive.Simulation.Runtime.FlightRecorder.ForBehavior();
        }

        // Definition ids travel as indices into a table derived from the content
        // catalog. The client derives the same one from the simdata we ship in the
        // handshake, so nothing is negotiated — but it must exist before the first
        // frame is encoded.
        DefinitionIdTable.Build(world.Content);

        // §162: fingerprint captured by Create's topology callback before scenario buildings
        // change tile flags, and before restoring a save. Matches the viewer's CreateTopology.

        // The blob is a delta from worldgen: it is applied onto a world already
        // rebuilt from the SAME seed (static topology is regenerated, never
        // stored). So restore has to happen after Create, not instead of it.
        TryRestore();


        if (creationConfig == null && !string.IsNullOrWhiteSpace(companionProfile))
        {
            foreach (var profile in companionProfile.Split(','))
            {
                var spawned = CharacterPresetRegistry.EnsureSpawned(
                    _engine.World, profile, out var presetNpcId);
                Console.WriteLine(spawned
                    ? $"[preset] {profile} spawned as NPC{presetNpcId}"
                    : $"[preset] {profile} restored as NPC{presetNpcId}");
            }
        }
    }

    public string WorldId { get; }
    public string CreationConfigText => Read(w => WorldCreationCodec.Encode(w.CreationConfig));

    public string? DrainLlmProviderFailureSummary() =>
        _llmProviderDiagnostics?.DrainSummary();

    /// <summary>
    /// §149.4: кладёт в мир СОЮЗ назначений всех игроков — единственное, из
    /// чего симуляция узнаёт «эта девушка в руках игрока». Реестр назначений
    /// живёт на сервере, симуляция его не сохраняет и по проводу не шлёт;
    /// набор переустанавливается целиком, а не по одному игроку, потому что
    /// это состояние, а не дельта: отключившегося надо уметь и убрать.
    /// Замок тот же, что держит тик, — иначе граница прав читалась бы на
    /// полушаге.
    /// </summary>
    public void SetPlayerControlledNpcs(IReadOnlyCollection<int> npcIds)
    {
        if (npcIds is null)
        {
            throw new ArgumentNullException(nameof(npcIds));
        }

        lock (_gate)
        {
            var owned = _engine.World.PlayerControlledNpcs;
            owned.Clear();
            foreach (var npcId in npcIds)
            {
                owned.Add(npcId);
            }
        }
    }

    /// <summary>
    /// Читает мир под тем же замком, что держит тик, — единственный законный
    /// способ ответить на вопрос «что там сейчас» из чужого потока.
    /// <para>
    /// ⚠️ Обратный вызов обязан ВЫЧИТАТЬ и вернуть готовое значение, а не
    /// вынести наружу ссылку на <c>WorldState</c> или на что-либо внутри него:
    /// за пределами замка это уже полушагнувший мир. Ровно та же дисциплина,
    /// что у снапшота (§83), просто без кодека — MCP-инструменту нужно имя
    /// колонистки, а не 37 КБ кадра.
    /// </para>
    /// </summary>
    public T Read<T>(Func<WorldState, T> read)
    {
        if (read is null)
        {
            throw new ArgumentNullException(nameof(read));
        }

        lock (_gate)
        {
            return read(_engine.World);
        }
    }

    /// <summary>
    /// Thread-safe ingress for an authenticated host-side controller. The
    /// ordinary manual-command executor remains the sole validation boundary;
    /// this method only serializes it with ticks and snapshot readers and makes
    /// the typed accepted/rejected admission observable to the caller.
    /// <para>
    /// Ownership, authentication and lease policy deliberately do not live
    /// here. A future MCP adapter must establish those before calling this seam.
    /// </para>
    /// </summary>
    public ManualCommandAdmission SubmitManualCommand(ISimulationCommand command)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        lock (_gate)
        {
            var admission = _engine.ApplyManualCommand(command);

            // A command can mutate the world without advancing its tick. A
            // snapshot already cached for that tick is therefore stale even
            // though its cache key still matches.
            _snapshotTick = -1;

            // ⭐ Not redundant with the drain in the tick loop. A rejection is
            // emitted HERE, between ticks — and an operator-paused world never
            // reaches Step(), so draining only there would lose exactly the
            // answer the agent is waiting for, and only in a paused world.
            DrainMcpEvents();
            return admission;
        }
    }

    /// <summary>Only under <c>_gate</c>. Cheap no-op when MCP is off.</summary>
    private void DrainMcpEvents()
    {
        if (_mcpEvents is { } log)
        {
            _mcpDrainSeq = log.Drain(_engine.World.Events, _mcpDrainSeq);
        }
    }

    /// <summary>
    /// Turns on the §144.6 chronicle mirror. Called from the composition root
    /// when <c>--mcp</c> is present.
    /// </summary>
    // §161: callers must authenticate through AdminCommandBus before entering here.
    internal AdminCommandResult SubmitAdminCommand(AdminCommand command)
    {
        lock (_gate)
        {
            var result = AdminWorldCommands.Execute(_engine.World, command);
            _snapshotTick = -1;
            DrainMcpEvents();
            return result;
        }
    }

    public void EnableMcpEventLog()
    {
        lock (_gate)
        {
            if (_mcpEvents != null)
            {
                return;
            }

            _mcpEvents = new Mcp.McpEventLog();

            // Start at the present, not at whatever the ring happens to hold: an
            // agent connecting on tick 50 000 wants what happens next, and the
            // ring's three seconds of backlog are events it cannot place anyway.
            _mcpDrainSeq = _engine.World.Events.HighestSeq;
        }
    }

    public int Seed
    {
        get
        {
            lock (_gate)
            {
                return _engine.World.Seed;
            }
        }
    }

    // §146: worldgen ran with (seed, mode) — the handshake carries both.
    public GameMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _engine.World.Mode;
            }
        }
    }

    public float TickDeltaTime => _settings.TickDeltaTime;

    /// <summary>
    /// Fingerprint of the static geometry, for the handshake. Computed in the
    /// constructor — topology never changes after worldgen, and an eager plain
    /// field cannot be torn by concurrent first readers the way a lazily-filled
    /// nullable could (a post-swap host's first readers are viewer threads).
    /// </summary>
    public uint TopologyChecksum { get; }

    public int Tick
    {
        get
        {
            lock (_gate)
            {
                return _engine.World.Tick;
            }
        }
    }

    public bool IsPaused => _clock.IsPaused;

    public float SpeedMultiplier => _clock.SpeedMultiplier;

    public long TicksRun => Interlocked.Read(ref _ticksRun);

    /// <summary>Colonists still standing, for the status line.</summary>
    public (int alive, int total, int objects) Census()
    {
        lock (_gate)
        {
            var alive = 0;
            foreach (var npc in _engine.World.Entities.Npcs.Values)
            {
                if (npc.Health > 0f)
                {
                    alive++;
                }
            }

            return (alive, _engine.World.Entities.Npcs.Count, _engine.World.Entities.Objects.Count);
        }
    }

    public long HighestEventSeq
    {
        get
        {
            lock (_gate)
            {
                return _engine.World.Events.HighestSeq;
            }
        }
    }

    /// <summary>Average cost of one <c>Step()</c>, excluding lock wait.</summary>
    public double AverageTickMs
    {
        get
        {
            var ticks = TicksRun;
            return ticks > 0 ? _busyMs / ticks : 0.0;
        }
    }

    private double _recentTickMs;
    /// <summary>§166: exponential moving average of recent step costs (~5 seconds at 4 Hz).</summary>
    public double RecentTickMs => System.Threading.Volatile.Read(ref _recentTickMs);

    /// <summary>
    /// Ticks actually produced per real second, measured. The honest health
    /// check: the world is meant to run at 1/TickDeltaTime, and anything else
    /// means it is either behind or catching up.
    /// </summary>
    public double MeasuredTicksPerSecond
    {
        get
        {
            var elapsed = _uptime.Elapsed.TotalSeconds;
            return elapsed > 0.5 ? TicksRun / elapsed : 0.0;
        }
    }

    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    public TimeSpan Uptime => _uptime.Elapsed;

    // ── the tick loop ─────────────────────────────────────────────────────

    /// <summary>
    /// Ceiling on operator fast-forward. At 200x the world runs 800 ticks a
    /// second — well past what one core sustains at ~8 ms a tick, so asking for
    /// more just means the loop runs flat out and reports a rate below what was
    /// requested. Better to cap honestly than to pretend.
    /// </summary>
    private const float MaxSpeedMultiplier = 200f;

    /// <summary>
    /// Steps the world on a fixed wall-clock cadence until cancelled. Real time
    /// is the master: a slow tick eats into the next sleep rather than pushing
    /// the world behind, and a long stall is dropped rather than repaid as a
    /// burst (the colony is not a physics sim; catching up 10 000 ticks at once
    /// would just stutter every viewer).
    /// </summary>
    public void Run(CancellationToken cancel)
    {
        var next = Stopwatch.GetTimestamp();
        var watch = new Stopwatch();

        while (!cancel.IsCancellationRequested)
        {
            // Recomputed every iteration: the operator can change speed from the
            // admin panel mid-run, and a period captured once outside the loop
            // would make SpeedMultiplier a number nothing acts on.
            var speed = _clock.SpeedMultiplier;
            if (float.IsPositiveInfinity(speed) || speed > MaxSpeedMultiplier)
            {
                speed = MaxSpeedMultiplier;
            }
            else if (speed < 0.1f)
            {
                speed = 0.1f;
            }

            var period = TimeSpan.FromSeconds(_settings.TickDeltaTime / speed);

            // Time the STEP, not the wait for the lock. Including the wait made
            // the average read ~10x worse than the work actually is, because a
            // connected viewer holds the gate while it serialises a frame —
            // which says something about contention but nothing at all about
            // what a tick costs, and that is the number capacity is planned on.
            var stepped = false;
            lock (_gate)
            {
                if (!_clock.IsPaused && !_engine.World.Completed)
                {
                    watch.Restart();
                    _engine.Step();
                    watch.Stop();
                    _busyMs += watch.Elapsed.TotalMilliseconds;
                    System.Threading.Volatile.Write(ref _recentTickMs,
                        TicksRun == 0 ? watch.Elapsed.TotalMilliseconds :
                        _recentTickMs * 0.9 + watch.Elapsed.TotalMilliseconds * 0.1);
                    Interlocked.Increment(ref _ticksRun);
                    stepped = true;

                    // Copy out before the ring buries it: at ~174 events/tick the
                    // 2048-entry buffer holds under twelve ticks.
                    DrainMcpEvents();
                }
            }

            if (stepped)
            {
                // Outside the gate: waking every viewer must not extend the
                // window in which the world is locked.
                var signal = Interlocked.Exchange(ref _tickSignal,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                signal.TrySetResult(true);
            }

            next += (long)(period.TotalSeconds * Stopwatch.Frequency);
            var remaining = (next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            if (remaining > 0)
            {
                cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining));
                continue;
            }

            // Behind schedule. Falling further behind every tick would be worse
            // than dropping the debt, so reset the phase and carry on at 1x.
            if (remaining < -1.0)
            {
                next = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// Completes as soon as a tick finishes (or promptly if <see cref="Tick"/>
    /// already differs from <paramref name="lastSeenTick"/>), and no later than
    /// <paramref name="maxWait"/> — the timeout keeps a paused world's viewers
    /// checking for clock changes at the old poll cadence. The signal is read
    /// BEFORE the tick comparison so a step landing between the two cannot be
    /// missed: that step completed this very signal.
    /// </summary>
    public async Task WaitForNextTickAsync(int lastSeenTick, TimeSpan maxWait, CancellationToken cancel)
    {
        var signal = Volatile.Read(ref _tickSignal).Task;
        if (Tick != lastSeenTick)
        {
            return;
        }

        await Task.WhenAny(signal, Task.Delay(maxWait, cancel)).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
    }

    // ── reading ───────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises the current tick under the lock. Reuses one snapshot instance,
    /// like the Unity runner does, so a 4 Hz world does not allocate a fresh
    /// object graph every frame.
    /// </summary>
    public byte[] EncodeSnapshot(bool includeDebugDetails)
    {
        lock (_gate)
        {
            RefreshSnapshot();

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WorldSnapshotCodec.Write(_snapshot, writer, includeDebugDetails);
            writer.Flush();
            return stream.ToArray();
        }
    }

    /// <summary>
    /// Encodes for ONE viewer, using its own baseline: a delta when the viewer is
    /// in step, a full frame when it is not (first connect, reconnect, or after it
    /// asked for one).
    /// <para>
    /// The encoder belongs to the connection, not to the world, and runs INSIDE
    /// the lock — a baseline built from a half-stepped world would poison every
    /// later delta. Per-viewer baselines cost ~35 KB each, which is nothing for a
    /// handful of viewers and buys the property that one slow client can never
    /// affect what another one receives.
    /// </para>
    /// </summary>
    public byte[] EncodeFor(SnapshotDeltaEncoder encoder, bool includeDebugDetails, out bool isKeyframe)
    {
        lock (_gate)
        {
            RefreshSnapshot();

            // A state the delta format cannot describe (a rebuilt death list)
            // resets the baseline here, turning this frame into a keyframe.
            encoder.EnsureBaselineValid(_snapshot);

            isKeyframe = encoder.BaselineTick < 0;
            if (!isKeyframe)
            {
                return encoder.Encode(_snapshot, includeDebugDetails);
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WorldSnapshotCodec.Write(_snapshot, writer, includeDebugDetails);
            writer.Flush();

            // Arm the baseline at the tick just sent, so the next frame can be a
            // delta against exactly this.
            encoder.Encode(_snapshot, includeDebugDetails);
            return stream.ToArray();
        }
    }

    private void RefreshSnapshot()
    {
        if (_snapshot is null || _snapshotTick != _engine.World.Tick)
        {
            _snapshot = WorldSnapshotExporter.Export(_engine.World, _snapshot);
            _snapshotTick = _engine.World.Tick;
        }
    }

    /// <summary>
    /// Encodes events newer than <paramref name="sinceSeq"/>, and reports the
    /// oldest seq still held so the receiver can tell a gap from an empty batch.
    /// Returns null when there is nothing new.
    /// </summary>
    public byte[]? EncodeEvents(long sinceSeq, out long watermark)
    {
        var batch = new List<SimulationEvent>();
        long oldest;

        lock (_gate)
        {
            var buffer = _engine.World.Events;
            oldest = buffer.LowestSeq;
            watermark = buffer.HighestSeq;
            if (watermark <= sinceSeq)
            {
                return null;
            }

            var items = buffer.Items;
            for (var i = 0; i < items.Count; i++)
            {
                // Only events a player could see, hear or read about — plus the
                // §145.3 order replies (ManualOrderInterrupted/ManualControlExpired/
                // GroupOrderResult), which must reach the viewer's toasts but are
                // NOT chronicle. The other ~97% by volume is the AI thinking out
                // loud — GoalScored, PerceivedObject, PlanCandidate — which
                // nothing on the client consumes and which cost ~66 KB/s per
                // viewer to ship.
                //
                // Note the watermark still advances past the filtered ones (it is
                // set from buffer.HighestSeq above), so a viewer never asks for
                // them again and never mistakes the gap for a dropped event.
                if (items[i].Seq > sinceSeq && GameEventTypes.IsWireRelevant(items[i]))
                {
                    batch.Add(items[i]);
                }
            }
        }

        // Everything new was chatter. Say nothing rather than sending an empty frame.
        if (batch.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        SimulationEventCodec.Write(batch, oldest, writer);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// The same selection as <see cref="EncodeEvents"/>, handed back as plain
    /// records instead of the viewer's binary frame (§144.6).
    /// <para>
    /// It is a separate method rather than a mode flag on <see cref="EncodeEvents"/>
    /// because the two differ in what silence means. The viewer is a stream: an
    /// empty batch is nothing to send, so it returns null and says nothing. A
    /// pulling agent asks a question and must always get an answer — "nothing
    /// happened, and here is your new watermark" is a result, not a non-event.
    /// Folding that into the viewer path would have made it return empty frames.
    /// </para>
    /// <para>
    /// What the two MUST share is the watermark rule, and they do: it comes from
    /// <c>HighestSeq</c> and therefore advances PAST the filtered-out chatter. A
    /// consumer that advanced only to the last event it was shown would re-scan
    /// the same ~97% of AI noise on every call, forever.
    /// </para>
    /// </summary>
    public EventBatch ReadEvents(long? sinceSeq, int limit, int? entityId)
    {
        lock (_gate)
        {
            if (_mcpEvents is not { } log)
            {
                return new EventBatch(
                    Array.Empty<EventRecord>(), 0, 0, false, false, false);
            }

            // Drain before answering: between the last tick and this call the
            // world may have moved, and the agent asked "what happened", not
            // "what had happened as of the previous tick boundary".
            DrainMcpEvents();
            return log.Read(sinceSeq, limit, entityId);
        }
    }

    /// <summary>Epoch of the current world instance; changes on restore and restart.</summary>
    public string McpSessionEpoch => _mcpEvents?.SessionEpoch ?? string.Empty;

    // ── clock commands ────────────────────────────────────────────────────

    /// <summary>
    /// Stopping the world is OPERATOR-ONLY, and there is deliberately no viewer
    /// counterpart — unlike speed, which merely clamps.
    /// <para>
    /// Pause is the biggest lever on a shared world there is: it does not run
    /// the colony wrong, it stops it, for everyone watching, until someone
    /// notices. A viewer had it, and it worked — through the speed bar's button
    /// AND, silently, through opening the in-game menu, which pauses before it
    /// draws. §83.4 says a viewer is a watcher, not a participant; that has to
    /// be true of the clock first of all.
    /// </para>
    /// </summary>
    public void PauseAsOperator() => _clock.Pause();

    public void ResumeAsOperator() => _clock.Resume();

    /// <summary>
    /// A VIEWER's speed request — clamped to 1x. A hosted world is shared and
    /// persistent: one viewer winding it to 50x would run days past everyone
    /// else watching and pour 200 frames a second down every socket. The client
    /// greys the fast-forward buttons out, but the rule belongs HERE — a server
    /// does not rely on the good manners of whatever connected to it.
    /// </summary>
    public void SetSpeed(float multiplier) => _clock.SetSpeed(Math.Min(1f, Math.Max(0.1f, multiplier)));

    /// <summary>
    /// The OPERATOR's speed request, from the password-protected panel. Not
    /// clamped: it is their world, and winding it forward deliberately is a
    /// legitimate thing to want. The asymmetry with <see cref="SetSpeed"/> is
    /// the reason the panel is behind a password.
    /// </summary>
    public void SetSpeedAsOperator(float multiplier) =>
        _clock.SetSpeed(Math.Min(200f, Math.Max(0.1f, multiplier)));

    // ── persistence ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes the full model blob. Same serializer the game uses, pointed at a
    /// MemoryStream instead of a file it opens itself — it takes a BinaryWriter,
    /// so nothing about it had to change to live here.
    /// <para>
    /// Written to a temp file and moved into place: a crash mid-write must not
    /// leave a truncated save where the only copy of the colony used to be.
    /// </para>
    /// </summary>
    public void Save()
    {
        byte[] blob;
        int tick;
        lock (_gate)
        {
            tick = _engine.World.Tick;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WorldSaveSerializer.Write(_engine.World, writer);
            writer.Flush();
            blob = stream.ToArray();
            // §161: serialize writes with world mutations; an older autosave must
            // never replace a just-acknowledged admin save or share its temp file.

            var directory = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _savePath + ".tmp";
            using (var file = File.Create(temp))
            using (var saveWriter = new BinaryWriter(file))
            {
                saveWriter.Write(SaveMagic);
                saveWriter.Write(SaveVersion);
                saveWriter.Write(Seed);
                saveWriter.Write((int)Mode); // §146.2 (v2) — beside the seed, same rule
                saveWriter.Write(tick);
                saveWriter.Write(blob.Length);
                saveWriter.Write(blob);
            }

            File.Move(temp, _savePath, overwrite: true);
        }
    }

    private const int SaveMagic = unchecked((int)0x48584C53); // "HXLS" — server save
    // v2 (§146.2): GameMode ordinal after the seed. v1 saves are always Feud.
    private const int SaveVersion = 2;

    private void TryRestore()
    {
        if (!File.Exists(_savePath))
        {
            Console.WriteLine($"[world] no save at {_savePath} — starting a fresh colony");
            return;
        }

        try
        {
            using var file = File.OpenRead(_savePath);
            using var reader = new BinaryReader(file);
            if (reader.ReadInt32() != SaveMagic)
            {
                Console.WriteLine("[world] save header not recognised — starting fresh");
                return;
            }

            var headerVersion = reader.ReadInt32();
            if (headerVersion != 1 && headerVersion != SaveVersion)
            {
                Console.WriteLine("[world] save header not recognised — starting fresh");
                return;
            }

            var savedSeed = reader.ReadInt32();
            // §146.2: v1 predates modes and is always Feud.
            var savedMode = headerVersion >= 2 ? (GameMode)reader.ReadInt32() : GameMode.Feud;
            if (savedMode != _engine.World.Mode)
            {
                Console.WriteLine(
                    $"[world] save is a {savedMode} world, this host runs {_engine.World.Mode} — starting fresh");
                return;
            }

            var savedTick = reader.ReadInt32();
            if (savedSeed != _engine.World.Seed)
            {
                // The blob only makes sense on top of ITS OWN worldgen. Applying
                // it to a different seed would put objects on tiles that do not
                // exist, so refuse rather than corrupt.
                Console.WriteLine(
                    $"[world] save is for seed {savedSeed}, this host runs {_engine.World.Seed} — starting fresh");
                return;
            }

            var length = reader.ReadInt32();
            var blob = reader.ReadBytes(length);

            using var blobStream = new MemoryStream(blob);
            using var blobReader = new BinaryReader(blobStream);
            WorldSaveSerializer.Read(_engine.World, blobReader);

            // The mirror now holds the chronicle of a world that no longer
            // happened — «дерево срублено» about a tree the restored world still
            // has standing. Drop it and re-anchor; the epoch change tells any
            // attached agent its picture is void.
            _mcpEvents?.Reset();
            _mcpDrainSeq = _engine.World.Events.HighestSeq;

            Console.WriteLine($"[world] restored seed {savedSeed} at tick {savedTick}");
        }
        catch (Exception ex)
        {
            // ⭐ §156: РОВНО НАОБОРОТ, чем раньше, и это не осторожность, а
            // единственный способ не съесть чужой мир. Здесь мы уже знаем, что
            // файл НАШ: магия, версия заголовка, сид и режим сошлись, — а
            // разобрать содержимое не смогли. Раньше такой случай начинал
            // колонию заново, и первый же автосейв затирал живой world.sav
            // необратимо; блоб v66 оборвал совместимость, так что «не смогли
            // разобрать» стало обычным делом при обновлении сервера, а не
            // экзотикой. Контракт CLAUDE.md: неопознанный сейв обязан
            // ОСТАНОВИТЬ старт, а не переписать мир. Оператор чинит это
            // осознанно — откатом версии или переносом файла.
            throw new InvalidOperationException(
                $"Сейв {_savePath} принадлежит этому миру (сид и режим совпали), " +
                $"но не читается: {ex.Message}. Запуск остановлен, чтобы автосейв " +
                "не переписал его новым миром. Верните прежнюю версию сервера или " +
                "уберите файл вручную, если мир действительно надо начать заново.",
                ex);
        }
    }
    public void Dispose() => _llmControlSystem?.Dispose();
}

}
