using HexLive.Simulation.Bootstrap;
using UnityEngine;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.UI;
using UnityEngine.UIElements;

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

        // Spec 20.16: stylized sky + sun/moon day-night lighting.
        var sky = root.AddComponent<HexLive.UnityPresentation.Environment.SkyDayNightController>();
        sky.SetRunner(runner);

        InstallCamera(runner);
        InstallCharacterUi(runner);
    }

    // Live-portrait stage + the Sims-style character panel. The panel appears
    // when an NPC is selected (spec: click a character to inspect it).
    private static void InstallCharacterUi(SimulationRunnerBehaviour runner)
    {
        if (Object.FindAnyObjectByType<CharacterPanel>() != null)
        {
            return;
        }

        var stageRoot = new GameObject("HexLive Portrait Stage");
        var portraitStage = stageRoot.AddComponent<PortraitStage>();

        var panelRoot = new GameObject("HexLive Character Panel");
        var document = panelRoot.AddComponent<UIDocument>();
        document.panelSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");

        var panel = panelRoot.AddComponent<CharacterPanel>();
        panel.SetRunner(runner);
        panel.SetPortraitStage(portraitStage);

        // Always-visible time controls (pause / play / speed) at the top.
        var speedRoot = new GameObject("HexLive Speed Bar");
        speedRoot.AddComponent<UIDocument>();
        var speedBar = speedRoot.AddComponent<SimSpeedBar>();
        speedBar.SetRunner(runner);

        // Left-side debug buttons: wound / clear / dirtier / cleaner.
        var debugRoot = new GameObject("HexLive Debug Controls");
        debugRoot.AddComponent<UIDocument>();
        var debugPanel = debugRoot.AddComponent<DebugControlsPanel>();
        debugPanel.SetRunner(runner);
    }

    private static void InstallCamera(SimulationRunnerBehaviour runner)
    {
        var mainCamera = Camera.main;
        if (mainCamera == null)
        {
            return;
        }

        // The portrait stage lives on its own layer rendered only by the
        // portrait camera — keep it out of the main view.
        var portraitLayer = LayerMask.NameToLayer("Portrait");
        if (portraitLayer >= 0)
        {
            mainCamera.cullingMask &= ~(1 << portraitLayer);
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
