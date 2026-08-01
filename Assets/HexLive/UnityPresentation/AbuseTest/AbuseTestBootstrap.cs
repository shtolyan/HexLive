using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
// Unity 6 завела СВОЙ UnityEngine.EntityId — имена столкнулись, поэтому
// псевдоним: он однозначен и читается лучше полного пути в каждом вызове.
using EntityId = HexLive.Simulation.Common.EntityId;

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
            // §92: та же камера, что на боевой карте, а не тестовая орбита.
            // Она сама следит за выделенным, отпускает по Escape и умеет
            // перебирать персонажей — переизобретать это в каждой сцене значит
            // получать в каждой сцене свой набор мелких отличий.
            // Ставится ПОСЛЕ создания runner'а (ему нужен SetRunner) — см. ниже.

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
                // Стартуем ПОСЛЕ льготных суток абьюза и в 15:00 — светло, и
                // ждать нечего.
                world.Tick = Simulation.Runtime.Spec81.AbuseGraceDays *
                    Simulation.Runtime.EnvironmentSystem.DayLengthTicks + 900;
            }

            var rts = camGo.AddComponent<Input.RtsCameraController>();
            rts.SetRunner(_runner);

            EquipBoth();
            InstallCharacterPanel();
        }

        // §91: ⭐ НИКАКИХ ПОДПОРОК. Отличие от настоящей игры ровно одно —
        // КАРТА. Погода, дождь, смена температуры, звери, голод, жажда, сон —
        // всё работает как в бою.
        //
        // Первая версия арены глушила нужды, держала вечные +18° и запрещала
        // зверей. Такой тест доказывал ровно ничего: вопрос не «может ли он
        // докопаться в вакууме», а «переживёт ли желание конкуренцию с
        // голодом, пеньком и дождём». Именно на этом мы и залипли.
        //
        // Единственная поблажка — СТАРТОВЫЙ ТИК: мир начинается уже после
        // льготных двух суток (§81), потому что смотреть на пустое ожидание
        // незачем. Это не правка поведения, а точка входа.

        // Ему — его обычная амуниция и оружие, ей — каменный нож, чтобы у неё
        // был выбор огрызнуться, а не только сдаться.
        private void EquipBoth()
        {
            var world = _runner?.Engine?.World;
            if (world == null)
            {
                return;
            }

            // §95: одежду девушкам НЕ трогаем. Раньше здесь стояло принудительное
            // переодевание в один и тот же комплект — и все трое выходили
            // одинаковыми, хотя мир раздаёт им разные вещи из общей ротации по
            // хешу от сида и id. Тест не должен переодевать то, что и так
            // работает: иначе он проверяет собственную заглушку.
            for (var i = 0; i < 3; i++)
            {
                if (world.Entities.Npcs.TryGetValue(
                        new EntityId(AbuseTestWorld.GirlId + i), out var girl))
                {
                    // Нож — чтобы у неё был выбор огрызнуться, а не только
                    // сдаться. Это единственное, что арена добавляет.
                    girl.Inventory.Items.Add("tool.knife");
                }
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
            // §90: промотка времени. Сцена нужна ровно для того, чтобы ловить
            // редкие моменты, а ждать их в реальном времени бессмысленно —
            // цикл абьюза повторяется примерно раз в 1000 тиков.
            //
            // Раскладка та же, что в соседних тестовых сценах, чтобы не
            // переучиваться: 1/2/3 — скорость, пробел — пауза, R — заново.
            var keyboard = Keyboard.current;
            if (keyboard == null || _runner == null)
            {
                return;
            }

            if (keyboard.digit1Key.wasPressedThisFrame)
            {
                _runner.SetSpeed(1f);
            }
            else if (keyboard.digit2Key.wasPressedThisFrame)
            {
                _runner.SetSpeed(3f);
            }
            else if (keyboard.digit3Key.wasPressedThisFrame)
            {
                _runner.SetSpeed(8f);
            }
            else if (keyboard.digit4Key.wasPressedThisFrame)
            {
                // Отдельная «очень быстро»: цикл абьюза виден целиком за
                // несколько секунд.
                _runner.SetSpeed(20f);
            }
            else if (keyboard.spaceKey.wasPressedThisFrame)
            {
                _runner.TogglePause();
            }
            else if (keyboard.rKey.wasPressedThisFrame)
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }
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
