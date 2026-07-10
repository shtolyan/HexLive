using HexLive.UnityPresentation.Bootstrap;
using UnityEngine;

namespace HexLive.UnityDebug.UI
{

public static class PrototypeDebugBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var existing = Object.FindAnyObjectByType<SimulationDebugPanel>();
        if (existing is not null)
        {
            return;
        }

        var runner = Object.FindAnyObjectByType<SimulationRunnerBehaviour>();

        // Debug HUD + controls panel
        var debugRoot = new GameObject("HexLive Debug");
        var panel = debugRoot.AddComponent<SimulationDebugPanel>();
        if (runner != null)
        {
            panel.SetRunner(runner);
        }

        // Point overlay (separate UIDocument on its own GameObject)
        var overlayRoot = new GameObject("HexLive Point Overlay");
        var overlay = overlayRoot.AddComponent<PointOverlayView>();
        if (runner != null)
        {
            overlay.SetRunner(runner);
        }
    }
}

}
