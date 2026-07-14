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
        // Apply the saved tuning asset (hop/swim/water feel) before anything
        // spawns — the values that used to be hand-edited code constants.
        Config.HexTuning.LoadAndApply();

        // Spec §42: load the wearable wardrobe from the GarmentCatalog asset
        // into GarmentLibrary before the world (and its content) is built.
        Config.GarmentTuning.LoadAndApply();

        var existingRunner = Object.FindAnyObjectByType<SimulationRunnerBehaviour>();
        if (existingRunner is not null)
        {
            return;
        }

        // Dev scenes (wardrobe test etc.) own themselves — the game world
        // must not boot on top of them.
        if (Object.FindAnyObjectByType<WardrobeTest.WardrobeTestBootstrap>() is not null)
        {
            return;
        }

        if (Object.FindAnyObjectByType<ShiverTest.ShiverTestBootstrap>() is not null)
        {
            return;
        }

        if (Object.FindAnyObjectByType<AxeChopTest.AxeChopTestBootstrap>() is not null)
        {
            return;
        }

        var root = new GameObject("HexLive Prototype");
        var runner = root.AddComponent<SimulationRunnerBehaviour>();

        // Spec 41.4: the world is NOT configured here — the loading screen
        // opens as a menu (Continue / New game) and bootstraps the chosen
        // world itself; everything below guards on runner.IsReady.
        var renderer = root.AddComponent<HexWorldRenderer>();
        renderer.SetRunner(runner);

        // Spec 20.16: stylized sky + sun/moon day-night lighting.
        var sky = root.AddComponent<HexLive.UnityPresentation.Environment.SkyDayNightController>();
        sky.SetRunner(runner);

        InstallCamera(runner);
        InstallCharacterUi(runner);

        // Spec 41.1/41.4: the loading curtain owns the rest — menu, world
        // bootstrap, replay, view spawn, warm-up, fade, unpause, Jana.
        var loaderRoot = new GameObject("HexLive Loading Screen");
        var loader = loaderRoot.AddComponent<LoadingScreen>();
        loader.Begin(runner);
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

        // Escape menu (continue / quit) — Escape with nothing selected.
        var menuRoot = new GameObject("HexLive Game Menu");
        menuRoot.AddComponent<UIDocument>();
        var menu = menuRoot.AddComponent<GameMenu>();
        menu.SetRunner(runner);
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
