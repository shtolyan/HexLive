#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>One Sims-like world-space work indicator, anchored to the animated head.</summary>
    public sealed class NpcWorldProgressBar : MonoBehaviour
    {
        private const float HeadClearance = 0.42f;
        private const float BarWidth = 0.11f;
        private const float BarHeight = 0.56f;
        private const float BarDepth = 0.055f;
        private Transform? _head;
        private Transform? _fill;
        private Camera? _camera;
        private float _progress;
        private bool _horizontal;
        private float _width = BarWidth;
        private float _height = BarHeight;
        private float _depth = BarDepth;
        private Material? _shellMaterial;
        private Material? _fillMaterial;

        public void Initialize(Transform head, bool horizontal = false)
        {
            _head = head;
            _horizontal = horizontal;
            _width = horizontal ? 0.36f : BarWidth;
            _height = horizontal ? 0.055f : BarHeight;
            _depth = horizontal ? 0.009f : BarDepth;
            _camera = Camera.main;

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            var shellMaterial = new Material(shader) { color = new Color(0.055f, 0.065f, 0.075f, 1f) };
            var fillMaterial = new Material(shader) { color = new Color(0.25f, 0.95f, 0.48f, 1f) };
            _shellMaterial = shellMaterial;
            _fillMaterial = fillMaterial;
            SetBaseColor(shellMaterial, shellMaterial.color);
            SetBaseColor(fillMaterial, fillMaterial.color);

            var shell = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shell.name = "Progress Shell";
            shell.transform.SetParent(transform, false);
            shell.transform.localScale = new Vector3(_width, _height, _depth);
            shell.GetComponent<Renderer>().sharedMaterial = shellMaterial;
            Destroy(shell.GetComponent<Collider>());

            var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fill.name = "Progress Fill";
            fill.transform.SetParent(transform, false);
            _fill = fill.transform;
            fill.GetComponent<Renderer>().sharedMaterial = fillMaterial;
            Destroy(fill.GetComponent<Collider>());

            gameObject.SetActive(false);
        }

        public void SetProgress(float progress, bool visible)
        {
            _progress = Mathf.Clamp01(progress);
            gameObject.SetActive(visible && _head != null);
            UpdateFill();
        }

        private void LateUpdate()
        {
            if (_head == null) return;

            // The animated bone is authoritative: standing, sitting and lying
            // therefore need no posture-specific offsets or hex-centre rules.
            transform.position = _head.position + Vector3.up * (_horizontal ? 0.10f : HeadClearance);
            if (_camera == null) _camera = Camera.main;
            if (_camera != null) transform.rotation = _camera.transform.rotation;

            // Keep a stable world size even though actor prefabs are normalized.
            var parent = transform.parent;
            if (parent != null)
            {
                var scale = parent.lossyScale;
                transform.localScale = new Vector3(
                    Mathf.Approximately(scale.x, 0f) ? 1f : 1f / scale.x,
                    Mathf.Approximately(scale.y, 0f) ? 1f : 1f / scale.y,
                    Mathf.Approximately(scale.z, 0f) ? 1f : 1f / scale.z);
            }
        }

        private void UpdateFill()
        {
            if (_fill == null) return;
            if (_horizontal)
            {
                const float inset = 0.008f;
                var width = Mathf.Max(0.001f, (_width - inset * 2f) * _progress);
                _fill.localScale = new Vector3(width, _height - inset * 2f, _depth + 0.002f);
                _fill.localPosition = new Vector3(-_width * 0.5f + inset + width * 0.5f, 0f, -0.002f);
                return;
            }
            var height = Mathf.Max(0.002f, (BarHeight - 0.035f) * _progress);
            _fill.localScale = new Vector3(BarWidth - 0.035f, height, BarDepth + 0.006f);
            _fill.localPosition = new Vector3(0f, -BarHeight * 0.5f + 0.0175f + height * 0.5f, -0.006f);
        }

        private static void SetBaseColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        }

        private void OnDestroy()
        {
            // Project bars are short-lived; release their runtime materials
            // when the finished item is collected instead of leaking each bill.
            if (_shellMaterial != null) Destroy(_shellMaterial);
            if (_fillMaterial != null) Destroy(_fillMaterial);
        }
    }
}
