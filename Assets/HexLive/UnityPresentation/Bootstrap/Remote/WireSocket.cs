#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using UnityEngine;
#else
using System.Net.WebSockets;
#endif

namespace HexLive.UnityPresentation.Bootstrap.Remote
{

/// <summary>
/// What the client tells the server about itself when the socket opens:
/// the player token (§145.3), the stable client id, and whether it reads
/// <c>FrameKind.Compressed</c> (§83.4). Desktop sends these as upgrade
/// headers; the browser cannot set headers on a WebSocket, so the web
/// socket carries them as subprotocols instead (§168.2).
/// </summary>
public sealed class WireSocketIdentity
{
    public const string ProtocolMarker = "hexlive.v1";
    public const string TokenProtocolPrefix = "hexlive.token.";
    public const string ClientProtocolPrefix = "hexlive.client.";

    public WireSocketIdentity(string? controlToken, string clientId, bool acceptsGzip)
    {
        ControlToken = controlToken;
        ClientId = clientId;
        AcceptsGzip = acceptsGzip;
    }

    public string? ControlToken { get; }
    public string ClientId { get; }
    public bool AcceptsGzip { get; }

    /// <summary>
    /// §168.2: the subprotocol list a browser offers. Values are base64url
    /// without padding — subprotocol names admit only RFC 7230 tchars, and a
    /// closed-test key may carry '/', '+' or '='.
    /// </summary>
    public IReadOnlyList<string> SubProtocols()
    {
        var list = new List<string> { ProtocolMarker };
        if (ControlToken is not null)
        {
            list.Add(TokenProtocolPrefix + Base64Url(ControlToken));
            list.Add(ClientProtocolPrefix + Base64Url(ClientId));
        }
        return list;
    }

    public static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The one WebSocket the remote backend talks through (§168.2). Messages are
/// whole binary frames; <see cref="ReceiveMessageAsync"/> returns null when
/// the peer closed. Desktop wraps <c>ClientWebSocket</c> exactly as the
/// backend used it before; the web build wraps the browser's <c>WebSocket</c>
/// through <c>HexWireSocket.jslib</c>.
/// </summary>
public interface IWireSocket : IDisposable
{
    bool IsOpen { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancel);

    /// <param name="onBytes">Called whenever bytes arrive, before the message
    /// is complete — liveness is measured by bytes, not by whole frames
    /// (§83.4: a pong queues behind a large keyframe on a slow link).</param>
    Task<byte[]?> ReceiveMessageAsync(Action onBytes, CancellationToken cancel);

    Task SendAsync(byte[] frame, CancellationToken cancel);

    void Abort();
}

/// <summary>
/// §168.4: where an <c>await</c> in the wire code resumes. Desktop keeps
/// <c>ConfigureAwait(false)</c> — the socket loop lives on the thread pool and
/// must not hop to Unity's main thread. In the browser that same call is a
/// trap: on Unity's main thread the continuation of a <c>ConfigureAwait(false)</c>
/// await is queued to the thread pool, and the web has no thread pool, so the
/// loop never resumes. Measured 9.10.2026: the browser received 78 frames
/// while <c>ConnectAsync</c> never returned. In the web build it resumes
/// through Unity's synchronization context instead.
/// </summary>
public static class WireAwait
{
#if UNITY_WEBGL && !UNITY_EDITOR
    private const bool StayOnContext = true;
#else
    private const bool StayOnContext = false;
#endif

#pragma warning disable RS0030 // the one sanctioned ConfigureAwait in wire code (§168.4)
    public static System.Runtime.CompilerServices.ConfiguredTaskAwaitable OnWire(this Task task) =>
        task.ConfigureAwait(StayOnContext);

    public static System.Runtime.CompilerServices.ConfiguredTaskAwaitable<T> OnWire<T>(this Task<T> task) =>
        task.ConfigureAwait(StayOnContext);
#pragma warning restore RS0030
}

public static class WireSocket
{
    public static IWireSocket Create(WireSocketIdentity identity)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return new BrowserWireSocket(identity);
#else
        return new ClientWireSocket(identity);
#endif
    }

    /// <summary>
    /// §168.4: a delay that ticks on the platform at hand. Desktop keeps
    /// <c>Task.Delay</c>; in the browser the thread-pool timer behind it never
    /// fires, so the wait is a Unity <c>Awaitable</c> on the main thread.
    /// </summary>
    public static async Task Delay(TimeSpan delay, CancellationToken cancel)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        await Awaitable.WaitForSecondsAsync((float)delay.TotalSeconds, cancel);
#else
        await Task.Delay(delay, cancel).ConfigureAwait(false);
#endif
    }
}

#if !(UNITY_WEBGL && !UNITY_EDITOR)
/// <summary>Desktop: the <c>ClientWebSocket</c> path the backend always had.</summary>
internal sealed class ClientWireSocket : IWireSocket
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _chunk = new byte[64 * 1024];
    private readonly MemoryStream _message = new();

    public ClientWireSocket(WireSocketIdentity identity)
    {
        // §121.9: токен и стабильный id клиента едут заголовками
        // HTTP-upgrade — не в query string, где они текли бы в логи.
        if (identity.ControlToken is not null)
        {
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + identity.ControlToken);
            _socket.Options.SetRequestHeader("X-HexLive-Client-Id", identity.ClientId);
        }

        // §83.4: этот клиент умеет кадры FrameKind.Compressed. Старый
        // сервер заголовка не знает и шлёт как раньше — совместимо в
        // обе стороны без бампа ProtocolVersion.
        if (identity.AcceptsGzip)
        {
            _socket.Options.SetRequestHeader("X-HexLive-Accepts", "gzip");
        }
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public Task ConnectAsync(Uri uri, CancellationToken cancel) => _socket.ConnectAsync(uri, cancel);

    public async Task<byte[]?> ReceiveMessageAsync(Action onBytes, CancellationToken cancel)
    {
        _message.SetLength(0);
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(_chunk), cancel)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.Count > 0)
            {
                onBytes();
            }

            _message.Write(_chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                return _message.ToArray();
            }
        }
    }

    public Task SendAsync(byte[] frame, CancellationToken cancel) =>
        _socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancel);

    public void Abort() => _socket.Abort();

    public void Dispose()
    {
        _socket.Dispose();
        _message.Dispose();
    }
}
#else
/// <summary>
/// Web: the browser's <c>WebSocket</c>, driven from the main thread (§168.4).
/// The jslib queues whole binary messages as they arrive; this side polls the
/// queue once per frame through <c>Awaitable.NextFrameAsync</c>, which is the
/// render cadence the backend drains at anyway — no callbacks into C#, so no
/// re-entrancy into the backend from inside a browser event.
/// </summary>
internal sealed class BrowserWireSocket : IWireSocket
{
    private const int StateOpen = 1;
    private const int StateClosed = 3;

    [DllImport("__Internal")] private static extern int HexWireOpen(string url, string protocolsJson);
    [DllImport("__Internal")] private static extern int HexWireState(int id);
    [DllImport("__Internal")] private static extern int HexWireNextSize(int id);
    [DllImport("__Internal")] private static extern int HexWireTake(int id, byte[] buffer, int length);
    [DllImport("__Internal")] private static extern int HexWireSend(int id, byte[] buffer, int length);
    [DllImport("__Internal")] private static extern int HexWireCloseCode(int id);
    [DllImport("__Internal")] private static extern void HexWireClose(int id);

    private readonly WireSocketIdentity _identity;
    private int _id;

    public BrowserWireSocket(WireSocketIdentity identity)
    {
        _identity = identity;
    }

    public bool IsOpen => _id != 0 && HexWireState(_id) == StateOpen;

    public async Task ConnectAsync(Uri uri, CancellationToken cancel)
    {
        var protocols = new StringBuilder("[");
        foreach (var protocol in _identity.SubProtocols())
        {
            if (protocols.Length > 1)
            {
                protocols.Append(',');
            }
            protocols.Append('"').Append(protocol).Append('"');
        }
        protocols.Append(']');

        _id = HexWireOpen(uri.AbsoluteUri, protocols.ToString());
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var state = HexWireState(_id);
            if (state == StateOpen)
            {
                return;
            }
            if (state == StateClosed)
            {
                // The browser hides the reason on purpose (no status, no
                // headers); the close code is all there is.
                throw new IOException($"WebSocket closed during connect (code {HexWireCloseCode(_id)}).");
            }
            await Awaitable.NextFrameAsync(cancel);
        }
    }

    public async Task<byte[]?> ReceiveMessageAsync(Action onBytes, CancellationToken cancel)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var size = HexWireNextSize(_id);
            if (size >= 0)
            {
                var bytes = new byte[size];
                HexWireTake(_id, bytes, size);
                onBytes();
                return bytes;
            }
            if (HexWireState(_id) == StateClosed)
            {
                return null;
            }
            await Awaitable.NextFrameAsync(cancel);
        }
    }

    public Task SendAsync(byte[] frame, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (HexWireSend(_id, frame, frame.Length) == 0)
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }
        return Task.CompletedTask;
    }

    public void Abort()
    {
        if (_id != 0)
        {
            HexWireClose(_id);
            _id = 0;
        }
    }

    public void Dispose() => Abort();
}
#endif

}
