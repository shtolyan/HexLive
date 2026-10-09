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

    /// <summary>Updating a web client IS reloading the page (§168.5).</summary>
    public static void Reload()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        HexPageReload();
#endif
    }
}

}
