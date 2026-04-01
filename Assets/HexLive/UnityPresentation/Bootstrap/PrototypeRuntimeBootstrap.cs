using HexLive.Simulation.Bootstrap;
using UnityEngine;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;

namespace HexLive.UnityPresentation.Bootstrap
{

public static class PrototypeRuntimeBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var existingRunner = Object.FindFirstObjectByType<SimulationRunnerBehaviour>();
        if (existingRunner is not null)
        {
            return;
        }

        var root = new GameObject("HexLive Prototype");
        var runner = root.AddComponent<SimulationRunnerBehaviour>();
        runner.Configure(PrototypeWorldDefinitionFactory.Create(), startPaused: true, initialSpeed: 1f);

        var renderer = root.AddComponent<HexWorldRenderer>();
        renderer.SetRunner(runner);

        InstallCamera(runner);
    }

    private static void InstallCamera(SimulationRunnerBehaviour runner)
    {
        var mainCamera = Camera.main;
        if (mainCamera == null)
        {
            return;
        }

        // Disable orbit camera if present
        var orbit = mainCamera.GetComponent<OrbitCameraController>();
        if (orbit != null)
        {
            orbit.enabled = false;
        }

        // Install RTS camera
        var rts = mainCamera.GetComponent<RtsCameraController>();
        if (rts == null)
        {
            rts = mainCamera.gameObject.AddComponent<RtsCameraController>();
        }
    }
}

}
