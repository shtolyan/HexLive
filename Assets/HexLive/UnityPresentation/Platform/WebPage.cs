#nullable enable
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace HexLive.UnityPresentation.Platform
{

/// <summary>
/// §168.5: what the page around a web build can and cannot do. The desktop
/// answers are the old behaviour, so callers branch on a property instead of
/// sprinkling <c>#if UNITY_WEBGL</c> through UI code.
/// </summary>
public static class WebPage
{
#if UNITY_WEBGL
    /// <summary>Presentation follows the web even in the editor on the WebGL
    /// platform — it previews what the browser will show (§168.2, rule of
    /// defines).</summary>
    public const bool IsWeb = true;
#else
    public const bool IsWeb = false;
#endif

    /// <summary>A browser tab is closed by the player, never by the page:
    /// <c>Application.Quit</c> there does nothing, so «Quit» is not offered.</summary>
    public static bool CanQuit => !IsWeb;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void HexPageReload();
    [DllImport("__Internal")] private static extern int HexAudioSampleRate();
    [DllImport("__Internal")] private static extern string? HexAccountSession();
    [DllImport("__Internal")] private static extern string? HexCreatorStoreGet(string key);
    [DllImport("__Internal")] private static extern int HexCreatorStoreSet(string key, string value);
    [DllImport("__Internal")] private static extern int HexCreatorLockStart(string key);
    [DllImport("__Internal")] private static extern int HexCreatorLockStatus(string key);
    [DllImport("__Internal")] private static extern void HexCreatorLockRelease(string key);
#endif

    /// <summary>
    /// §171.12: the player's account session stored by the server's /login page
    /// (Firebase sign-in → /api/accounts/v1/session). It is the player identity of
    /// the web client: the /watch token (§168.2) and the voice Bearer (§171.11).
    /// Null off the web, before sign-in, or after it expired.
    /// </summary>
    public static string? AccountSessionToken
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var token = HexAccountSession();
            return string.IsNullOrEmpty(token) ? null : token;
#else
            return null;
#endif
        }
    }

    /// <summary>§168.6: the browser's real audio output rate; 0 when unknown or off the web.</summary>
    public static int AudioSampleRate
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return HexAudioSampleRate();
#else
            return 0;
#endif
        }
    }

    /// <summary>
    /// §168.10: the game server of the page itself — the web build is served
    /// by the same host as <c>/watch</c>, so <c>https://host/play/</c> means
    /// <c>wss://host/watch</c>. Null outside the browser or for a page opened
    /// from disk.
    /// </summary>
    public static string? PageServerUrl
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!System.Uri.TryCreate(UnityEngine.Application.absoluteURL, System.UriKind.Absolute, out var page) ||
                (page.Scheme != "https" && page.Scheme != "http"))
            {
                return null;
            }

            var socket = new System.UriBuilder(page)
            {
                Scheme = page.Scheme == "https" ? "wss" : "ws",
                Path = "/watch",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return socket.Uri.AbsoluteUri;
#else
            return null;
#endif
        }
    }

    /// <summary>Updating a web client IS reloading the page (§168.5).</summary>
    public static void Reload()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        HexPageReload();
#endif
    }

    // §171.20: draft/immutable creation attempt only, never a credential store.
    public static string? CreatorRead(string key)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return HexCreatorStoreGet(key);
#else
        return UnityEngine.PlayerPrefs.GetString(key, "");
#endif
    }
    public static bool CreatorWrite(string key, string value)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return HexCreatorStoreSet(key, value ?? "") == 1;
#else
        if (string.IsNullOrEmpty(value)) UnityEngine.PlayerPrefs.DeleteKey(key);
        else UnityEngine.PlayerPrefs.SetString(key, value);
        UnityEngine.PlayerPrefs.Save(); return true;
#endif
    }
#if !UNITY_WEBGL || UNITY_EDITOR
    private static readonly System.Collections.Generic.HashSet<string> CreatorLocks = new();
#endif
    public static int CreatorAcquire(string key)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return HexCreatorLockStart(key);
#else
        return CreatorLocks.Add(key) ? 1 : 2;
#endif
    }
    public static int CreatorLockState(string key)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return HexCreatorLockStatus(key);
#else
        return CreatorLocks.Contains(key) ? 1 : 2;
#endif
    }
    public static void CreatorRelease(string key)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        HexCreatorLockRelease(key);
#else
        CreatorLocks.Remove(key);
#endif
    }
}

}
