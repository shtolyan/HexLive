using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.SwimTest
{

// Swim test scene (§40.18-B dev tool): a REAL simulation reduced to the
// water-and-ledges problem. Two tiny islands split by a two-tile strait of
// deep water; the home island carries a HILL (elevation 2). One girl wakes
// starving AND parched: the coconuts sit ON TOP of the hill (hex-step jump
// up, eat, jump down), the only pond is ACROSS the strait (dive in, tread a
// beat, swim, climb out, fill the bottle, drink). Both №45 jump mechanics
// and §40.18-B swimming run every lap. When she is fed and watered she is
// teleported home needy again — an endless tuning loop (R restarts a lap).
// Every coefficient is a serialized field, tuned live in the inspector.
public sealed class SwimTestBootstrap : MonoBehaviour
{
    [Header("Вода — визуал")]
    [Tooltip("На сколько (в мировых единицах) корень актёра проваливается НИЖЕ поверхности воды. 0 — ноги на поверхности.")]
    [Range(-0.5f, 1.5f)]
    [SerializeField] private float _sinkDepth = 0.6f;

    [Tooltip("Высота тела в воде (общая для всех, для tread и гребков). Мировые единицы, + = вверх.")]
    [Range(-1f, 1f)]
    [SerializeField] private float _swimBodyLift;

    [Tooltip("Амплитуда волны — высота гребня в мировых единицах. ОДНА на всё: и меш воды, и качание пловца.")]
    [Range(0f, 1f)]
    [SerializeField] private float _waveAmplitude = 0.1f;

    [Tooltip("Частота волны (рад/юнит). Длина волны = 2π/частота: меньше значение — длиннее и плавнее волна.")]
    [Range(0.05f, 3f)]
    [SerializeField] private float _waveFrequency = 1.4f;

    [Tooltip("Скорость бега волны по поверхности.")]
    [Range(0f, 6f)]
    [SerializeField] private float _waveSpeed = 2f;

    [Header("Вода — симуляция")]
    [Tooltip("Пауза после прыжка в воду: сколько секунд она барахтается на месте (tread idle), прежде чем поплыть.")]
    [Range(0f, 15f)]
    [SerializeField] private float _treadPauseSeconds = 0.75f;

    [Tooltip("Множитель скорости движения в глубокой воде (1 — как пешком).")]
    [Range(0.1f, 1.5f)]
    [SerializeField] private float _swimSpeedFactor = 0.6f;

    [Header("Сидение на краю (ledge)")]
    [Tooltip("Подъём попы по Y на верхнюю ступень, когда сидит на краю (вьюха, модель не трогает). Умножается на кол-во ступеней вверх. Меньше = ниже к земле.")]
    [Range(-1f, 1f)]
    [SerializeField] private float _ledgeSeatLift = 0.4f;

    [Tooltip("Сдвиг НАЗАД на кромку (к верхнему тайлу), чтобы подъём приходился на землю, а не висел над обрывом.")]
    [Range(0f, 1f)]
    [SerializeField] private float _ledgeSeatBack = 0.45f;

    // §21.21B: живые ручки прыжка. Каждый кадр проталкиваются в HexHopTuning
    // (единый источник тайминга: сим-траверс + скорость клипа + дуга тела),
    // так что менять можно прямо в плей-моде — применяется мгновенно.
    [Header("Прыжок — тайминг (анимация = мастер-часы)")]
    [Tooltip("ВСЁ окно прыжка ВВЕРХ: толчок + полёт + приземление. Клип сжимается ровно в это время, сек.")]
    [Range(0.5f, 5f)]
    [SerializeField] private float _hopSeconds = 2f;

    [Tooltip("Окно прыжка ВНИЗ (спрыгивание) — меньше = быстрее. Тайминги толчка/посадки масштабируются пропорционально. Сек.")]
    [Range(0.2f, 5f)]
    [SerializeField] private float _downHopSeconds = 2f;

    [Tooltip("ТОЛЧОК: сколько в начале клипа занимает присед/замах — тело стоит, анимация уже играет, сек.")]
    [Range(0f, 2f)]
    [SerializeField] private float _hopTakeoffSeconds = 0.5f;

    [Tooltip("ПРИЗЕМЛЕНИЕ: сколько в конце клипа занимает посадка/выправление ног — тело уже в точке, стоит, сек.")]
    [Range(0f, 2f)]
    [SerializeField] private float _hopLandingSeconds = 0.5f;

    [Tooltip("ОТСТУП от стены (мировые единицы, перпендикулярно границе): взлетает ровно за столько ДО стены и приземляется ровно за столько ПОСЛЕ — симметрично. Больше = дальше от стены и длиннее прыжок.")]
    [Range(0.1f, 1.5f)]
    [SerializeField] private float _hopEdgePadding = 0.5f;

    [Tooltip("НЫРОК: на сколько мировых единиц она уходит ПОД уровень плавания в нижней точке плюха, потом выныривает.")]
    [Range(0f, 1.5f)]
    [SerializeField] private float _divePlungeDepth = 0.35f;

    [Tooltip("СПРЫГИВАНИЕ: на сколько мировых единиц она подпрыгивает ВВЕРХ с края перед падением (чтобы ноги не задевали кромку). 0 = сразу вниз.")]
    [Range(0f, 0.8f)]
    [SerializeField] private float _hopDownUp = 0.2f;

    [Tooltip("СПРЫГИВАНИЕ: доля полёта, до которой она летит РОВНО и не падает. 0.5 = падает только перелетев кромку (не задевает край). Меньше = падает раньше.")]
    [Range(0f, 0.95f)]
    [SerializeField] private float _hopDownFallStart = 0.5f;

    [Tooltip("Задержка старта симуляции после запуска сцены (реальные секунды): Unity успевает прогрузиться и отрисоваться, пока мир стоит на паузе.")]
    [Range(0f, 10f)]
    [SerializeField] private float _startDelaySeconds = 3f;

    [Header("Конфиг (ScriptableObject)")]
    [Tooltip("Ассет HexTuningConfig, который читает основная игра. Кнопки сохранения/загрузки — под этим компонентом в инспекторе.")]
    [SerializeField] private Config.HexTuningConfig _tuningConfig;

    public Config.HexTuningConfig TuningConfig => _tuningConfig;

    // Editor button: current slider values -> the config asset. The editor
    // then marks the asset dirty and saves it (persists, even from play mode
    // since it's an asset, not a scene object).
    public void WriteSlidersToConfig()
    {
        if (_tuningConfig == null)
        {
            return;
        }

        _tuningConfig.hopSeconds = _hopSeconds;
        _tuningConfig.downHopSeconds = _downHopSeconds;
        _tuningConfig.hopTakeoffSeconds = _hopTakeoffSeconds;
        _tuningConfig.hopLandingSeconds = _hopLandingSeconds;
        _tuningConfig.hopEdgePadding = _hopEdgePadding;
        _tuningConfig.hopDownUp = _hopDownUp;
        _tuningConfig.hopDownFallStartFrac = _hopDownFallStart;
        _tuningConfig.divePlungeDepth = _divePlungeDepth;
        _tuningConfig.swimEntryPauseSeconds = _treadPauseSeconds;
        _tuningConfig.swimSpeedFactor = _swimSpeedFactor;
        _tuningConfig.sinkDepth = _sinkDepth;
        _tuningConfig.swimBodyLift = _swimBodyLift;
        _tuningConfig.ledgeSeatLift = _ledgeSeatLift;
        _tuningConfig.ledgeSeatBack = _ledgeSeatBack;
        _tuningConfig.waveAmplitude = _waveAmplitude;
        _tuningConfig.waveFrequency = _waveFrequency;
        _tuningConfig.waveSpeed = _waveSpeed;
        // wadeDepth has no SwimTest slider — its config value is left as is.
    }

    // Editor button: the config asset -> the sliders (revert to saved).
    public void ReadSlidersFromConfig()
    {
        if (_tuningConfig == null)
        {
            return;
        }

        _hopSeconds = _tuningConfig.hopSeconds;
        _downHopSeconds = _tuningConfig.downHopSeconds;
        _hopTakeoffSeconds = _tuningConfig.hopTakeoffSeconds;
        _hopLandingSeconds = _tuningConfig.hopLandingSeconds;
        _hopEdgePadding = _tuningConfig.hopEdgePadding;
        _hopDownUp = _tuningConfig.hopDownUp;
        _hopDownFallStart = _tuningConfig.hopDownFallStartFrac;
        _divePlungeDepth = _tuningConfig.divePlungeDepth;
        _treadPauseSeconds = _tuningConfig.swimEntryPauseSeconds;
        _swimSpeedFactor = _tuningConfig.swimSpeedFactor;
        _sinkDepth = _tuningConfig.sinkDepth;
        _swimBodyLift = _tuningConfig.swimBodyLift;
        _ledgeSeatLift = _tuningConfig.ledgeSeatLift;
        _ledgeSeatBack = _tuningConfig.ledgeSeatBack;
        _waveAmplitude = _tuningConfig.waveAmplitude;
        _waveFrequency = _tuningConfig.waveFrequency;
        _waveSpeed = _tuningConfig.waveSpeed;
    }

    private SimulationRunnerBehaviour _runner;
    private JunctionId? _homeJunction;
    private TileCoord _homeTile;
    private float _restartDelay;
    private float _startDelayElapsed;
    private bool _started;

    private void Awake()
    {
        Application.runInBackground = true;

        // NB: the inspector sliders are the source of truth AT PLAY. We do
        // NOT overwrite them from the code defaults here — that reset every
        // value the user dialed in before pressing Play. PushTuning() below
        // (and every frame) pushes the inspector values INTO the tuning
        // statics, so what you set is what runs. To move a value into the
        // shipped code default, edit HexHopTuning.cs; to snap a slider back
        // to that default, right-click the field in the inspector → Reset.

        BuildEnvironment();

        var root = new GameObject("HexLive SwimTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        // Start PAUSED: the sim otherwise begins ticking while Unity is still
        // compiling shaders / building the scene, and the first seconds of
        // action play out unseen (the main game gates this with the loader).
        // Update() unpauses after _startDelaySeconds of real rendered time.
        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);
        OpenStrait();
        RememberHome();
        PushTuning();
    }

    private void Update()
    {
        PushTuning();

        // Grace period: the world stays paused while Unity finishes loading
        // and rendering, so second 0 of the action is never missed.
        if (!_started)
        {
            _startDelayElapsed += Time.deltaTime;
            if (_startDelayElapsed >= _startDelaySeconds && _runner != null)
            {
                _started = true;
                if (_runner.IsPaused)
                {
                    _runner.TogglePause();
                }
            }

            return;
        }

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
        {
            SendHerHome();
        }

        TickAutoLoop();
    }

    // ---- world ----

    // Home island (q -2..0, elevation 1) with a HILL at (-1,0) (elevation 2)
    // holding all the coconuts — eating means a hex-step jump up and back
    // down. Home island spans q -4..0 (the left tail is spawn run-up space).
    // A 2x3 strait of deep water (elevation 0, not walkable => swim tiles)
    // splits it from the far island (q 3..4), whose pond is the only drink —
    // drinking means the full swim round trip.
    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var tiles = new List<TileBootstrap>();
        for (var r = -1; r <= 1; r++)
        {
            // q from -4: two extra home tiles on the left give the spawn a
            // walking approach to the hill (watch the brake/windup/jump).
            for (var q = -4; q <= 4; q++)
            {
                var water = q is 1 or 2;
                var hill = q == -1 && r == 0;
                tiles.Add(new TileBootstrap
                {
                    Q = q,
                    R = r,
                    Walkable = !water,
                    Water = water,
                    Elevation = water ? 0 : hill ? 2 : 1
                });
            }
        }

        return new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 777 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 24f },
            Fragments =
            {
                new FragmentBootstrap { Id = 1, Tiles = tiles }
            },
            Objects =
            {
                // The larder on the hilltop (elevation 2).
                Food(201, -1, 0, 1),
                Food(202, -1, 0, 2),
                Food(203, -1, 0, 3),
                Food(204, -1, 0, 4),
                Food(205, -1, 0, 5),
                Food(206, -1, 0, 6),
                // The only water — across the strait.
                new ObjectBootstrap
                {
                    Id = 210,
                    DefinitionId = "water.pond",
                    FragmentId = 1,
                    TileQ = 4,
                    TileR = 0,
                    JunctionSlots = { 1 }
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
                    // The far EDGE of the home island — maximum run-up to the
                    // hill at (-1,0): three full tiles of approach to watch
                    // the walk -> brake -> windup -> jump sequence unhurried.
                    TileQ = -4,
                    TileR = 0,
                    // Starving AND parched, otherwise content — the only two
                    // things worth doing are the hilltop food and the
                    // across-the-water pond.
                    Hunger = 0.9f,
                    Thirst = 0.85f,
                    Energy = 0.95f,
                    Comfort = 0.9f,
                    Social = 0.9f,
                    ThermalDiscomfort = 0.1f
                }
            }
        };
    }

    private static ObjectBootstrap Food(int id, int q, int r, int slot)
    {
        return new ObjectBootstrap
        {
            Id = id,
            DefinitionId = "food.coconut",
            FragmentId = 1,
            TileQ = q,
            TileR = r,
            JunctionSlots = { slot }
        };
    }

    // The build-time swim ring only opens water junctions TOUCHING land — a
    // two-tile strait keeps its interior blocked (spec 40.18: the ring alone
    // can't bridge a full water tile). The test strait floods completely,
    // like the main island's SE corridor.
    private void OpenStrait()
    {
        var world = _runner.Engine?.World;
        if (world == null)
        {
            return;
        }

        var toOpen = new List<JunctionId>();
        foreach (var pair in world.Junctions.Items)
        {
            if (pair.Value.Blocked && SpatialQueries.IsAllWaterJunction(world, pair.Key))
            {
                toOpen.Add(pair.Key);
            }
        }

        foreach (var id in toOpen)
        {
            world.Junctions.Items[id].Blocked = false;
            world.SwimJunctions.Add(id);
        }
    }

    private void RememberHome()
    {
        var world = _runner.Engine?.World;
        if (world == null)
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            _homeJunction = npc.CurrentJunction;
            _homeTile = npc.Tile;
            break;
        }
    }

    // ---- the endless lap ----

    // Once she is fed (hilltop coconut) AND watered (pond across the strait)
    // and stands idle, wait a beat and teleport her home starving and
    // parched — the next lap starts by itself.
    private void TickAutoLoop()
    {
        var world = _runner.Engine?.World;
        if (world == null)
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            var done = !npc.Movement.IsMoving &&
                npc.Needs.Hunger < 0.4f && npc.Needs.Thirst < 0.4f;
            if (!done)
            {
                _restartDelay = 0f;
                return;
            }

            _restartDelay += Time.deltaTime;
            if (_restartDelay > 2.5f)
            {
                SendHerHome();
            }

            return;
        }
    }

    private void SendHerHome()
    {
        _restartDelay = 0f;
        var world = _runner.Engine?.World;
        if (world == null || _homeJunction is not { } home ||
            !world.Junctions.Items.TryGetValue(home, out var junction))
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            var previousTile = npc.Tile;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.IsMoving = false;
            npc.Movement.Status = MovementStatus.Idle;
            npc.Movement.ClimbPauseTimer = 0f;
            npc.Movement.HopTimer = 0f;
            npc.Movement.HopPathIndex = -1;
            npc.Plan.Steps.Clear();
            npc.Plan.Status = HexLive.Simulation.AI.PlanStatus.Failed;
            npc.CurrentJunction = home;
            npc.Tile = _homeTile;
            npc.Position = junction.WorldPosition;
            if (previousTile != _homeTile)
            {
                SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, _homeTile);
            }

            // Needy again — and the bottle is emptied, otherwise the next
            // "drink" would be a sip on the spot instead of a swim.
            npc.Needs.Hunger = 0.9f;
            npc.Needs.Thirst = 0.85f;
            npc.BottleWater = WaterKind.None;
            return;
        }
    }

    // ---- tuning ----

    private void PushTuning()
    {
        SwimVisuals.SinkDepth = _sinkDepth;
        MovementSystem.SwimEntryPauseSeconds = _treadPauseSeconds;
        MovementSystem.SwimSpeedFactor = _swimSpeedFactor;
        Wearing.NpcActorView.SwimBodyLift = _swimBodyLift;
        Wearing.NpcActorView.LedgeSeatLift = _ledgeSeatLift;
        Wearing.NpcActorView.LedgeSeatBack = _ledgeSeatBack;
        WaterWave.Amplitude = _waveAmplitude;
        WaterWave.Frequency = _waveFrequency;
        WaterWave.Speed = _waveSpeed;

        // §21.21B: hop timing — one shared source for sim and view.
        HexHopTuning.HopSeconds = _hopSeconds;
        HexHopTuning.DownHopSeconds = _downHopSeconds;
        HexHopTuning.TakeoffSeconds = _hopTakeoffSeconds;
        HexHopTuning.LandingSeconds = _hopLandingSeconds;
        HexHopTuning.EdgePadding = _hopEdgePadding;
        HexHopTuning.DivePlungeDepth = _divePlungeDepth;
        HexHopTuning.DownHopUp = _hopDownUp;
        HexHopTuning.DownFallStartFrac = _hopDownFallStart;
    }

    // ---- environment ----

    private void BuildEnvironment()
    {
        var camGo = new GameObject("SwimTestCamera")
        {
            tag = "MainCamera"
        };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 42f;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<SwimTestOrbitCamera>();
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(12f, 8f, 1100f, 22f),
            "SWIM TEST — еда на вершине холма (прыжки), пруд за проливом (плавание). Коэффициенты на компоненте SwimTest, R — перезапуск круга, ПКМ — орбита, колесо — зум, камера прилипла к NPC (СКМ — отлипнуть, F — прилипнуть)");
    }
}

// Inspection camera GLUED to the NPC: the orbit focus tracks her every frame
// (no chasing the action by hand). RMB drag orbits, scroll zooms; MMB drag
// pans AND releases the follow, F snaps it back on.
public sealed class SwimTestOrbitCamera : MonoBehaviour
{
    private Vector3 _focus;
    private float _yaw = 205f;
    private float _pitch = 38f;
    private float _distance = 9.5f;
    private bool _follow = true;
    private Wearing.NpcActorView _target;

    private void Start()
    {
        // Between the two water columns (tiles (1,0) and (2,0)) — the first
        // frames before the actor spawns.
        var a = Spatial.SimulationUnityMapper.ToUnityTilePosition(new TileCoord(1, 0), 0f);
        var b = Spatial.SimulationUnityMapper.ToUnityTilePosition(new TileCoord(2, 0), 0f);
        _focus = (a + b) * 0.5f + Vector3.up * 0.4f;
    }

    private void LateUpdate()
    {
        if (_target == null)
        {
            _target = FindAnyObjectByType<Wearing.NpcActorView>();
        }

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
        {
            _follow = true;
        }

        if (_follow && _target != null)
        {
            // Critically-damped-ish chase: tight enough to never lose her,
            // soft enough not to jitter on the jump snaps.
            var want = _target.transform.position + Vector3.up * 0.9f;
            _focus = Vector3.Lerp(_focus, want, 1f - Mathf.Exp(-8f * Time.deltaTime));
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, -10f, 85f);
            }

            if (mouse.middleButton.isPressed)
            {
                _follow = false; // manual look-around; F re-glues to the NPC
                var delta = mouse.delta.ReadValue();
                var rot = Quaternion.Euler(0f, _yaw, 0f);
                _focus += rot * new Vector3(-delta.x, 0f, -delta.y) * 0.003f * _distance;
            }

            var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f)
            {
                _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 1.5f, 30f);
            }
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
