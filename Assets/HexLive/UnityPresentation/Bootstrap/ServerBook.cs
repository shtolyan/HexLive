#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{

/// <summary>
/// The servers this player has watched, most recent first, and which kind of
/// session they were in last.
/// <para>
/// Exists so "Continue" means the same thing it means everywhere else: put me
/// back where I was. If that was a colony on a server, retyping its address to
/// get back to it would be a strange thing to ask.
/// </para>
/// <para>
/// PlayerPrefs rather than a file: it is a handful of short strings, it is
/// per-machine by nature, and it must survive a build that has no write access
/// to the game directory.
/// </para>
/// </summary>
public static class ServerBook
{
    private const string RecentKey = "HexLive.Servers.Recent";
    private const string LastUrlKey = "HexLive.Servers.LastUrl";
    private const string LastWasRemoteKey = "HexLive.Servers.LastWasRemote";

    /// <summary>Deliberately short. A long history of dead addresses is clutter, not a feature.</summary>
    private const int MaxRemembered = 6;

    // '\n' cannot appear in a URL, so it is a safe separator and keeps the
    // stored value readable if anyone ever inspects the prefs.
    private const char Separator = '\n';

    public static string DefaultUrl => "ws://localhost:5123/watch";

    /// <summary>Was the last session spent watching a server?</summary>
    public static bool LastSessionWasRemote => PlayerPrefs.GetInt(LastWasRemoteKey, 0) == 1;

    /// <summary>The server the last session was on, or null.</summary>
    public static string? LastUrl
    {
        get
        {
            var url = PlayerPrefs.GetString(LastUrlKey, string.Empty);
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
    }

    public static List<string> Recent()
    {
        var stored = PlayerPrefs.GetString(RecentKey, string.Empty);
        var result = new List<string>();
        if (string.IsNullOrEmpty(stored))
        {
            return result;
        }

        foreach (var entry in stored.Split(Separator))
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Records a successful-enough connect attempt and moves it to the top.</summary>
    public static void Remember(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var recent = Recent();
        recent.Remove(url);
        recent.Insert(0, url);
        while (recent.Count > MaxRemembered)
        {
            recent.RemoveAt(recent.Count - 1);
        }

        PlayerPrefs.SetString(RecentKey, string.Join(Separator.ToString(), recent));
        PlayerPrefs.SetString(LastUrlKey, url);
        PlayerPrefs.SetInt(LastWasRemoteKey, 1);
        PlayerPrefs.Save();
    }

    public static void Forget(string url)
    {
        var recent = Recent();
        if (!recent.Remove(url))
        {
            return;
        }

        PlayerPrefs.SetString(RecentKey, string.Join(Separator.ToString(), recent));
        if (LastUrl == url)
        {
            PlayerPrefs.SetInt(LastWasRemoteKey, 0);
        }

        PlayerPrefs.Save();
    }

    /// <summary>Called when a LOCAL game starts, so Continue stops offering the server.</summary>
    public static void RememberLocalSession()
    {
        PlayerPrefs.SetInt(LastWasRemoteKey, 0);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Turns what a person types into a URL. Hosts, host:port and bare IPs are
    /// all reasonable things to paste, and none of them should be rejected for
    /// missing a scheme or a path.
    /// </summary>
    public static string Normalize(string raw)
    {
        var url = (raw ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return url;
        }

        if (!url.Contains("://"))
        {
            url = "ws://" + url;
        }

        var afterScheme = url.IndexOf("://", System.StringComparison.Ordinal) + 3;
        if (url.IndexOf('/', afterScheme) < 0)
        {
            url += "/watch";
        }

        return url;
    }

    /// <summary>Just the host:port, for a menu row that should not be 40 characters wide.</summary>
    public static string ShortLabel(string url)
    {
        var text = url ?? string.Empty;
        var scheme = text.IndexOf("://", System.StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text.Substring(scheme + 3);
        }

        var slash = text.IndexOf('/');
        return slash > 0 ? text.Substring(0, slash) : text;
    }
}

}
