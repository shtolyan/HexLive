using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

namespace HexLive.Server
{

/// <summary>
/// One viewer watching the world: handshake, then a tick frame and an event
/// batch whenever there is something new.
/// <para>
/// A viewer is a passive observer of a world that does not wait for it. If a
/// connection stalls, its frames are simply skipped — the colony must never be
/// held up by somebody's slow network, so this loop polls the host's current
/// tick rather than being pushed to.
/// </para>
/// <para>
/// §121.9: авторизованный токеном игрок — больше не только зритель. Его
/// соединение несёт owner (<c>ws:&lt;guid&gt;</c>), и кадры
/// <see cref="FrameKind.NpcCommand"/> проходят: потолок кадра → rate-limit →
/// постоянное назначение §149 → реестр лиз →
/// <c>WorldHost.SubmitManualCommand</c> (единственный валидатор — тот же
/// <c>ManualCommandExecutor</c>) → синхронный
/// <see cref="FrameKind.CommandResult"/> с вердиктом. Сервер не верит клиенту
/// НИЧЕГО: «агент не может больше игрока» (§144.3) распространяется на игрока
/// по сети буквально, а часы остаются операторскими (§83.2).
/// </para>
/// </summary>
public sealed class ViewerConnection
{
    // Потолок кадра команды. Раньше 4 КБ хватало всем (групповая команда с
    // сотней акторов — сотни байт), но §120.8 везёт в PlaceBuildingBlueprint
    // ЦЕЛЫЙ чертёж дома JSON'ом: committed-план ~10 КБ, большой ручной дом —
    // десятки. 256 КБ по-прежнему копейки для памяти и заведомо больше
    // MaxBlueprintJsonChars, который исполнитель проверяет сам.
    private const int MaxCommandFrameBytes = 256 * 1024;

    // Token-bucket: щедрее любого живого игрока (drag-приказы группами — это
    // единицы кадров в секунду), но душит цикл злонамеренного клиента.
    private const double CommandRatePerSecond = 10.0;
    private const double CommandBurst = 30.0;

    private readonly WorldHost _host;
    private readonly WebSocket _socket;
    private readonly string _simData;
    private readonly bool _includeDebugDetails;
    private readonly string? _controlOwner;
    private readonly ControlLeases? _leases;
    private readonly HashSet<int>? _assignedNpcIds;
    private byte[]? _lastCraftingOptionsFrame;

    private double _rateTokens = CommandBurst;
    private DateTimeOffset _rateStamp = DateTimeOffset.UtcNow;

    private long _eventSeq;

    // This viewer's own baseline. Per-connection so a slow or reconnecting
    // client can never change what another one receives.
    private readonly SnapshotDeltaEncoder _delta = new();
    private volatile bool _keyframeRequested = true;

    public ViewerConnection(
        WorldHost host, WebSocket socket, string simData, bool includeDebugDetails,
        string? controlOwner = null, ControlLeases? leases = null,
        IReadOnlyList<int>? assignedNpcIds = null)
    {
        _host = host;
        _socket = socket;
        _simData = simData;
        _includeDebugDetails = includeDebugDetails;
        _controlOwner = controlOwner;
        _leases = leases;
        _assignedNpcIds = assignedNpcIds is null
            ? null
            : new HashSet<int>(assignedNpcIds);
    }

    private bool ControlEnabled =>
        _controlOwner is not null && _leases is not null && _assignedNpcIds is not null;

    public async Task RunAsync(CancellationToken cancel)
    {
        _eventSeq = _host.HighestEventSeq;

        var handshake = new Handshake
        {
            Seed = _host.Seed,
            Mode = (int)_host.Mode,
            Tick = _host.Tick,
            TickDeltaTime = _host.TickDeltaTime,
            SpeedMultiplier = _host.SpeedMultiplier,
            Paused = _host.IsPaused,
            EventSeq = _eventSeq,
            TopologyChecksum = _host.TopologyChecksum,
            SimData = _simData,
            ControlEnabled = ControlEnabled,
            ControlOwner = _controlOwner ?? string.Empty,
        };
        if (_assignedNpcIds is not null)
        {
            handshake.AssignedNpcIds.AddRange(_assignedNpcIds);
            handshake.AssignedNpcIds.Sort();
        }

        await SendAsync(Frame.Handshake(handshake), cancel).ConfigureAwait(false);

        // Commands arrive on their own loop so a silent client never blocks the
        // frames going out.
        var reader = Task.Run(() => ReadCommandsAsync(cancel), cancel);

        var lastTick = -1;
        var lastPaused = _host.IsPaused;
        var lastSpeed = _host.SpeedMultiplier;
        // Poll a little finer than the tick so a frame goes out promptly after
        // each step without spinning.
        var poll = TimeSpan.FromSeconds(Math.Max(0.01f, _host.TickDeltaTime / 4f));

        try
        {
            while (!cancel.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                // Tell the viewer when the world's clock moves. Another viewer
                // may have paused it, and without this the silence that follows
                // is indistinguishable from a dead connection.
                if (_host.IsPaused != lastPaused || Math.Abs(_host.SpeedMultiplier - lastSpeed) > 0.0001f)
                {
                    lastPaused = _host.IsPaused;
                    lastSpeed = _host.SpeedMultiplier;
                    await SendAsync(Frame.ServerClock(lastPaused, lastSpeed), cancel).ConfigureAwait(false);
                }

                var tick = _host.Tick;
                if (tick != lastTick)
                {
                    lastTick = tick;

                    if (_keyframeRequested)
                    {
                        _keyframeRequested = false;
                        _delta.Reset();
                    }

                    var payload = _host.EncodeFor(_delta, _includeDebugDetails, out var isKeyframe);
                    await SendAsync(
                        Frame.Wrap(isKeyframe ? FrameKind.Snapshot : FrameKind.SnapshotDelta, payload),
                        cancel).ConfigureAwait(false);

                    var events = _host.EncodeEvents(_eventSeq, out var watermark);
                    if (events != null)
                    {
                        _eventSeq = watermark;
                        await SendAsync(Frame.Wrap(FrameKind.Events, events), cancel).ConfigureAwait(false);
                    }

                    await SendCraftingOptionsIfChangedAsync(cancel).ConfigureAwait(false);

                    continue;
                }

                await Task.Delay(poll, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (WebSocketException)
        {
            // Viewer went away mid-frame. Normal.
        }

        await reader.ConfigureAwait(false);
    }

    /// <summary>
    /// §138.2: the UI reads the same server-side CraftingOptions calculation
    /// that CraftItemCommand will repeat at admission. The batch is private to
    /// this viewer because its NPC list is private (§149). Identical batches
    /// are suppressed; progress/resources/manual state naturally make a new
    /// frame when the visible answer changes.
    /// </summary>
    private async Task SendCraftingOptionsIfChangedAsync(CancellationToken cancel)
    {
        if (!ControlEnabled)
        {
            return;
        }

        var frame = _host.Read(world => Frame.CraftingOptions(
            PlayerCraftingOptions.Capture(world, _assignedNpcIds!)));
        if (FramesEqual(frame, _lastCraftingOptionsFrame))
        {
            return;
        }

        _lastCraftingOptionsFrame = frame;
        await SendAsync(frame, cancel).ConfigureAwait(false);
    }

    private static bool FramesEqual(byte[] left, byte[]? right)
    {
        if (right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private async Task ReadCommandsAsync(CancellationToken cancel)
    {
        var buffer = new byte[MaxCommandFrameBytes];
        try
        {
            while (!cancel.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.Count < 1)
                {
                    continue;
                }

                // §121.9: приказ NPC. Разбор и вердикт — в своём методе; кадр,
                // не поместившийся в буфер целиком, отвергается (это уже не
                // команда, а мусор или атака на память).
                if (buffer[0] == (byte)FrameKind.NpcCommand)
                {
                    await HandleNpcCommandAsync(buffer, result, cancel).ConfigureAwait(false);
                    continue;
                }

                // The viewer's chain broke (a gap, a decode failure, a fresh
                // mirror). Answer with a full frame — the one recovery path.
                //
                // Checked BEFORE any minimum-length guard: this frame is its
                // kind byte and nothing else. An earlier version required two
                // bytes first and silently discarded it — which turned every
                // reconnect into a world frozen forever, politely answering
                // pings while rejecting each delta it could no longer apply.
                if (buffer[0] == (byte)FrameKind.RequestKeyframe)
                {
                    _keyframeRequested = true;
                    Console.WriteLine("[viewer] asked for a keyframe");
                    continue;
                }

                if (result.Count < 2)
                {
                    continue;
                }

                // Echo pings straight back. This is what lets a viewer measure
                // round-trip time AND detect a half-open socket — the kind that
                // stays "Open" while nothing can actually cross it.
                if (buffer[0] == (byte)FrameKind.Ping)
                {
                    var token = new byte[result.Count - 1];
                    Buffer.BlockCopy(buffer, 1, token, 0, token.Length);
                    await SendAsync(Frame.Wrap(FrameKind.Pong, token), cancel).ConfigureAwait(false);
                    continue;
                }

                if (buffer[0] != (byte)FrameKind.Command)
                {
                    continue;
                }

                using var stream = new MemoryStream(buffer, 1, result.Count - 1);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                var kind = (CommandKind)reader.ReadByte();
                var value = reader.ReadSingle();

                switch (kind)
                {
                    // Refused, not clamped: there is no "a little bit paused".
                    // A client of the current build never sends these (the
                    // button is greyed on a remote link, and the in-game menu no
                    // longer pauses somebody else's colony to draw itself), so a
                    // line here means an old build or a hand-rolled client —
                    // which is exactly the case the server must not trust.
                    case CommandKind.Pause:
                    case CommandKind.Resume:
                        Console.WriteLine($"[viewer] refused clock command {kind} — operator-only");
                        break;
                    case CommandKind.SetSpeed:
                        _host.SetSpeed(value);
                        Console.WriteLine($"[viewer] set speed {value}x");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    // §121.9: один кадр NpcCommand — один синхронный вердикт CommandResult.
    // Порядок обороны: полный ли кадр → авторизован ли → rate-limit → разбор →
    // лизы → симуляция. До SubmitManualCommand доходит только то, что прошло
    // всё; сам приказ валидирует ManualCommandExecutor, вторых правил нет.
    private async Task HandleNpcCommandAsync(
        byte[] buffer, WebSocketReceiveResult result, CancellationToken cancel)
    {
        // Кадр обязан быть ЦЕЛЫМ сообщением в нашем буфере: correlationId ещё
        // можно достать (первые 4 байта payload'а), а команду уже нельзя.
        var correlationId = result.Count >= 5 ? BitConverter.ToInt32(buffer, 1) : 0;
        if (!result.EndOfMessage)
        {
            await DrainOversizedMessageAsync(buffer, cancel).ConfigureAwait(false);
            await SendResultAsync(correlationId, "FrameTooLarge", cancel).ConfigureAwait(false);
            return;
        }

        if (!ControlEnabled)
        {
            await SendResultAsync(correlationId, "ControlNotGranted", cancel).ConfigureAwait(false);
            return;
        }

        if (!TakeRateToken())
        {
            await SendResultAsync(correlationId, "RateLimited", cancel).ConfigureAwait(false);
            return;
        }

        ISimulationCommand command;
        try
        {
            var payload = new byte[result.Count - 1];
            Buffer.BlockCopy(buffer, 1, payload, 0, payload.Length);
            (correlationId, command) = Frame.ReadNpcCommand(payload);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                       or EndOfStreamException)
        {
            await SendResultAsync(correlationId, "BadFrame", cancel).ConfigureAwait(false);
            return;
        }

        if (!TryMapLease(command, out var refusal))
        {
            await SendResultAsync(correlationId, refusal, cancel,
                (command.TargetEntity)?.Value).ConfigureAwait(false);
            return;
        }

        var admission = _host.SubmitManualCommand(command);
        await SendAsync(Frame.CommandResult(correlationId, admission), cancel)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// §144.2: маппинг команды на единый реестр лиз. Взятие ручного режима =
    /// взятие лиза, снятие = освобождение, любой другой приказ = продление.
    /// Групповые проверяются ПО КАЖДОМУ актору и отказывают целиком: Apply не
    /// возвращает пер-акторные вердикты, и смешанная группа исполнилась бы
    /// частично и молча.
    /// </summary>
    private bool TryMapLease(ISimulationCommand command, out string refusal)
    {
        refusal = string.Empty;
        var leases = _leases!;
        var owner = _controlOwner!;

        // §149.3: постоянное назначение проверяется ДО временного лиза. Лиз
        // отвечает «кто сейчас командует», но не может сам выдать игроку новую
        // колонистку. Группа проходит только целиком.
        if (!AllActorsAssigned(command))
        {
            refusal = "NotAssigned";
            return false;
        }

        switch (command)
        {
            case SetManualControlCommand setManual:
                if (setManual.Enabled)
                {
                    if (!leases.TryAcquire(setManual.Npc.Value, owner, out _, out var heldBy))
                    {
                        Console.WriteLine(
                            $"[viewer {owner}] lease refused for NPC{setManual.Npc.Value}: held by {heldBy}");
                        refusal = "ControlledByOther";
                        return false;
                    }

                    return true;
                }

                if (leases.HolderOf(setManual.Npc.Value) is { Length: > 0 } current &&
                    !string.Equals(current, owner, StringComparison.Ordinal))
                {
                    refusal = "ControlledByOther";
                    return false;
                }

                leases.Release(setManual.Npc.Value, owner);
                return true;

            case SetGroupManualControlCommand groupManual:
                foreach (var actor in groupManual.Actors)
                {
                    var holder = leases.HolderOf(actor.Value);
                    if (holder.Length > 0 && !string.Equals(holder, owner, StringComparison.Ordinal))
                    {
                        refusal = "ControlledByOther";
                        return false;
                    }
                }

                foreach (var actor in groupManual.Actors)
                {
                    if (groupManual.Enabled)
                    {
                        leases.TryAcquire(actor.Value, owner, out _, out _);
                    }
                    else
                    {
                        leases.Release(actor.Value, owner);
                    }
                }

                return true;

            case IGroupSimulationCommand group:
                foreach (var actor in group.Actors)
                {
                    if (!leases.TryRenew(actor.Value, owner, out var groupHolder))
                    {
                        refusal = groupHolder.Length > 0 ? "ControlledByOther" : "NoLease";
                        return false;
                    }
                }

                return true;

            default:
                if (command.TargetEntity is { } actorId)
                {
                    if (!leases.TryRenew(actorId.Value, owner, out var holder))
                    {
                        refusal = holder.Length > 0 ? "ControlledByOther" : "NoLease";
                        return false;
                    }
                }

                return true;
        }
    }

    private bool AllActorsAssigned(ISimulationCommand command)
        => PlayerCommandAssignment.Allows(command, _assignedNpcIds!);

    private bool TakeRateToken()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _rateStamp).TotalSeconds;
        _rateStamp = now;
        _rateTokens = Math.Min(CommandBurst, _rateTokens + elapsed * CommandRatePerSecond);
        if (_rateTokens < 1.0)
        {
            return false;
        }

        _rateTokens -= 1.0;
        return true;
    }

    private Task SendResultAsync(
        int correlationId, string reason, CancellationToken cancel, int? actorId = null) =>
        SendAsync(
            Frame.CommandResult(correlationId, accepted: false, actorId, "NpcCommand", reason),
            cancel);

    // Кадр больше буфера: дочитать фрагменты до конца сообщения и выбросить —
    // иначе следующий ReceiveAsync прочтёт его СЕРЕДИНУ как новый кадр.
    private async Task DrainOversizedMessageAsync(byte[] buffer, CancellationToken cancel)
    {
        while (_socket.State == WebSocketState.Open)
        {
            var more = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel)
                .ConfigureAwait(false);
            if (more.EndOfMessage || more.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    // The frame loop and the pong echo (which lives on the READER task) both
    // send; one gate keeps them from interleaving mid-message.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private async Task SendAsync(byte[] frame, CancellationToken cancel)
    {
        await _sendGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancel)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }
}

}
