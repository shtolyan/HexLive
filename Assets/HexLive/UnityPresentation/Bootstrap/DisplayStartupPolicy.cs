#nullable enable
using System.Collections;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{
    /// <summary>
    /// The desktop Player has one authored presentation target: Full HD in
    /// points, full screen. On a display whose own modes are 16:10 the frame is
    /// letterboxed — black bars above and below — which is the intended look:
    /// exactly 1920x1080 rendered points, never the panel's native pixel count.
    /// Apply it on every process launch rather than trusting Unity's persisted
    /// player preferences from an older build.
    ///
    /// The policy also REPORTS WHAT IT GOT, not only what it asked for. The
    /// earlier version logged the request alone, so a request the platform
    /// silently declined read exactly like a request it honoured.
    /// </summary>
    internal static class DisplayStartupPolicy
    {
        internal const int Width = 1920;
        internal const int Height = 1080;
        private const string NativeResolutionArgument = "-hexlive-native-resolution";
        private const string BorderlessArgument = "-hexlive-borderless";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            if (Application.isEditor)
            {
                return;
            }

            if (HasArgument(NativeResolutionArgument))
            {
                var display = Display.main;
                var nativeWidth = Mathf.Max(1, display.systemWidth);
                var nativeHeight = Mathf.Max(1, display.systemHeight);
                Screen.SetResolution(
                    nativeWidth,
                    nativeHeight,
                    FullScreenMode.FullScreenWindow);
                Debug.Log(
                    $"[Display] Native fullscreen requested: {nativeWidth}x{nativeHeight}.");
                ReportWhenApplied($"native {nativeWidth}x{nativeHeight} FullScreenWindow", 0, 0);
                return;
            }

            // Borderless keeps the desktop's own surface and lets the platform
            // decide the pixel count — the v0.1.54 behaviour, kept as an escape
            // hatch so a wrong default costs a relaunch, not a rebuild.
            if (HasArgument(BorderlessArgument))
            {
                Screen.SetResolution(Width, Height, FullScreenMode.FullScreenWindow);
                Debug.Log($"[Display] Full HD borderless requested: {Width}x{Height}.");
                ReportWhenApplied($"borderless {Width}x{Height}", Width, Height);
                return;
            }

            // Exclusive full screen is the mode that honours an exact resolution:
            // the frame is rendered at 1920x1080 and letterboxed into whatever
            // mode the display is in. Borderless instead adopts the desktop
            // surface, which on a Retina panel is several times Full HD.
            Screen.SetResolution(Width, Height, FullScreenMode.ExclusiveFullScreen);
            Debug.Log($"[Display] Full HD fullscreen requested: {Width}x{Height}.");
            ReportWhenApplied($"exclusive {Width}x{Height}", Width, Height);
        }

        // System.Environment must be spelled out: the project has its own
        // HexLive.UnityPresentation.Environment namespace, which wins over a
        // bare `Environment` inside this namespace.
        private static bool HasArgument(string argument) =>
            System.Array.Exists(
                System.Environment.GetCommandLineArgs(),
                candidate => string.Equals(candidate, argument, System.StringComparison.Ordinal));

        private static void ReportWhenApplied(string requested, int wantWidth, int wantHeight)
        {
            var host = new GameObject("HexLiveDisplayReport") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<DisplayStartupReport>().Begin(requested, wantWidth, wantHeight);
        }

        private sealed class DisplayStartupReport : MonoBehaviour
        {
            internal void Begin(string requested, int wantWidth, int wantHeight) =>
                StartCoroutine(ReportAfterSettling(requested, wantWidth, wantHeight));

            private IEnumerator ReportAfterSettling(string requested, int wantWidth, int wantHeight)
            {
                // A resolution change lands over the following frames; reading it
                // in the same frame reports the old surface.
                for (var frame = 0; frame < 10; frame++)
                {
                    yield return null;
                }

                var display = Display.main;
                var current = Screen.currentResolution;
                Debug.Log(
                    $"[Display] APPLIED requested={requested} -> Screen={Screen.width}x{Screen.height} " +
                    $"mode={Screen.fullScreenMode} dpi={Screen.dpi} " +
                    $"rendering={display.renderingWidth}x{display.renderingHeight} " +
                    $"system={display.systemWidth}x{display.systemHeight} " +
                    $"currentResolution={current.width}x{current.height}@{current.refreshRateRatio.value:0.##}Hz");

                if (wantWidth > 0 && (Screen.width != wantWidth || Screen.height != wantHeight))
                {
                    Debug.LogWarning(
                        $"[Display] NOT Full HD: asked {wantWidth}x{wantHeight}, got " +
                        $"{Screen.width}x{Screen.height} — the platform declined the request.");
                }

                Destroy(gameObject);
            }
        }
    }
}
