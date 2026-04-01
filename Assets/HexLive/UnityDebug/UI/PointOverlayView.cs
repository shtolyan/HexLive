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

        private readonly Dictionary<int, VisualElement> _pointElements = new();
        private readonly Dictionary<int, VisualElement> _groupElements = new();
        private readonly HashSet<int> _processedGroups = new();

        private static readonly Color BadgeBg = new(0.05f, 0.05f, 0.06f, 0.75f);
        private static readonly Color ConnectionBg = new(0.04f, 0.07f, 0.04f, 0.80f);
        private static readonly Color ConnectionBorder = new(0.20f, 0.50f, 0.28f);
        private static readonly Color Txt = new(0.85f, 0.86f, 0.88f);
        private static readonly Color Dim = new(0.50f, 0.53f, 0.58f);
        private static readonly Color Green = new(0.24f, 0.72f, 0.34f);

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

            if (!_visible) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var snapshot = _runner != null ? _runner.CreateSnapshot() : null;
            if (snapshot == null) { HideAll(); return; }

            RefreshOverlays(snapshot);
        }

        private void RefreshOverlays(WorldSnapshot snapshot)
        {
            _processedGroups.Clear();
            foreach (var kv in _pointElements) kv.Value.style.display = DisplayStyle.None;
            foreach (var kv in _groupElements) kv.Value.style.display = DisplayStyle.None;

            var groupPoints = new Dictionary<int, List<PointSnapshot>>();
            foreach (var p in snapshot.Points)
            {
                if (p.ConnectionGroupId.HasValue)
                {
                    var gid = p.ConnectionGroupId.Value.Value;
                    if (!groupPoints.ContainsKey(gid))
                        groupPoints[gid] = new List<PointSnapshot>();
                    groupPoints[gid].Add(p);
                }
            }

            foreach (var p in snapshot.Points)
            {
                if (p.ConnectionGroupId.HasValue)
                {
                    var gid = p.ConnectionGroupId.Value.Value;
                    if (_processedGroups.Contains(gid)) continue;
                    _processedGroups.Add(gid);

                    var members = groupPoints[gid];
                    var pos = WorldToPanel(ComputeCentroid(members));
                    if (!pos.HasValue) continue;

                    var card = GetOrCreateGroupCard(gid, members);
                    PlaceAt(card, pos.Value);
                    card.style.display = DisplayStyle.Flex;
                }
                else
                {
                    var pos = WorldToPanel(p.WorldPosition);
                    if (!pos.HasValue) continue;

                    var badge = GetOrCreateBadge(p);
                    UpdateBadge(badge, p);
                    PlaceAt(badge, pos.Value);
                    badge.style.display = DisplayStyle.Flex;
                }
            }
        }

        // ── Interior badge: tiny dot ──

        private VisualElement GetOrCreateBadge(PointSnapshot p)
        {
            var id = p.Id.Value;
            if (_pointElements.TryGetValue(id, out var e)) return e;

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
            _pointElements[id] = b;
            return b;
        }

        private void UpdateBadge(VisualElement b, PointSnapshot p)
        {
            var lbl = b.Q<Label>("t");
            if (lbl == null) return;

            var role = p.Role.ToString();
            var shortRole = role == "Access" ? "" : " " + role[0];
            lbl.text = string.Format("{0}{1}", p.Id.Value, shortRole);

            if (p.Occupied)
                b.style.backgroundColor = new Color(0.35f, 0.08f, 0.08f, 0.80f);
            else if (p.Reserved)
                b.style.backgroundColor = new Color(0.40f, 0.20f, 0.05f, 0.80f);
            else
                b.style.backgroundColor = BadgeBg;
        }

        // ── Connection group: compact card ──

        private VisualElement GetOrCreateGroupCard(int gid, List<PointSnapshot> members)
        {
            if (_groupElements.TryGetValue(gid, out var e))
            {
                e.Clear();
                BuildCard(e, gid, members);
                return e;
            }

            var c = new VisualElement();
            c.pickingMode = PickingMode.Ignore;
            c.style.position = UnityEngine.UIElements.Position.Absolute;
            c.style.backgroundColor = ConnectionBg;
            c.style.borderTopLeftRadius = 3f;
            c.style.borderTopRightRadius = 3f;
            c.style.borderBottomLeftRadius = 3f;
            c.style.borderBottomRightRadius = 3f;
            c.style.borderBottomColor = ConnectionBorder;
            c.style.borderTopColor = ConnectionBorder;
            c.style.borderLeftColor = ConnectionBorder;
            c.style.borderRightColor = ConnectionBorder;
            c.style.borderBottomWidth = 1f;
            c.style.borderTopWidth = 1f;
            c.style.borderLeftWidth = 1f;
            c.style.borderRightWidth = 1f;
            c.style.paddingLeft = 3f;
            c.style.paddingRight = 3f;
            c.style.paddingTop = 2f;
            c.style.paddingBottom = 2f;

            BuildCard(c, gid, members);
            _overlayRoot.Add(c);
            _groupElements[gid] = c;
            return c;
        }

        private static void BuildCard(VisualElement c, int gid, List<PointSnapshot> members)
        {
            // One-line header: "G42 · 3pts"
            var h = new Label(string.Format("G{0} {1}pt", gid, members.Count));
            h.style.color = Green;
            h.style.fontSize = 6;
            h.style.unityFontStyleAndWeight = FontStyle.Bold;
            h.style.unityTextAlign = TextAnchor.MiddleCenter;
            c.Add(h);

            // Compact: list point IDs in one row
            var ids = new System.Text.StringBuilder();
            for (var i = 0; i < members.Count; i++)
            {
                if (i > 0) ids.Append(" ");
                ids.Append(members[i].Id.Value);
            }

            var row = new Label(ids.ToString());
            row.style.color = Dim;
            row.style.fontSize = 5;
            row.style.unityTextAlign = TextAnchor.MiddleCenter;
            c.Add(row);
        }

        // ── Positioning ──

        private Vector2? WorldToPanel(Float2 worldPos)
        {
            var h = SimulationUnityMapper.TileHeight + SimulationUnityMapper.PointMarkerLift;
            var uPos = SimulationUnityMapper.ToUnityPosition(worldPos, h);
            var sp = _cam.WorldToScreenPoint(uPos);
            if (sp.z < 0f) return null;

            // Screen coords: bottom-left origin → flip Y for UI Toolkit (top-left origin)
            var screenX = sp.x;
            var screenY = Screen.height - sp.y;

            // Account for panel DPI scaling
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

        private static Float2 ComputeCentroid(List<PointSnapshot> m)
        {
            var x = 0f; var y = 0f;
            foreach (var p in m) { x += p.WorldPosition.X; y += p.WorldPosition.Y; }
            var c = m.Count > 0 ? m.Count : 1;
            return new Float2(x / c, y / c);
        }

        private void HideAll()
        {
            foreach (var kv in _pointElements) kv.Value.style.display = DisplayStyle.None;
            foreach (var kv in _groupElements) kv.Value.style.display = DisplayStyle.None;
        }
    }
}
