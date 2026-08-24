using System;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.BackWeaponTest
{
    /// <summary>
    /// Play-mode laboratory for the shared back-weapon slot.
    /// A random bare-torso actor is spawned with tool.knife on chestUpper.
    /// Tune public offsetPosition/offsetEuler/offsetScale in the Inspector or
    /// use the small on-screen panel and keyboard nudges; Save Pose prints the
    /// exact values to the Unity console for copying into the shared slot.
    /// </summary>
    public sealed class BackWeaponPlacementTestBootstrap : MonoBehaviour
    {
        [Header("Tool")]
        [Tooltip("One tool for the shared back slot. Keep this as tool.knife for this lab.")]
        public string toolId = "tool.knife";

        [Header("Shared back-slot offsets")]
        [Tooltip("Offset in chestUpper local space, added after the canonical game slot.")]
        public Vector3 offsetPosition = Vector3.zero;
        [Tooltip("Euler offset in chestUpper local space, added after the canonical game rotation.")]
        public Vector3 offsetEuler = Vector3.zero;
        [Tooltip("Multiplier on top of ObjectFit's shared world-size normalization.")]
        public Vector3 offsetScale = Vector3.one;

        [Header("Preview")]
        public bool freezeInIdle = true;
        [Tooltip("Hide the hairstyle so the back-slot silhouette is unobstructed while tuning.")]
        public bool hideHair = true;
        public bool viewBack = true;
        public bool showPanel = true;
        public float nudgeStep = 0.005f;
        public float rotateStep = 2f;
        public float scaleStep = 0.02f;

        private static readonly string[] ActorChoices = { "Marta", "Jana", "Molly", "Jolly" };
        private static readonly Vector3 GameSlotPosition = new Vector3(0.105f, -0.200f, -0.078f);
        private static readonly Quaternion GameSlotRotation = Quaternion.Euler(-3.335f, -0.358f, 18.524f);

        private Camera _camera;
        private GameObject _actorRoot;
        private GameObject _body;
        private GameObject _backProp;
        private BodyBones _bodyBones;
        private Animator _animator;
        private Transform _backBone;
        private Quaternion _prefabAxisCorrection;
        private float _fitScale = 1f;
        private string _actorName;
        private bool _lastViewBack;
        private string _status = "Starting...";

        private void Awake()
        {
            Application.runInBackground = true;
            BuildEnvironment();
            SpawnRandomBareTorso();
            SpawnKnife();
            _lastViewBack = !viewBack;
            UpdateViewCamera();
        }

        private void Update()
        {
            if (_backProp != null)
            {
                ApplyPlacement();
            }

            if (_animator != null)
            {
                _animator.speed = freezeInIdle ? 0f : 1f;
            }

            if (_lastViewBack != viewBack)
            {
                _lastViewBack = viewBack;
                UpdateViewCamera();
            }
        }

        private void OnGUI()
        {
            if (!showPanel)
            {
                return;
            }

            const float width = 365f;
            const float height = 330f;
            GUI.Box(new Rect(12f, 12f, width, height), "Back Weapon Placement");
            GUILayout.BeginArea(new Rect(24f, 42f, width - 24f, height - 38f));
            GUILayout.Label("Actor: " + (_actorName ?? "loading") + "   Tool: " + toolId);
            GUILayout.Label("Bare torso. Camera: " + (viewBack ? "BACK" : "FRONT") + " (V toggles)");
            GUILayout.Space(4f);

            offsetPosition = SliderVector3("Position", offsetPosition, -0.50f, 0.50f, 0.001f);
            offsetEuler = SliderVector3("Rotation", offsetEuler, -180f, 180f, 1f);
            offsetScale = SliderVector3("Scale", offsetScale, 0.25f, 2.5f, 0.01f);

            GUILayout.Space(4f);
            GUILayout.Label("Arrows/XZ/Y: move   Q/E: yaw   R/F: pitch   T/G: roll");
            GUILayout.Label("[ / ]: scale   V: front/back camera");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset offsets", GUILayout.Width(120f)))
            {
                ResetOffsets();
            }
            if (GUILayout.Button("Save Pose", GUILayout.Width(100f)))
            {
                PrintPose();
            }
            if (GUILayout.Button("Hide", GUILayout.Width(70f)))
            {
                showPanel = false;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(_status);
            GUILayout.EndArea();

            HandleKeyboard(Event.current);
        }

        private Vector3 SliderVector3(string label, Vector3 value, float min, float max, float snap)
        {
            GUILayout.Label(label + "  " + Format(value));
            GUILayout.BeginHorizontal();
            value.x = Snap(GUILayout.HorizontalSlider(value.x, min, max), snap);
            value.y = Snap(GUILayout.HorizontalSlider(value.y, min, max), snap);
            value.z = Snap(GUILayout.HorizontalSlider(value.z, min, max), snap);
            GUILayout.EndHorizontal();
            return value;
        }

        private static float Snap(float value, float step)
        {
            if (step <= 0f) return value;
            return Mathf.Round(value / step) * step;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " +
                         value.y.ToString("0.000") + ", " +
                         value.z.ToString("0.000") + ")";
        }

        private void HandleKeyboard(Event e)
        {
            if (e == null || e.type != EventType.KeyDown)
            {
                return;
            }

            var moved = false;
            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: offsetPosition.x -= nudgeStep; moved = true; break;
                case KeyCode.RightArrow: offsetPosition.x += nudgeStep; moved = true; break;
                case KeyCode.UpArrow: offsetPosition.z += nudgeStep; moved = true; break;
                case KeyCode.DownArrow: offsetPosition.z -= nudgeStep; moved = true; break;
                case KeyCode.PageUp: offsetPosition.y += nudgeStep; moved = true; break;
                case KeyCode.PageDown: offsetPosition.y -= nudgeStep; moved = true; break;
                case KeyCode.Q: offsetEuler.y -= rotateStep; moved = true; break;
                case KeyCode.E: offsetEuler.y += rotateStep; moved = true; break;
                case KeyCode.R: offsetEuler.x -= rotateStep; moved = true; break;
                case KeyCode.F: offsetEuler.x += rotateStep; moved = true; break;
                case KeyCode.T: offsetEuler.z -= rotateStep; moved = true; break;
                case KeyCode.G: offsetEuler.z += rotateStep; moved = true; break;
                case KeyCode.LeftBracket: offsetScale -= Vector3.one * scaleStep; moved = true; break;
                case KeyCode.RightBracket: offsetScale += Vector3.one * scaleStep; moved = true; break;
                case KeyCode.V: viewBack = !viewBack; moved = true; break;
                case KeyCode.P:
                    PrintPose();
                    moved = true;
                    break;
            }

            if (moved)
            {
                offsetScale.x = Mathf.Max(0.05f, offsetScale.x);
                offsetScale.y = Mathf.Max(0.05f, offsetScale.y);
                offsetScale.z = Mathf.Max(0.05f, offsetScale.z);
                e.Use();
            }
        }

        private void BuildEnvironment()
        {
            var camGo = new GameObject("BackWeaponTest Camera");
            _camera = camGo.AddComponent<Camera>();
            // Keep this lab camera untagged: PrototypeRuntimeBootstrap owns the
            // tagged gameplay camera and would otherwise replace this fixed
            // placement view with the RTS rig.
            camGo.tag = "Untagged";
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
            _camera.fieldOfView = 35f;
            _camera.nearClipPlane = 0.03f;
            _camera.farClipPlane = 100f;

            var lightGo = new GameObject("BackWeaponTest Sun");
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "BackWeaponTest Ground";
            ground.transform.localScale = Vector3.one * 2f;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (mat != null)
            {
                mat.SetColor("_BaseColor", new Color(0.34f, 0.40f, 0.34f));
                mat.SetFloat("_Smoothness", 0f);
                ground.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        private void SpawnRandomBareTorso()
        {
            _actorName = ActorChoices[UnityEngine.Random.Range(0, ActorChoices.Length)];
            var prefab = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>("HexLive/Actors/" + _actorName);
            if (prefab == null)
            {
                _status = "Actor prefab missing: " + _actorName;
                Debug.LogError("[BackWeaponTest] Actor prefab not found: " + _actorName);
                return;
            }

            _actorRoot = new GameObject("BackWeaponTest Actor " + _actorName);
            _body = Instantiate(prefab, _actorRoot.transform);
            _body.name = _actorName;
            _actorRoot.transform.position = Vector3.zero;
            _actorRoot.transform.rotation = Quaternion.identity;

            _bodyBones = _body.GetComponentInChildren<BodyBones>();
            _animator = _body.GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.Play("Base Layer.Idle", 0, 0f);
                _animator.Update(0f);
                _animator.speed = freezeInIdle ? 0f : 1f;
            }

            if (_bodyBones == null)
            {
                _status = "BodyBones missing";
                return;
            }

            if (Enum.TryParse(_actorName, out ActorName parsed))
            {
                _bodyBones.Construct(parsed);
                if (hideHair)
                {
                    _bodyBones.SetHair(null);
                }
            }

            foreach (var skin in _actorRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skin.updateWhenOffscreen = true;
            }

            _status = "Random bare-torso actor ready.";
        }

        private void SpawnKnife()
        {
            if (_bodyBones == null)
            {
                return;
            }

            _backBone = _bodyBones.GetBone("chestUpper") ??
                        _bodyBones.GetBone("chestLower") ??
                        _bodyBones.GetBone("spine2") ??
                        _bodyBones.GetBone("abdomenUpper");
            if (_backBone == null)
            {
                _status = "No upper-spine bone found.";
                return;
            }

            var model = GearLibrary.LoadPrefab(toolId);
            _backProp = model != null
                ? Instantiate(model, _backBone)
                : HexLive.UnityPresentation.Environment.LowPolyToolFactory.Build(toolId);
            if (_backProp == null)
            {
                _status = "Knife prefab missing.";
                return;
            }

            _backProp.name = "BackWeaponTest Prop " + toolId;
            _backProp.transform.SetParent(_backBone, false);
            _prefabAxisCorrection = _backProp.transform.localRotation;
            _fitScale = ObjectFit.FitScaleFactor(_backProp, toolId);
            _status = "Knife mounted. Tune offsets.";
            ApplyPlacement();
        }

        private void ApplyPlacement()
        {
            if (_backProp == null || _backBone == null)
            {
                return;
            }

            _backProp.transform.localPosition = Vector3.zero;
            _backProp.transform.localScale = Vector3.Scale(Vector3.one * _fitScale, offsetScale);
            _backProp.transform.localRotation = _prefabAxisCorrection;

            var renderers = _backProp.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                _backProp.transform.localRotation = GameSlotRotation * _prefabAxisCorrection *
                    Quaternion.Euler(offsetEuler);
                _backProp.transform.localPosition = GameSlotPosition + offsetPosition;
                return;
            }

            var initialBounds = CombinedBounds(renderers);
            var workingWorld = initialBounds.center - _backProp.transform.position;
            var workingLocal = _backBone.InverseTransformDirection(workingWorld);
            var desiredWorkingLocal = GameSlotRotation * Vector3.down;
            var baseRotation = workingLocal.sqrMagnitude > 0.000001f
                ? Quaternion.FromToRotation(workingLocal.normalized, desiredWorkingLocal.normalized) *
                  _prefabAxisCorrection
                : GameSlotRotation * Quaternion.Euler(0f, 0f, 180f) * _prefabAxisCorrection;

            _backProp.transform.localRotation = baseRotation;
            var combined = CombinedBounds(renderers);
            var boundsCentreLocal = _backBone.InverseTransformPoint(combined.center);
            var basePosition = GameSlotPosition - boundsCentreLocal;

            _backProp.transform.localRotation = baseRotation * Quaternion.Euler(offsetEuler);
            _backProp.transform.localPosition = basePosition + offsetPosition;
        }

        private static Bounds CombinedBounds(Renderer[] renderers)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private void UpdateViewCamera()
        {
            if (_camera == null)
            {
                return;
            }

            var target = new Vector3(0f, 1.05f, 0f);
            var position = viewBack
                ? new Vector3(0f, 1.30f, -3.25f)
                : new Vector3(0f, 1.30f, 3.25f);
            _camera.transform.position = position;
            _camera.transform.LookAt(target);
        }

        private void ResetOffsets()
        {
            offsetPosition = Vector3.zero;
            offsetEuler = Vector3.zero;
            offsetScale = Vector3.one;
            _status = "Offsets reset to the canonical game slot.";
        }

        private void PrintPose()
        {
            _status = "Pose printed to Console.";
            Debug.Log("[BackWeaponTest] " +
                "tool=" + toolId +
                " offsetPosition=" + offsetPosition +
                " offsetEuler=" + offsetEuler +
                " offsetScale=" + offsetScale +
                " actor=" + _actorName);
        }

        private void OnDestroy()
        {
            if (_camera != null && _camera.gameObject != null)
            {
                Destroy(_camera.gameObject);
            }
        }
    }
}
