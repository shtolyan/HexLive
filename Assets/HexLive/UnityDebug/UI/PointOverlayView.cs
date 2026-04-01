using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityDebug.UI
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class PointOverlayView : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;

        private UIDocument _document;
        private VisualElement _overlayRoot;
        private Camera _cam;
        private bool _visible;

        private readonly Dictionary<int, VisualElement> _badges = new();

        // Edge drawing
        private WorldSnapshot _cachedSnapshot;
        private Material _lineMaterial;

        private static readonly Color BadgeBg = new(0.05f, 0.05f, 0.06f, 0.75f);
        private static readonly Color BlockedBg = new(0.30f, 0.08f, 0.08f, 0.80f);
        private static readonly Color OccupiedBg = new(0.35f, 0.08f, 0.08f, 0.80f);
        private static readonly Color ReservedBg = new(0.40f, 0.20f, 0.05f, 0.80f);
        private static readonly Color BoundaryBg = new(0.04f, 0.07f, 0.04f, 0.80f);
        private static readonly Color Txt = new(0.85f, 0.86f, 0.88f);
        private static readonly Color Green = new(0.24f, 0.72f, 0.34f);
        private static readonly Color EdgePassable = new(0.20f, 0.65f, 0.25f, 0.45f);
        private static readonly Color EdgeBlocked = new(0.75f, 0.15f, 0.15f, 0.55f);

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            if (_document.panelSettings == null)
            {
                var loaded = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
                if (loaded != null)
                    _document.panelSettings = loaded;
                else
                {
                    var ps = ScriptableObject.CreateInstance<PanelSettings>();
                    ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                    ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                    ps.referenceResolution = new Vector2Int(1920, 1080);
                    ps.match = 0.5f;
                    ps.sortingOrder = 100;
                    _document.panelSettings = ps;
                }
            }

            _document.sortingOrder = -10f;

            _overlayRoot = _document.rootVisualElement;
            _overlayRoot.Clear();
            _overlayRoot.pickingMode = PickingMode.Ignore;
            _overlayRoot.style.position = UnityEngine.UIElements.Position.Absolute;
            _overlayRoot.style.left = 0f;
            _overlayRoot.style.top = 0f;
            _overlayRoot.style.right = 0f;
            _overlayRoot.style.bottom = 0f;
            _overlayRoot.style.display = DisplayStyle.None;

            CreateLineMaterial();
        }

        private void Update()
        {
            if (_runner == null)
                _runner = FindFirstObjectByType<SimulationRunnerBehaviour>();

            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
            {
                _visible = !_visible;
                _overlayRoot.style.display = _visible ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (!_visible)
            {
                _cachedSnapshot = null;
                return;
            }

            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var snapshot = _runner != null ? _runner.CreateSnapshot() : null;
            if (snapshot == null) { HideAll(); _cachedSnapshot = null; return; }

            _cachedSnapshot = snapshot;
            RefreshOverlays(snapshot);
        }

        private void OnRenderObject()
        {
            if (!_visible || _cachedSnapshot == null) return;

            DrawEdges(_cachedSnapshot);
        }

        private void RefreshOverlays(WorldSnapshot snapshot)
        {
            foreach (var kv in _badges) kv.Value.style.display = DisplayStyle.None;

            foreach (var junction in snapshot.Junctions)
            {
                var pos = WorldToPanel(junction.WorldPosition);
                if (!pos.HasValue) continue;

                var badge = GetOrCreateBadge(junction);
                UpdateBadge(badge, junction);
                PlaceAt(badge, pos.Value);
                badge.style.display = DisplayStyle.Flex;
            }
        }

        // ── Edge drawing with GL ──

        private void DrawEdges(WorldSnapshot snapshot)
        {
            if (_lineMaterial == null) return;

            var junctionLookup = new Dictionary<int, JunctionSnapshot>();
            foreach (var j in snapshot.Junctions)
            {
                junctionLookup[j.Id.Value] = j;
            }

            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);

            var drawn = new HashSet<long>();
            var lineHeight = SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift * 0.5f;

            foreach (var junction in snapshot.Junctions)
            {
                var fromPos = SimulationUnityMapper.ToUnityPosition(junction.WorldPosition, lineHeight);

                foreach (var neighborId in junction.Neighbors)
                {
                    var a = junction.Id.Value;
                    var b = neighborId.Value;
                    var edgeKey = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!drawn.Add(edgeKey)) continue;

                    if (!junctionLookup.TryGetValue(b, out var neighbor)) continue;

                    var toPos = SimulationUnityMapper.ToUnityPosition(neighbor.WorldPosition, lineHeight);
                    var blocked = junction.Blocked || neighbor.Blocked;

                    GL.Color(blocked ? EdgeBlocked : EdgePassable);
                    GL.Vertex(fromPos);
                    GL.Vertex(toPos);
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        private void CreateLineMaterial()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;

            _lineMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _lineMaterial.SetInt("_ZWrite", 0);
        }

        // ── Badges ──

        private VisualElement GetOrCreateBadge(JunctionSnapshot j)
        {
            var id = j.Id.Value;
            if (_badges.TryGetValue(id, out var e)) return e;

            var b = new VisualElement();
            b.pickingMode = PickingMode.Ignore;
            b.style.position = UnityEngine.UIElements.Position.Absolute;
            b.style.backgroundColor = BadgeBg;
            b.style.borderTopLeftRadius = 10f;
            b.style.borderTopRightRadius = 10f;
            b.style.borderBottomLeftRadius = 10f;
            b.style.borderBottomRightRadius = 10f;
            b.style.paddingLeft = 2f;
            b.style.paddingRight = 2f;
            b.style.paddingTop = 1f;
            b.style.paddingBottom = 1f;
            b.style.flexDirection = FlexDirection.Row;
            b.style.alignItems = Align.Center;

            var lbl = new Label();
            lbl.name = "t";
            lbl.style.color = Txt;
            lbl.style.fontSize = 6;
            lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            b.Add(lbl);

            _overlayRoot.Add(b);
            _badges[id] = b;
            return b;
        }

        private static void UpdateBadge(VisualElement b, JunctionSnapshot j)
        {
            var lbl = b.Q<Label>("t");
            if (lbl == null) return;

            var isBoundary = j.Tiles.Count > 1;
            lbl.text = j.Id.Value.ToString();
            lbl.style.color = isBoundary ? Green : Txt;

            if (j.Blocked)
                b.style.backgroundColor = BlockedBg;
            else if (j.Occupied)
                b.style.backgroundColor = OccupiedBg;
            else if (j.Reserved)
                b.style.backgroundColor = ReservedBg;
            else
                b.style.backgroundColor = isBoundary ? BoundaryBg : BadgeBg;
        }

        // ── Positioning ──

        private Vector2? WorldToPanel(Float2 worldPos)
        {
            var h = SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift;
            var uPos = SimulationUnityMapper.ToUnityPosition(worldPos, h);
            var sp = _cam.WorldToScreenPoint(uPos);
            if (sp.z < 0f) return null;

            var screenX = sp.x;
            var screenY = Screen.height - sp.y;

            var scale = 1f;
            var ps = _document.panelSettings;
            if (ps != null && ps.scaleMode == PanelScaleMode.ConstantPhysicalSize)
            {
                var dpi = Screen.dpi > 0f ? Screen.dpi : 96f;
                scale = ps.referenceDpi / dpi;
            }
            else if (ps != null && ps.scaleMode == PanelScaleMode.ScaleWithScreenSize)
            {
                var refRes = ps.referenceResolution;
                var scaleX = (float)Screen.width / refRes.x;
                var scaleY = (float)Screen.height / refRes.y;
                scale = 1f / Mathf.Lerp(scaleX, scaleY, ps.match);
            }

            return new Vector2(screenX * scale, screenY * scale);
        }

        private static void PlaceAt(VisualElement el, Vector2 pos)
        {
            el.style.left = pos.x;
            el.style.top = pos.y;
            el.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-100f));
        }

        private void HideAll()
        {
            foreach (var kv in _badges) kv.Value.style.display = DisplayStyle.None;
        }
    }
}
