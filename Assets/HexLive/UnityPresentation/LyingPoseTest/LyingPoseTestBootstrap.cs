using System.Collections;
using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using SimEntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.UnityPresentation.LyingPoseTest
{

// Bug #23: visual truth scene for the two authored sleep poses.
//
// This is deliberately a REAL one-NPC simulation on a seven-tile flower. The
// NPC starts on one of the six petals; the center is the only indoor tile, so
// the normal ground-sleep planner chooses it and pathfinding approaches it from
// the selected angle. Energy starts at one quarter of the live ordinary-sleep
// threshold, but remains non-zero. The runner remains paused and the
// bootstrap advances the REAL simulation exactly one tick per rendered frame;
// this prevents a long first-load frame from skipping the whole 100-tick sleep
// interaction. Once the inspected Sleep (or optional FeedOther aid) stabilises,
// stepping stops while Unity's Animator keeps running into its idle. The cyan
// rectangle is not hand-tuned
// art: it is the exact simulation footprint from Spec49 (1.32 x 0.36 world units).
public sealed class LyingPoseTestBootstrap : MonoBehaviour
{
    private const int StableSleepTicks = 4;
    private const int FirstPoseNpcId = 2;
    private const int SecondPoseNpcId = 1;
    private const int HelperNpcId = 3;
    private const float AidTargetHunger = 0.58f;
    private const int TileElevation = 1;
    private const float ElevationStep = 0.55f;

    [Tooltip("Задержка перед запуском реальной симуляции: актёр и тайл успевают появиться до выбора сна.")]
    [Range(0f, 5f)]
    [SerializeField] private float _startDelaySeconds = 1.5f;

    // The report explicitly calls out pose two; open the scene on it. The
    // selection survives a scene reload, so keys 1/2 and MCP can compare both
    // variants without ever putting two bodies on the flower arena.
    private static int _selectedPose = 1;
    private static int _approachIndex;
    private static bool _batchRunning;
    private static int _batchCase;
    private static bool _aidMode;

    // Axial neighbours in clockwise order. One complete turn exercises all six
    // entry starts; the lying solver may snap several arrivals to the same
    // clean hex axis, which is part of the production flow being inspected.
    private static readonly (int q, int r, string label)[] Approaches =
    {
        (1, 0, "E"),
        (1, -1, "NE"),
        (0, -1, "NW"),
        (-1, 0, "W"),
        (-1, 1, "SW"),
        (0, 1, "SE")
    };

    private SimulationRunnerBehaviour _runner;
    private float _startDelayElapsed;
    private bool _started;
    private bool _sleepCaptured;
    private int _sleepObservedTicks;
    private bool _aidCaptured;
    private int _aidObservedTicks;
    private float _captureReadyAt = -1f;
    private bool _captureRoutineStarted;
    private GameObject _footprintRoot;
    private NPCState _deferredHelper;

    public int SelectedPose => _selectedPose;

    public int ApproachIndex => _approachIndex;

    public bool SleepCaptured => _sleepCaptured;

    public bool BatchRunning => _batchRunning;

    public bool AidMode => _aidMode;

    public bool AidCaptured => _aidCaptured;

    private bool CaptureReady => _aidMode ? _aidCaptured : _sleepCaptured;

    // Read the LIVE tuning, not the source default: simdata currently lowers
    // SleepEnergyThreshold substantially. A quarter-threshold remains tired
    // throughout the friend's short approach, while staying non-zero so this
    // is ordinary planned sleep rather than exhaustion collapse.
    private static float InitialEnergy =>
        Mathf.Max(0.05f, SimBalance.SleepEnergyThreshold * 0.25f);

    private void Awake()
    {
        Application.runInBackground = true;

        var camGo = new GameObject("LyingPoseTestCamera")
        {
            tag = "MainCamera"
        };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 36f;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<LyingPoseTestOrbitCamera>();

        var root = new GameObject("HexLive LyingPoseTest Sim");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        _runner.AutosaveSuppressed = true;
        // This dev scene owns the paused clock. Mark loading as complete so
        // SimulationRunnerBehaviour's missing-loader watchdog never resumes it;
        // AutosaveSuppressed still guarantees that no real save is written.
        _runner.AutosaveEnabled = true;
        var worldRenderer = root.AddComponent<HexWorldRenderer>();
        worldRenderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        _runner.Configure(BuildWorldDefinition(), startPaused: true, initialSpeed: 1f);
        var world = _runner.Engine?.World;
        if (world != null)
        {
            // Midday light is easiest to inspect. InitialEnergy independently
            // makes the ordinary Sleep goal decisive without a zero-energy coma.
            world.Tick = 900;
            if (_aidMode)
            {
                DeferHelper(world);
            }
        }

        PushTestOverrides();
    }

    private void Update()
    {
        PushTestOverrides();

        if (!_started)
        {
            _startDelayElapsed += Time.unscaledDeltaTime;
            if (_startDelayElapsed >= _startDelaySeconds && _runner != null)
            {
                _started = true;
            }

            return;
        }

        // Do not let a slow asset-loading frame feed a large delta into the
        // clock: one explicit tick per frame preserves the ordinary decision,
        // planning, pathfinding and execution systems, while guaranteeing that
        // the visual test observes the first Sleep/InProgress tick.
        if (!CaptureReady && _runner != null)
        {
            if (!_runner.IsPaused)
            {
                _runner.Pause();
            }

            _runner.StepSingleTick();
        }

        var npc = TestNpc();
        var sleeping = npc != null &&
            npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Sleep;
        if (!_sleepCaptured && sleeping)
        {
            // The renderer interpolates between two simulation snapshots. Keep
            // the real Sleep interaction alive for several ticks so both ends
            // contain the final lying yaw; freezing the first tick preserved
            // the preceding walk rotation and made the mesh look askew.
            _sleepObservedTicks++;
        }
        else if (!_sleepCaptured)
        {
            _sleepObservedTicks = 0;
        }

        if (!_sleepCaptured && _sleepObservedTicks >= StableSleepTicks)
        {
            _sleepCaptured = true;
            BuildFootprint(npc);
            if (_aidMode)
            {
                // She becomes helpably hungry only AFTER she is safely asleep:
                // 0.58 opens Feed (0.55) but stays below sleep interruption
                // (0.60). The friend then enters the live world with a meal.
                npc.Needs.Hunger = AidTargetHunger;
                SpawnDeferredHelper();
            }
            else
            {
                _captureReadyAt = Time.unscaledTime;
                if (!_runner.IsPaused)
                {
                    _runner.Pause();
                }
            }

            Debug.Log(
                $"[LyingPoseTest] Pose={_selectedPose + 1} Npc={npc.Id.Value} " +
                $"Sleep=InProgress Position=({npc.Position.X:F3},{npc.Position.Y:F3}) " +
                $"Heading={npc.RotationDegrees:F1} Footprint={BodyLength:F2}x{BodyWidth:F2}wu");
        }

        if (_aidMode && _sleepCaptured && !_aidCaptured)
        {
            var helper = HelperNpc();
            var feeding = helper != null &&
                helper.Execution.Status == ExecutionStatus.InProgress &&
                helper.Execution.CurrentInteraction == InteractionType.FeedOther &&
                npc.Execution.Status == ExecutionStatus.InProgress &&
                npc.Execution.CurrentInteraction == InteractionType.Sleep;
            _aidObservedTicks = feeding ? _aidObservedTicks + 1 : 0;
            if (_aidObservedTicks >= StableSleepTicks)
            {
                _aidCaptured = true;
                _captureReadyAt = Time.unscaledTime;
                if (!_runner.IsPaused)
                {
                    _runner.Pause();
                }

                Debug.Log(
                    $"[LyingPoseTest] Aid=FeedOther Helper={helper.Id.Value} " +
                    $"Start={Approaches[_approachIndex].label} " +
                    $"Position=({helper.Position.X:F3},{helper.Position.Y:F3}) " +
                    $"Heading={helper.RotationDegrees:F1} TargetHunger={npc.Needs.Hunger:F2}");
            }
        }

        if (_batchRunning && CaptureReady && !_captureRoutineStarted &&
            Time.unscaledTime - _captureReadyAt >= 6f)
        {
            StartCoroutine(CaptureBatchCase());
        }

        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        if (keyboard.digit1Key.wasPressedThisFrame)
        {
            LoadPose(0);
        }
        else if (keyboard.digit2Key.wasPressedThisFrame)
        {
            LoadPose(1);
        }
        else if (keyboard.rKey.wasPressedThisFrame)
        {
            LoadCase(_selectedPose, _approachIndex);
        }
        else if (keyboard.aKey.wasPressedThisFrame)
        {
            LoadAidCase(_selectedPose, _approachIndex);
        }
        else if (keyboard.qKey.wasPressedThisFrame)
        {
            LoadCase(_selectedPose, _approachIndex - 1);
        }
        else if (keyboard.eKey.wasPressedThisFrame)
        {
            LoadCase(_selectedPose, _approachIndex + 1);
        }
    }

    public void LoadPose(int poseIndex)
    {
        LoadCase(poseIndex, _approachIndex);
    }

    // MCP/Editor entry point: runs pose 1 and pose 2 from every petal, letting
    // each real simulation walk, choose Sleep and settle before its screenshot.
    public void StartBatch()
    {
        _aidMode = false;
        _batchRunning = true;
        _batchCase = 0;
        LoadCase(0, 0);
    }

    // Same matrix, but the selected petal is the FRIEND'S deferred spawn.
    // The sleeper always enters from E so her body remains a stable reference
    // while the helper proves every route to the canonical feet station.
    public void StartAidBatch()
    {
        _aidMode = true;
        _batchRunning = true;
        _batchCase = 0;
        LoadCase(0, 0);
    }

    public void LoadAidCase(int poseIndex, int helperApproachIndex)
    {
        _aidMode = true;
        _batchRunning = false;
        LoadCase(poseIndex, helperApproachIndex);
    }

    public void LoadCase(int poseIndex, int approachIndex)
    {
        _selectedPose = Mathf.Clamp(poseIndex, 0, 1);
        _approachIndex = (approachIndex % Approaches.Length + Approaches.Length) % Approaches.Length;
        SceneManager.LoadScene(SceneManager.GetActiveScene().path);
    }

    private IEnumerator CaptureBatchCase()
    {
        _captureRoutineStarted = true;
        yield return new WaitForEndOfFrame();

        var approach = Approaches[_approachIndex];
        var directory = Path.Combine(Application.dataPath, "Screenshots");
        Directory.CreateDirectory(directory);
        var prefix = _aidMode ? "bug23_aid" : "bug23";
        var fileName = $"{prefix}_pose{_selectedPose + 1}_{approach.label.ToLowerInvariant()}.png";
        ScreenCapture.CaptureScreenshot(Path.Combine(directory, fileName));

        // CaptureScreenshot writes after the frame. Keep this scene alive long
        // enough for the file to finish before loading the next flower case.
        yield return new WaitForSecondsRealtime(1f);
        Debug.Log($"[LyingPoseTest] Captured {fileName}");

        _batchCase++;
        if (_batchCase >= 2 * Approaches.Length)
        {
            _batchRunning = false;
            Debug.Log(_aidMode
                ? "[LyingPoseTest] Aid batch complete: 2 poses x 6 helper starts."
                : "[LyingPoseTest] Batch complete: 2 poses x 6 approaches.");
            yield break;
        }

        LoadCase(_batchCase / Approaches.Length, _batchCase % Approaches.Length);
    }

    private void PushTestOverrides()
    {
        SimBalance.HungerRate = 0f;
        SimBalance.ThirstRate = 0f;
        SimBalance.EnergyRate = 0f;
        SimBalance.BaseTemperature = 22f;
        SimBalance.TemperatureAmplitude = 0f;
        MobCatalog.For(MobIds.Dog).RaidChancePerDay = 0f;

        var world = _runner != null ? _runner.Engine?.World : null;
        if (world != null)
        {
            world.NextMobSpawnCheckTick = int.MaxValue;
            if (world.Mobs.Count > 0)
            {
                world.Mobs.Clear();
            }
        }
    }

    private NPCState TestNpc()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null)
        {
            return null;
        }

        var id = new SimEntityId(_selectedPose == 0 ? FirstPoseNpcId : SecondPoseNpcId);
        return world.Entities.Npcs.TryGetValue(id, out var npc) ? npc : null;
    }

    private NPCState HelperNpc()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        return world != null && world.Entities.Npcs.TryGetValue(new SimEntityId(HelperNpcId), out var helper)
            ? helper
            : null;
    }

    private void DeferHelper(WorldState world)
    {
        var helperId = new SimEntityId(HelperNpcId);
        if (!world.Entities.Npcs.TryGetValue(helperId, out _deferredHelper))
        {
            Debug.LogError("[LyingPoseTest] Aid mode could not find its helper NPC.");
            return;
        }

        var sleeper = TestNpc();
        if (sleeper != null)
        {
            SetFriendship(sleeper, _deferredHelper, 0.85f);
        }

        _deferredHelper.CompassionTrait = 1f;
        _deferredHelper.Needs.Compassion = 0f;
        _deferredHelper.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));

        world.Entities.Npcs.Remove(helperId);
        RemoveFrom(world.Occupancy.EntitiesInTile, _deferredHelper.Tile, helperId);
        RemoveFrom(world.Caches.EntitiesByTile, _deferredHelper.Tile, helperId);
        RemoveFrom(world.Caches.EntitiesByFragment, _deferredHelper.Fragment, helperId);
    }

    private void SpawnDeferredHelper()
    {
        var world = _runner != null ? _runner.Engine?.World : null;
        if (world == null || _deferredHelper == null)
        {
            Debug.LogError("[LyingPoseTest] Aid helper was not deferred correctly.");
            return;
        }

        world.Entities.Npcs[_deferredHelper.Id] = _deferredHelper;
        AddTo(world.Occupancy.EntitiesInTile, _deferredHelper.Tile, _deferredHelper.Id);
        AddTo(world.Caches.EntitiesByTile, _deferredHelper.Tile, _deferredHelper.Id);
        AddTo(world.Caches.EntitiesByFragment, _deferredHelper.Fragment, _deferredHelper.Id);
        Debug.Log(
            $"[LyingPoseTest] Spawned friend NPC{_deferredHelper.Id.Value} with coconut " +
            $"at {Approaches[_approachIndex].label} after sleeper settled.");
        _deferredHelper = null;
    }

    private static void SetFriendship(NPCState first, NPCState second, float value)
    {
        foreach (var relation in new[]
                 {
                     first.Social.GetOrCreate(second.Id),
                     second.Social.GetOrCreate(first.Id)
                 })
        {
            relation.Affinity = value;
            relation.Familiarity = value;
            relation.Trust = value;
        }
    }

    private static void RemoveFrom<TKey>(
        Dictionary<TKey, List<SimEntityId>> map, TKey key, SimEntityId id)
    {
        if (map.TryGetValue(key, out var values))
        {
            values.Remove(id);
        }
    }

    private static void AddTo<TKey>(
        Dictionary<TKey, List<SimEntityId>> map, TKey key, SimEntityId id)
    {
        if (!map.TryGetValue(key, out var values))
        {
            values = new List<SimEntityId>();
            map[key] = values;
        }

        if (!values.Contains(id))
        {
            values.Add(id);
        }
    }

    private static float BodyLength =>
        HexSpatialMath.HexRadius * Spec49.LieBodyLengthFactor;

    private static float BodyWidth =>
        HexSpatialMath.HexRadius * Spec49.LieBodyWidthFactor;

    private void BuildFootprint(NPCState npc)
    {
        if (_footprintRoot != null)
        {
            Destroy(_footprintRoot);
        }

        _footprintRoot = new GameObject("MODEL DATA — lying footprint");
        var center = SimulationUnityMapper.ToUnityPosition(npc.Position, GroundY + 0.035f);
        var heading = npc.RotationDegrees * Mathf.Deg2Rad;
        // Simulation heading: 0 degrees = +X. For a lying body +forward runs
        // head -> feet (§111.9), which is why the red marker is at -forward.
        var forward = new Vector3(Mathf.Cos(heading), 0f, Mathf.Sin(heading));
        var lateral = new Vector3(-forward.z, 0f, forward.x);
        var halfLength = BodyLength * 0.5f;
        var halfWidth = BodyWidth * 0.5f;

        var line = _footprintRoot.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = true;
        line.positionCount = 4;
        line.startWidth = 0.025f;
        line.endWidth = 0.025f;
        line.numCornerVertices = 3;
        line.material = NewMaterial(new Color(0.05f, 0.95f, 1f, 1f));
        line.SetPositions(new[]
        {
            center - forward * halfLength - lateral * halfWidth,
            center + forward * halfLength - lateral * halfWidth,
            center + forward * halfLength + lateral * halfWidth,
            center - forward * halfLength + lateral * halfWidth
        });

        CreateMarker("HEAD (model)", center - forward * halfLength, Color.red);
        CreateMarker("FEET (model)", center + forward * halfLength, Color.yellow);
    }

    private void CreateMarker(string markerName, Vector3 position, Color color)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = markerName;
        marker.transform.SetParent(_footprintRoot.transform, true);
        marker.transform.position = position + Vector3.up * 0.035f;
        marker.transform.localScale = Vector3.one * 0.09f;
        var collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        marker.GetComponent<Renderer>().material = NewMaterial(color);
    }

    private static Material NewMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        var material = new Material(shader);
        material.color = color;
        return material;
    }

    private static float GroundY =>
        SimulationUnityMapper.TileHeight + TileElevation * ElevationStep;

    private static WorldBootstrapDefinition BuildWorldDefinition()
    {
        var npcId = _selectedPose == 0 ? FirstPoseNpcId : SecondPoseNpcId;
        var approach = Approaches[_approachIndex];
        var sleeperApproach = _aidMode ? Approaches[0] : approach;
        var definition = new WorldBootstrapDefinition
        {
            Simulation = new SimulationBootstrapSettings { Seed = 1072991686 },
            Environment = new EnvironmentBootstrap { GlobalTemperature = 22f },
            Fragments =
            {
                new FragmentBootstrap
                {
                    Id = 1,
                    Tiles = new List<TileBootstrap>
                    {
                        // A roof beats proximity in BuildGroundSleepPlan. This
                        // makes the center the unique normal AI choice without
                        // injecting a plan or teleporting the actor.
                        new()
                        {
                            Q = 0,
                            R = 0,
                            Walkable = true,
                            Indoor = true,
                            Water = false,
                            Elevation = TileElevation
                        },
                        new() { Q = 1, R = 0, Walkable = true, Elevation = TileElevation },
                        new() { Q = 1, R = -1, Walkable = true, Elevation = TileElevation },
                        new() { Q = 0, R = -1, Walkable = true, Elevation = TileElevation },
                        new() { Q = -1, R = 0, Walkable = true, Elevation = TileElevation },
                        new() { Q = -1, R = 1, Walkable = true, Elevation = TileElevation },
                        new() { Q = 0, R = 1, Walkable = true, Elevation = TileElevation }
                    }
                }
            },
            Npcs =
            {
                new NpcBootstrap
                {
                    Id = npcId,
                    DisplayName = "Jana",
                    ActorMesh = "Jana",
                    FragmentId = 1,
                    TileQ = sleeperApproach.q,
                    TileR = sleeperApproach.r,
                    Hunger = 0f,
                    Thirst = 0f,
                    Energy = InitialEnergy,
                    Comfort = 1f,
                    Social = 1f,
                    ThermalDiscomfort = 0f
                }
            }
        };

        if (_aidMode)
        {
            definition.Npcs.Add(new NpcBootstrap
            {
                Id = HelperNpcId,
                DisplayName = "Molly",
                ActorMesh = "Molly",
                FragmentId = 1,
                TileQ = approach.q,
                TileR = approach.r,
                Hunger = 0f,
                Thirst = 0f,
                Energy = 0.9f,
                Comfort = 1f,
                Social = 1f,
                ThermalDiscomfort = 0f
            });
        }

        return definition;
    }

    private void OnGUI()
    {
        var npc = TestNpc();
        var state = !_started
            ? "подготовка сцены…"
            : !_sleepCaptured
                ? "AI выбирает сон…"
                : _aidMode && !_aidCaptured
                    ? "подруга с кокосом идёт помогать…"
                    : _aidMode
                        ? "FeedOther/InProgress — симуляция зафиксирована"
                        : "Sleep/InProgress — симуляция зафиксирована";
        var id = npc != null ? npc.Id.Value.ToString() : "—";
        var approach = Approaches[_approachIndex];
        var startLabel = _aidMode ? "СТАРТ ПОДРУГИ" : "ПОДХОД";
        GUI.Box(new Rect(12f, 12f, 680f, 116f), string.Empty);
        GUI.Label(new Rect(24f, 20f, 570f, 22f),
            $"BUG #23 — ПОЗА {_selectedPose + 1}/2 · {startLabel} {_approachIndex + 1}/6 ({approach.label}) · NPC {id}");
        GUI.Label(new Rect(24f, 44f, 650f, 22f), state);
        GUI.Label(new Rect(24f, 68f, 650f, 22f),
            $"Модель данных: {BodyLength:F2} × {BodyWidth:F2} wu · бирюзовая рамка · красная голова · жёлтые ноги");
        GUI.Label(new Rect(24f, 92f, 650f, 22f),
            "1/2 — поза · Q/E — лепесток · R — повторить · A — помощь с кокосом · ПКМ/колесо — камера");
    }
}

// Close inspection camera: default view is steep enough to compare the skin
// with the exact footprint, but remains orbitable for silhouette/height checks.
public sealed class LyingPoseTestOrbitCamera : MonoBehaviour
{
    private Vector3 _focus = new(0f, 0.72f, 0f);
    private float _yaw = 205f;
    private float _pitch = 58f;
    private float _distance = 5.4f;

    private void LateUpdate()
    {
        var actor = FindAnyObjectByType<Wearing.NpcActorView>();
        if (actor != null)
        {
            var wanted = actor.transform.position + Vector3.up * 0.35f;
            _focus = Vector3.Lerp(_focus, wanted, 1f - Mathf.Exp(-8f * Time.deltaTime));
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, 10f, 88f);
            }

            var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f)
            {
                _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 1.5f, 10f);
            }
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
