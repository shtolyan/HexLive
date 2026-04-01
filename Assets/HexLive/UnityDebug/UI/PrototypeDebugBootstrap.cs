using HexLive.UnityPresentation.Bootstrap;
using UnityEngine;

namespace HexLive.UnityDebug.UI
{

public static class PrototypeDebugBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var existing = Object.FindFirstObjectByType<SimulationDebugPanel>();
        if (existing is not null)
        {
            return;
        }

        var root = new GameObject("HexLive Debug");
        var panel = root.AddComponent<SimulationDebugPanel>();
        var runner = Object.FindFirstObjectByType<SimulationRunnerBehaviour>();
        if (runner != null)
        {
            panel.SetRunner(runner);
        }
    }
}

}
