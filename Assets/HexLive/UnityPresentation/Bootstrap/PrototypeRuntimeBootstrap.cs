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
        var existingRunner = Object.FindAnyObjectByType<SimulationRunnerBehaviour>();
        if (existingRunner is not null)
        {
            return;
        }

        var root = new GameObject("HexLive Prototype");
        var runner = root.AddComponent<SimulationRunnerBehaviour>();

        // Presentation-side randomness (spec 29C.1): each play-mode session
        // gets a fresh seed; the simulation itself stays deterministic per seed.
        var seed = System.Environment.TickCount;
        Debug.Log($"[HexLive] World seed: {seed}");
        // Spec 31.13: play mode drops straight into a живой мир — the
        // simulation ticks at normal speed from frame one.
        runner.Configure(PrototypeWorldDefinitionFactory.Create(seed), startPaused: false, initialSpeed: 1f);

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

        rts.SetRunner(runner);
    }
}

}
