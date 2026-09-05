#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
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

    // An HTTP/WebSocket upgrade and the first protocol Handshake are separate
    // milestones. Neither may own the loading curtain forever: a TLS black hole
    // can stall ConnectAsync, while a peer which sends frames but no valid
    // Handshake keeps the byte-liveness watchdog satisfied indefinitely.
    private const double ConnectAttemptTimeoutSeconds = 12.0;
    private const double HandshakeTimeoutSeconds = 12.0;

    /// <summary>Ping cadence. Also the liveness probe — see <see cref="StallAfterSeconds"/>.</summary>
    private const double PingIntervalSeconds = 2.0;

    /// <summary>
    /// Silence longer than this (while the world is NOT paused) counts as
    /// stalled. Generous next to the 4 Hz frame rate so ordinary jitter never
    /// trips it.
    /// </summary>
    private const double StallAfterSeconds = 3.0;

    /// <summary>
    /// An unanswered ping for this long — WITH nothing at all arriving for the
    /// same stretch — means the socket is half-open: still "Open" as far as the
    /// OS is concerned, but nothing crosses it. Tear it down and reconnect
    /// rather than waiting forever on a dead pipe. An overdue pong alone is NOT
    /// death: it shares the TCP stream with the frames and on a slow link
    /// queues behind a large keyframe still transferring.
    /// </summary>
    private const double DeadAfterSeconds = 10.0;

    private readonly string _url;
    private readonly string? _controlToken;
    private readonly string _clientId;
    private readonly CancellationTokenSource _shutdown = new();

    private ClientWebSocket? _socket;
    private CancellationToken _connectionCancel;

    // §121.9: корреляция приказ→вердикт и локально синтезированные события
    // отказа (см. Dispatch: CommandResult). Кладутся на сокет-потоке,
    // дренируются на главном — поэтому под _inbox.
    private int _commandSeq;
    private readonly List<SimulationEvent> _verdictEvents = new();

    // Written by the socket thread, read by the main thread. Bytes only.
    private readonly object _inbox = new();
    private readonly Queue<(bool keyframe, int tick, byte[] bytes)> _snapshotFrames = new();

    /// <summary>
    /// The tick of every frame queued since the main thread last looked, in
    /// arrival order, each with its SOCKET-THREAD receipt instant. The clock is
    /// told about arrivals BEFORE it is asked what to present — it will not
    /// present anything at all until it has seen one (see
    /// <c>RemotePlayhead</c>), and it measures the world's real timeline from
    /// these stamps, not from the speed the server claims and not from
    /// render-frame gaps (§83.2.9): stamping here is what frees the estimate
    /// from render-frame quantization.
    /// </summary>
    private readonly List<(int tick, double arrivalSeconds)> _arrivedTicks = new();
    private readonly List<byte[]> _eventFrames = new();
    private Handshake? _handshake;
    private readonly Dictionary<int, List<CraftRecipeOption>> _craftingOptions = new();
    private readonly Dictionary<int, AgentStateFrame> _agentStates = new();
    private readonly Queue<SttTokenResultFrame> _sttTokenResults = new();
    private readonly Queue<AgentTextResultFrame> _agentTextResults = new();
    private readonly Queue<AgentSpeechMessage> _agentSpeech = new();
    private readonly Dictionary<string, PendingSpeech> _pendingSpeech = new();
    private bool _handshakeIsNew;
    private LinkState _state = LinkState.Connecting;
    private string? _message;
    private int _pingMilliseconds = -1;
    private int _attempt;

    private readonly Stopwatch _sinceLastFrame = Stopwatch.StartNew();

    // §83 фаза 3: адаптивная задержка интерполяции ВКЛЮЧЕНА — событийная
    // отправка по тику давно на проде, а старый страх «poll-сервер заставит
    // недобуферить» опровергнут синтетикой (см. RemotePlayheadTests). На
    // чистом канале лаг падает ~600→~400 мс; на замеренном канале игрока
    // 16 КБ/с (баг 339) выученный по фактическому дефициту буфер даёт втрое
    // меньше эпизодов голодания, чем фиксированные 3 тика.
    private readonly RemotePlayhead _clock = new() { AdaptiveDelay = true };
    private readonly NetworkConditionSimulator? _netsim = NetworkConditionSimulator.FromSessionConfig();
    private readonly WorldSnapshot _snapshot = new();
    private readonly List<SimulationEvent> _pendingEvents = new();

    private WorldState? _localWorld;
    private bool _ready;

    private sealed class PendingSpeech
    {
        public AgentSpeechBeginFrame Begin = null!;
        public MemoryStream Bytes = new();
        public int NextChunkIndex;
        public DateTime CreatedUtc = DateTime.UtcNow;
    }

    private sealed class InitialWorldBuild
    {
        public Handshake Handshake { get; set; } = null!;
        public WorldState World { get; set; } = null!;
        public WorldSnapshot StaticSnapshot { get; set; } = null!;
        public uint Checksum { get; set; }
    }

    // Worldgen is pure simulation work but used to run in ConsumeHandshake on
    // Unity's main thread. On a production island that made the editor/player
    // look dead exactly when the loading curtain said the island was waking.
    // Tick only polls this task and atomically adopts its finished result.
    private Task<InitialWorldBuild>? _initialWorldBuild;

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

    // §146.2: the (seed, mode) the ACCEPTED world was built from. Snapshotted
    // when the first handshake passes the checksum — `_handshake` itself is
    // replaced by every reconnect, so comparing against it would always agree.
    private int _acceptedSeed;
    private GameMode _acceptedMode;

    public int CurrentTick => _snapshot.Tick;

    public float TickAlpha => _clock.TickAlpha;

    public bool IsPaused => _clock.IsPaused;

    public float SpeedMultiplier => _clock.SpeedMultiplier;

    // We do not own this world. Both of these being false is what makes the
    // debug body controls and the save button go quiet instead of throwing.
    public bool SupportsDirectWorldMutation => false;

    public bool SupportsClientSave => false;

    public bool SupportsAgentIntegration
    {
        get { lock (_inbox) return _handshake?.AgentIntegrationEnabled == true; }
    }

    public bool SttAvailable
    {
        get { lock (_inbox) return _handshake?.SttAvailable == true; }
    }

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

    public bool CanControlNpc(EntityId npc)
    {
        lock (_inbox)
        {
            return _handshake?.ControlEnabled == true &&
                   _handshake.AssignedNpcIds.Contains(npc.Value) &&
                   (!_agentStates.TryGetValue(npc.Value, out var agent) || !agent.Attached);
        }
    }

    public bool TryGetAgentState(EntityId npc, out AgentStateFrame state)
    {
        lock (_inbox)
        {
            if (_agentStates.TryGetValue(npc.Value, out var found))
            {
                state = found;
                return true;
            }
            state = new AgentStateFrame { NpcId = npc.Value };
            return false;
        }
    }

    public void RequestSttToken(int correlationId)
    {
        if (SupportsAgentIntegration && SttAvailable)
            Send(AgentWire.SttTokenRequest(correlationId));
    }

    public bool TryTakeSttTokenResult(out SttTokenResultFrame result)
    {
        lock (_inbox)
        {
            if (_sttTokenResults.Count > 0)
            {
                result = _sttTokenResults.Dequeue();
                return true;
            }
            result = default;
            return false;
        }
    }

    public void SendAgentText(int correlationId, EntityId npc, string messageId,
        string language, string text)
    {
        if (SupportsAgentIntegration)
            Send(AgentWire.AgentTextInput(correlationId, npc.Value, messageId, language, text));
    }

    public bool TryTakeAgentTextResult(out AgentTextResultFrame result)
    {
        lock (_inbox)
        {
            if (_agentTextResults.Count > 0)
            {
                result = _agentTextResults.Dequeue();
                return true;
            }
            result = default;
            return false;
        }
    }

    public bool TryTakeAgentSpeech(out AgentSpeechMessage speech)
    {
        lock (_inbox)
        {
            if (_agentSpeech.Count > 0)
            {
                speech = _agentSpeech.Dequeue();
                return true;
            }
            speech = null!;
            return false;
        }
    }

    public bool TryGetCraftingOptions(EntityId npc, List<CraftRecipeOption> into)
    {
        into.Clear();
        lock (_inbox)
        {
            if (_handshake?.ControlEnabled != true ||
                !_handshake.AssignedNpcIds.Contains(npc.Value) ||
                !_craftingOptions.TryGetValue(npc.Value, out var options))
            {
                return false;
            }

            into.AddRange(options);
            return true;
        }
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

            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var socket = new ClientWebSocket();
            try
            {
                // §121.9: токен и стабильный id клиента едут заголовками
                // HTTP-upgrade — не в query string, где они текли бы в логи.
                if (_controlToken is not null)
                {
                    socket.Options.SetRequestHeader(
                        "Authorization", "Bearer " + _controlToken);
                    socket.Options.SetRequestHeader("X-HexLive-Client-Id", _clientId);
                }

                // §83.4: этот клиент умеет кадры FrameKind.Compressed. Старый
                // сервер заголовка не знает и шлёт как раньше — совместимо в
                // обе стороны без бампа ProtocolVersion.
                socket.Options.SetRequestHeader("X-HexLive-Accepts", "gzip");

                using (var connectAttempt =
                       CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime.Token))
                {
                    connectAttempt.CancelAfter(
                        TimeSpan.FromSeconds(ConnectAttemptTimeoutSeconds));
                    await socket.ConnectAsync(new Uri(_url), connectAttempt.Token)
                        .ConfigureAwait(false);
                }

                lock (_inbox)
                {
                    // Liveness belongs to THIS WebSocket. Carrying an unanswered
                    // ping from the dead socket into the replacement prevents the
                    // replacement from ever sending its own probe and makes it get
                    // aborted ten seconds later in an endless reconnect loop.
                    _pongPending = false;
                    _pingMilliseconds = -1;
                    _sinceLastFrame.Restart();
                    _socket = socket;
                    _connectionCancel = connectionLifetime.Token;
                }

                Volatile.Write(ref _lastReceiveTimestamp, Stopwatch.GetTimestamp());

                await PumpAsync(socket, connectionLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                SetState(LinkState.Reconnecting,
                    $"Connection attempt timed out after {ConnectAttemptTimeoutSeconds:0} seconds.");
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
                // A send from the old connection must not keep the shared send
                // gate forever and starve pings, commands and keyframe requests
                // on the replacement socket. Cancel it and force the old socket
                // awake before publishing that there is no current connection.
                connectionLifetime.Cancel();
                try
                {
                    socket.Abort();
                }
                catch (Exception)
                {
                    // The socket is already gone; cleanup is complete enough.
                }

                lock (_inbox)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                        _connectionCancel = default;
                        _pongPending = false;
                    }
                }
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
        using var pumpLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        var pinger = PingLoopAsync(socket, pumpLifetime.Token);
        try
        {
            while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                while (true)
                {
                    // Cancelling this token intentionally aborts only the current
                    // connection. A receive timeout still belongs to PingLoop,
                    // but a replacement socket must be able to retire this pump.
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancel)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    // Живость меряется ПРИХОДЯЩИМИ БАЙТАМИ, не только понгом:
                    // понг едет тем же TCP-потоком и на медленном канале
                    // застревает ПОЗАДИ большого кейфрейма. Считать такую
                    // паузу смертью сокета — значит рвать живое соединение на
                    // середине загрузки и никогда не докачать её (ровно та
                    // 12-секундная петля реконнекта, что показал прод).
                    if (result.Count > 0)
                    {
                        Volatile.Write(ref _lastReceiveTimestamp, Stopwatch.GetTimestamp());
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

                // §83: the network-condition rehearsal (-hexlive-netsim) delays
                // every message HERE, before dispatch, so arrival stamps and
                // stall detection see the simulated latency exactly as they
                // would see a real one. FIFO is preserved — TCP delays, it
                // never reorders.
                if (_netsim != null)
                {
                    await _netsim.DelayAsync(cancel).ConfigureAwait(false);
                }

                Dispatch((FrameKind)bytes[0], payload);
            }
        }
        finally
        {
            pumpLifetime.Cancel();
            try
            {
                await pinger.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when this receive pump is retired.
            }
        }
    }

    private void Dispatch(FrameKind kind, byte[] payload)
    {
        switch (kind)
        {
            // §83.4: сервер сжал большой кадр (мы объявили поддержку заголовком
            // upgrade-запроса). Разжать и раздать как обычный.
            case FrameKind.Compressed:
            {
                byte[] inner;
                try
                {
                    inner = Frame.Decompress(payload);
                }
                catch (Exception ex) when (
                    ex is InvalidDataException or EndOfStreamException or IOException)
                {
                    bool waitingForHandshake;
                    lock (_inbox)
                    {
                        waitingForHandshake = _handshake == null;
                    }
                    if (waitingForHandshake)
                    {
                        // There is no usable session to preserve. Waiting for
                        // later snapshots after rejecting frame #1 only leaves
                        // LoadingScreen in an eternal Connecting state.
                        Fail($"Server handshake is invalid: {ex.Message}");
                    }
                    else
                    {
                        Debug.LogWarning($"[HexLive] Compressed frame refused ({ex.Message}).");
                    }
                    return;
                }

                if (inner.Length < 1)
                {
                    return;
                }

                var innerPayload = new byte[inner.Length - 1];
                Buffer.BlockCopy(inner, 1, innerPayload, 0, innerPayload.Length);
                Dispatch((FrameKind)inner[0], innerPayload);
                return;
            }

            case FrameKind.Handshake:
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var handshake = Handshake.Read(reader);
                    lock (_inbox)
                    {
                        // A TCP/WebSocket upgrade is not a successful reconnect:
                        // only a valid protocol handshake proves this session is
                        // usable. Otherwise a peer that accepts and then hangs
                        // would reset the retry budget forever.
                        _attempt = 0;
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
                        _craftingOptions.Clear();
                        _agentStates.Clear();
                        _sttTokenResults.Clear();
                        _agentTextResults.Clear();
                        _agentSpeech.Clear();
                        foreach (var pending in _pendingSpeech.Values) pending.Bytes.Dispose();
                        _pendingSpeech.Clear();
                    }
                }

                break;

            case FrameKind.Snapshot:
            case FrameKind.SnapshotDelta:
                _sinceLastFrame.Restart();
                // The receipt instant, taken on THIS thread. The playhead syncs
                // to these stamps; taking them on the main thread instead
                // quantized every measurement to render frames and silently
                // dropped the sample whenever two frames landed in one Update.
                var arrivalSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                var frameTick = WorldSnapshotCodec.PeekFrameTick(
                    payload, kind == FrameKind.Snapshot);
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

                    _snapshotFrames.Enqueue((kind == FrameKind.Snapshot, frameTick, payload));
                    _arrivedTicks.Add((frameTick, arrivalSeconds));
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

            case FrameKind.CraftingOptions:
            {
                var snapshot = Frame.ReadCraftingOptions(payload);
                lock (_inbox)
                {
                    _craftingOptions.Clear();
                    for (var i = 0; i < snapshot.Npcs.Count; i++)
                    {
                        var npc = snapshot.Npcs[i];
                        if (_handshake?.AssignedNpcIds.Contains(npc.NpcId) == true)
                        {
                            _craftingOptions[npc.NpcId] = npc.Options;
                        }
                    }
                }

                break;
            }

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

            case FrameKind.AgentState:
                TryDispatchAgentFrame(() =>
                {
                    var state = AgentWire.ReadAgentState(payload);
                    lock (_inbox)
                    {
                        _agentStates[state.NpcId] = state;
                        if (!state.Attached)
                            foreach (var id in _pendingSpeech.Where(item => item.Value.Begin.NpcId == state.NpcId)
                                         .Select(item => item.Key).ToArray()) DropPendingSpeech(id);
                    }
                });
                break;

            case FrameKind.SttTokenResult:
                TryDispatchAgentFrame(() =>
                {
                    var result = AgentWire.ReadSttTokenResult(payload);
                    lock (_inbox) _sttTokenResults.Enqueue(result);
                });
                break;

            case FrameKind.AgentTextResult:
                TryDispatchAgentFrame(() =>
                {
                    var result = AgentWire.ReadAgentTextResult(payload);
                    lock (_inbox) _agentTextResults.Enqueue(result);
                });
                break;

            case FrameKind.AgentSpeechBegin:
                TryDispatchAgentFrame(() =>
                {
                    var begin = AgentWire.ReadAgentSpeechBegin(payload);
                    lock (_inbox)
                    {
                        if (_handshake?.AssignedNpcIds.Contains(begin.NpcId) != true) return;
                        if (_pendingSpeech.Count >= 20 && !_pendingSpeech.ContainsKey(begin.UtteranceId)) return;
                        if (_pendingSpeech.TryGetValue(begin.UtteranceId, out var old))
                            old.Bytes.Dispose();
                        _pendingSpeech[begin.UtteranceId] = new PendingSpeech
                        {
                            Begin = begin,
                            Bytes = new MemoryStream(Math.Max(0, begin.TotalBytes)),
                        };
                    }
                });
                break;

            case FrameKind.AgentSpeechChunk:
                TryDispatchAgentFrame(() =>
                {
                    var chunk = AgentWire.ReadAgentSpeechChunk(payload);
                    lock (_inbox)
                    {
                        if (!_pendingSpeech.TryGetValue(chunk.UtteranceId, out var pending) ||
                            chunk.Index != pending.NextChunkIndex ||
                            pending.Bytes.Length + chunk.Bytes.Length > pending.Begin.TotalBytes)
                        {
                            DropPendingSpeech(chunk.UtteranceId);
                            return;
                        }
                        pending.Bytes.Write(chunk.Bytes, 0, chunk.Bytes.Length);
                        pending.NextChunkIndex++;
                    }
                });
                break;

            case FrameKind.AgentSpeechEnd:
                TryDispatchAgentFrame(() =>
                {
                    var end = AgentWire.ReadAgentSpeechEnd(payload);
                    lock (_inbox)
                    {
                        if (!_pendingSpeech.TryGetValue(end.UtteranceId, out var pending)) return;
                        var bytes = pending.Bytes.ToArray();
                        string actual;
                        using (var hash = SHA256.Create())
                            actual = ToHex(hash.ComputeHash(bytes));
                        var validWave = bytes.Length == 0 &&
                                        pending.Begin.DurationMilliseconds == 0 ||
                                        Audio.VoiceVisemeBaker.TryValidateWave(bytes,
                                            out var durationMs, out _) &&
                                        Math.Abs(durationMs - pending.Begin.DurationMilliseconds) <= 100;
                        if (bytes.Length == pending.Begin.TotalBytes && validWave &&
                            string.Equals(actual, pending.Begin.Sha256, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(actual, end.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            _agentSpeech.Enqueue(new AgentSpeechMessage
                            {
                                Metadata = pending.Begin,
                                Wav = bytes,
                            });
                            while (_agentSpeech.Count > 8) _agentSpeech.Dequeue();
                        }
                        DropPendingSpeech(end.UtteranceId);
                    }
                });
                break;
        }
    }

    private static void TryDispatchAgentFrame(Action decode)
    {
        try { decode(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            Debug.LogWarning($"[HexLive] Agent frame refused ({ex.GetType().Name}).");
        }
    }

    private void DropPendingSpeech(string utteranceId)
    {
        if (_pendingSpeech.TryGetValue(utteranceId, out var pending))
        {
            pending.Bytes.Dispose();
            _pendingSpeech.Remove(utteranceId);
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        for (var i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
        return builder.ToString();
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
    /// Stopwatch-момент последнего ПРИНЯТОГО куска байтов текущего сокета —
    /// пишется на сокет-потоке, читается пинг-циклом. Вторая половина проверки
    /// живости: неотвеченный понг смертелен только вместе с полной тишиной в
    /// приёме, иначе медленная докачка кейфрейма читается как смерть.
    /// </summary>
    private long _lastReceiveTimestamp;

    /// <summary>
    /// Pings on a fixed cadence and watches for the reply. This is what turns a
    /// half-open socket — the kind a laptop lid or a NAT timeout leaves behind,
    /// where the OS still reports Open — into a reconnect instead of a world
    /// that quietly stops moving.
    /// </summary>
    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var unanswered = Stopwatch.StartNew();
        var waitingForHandshake = Stopwatch.StartNew();
        try
        {
            while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds), cancel).ConfigureAwait(false);
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                bool handshakeAccepted;
                lock (_inbox)
                {
                    handshakeAccepted = _handshake != null;
                }
                if (!handshakeAccepted &&
                    waitingForHandshake.Elapsed.TotalSeconds > HandshakeTimeoutSeconds)
                {
                    SetState(LinkState.Reconnecting,
                        $"No valid handshake after {HandshakeTimeoutSeconds:0} seconds.");
                    socket.Abort();
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

                var sinceBytesSeconds =
                    (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastReceiveTimestamp)) /
                    (double)Stopwatch.Frequency;
                if (waiting && unanswered.Elapsed.TotalSeconds > DeadAfterSeconds &&
                    sinceBytesSeconds > DeadAfterSeconds)
                {
                    SetState(LinkState.Reconnecting, "No response from the server.");
                    socket.Abort();
                    return;
                }

                if (!waiting)
                {
                    await SendSerializedAsync(socket, Frame.Ping(Stopwatch.GetTimestamp()), cancel)
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
        lock (_inbox)
        {
            var expiry = DateTime.UtcNow.AddSeconds(-30);
            foreach (var id in _pendingSpeech.Where(item => item.Value.CreatedUtc < expiry)
                         .Select(item => item.Key).ToArray()) DropPendingSpeech(id);
        }
        ConsumeHandshake();
        if (!_ready)
        {
            return;
        }

        List<byte[]>? events = null;
        List<(int tick, double arrivalSeconds)>? arrived = null;
        int buffered;
        bool clockChanged;
        bool paused;
        float speed;

        lock (_inbox)
        {
            buffered = _snapshotFrames.Count;
            if (_arrivedTicks.Count > 0)
            {
                arrived = new List<(int, double)>(_arrivedTicks);
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
                _clock.OnFrameArrived(arrived[i].tick, arrived[i].arrivalSeconds, out _);
            }
        }

        _clock.Advance(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            unscaledDeltaTime);

        // Decode every queued frame the playhead has reached — bounded by
        // TickToDecode, never by "how many happen to be buffered". At 1x the
        // playhead moves ~0.07 tick per rendered frame, so this admits at most
        // one frame per Update by construction and the old 2x-speed burst
        // (prev=N, curr=N+2 lerped over one tick of alpha) cannot happen.
        // Operator fast-forward legitimately admits several; the guard is only
        // a runaway stop, sized above the queue's own overflow cap.
        for (var i = 0; i < 128; i++)
        {
            (bool keyframe, int tick, byte[] bytes) frame;
            lock (_inbox)
            {
                if (_snapshotFrames.Count == 0 ||
                    _snapshotFrames.Peek().tick > _clock.TickToDecode)
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

        RemoteLinkDiagnostics.Publish(
            _clock.BufferedTicks, _clock.DelayTicks, _clock.TargetErrorTicks,
            _clock.JitterP95Milliseconds, buffered, _netsim != null);
    }

    /// <summary>
    /// Handles the handshake — the first one, and every later one from a
    /// reconnect. On a reconnect the static world is already built, so all that
    /// is needed is to confirm it is still the SAME world and resync the clock.
    /// </summary>
    private void ConsumeHandshake()
    {
        if (TryCompleteInitialWorldBuild())
        {
            return;
        }

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
            // Reconnected. A different seed or mode means the operator
            // restarted the server on another world — nothing we are showing
            // is valid any more.
            if (handshake.Seed != _acceptedSeed || (GameMode)handshake.Mode != _acceptedMode)
            {
                Fail($"The server is now running seed {handshake.Seed} ({(GameMode)handshake.Mode})," +
                     $" not {_acceptedSeed} ({_acceptedMode}) — this is a different world.");
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

        Debug.Log($"[HexLive] Building seed {handshake.Seed} topology on a worker.");
        _initialWorldBuild = Task.Run(() => BuildInitialWorld(handshake));
    }

    private static InitialWorldBuild BuildInitialWorld(Handshake handshake)
    {
        // §146.2: the island is a function of (seed, mode) — regenerate with
        // the server's mode or the checksum below would refuse every BigIsland
        // world with a misleading "different builds".
        var clock = Stopwatch.StartNew();
        var definition = PrototypeWorldDefinitionFactory.Create(
            handshake.Seed, (GameMode)handshake.Mode);
        var definitionMs = clock.ElapsedMilliseconds;

        // Topology ONLY. The full Create also spawns every object and NPC,
        // rolls wardrobes and bakes ~200k junctions' step deltas — simulation
        // furniture a viewer never touches: entities arrive in the first wire
        // keyframe, and this world is read for Content definitions, tiles and
        // junctions alone. On the production big island the full build was the
        // main course of a 237-second connect.
        clock.Restart();
        var world = new WorldStateFactory().CreateTopology(definition);
        var topologyMs = clock.ElapsedMilliseconds;

        clock.Restart();
        var checksum = TopologyChecksum.Compute(world);
        var checksumMs = clock.ElapsedMilliseconds;

        clock.Restart();
        var snapshot = WorldSnapshotExporter.Export(world);
        Debug.Log($"[HexLive] Topology build: definition {definitionMs} ms, " +
                  $"junctions {topologyMs} ms, checksum {checksumMs} ms, " +
                  $"export {clock.ElapsedMilliseconds} ms " +
                  $"({world.Junctions.Items.Count} junctions).");

        return new InitialWorldBuild
        {
            Handshake = handshake,
            World = world,
            Checksum = checksum,
            StaticSnapshot = snapshot,
        };
    }

    /// <returns>True while a build is still pending or after its result was
    /// adopted; false when there was no build (or a stale build was discarded)
    /// and a newly arrived handshake may be consumed in this same tick.</returns>
    private bool TryCompleteInitialWorldBuild()
    {
        var task = _initialWorldBuild;
        if (task == null)
        {
            return false;
        }
        if (!task.IsCompleted)
        {
            return true;
        }

        _initialWorldBuild = null;
        if (task.IsCanceled)
        {
            Fail("The local topology build was canceled.");
            return true;
        }
        if (task.IsFaulted)
        {
            Fail("The local topology build failed: " +
                 (task.Exception?.GetBaseException().Message ?? "unknown error"));
            return true;
        }

        var result = task.Result;
        lock (_inbox)
        {
            if (!ReferenceEquals(_handshake, result.Handshake))
            {
                // The socket reconnected while the old seed was building. Its
                // result belongs to the retired connection; consume the new
                // handshake below instead of ever publishing stale geometry.
                return false;
            }
        }

        var handshake = result.Handshake;
        if (result.Checksum != handshake.TopologyChecksum)
        {
            // Worldgen is deterministic, so this means the two ends are not the
            // same build. Refuse: every position we draw would be subtly wrong,
            // and retrying cannot fix a version difference.
            Fail($"Topology mismatch: server 0x{handshake.TopologyChecksum:X8}, " +
                 $"local 0x{result.Checksum:X8}. The client and server are running different builds.");
            return true;
        }

        _localWorld = result.World;

        // Static geometry comes from our own world; the wire only carries what
        // moves. Tiles as well as junctions — both are worldgen output, and the
        // checksum above is what proves ours match the server's.
        foreach (var junction in result.StaticSnapshot.Junctions)
        {
            _snapshot.Junctions.Add(junction);
        }

        foreach (var tile in result.StaticSnapshot.Tiles)
        {
            _snapshot.Tiles.Add(tile);
        }

        // Object definition ids travel as indices into a table both ends derive
        // from this same catalog. Must be built BEFORE the first frame decodes.
        DefinitionIdTable.Build(_localWorld.Content);

        _acceptedSeed = handshake.Seed;
        _acceptedMode = (GameMode)handshake.Mode;

        lock (_inbox)
        {
            _serverPaused = handshake.Paused;
            _serverSpeed = handshake.SpeedMultiplier;
        }

        _clock.Configure(handshake.TickDeltaTime, handshake.Tick,
            handshake.SpeedMultiplier, handshake.Paused);
        _ready = true;
        Debug.Log($"[HexLive] Watching seed {handshake.Seed} from tick {handshake.Tick} " +
                  $"(topology 0x{result.Checksum:X8} matches).");

        // §149: права ПОЛУЧЕННЫЕ, а не запрошенные. Без этой строки лог отвечал
        // только «к чему подключился» и молчал о том, чем разрешено управлять:
        // мёртвый тумблер на выданной колонистке выглядел точно так же, как
        // отсутствие назначения, и различить их можно было лишь по серверному
        // hexlive-players.json.
        Debug.Log($"[HexLive] Control: enabled={handshake.ControlEnabled} " +
                  $"owner={(string.IsNullOrEmpty(handshake.ControlOwner) ? "—" : handshake.ControlOwner)} " +
                  $"assigned=[{string.Join(",", handshake.AssignedNpcIds)}]");
        return true;
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
        ClientWebSocket? socket;
        CancellationToken connectionCancel;
        lock (_inbox)
        {
            socket = _socket;
            connectionCancel = _connectionCancel;
        }

        if (socket == null || socket.State != WebSocketState.Open ||
            connectionCancel.IsCancellationRequested)
        {
            return;
        }

        // Fire and forget: a command is advisory, and blocking the main thread
        // on the network would be a far worse bug than a dropped pause.
        _ = SendSerializedAsync(socket, frame, connectionCancel);
    }

    private async Task SendSerializedAsync(
        WebSocket socket,
        byte[] frame,
        CancellationToken connectionCancel)
    {
        try
        {
            await _sendGate.WaitAsync(connectionCancel).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // shutting down
        }

        try
        {
            await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true,
                connectionCancel).ConfigureAwait(false);
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
