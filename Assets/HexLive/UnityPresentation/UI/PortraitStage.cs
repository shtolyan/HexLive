using HexLive.UnityPresentation.Rendering;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Renders a live face close-up of the selected NPC into a RenderTexture —
    /// filming the REAL in-world character (actual dirt, tan, wounds, clothes),
    /// not a staged clone. A dedicated camera hovers in front of the face and
    /// follows it every frame. It culls to the "Actors" layer only, so the
    /// character renders against a clean solid backdrop — no environment.
    /// </summary>
    public sealed class PortraitStage : MonoBehaviour
    {
        private const int TextureSize = 320;

        // Framing tuned on the 1.7 m source model (multiplied by world scale):
        // camera sits in front of the face, slightly above, looking back at it.
        private const float FaceDistanceMeters = 0.72f;
        private const float EyeLiftMeters = 0.03f;

        private static readonly Color Backdrop = new(0.10f, 0.12f, 0.14f, 1f);

        private RenderTexture _texture;
        private Camera _camera;
        private HexWorldRenderer _worldRenderer;
        private int _npcId = -1;

        public RenderTexture Texture => _texture;

        private void Awake()
        {
            _texture = new RenderTexture(TextureSize, TextureSize, 16, RenderTextureFormat.ARGB32)
            {
                name = "NpcPortrait",
                antiAliasing = 2
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
            // Actors-only mask is applied in LateUpdate (retried until the
            // layer resolves — an externally added layer may load late).
            _camera.cullingMask = ~(1 << 5); // interim: world minus UI
            _camera.enabled = false;
        }

        /// <summary>Follow this NPC's face; pass a negative id to stop.</summary>
        public void SetTarget(int npcId)
        {
            _npcId = npcId;
            if (_camera != null && npcId < 0)
            {
                _camera.enabled = false;
            }
        }

        private bool _maskResolved;

        // After the world renderer has interpolated the actors for this frame.
        private void LateUpdate()
        {
            if (_camera == null)
            {
                return;
            }

            // Actors-only: the portrait shows the character (clothes, decals,
            // effects — everything on her hierarchy) against the solid
            // backdrop; the environment never renders into the texture.
            if (!_maskResolved)
            {
                var actorsLayer = LayerMask.NameToLayer("Actors");
                if (actorsLayer >= 0)
                {
                    _camera.cullingMask = 1 << actorsLayer;
                    _maskResolved = true;
                }
            }

            if (_npcId < 0)
            {
                _camera.enabled = false;
                return;
            }

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
                if (_worldRenderer == null)
                {
                    return;
                }
            }

            if (!_worldRenderer.TryGetNpcFace(_npcId, out var face, out var forward, out var up, out var scale))
            {
                _camera.enabled = false;
                return;
            }

            // Rigidly pinned to the face rig: the camera hovers straight in
            // front of the face along ITS OWN forward/up axes, so the face
            // stays centered whatever the pose — standing, sitting, lying.
            var eye = face + forward * (FaceDistanceMeters * scale)
                           + up * (EyeLiftMeters * scale);
            _camera.transform.position = eye;
            _camera.transform.rotation = Quaternion.LookRotation(face - eye, up);
            _camera.enabled = true;
        }

        private void OnDestroy()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }
        }
    }
}
