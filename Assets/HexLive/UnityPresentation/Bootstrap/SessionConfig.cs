#nullable enable
using System;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{

public enum SimulationMode
{
    /// <summary>
    /// The world runs in this process. The default, and fully self-contained:
    /// no server, no network, no configuration. This is what the game has
    /// always done and what it must keep doing when nothing is set up.
    /// </summary>
    Local,

    /// <summary>
    /// Local world, but read back through the wire codec. A rehearsal mode for
    /// the editor and CI: it makes a field forgotten in the codec visible on the
    /// first Play, without standing up a server. Never ship a session in it.
    /// </summary>
    Loopback,

    /// <summary>
    /// The world runs somewhere else and this process only watches it.
    /// </summary>
    Remote,
}

/// <summary>
/// Which simulation this session talks to.
/// <para>
/// Resolved once, from the command line: <c>-hexlive-server ws://host:port</c>
/// for a remote world, <c>-hexlive-loopback</c> for the codec rehearsal. Neither
/// (the normal case) means <see cref="SimulationMode.Local"/> — a build with no
/// server anywhere still starts and plays, which is the whole point of keeping
/// the local backend first-class.
/// </para>
/// </summary>
public static class SessionConfig
{
    private const string ServerArgument = "-hexlive-server";
    private const string LoopbackArgument = "-hexlive-loopback";

    private static bool _resolved;
    private static SimulationMode _mode = SimulationMode.Local;
    private static string? _serverUrl;

    public static SimulationMode Mode
    {
        get
        {
            Resolve();
            return _mode;
        }
    }

    /// <summary>The server to watch; null in <see cref="SimulationMode.Local"/>.</summary>
    public static string? ServerUrl
    {
        get
        {
            Resolve();
            return _serverUrl;
        }
    }

    /// <summary>
    /// Force a mode at runtime (a main-menu "connect to server" entry, or a test
    /// harness). Passing a null/blank url returns the session to local.
    /// </summary>
    public static void UseServer(string? url)
    {
        _resolved = true;
        if (string.IsNullOrWhiteSpace(url))
        {
            _mode = SimulationMode.Local;
            _serverUrl = null;
            return;
        }

        _mode = SimulationMode.Remote;
        _serverUrl = url;
    }

    private static void Resolve()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;

        string[] args;
        try
        {
            args = Environment.GetCommandLineArgs();
        }
        catch (Exception)
        {
            // Some platforms refuse the command line outright. Local is the
            // safe answer: the game runs, it just runs its own world.
            return;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], LoopbackArgument, StringComparison.OrdinalIgnoreCase))
            {
                _mode = SimulationMode.Loopback;
                Debug.Log("[HexLive] Session mode: Loopback (local world, read back through the wire codec)");
                return;
            }

            if (i + 1 >= args.Length ||
                !string.Equals(args[i], ServerArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = args[i + 1];
            if (string.IsNullOrWhiteSpace(url))
            {
                break;
            }

            _mode = SimulationMode.Remote;
            _serverUrl = url;
            Debug.Log($"[HexLive] Session mode: Remote ({url})");
            return;
        }
    }
}

}
