using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.LocomotionTest
{

// §71.5 dev scene: walk one colonist past a measured grid and calibrate her
// gait. Two things are tuned here and nowhere else.
//
//   1. STRIDE. How much ground a clip covers per second at playback 1x. The rig
//      had one such number for the whole cast (the base walk's 0.76), so every
//      clip swapped into the walk slot — the sad walk, the armed walk, the male
//      set, the §50 crawl — was played as if it had the base walk's stride and
//      slid by the ratio between them. Each clip carries its own figure now
//      (NpcAnimSet.strides) and this scene is where those figures come from.
//   2. SMOOTHING. The sim steps at 4 Hz and so does its speed, so the raw
//      frame-to-frame delta pumped the walk cycle four times a second.
//
// The mover is deliberately dumb but FAITHFUL: "sim step" mode advances a pose
// on a 4 Hz clock and the body reads the interpolation between the last two,
// which is exactly what HexWorldRenderer.InterpolateMovables does — including
// the freeze on a planted pivot. Tuning against smooth motion would tune away
// the very stutter the scene exists to see. The pulse buttons inject the sim's
// other stalls (a blocked step, the post-pivot pause) on demand.
//
// Read the feet against the 1-unit grid: at a correct stride the contact foot
// stays on its line for the whole stance.
public sealed class LocomotionTestBootstrap : MonoBehaviour
{
    [Tooltip("Кто ходит. Женские тела берут авторские клипы, Kshishtof — §78 мужской комплект.")]
    [SerializeField] private string _actor = "Jana";
    [Tooltip("Скорость, мировых единиц/с. 1.2 = SimBalance.BaseMoveSpeedFactor, обычный шаг колонии.")]
    [SerializeField] private float _speed = 1.2f;
    [Tooltip("Длина дорожки — до разворота на месте.")]
    [SerializeField] private float _laneLength = 7f;

    private const float SimTick = 0.25f;      // 4 Гц — как WorldBootstrap.TickDeltaTime
    private const string ArmedItemId = "tool.axe_stone";

    private NpcActorView _view;
    private Transform _actorRoot;
    private NpcAnimSet _animSet;
    private LocomotionFollowCamera _follow;

    // Ход симуляции: поза на границах тиков + аккумулятор кадра между ними.
    private Vector3 _prevPos, _currPos;
    private float _prevYaw, _currYaw;
    private float _tickAccum;
    private float _targetZ;
    private int _freezeTicks;                 // «замер на тик» — пульс-кнопки
    private float _pauseSeconds;              // пауза после разворота

    private bool _simStep = true;             // сим-достоверный ход vs гладкий
    private bool _running;
    private bool _armed;
    private bool _sad;
    private float _simSpeed = 1f;
    private bool _paused;

    private void Awake()
    {
        // Ходит и когда окно редактора не в фокусе.
        Application.runInBackground = true;

        BuildEnvironment();
        SpawnActor();

        _targetZ = _laneLength;
        _prevPos = _currPos = Vector3.zero;
        _prevYaw = _currYaw = 0f;
    }

    private void BuildEnvironment()
    {
        var camGo = new GameObject("LocomotionCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
        _follow = camGo.AddComponent<LocomotionFollowCamera>();

        var lightGo = new GameObject("Sun");
        var sun = lightGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.96f, 0.88f);
        sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        lightGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = Vector3.one * 4f;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.34f, 0.39f, 0.33f));
        mat.SetFloat("_Smoothness", 0f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = mat;

        BuildGrid();
    }

    // Мерная сетка: полоска на КАЖДУЮ мировую единицу вдоль дорожки. Скольжение
    // ступни видно только относительно земли — на пустой плоскости и вопиющее
    // проскальзывание читается как нормальная ходьба.
    private void BuildGrid()
    {
        var markMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        markMat.SetColor("_BaseColor", new Color(0.86f, 0.84f, 0.76f));
        markMat.SetFloat("_Smoothness", 0f);

        var root = new GameObject("Grid").transform;
        for (var z = -12; z <= 12; z++)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = $"z{z}";
            bar.transform.SetParent(root);
            // Каждая пятая — шире, чтобы считать метры глазом.
            var thick = z % 5 == 0 ? 0.07f : 0.03f;
            bar.transform.localScale = new Vector3(z % 5 == 0 ? 3f : 1.6f, 0.01f, thick);
            bar.transform.position = new Vector3(0f, 0.006f, z);
            bar.GetComponent<MeshRenderer>().sharedMaterial = markMat;
            Destroy(bar.GetComponent<Collider>());
        }
    }

    private void SpawnActor()
    {
        var prefab = Resources.Load<GameObject>($"HexLive/Actors/{_actor}");
        if (prefab == null)
        {
            Debug.LogError($"LocomotionTest: actor prefab 'HexLive/Actors/{_actor}' not found");
            return;
        }

        var root = new GameObject($"Actor {_actor}");
        _actorRoot = root.transform;
        _view = root.AddComponent<NpcActorView>();

        var body = Instantiate(prefab, root.transform);
        body.name = _actor;
        // ⭐ РОВНО игровой масштаб. Калибровка меряется в ростах тела, поэтому
        // тело другого размера откалибрует другое число (§71.5).
        body.transform.localScale = Vector3.one * HexWorldRenderer.ActorScale;

        // Те же правила спячки, что в остальных сценах-стендах: не накормленный
        // солвер FinalIK дерётся с процедурными слоями (§78.3).
        var fbbik = body.GetComponentInChildren<FullBodyBipedIK>();
        if (fbbik != null)
        {
            fbbik.enabled = false;
        }

        var lookAt = body.GetComponentInChildren<LookAtIK>();
        if (lookAt != null)
        {
            lookAt.solver.IKPositionWeight = 0f;
        }

        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            skin.updateWhenOffscreen = true;
        }

        _view.Construct(_actor);
        _animSet = Resources.Load<NpcAnimSet>("HexLive/NpcAnimSet");
        if (_follow != null)
        {
            _follow.Target = _actorRoot;
        }
    }

    private void Update()
    {
        ReadKeys();

        if (_view == null || _actorRoot == null)
        {
            return;
        }

        if (_paused)
        {
            return;
        }

        if (_simStep)
        {
            StepSim();
        }
        else
        {
            StepSmooth();
        }
    }

    // Гладкий ход: чистая калибровка шага, без 4 Гц-ступенек. Сначала ставим
    // stride здесь, потом переключаемся в сим-режим и смотрим на дёрганость.
    private void StepSmooth()
    {
        var dt = Time.deltaTime * _simSpeed;
        var pos = _actorRoot.position;
        var dir = Mathf.Sign(_targetZ - pos.z);
        pos.z += dir * _speed * dt;
        _actorRoot.position = pos;
        _actorRoot.rotation = Quaternion.Euler(0f, dir > 0f ? 0f : 180f, 0f);

        if (Mathf.Abs(_targetZ - pos.z) < 0.15f)
        {
            _targetZ = -_targetZ;
        }
    }

    // Сим-достоверный ход: поза считается РАЗ В ТИК, тело читает интерполяцию
    // между двумя последними — как HexWorldRenderer.InterpolateMovables.
    private void StepSim()
    {
        var tick = SimTick / Mathf.Max(0.01f, _simSpeed);
        _tickAccum += Time.deltaTime;
        while (_tickAccum >= tick)
        {
            _tickAccum -= tick;
            AdvanceOneTick();
        }

        var alpha = Mathf.Clamp01(_tickAccum / tick);
        _actorRoot.position = Vector3.Lerp(_prevPos, _currPos, alpha);
        _actorRoot.rotation = Quaternion.Slerp(
            Quaternion.Euler(0f, _prevYaw, 0f), Quaternion.Euler(0f, _currYaw, 0f), alpha);
    }

    private void AdvanceOneTick()
    {
        _prevPos = _currPos;
        _prevYaw = _currYaw;

        // Поворот: те же ручки, что у сима, чтобы стенд не разъезжался с игрой.
        var wantYaw = _targetZ > _currPos.z ? 0f : 180f;
        var facingError = Mathf.Abs(Mathf.DeltaAngle(_currYaw, wantYaw));
        var turnPerTick = 90f * SimBalance.BaseTurnSpeedFactor * SimTick;
        _currYaw = Mathf.MoveTowardsAngle(_currYaw, wantYaw, turnPerTick);

        if (_pauseSeconds > 0f)
        {
            _pauseSeconds -= SimTick;
            return;                                   // пауза после разворота
        }

        if (_freezeTicks > 0)
        {
            _freezeTicks--;
            return;                                   // заминка/затор — не двигаемся
        }

        // Выше TurnFreezeAngle сим не переставляет ноги вовсе: встала и
        // развернулась, потом ещё PostTurnPauseSeconds постояла.
        if (facingError > SimBalance.TurnFreezeAngle)
        {
            _pauseSeconds = SimBalance.PostTurnPauseSeconds;
            return;
        }

        // ...а мягкий поворот просто стоит скорости — та самая 4 Гц-ступенька.
        var alignment = 1f;
        if (facingError > SimBalance.TurnFreeAngle)
        {
            var over = (facingError - SimBalance.TurnFreeAngle) /
                Mathf.Max(1f, SimBalance.TurnFreezeAngle - SimBalance.TurnFreeAngle);
            alignment = 1f - Mathf.Clamp01(over) * (1f - SimBalance.TurnMinSpeedFactor);
        }

        var step = _speed * alignment * SimTick;
        var toTarget = _targetZ - _currPos.z;
        if (Mathf.Abs(toTarget) <= step)
        {
            _currPos.z = _targetZ;
            _targetZ = -_targetZ;                     // развернулась и пошла обратно
        }
        else
        {
            _currPos.z += Mathf.Sign(toTarget) * step;
        }
    }

    private void ReadKeys()
    {
        var kb = Keyboard.current;
        if (kb == null)
        {
            return;
        }

        if (kb.rKey.wasPressedThisFrame)
        {
            SetRunning(!_running);
        }

        if (kb.aKey.wasPressedThisFrame)
        {
            SetArmed(!_armed);
        }

        if (kb.sKey.wasPressedThisFrame)
        {
            SetSad(!_sad);
        }

        if (kb.spaceKey.wasPressedThisFrame)
        {
            _paused = !_paused;
        }

        if (kb.fKey.wasPressedThisFrame)
        {
            _freezeTicks += 1;
        }
    }

    private void SetRunning(bool on)
    {
        _running = on;
        _view?.SetRunning(on);
    }

    private void SetArmed(bool on)
    {
        _armed = on;
        // Тот же путь, что в игре: предмет в руке → SetHandProp → стойка.
        _view?.SetInteraction(null, on ? ArmedItemId : null);
    }

    private void SetSad(bool on)
    {
        _sad = on;
        _view?.SetSadWalk(on);
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12f, 12f, 430f, 640f), GUI.skin.box);

        GUILayout.Label($"АКТЁР: {_actor}   ({(_simStep ? "сим-шаг 4 Гц" : "гладко")})");
        GUILayout.Label("R бег · A инструмент · S грусть · F заминка на тик · Space пауза");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_running ? "БЕГ" : "шаг"))
        {
            SetRunning(!_running);
        }

        if (GUILayout.Button(_armed ? "С ТОПОРОМ" : "без топора"))
        {
            SetArmed(!_armed);
        }

        if (GUILayout.Button(_sad ? "ГРУСТНАЯ" : "обычная"))
        {
            SetSad(!_sad);
        }

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_simStep ? "ход: СИМ 4 Гц" : "ход: гладкий"))
        {
            _simStep = !_simStep;
            _prevPos = _currPos = _actorRoot != null ? _actorRoot.position : Vector3.zero;
        }

        if (GUILayout.Button("заминка (1 тик)"))
        {
            _freezeTicks += 1;
        }

        if (GUILayout.Button("разворот"))
        {
            _targetZ = -_targetZ;
        }

        GUILayout.EndHorizontal();

        _speed = Row("Скорость (ед/с)", _speed, 0.1f, 4f);
        var newSim = Mathf.Round(Row("Ускорение мира", _simSpeed, 1f, 8f));
        if (!Mathf.Approximately(newSim, _simSpeed))
        {
            _simSpeed = newSim;
            _view?.SetSimSpeed(_simSpeed);
        }

        GUILayout.Space(6f);
        GUILayout.Label("— КАЛИБРОВКА КЛИПОВ (ростов тела/с на 1×) —");
        StrideRow("Слот 0 · шаг", 0, NpcActorView.FullWalkBodyHeightsPerSec);
        StrideRow("Слот 1 · трусца", 1,
            NpcActorView.FullWalkBodyHeightsPerSec * NpcActorView.SlowRunCadence);
        StrideRow("Слот 2 · бег", 2,
            NpcActorView.FullWalkBodyHeightsPerSec * NpcActorView.RunCadence);

        GUILayout.Space(6f);
        GUILayout.Label("— СГЛАЖИВАНИЕ И ПОРОГИ —");
        NpcActorView.SpeedSmoothTau = Row("Сглаживание τ (с)", NpcActorView.SpeedSmoothTau, 0f, 0.6f);
        NpcActorView.WalkHoldSeconds = Row("Держать шаг (с)", NpcActorView.WalkHoldSeconds, 0f, 1f);
        NpcActorView.PivotYawSpeed = Row("Разворот (град/с)", NpcActorView.PivotYawSpeed, 20f, 400f);
        NpcActorView.MaxWalkCadence = Row("Потолок шага", NpcActorView.MaxWalkCadence, 1f, 3f);
        NpcActorView.MaxGaitCadence = Row("Потолок бега", NpcActorView.MaxGaitCadence, 1f, 2.5f);
        NpcActorView.MinGaitCadence = Row("Пол темпа", NpcActorView.MinGaitCadence, 0.1f, 1f);

        GUILayout.Space(8f);
#if UNITY_EDITOR
        if (GUILayout.Button("СОХРАНИТЬ → NpcAnimSet + HexTuningConfig"))
        {
            Save();
        }
#endif
        if (GUILayout.Button("Вывести значения в консоль"))
        {
            Debug.Log($"[LocomotionTuning] walk={NpcActorView.FullWalkBodyHeightsPerSec:0.###} " +
                      $"slowRun={NpcActorView.SlowRunCadence:0.###} run={NpcActorView.RunCadence:0.###} " +
                      $"tau={NpcActorView.SpeedSmoothTau:0.###} hold={NpcActorView.WalkHoldSeconds:0.###} " +
                      $"pivot={NpcActorView.PivotYawSpeed:0.#} maxWalk={NpcActorView.MaxWalkCadence:0.###} " +
                      $"maxGait={NpcActorView.MaxGaitCadence:0.###} min={NpcActorView.MinGaitCadence:0.###}");
        }

        GUILayout.EndArea();
    }

    // Одна строка калибровки: имя клипа, который РЕАЛЬНО стоит в слоте сейчас,
    // и его stride. Слот 0 меняется на ходу (грусть, топор, ползание) — поэтому
    // строка читает слот, а не догадывается по состоянию.
    private void StrideRow(string label, int slot, float fallback)
    {
        var clip = _view != null ? _view.ActiveGaitClip(slot) : null;
        if (clip == null || _animSet == null)
        {
            GUILayout.Label($"{label}: —");
            return;
        }

        var current = _animSet.StrideFor(clip, fallback);
        GUILayout.Label($"{label}: {clip.name}");
        var edited = Row("    ростов/с", current, 0.1f, 4f);
        if (!Mathf.Approximately(edited, current))
        {
            _animSet.SetStride(clip, edited);
        }
    }

#if UNITY_EDITOR
    private void Save()
    {
        var tuning = Resources.Load<Config.HexTuningConfig>(Config.HexTuning.ResourcePath);
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
        Debug.Log("[LocomotionTest] сохранено: NpcAnimSet.strides + HexTuningConfig");
    }
#endif

    private static float Row(string label, float val, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(150f));
        val = GUILayout.HorizontalSlider(val, min, max, GUILayout.Width(150f));
        GUILayout.Label(val.ToString("0.00"), GUILayout.Width(48f));
        GUILayout.EndHorizontal();
        return val;
    }
}

// Камера едет за актрисой вдоль дорожки: ноги должны оставаться в кадре, иначе
// калибровать нечем. ПКМ — облёт, колесо — приближение.
public sealed class LocomotionFollowCamera : MonoBehaviour
{
    public Transform Target;

    private float _yaw = 90f;
    private float _pitch = 8f;
    private float _distance = 4.5f;

    private void LateUpdate()
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, -10f, 85f);
            }

            var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f)
            {
                _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 1f, 14f);
            }
        }

        var focus = Target != null ? Target.position + Vector3.up * 0.75f : Vector3.up * 0.75f;
        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
