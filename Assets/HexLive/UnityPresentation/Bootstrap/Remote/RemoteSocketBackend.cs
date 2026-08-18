#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using UnityEngine;
using EntityId = HexLive.Simulation.Common.EntityId;
// `using System.Diagnostics` above (Stopwatch) collides with UnityEngine over
// the name `Debug`. Aliased once here rather than qualifying every call site:
// in a MonoBehaviour-adjacent file Debug always means Unity's.
using Debug = UnityEngine.Debug;

namespace HexLive.UnityPresentation.Bootstrap.Remote
{

/// <summary>
/// The world lives on a server; this process watches it.
/// <para>
/// Two threads, and the split between them is the whole design. A background
/// task owns the socket and does nothing but receive bytes into a queue — it
/// never touches a <see cref="WorldSnapshot"/>, a Unity object, or anything the
/// main thread reads. The main thread drains that queue in <see cref="Tick"/>,
/// decodes only the frames it is going to present, and hands the renderer the
/// same reused snapshot instance the local backend does. Decoding on the main
/// thread is deliberate: a snapshot shared across threads is a data race waiting
/// for a bad frame, and decoding is cheap next to rendering.
/// </para>
/// <para>
/// The ~14 000 junctions never cross the wire. On connect this rebuilds the
/// world locally from the seed the server sent — the same trick loading a save
/// already relies on — and verifies it against the server's topology checksum
/// before showing anything.
/// </para>
/// <para>
/// <b>Failure is separated into two kinds, because they need opposite
/// responses.</b> A dropped socket is expected and temporary: retry with
/// backoff, keep the last world on screen, say "reconnecting". A topology or
/// protocol mismatch means the two ends are different builds — retrying would
/// flap forever, so it stops and says why.
/// </para>
/// </summary>
public sealed class RemoteSocketBackend : ISimulationBackend
{
    // Retry schedule. Starts fast (most drops are a blip) and backs off so a
    // server that is genuinely down is not hammered.
    private static readonly float[] RetryDelaysSeconds = { 0.5f, 1f, 2f, 4f, 8f, 15f };

    /// <summary>
    /// §145.3: сколько подряд неудачных заходов терпеть, прежде чем честно
    /// сдаться (LinkState.Failed → модальный диалог и возврат в меню).
    /// Бесконечный молчаливый реконнект игрок читает как «игра зависла»:
    /// сервер, погасший вместе с электричеством, от ожидания не вернётся.
    /// Восемь циклов по расписанию выше — около 45 секунд: любой рестарт
    /// сервера успевает, любое настоящее «его больше нет» — не мучает.
    /// </summary>
    private const int MaxConnectAttempts = 8;

    /// <summary>Ping cadence. Also the liveness probe — see <see cref="StallAfterSeconds"/>.</summary>
    private const double PingIntervalSeconds = 2.0;

    /// <summary>
    /// Silence longer than this (while the world is NOT paused) counts as
    /// stalled. Generous next to the 4 Hz frame rate so ordinary jitter never
    /// trips it.
    /// </summary>
    private const double StallAfterSeconds = 3.0;

    /// <summary>
    /// An unanswered ping for this long means the socket is half-open — still
    /// "Open" as far as the OS is concerned, but nothing crosses it. Tear it
    /// down and reconnect rather than waiting forever on a dead pipe.
    /// </summary>
    private const double DeadAfterSeconds = 10.0;

    private readonly string _url;
    private readonly string? _controlToken;
    private readonly string _clientId;
    private readonly CancellationTokenSource _shutdown = new();

    private ClientWebSocket? _socket;

    // §121.9: корреляция приказ→вердикт и локально синтезированные события
    // отказа (см. Dispatch: CommandResult). Кладутся на сокет-потоке,
    // дренируются на главном — поэтому под _inbox.
    private int _commandSeq;
    private readonly List<SimulationEvent> _verdictEvents = new();

    // Written by the socket thread, read by the main thread. Bytes only.
    private readonly object _inbox = new();
    private readonly Queue<(bool keyframe, byte[] bytes)> _snapshotFrames = new();

    /// <summary>
    /// The tick of every frame queued since the main thread last looked, in
    /// arrival order. The clock is told about arrivals BEFORE it is asked what to
    /// present — it will not present anything at all until it has seen one (see
    /// <c>RemoteTickClock._started</c>), and it measures the world's real rate
    /// from these, not from the speed the server claims.
    /// </summary>
    private readonly List<int> _arrivedTicks = new();
    private readonly List<byte[]> _eventFrames = new();
    private Handshake? _handshake;
    private bool _handshakeIsNew;
    private LinkState _state = LinkState.Connecting;
    private string? _message;
    private int _pingMilliseconds = -1;
    private int _attempt;

    private readonly Stopwatch _sinceLastFrame = Stopwatch.StartNew();

    private readonly RemoteTickClock _clock = new();
    private readonly WorldSnapshot _snapshot = new();
    private readonly List<SimulationEvent> _pendingEvents = new();

    private WorldState? _localWorld;
    private bool _ready;

    /// <param name="controlToken">§121.9: токен игрока (<c>--control</c> на
    /// сервере). Null — прежний анонимный зритель.</param>
    /// <param name="clientId">Стабильный id клиента: лиз переживает реконнект,
    /// потому что owner <c>ws:&lt;clientId&gt;</c> не меняется.</param>
    public RemoteSocketBackend(string url, string? controlToken = null, string? clientId = null)
    {
        _url = url;
        _controlToken = string.IsNullOrEmpty(controlToken) ? null : controlToken;
        _clientId = string.IsNullOrEmpty(clientId)
            ? Guid.NewGuid().ToString("N")
            : clientId!;
    }

    public bool IsReady => _ready;

    public bool IsCompleted => _ready && _snapshot.Completed;

    public int Seed => _handshake?.Seed ?? 0;

    public int CurrentTick => _snapshot.Tick;

    public float TickAlpha => _clock.TickAlpha;

    public bool IsPaused => _clock.IsPaused;

    public float SpeedMultiplier => _clock.SpeedMultiplier;

    // We do not own this world. Both of these being false is what makes the
    // debug body controls and the save button go quiet instead of throwing.
    public bool SupportsDirectWorldMutation => false;

    public bool SupportsClientSave => false;

    // §121.9: приказы ЕЗДЯТ — если сервер принял токен игрока
    // (Handshake.ControlEnabled). Кадр появился здесь, и ни одна кнопка
    // интерфейса об этом не узнала — ровно как обещал §121.4.
    public bool SupportsNpcCommands
    {
        get
        {
            lock (_inbox)
            {
                return _handshake?.ControlEnabled == true;
            }
        }
    }

    public bool TryGetCraftingOptions(EntityId npc, List<CraftRecipeOption> into)
    {
        // Рецепты пока не ездят по проводу: вкладка «Крафт» на удалёнке
        // пуста. Приказ CraftItemCommand при этом валиден — MCP-агент шлёт
        // его по каталогу рецептов; окно UI догонит отдельным кадром.
        into.Clear();
        return false;
    }

    public void EnqueueCommand(ISimulationCommand command)
    {
        if (!SupportsNpcCommands)
        {
            return;
        }

        var correlationId = Interlocked.Increment(ref _commandSeq);
        Send(Frame.NpcCommand(correlationId, command));
    }

    public SimulationLink Link
    {
        get
        {
            lock (_inbox)
            {
                return new SimulationLink(_state, _pingMilliseconds, _message);
            }
        }
    }

    public void Connect() => _ = Task.Run(() => ConnectLoopAsync(_shutdown.Token));

    // ── socket thread ─────────────────────────────────────────────────────

    /// <summary>
    /// Connect, pump, and on an unclean exit come back and try again. The whole
    /// reconnect policy lives here so the main thread never has to think about
    /// sockets.
    /// </summary>
    private async Task ConnectLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            SetState(_attempt == 0 ? LinkState.Connecting : LinkState.Reconnecting, null);

            try
            {
                using var socket = new ClientWebSocket();
                // §121.9: токен и стабильный id клиента едут заголовками
                // HTTP-upgrade — не в query string, где они текли бы в логи.
                if (_controlToken is not null)
                {
                    socket.Options.SetRequestHeader(
                        "Authorization", "Bearer " + _controlToken);
                    socket.Options.SetRequestHeader("X-HexLive-Client-Id", _clientId);
                }

                _socket = socket;
                await socket.ConnectAsync(new Uri(_url), cancel).ConfigureAwait(false);

                _attempt = 0;
                _sinceLastFrame.Restart();
                await PumpAsync(socket, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (UriFormatException ex)
            {
                // A malformed address will never become valid by retrying.
                Fail($"Bad server address '{_url}': {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                SetState(LinkState.Reconnecting, ex.Message);
            }
            finally
            {
                _socket = null;
            }

            lock (_inbox)
            {
                if (_state == LinkState.Failed)
                {
                    return;
                }
            }

            var delay = RetryDelaysSeconds[Math.Min(_attempt, RetryDelaysSeconds.Length - 1)];
            _attempt++;
            if (_attempt >= MaxConnectAttempts)
            {
                Fail($"No connection after {MaxConnectAttempts} attempts.");
                return;
            }

            SetState(LinkState.Reconnecting, null);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var chunk = new byte[64 * 1024];
        using var message = new MemoryStream();

        var pinger = Task.Run(() => PingLoopAsync(socket, cancel), cancel);

        while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            while (true)
            {
                // NOTE: cancelling a ReceiveAsync ABORTS the socket (it goes to
                // Aborted and every later call throws). So the only token here
                // is shutdown — never a per-receive timeout. Liveness is the
                // ping loop's job instead.
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancel)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await pinger.ConfigureAwait(false);
                    return;
                }

                message.Write(chunk, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            var bytes = message.ToArray();
            if (bytes.Length < 1)
            {
                continue;
            }

            var payload = new byte[bytes.Length - 1];
            Buffer.BlockCopy(bytes, 1, payload, 0, payload.Length);
            Dispatch((FrameKind)bytes[0], payload);
        }

        await pinger.ConfigureAwait(false);
    }

    private void Dispatch(FrameKind kind, byte[] payload)
    {
        switch (kind)
        {
            case FrameKind.Handshake:
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var handshake = Handshake.Read(reader);
                    lock (_inbox)
                    {
                        _handshake = handshake;
                        _handshakeIsNew = true;
                        _state = LinkState.Live;
                        _message = null;

                        // Drop anything queued from BEFORE this connection —
                        // stale frames from the old socket must not be applied
                        // to the fresh session. Done HERE, on the socket thread,
                        // because the server sends its first keyframe right on
                        // the handshake's heels: a clear performed later on the
                        // main thread was reached AFTER that keyframe had been
                        // queued and threw it away, stranding every reconnect.
                        _snapshotFrames.Clear();
                        _arrivedTicks.Clear();
                        _eventFrames.Clear();
                    }
                }

                break;

            case FrameKind.Snapshot:
            case FrameKind.SnapshotDelta:
                _sinceLastFrame.Restart();
                lock (_inbox)
                {
                    // Falling behind used to drop the OLDEST queued frame, which
                    // is fatal for deltas: the chain silently loses a link and
                    // every later frame applies to the wrong state. Drop the whole
                    // queue and ask for a fresh keyframe instead.
                    if (_snapshotFrames.Count > 64)
                    {
                        _snapshotFrames.Clear();
                        _arrivedTicks.Clear();
                        _needKeyframe = true;
                    }

                    _snapshotFrames.Enqueue((kind == FrameKind.Snapshot, payload));
                    _arrivedTicks.Add(
                        WorldSnapshotCodec.PeekFrameTick(payload, kind == FrameKind.Snapshot));
                    if (_state == LinkState.Stalled)
                    {
                        _state = LinkState.Live;
                        _message = null;
                    }
                }

                if (_needKeyframe)
                {
                    RequestKeyframe();
                }

                break;

            case FrameKind.Events:
                lock (_inbox)
                {
                    _eventFrames.Add(payload);
                }

                break;

            case FrameKind.Pong:
                var sentAtTicks = Frame.ReadLong(payload);
                var rtt = (int)Math.Max(0, (Stopwatch.GetTimestamp() - sentAtTicks) * 1000.0 / Stopwatch.Frequency);
                lock (_inbox)
                {
                    // Smoothed: a single fat sample should nudge the readout, not
                    // make it jump around.
                    _pingMilliseconds = _pingMilliseconds < 0
                        ? rtt
                        : (int)(_pingMilliseconds * 0.7f + rtt * 0.3f);
                    _pongPending = false;
                }

                break;

            case FrameKind.ServerClock:
                var (paused, speed) = Frame.ReadServerClock(payload);
                lock (_inbox)
                {
                    _serverPaused = paused;
                    _serverSpeed = speed;
                    _serverClockIsNew = true;
                }

                break;

            // §121.9: вердикт границы приёма. Отказ превращается в ЛОКАЛЬНОЕ
            // событие ManualOrderRejected — существующий слив событий в
            // SimulationRunnerBehaviour показывает тост §121.5 без единой
            // правки UI. Принятые вердикты молчат: принятый приказ виден по
            // самой колонистке.
            case FrameKind.CommandResult:
            {
                var verdict = Frame.ReadCommandResult(payload);
                if (!verdict.Accepted && verdict.ActorId is { } rejectedActor)
                {
                    lock (_inbox)
                    {
                        _verdictEvents.Add(new SimulationEvent
                        {
                            Tick = _handshake?.Tick ?? 0,
                            EntityId = rejectedActor,
                            Type = "ManualOrderRejected",
                            Message = $"Order={verdict.Order} Reason={verdict.Reason}",
                        });
                    }
                }

                break;
            }
        }
    }

    private volatile bool _needKeyframe;
    private int _keyframeFailures;

    /// <summary>The one recovery path: ask for a full frame and start again.</summary>
    private void RequestKeyframe()
    {
        _needKeyframe = false;
        Send(Frame.Wrap(FrameKind.RequestKeyframe, System.Array.Empty<byte>()));
    }

    private bool _pongPending;
    private bool _serverPaused;
    private float _serverSpeed = 1f;
    private bool _serverClockIsNew;

    /// <summary>
    /// Pings on a fixed cadence and watches for the reply. This is what turns a
    /// half-open socket — the kind a laptop lid or a NAT timeout leaves behind,
    /// where the OS still reports Open — into a reconnect instead of a world
    /// that quietly stops moving.
    /// </summary>
    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var unanswered = Stopwatch.StartNew();
        try
        {
            while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds), cancel).ConfigureAwait(false);
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                bool waiting;
                lock (_inbox)
                {
                    waiting = _pongPending;
                    if (!waiting)
                    {
                        _pongPending = true;
                        unanswered.Restart();
                    }
                }

                if (waiting && unanswered.Elapsed.TotalSeconds > DeadAfterSeconds)
                {
                    SetState(LinkState.Reconnecting, "No response from the server.");
                    socket.Abort();
                    return;
                }

                if (!waiting)
                {
                    await SendSerializedAsync(socket, Frame.Ping(Stopwatch.GetTimestamp()))
                        .ConfigureAwait(false);
                }

                // Frames should be arriving four times a second. If they are not,
                // and nobody paused the world, say so rather than looking frozen.
                lock (_inbox)
                {
                    if (_state == LinkState.Live && !_serverPaused &&
                        _sinceLastFrame.Elapsed.TotalSeconds > StallAfterSeconds)
                    {
                        _state = LinkState.Stalled;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The receive loop owns reporting; nothing useful to add here.
        }
    }

    private void SetState(LinkState state, string? message)
    {
        lock (_inbox)
        {
            if (_state == LinkState.Failed)
            {
                return;
            }

            _state = state;
            _message = message;
        }
    }

    private void Fail(string reason)
    {
        lock (_inbox)
        {
            _state = LinkState.Failed;
            _message = reason;
        }

        Debug.LogError($"[HexLive] {reason}");
    }

    // ── main thread ───────────────────────────────────────────────────────

    public void Tick(float unscaledDeltaTime)
    {
        ConsumeHandshake();
        if (!_ready)
        {
            return;
        }

        List<byte[]>? events = null;
        List<int>? arrived = null;
        int buffered;
        bool clockChanged;
        bool paused;
        float speed;

        lock (_inbox)
        {
            buffered = _snapshotFrames.Count;
            if (_arrivedTicks.Count > 0)
            {
                arrived = new List<int>(_arrivedTicks);
                _arrivedTicks.Clear();
            }

            if (_eventFrames.Count > 0)
            {
                events = new List<byte[]>(_eventFrames);
                _eventFrames.Clear();
            }

            clockChanged = _serverClockIsNew;
            _serverClockIsNew = false;
            paused = _serverPaused;
            speed = _serverSpeed;
        }

        if (clockChanged)
        {
            _clock.OnServerClock(speed, paused);
        }

        if (events != null)
        {
            DecodeEvents(events);
        }

        // ⭐ Arrivals FIRST, and this ordering is the whole reason a connected
        // client used to show an empty island: the clock refuses to present
        // anything before it has seen a frame arrive, and the only call telling
        // it one had arrived sat INSIDE the present-loop below — a loop that runs
        // zero times until the clock starts. Connected, live, 7 ms ping, not one
        // snapshot ever decoded, and no error anywhere, because nothing had
        // failed. It is also where the tick rate is measured, so it belongs here
        // on the arrival side rather than after presentation, where the estimate
        // would be measuring its own output.
        if (arrived != null)
        {
            for (var i = 0; i < arrived.Count; i++)
            {
                // The verdict is deliberately not acted on: "drop this frame" is
                // advice from a world of independent snapshots, and our deltas
                // are a chain — a hole in it is refused by the reader below and
                // answered with a keyframe, which is the only safe repair.
                _clock.OnFrameArrived(arrived[i], out _);
            }
        }

        var present = _clock.Advance(unscaledDeltaTime, buffered);
        for (var i = 0; i < present; i++)
        {
            (bool keyframe, byte[] bytes) frame;
            lock (_inbox)
            {
                if (_snapshotFrames.Count == 0)
                {
                    break;
                }

                frame = _snapshotFrames.Dequeue();
            }

            using var stream = new MemoryStream(frame.bytes);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (frame.keyframe)
            {
                try
                {
                    WorldSnapshotCodec.Read(reader, _snapshot);
                    _needKeyframe = false;
                    _keyframeFailures = 0;
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
                {
                    // A keyframe that will not decode is almost certainly a
                    // build mismatch — the codec's version check throws exactly
                    // this. One retry covers a corrupt frame; repeated failures
                    // cannot be fixed by asking again, so stop and say why
                    // instead of spamming requests four times a second forever.
                    _keyframeFailures++;
                    if (_keyframeFailures >= 3)
                    {
                        Fail($"Keyframe undecodable after {_keyframeFailures} attempts ({ex.Message}) — " +
                             "the client and server are running different builds.");
                        _ready = false;
                        break;
                    }

                    Debug.LogWarning($"[HexLive] Keyframe refused ({ex.Message}) — requesting another.");
                    RequestKeyframe();
                    break;
                }
            }
            else
            {
                try
                {
                    SnapshotDeltaReader.Apply(reader, _snapshot, _snapshot.Tick);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
                {
                    // A delta we cannot apply is NOT something to apply anyway.
                    // Say so once, drop what is queued and start again from a
                    // full frame — a mirror that quietly drifts is far worse than
                    // a second of stutter. EndOfStream/IO are included: a
                    // truncated record surfaces as those, not as InvalidData.
                    Debug.LogWarning($"[HexLive] Delta refused ({ex.Message}) — requesting a keyframe.");
                    lock (_inbox)
                    {
                        _snapshotFrames.Clear();
                        _arrivedTicks.Clear();
                    }

                    RequestKeyframe();
                    break;
                }
            }

            _clock.OnTickPresented(_snapshot.Tick);
        }
    }

    /// <summary>
    /// Handles the handshake — the first one, and every later one from a
    /// reconnect. On a reconnect the static world is already built, so all that
    /// is needed is to confirm it is still the SAME world and resync the clock.
    /// </summary>
    private void ConsumeHandshake()
    {
        Handshake? handshake;
        lock (_inbox)
        {
            if (!_handshakeIsNew)
            {
                return;
            }

            _handshakeIsNew = false;
            handshake = _handshake;
        }

        if (handshake == null)
        {
            return;
        }

        if (_ready)
        {
            // Reconnected. A different seed means the operator restarted the
            // server on another world — nothing we are showing is valid any more.
            if (handshake.Seed != Seed)
            {
                Fail($"The server is now running seed {handshake.Seed}, not {Seed} — this is a different world.");
                _ready = false;
                return;
            }

            // The queue was already cleared on the socket thread when this
            // handshake arrived (see Dispatch) — clearing again HERE would race
            // the keyframe the server sends immediately after the handshake and
            // could discard it. The request below is a belt on top of the
            // server's own keyframe-on-connect: a duplicate full frame is
            // harmless, a missing one is a frozen world.
            RequestKeyframe();

            _clock.Configure(handshake.TickDeltaTime, handshake.Tick,
                handshake.SpeedMultiplier, handshake.Paused);
            Debug.Log($"[HexLive] Reconnected at tick {handshake.Tick}.");
            return;
        }

        // The server's tuning, not ours. Applied BEFORE worldgen because the
        // object catalog decides which junctions are blocked — a client running
        // its own slightly-older ScriptableObjects would otherwise build a
        // different island and never know.
        if (!string.IsNullOrEmpty(handshake.SimData) && !SimDataFile.ApplyJson(handshake.SimData))
        {
            Fail("The server's simdata could not be applied.");
            return;
        }

        var definition = PrototypeWorldDefinitionFactory.Create(handshake.Seed);
        _localWorld = new WorldStateFactory().Create(definition);

        var checksum = TopologyChecksum.Compute(_localWorld);
        if (checksum != handshake.TopologyChecksum)
        {
            // Worldgen is deterministic, so this means the two ends are not the
            // same build. Refuse: every position we draw would be subtly wrong,
            // and retrying cannot fix a version difference.
            Fail($"Topology mismatch: server 0x{handshake.TopologyChecksum:X8}, " +
                 $"local 0x{checksum:X8}. The client and server are running different builds.");
            return;
        }

        // Static geometry comes from our own world; the wire only carries what
        // moves. Tiles as well as junctions — both are worldgen output, and the
        // checksum above is what proves ours match the server's.
        var local = WorldSnapshotExporter.Export(_localWorld);
        foreach (var junction in local.Junctions)
        {
            _snapshot.Junctions.Add(junction);
        }

        foreach (var tile in local.Tiles)
        {
            _snapshot.Tiles.Add(tile);
        }

        // Object definition ids travel as indices into a table both ends derive
        // from this same catalog. Must be built BEFORE the first frame decodes.
        DefinitionIdTable.Build(_localWorld.Content);

        lock (_inbox)
        {
            _serverPaused = handshake.Paused;
            _serverSpeed = handshake.SpeedMultiplier;
        }

        _clock.Configure(handshake.TickDeltaTime, handshake.Tick,
            handshake.SpeedMultiplier, handshake.Paused);
        _ready = true;
        Debug.Log($"[HexLive] Watching seed {handshake.Seed} from tick {handshake.Tick} " +
                  $"(topology 0x{checksum:X8} matches).");
    }

    private void DecodeEvents(List<byte[]> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            using var stream = new MemoryStream(frames[i]);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            SimulationEventCodec.Read(reader, _pendingEvents);
        }
    }

    public long DrainEvents(long sinceSeq, List<SimulationEvent> into)
    {
        // §121.9: локально синтезированные отказы — вне серверной нумерации
        // Seq, поэтому мимо водяного знака: он принадлежит потоку сервера.
        lock (_inbox)
        {
            if (_verdictEvents.Count > 0)
            {
                into.AddRange(_verdictEvents);
                _verdictEvents.Clear();
            }
        }

        if (_pendingEvents.Count == 0)
        {
            return sinceSeq;
        }

        var watermark = sinceSeq;
        for (var i = 0; i < _pendingEvents.Count; i++)
        {
            var e = _pendingEvents[i];
            if (e.Seq <= sinceSeq)
            {
                continue;
            }

            into.Add(e);
            if (e.Seq > watermark)
            {
                watermark = e.Seq;
            }
        }

        _pendingEvents.Clear();
        return watermark;
    }

    public WorldSnapshot? CreateSnapshot() => _ready ? _snapshot : null;

    public bool TryGetObjectDefinition(string id, out ObjectDefinition? definition)
    {
        if (id != null && _localWorld != null &&
            _localWorld.Content.ObjectDefinitions.TryGetValue(id, out var def))
        {
            definition = def;
            return true;
        }

        definition = null;
        return false;
    }

    // Clock controls are REQUESTS. The server owns the world's clock; what we
    // report back is what we observe, never what we asked for.
    public void Pause() => Send(Frame.Command(CommandKind.Pause, 0f));

    public void Resume() => Send(Frame.Command(CommandKind.Resume, 0f));

    public void TogglePause() => Send(Frame.Command(IsPaused ? CommandKind.Resume : CommandKind.Pause, 0f));

    public void SetSpeed(float speedMultiplier) => Send(Frame.Command(CommandKind.SetSpeed, speedMultiplier));

    public void StepSingleTick()
    {
        // Single-stepping someone else's world is not meaningful — other viewers
        // are watching the same one.
    }

    public void WriteSaveNow()
    {
        // The server owns persistence.
    }

    // One send at a time. Sends originate on three threads (frames come in on
    // the socket thread which may answer with RequestKeyframe, pings on their
    // own loop, commands on the Unity main thread), and while modern .NET
    // serialises concurrent WebSocket sends internally, Unity's Mono BCL makes
    // no such promise — interleaved sends there can corrupt the stream.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private void Send(byte[] frame)
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open)
        {
            return;
        }

        // Fire and forget: a command is advisory, and blocking the main thread
        // on the network would be a far worse bug than a dropped pause.
        _ = SendSerializedAsync(socket, frame);
    }

    private async Task SendSerializedAsync(WebSocket socket, byte[] frame)
    {
        try
        {
            await _sendGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // shutting down
        }

        try
        {
            await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true,
                _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection loop owns error reporting; a lost advisory send is fine.
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Shutdown()
    {
        _shutdown.Cancel();
        try
        {
            _socket?.Abort();
        }
        catch (Exception)
        {
            // Tearing down; nothing useful to do about it.
        }

        _shutdown.Dispose();
    }
}

}
