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
#endif

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
}

}
