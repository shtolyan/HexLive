using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// </summary>
public sealed class ViewerConnection
{
    private readonly WorldHost _host;
    private readonly WebSocket _socket;
    private readonly string _simData;
    private readonly bool _includeDebugDetails;

    private long _eventSeq;

    // This viewer's own baseline. Per-connection so a slow or reconnecting
    // client can never change what another one receives.
    private readonly SnapshotDeltaEncoder _delta = new();
    private volatile bool _keyframeRequested = true;

    public ViewerConnection(WorldHost host, WebSocket socket, string simData, bool includeDebugDetails)
    {
        _host = host;
        _socket = socket;
        _simData = simData;
        _includeDebugDetails = includeDebugDetails;
    }

    public async Task RunAsync(CancellationToken cancel)
    {
        _eventSeq = _host.HighestEventSeq;

        await SendAsync(Frame.Handshake(new Handshake
        {
            Seed = _host.Seed,
            Tick = _host.Tick,
            TickDeltaTime = _host.TickDeltaTime,
            SpeedMultiplier = _host.SpeedMultiplier,
            Paused = _host.IsPaused,
            EventSeq = _eventSeq,
            TopologyChecksum = _host.TopologyChecksum,
            SimData = _simData,
        }), cancel).ConfigureAwait(false);

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

    private async Task ReadCommandsAsync(CancellationToken cancel)
    {
        var buffer = new byte[256];
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
