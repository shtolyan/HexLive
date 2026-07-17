using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Left-side debug buttons: inject a random wound / clear all wounds, and
    /// make the skin dirtier / cleaner. Acts on the currently selected NPC, or
    /// on everyone when nothing is selected. Debug-only — mutates sim state
    /// directly (so it also breaks determinism; that's fine for inspection).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DebugControlsPanel : MonoBehaviour
    {
        // Presentation-side debug overrides, read by HexWorldRenderer each tick.
        // HideClothing strips garment visuals (sim wardrobe untouched);
        // SweatOverride replaces the sim's ThermalComfort for the sweat decals
        // (null = sim-driven), since the sim recomputes thermal every slow tick.
        public static bool HideClothing;
        public static float? SweatOverride;

        [SerializeField] private SimulationRunnerBehaviour _runner;

        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.94f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Text = new(0.86f, 0.89f, 0.90f);
        private static readonly Color Wound = new(0.72f, 0.28f, 0.30f);
        private static readonly Color Dirt = new(0.52f, 0.44f, 0.32f);
        private static readonly Color TanCol = new(0.58f, 0.40f, 0.24f);

        private static readonly BodyPart[] Parts =
            (BodyPart[])Enum.GetValues(typeof(BodyPart));

        private UIDocument _document;
        private Label _targetLabel;

        // Collapsible like the bottom character bar: hidden by default, a small
        // arrow tab pops it open, the header arrow tucks it away again.
        private bool _collapsed = true;
        private VisualElement _box;
        private VisualElement _expandTab;

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "DebugControlsPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 170;
                _document.panelSettings = settings;
            }

            Build();
        }

        private void Update()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (_targetLabel != null)
            {
                _targetLabel.text = NpcSelection.HasSelection
                    ? $"target: NPC #{NpcSelection.SelectedId}"
                    : "target: everyone";
            }
        }

        private void Build()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.pickingMode = PickingMode.Ignore;

            var box = new VisualElement();
            _box = box;
            box.style.position = Position.Absolute;
            box.style.left = 14f;
            box.style.top = 150f; // below the top-left weather widget
            box.style.width = 176f;
            box.style.flexDirection = FlexDirection.Column;
            box.style.backgroundColor = Panel;
            SetBorder(box, Stroke);
            SetRadius(box, 12f);
            box.style.paddingLeft = 8f;
            box.style.paddingRight = 8f;
            box.style.paddingTop = 8f;
            box.style.paddingBottom = 8f;
            // Block world-picking while the pointer is over the panel, so a
            // button click doesn't also deselect the NPC behind it.
            box.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
            box.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
            root.Add(box);

            // Header row: DEBUG title on the left, a collapse arrow on the right.
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 2f;
            box.Add(header);

            var title = new Label("DEBUG");
            title.style.color = new Color(0.604f, 0.651f, 0.678f);
            title.style.fontSize = 11;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);

            var collapse = new Label("‹");
            collapse.style.color = Text;
            collapse.style.fontSize = 16;
            collapse.style.unityFontStyleAndWeight = FontStyle.Bold;
            collapse.style.paddingLeft = 6f;
            collapse.style.paddingRight = 6f;
            collapse.RegisterCallback<MouseDownEvent>(evt => { SetCollapsed(true); evt.StopPropagation(); });
            header.Add(collapse);

            _targetLabel = new Label("target: everyone");
            _targetLabel.style.color = new Color(0.55f, 0.60f, 0.63f);
            _targetLabel.style.fontSize = 10;
            _targetLabel.style.marginBottom = 8f;
            box.Add(_targetLabel);

            _hexInspectorButton = MakeButton("[ ] Hex inspector", Raised, ToggleHexInspector);
            _hexInspectorLabel = (Label)_hexInspectorButton[0];
            box.Add(_hexInspectorButton);

            box.Add(MakeButton("+ Random wound", Wound, AddRandomWound));
            box.Add(MakeButton("Clear wounds", Raised, ClearWounds));
            box.Add(MakeButton("+ Dirt", Dirt, () => AdjustHygiene(-0.25f)));
            box.Add(MakeButton("- Dirt (wash)", Raised, () => AdjustHygiene(+0.25f)));
            box.Add(MakeButton("+ Tan", TanCol, () => AdjustTan(+0.25f)));
            box.Add(MakeButton("- Tan", Raised, () => AdjustTan(-0.25f)));
            box.Add(MakeButton("+ Sweat", Raised, () => SweatOverride = 0.6f));
            box.Add(MakeButton("- Sweat", Raised, () => SweatOverride = -1f));
            box.Add(MakeButton("+ Tear clothes", Raised, TearClothes));

            _clothesButton = MakeButton("Hide clothes", Raised, ToggleClothes);
            _clothesLabel = (Label)_clothesButton[0];
            box.Add(_clothesButton);

            BuildExpandTab(root);
            ApplyCollapsed(); // hidden by default
        }

        // Small arrow tab (where the panel sits) shown while the panel is hidden;
        // clicking it opens the panel again.
        private void BuildExpandTab(VisualElement root)
        {
            _expandTab = new VisualElement();
            _expandTab.style.position = Position.Absolute;
            _expandTab.style.left = 14f;
            _expandTab.style.top = 150f;
            _expandTab.style.width = 30f;
            _expandTab.style.height = 30f;
            _expandTab.style.flexDirection = FlexDirection.Row;
            _expandTab.style.alignItems = Align.Center;
            _expandTab.style.justifyContent = Justify.Center;
            _expandTab.style.backgroundColor = Panel;
            SetBorder(_expandTab, Stroke);
            SetRadius(_expandTab, 8f);
            _expandTab.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
            _expandTab.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
            _expandTab.RegisterCallback<MouseDownEvent>(evt => { SetCollapsed(false); evt.StopPropagation(); });

            var arrow = new Label("›"); // ›
            arrow.style.color = Text;
            arrow.style.fontSize = 16;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            _expandTab.Add(arrow);

            root.Add(_expandTab);
        }

        private void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
            ApplyCollapsed();
        }

        private void ApplyCollapsed()
        {
            if (_box != null)
            {
                _box.style.display = _collapsed ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_expandTab != null)
            {
                _expandTab.style.display = _collapsed ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private VisualElement _clothesButton;
        private Label _clothesLabel;
        private VisualElement _hexInspectorButton;
        private Label _hexInspectorLabel;

        private void ToggleHexInspector()
        {
            HexSelection.SetEnabled(!HexSelection.Enabled);
            UpdateHexInspectorLabel();
        }

        private void UpdateHexInspectorLabel()
        {
            if (_hexInspectorLabel != null)
            {
                _hexInspectorLabel.text = HexSelection.Enabled
                    ? "[x] Hex inspector"
                    : "[ ] Hex inspector";
            }
        }

        private void ToggleClothes()
        {
            HideClothing = !HideClothing;
            if (_clothesLabel != null)
            {
                _clothesLabel.text = HideClothing ? "Show clothes" : "Hide clothes";
            }
        }

        // ---- actions ----

        private void ForEachTarget(Action<NPCState> action)
        {
            if (_runner?.Engine == null)
            {
                return;
            }

            var npcs = _runner.Engine.World.Entities.Npcs;
            if (NpcSelection.HasSelection)
            {
                foreach (var npc in npcs.Values)
                {
                    if (npc.Id.Value == NpcSelection.SelectedId)
                    {
                        action(npc);
                        return;
                    }
                }

                return; // selected NPC not found (e.g. dead) — do nothing
            }

            foreach (var npc in npcs.Values)
            {
                action(npc);
            }
        }

        private void AddRandomWound() => ForEachTarget(npc =>
        {
            var part = Parts[UnityEngine.Random.Range(0, Parts.Length)];
            npc.Body.Parts[part] = Mathf.Clamp01(npc.Body.Parts[part] - 0.35f);
            npc.Health = npc.Body.Mean();
            npc.Needs.Blood = Mathf.Clamp01(npc.Needs.Blood - 0.15f);
            // Spec 40.8B/40.8-E: the decal comes from the wound RECORD, not
            // from HP, and a hit tears THREE gashes (mirrors WoundMath: the
            // damage splits, so balance math is identical). At the cap (36)
            // reopen an existing wound instead of adding another record.
            for (var gash = 0; gash < 3; gash++)
            {
                if (npc.Wounds.Count >= 36)
                {
                    var reopen = npc.Wounds[UnityEngine.Random.Range(0, npc.Wounds.Count)];
                    reopen.Severity = reopen.Severity * (1f - reopen.Heal01) + 0.35f / 3f;
                    reopen.Heal01 = 0f;
                    continue;
                }

                npc.Wounds.Add(new HexLive.Simulation.Agents.WoundState
                {
                    Id = npc.NextWoundId++,
                    Zone = part,
                    Severity = 0.35f / 3f,
                    Heal01 = 0f,
                    Seed = UnityEngine.Random.Range(1, int.MaxValue)
                });
            }
        });

        private void ClearWounds() => ForEachTarget(npc =>
        {
            foreach (var part in Parts)
            {
                npc.Body.Parts[part] = 1f;
            }

            npc.Health = 1f;
            npc.Needs.Blood = 1f;
            npc.Wounds.Clear();
        });

        private void AdjustHygiene(float delta) => ForEachTarget(npc =>
            npc.Needs.Hygiene = Mathf.Clamp01(npc.Needs.Hygiene + delta));

        private void AdjustTan(float delta) => ForEachTarget(npc =>
            npc.Needs.TanLevel = Mathf.Clamp01(npc.Needs.TanLevel + delta));

        // Rip the worn clothes a step further (durability -0.3) — the tear
        // shader's transparent holes show up without waiting days of wear.
        private void TearClothes() => ForEachTarget(npc =>
        {
            foreach (var item in npc.WornItems)
            {
                item.Durability = Mathf.Max(0.05f, item.Durability - 0.3f);
            }
        });

        // ---- ui helpers ----

        private VisualElement MakeButton(string text, Color accent, Action onClick)
        {
            var b = new VisualElement();
            b.style.flexDirection = FlexDirection.Row;
            b.style.alignItems = Align.Center;
            b.style.height = 30f;
            b.style.marginBottom = 5f;
            b.style.paddingLeft = 10f;
            b.style.paddingRight = 10f;
            b.style.backgroundColor = accent;
            SetRadius(b, 8f);

            var label = new Label(text);
            label.style.color = Text;
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.Add(label);

            b.RegisterCallback<MouseDownEvent>(_ => onClick());
            return b;
        }

        private static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static void SetBorder(VisualElement e, Color c)
        {
            e.style.borderTopWidth = 1f;
            e.style.borderBottomWidth = 1f;
            e.style.borderLeftWidth = 1f;
            e.style.borderRightWidth = 1f;
            e.style.borderTopColor = c;
            e.style.borderBottomColor = c;
            e.style.borderLeftColor = c;
            e.style.borderRightColor = c;
        }
    }
}
