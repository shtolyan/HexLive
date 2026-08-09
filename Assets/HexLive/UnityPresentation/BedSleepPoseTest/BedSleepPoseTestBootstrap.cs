#nullable enable
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.BedSleepPoseTest
{

/// <summary>
/// Visual acceptance scene for the production bed.basic sleep attachment.
/// It deliberately uses the same BedAssembly, Marta prefab, actor scale,
/// NpcActorView.Construct and SetLaying path as HexWorldRenderer.
/// </summary>
public sealed class BedSleepPoseTestBootstrap : MonoBehaviour
{
    private const float ProductionActorScale = 1.5f * (11f / 30f) * 2.4f / 1.7f;
    private const float AuthoredSleepSurfaceY = 0.24f;

    [Header("Sleep placement")]
    [Tooltip("Дополнительный мировой Y-offset поверх штатных 0.24 wu. Можно крутить в Play Mode.")]
    [Range(-0.30f, 0.30f)]
    [SerializeField] private float _sleepYOffset;

    [Tooltip("Показывать ползунок также поверх Game View.")]
    [SerializeField] private bool _showGameViewSlider = true;

    private Transform? _sleepPoint;
    private NpcActorView? _actorView;
    private Material? _floorMaterial;

    public float SleepYOffset
    {
        get => _sleepYOffset;
        set => _sleepYOffset = Mathf.Clamp(value, -0.30f, 0.30f);
    }

    private void Awake()
    {
        Application.runInBackground = true;
        BuildFloor();
        BuildBed();
        BuildMarta();
        BuildLightingAndCamera();
        ApplyPose();
    }

    private void Update() => ApplyPose();

    private void BuildFloor()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Test floor — Y=0";
        floor.transform.SetParent(transform, false);
        floor.transform.localPosition = new Vector3(0f, -0.04f, 0f);
        floor.transform.localScale = new Vector3(4.2f, 0.08f, 4.2f);

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        _floorMaterial = new Material(shader!) { name = "BedSleepTest floor" };
        var color = new Color(0.22f, 0.29f, 0.23f);
        _floorMaterial.color = color;
        if (_floorMaterial.HasProperty("_BaseColor"))
            _floorMaterial.SetColor("_BaseColor", color);
        if (_floorMaterial.HasProperty("_Smoothness"))
            _floorMaterial.SetFloat("_Smoothness", 0.05f);
        floor.GetComponent<Renderer>().sharedMaterial = _floorMaterial;
    }

    private void BuildBed()
    {
        var bed = BedAssembly.BuildFinished("bed.basic");
        if (bed == null)
        {
            Debug.LogError("[BedSleepPoseTest] bed.basic could not be built.", this);
            return;
        }

        bed.name = "Production bed.basic";
        bed.transform.SetParent(transform, false);
        bed.transform.localPosition = Vector3.zero;
        bed.transform.localRotation = Quaternion.identity;

        foreach (var candidate in bed.GetComponentsInChildren<Transform>(true))
        {
            if (candidate.name != "point") continue;
            _sleepPoint = candidate;
            break;
        }

        if (_sleepPoint == null)
            Debug.LogError("[BedSleepPoseTest] production bed has no sleep point.", bed);
    }

    private void BuildMarta()
    {
        var prefab = Resources.Load<GameObject>("HexLive/Actors/Marta");
        if (prefab == null)
        {
            Debug.LogError("[BedSleepPoseTest] Marta prefab is missing.", this);
            return;
        }

        var actorRoot = new GameObject("NPC 1 (Marta) — production view");
        actorRoot.transform.SetParent(transform, false);
        var actorBody = Instantiate(prefab, actorRoot.transform);
        actorBody.name = "Marta";
        actorBody.transform.localPosition = Vector3.zero;
        actorBody.transform.localRotation = Quaternion.identity;
        actorBody.transform.localScale = Vector3.one * ProductionActorScale;

        _actorView = actorRoot.AddComponent<NpcActorView>();
        _actorView.Construct("Marta", 1);
        _actorView.SetInteraction("Sleep", string.Empty);
    }

    private void BuildLightingAndCamera()
    {
        var lightObject = new GameObject("BedSleepTest Sun");
        lightObject.transform.SetParent(transform, false);
        lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        var sun = lightObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.15f;
        sun.color = new Color(1f, 0.91f, 0.78f);

        var cameraObject = new GameObject("BedSleepTest Camera") { tag = "MainCamera" };
        cameraObject.transform.SetParent(transform, false);
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 36f;
        camera.nearClipPlane = 0.05f;
        camera.clearFlags = CameraClearFlags.Skybox;
        cameraObject.AddComponent<BedSleepPoseOrbitCamera>();
    }

    private void ApplyPose()
    {
        if (_sleepPoint == null || _actorView == null) return;
        var actorRoot = _actorView.transform;
        actorRoot.position = new Vector3(_sleepPoint.position.x, 0f, _sleepPoint.position.z);
        actorRoot.rotation = _sleepPoint.rotation;
        _actorView.SetLaying(true, _sleepPoint, AuthoredSleepSurfaceY + _sleepYOffset);
    }

    private void OnGUI()
    {
        if (!_showGameViewSlider) return;
        GUI.Box(new Rect(18f, 18f, 430f, 102f), "BED SLEEP POSE TEST");
        GUI.Label(new Rect(34f, 48f, 390f, 22f),
            $"Sleep Y = {AuthoredSleepSurfaceY + _sleepYOffset:F3} wu  (offset {_sleepYOffset:+0.000;-0.000;0.000})");
        SleepYOffset = GUI.HorizontalSlider(
            new Rect(34f, 80f, 390f, 22f), _sleepYOffset, -0.30f, 0.30f);
        GUI.Label(new Rect(34f, 101f, 390f, 18f), "ПКМ — вращать камеру, колесо — масштаб");
    }

    private void OnDestroy()
    {
        if (_floorMaterial != null) Destroy(_floorMaterial);
    }
}

[DefaultExecutionOrder(10000)]
public sealed class BedSleepPoseOrbitCamera : MonoBehaviour
{
    private Vector3 _focus = new(0f, 0.42f, 0f);
    private float _yaw = 205f;
    private float _pitch = 34f;
    private float _distance = 4.2f;

    private void Start()
    {
        // Main-camera services are installed globally for normal gameplay.
        // This visual fixture owns its camera and must not be steered by them.
        foreach (var behaviour in GetComponents<MonoBehaviour>())
        {
            if (behaviour == this) continue;
            var typeName = behaviour.GetType().Name;
            if (typeName is "RtsCameraController" or "SimulationInputAdapter" or
                "CameraFoliageCuller")
            {
                behaviour.enabled = false;
            }
        }
    }

    private void LateUpdate()
    {
        var actor = FindAnyObjectByType<NpcActorView>();
        if (actor != null)
        {
            var wanted = actor.transform.position + Vector3.up * 0.38f;
            _focus = Vector3.Lerp(_focus, wanted, 1f - Mathf.Exp(-8f * Time.deltaTime));
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, 8f, 82f);
            }

            var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f)
                _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 1.5f, 8f);
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
