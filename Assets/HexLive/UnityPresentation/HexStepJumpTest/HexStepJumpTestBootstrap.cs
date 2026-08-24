using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.HexStepJumpTest
{

/// <summary>
/// Play-mode lab for §21.21B v23 — И это НАСТОЯЩАЯ симуляция, не скриптовый
/// прогон. Зигзаг-лестница из гексов (+1 уровень на каждой кромке, поворот 60°
/// перед каждым прыжком), внизу и наверху — водосборы §54.15 с припаркованными
/// бутылками. Марта хочет пить; вода всегда в ПРОТИВОПОЛОЖНОМ конце лестницы,
/// так что она сама (перцепция → решение → план → путь) идёт и прыгает по
/// ступеням вверх, пьёт, а на следующем круге — вниз. Никаких телепортов и
/// ручных маршрутов: только жажда.
/// Оранжевый маркер показывает сырую тиковую позицию модели, актёр —
/// интерполированную вьюху.
/// </summary>
public sealed class HexStepJumpTestBootstrap : MonoBehaviour
{
    public static Action<HexTuningConfig> SaveConfigAssetInEditor;

    private const float DefaultSpeed = 0.35f;
    private const float StartDelaySeconds = 1.25f;
    private const float RearmPauseSeconds = 2f;
    private const float ElevationStep = 0.55f;

    // Зигзаг NE/SE: каждый шаг +1 уровень и поворот 60° перед прыжком.
    private static readonly TileCoord StartPad = new(-1, 0);   // e0
    private static readonly TileCoord BottomTile = new(0, 0);  // e0, нижний водосбор
    private static readonly TileCoord[] Steps =
    {
        new(1, -1), // e1 (NE от низа)
        new(1, 0),  // e2 (SE)
        new(2, -1), // e3 (NE)
        new(2, 0)   // e4 (SE) — вершина, верхний водосбор
    };

    private const int TopCollectorId = 11;
    private const int TopVesselId = 12;
    private const int BottomCollectorId = 21;
    private const int BottomVesselId = 22;

    // §54.15 ids (WaterCollectorMath — internal для сим-сборки, поэтому
    // литералы; станция и бутылка — шипнутый контент каталога).
    private const string CollectorDefinitionId = "station.water_collector";
    private const string VesselDefinitionId = "tool.bottle";

    private SimulationRunnerBehaviour _runner;
    private HexWorldRenderer _renderer;
    private HexTuningConfig _config;
    private JumpValues _values;
    private GameObject _modelMarker;
    private Vector2 _scroll;
    private float _startDelay;
    private float _rearmDelay;
    private bool _started;
    private bool _waterOnTop = true;
    private int _lapCount;
    private string _status = "Загрузка тестового мира…";
    private GUIStyle _titleStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _smallStyle;

    private void Awake()
    {
        Application.runInBackground = true;
        // This dev scene bypasses LoadingScreen, which normally clears a pause
        // menu's Time.timeScale=0. The local simulation deliberately ticks from
        // unscaled time, while Animator and gait sampling use scaled deltaTime;
        // inheriting a zero scale therefore moves the model under a frozen pose.
        Time.timeScale = 1f;
        _values = JumpValues.InitialDefaults;
        _config = HexLive.UnityPresentation.Content.AtomicResources.Load<HexTuningConfig>("HexLive/HexTuningConfig");
        if (_config != null)
        {
            // Стартуем с того, что реально играет игра, а не с код-дефолтов.
            _values = JumpValues.From(_config, DefaultSpeed);
        }

        ApplyLiveTuning();
        BuildModelMarker();

        var root = new GameObject("HexStepJumpTest Simulation");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        _runner.AutosaveSuppressed = true;
        _renderer = root.AddComponent<HexWorldRenderer>();
        _renderer.SetRunner(_runner);
        var sky = root.AddComponent<HexLive.UnityPresentation.Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: DefaultSpeed);
        BuildCamera();
        ArmWaterCycle(waterOnTop: true);
        _status = "Она хочет пить. Полная бутылка — в водосборе НАВЕРХУ лестницы.";
    }

    private void Update()
    {
        ApplyLiveTuning();
        UpdateModelMarker();

        if (!_started)
        {
            _startDelay += Time.unscaledDeltaTime;
            if (_startDelay >= StartDelaySeconds)
            {
                _started = true;
                _runner.Resume();
            }

            return;
        }

        // Круг завершён: напилась и стоит. Перезаряжаем жажду и переносим
        // воду в противоположный конец лестницы — следующий круг идёт в
        // другую сторону (вверх ↔ вниз), всё через обычные решения сима.
        var npc = CurrentNpc();
        if (npc == null || npc.Needs.Thirst > 0.35f || npc.Movement.IsMoving ||
            npc.Movement.HopTimer > 0f)
        {
            _rearmDelay = 0f;
            return;
        }

        _rearmDelay += Time.unscaledDeltaTime;
        if (_rearmDelay >= RearmPauseSeconds)
        {
            _lapCount++;
            ArmWaterCycle(!_waterOnTop);
        }
    }

    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>
        {
            new() { Q = StartPad.Q, R = StartPad.R, Walkable = true, Elevation = 0 },
            new() { Q = BottomTile.Q, R = BottomTile.R, Walkable = true, Elevation = 0 }
        };
        for (var i = 0; i < Steps.Length; i++)
        {
            tiles.Add(new TileBootstrap
            {
                Q = Steps[i].Q,
                R = Steps[i].R,
                Walkable = true,
                Elevation = i + 1
            });
        }

        var top = Steps[Steps.Length - 1];
        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 21021 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Objects =
            {
                // §54.15: водосбор + припаркованная в его слоте бутылка — на
                // ОБОИХ концах лестницы. Кто из них полон, решает ArmWaterCycle.
                new ObjectBootstrap
                {
                    Id = TopCollectorId, DefinitionId = CollectorDefinitionId,
                    FragmentId = 1, TileQ = top.Q, TileR = top.R,
                    JunctionSlots = { 0 }
                },
                new ObjectBootstrap
                {
                    Id = TopVesselId, DefinitionId = VesselDefinitionId,
                    FragmentId = 1, TileQ = top.Q, TileR = top.R,
                    JunctionSlots = { 0 }
                },
                new ObjectBootstrap
                {
                    Id = BottomCollectorId, DefinitionId = CollectorDefinitionId,
                    FragmentId = 1, TileQ = BottomTile.Q, TileR = BottomTile.R,
                    JunctionSlots = { 0 }
                },
                new ObjectBootstrap
                {
                    Id = BottomVesselId, DefinitionId = VesselDefinitionId,
                    FragmentId = 1, TileQ = BottomTile.Q, TileR = BottomTile.R,
                    JunctionSlots = { 0 }
                }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = 1,
                    DisplayName = "Marta",
                    ActorMesh = "Marta",
                    FragmentId = 1,
                    TileQ = StartPad.Q,
                    TileR = StartPad.R,
                    Hunger = 0.1f,
                    Thirst = 0.85f,
                    Energy = 0.95f,
                    Comfort = 0.95f,
                    Social = 0.95f,
                    ThermalDiscomfort = 0f
                }
            }
        };
    }

    // Вода — в одном конце, пустая бутылка — в другом; её собственная бутылка
    // пуста, жажда высокая. Дальше сим сам: перцепция находит полный водосбор,
    // план ведёт её по лестнице, переливание даёт глотки, следующий план — пьёт.
    private void ArmWaterCycle(bool waterOnTop)
    {
        var world = _runner?.Engine?.World;
        if (world == null)
        {
            return;
        }

        _waterOnTop = waterOnTop;
        _rearmDelay = 0f;
        SetVesselFill(world, TopVesselId, waterOnTop ? 1f : 0f);
        SetVesselFill(world, BottomVesselId, waterOnTop ? 0f : 1f);

        var npc = CurrentNpc();
        if (npc != null)
        {
            npc.BottleWater = WaterKind.None;
            npc.BottleCharges = 0;
            npc.Needs.Thirst = 0.85f;
            // Лаборатория живёт дольше одного дня: не даём сну и голоду
            // перебить сценарий жажды.
            npc.Needs.Energy = 0.95f;
            npc.Needs.Hunger = 0.1f;
        }

        _status = waterOnTop
            ? $"Круг {_lapCount + 1}: вода НАВЕРХУ — она прыгает ВВЕРХ по ступеням."
            : $"Круг {_lapCount + 1}: вода ВНИЗУ — она спрыгивает ВНИЗ по ступеням.";
    }

    private static void SetVesselFill(WorldState world, int objectId, float fill)
    {
        if (world.Entities.Objects.TryGetValue(new ObjectId(objectId), out var vessel))
        {
            vessel.ResourceAmount = fill;
        }
    }

    private NPCState CurrentNpc()
    {
        var world = _runner?.Engine?.World;
        if (world == null)
        {
            return null;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            return npc;
        }

        return null;
    }

    private void ApplyLiveTuning()
    {
        _values.Validate();
        HexHopTuning.HopSeconds = _values.HopSeconds;
        HexHopTuning.TakeoffSeconds = _values.TakeoffSeconds;
        HexHopTuning.LandingSeconds = _values.LandingSeconds;
        HexHopTuning.EdgePadding = _values.EdgePadding;
        HexHopTuning.LipClearance = _values.LipClearance;
        if (_runner != null && Mathf.Abs(_runner.SpeedMultiplier - _values.Speed) > 0.0001f)
        {
            _runner.SetSpeed(_values.Speed);
        }
    }

    private void SaveToGameConfig()
    {
        if (_config == null)
        {
            _status = "HexTuningConfig не найден в Resources/HexLive.";
            return;
        }

        _values.WriteTo(_config);
        if (SaveConfigAssetInEditor == null)
        {
            _status = "Значения применены, но сохранить ассет можно только в Unity Editor.";
            return;
        }

        SaveConfigAssetInEditor(_config);
        _status = "Сохранено в HexTuningConfig: эти значения теперь использует игра.";
    }

    private void BuildCamera()
    {
        var cameraObject = new GameObject("HexStepJumpTest Camera") { tag = "MainCamera" };
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 38f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 120f;
        cameraObject.AddComponent<AudioListener>();
        cameraObject.AddComponent<FMODUnity.StudioListener>();
        var orbit = cameraObject.AddComponent<HexStepJumpOrbitCamera>();
        orbit.Configure(_runner, _renderer);
    }

    private void BuildModelMarker()
    {
        _modelMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _modelMarker.name = "RAW MODEL POSITION (tick-stepped)";
        _modelMarker.transform.localScale = Vector3.one * 0.22f;
        var collider = _modelMarker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { color = new Color(1f, 0.28f, 0.02f) };
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", new Color(1f, 0.08f, 0f) * 2f);
        _modelMarker.GetComponent<MeshRenderer>().material = material;
    }

    private void UpdateModelMarker()
    {
        var world = _runner?.Engine?.World;
        var npc = world == null ? null : CurrentNpc();
        if (npc == null || _modelMarker == null)
        {
            return;
        }

        var elevation = world.Tiles.Items.TryGetValue(npc.Tile, out var tile) ? tile.Elevation : 0;
        var groundY = SimulationUnityMapper.TileHeight + elevation * ElevationStep;
        _modelMarker.transform.position = SimulationUnityMapper.ToUnityPosition(
            npc.Position, groundY + 0.14f);
    }

    private void OnGUI()
    {
        EnsureStyles();
        var panelWidth = Mathf.Min(430f, Screen.width - 24f);
        GUILayout.BeginArea(new Rect(12f, 12f, panelWidth, Screen.height - 24f), GUI.skin.box);
        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label("ЛАБОРАТОРИЯ ПРЫЖКА: ЛЕСТНИЦА И ЖАЖДА", _titleStyle);
        GUILayout.Label(_status, _smallStyle);
        GUILayout.Space(6f);

        GUILayout.Label("Просмотр", _sectionStyle);
        _values.Speed = Slider("Скорость модели и вьюхи", _values.Speed, 0.05f, 1f, "×");
        GUILayout.Label("ПКМ — орбита, колесо — зум, F — вернуть обзор. Камера работает по unscaled time.", _smallStyle);
        if (GUILayout.Button("↻ Перезарядить цикл (вода в другой конец)"))
        {
            _lapCount++;
            ArmWaterCycle(!_waterOnTop);
        }

        GUILayout.Label("Прыжок — §21.21B v23, пять ручек", _sectionStyle);
        _values.HopSeconds = Slider("Всё окно прыжка", _values.HopSeconds, 0.5f, 5f, "с");
        _values.TakeoffSeconds = Slider("Подготовка / толчок", _values.TakeoffSeconds, 0f, 2f, "с");
        _values.LandingSeconds = Slider("Посадка / выправление", _values.LandingSeconds, 0f, 2f, "с");
        _values.EdgePadding = Slider("Отступ у кромки (симметрично)", _values.EdgePadding, 0.1f, 1.5f, "wu");
        _values.LipClearance = Slider("Клиренс над кромкой", _values.LipClearance, 0f, 0.8f, "wu");
        var flight = Mathf.Max(0.05f, _values.HopSeconds - _values.TakeoffSeconds - _values.LandingSeconds);
        GUILayout.Label(
            $"Полёт {flight:0.00} с на {2f * _values.EdgePadding:0.00} wu = " +
            $"{2f * _values.EdgePadding / flight:0.00} wu/с (шаг ≈ 1.2 wu/с). " +
            "Перед отрывом она ДОВОРАЧИВАЕТСЯ до направления полёта — это не ручка, это правило.",
            _smallStyle);

        GUILayout.Space(8f);
        GUI.backgroundColor = new Color(0.55f, 1f, 0.65f);
        if (GUILayout.Button("💾  СОХРАНИТЬ ПРЫЖОК В ИГРУ", GUILayout.Height(32f))) SaveToGameConfig();
        GUI.backgroundColor = Color.white;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Загрузить сохранённое"))
        {
            if (_config != null)
            {
                _values = JumpValues.From(_config, _values.Speed);
                _status = "Загружены текущие сохранённые значения HexTuningConfig.";
            }
        }
        if (GUILayout.Button("Вернуть дефолт"))
        {
            _values = JumpValues.InitialDefaults;
            _status = "Восстановлен код-дефолт v23. Нажмите «Сохранить», чтобы принять его в игру.";
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        DrawTelemetry();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawTelemetry()
    {
        GUILayout.Label("Телеметрия", _sectionStyle);
        var npc = CurrentNpc();
        if (npc == null)
        {
            GUILayout.Label("NPC ещё не создан.", _smallStyle);
            return;
        }

        var viewText = _renderer != null && _renderer.TryGetNpcViewPosition(npc.Id.Value, out var view)
            ? $"view ({view.x:0.000}, {view.y:0.000}, {view.z:0.000})"
            : "view загружается";
        GUILayout.Label(
            $"tick {_runner.CurrentTick}  |  круг {_lapCount + 1} ({(_waterOnTop ? "вверх" : "вниз")})\n" +
            $"model ({npc.Position.X:0.000}, {npc.Position.Y:0.000})  |  {viewText}  |  " +
            $"tile ({npc.Tile.Q},{npc.Tile.R})\n" +
            $"жажда {npc.Needs.Thirst:0.00}  |  цель {npc.Mind.CurrentGoal}  |  " +
            $"глотков в бутылке {npc.BottleCharges}\n" +
            $"state {npc.Movement.Status}  |  hop {npc.Movement.HopTimer:0.00} с  |  " +
            $"path {npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}\n" +
            "Оранжевая сфера = точка модели (телепорт раз в tick). Персонаж = интерполированная вьюха.",
            _smallStyle);
    }

    private float Slider(string label, float value, float min, float max, string suffix)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(245f));
        GUILayout.Label($"{value:0.00}{suffix}", GUILayout.Width(68f));
        GUILayout.EndHorizontal();
        return GUILayout.HorizontalSlider(value, min, max);
    }

    private void EnsureStyles()
    {
        if (_titleStyle != null)
        {
            return;
        }

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 17,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        _sectionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            margin = new RectOffset(0, 0, 10, 3)
        };
        _smallStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
    }

    [Serializable]
    private struct JumpValues
    {
        public float Speed;
        public float HopSeconds;
        public float TakeoffSeconds;
        public float LandingSeconds;
        public float EdgePadding;
        public float LipClearance;

        // Код-дефолты v23 (HexHopTuning). «Загрузить сохранённое» берёт то,
        // что реально играет игра (HexTuningConfig).
        public static JumpValues InitialDefaults => new()
        {
            Speed = DefaultSpeed,
            HopSeconds = 1.2f,
            TakeoffSeconds = 0.25f,
            LandingSeconds = 0.35f,
            EdgePadding = 0.3f,
            LipClearance = 0.2f
        };

        public static JumpValues From(HexTuningConfig config, float speed)
        {
            return new JumpValues
            {
                Speed = speed,
                HopSeconds = config.hopSeconds,
                TakeoffSeconds = config.hopTakeoffSeconds,
                LandingSeconds = config.hopLandingSeconds,
                EdgePadding = config.hopEdgePadding,
                LipClearance = config.hopLipClearance
            };
        }

        public void Validate()
        {
            Speed = Mathf.Clamp(Speed, 0.05f, 1f);
            HopSeconds = Mathf.Clamp(HopSeconds, 0.5f, 5f);
            TakeoffSeconds = Mathf.Clamp(TakeoffSeconds, 0f, HopSeconds - 0.05f);
            LandingSeconds = Mathf.Clamp(LandingSeconds, 0f,
                Mathf.Max(0f, HopSeconds - TakeoffSeconds - 0.05f));
            EdgePadding = Mathf.Clamp(EdgePadding, 0.1f, 1.5f);
            LipClearance = Mathf.Clamp(LipClearance, 0f, 0.8f);
        }

        public void WriteTo(HexTuningConfig config)
        {
            Validate();
            config.hopSeconds = HopSeconds;
            config.hopTakeoffSeconds = TakeoffSeconds;
            config.hopLandingSeconds = LandingSeconds;
            config.hopEdgePadding = EdgePadding;
            config.hopLipClearance = LipClearance;
        }
    }
}

/// <summary>
/// Scene-local inspection camera. Mouse deltas are already per-frame values,
/// so rotation deliberately does not multiply them by deltaTime; smoothing
/// alone uses unscaled time. This keeps the camera responsive at any simulation
/// speed and even while the model clock is paused.
/// </summary>
public sealed class HexStepJumpOrbitCamera : MonoBehaviour
{
    private const float OrbitDegreesPerPixel = 0.22f;
    private const float FollowSmoothTime = 0.08f;
    private const float FrameRightFactor = 0.18f;

    private SimulationRunnerBehaviour _runner;
    private HexWorldRenderer _renderer;
    private Vector3 _focus;
    private Vector3 _focusVelocity;
    private float _yaw = 210f;
    private float _pitch = 32f;
    private float _distance = 9.5f;
    private bool _initialized;

    public void Configure(SimulationRunnerBehaviour runner, HexWorldRenderer renderer)
    {
        _runner = runner;
        _renderer = renderer;
    }

    private void LateUpdate()
    {
        HandleInput();

        var deltaTime = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
        if (!TryGetTarget(out var target))
        {
            target = SimulationUnityMapper.ToUnityTilePosition(CenterForCamera(),
                SimulationUnityMapper.TileHeight + 1.1f);
        }

        if (!_initialized)
        {
            _focus = target;
            _initialized = true;
        }
        else
        {
            _focus = Vector3.SmoothDamp(_focus, target, ref _focusVelocity,
                FollowSmoothTime, Mathf.Infinity, deltaTime);
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        // Aim a little left of the actor: the actor then sits in the right-hand
        // clear area instead of under the 430 px tuning panel.
        var lookAt = _focus - rotation * Vector3.right * (_distance * FrameRightFactor);
        var desiredPosition = lookAt - rotation * Vector3.forward * _distance;
        var blend = 1f - Mathf.Exp(-14f * deltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, blend);
        transform.rotation = Quaternion.Slerp(transform.rotation, rotation, blend);
    }

    private void HandleInput()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
        {
            _yaw = 210f;
            _pitch = 32f;
            _distance = 9.5f;
        }

        var mouse = Mouse.current;
        if (mouse == null)
        {
            return;
        }

        if (mouse.rightButton.isPressed)
        {
            var delta = mouse.delta.ReadValue();
            _yaw += delta.x * OrbitDegreesPerPixel;
            _pitch = Mathf.Clamp(_pitch - delta.y * OrbitDegreesPerPixel, 10f, 75f);
        }

        var scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            _distance = Mathf.Clamp(
                _distance * Mathf.Exp(-scroll * 0.0015f), 4.2f, 18f);
        }
    }

    private bool TryGetTarget(out Vector3 target)
    {
        if (_runner == null || !_runner.IsReady)
        {
            target = default;
            return false;
        }

        var snapshot = _runner.CreateSnapshot();
        if (snapshot == null || snapshot.Npcs.Count == 0)
        {
            target = default;
            return false;
        }

        var npc = snapshot.Npcs[0];
        if (_renderer != null && _renderer.TryGetNpcBodyCenter(npc.Id.Value, out target))
        {
            return true;
        }

        target = SimulationUnityMapper.ToUnityPosition(
            npc.Position, SimulationUnityMapper.CameraTargetHeight);
        return true;
    }

    // Середина лестницы — гекс (1,0).
    private static TileCoord CenterForCamera() => new(1, 0);
}

}
