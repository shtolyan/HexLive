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
    private static bool _sceneHookInstalled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        // RuntimeInitializeOnLoadMethod fires ONCE at app startup, not on scene
        // reloads. The Escape-menu "return to main menu" reloads the active
        // scene, so we also (re)bootstrap on every scene load — otherwise the
        // reloaded scene comes up empty (world gone, no menu UI). The
        // existing-runner guard in Boot() prevents a double-boot on first load.
        if (!_sceneHookInstalled)
        {
            _sceneHookInstalled = true;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, __) => Boot();
        }

        Boot();
    }

    private static void Boot()
    {
        // Apply the saved tuning asset (hop/swim/water feel) before anything
        // spawns — the values that used to be hand-edited code constants.
        // Idempotent (each catalog clears/overrides), so re-running per scene
        // load is safe.
        Config.HexTuning.LoadAndApply();

        // §59: the themed balance configs (Character / ResourceLoop / Social /
        // Threat) from Resources/HexLive/Balance → SimBalance + Spec statics
        // via the reflection mirror.
        Config.BalanceTuning.LoadAndApply();

        // Spec §42: load the wearable wardrobe from the GarmentCatalog asset
        // into GarmentLibrary before the world (and its content) is built.
        Config.GarmentTuning.LoadAndApply();

        // Per-mob combat/behaviour from the MobConfig assets (one per mob) into
        // MobCatalog, before the world spawns any creatures.
        Config.MobTuning.LoadAndApply();

        // Per-gear (weapon+tool) sheets from the GearConfig assets (one per
        // item) into GearCatalog + GearLibrary (prefabs, animations).
        Config.GearTuning.LoadAndApply();

        // Per-world-object action sheets (skills → yields) from the
        // WorldObjectConfig assets into WorldObjectLibrary (merged into the
        // content catalog when a world is built).
        Config.ObjectTuning.LoadAndApply();

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

        // Spec §67: FMOD-backed sound — sim-event one-shots + island ambience.
        var sound = root.AddComponent<Audio.SoundManager>();
        sound.Construct(runner, renderer);

        // Spec §70: музыка. Сама решает, что играть и когда молчать; режим
        // (меню / игра) читает у загрузочной шторки, поэтому проводов нет.
        root.AddComponent<Audio.MusicDirector>();

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

        // Spec §57: the limb-health body doll — its own staged clone + camera
        // on the hidden Portrait layer, far outside the world.
        var dollRoot = new GameObject("HexLive Health Doll Stage");
        var healthDollStage = dollRoot.AddComponent<HealthDollStage>();

        var panelRoot = new GameObject("HexLive Character Panel");
        var document = panelRoot.AddComponent<UIDocument>();
        document.panelSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");

        var panel = panelRoot.AddComponent<CharacterPanel>();
        panel.SetRunner(runner);
        panel.SetPortraitStage(portraitStage);
        panel.SetHealthDollStage(healthDollStage);

        var hexPanelRoot = new GameObject("HexLive Hex Inspector");
        hexPanelRoot.AddComponent<UIDocument>();
        var hexPanel = hexPanelRoot.AddComponent<HexInspectorPanel>();
        hexPanel.SetRunner(runner);

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

        var historyRoot = new GameObject("HexLive Game History");
        historyRoot.AddComponent<UIDocument>();
        var historyPanel = historyRoot.AddComponent<GameHistoryPanel>();
        historyPanel.SetRunner(runner);

        // Escape menu (continue / quit) — Escape with nothing selected.
        var menuRoot = new GameObject("HexLive Game Menu");
        menuRoot.AddComponent<UIDocument>();
        var menu = menuRoot.AddComponent<GameMenu>();
        menu.SetRunner(runner);

        // Victory/end-of-simulation summary — hidden until the raft launches.
        var endRoot = new GameObject("HexLive End Summary");
        endRoot.AddComponent<UIDocument>();
        var endSummary = endRoot.AddComponent<EndSummaryPanel>();
        endSummary.SetRunner(runner);
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
