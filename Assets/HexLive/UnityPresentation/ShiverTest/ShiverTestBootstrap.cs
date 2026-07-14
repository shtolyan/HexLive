using HexLive.UnityPresentation.Wearing;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.ShiverTest
{

// Dev scene: spawn Jana with a live NpcActorView and drive her thermal comfort
// so the procedural cold shiver / hot fan can be eyeballed in isolation.
//   C — cold (shiver)   H — hot (fan)   N — neutral
//   ↑/↓ — intensity      RMB drag — orbit   scroll — zoom
// Spec 40.7: the cold branch is a two-sided hug (both arms + spine curl + head
// tuck); this scene exists to tune that pose.
public sealed class ShiverTestBootstrap : MonoBehaviour
{
    private NpcActorView _view;
    private float _thermal = -1f; // start cold so the shiver shows immediately

    private void Awake()
    {
        // The pose keeps updating while the editor window is unfocused.
        Application.runInBackground = true;

        BuildEnvironment();
        SpawnJana();
    }

    private void BuildEnvironment()
    {
        var camGo = new GameObject("ShiverCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
        camGo.AddComponent<ShiverOrbitCamera>();

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
        ground.transform.localScale = Vector3.one * 2f;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.36f, 0.42f, 0.34f));
        mat.SetFloat("_Smoothness", 0f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private void SpawnJana()
    {
        var prefab = Resources.Load<GameObject>("HexLive/Actors/Jana");
        if (prefab == null)
        {
            Debug.LogError("ShiverTest: actor prefab 'HexLive/Actors/Jana' not found");
            return;
        }

        var root = new GameObject("Actor Jana");
        _view = root.AddComponent<NpcActorView>();

        var body = Instantiate(prefab, root.transform);
        body.name = "Jana";

        // Same dormancy rules as the wardrobe scene: an unfed FinalIK solver
        // would fight (or freeze) the procedural thermal layer.
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

        // Lying/hunched poses stretch past authored skin bounds — keep visible.
        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            skin.updateWhenOffscreen = true;
        }

        _view.Construct("Jana");
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.cKey.wasPressedThisFrame)
            {
                _thermal = -1f;
            }
            if (kb.hKey.wasPressedThisFrame)
            {
                _thermal = 1f;
            }
            if (kb.nKey.wasPressedThisFrame)
            {
                _thermal = 0f;
            }
            if (kb.upArrowKey.isPressed)
            {
                _thermal = Mathf.Clamp(_thermal + Time.deltaTime, -1f, 1f);
            }
            if (kb.downArrowKey.isPressed)
            {
                _thermal = Mathf.Clamp(_thermal - Time.deltaTime, -1f, 1f);
            }
        }

        _view?.SetThermal(_thermal);
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12f, 12f, 360f, 470f), GUI.skin.box);

        GUILayout.Label($"thermal = {_thermal:0.00}   (C холод · H жара · N норма · ↑/↓ сила)");
        var state = _thermal < -0.4f ? "ОЗНОБ (холод)" : _thermal > 0.4f ? "жара" : "норма";
        GUILayout.Label($"состояние: {state}");
        GUILayout.Space(8f);
        GUILayout.Label("— настройка дрожи (градусы) —");

        NpcActorView.ShiverTuning.Frequency =
            Row("Частота", NpcActorView.ShiverTuning.Frequency, 5f, 60f);
        NpcActorView.ShiverTuning.ShoulderTremble =
            Row("Плечи · дрожь", NpcActorView.ShiverTuning.ShoulderTremble, 0f, 4f);
        NpcActorView.ShiverTuning.ForearmTremble =
            Row("Предплечья · дрожь", NpcActorView.ShiverTuning.ForearmTremble, 0f, 4f);
        NpcActorView.ShiverTuning.SpineTremble =
            Row("Спина · дрожь", NpcActorView.ShiverTuning.SpineTremble, 0f, 4f);
        NpcActorView.ShiverTuning.HeadTremble =
            Row("Голова · дрожь", NpcActorView.ShiverTuning.HeadTremble, 0f, 4f);
        NpcActorView.ShiverTuning.SpineCurl =
            Row("Спина · наклон", NpcActorView.ShiverTuning.SpineCurl, 0f, 25f);
        NpcActorView.ShiverTuning.HeadTuck =
            Row("Голова · наклон", NpcActorView.ShiverTuning.HeadTuck, 0f, 25f);

        GUILayout.Space(6f);
        if (GUILayout.Button("Вывести значения в консоль"))
        {
            Debug.Log($"[ShiverTuning] Frequency={NpcActorView.ShiverTuning.Frequency:0.###} " +
                      $"ShoulderTremble={NpcActorView.ShiverTuning.ShoulderTremble:0.###} " +
                      $"ForearmTremble={NpcActorView.ShiverTuning.ForearmTremble:0.###} " +
                      $"SpineTremble={NpcActorView.ShiverTuning.SpineTremble:0.###} " +
                      $"HeadTremble={NpcActorView.ShiverTuning.HeadTremble:0.###} " +
                      $"SpineCurl={NpcActorView.ShiverTuning.SpineCurl:0.###} " +
                      $"HeadTuck={NpcActorView.ShiverTuning.HeadTuck:0.###}");
        }

        GUILayout.EndArea();
    }

    private static float Row(string label, float val, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140f));
        val = GUILayout.HorizontalSlider(val, min, max, GUILayout.Width(120f));
        GUILayout.Label(val.ToString("0.00"), GUILayout.Width(44f));
        GUILayout.EndHorizontal();
        return val;
    }
}

// Minimal inspection camera: RMB drag orbits, scroll zooms.
public sealed class ShiverOrbitCamera : MonoBehaviour
{
    private Vector3 _focus = new(0f, 1.0f, 0f);
    private float _yaw = 180f;
    private float _pitch = 6f;
    private float _distance = 3.0f;

    private void LateUpdate()
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.25f, -15f, 85f);
            }

            var scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f)
            {
                _distance = Mathf.Clamp(_distance * (1f - scroll * 0.05f), 0.6f, 10f);
            }
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
        transform.rotation = rotation;
    }
}

}
