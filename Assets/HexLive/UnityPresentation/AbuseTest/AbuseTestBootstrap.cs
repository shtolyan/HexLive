using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.AbuseTest
{
    /// <summary>
    /// §87: арена «он и она». Крошечный плоский островок, один чужак и одна
    /// девушка в двух шагах — и больше НИЧЕГО: ни зверей, ни стройки, ни
    /// голода, ни холода.
    ///
    /// Зачем она понадобилась: сцена абьюза четыре игровых дня не случалась ни
    /// разу, при том что headless-замеры показывали «жертва есть в 95% времени».
    /// Спорить об этом было бесполезно — нужен один мир, который видно обоим.
    /// Мир поэтому живёт в общем <see cref="AbuseTestWorld"/>: его строит и эта
    /// сцена, и проба.
    ///
    /// Сцена — это не только «посмотреть». Здесь тестируются драки: сюда же
    /// удобно смотреть, как отказ переходит в бой (§85) и как работает пощада
    /// (§86) — на большой карте эти моменты приходится ловить часами.
    /// </summary>
    public sealed class AbuseTestBootstrap : MonoBehaviour
    {
        private SimulationRunnerBehaviour _runner;

        private void Awake()
        {
            Application.runInBackground = true;

            var camGo = new GameObject("AbuseTestCamera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 42f;
            cam.nearClipPlane = 0.05f;
            camGo.AddComponent<AmputationTest.AmputationTestOrbitCamera>();

            var root = new GameObject("HexLive AbuseTest Sim");
            _runner = root.AddComponent<SimulationRunnerBehaviour>();
            // Тестовый мир — НИКОГДА не поверх настоящего сейва.
            _runner.AutosaveSuppressed = true;

            var worldRenderer = root.AddComponent<HexWorldRenderer>();
            worldRenderer.SetRunner(_runner);
            var sky = root.AddComponent<Environment.SkyDayNightController>();
            sky.SetRunner(_runner);

            // Пауза на старте, чтобы не потерять первые секунды на загрузке
            // шейдеров: сцена начинается сразу, и пропустить её легко.
            _runner.Configure(AbuseTestWorld.Build(), startPaused: true, initialSpeed: 1f);

            var world = _runner.Engine?.World;
            if (world != null)
            {
                world.Tick = 900;   // 15:00, светло
            }

            EquipBoth();
            PushTestOverrides();
            InstallCharacterPanel();
        }

        // Абьюз — ЕДИНСТВЕННОЕ, что здесь проверяется, поэтому всё остальное
        // выключено: нужды заморожены, зверей нет, тепло круглые сутки.
        // Толкается каждый кадр, потому что HexTuning.LoadAndApply
        // (AfterSceneLoad, то есть ПОСЛЕ Awake) переписывает статики из ассета.
        private void PushTestOverrides()
        {
            SimBalance.HungerRate = 0f;
            SimBalance.ThirstRate = 0f;
            SimBalance.EnergyRate = 0f;
            SimBalance.BaseTemperature = 18f;
            SimBalance.TemperatureAmplitude = 2f;
            MobCatalog.For(MobIds.Dog).RaidChancePerDay = 0f;

            // ⭐ Отсрочка в НОЛЬ: в настоящей игре чужак не трогает колонию два
            // дня, и именно поэтому «ничего не происходит» так долго читалось
            // как поломка. На арене ждать нечего.
            Spec81.AbuseGraceDays = 0;

            var world = _runner != null ? _runner.Engine?.World : null;
            if (world != null)
            {
                world.NextMobSpawnCheckTick = int.MaxValue;
            }
        }

        // Ему — его обычная амуниция и оружие, ей — каменный нож, чтобы у неё
        // был выбор огрызнуться, а не только сдаться.
        private void EquipBoth()
        {
            var world = _runner?.Engine?.World;
            if (world == null)
            {
                return;
            }

            if (world.Entities.Npcs.TryGetValue(
                    new EntityId(AbuseTestWorld.GirlId), out var girl))
            {
                WardrobeDebugHelpers.Redress(world, girl, "clothing.top_tropic", "Shorts 1389");
                girl.Inventory.Items.Add("tool.knife");
            }

            if (world.Entities.Npcs.TryGetValue(
                    new EntityId(AbuseTestWorld.OutsiderId), out var outsider))
            {
                WardrobeDebugHelpers.Redress(world, outsider,
                    "TonnyFlash", "FCO Pants Male", "FAO Harness Male", "FCO Boots Male");
                outsider.Inventory.Items.Add("tool.spear");
                outsider.Inventory.Items.Add("tool.knife");
                // Он должен ХОТЕТЬ прямо сейчас: одиночество — единственная
                // незакрытая нужда во всём мире.
                outsider.Needs.Social = 0f;
            }
        }

        private void Update()
        {
            PushTestOverrides();
        }

        private void InstallCharacterPanel()
        {
            var stageRoot = new GameObject("HexLive Portrait Stage");
            var portraitStage = stageRoot.AddComponent<UI.PortraitStage>();

            var dollRoot = new GameObject("HexLive Health Doll Stage");
            var healthDollStage = dollRoot.AddComponent<UI.HealthDollStage>();

            var cacheRoot = new GameObject("HexLive Portrait Cache");
            var portraitCache = cacheRoot.AddComponent<UI.NpcPortraitCache>();
            var worldRenderer = Object.FindAnyObjectByType<HexWorldRenderer>();
            if (worldRenderer != null)
            {
                worldRenderer.SetPortraitCache(portraitCache);
            }

            var panelRoot = new GameObject("HexLive Character Panel");
            var document = panelRoot.AddComponent<UIDocument>();
            document.panelSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            var panel = panelRoot.AddComponent<UI.CharacterPanel>();
            panel.SetRunner(_runner);
            panel.SetPortraitStage(portraitStage);
            panel.SetHealthDollStage(healthDollStage);
            panel.SetPortraitCache(portraitCache);

            // Выделен ЧУЖАК: смотреть надо на него — на его нужду в общении и
            // на то, когда он срывается с места.
            Input.NpcSelection.Select(AbuseTestWorld.OutsiderId);
        }
    }
}
