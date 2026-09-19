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
    // §83: сетевая репетиция — задержка+джиттер поверх реального сокета, чтобы
    // сингапурский пинг воспроизводился на localhost. Формат
    // "<baseMs>:<jitterMs>[:seed]"; без аргумента симулятора нет вовсе.
    private const string NetSimArgument = "-hexlive-netsim";
    private const string ClientIdPref = "HexLive.RemoteClientId";
    // §121.9: явный, ВИДИМЫЙ источник личности этого клиента, главнее реестра.
    // PlayerPrefs (реестр Windows / defaults на mac) непрозрачен и его легко
    // оставить в чужом значении руками или сторонним инструментом — тогда клиент
    // молча играет за не тех NPC. Аргумент/файл делают id тем, что можно открыть
    // глазами и положить в репозиторий рядом со сборкой.
    //   -hexlive-client-id <32-hex>            — на один запуск;
    //   hexlive-client-id.txt рядом с exe      — на установку.
    // Оба нормализуются как канонический GUID "N"; кривое значение игнорируется.
    private const string ClientIdArgument = "-hexlive-client-id";
    private const string ClientIdFileName = "hexlive-client-id.txt";

    private static bool _resolved;
    private static SimulationMode _mode = SimulationMode.Local;
    private static string? _serverUrl;
    private static string? _controlToken;
    private static (double BaseMs, double JitterMs, int Seed)? _netSim;
    private static string? _forcedClientId;

    public static event Action? ServerChanged;

    public static void UseAccount(string accountId)
    {
        Resolve();
        if (!Guid.TryParseExact(accountId, "N", out var id)) throw new ArgumentException(nameof(accountId));
        _forcedClientId = id.ToString("N");
        PlayerPrefs.SetString(ClientIdPref, _forcedClientId);
        PlayerPrefs.Save();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _resolved = false;
        _mode = SimulationMode.Local;
        _serverUrl = null;
        _controlToken = null;
        _netSim = null;
        _forcedClientId = null;
        ServerChanged = null;
    }

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

    /// <summary>§83: параметры <c>-hexlive-netsim</c>; null — обычная сессия.</summary>
    public static (double BaseMs, double JitterMs, int Seed)? NetSim
    {
        get
        {
            Resolve();
            return _netSim;
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
            Resolve();

            // §121.9: явный источник (аргумент или файл рядом с игрой) главнее
            // непрозрачного PlayerPrefs. Если он задан — им же приводим и реестр
            // в согласие, чтобы читатели реестра (Agent Studio) видели тот же id,
            // и играем именно за него. Это и есть «взять фиксированный id и
            // загрузиться», без зависимости от того, что лежит в реестре сейчас.
            var forced = _forcedClientId ?? (ClosedTestAccess.Enabled ? null : ReadClientIdFile());
            if (!string.IsNullOrEmpty(forced))
            {
                if (!string.Equals(PlayerPrefs.GetString(ClientIdPref, string.Empty),
                        forced, StringComparison.Ordinal))
                {
                    PlayerPrefs.SetString(ClientIdPref, forced);
                    PlayerPrefs.Save();
                }

                return forced!;
            }

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
    /// §121.9: id из файла <c>hexlive-client-id.txt</c> рядом с игрой (папка над
    /// <c>*_Data</c>, где лежит exe), либо null — файла нет / значение кривое.
    /// Видимый на диске источник личности установки; в редакторе смотрит в корень
    /// проекта, где его обычно нет, и тихо отдаёт null.
    /// </summary>
    private static string? ReadClientIdFile()
    {
        try
        {
            var root = System.IO.Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var path = System.IO.Path.Combine(root!, ClientIdFileName);
            return System.IO.File.Exists(path)
                ? NormalizeClientId(System.IO.File.ReadAllText(path))
                : null;
        }
        catch (Exception)
        {
            // Нечитаемый/недоступный файл — не повод падать: молча к PlayerPrefs.
            return null;
        }
    }

    // Канонический 32-символьный GUID "N" или null. Пробелы/переводы строк из
    // файла и регистр значения не мешают; всё, что не GUID, — игнорируется.
    private static string? NormalizeClientId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Guid.TryParseExact(raw.Trim(), "N", out var id) ? id.ToString("N") : null;
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
        var previousMode = _mode;
        var previousUrl = _serverUrl;
        if (!string.IsNullOrWhiteSpace(controlToken))
        {
            _controlToken = ResolveToken(controlToken!);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            _mode = SimulationMode.Local;
            _serverUrl = null;
            NotifyServerChanged(previousMode, previousUrl);
            return;
        }

        _mode = SimulationMode.Remote;
        _serverUrl = ServerBook.Normalize(url);
        NotifyServerChanged(previousMode, previousUrl);
    }

    private static void NotifyServerChanged(SimulationMode previousMode, string? previousUrl)
    {
        if (previousMode != _mode ||
            !string.Equals(previousUrl, _serverUrl, StringComparison.OrdinalIgnoreCase))
        {
            ServerChanged?.Invoke();
        }
    }

    private static void Resolve()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        if (ClosedTestAccess.Enabled) return;

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

            if (i + 1 < args.Length &&
                string.Equals(args[i], ClientIdArgument, StringComparison.OrdinalIgnoreCase))
            {
                // §121.9: принудительный id этого запуска. Кривое значение
                // отбрасывается (null) — тогда сработает файл/реестр.
                _forcedClientId = NormalizeClientId(args[i + 1]);
                if (_forcedClientId != null)
                {
                    Debug.Log($"[HexLive] Client id forced by {ClientIdArgument}: {_forcedClientId}");
                }

                continue;
            }

            if (i + 1 < args.Length &&
                string.Equals(args[i], NetSimArgument, StringComparison.OrdinalIgnoreCase))
            {
                _netSim = ParseNetSim(args[i + 1]);
                if (_netSim is { } sim)
                {
                    Debug.Log($"[HexLive] NetSim: base={sim.BaseMs}ms " +
                              $"jitter={sim.JitterMs}ms seed={sim.Seed}");
                }

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
            _serverUrl = ServerBook.Normalize(url);
            Debug.Log($"[HexLive] Session mode: Remote ({_serverUrl})");
        }
    }

    // "<baseMs>:<jitterMs>[:seed]" — например "55:25" или "55:25:7". Кривое
    // значение = симулятора нет: репетиционный флаг не должен уметь ронять
    // обычный запуск.
    private static (double BaseMs, double JitterMs, int Seed)? ParseNetSim(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 ||
            !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var baseMs) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var jitterMs))
        {
            Debug.LogWarning($"[HexLive] Ignoring malformed {NetSimArgument} '{value}' " +
                             "(expected <baseMs>:<jitterMs>[:seed]).");
            return null;
        }

        var seed = 1;
        if (parts.Length == 3 && !int.TryParse(parts[2], out seed))
        {
            seed = 1;
        }

        return (baseMs, jitterMs, seed);
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
