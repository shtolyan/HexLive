using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
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
/// Isolated play-mode lab for §21.21B. A real NPC crosses a seven-hex flower
/// through the raised centre, first up and then down, from all six directions.
/// The orange marker shows the raw tick-stepped model position while the actor
/// shows the interpolated view.
/// </summary>
public sealed class HexStepJumpTestBootstrap : MonoBehaviour
{
    public static Action<HexTuningConfig> SaveConfigAssetInEditor;

    private const float DefaultSpeed = 0.24f;
    private const float StartDelaySeconds = 1.25f;
    private const float RoutePauseSeconds = 1.5f;
    private const float ElevationStep = 0.55f;

    private static readonly TileCoord Center = new(0, 0);
    private static readonly TileCoord[] Ring =
    {
        new(1, 0), new(1, -1), new(0, -1),
        new(-1, 0), new(-1, 1), new(0, 1)
    };

    private SimulationRunnerBehaviour _runner;
    private HexWorldRenderer _renderer;
    private HexTuningConfig _config;
    private JumpValues _values;
    private GameObject _modelMarker;
    private Vector2 _scroll;
    private int _directionIndex;
    private float _startDelay;
    private float _routeDelay;
    private bool _started;
    private bool _autoDirections = true;
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
        _config = Resources.Load<HexTuningConfig>("HexLive/HexTuningConfig");
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
        StartRoute(_directionIndex);
        _status = "Маршрут готов. После короткой паузы начнётся медленный прогон.";
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
                _status = DirectionLabel();
            }

            return;
        }

        if (!_autoDirections || !RouteFinished())
        {
            _routeDelay = 0f;
            return;
        }

        _routeDelay += Time.unscaledDeltaTime;
        if (_routeDelay >= RoutePauseSeconds)
        {
            _directionIndex = (_directionIndex + 1) % Ring.Length;
            StartRoute(_directionIndex);
        }
    }

    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>
        {
            new() { Q = 0, R = 0, Walkable = true, Elevation = 2 }
        };
        foreach (var coord in Ring)
        {
            tiles.Add(new TileBootstrap
            {
                Q = coord.Q,
                R = coord.R,
                Walkable = true,
                Elevation = 1
            });
        }

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 21021 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = 1,
                    DisplayName = "Marta",
                    ActorMesh = "Marta",
                    FragmentId = 1,
                    TileQ = Ring[0].Q,
                    TileR = Ring[0].R,
                    Hunger = 0.1f,
                    Thirst = 0.1f,
                    Energy = 0.95f,
                    Comfort = 0.95f,
                    Social = 0.95f,
                    ThermalDiscomfort = 0f
                }
            }
        };
    }

    private void StartRoute(int direction)
    {
        var world = _runner?.Engine?.World;
        if (world == null)
        {
            _status = "Мир ещё не готов.";
            return;
        }

        var npc = FirstNpc(world);
        var startTile = Ring[direction % Ring.Length];
        var targetTile = Ring[(direction + 3) % Ring.Length];
        var start = CenterJunction(world, startTile);
        var middle = CenterJunction(world, Center);
        var target = CenterJunction(world, targetTile);
        if (npc == null || start is not { } startId || middle is not { } middleId ||
            target is not { } targetId ||
            !world.Junctions.Items.TryGetValue(startId, out var startJunction))
        {
            _status = "Не удалось собрать маршрут через центральный гекс.";
            return;
        }

        var up = HexPathfinder.FindPath(world, startId, middleId, null,
            weightClimb: false, canJump: true);
        var down = HexPathfinder.FindPath(world, middleId, targetId, null,
            weightClimb: false, canJump: true);
        if (up.Count < 2 || down.Count < 2)
        {
            _status = "Pathfinder не нашёл обе половины маршрута.";
            return;
        }

        var previousTile = npc.Tile;
        ResetMovement(npc);
        npc.Tile = startTile;
        npc.CurrentJunction = startId;
        npc.Position = startJunction.WorldPosition;
        var facing = HexSpatialMath.TileToWorld(Center) - npc.Position;
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(facing);
        if (previousTile != startTile)
        {
            SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, startTile);
        }

        npc.Mind.CurrentGoal = GoalType.None;
        // Keep the ordinary goal auction out of this deterministic lab. The
        // movement and execution systems still run; only DecisionSystem treats
        // this as a permanent post-wake observation grace.
        npc.Mind.WakeGraceUntilTick = int.MaxValue;
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = targetId;
        npc.Plan.TargetTile = targetTile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetId
        });

        npc.Movement.JunctionPath.AddRange(up);
        for (var i = 1; i < down.Count; i++)
        {
            npc.Movement.JunctionPath.Add(down[i]);
        }
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
        _routeDelay = 0f;
        _status = DirectionLabel();
    }

    private static void ResetMovement(NPCState npc)
    {
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Idle);
        npc.Movement.ClimbPauseTimer = 0f;
        npc.Movement.PostTurnTimer = 0f;
        npc.Movement.HopTimer = 0f;
        npc.Movement.HopArmed = false;
        npc.Movement.HopCrossed = false;
        npc.Movement.HopPathIndex = -1;
        npc.Movement.HopLandingIndex = 0;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
    }

    private bool RouteFinished()
    {
        var world = _runner?.Engine?.World;
        var npc = world == null ? null : FirstNpc(world);
        return npc != null && !npc.Movement.IsMoving && npc.Movement.HopTimer <= 0f &&
            npc.Plan.Status == PlanStatus.Completed;
    }

    private static NPCState FirstNpc(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            return npc;
        }

        return null;
    }

    private static JunctionId? CenterJunction(WorldState world, TileCoord coord)
    {
        if (!world.Tiles.Items.TryGetValue(coord, out var tile))
        {
            return null;
        }

        var tileCenter = HexSpatialMath.TileToWorld(coord);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var id in tile.Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(id, out var junction))
            {
                continue;
            }

            var dx = junction.WorldPosition.X - tileCenter.X;
            var dy = junction.WorldPosition.Y - tileCenter.Y;
            var sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = id;
            }
        }

        return best;
    }

    private void ApplyLiveTuning()
    {
        _values.Validate();
        HexHopTuning.HopSeconds = _values.HopSeconds;
        HexHopTuning.DownHopSeconds = _values.DownHopSeconds;
        HexHopTuning.TakeoffSeconds = _values.TakeoffSeconds;
        HexHopTuning.LandingSeconds = _values.LandingSeconds;
        HexHopTuning.LandingFootClearance = _values.LandingFootClearance;
        HexHopTuning.LandingFootGuardSeconds = _values.LandingFootGuardSeconds;
        HexHopTuning.EdgePadding = _values.EdgePadding;
        HexHopTuning.FarPadding = _values.FarPadding;
        HexHopTuning.DownHopUp = _values.DownHopUp;
        HexHopTuning.DownFallStartFrac = _values.DownFallStart;
        HexHopTuning.FlightSettleFrac = _values.FlightSettle;
        HexHopTuning.UpApexFrac = _values.UpApex;
        HexHopTuning.UpOvershoot = _values.UpOvershoot;
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
        var npc = world == null ? null : FirstNpc(world);
        if (npc == null || _modelMarker == null)
        {
            return;
        }

        var elevation = world.Tiles.Items.TryGetValue(npc.Tile, out var tile) ? tile.Elevation : 1;
        var groundY = SimulationUnityMapper.TileHeight + elevation * ElevationStep;
        _modelMarker.transform.position = SimulationUnityMapper.ToUnityPosition(
            npc.Position, groundY + 0.14f);
    }

    private string DirectionLabel()
    {
        var start = Ring[_directionIndex];
        var target = Ring[(_directionIndex + 3) % Ring.Length];
        return $"Направление {_directionIndex + 1}/6: ({start.Q},{start.R}) → центр ↑ → ({target.Q},{target.R}) ↓";
    }

    private void OnGUI()
    {
        EnsureStyles();
        var panelWidth = Mathf.Min(430f, Screen.width - 24f);
        GUILayout.BeginArea(new Rect(12f, 12f, panelWidth, Screen.height - 24f), GUI.skin.box);
        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label("ЛАБОРАТОРИЯ ПРЫЖКА ЧЕРЕЗ СТУПЕНЬ", _titleStyle);
        GUILayout.Label(_status, _smallStyle);
        GUILayout.Space(6f);

        GUILayout.Label("Просмотр", _sectionStyle);
        _values.Speed = Slider("Скорость модели и вьюхи", _values.Speed, 0.05f, 1f, "×");
        GUILayout.Label("ПКМ — орбита, колесо — зум, F — вернуть обзор. Камера работает по unscaled time.", _smallStyle);
        _autoDirections = GUILayout.Toggle(_autoDirections, " Автоматически менять все 6 направлений");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("↻ Этот маршрут")) StartRoute(_directionIndex);
        if (GUILayout.Button("→ Следующее"))
        {
            _directionIndex = (_directionIndex + 1) % Ring.Length;
            StartRoute(_directionIndex);
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("Тайминг", _sectionStyle);
        _values.HopSeconds = Slider("Прыжок вверх — всё окно", _values.HopSeconds, 0.5f, 5f, "с");
        _values.DownHopSeconds = Slider("Прыжок вниз — всё окно", _values.DownHopSeconds, 0.2f, 5f, "с");
        _values.TakeoffSeconds = Slider("Подготовка / толчок", _values.TakeoffSeconds, 0f, 2f, "с");
        _values.LandingSeconds = Slider("Посадка / выправление", _values.LandingSeconds, 0f, 2f, "с");
        _values.LandingFootClearance = Slider("Клиренс подошвы", _values.LandingFootClearance, 0f, 0.08f, "wu");
        _values.LandingFootGuardSeconds = Slider("Защита после касания", _values.LandingFootGuardSeconds, 0f, 1.2f, "с");
        var flight = Mathf.Max(0.05f, _values.HopSeconds - _values.TakeoffSeconds - _values.LandingSeconds);
        GUILayout.Label($"Чистый полёт вверх: {flight:0.00} с", _smallStyle);

        GUILayout.Label("Геометрия полёта", _sectionStyle);
        _values.EdgePadding = Slider("Отступ у кромки", _values.EdgePadding, 0.1f, 1.5f, "wu");
        _values.FarPadding = Slider("Дальний отступ / разбег", _values.FarPadding, 0.2f, 1.2f, "wu");
        _values.FlightSettle = Slider("Доля полёта с движением", _values.FlightSettle, 0.2f, 1f, "");

        GUILayout.Label("Кривая вверх", _sectionStyle);
        _values.UpApex = Slider("Момент вершины дуги", _values.UpApex, 0.1f, 0.9f, "");
        _values.UpOvershoot = Slider("Высота над ступенькой", _values.UpOvershoot, 1f, 2f, "×");

        GUILayout.Label("Кривая вниз", _sectionStyle);
        _values.DownHopUp = Slider("Подброс перед падением", _values.DownHopUp, 0f, 0.8f, "wu");
        _values.DownFallStart = Slider("Когда начинается падение", _values.DownFallStart, 0f, 0.95f, "");
        var lipCross = _values.EdgePadding / Mathf.Max(0.001f, _values.EdgePadding + _values.FarPadding);
        GUILayout.Label($"Кромка пересекается на {lipCross:0.00}; падение лучше начинать не раньше.", _smallStyle);
        GUILayout.Label("Глубина нырка не показана: эта сцена проверяет только сухую ступень.", _smallStyle);

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
            _status = "Восстановлен исходный пресет. Нажмите «Сохранить», чтобы принять его в игру.";
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
        var world = _runner?.Engine?.World;
        var npc = world == null ? null : FirstNpc(world);
        if (npc == null)
        {
            GUILayout.Label("NPC ещё не создан.", _smallStyle);
            return;
        }

        var viewText = _renderer != null && _renderer.TryGetNpcViewPosition(npc.Id.Value, out var view)
            ? $"view ({view.x:0.000}, {view.y:0.000}, {view.z:0.000})"
            : "view загружается";
        GUILayout.Label(
            $"tick {_runner.CurrentTick}  |  model ({npc.Position.X:0.000}, {npc.Position.Y:0.000})  |  {viewText}  |  " +
            $"tile ({npc.Tile.Q},{npc.Tile.R})\n" +
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
        public float DownHopSeconds;
        public float TakeoffSeconds;
        public float LandingSeconds;
        public float LandingFootClearance;
        public float LandingFootGuardSeconds;
        public float EdgePadding;
        public float FarPadding;
        public float DownHopUp;
        public float DownFallStart;
        public float FlightSettle;
        public float UpApex;
        public float UpOvershoot;

        // Snapshot of HexTuningConfig at creation time. Saving experiments does
        // not mutate this preset, so the original setup is always one click away.
        public static JumpValues InitialDefaults => new()
        {
            Speed = DefaultSpeed,
            HopSeconds = 2f,
            DownHopSeconds = 1f,
            TakeoffSeconds = 0.1f,
            LandingSeconds = 0.5f,
            LandingFootClearance = 0.025f,
            LandingFootGuardSeconds = 0.65f,
            EdgePadding = 0.1f,
            FarPadding = 0.65f,
            DownHopUp = 0f,
            DownFallStart = 0.15f,
            FlightSettle = 0.65f,
            UpApex = 0.5f,
            UpOvershoot = 1.3f
        };

        public static JumpValues From(HexTuningConfig config, float speed)
        {
            return new JumpValues
            {
                Speed = speed,
                HopSeconds = config.hopSeconds,
                DownHopSeconds = config.downHopSeconds,
                TakeoffSeconds = config.hopTakeoffSeconds,
                LandingSeconds = config.hopLandingSeconds,
                LandingFootClearance = config.hopLandingFootClearance,
                LandingFootGuardSeconds = config.hopLandingFootGuardSeconds,
                EdgePadding = config.hopEdgePadding,
                FarPadding = config.hopFarPadding,
                DownHopUp = config.hopDownUp,
                DownFallStart = config.hopDownFallStartFrac,
                FlightSettle = config.hopFlightSettleFrac,
                UpApex = config.hopUpApexFrac,
                UpOvershoot = config.hopUpOvershoot
            };
        }

        public void Validate()
        {
            Speed = Mathf.Clamp(Speed, 0.05f, 1f);
            HopSeconds = Mathf.Clamp(HopSeconds, 0.5f, 5f);
            DownHopSeconds = Mathf.Clamp(DownHopSeconds, 0.2f, 5f);
            TakeoffSeconds = Mathf.Clamp(TakeoffSeconds, 0f, HopSeconds - 0.05f);
            LandingSeconds = Mathf.Clamp(LandingSeconds, 0f,
                Mathf.Max(0f, HopSeconds - TakeoffSeconds - 0.05f));
            LandingFootClearance = Mathf.Clamp(LandingFootClearance, 0f, 0.08f);
            LandingFootGuardSeconds = Mathf.Clamp(LandingFootGuardSeconds, 0f, 1.2f);
            EdgePadding = Mathf.Clamp(EdgePadding, 0.1f, 1.5f);
            FarPadding = Mathf.Clamp(FarPadding, 0.2f, 1.2f);
            DownHopUp = Mathf.Clamp(DownHopUp, 0f, 0.8f);
            DownFallStart = Mathf.Clamp(DownFallStart, 0f, 0.95f);
            FlightSettle = Mathf.Clamp(FlightSettle, 0.2f, 1f);
            UpApex = Mathf.Clamp(UpApex, 0.1f, 0.9f);
            UpOvershoot = Mathf.Clamp(UpOvershoot, 1f, 2f);
        }

        public void WriteTo(HexTuningConfig config)
        {
            Validate();
            config.hopSeconds = HopSeconds;
            config.downHopSeconds = DownHopSeconds;
            config.hopTakeoffSeconds = TakeoffSeconds;
            config.hopLandingSeconds = LandingSeconds;
            config.hopLandingFootClearance = LandingFootClearance;
            config.hopLandingFootGuardSeconds = LandingFootGuardSeconds;
            config.hopEdgePadding = EdgePadding;
            config.hopFarPadding = FarPadding;
            config.hopDownUp = DownHopUp;
            config.hopDownFallStartFrac = DownFallStart;
            config.hopFlightSettleFrac = FlightSettle;
            config.hopUpApexFrac = UpApex;
            config.hopUpOvershoot = UpOvershoot;
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
    private float _distance = 8.5f;
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
            _distance = 8.5f;
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

    private static TileCoord CenterForCamera() => new(0, 0);
}

}
