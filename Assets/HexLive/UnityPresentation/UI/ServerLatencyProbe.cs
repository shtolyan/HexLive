using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Bootstrap.Remote;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    public readonly struct ServerProbeResult
    {
        public readonly int PingMilliseconds;
        public readonly int ProtocolVersion;
        public ServerProbeResult(int ping, int protocol) { PingMilliseconds = ping; ProtocolVersion = protocol; }
    }

    public static class ServerLatencyProbe
    {
        // Anonymous, no commands or account token. Existing Ping/Pong also works across browser origins.
        public static IEnumerator Run(ServerDirectoryEntry entry, Action<ServerProbeResult> completed)
        {
            using var cancel = new CancellationTokenSource();
            using var socket = WireSocket.Create(new WireSocketIdentity(null, string.Empty, false));
            var deadline = Time.realtimeSinceStartupAsDouble + 8;
            Task pending = socket.ConnectAsync(new Uri(entry.socketUrl), cancel.Token);
            try
            {
                while (!pending.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                if (!Succeeded(pending)) { completed(new ServerProbeResult(-1, 0)); yield break; }
                var token = Stopwatch.GetTimestamp();
                pending = socket.SendAsync(Frame.Ping(token), cancel.Token);
                while (!pending.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                if (!Succeeded(pending)) { completed(new ServerProbeResult(-1, 0)); yield break; }
                var protocol = 0;
                while (Time.realtimeSinceStartupAsDouble < deadline)
                {
                    var receive = socket.ReceiveMessageAsync(() => { }, cancel.Token);
                    pending = receive;
                    while (!receive.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                    if (!Succeeded(receive)) break;
                    var bytes = Platform.CompletedTask.Result(receive);
                    if (bytes == null) break;
                    if (bytes.Length >= 5 && bytes[0] == (byte)FrameKind.Handshake) protocol = BitConverter.ToInt32(bytes, 1);
                    if (bytes.Length == 9 && bytes[0] == (byte)FrameKind.Pong && BitConverter.ToInt64(bytes, 1) == token)
                    {
                        var ping = (int)Math.Max(1, (Stopwatch.GetTimestamp() - token) * 1000.0 / Stopwatch.Frequency);
                        completed(new ServerProbeResult(protocol > 0 ? ping : -1, protocol)); yield break;
                    }
                }
                completed(new ServerProbeResult(-1, protocol));
            }
            finally
            {
                cancel.Cancel(); socket.Abort();
                // A socket cancelled after its menu detached may fault later; observe without a thread-pool continuation.
                pending.GetAwaiter().OnCompleted(() => { if (pending.IsFaulted) _ = pending.Exception; });
            }
        }

        private static bool Succeeded(Task task)
        {
            if (task.IsFaulted) _ = task.Exception;
            return task.Status == TaskStatus.RanToCompletion;
        }
    }
}
