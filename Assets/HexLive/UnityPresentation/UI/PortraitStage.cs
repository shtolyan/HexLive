using System.Collections.Generic;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Renders the selected live actor into the full identity-card background.
    /// The real hierarchy is filmed (current pose, clothes, dirt and wounds),
    /// but it is isolated on the Portrait layer for this camera render only, so
    /// nearby actors can never enter the frame. A camera-local neon set supplies
    /// the background and can evolve without coupling UI layout to world art.
    /// </summary>
    public sealed class PortraitStage : MonoBehaviour
    {
        // The identity card is 396x286 design units. This near-matching texture
        // gives the full-panel image a little supersampling without paying for
        // a square texture whose sides would immediately be cropped.
        private const int TextureWidth = 512;
        private const int TextureHeight = 368;
        private const float TextureAspect = TextureWidth / (float)TextureHeight;

        // The old round portrait was centred at x=108 inside the 396-wide card.
        // Widening the photograph must reveal more SET, not move the face under
        // the text column, so the camera keeps the subject at this screen anchor.
        private const float SubjectViewportX = 108f / 396f;
        private const float FaceDistanceMeters = 0.72f;
        private const float EyeLiftMeters = 0.03f;
#if UNITY_WEBGL && !UNITY_EDITOR
        // #434 / §168.11: one portrait render costs ~0.9 ms CPU (M1, WebGL),
        // so 30 Hz is ~0.45 ms a frame; the old 6 Hz made blinks and call
        // lip-sync visibly jerky. ?portraitHz=N in the page URL overrides it
        // for measurement (0 = every frame); WebPerf (?perf=1) shows the cost.
        private static readonly float WebRenderIntervalSeconds = WebRenderInterval(30f);
        private float _nextWebRenderAt;

        private static float WebRenderInterval(float defaultHz)
        {
            var url = Application.absoluteURL ?? string.Empty;
            var at = url.IndexOf("portraitHz=", System.StringComparison.Ordinal);
            if (at < 0) return 1f / defaultHz;
            var start = at + "portraitHz=".Length;
            var end = start;
            while (end < url.Length && (char.IsDigit(url[end]) || url[end] == '.')) end++;
            if (!float.TryParse(url.Substring(start, end - start), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var hz)) return 1f / defaultHz;
            return hz <= 0f ? 0f : 1f / hz;
        }
#endif
        private long _perfRenderStart;
        private const float NeonSetDepth = 20f;
        private const float NeonSetOverscan = 1.04f;
        private const string NeonGridShaderPath = "HexLive/UI/PortraitNeonGrid";

        private static readonly Color Backdrop = new(0.012f, 0.021f, 0.031f, 1f);
        private static readonly int UnscaledTimeId = Shader.PropertyToID("_UnscaledTime");

        private RenderTexture _texture;
        private Camera _camera;
        private HexWorldRenderer _worldRenderer;
        private Material _neonGridMaterial;
        private GameObject _neonGrid;
        private int _npcId = -1;
        private int _portraitLayer;
        private bool _layersOverridden;

        // Reused every portrait render. The selected actor may gain/remove
        // clothes and VFX at runtime, so the hierarchy is sampled each render;
        // List overloads keep that operation allocation-free.
        private readonly List<Transform> _portraitTransforms = new(96);
        private readonly List<int> _savedLayers = new(96);

        public RenderTexture Texture => _texture;

        private void Awake()
        {
            _portraitLayer = LayerMask.NameToLayer("Portrait");
            if (_portraitLayer < 0)
            {
                // Fail closed: layer 31 is used only for the synchronous camera
                // pass below. A broad Actors mask would reintroduce photobombing.
                _portraitLayer = 31;
                Debug.LogError("[PortraitStage] Portrait layer is missing; using private layer 31.");
            }

            _texture = new RenderTexture(
                TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32)
            {
                name = "NpcIdentityPortrait",
#if UNITY_WEBGL && !UNITY_EDITOR
                antiAliasing = 1, // §168.11: портрет 512×368 — MSAA в вебе не окупается
#else
                antiAliasing = 2,
#endif
                wrapMode = TextureWrapMode.Clamp
            };
            _texture.Create();

            var camGo = new GameObject("PortraitCamera");
            camGo.transform.SetParent(transform, false);

            _camera = camGo.AddComponent<Camera>();
            _camera.targetTexture = _texture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Backdrop;
            _camera.fieldOfView = 22f;
            _camera.nearClipPlane = 0.03f;
            _camera.farClipPlane = 60f;
            _camera.cullingMask = 1 << _portraitLayer;
            _camera.enabled = false;

            BuildNeonSet(camGo.transform);

            // SRP callbacks own the real game. Built-in callbacks are kept as a
            // harmless guarded fallback for test scenes with no render pipeline.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
        }

        /// <summary>
        /// §174.7: the web page's character card leaves a transparent hole for
        /// the portrait; the camera renders straight into that part of the
        /// screen (normalized, bottom-left). <see cref="ShowInTexture"/> returns
        /// it to the RenderTexture the UI Toolkit card shows.
        /// </summary>
        private bool _onScreen;

        public void ShowOnScreen(Rect viewport)
        {
            if (_camera == null) return;
            _onScreen = true;
            _camera.targetTexture = null;
            _camera.rect = viewport;
            _camera.depth = 40;
            _camera.ResetAspect();
        }

        public void ShowInTexture()
        {
            if (_camera == null || _camera.targetTexture == _texture) return;
            _onScreen = false;
            _camera.rect = new Rect(0, 0, 1, 1);
            _camera.targetTexture = _texture;
            _camera.ResetAspect();
        }

        /// <summary>Film this NPC in the pose-appropriate frame; negative id stops.</summary>
        public void SetTarget(int npcId)
        {
            if (_npcId == npcId)
            {
                return;
            }

            RestoreActorLayers();
            _npcId = npcId;
            if (_camera != null && npcId < 0)
            {
                _camera.enabled = false;
            }
        }

        // After the world renderer has interpolated actor transforms for the frame.
        // §168.11: обёртка профилировщика (?perf=1); вне него — одна проверка флага.
        private void LateUpdate()
        {
            var perfStart = HexLive.UnityPresentation.Diagnostics.WebPerf.Begin();
            LateUpdateCore();
            HexLive.UnityPresentation.Diagnostics.WebPerf.End(HexLive.UnityPresentation.Diagnostics.WebPerf.PortraitLateUpdate, perfStart);
        }

        private void LateUpdateCore()
        {
            if (_camera == null)
            {
                return;
            }

            // Safety net for an interrupted render pipeline: no actor may stay
            // on the Portrait layer beyond its synchronous camera pass.
            RestoreActorLayers();

            if (_neonGridMaterial != null)
            {
                _neonGridMaterial.SetFloat(UnscaledTimeId, Time.unscaledTime);
            }

            if (_npcId < 0)
            {
                _camera.enabled = false;
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            // §168.11 / #434: камера портрета снимает только слой Portrait, но
            // в браузере (один поток) живой портрет всё равно не чаще 30 Гц;
            // между проходами текстура держит прошлый кадр.
            // On the screen there is no texture to hold the last frame: the
            // world camera repaints that rectangle every frame, so a skipped
            // portrait pass shows as flicker (§174.7). Throttle only the texture.
            if (!_onScreen && Time.unscaledTime < _nextWebRenderAt)
            {
                _camera.enabled = false;
                return;
            }
            _nextWebRenderAt = Time.unscaledTime + WebRenderIntervalSeconds;
#endif

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
                if (_worldRenderer == null)
                {
                    _camera.enabled = false;
                    return;
                }
            }

            // §107.4a: sleep animation moves the face through the frame, not
            // the lens around the face. The support is also the actor's own
            // ground/bed pin, so no stale first-frame pose needs to be cached.
            if (_worldRenderer.TryGetActorView(_npcId, out var actor) && actor != null &&
                actor.TryGetLyingPortraitFrame(out var support, out var lyingScale))
            {
                var shot = LyingPortraitFraming.CameraPose(
                    support, lyingScale, _camera.fieldOfView, TextureAspect);
                _camera.transform.SetPositionAndRotation(shot.position, shot.rotation);
                _camera.enabled = true;
                return;
            }

            if (!_worldRenderer.TryGetNpcFace(
                    _npcId, out var face, out var forward, out var up, out var scale))
            {
                _camera.enabled = false;
                return;
            }

            // Start from the old square portrait's exact face framing, then
            // slide the camera parallel to its image plane. Its rotation stays
            // unchanged, so widening the texture reveals only more background:
            // the face remains on the old x=108 UI anchor.
            var baseEye = face + forward * (FaceDistanceMeters * scale)
                               + up * (EyeLiftMeters * scale);
            var levelUp = Vector3.Dot(up, Vector3.up) > 0.5f ? Vector3.up : up;
            var rotation = Quaternion.LookRotation(face - baseEye, levelUp);
            var depth = Mathf.Max(0.01f, Vector3.Dot(
                face - baseEye, rotation * Vector3.forward));
            var halfWidth = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) *
                            depth * TextureAspect;
            var horizontalOffset = (0.5f - SubjectViewportX) * 2f * halfWidth;

            _camera.transform.SetPositionAndRotation(
                baseEye + rotation * Vector3.right * horizontalOffset,
                rotation);
            _camera.enabled = true;
        }

        private void BuildNeonSet(Transform cameraTransform)
        {
            var shader = Resources.Load<Shader>(NeonGridShaderPath);
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning(
                    "[PortraitStage] Neon grid shader is missing or unsupported; " +
                    "using the safe dark camera background.");
                return;
            }

            _neonGridMaterial = new Material(shader)
            {
                name = "PortraitNeonGrid (Runtime)",
                hideFlags = HideFlags.DontSave
            };

            _neonGrid = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _neonGrid.name = "Portrait Neon Grid";
            _neonGrid.layer = _portraitLayer;
            _neonGrid.transform.SetParent(cameraTransform, false);
            _neonGrid.transform.localPosition = new Vector3(0f, 0f, NeonSetDepth);

            var fullHeight = 2f * NeonSetDepth *
                             Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) *
                             NeonSetOverscan;
            _neonGrid.transform.localScale =
                new Vector3(fullHeight * TextureAspect, fullHeight, 1f);

            var collider = _neonGrid.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var renderer = _neonGrid.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _neonGridMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext _, Camera camera)
        {
            if (camera == _camera)
            {
                _perfRenderStart = HexLive.UnityPresentation.Diagnostics.WebPerf.Begin();
                IsolateSelectedActor();
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext _, Camera camera)
        {
            if (camera == _camera)
            {
                RestoreActorLayers();
                HexLive.UnityPresentation.Diagnostics.WebPerf.End(
                    HexLive.UnityPresentation.Diagnostics.WebPerf.PortraitCameraRender, _perfRenderStart);
                _perfRenderStart = 0;
            }
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (camera == _camera)
            {
                IsolateSelectedActor();
            }
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == _camera)
            {
                RestoreActorLayers();
            }
        }

        private void IsolateSelectedActor()
        {
            if (_layersOverridden || _npcId < 0 || _worldRenderer == null ||
                !_worldRenderer.TryGetActorView(_npcId, out var actorView) || actorView == null)
            {
                return;
            }

            _portraitTransforms.Clear();
            _savedLayers.Clear();
            actorView.GetComponentsInChildren(true, _portraitTransforms);
            for (var i = 0; i < _portraitTransforms.Count; i++)
            {
                var target = _portraitTransforms[i];
                _savedLayers.Add(target.gameObject.layer);
                target.gameObject.layer = _portraitLayer;
            }

            _layersOverridden = true;
        }

        private void RestoreActorLayers()
        {
            if (!_layersOverridden)
            {
                return;
            }

            var count = Mathf.Min(_portraitTransforms.Count, _savedLayers.Count);
            for (var i = 0; i < count; i++)
            {
                var target = _portraitTransforms[i];
                if (target != null)
                {
                    target.gameObject.layer = _savedLayers[i];
                }
            }

            _layersOverridden = false;
            _portraitTransforms.Clear();
            _savedLayers.Clear();
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            RestoreActorLayers();

            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_neonGridMaterial != null)
            {
                Destroy(_neonGridMaterial);
            }

            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }
        }
    }
}
