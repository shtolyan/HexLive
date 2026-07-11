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

            var title = new Label("DEBUG");
            title.style.color = new Color(0.604f, 0.651f, 0.678f);
            title.style.fontSize = 11;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 2f;
            box.Add(title);

            _targetLabel = new Label("target: everyone");
            _targetLabel.style.color = new Color(0.55f, 0.60f, 0.63f);
            _targetLabel.style.fontSize = 10;
            _targetLabel.style.marginBottom = 8f;
            box.Add(_targetLabel);

            box.Add(MakeButton("+ Random wound", Wound, AddRandomWound));
            box.Add(MakeButton("Clear wounds", Raised, ClearWounds));
            box.Add(MakeButton("+ Dirt", Dirt, () => AdjustHygiene(-0.25f)));
            box.Add(MakeButton("- Dirt (wash)", Raised, () => AdjustHygiene(+0.25f)));
            box.Add(MakeButton("+ Tan", TanCol, () => AdjustTan(+0.25f)));
            box.Add(MakeButton("- Tan", Raised, () => AdjustTan(-0.25f)));
            box.Add(MakeButton("+ Sweat", Raised, () => SweatOverride = 0.6f));
            box.Add(MakeButton("- Sweat", Raised, () => SweatOverride = -1f));

            _clothesButton = MakeButton("Hide clothes", Raised, ToggleClothes);
            _clothesLabel = (Label)_clothesButton[0];
            box.Add(_clothesButton);
        }

        private VisualElement _clothesButton;
        private Label _clothesLabel;

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
        });

        private void ClearWounds() => ForEachTarget(npc =>
        {
            foreach (var part in Parts)
            {
                npc.Body.Parts[part] = 1f;
            }

            npc.Health = 1f;
            npc.Needs.Blood = 1f;
        });

        private void AdjustHygiene(float delta) => ForEachTarget(npc =>
            npc.Needs.Hygiene = Mathf.Clamp01(npc.Needs.Hygiene + delta));

        private void AdjustTan(float delta) => ForEachTarget(npc =>
            npc.Needs.TanLevel = Mathf.Clamp01(npc.Needs.TanLevel + delta));

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
