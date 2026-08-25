using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.LocomotionTest
{

/// <summary>
/// §71 locomotion laboratory, iteration 2: the REAL game world. The full
/// prototype island is generated from the seed exactly as the game does it —
/// real colonists with real stats, live AI, fauna, weather — and rendered
/// through the shipped snapshot/interpolation/actor flow with the game's RTS
/// camera. Nothing is muted: the point is to catch the suspicious smoothness
/// the synthetic ruler could never reproduce. The one lab addition is the lag
/// recorder — press ЗАПИСЬ, reproduce the oddity, press СТОП, and the take
/// (per-frame model vs view vs animator for every colonist) lands in
/// Captures/*.jsonl for offline analysis.
/// </summary>
public sealed class LocomotionTestBootstrap : MonoBehaviour
{
    [Tooltip("Сид реального мира — тот же генератор, что в игре.")]
    [SerializeField] private int _seed = 12345;
    [Tooltip("Задержка старта симуляции: Unity успевает прогрузиться на паузе.")]
    [Range(0f, 10f)]
    [SerializeField] private float _startDelaySeconds = 2f;

    private SimulationRunnerBehaviour _runner;
    private HexWorldRenderer _renderer;
    private NpcAnimSet _animSet;
    private LocomotionLagRecorder _recorder;
    private GameObject _modelMarker;
    private Vector2 _scroll;
    private float _worldSpeed = 1f;
    private bool _started;
    private float _startDelay;
    private string _status = "Реальный мир загружается…";

    private int _lastModelTick = -1;
    private HexLive.Simulation.Common.Float2 _lastModelPosition;
    private int _lastModelNpcId = -1;
    private float _modelTickSpeed;
    private float _viewSpeed;
    private Vector3 _lastViewPosition;
    private bool _hasViewSample;

    private int _savedVSyncCount;
    private int _savedTargetFrameRate;

    private void Awake()
    {
        Application.runInBackground = true;
        // timeScale survives Enter Play Mode with domain reload off (the
        // LoadingScreen trap): a stale zero freezes scaled-time animators
        // while the sim keeps walking bodies around on its unscaled clock.
        Time.timeScale = 1f;

        _savedVSyncCount = QualitySettings.vSyncCount;
        _savedTargetFrameRate = Application.targetFrameRate;
        _animSet = HexLive.UnityPresentation.Content.AtomicResources.Load<NpcAnimSet>("HexLive/NpcAnimSet");

        var root = new GameObject("LocomotionTest — real world");
        _runner = root.AddComponent<SimulationRunnerBehaviour>();
        // Throwaway world: never let it touch the real hexlive_save.dat.
        _runner.AutosaveSuppressed = true;
        _renderer = root.AddComponent<HexWorldRenderer>();
        _renderer.SetRunner(_runner);
        var sky = root.AddComponent<Environment.SkyDayNightController>();
        sky.SetRunner(_runner);

        // The exact world the game builds: same factory, same seed semantics,
        // real colonist stats and live systems. No muting, no stripped fauna.
        _runner.Configure(
            PrototypeWorldDefinitionFactory.Create(_seed),
            startPaused: true, initialSpeed: 1f);

        _recorder = root.AddComponent<LocomotionLagRecorder>();
        _recorder.Configure(_runner, _renderer);

        BuildCamera();
        BuildModelMarker();
    }

    private void OnDestroy()
    {
        QualitySettings.vSyncCount = _savedVSyncCount;
        Application.targetFrameRate = _savedTargetFrameRate;
    }

    private void BuildCamera()
    {
        // The game's own camera rig: RTS movement, edge pan, follow (F), and
        // the §121 input adapter so colonists select and take orders exactly
        // like in the shipped game.
        var cameraObject = new GameObject("LocomotionTest Camera")
        {
            tag = "MainCamera"
        };
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 42f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 250f;
        cameraObject.AddComponent<AudioListener>();
        var rts = cameraObject.AddComponent<Input.RtsCameraController>();
        rts.SetRunner(_runner);
        var input = cameraObject.AddComponent<Input.SimulationInputAdapter>();
        input.SetRunner(_runner);
    }

    private void BuildModelMarker()
    {
        _modelMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _modelMarker.name = "RAW MODEL POSITION (4 Hz)";
        _modelMarker.transform.localScale = Vector3.one * 0.18f;
        Destroy(_modelMarker.GetComponent<Collider>());
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { color = new Color(1f, 0.25f, 0.02f) };
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", new Color(1f, 0.06f, 0f) * 2f);
        _modelMarker.GetComponent<MeshRenderer>().material = material;
        _modelMarker.SetActive(false);
    }

    private void Update()
    {
        if (!_started)
        {
            _startDelay += Time.unscaledDeltaTime;
            if (_startDelay >= _startDelaySeconds)
            {
                _started = true;
                _runner.Resume();
                _status = "Реальный мир живёт. Выбери колонистку кликом, F — следить.";
            }
            return;
        }

        if (_runner != null && Mathf.Abs(_runner.SpeedMultiplier - _worldSpeed) > 0.001f)
        {
            _runner.SetSpeed(_worldSpeed);
        }

        UpdateTelemetry();
    }

    private HexLive.Simulation.Agents.NPCState SelectedNpc()
    {
        var world = _runner?.Engine?.World;
        var id = Input.NpcSelection.PrimaryId;
        return world != null && id > 0 && world.Entities.Npcs.TryGetValue(
            new HexLive.Simulation.Common.EntityId(id), out var npc)
            ? npc : null;
    }

    private void UpdateTelemetry()
    {
        var npc = SelectedNpc();
        if (npc == null)
        {
            if (_modelMarker != null && _modelMarker.activeSelf)
            {
                _modelMarker.SetActive(false);
            }
            _hasViewSample = false;
            _lastModelNpcId = -1;
            return;
        }

        if (npc.Id.Value != _lastModelNpcId)
        {
            _lastModelNpcId = npc.Id.Value;
            _lastModelTick = -1;
            _hasViewSample = false;
        }

        if (_runner.CurrentTick != _lastModelTick)
        {
            if (_lastModelTick >= 0)
            {
                _modelTickSpeed = HexSpatialMath.Distance(
                    _lastModelPosition, npc.Position) / 0.25f;
            }
            _lastModelPosition = npc.Position;
            _lastModelTick = _runner.CurrentTick;
        }

        if (_renderer.TryGetNpcViewPosition(npc.Id.Value, out var viewPosition))
        {
            if (_hasViewSample && Time.unscaledDeltaTime > 0f)
            {
                _viewSpeed = Vector3.Distance(viewPosition, _lastViewPosition) /
                    Time.unscaledDeltaTime;
            }
            _lastViewPosition = viewPosition;
            _hasViewSample = true;
        }

        if (_modelMarker != null)
        {
            if (!_modelMarker.activeSelf)
            {
                _modelMarker.SetActive(true);
            }
            var world = _runner.Engine.World;
            var elevation = world.Tiles.Items.TryGetValue(npc.Tile, out var tile)
                ? tile.Elevation : 1;
            var groundY = SimulationUnityMapper.TileHeight + elevation * 0.55f;
            _modelMarker.transform.position = SimulationUnityMapper.ToUnityPosition(
                npc.Position, groundY + 0.14f);
        }
    }

    private string BuildRecordingMeta()
    {
        return "{\"meta\":true" +
            ",\"seed\":" + _seed +
            ",\"tick\":" + _runner.CurrentTick +
            ",\"simSpeed\":" + _runner.SpeedMultiplier.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"speedSmoothTau\":" + NpcActorView.SpeedSmoothTau.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"walkHold\":" + NpcActorView.WalkHoldSeconds.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"midJourneyWalkHold\":" + NpcActorView.MidJourneyWalkHoldSeconds.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"pivotYawSpeed\":" + NpcActorView.PivotYawSpeed.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"vSync\":" + QualitySettings.vSyncCount +
            ",\"selected\":" + Input.NpcSelection.PrimaryId +
            "}";
    }

    private void OnGUI()
    {
        var width = Mathf.Min(440f, Screen.width - 24f);
        GUILayout.BeginArea(new Rect(12f, 12f, width, Screen.height - 24f), GUI.skin.box);
        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label("ЛОКОМОЦИЯ — РЕАЛЬНЫЙ МИР (сид " + _seed + ")");
        GUILayout.Label(_status);

        // ---- the lag recorder: the whole point of this iteration ----
        GUILayout.Space(8f);
        if (_recorder != null && _recorder.IsRecording)
        {
            GUI.color = new Color(1f, 0.45f, 0.4f);
            if (GUILayout.Button(
                    $"⏹ СТОП — записано {_recorder.FrameCount} кадров, {_recorder.Seconds:0.0} с",
                    GUILayout.Height(44f)))
            {
                var path = _recorder.StopRecording();
                _status = string.IsNullOrEmpty(path)
                    ? "Запись пуста — файл не создан."
                    : "Запись: " + System.IO.Path.GetFileName(path);
            }
            GUI.color = Color.white;
        }
        else if (GUILayout.Button("⏺ ЗАПИСАТЬ ЛАГ", GUILayout.Height(44f)))
        {
            _recorder.StartRecording(BuildRecordingMeta());
            _status = "Идёт запись — воспроизведи подозрительный фрагмент.";
        }
        if (_recorder != null && !_recorder.IsRecording &&
            !string.IsNullOrEmpty(_recorder.LastSavedPath))
        {
            GUILayout.Label("Последняя запись: " +
                System.IO.Path.GetFileName(_recorder.LastSavedPath));
        }

        GUILayout.Space(8f);
        _worldSpeed = Slider("Скорость мира", _worldSpeed, 0.05f, 3f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_runner != null && _runner.IsPaused ? "▶ play" : "Ⅱ pause"))
        {
            _runner?.TogglePause();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("Сглаживание вьюхи (живые ручки)");
        NpcActorView.SpeedSmoothTau = Slider(
            "SpeedSmoothTau", NpcActorView.SpeedSmoothTau, 0f, 0.6f);
        NpcActorView.WalkHoldSeconds = Slider(
            "WalkHoldSeconds", NpcActorView.WalkHoldSeconds, 0f, 1f);
        NpcActorView.MidJourneyWalkHoldSeconds = Slider(
            "MidJourneyWalkHold", NpcActorView.MidJourneyWalkHoldSeconds, 0f, 2f);
        NpcActorView.PivotYawSpeed = Slider(
            "PivotYawSpeed", NpcActorView.PivotYawSpeed, 20f, 400f);
        var vSync = QualitySettings.vSyncCount > 0;
        var requestedVSync = GUILayout.Toggle(vSync, " VSync 1 (только этот Play Mode)");
        if (requestedVSync != vSync)
        {
            QualitySettings.vSyncCount = requestedVSync ? 1 : 0;
            Application.targetFrameRate = -1;
        }

        GUILayout.Space(8f);
        GUILayout.Label("Калибровка клипов (ростов тела/с при 1×)");
        NpcActorView view = null;
        var selectedId = Input.NpcSelection.PrimaryId;
        if (selectedId > 0)
        {
            _renderer?.TryGetActorView(selectedId, out view);
        }
        StrideRow("Шаг", view?.ActiveGaitClip(0), NpcActorView.FullWalkBodyHeightsPerSec);
        StrideRow("Трусца", view?.ActiveGaitClip(1),
            NpcActorView.FullWalkBodyHeightsPerSec * NpcActorView.SlowRunCadence);
        StrideRow("Бег", view?.ActiveGaitClip(2),
            NpcActorView.FullWalkBodyHeightsPerSec * NpcActorView.RunCadence);

        DrawTelemetry(view);

#if UNITY_EDITOR
        GUILayout.Space(8f);
        if (GUILayout.Button("СОХРАНИТЬ stride + tuning в игру"))
        {
            var tuning = HexLive.UnityPresentation.Content.AtomicResources.Load<Config.HexTuningConfig>(Config.HexTuning.ResourcePath);
            if (tuning != null)
            {
                Config.HexTuning.Capture(tuning);
                UnityEditor.EditorUtility.SetDirty(tuning);
            }
            if (_animSet != null)
            {
                UnityEditor.EditorUtility.SetDirty(_animSet);
            }
            UnityEditor.AssetDatabase.SaveAssets();
            _status = "Калибровка сохранена в NpcAnimSet и HexTuningConfig.";
        }
#endif
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawTelemetry(NpcActorView view)
    {
        GUILayout.Space(8f);
        GUILayout.Label("Телеметрия выбранной колонистки");
        var npc = SelectedNpc();
        if (npc == null)
        {
            GUILayout.Label("Никто не выбран — кликни по колонистке.");
            return;
        }

        var residual = Mathf.Abs(Mathf.DeltaAngle(
            npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
        var animator = view != null ? view.GetComponentInChildren<Animator>() : null;
        GUILayout.Label(
            $"{npc.DisplayName} · tick {_runner.CurrentTick}\n" +
            $"model {_modelTickSpeed:0.000} · view {_viewSpeed:0.000} wu/s\n" +
            $"status {npc.Movement.Status} · goal {npc.Mind.CurrentGoal} · " +
            $"path {npc.Movement.PathIndex}/{npc.Movement.JunctionPath.Count}\n" +
            $"rotation {npc.RotationDegrees:0.0}° → {npc.Movement.DesiredRotationDegrees:0.0}° · " +
            $"residual {residual:0.0}°\n" +
            $"mobility {npc.Body.MobilityFactor():0.000} · running {npc.Mind.IsRunning} · " +
            $"Animator.speed {(animator != null ? animator.speed : 0f):0.00}");
        GUILayout.Label("Оранжевая сфера = сырая точка модели 4 Гц; тело = интерполированная вьюха.");
    }

    private void StrideRow(string label, AnimationClip clip, float fallback)
    {
        if (clip == null || _animSet == null)
        {
            GUILayout.Label($"{label}: — (нужна выбранная колонистка)");
            return;
        }
        var current = _animSet.StrideFor(clip, fallback);
        var edited = Slider($"{label}: {clip.name}", current, 0.1f, 4f);
        if (!Mathf.Approximately(edited, current))
        {
            _animSet.SetStride(clip, edited);
        }
    }

    private static float Slider(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(250f));
        GUILayout.Label(value.ToString("0.00"), GUILayout.Width(55f));
        GUILayout.EndHorizontal();
        return GUILayout.HorizontalSlider(value, min, max);
    }
}

}
