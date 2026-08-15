#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{
    /// <summary>
    /// The desktop Player has one authored presentation target: Full HD in a
    /// borderless full-screen window. Apply it on every process launch rather
    /// than trusting Unity's persisted player preferences from an older build.
    /// </summary>
    internal static class DisplayStartupPolicy
    {
        internal const int Width = 1920;
        internal const int Height = 1080;
        private const string NativeResolutionArgument = "-hexlive-native-resolution";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            if (Application.isEditor)
            {
                return;
            }

            if (System.Array.Exists(
                    System.Environment.GetCommandLineArgs(),
                    argument => string.Equals(
                        argument,
                        NativeResolutionArgument,
                        System.StringComparison.Ordinal)))
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
                return;
            }

            Screen.SetResolution(Width, Height, FullScreenMode.FullScreenWindow);
            Debug.Log($"[Display] Full HD fullscreen requested: {Width}x{Height}.");
        }
    }
}
