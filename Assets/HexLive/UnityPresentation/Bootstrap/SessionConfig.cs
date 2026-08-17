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
    // §121.9: токен игрока для управления колонисткой на сервере. Значение —
    // сам токен либо путь к файлу с ним (hexlive-player.txt рядом с сейвом
    // сервера); без аргумента — прежний анонимный зритель.
    private const string TokenArgument = "-hexlive-token";
    private const string ClientIdPref = "HexLive.RemoteClientId";

    private static bool _resolved;
    private static SimulationMode _mode = SimulationMode.Local;
    private static string? _serverUrl;
    private static string? _controlToken;

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

    /// <summary>§121.9: токен игрока для NpcCommand; null — аноним.</summary>
    public static string? ControlToken
    {
        get
        {
            Resolve();
            return _controlToken;
        }
    }

    /// <summary>
    /// §121.9: стабильный id этого клиента — owner лиза (<c>ws:&lt;id&gt;</c>)
    /// переживает реконнект и перезапуск игры. Генерируется один раз и живёт
    /// в PlayerPrefs.
    /// </summary>
    public static string ClientId
    {
        get
        {
            var existing = PlayerPrefs.GetString(ClientIdPref, string.Empty);
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            var fresh = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(ClientIdPref, fresh);
            PlayerPrefs.Save();
            return fresh;
        }
    }

    /// <summary>
    /// Force a mode at runtime (a main-menu "connect to server" entry, or a test
    /// harness). Passing a null/blank url returns the session to local.
    /// <para>
    /// ⭐ §145.3: сначала Resolve() — иначе выбор в меню затирал ВЕСЬ разбор
    /// командной строки, включая <c>-hexlive-token</c>: игрок запускал клиент
    /// с токеном, кликал «подключиться» и молча становился зрителем. Токен из
    /// меню (если дан) главнее CLI; пустое поле меню оставляет CLI-токен жить.
    /// </para>
    /// </summary>
    public static void UseServer(string? url, string? controlToken = null)
    {
        Resolve();
        if (!string.IsNullOrWhiteSpace(controlToken))
        {
            _controlToken = ResolveToken(controlToken!);
        }

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
            // Fully qualified on purpose: this file sits in
            // HexLive.UnityPresentation.Bootstrap, and the sibling NAMESPACE
            // HexLive.UnityPresentation.Environment (CampfireEffect, the sky
            // controller, TreeFall) shadows System.Environment from here.
            args = System.Environment.GetCommandLineArgs();
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

            if (i + 1 < args.Length &&
                string.Equals(args[i], TokenArgument, StringComparison.OrdinalIgnoreCase))
            {
                _controlToken = ResolveToken(args[i + 1]);
                continue;
            }

            if (i + 1 >= args.Length ||
                !string.Equals(args[i], ServerArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = args[i + 1];
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            _mode = SimulationMode.Remote;
            _serverUrl = url;
            Debug.Log($"[HexLive] Session mode: Remote ({url})");
        }
    }

    // Значение аргумента — сам токен ЛИБО путь к файлу с ним: сервер кладёт
    // hexlive-player.txt рядом с сейвом, и удобнее сослаться на файл, чем
    // копировать строку (заодно токен не светится в списке процессов).
    private static string? ResolveToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            if (System.IO.File.Exists(value))
            {
                var content = System.IO.File.ReadAllText(value).Trim();
                return string.IsNullOrEmpty(content) ? null : content;
            }
        }
        catch (Exception)
        {
            // Нечитаемый файл — попробуем как literal-токен ниже.
        }

        return value.Trim();
    }
}

}
