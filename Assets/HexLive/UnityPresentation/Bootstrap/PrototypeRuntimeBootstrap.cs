using HexLive.Simulation.Bootstrap;
using UnityEngine;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Content;
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
        // §152: start the live registry while the player is still on the menu.
        // Local simdata is applied from config/simdata after the player chooses
        // a local world; a remote server supplies its own simdata in handshake.
        ContentAssetService.Instance.RefreshRegistry();

        // §104.9: the empty-id fist sheet has no owning item object from which
        // it could be discovered lazily. Queue its PRESENTATION-ONLY record
        // during the menu bootstrap; never overwrite server-authoritative
        // GearCatalog/recipes with local Player asset values here.
        Config.GearTuning.PrewarmPresentation();

        // Per-object wardrobe metadata is a live record, so one updated skirt
        // changes its own simulation presentation fields without a catalog.
        Wearing.Garments.WardrobeMeta.Load();

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

        // §127: романтические тест-сцены — свои камера/актёры/UI, мир и
        // загрузочная шторка поверх них не нужны.
        if (Object.FindAnyObjectByType<TwoPeopleTest.TwoPeopleTestBootstrap>() is not null)
        {
            return;
        }

        if (Object.FindAnyObjectByType<HexFlowerTest.HexFlowerTestBootstrap>() is not null)
        {
            return;
        }

        // §71.5: both LocomotionTest and MovementSmoothnessTest are driven by
        // the same self-contained bootstrap. They deliberately have no sim
        // runner: spawning the main menu/world over their measured lane adds a
        // second camera, UI and actors and invalidates every frame sample.
        if (Object.FindAnyObjectByType<LocomotionTest.LocomotionTestBootstrap>() is not null)
        {
            return;
        }

        // PERF: cap the render scale on Retina/4K displays before anything
        // draws — player builds only, see GraphicsPerfPolicy.
        GraphicsPerfPolicy.ApplyRenderScale();

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

        // §80: снимки лиц — своя камера, отдельная от живой портретной. Живая
        // снимает ОДНОГО выбранного каждый кадр для панели; эта раз в игровой
        // час фотографирует по одному телу в Texture2D, и снимки идут в пузыри
        // и во вкладку отношений, где лиц нужно много сразу.
        var portraitCacheRoot = new GameObject("HexLive Portrait Cache");
        var portraitCache = portraitCacheRoot.AddComponent<NpcPortraitCache>();
        var worldRenderer = Object.FindAnyObjectByType<Rendering.HexWorldRenderer>();
        if (worldRenderer != null)
        {
            worldRenderer.SetPortraitCache(portraitCache);
        }

        // Spec §51/§57: inventory and HP share one already-composed actor clone.
        var dollRoot = new GameObject("HexLive Character Doll Stage");
        var characterDollStage = dollRoot.AddComponent<CharacterDollStage>();

        var panelRoot = new GameObject("HexLive Character Panel");
        var document = panelRoot.AddComponent<UIDocument>();
        document.panelSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");

        var panel = panelRoot.AddComponent<CharacterPanel>();
        panel.SetRunner(runner);
        panel.SetPortraitStage(portraitStage);
        panel.SetCharacterDollStage(characterDollStage);
        panel.SetPortraitCache(portraitCache);

        var hexPanelRoot = new GameObject("HexLive Hex Inspector");
        hexPanelRoot.AddComponent<UIDocument>();
        var hexPanel = hexPanelRoot.AddComponent<HexInspectorPanel>();
        hexPanel.SetRunner(runner);

        // §121: меню действий ручного режима — открывается кликом по объекту,
        // человеку или зверю, когда выбранной колонисткой управляет игрок.
        var contextMenuRoot = new GameObject("HexLive Context Menu");
        contextMenuRoot.AddComponent<UIDocument>();
        contextMenuRoot.AddComponent<ContextMenuPanel>();

        // §128: Kenshi-style two-window exchange with any unconscious person.
        var lootRoot = new GameObject("HexLive Loot Transfer");
        lootRoot.AddComponent<UIDocument>();
        var lootPanel = lootRoot.AddComponent<LootTransferPanel>();
        lootPanel.SetRunner(runner);

        // Always-visible time controls (pause / play / speed) at the top.
        var speedRoot = new GameObject("HexLive Speed Bar");
        speedRoot.AddComponent<UIDocument>();
        var speedBar = speedRoot.AddComponent<SimSpeedBar>();
        speedBar.SetRunner(runner);

        // §120.7: игровой режим строительства (кнопка «Строить» / клавиша B) —
        // Sims-лоток, ghost, команды разметки; строят девушки.
        var buildRoot = new GameObject("HexLive Build Mode");
        buildRoot.AddComponent<UIDocument>();
        var buildMode = buildRoot.AddComponent<BuildModePanel>();
        buildMode.SetRunner(runner);
        if (worldRenderer != null) buildMode.SetWorldRenderer(worldRenderer);

        // §120.1: колышки размеченных площадок — до первого ингредиента.
        var stakes = buildRoot.AddComponent<Environment.BuildSiteStakeRenderer>();
        if (worldRenderer != null) stakes.Construct(runner, worldRenderer);

        // Left-side debug buttons: wound / clear / dirtier / cleaner.
        var debugRoot = new GameObject("HexLive Debug Controls");
        debugRoot.AddComponent<UIDocument>();
        var debugPanel = debugRoot.AddComponent<DebugControlsPanel>();
        debugPanel.SetRunner(runner);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        debugPanel.SetRuntimeConsole(new LunarRuntimeConsoleProvider());
#elif UNITY_EDITOR || UNITY_STANDALONE_OSX || UNITY_STANDALONE_WIN
        var consoleRoot = new GameObject("HexLive Runtime Console");
        consoleRoot.AddComponent<UIDocument>();
        var runtimeConsole = consoleRoot.AddComponent<DesktopRuntimeConsole>();
        debugPanel.SetRuntimeConsole(runtimeConsole);
#endif
#endif

        // In-game bug tracker window (BUGS.json), opened from the debug panel.
        var bugRoot = new GameObject("HexLive Bug Reports");
        bugRoot.AddComponent<UIDocument>();
        var bugPanel = bugRoot.AddComponent<BugReportPanel>();
        bugPanel.SetRunner(runner);
        debugPanel.SetBugReportPanel(bugPanel);

        var historyRoot = new GameObject("HexLive Game History");
        historyRoot.AddComponent<UIDocument>();
        var historyPanel = historyRoot.AddComponent<GameHistoryPanel>();
        historyPanel.SetRunner(runner);

        // §150: one procedural map rendered compactly below history and again
        // as the high-altitude world overlay.
        var mapRoot = new GameObject("HexLive Tactical Map");
        mapRoot.AddComponent<UIDocument>();
        var tacticalMap = mapRoot.AddComponent<TacticalMapPanel>();
        tacticalMap.SetRunner(runner);
        tacticalMap.SetPortraitCache(portraitCache);

        // Escape menu (continue / quit) — Escape with nothing selected.
        var menuRoot = new GameObject("HexLive Game Menu");
        menuRoot.AddComponent<UIDocument>();
        var menu = menuRoot.AddComponent<GameMenu>();
        menu.SetRunner(runner);

        // §145.3: связь с сервером потеряна насовсем — модальный диалог,
        // ОК возвращает в главное меню. Без него вечный реконнект выглядел
        // как зависшая игра.
        var linkLostRoot = new GameObject("HexLive Connection Lost");
        linkLostRoot.AddComponent<UIDocument>();
        var linkLost = linkLostRoot.AddComponent<ConnectionLostDialog>();
        linkLost.SetRunner(runner);

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

        // PhotoBake (bug #244) — слой офф-скрин съёмки импосторов и
        // портретов. Он пуст между синхронными проходами пекарен, но главная
        // камера всё равно не смотрит на него: страховка от будущего
        // асинхронного бейка, чей объект иначе мигнул бы на весь экран.
        mainCamera.cullingMask &= ~(1 << Views.ObjectImpostor.PhotoBakeLayer());

        // PERF: short ground props (SmallProps layer, assigned in
        // HexWorldRenderer.SuppressSmallPropShadows) stop drawing beyond the
        // distance where they are a few pixels tall. A zero entry means "use
        // the far plane", so every other layer is untouched. Spherical
        // distance keeps the cut stable while the camera pitches.
        var smallPropsLayer = LayerMask.NameToLayer("SmallProps");
        if (smallPropsLayer >= 0)
        {
            var cullDistances = new float[32];
            cullDistances[smallPropsLayer] = 45f;
            mainCamera.layerCullDistances = cullDistances;
            mainCamera.layerCullSpherical = true;
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

        // §121: ручной ввод — на той же камере: он получает левый клик первым
        // и, если управление в руках игрока, съедает его (идти / меню).
        var manualInput = mainCamera.GetComponent<Input.SimulationInputAdapter>();
        if (manualInput == null)
        {
            manualInput = mainCamera.gameObject.AddComponent<Input.SimulationInputAdapter>();
        }

        manualInput.SetRunner(runner);

    }
}

}
