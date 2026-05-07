using System;
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityDebug.UI
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class SimulationDebugPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;

        private readonly float[] _speedPresets = { 0.25f, 1f, 4f, 16f, 64f };

        private UIDocument _document;

        // HUD elements
        private Label _hudNpcName;
        private Label _hudTileValue;
        private Label _hudGoalValue;
        private Label _hudInteractionValue;
        private VisualElement _hungerBar;
        private VisualElement _energyBar;
        private VisualElement _comfortBar;
        private VisualElement _thermalBar;
        private Label _hungerLabel;
        private Label _energyLabel;
        private Label _comfortLabel;
        private Label _thermalLabel;

        // Debug elements
        private Foldout _debugFoldout;
        private Label _readyValue;
        private Label _tickValue;
        private Label _pausedValue;
        private Label _speedValue;
        private Label _planValue;
        private Label _targetTileValue;
        private Label _movementValue;
        private Label _executionValue;
        private Label _pathValue;
        private Label _temperatureValue;
        private Label _reservedValue;
        private Label _occupiedValue;
        private VisualElement _scoresContainer;
        private VisualElement _traceContainer;

        // Button refs for active speed highlight
        private readonly List<Button> _speedButtons = new();
        private Button _pauseButton;

        // Toggle visibility
        private bool _visible = true;

        // Colors
        private static readonly Color PanelBg = new(0.09f, 0.09f, 0.10f, 0.96f);
        private static readonly Color PanelBorder = new(0.20f, 0.20f, 0.24f);
        private static readonly Color SectionBg = new(0.12f, 0.12f, 0.14f, 0.90f);
        private static readonly Color TextPrimary = new(0.93f, 0.94f, 0.95f);
        private static readonly Color TextSecondary = new(0.60f, 0.63f, 0.68f);
        private static readonly Color TextMuted = new(0.45f, 0.48f, 0.54f);
        private static readonly Color AccentBlue = new(0.30f, 0.56f, 0.92f);
        private static readonly Color ButtonBg = new(0.16f, 0.17f, 0.20f);
        private static readonly Color ButtonHover = new(0.22f, 0.23f, 0.27f);
        private static readonly Color ButtonActive = new(0.26f, 0.28f, 0.34f);
        private static readonly Color BarTrack = new(0.14f, 0.14f, 0.16f);

        // Need bar colors
        private static readonly Color HungerColor = new(0.92f, 0.55f, 0.20f);
        private static readonly Color EnergyColor = new(0.40f, 0.78f, 0.35f);
        private static readonly Color ComfortColor = new(0.40f, 0.62f, 0.92f);
        private static readonly Color ThermalColor = new(0.90f, 0.35f, 0.35f);
        private static readonly Color ScoreBarColor = new(0.55f, 0.45f, 0.85f);

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            if (_document.panelSettings == null)
            {
                _document.panelSettings = LoadOrCreatePanelSettings();
            }

            BuildUi();
        }

        private void Update()
        {
            if (_runner == null)
            {
                _runner = FindFirstObjectByType<SimulationRunnerBehaviour>();
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.tabKey.wasPressedThisFrame)
            {
                _visible = !_visible;
                _document.rootVisualElement.style.display = _visible
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }

            if (!_visible) return;

            Refresh();
        }

        // ─────────────────────────────────────────
        // UI Construction
        // ─────────────────────────────────────────

        private void BuildUi()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.style.flexDirection = FlexDirection.Row;
            root.style.justifyContent = Justify.FlexStart;
            root.style.alignItems = Align.FlexStart;
            root.style.paddingLeft = 14f;
            root.style.paddingRight = 14f;
            root.style.paddingTop = 14f;
            root.style.paddingBottom = 14f;
            root.pickingMode = PickingMode.Ignore;

            var hudPanel = CreatePanel(300f);
            hudPanel.style.marginRight = 10f;
            root.Add(hudPanel);

            var debugPanel = CreatePanel(400f);
            root.Add(debugPanel);

            BuildHud(hudPanel);
            BuildDebug(debugPanel);
        }

        private void BuildHud(VisualElement panel)
        {
            panel.Add(CreateHeader("NPC HUD"));

            var infoSection = CreateSection();
            _hudNpcName = AddKeyValue(infoSection, "NPC");
            _hudTileValue = AddKeyValue(infoSection, "Tile");
            _hudGoalValue = AddKeyValue(infoSection, "Goal");
            _hudInteractionValue = AddKeyValue(infoSection, "Interaction");
            panel.Add(infoSection);

            panel.Add(CreateSectionTitle("Needs"));

            var needsSection = CreateSection();
            _hungerBar = AddNeedBar(needsSection, "Hunger", "Pressure to find food", HungerColor, out _hungerLabel);
            _energyBar = AddNeedBar(needsSection, "Energy", "Current rest reserve", EnergyColor, out _energyLabel);
            _comfortBar = AddNeedBar(needsSection, "Comfort", "Current comfort level", ComfortColor, out _comfortLabel);
            _thermalBar = AddNeedBar(needsSection, "Thermal", "Temperature discomfort", ThermalColor, out _thermalLabel);
            panel.Add(needsSection);
        }

        private void BuildDebug(VisualElement panel)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.maxHeight = Length.Percent(100);
            panel.Add(scroll);

            var content = scroll.contentContainer;

            // Status section
            content.Add(CreateHeader("Debug Tools"));

            var statusSection = CreateSection();
            _readyValue = AddKeyValue(statusSection, "Ready");
            _tickValue = AddKeyValue(statusSection, "Tick");
            _pausedValue = AddKeyValue(statusSection, "Paused");
            _speedValue = AddKeyValue(statusSection, "Speed");
            content.Add(statusSection);

            // Controls
            content.Add(CreateSectionTitle("Controls"));
            var controlsSection = CreateSection();

            var mainRow = CreateRow();
            _pauseButton = CreateStyledButton("Pause / Resume", OnTogglePause);
            mainRow.Add(_pauseButton);
            mainRow.Add(CreateStyledButton("Step Tick", OnStepTick));
            controlsSection.Add(mainRow);

            var speedLabel = new Label("Speed Presets");
            speedLabel.style.color = TextMuted;
            speedLabel.style.fontSize = 10;
            speedLabel.style.marginBottom = 4f;
            speedLabel.style.marginTop = 2f;
            controlsSection.Add(speedLabel);

            var speedRow = CreateRow();
            _speedButtons.Clear();
            foreach (var speed in _speedPresets)
            {
                var captured = speed;
                var btn = CreateStyledButton(string.Format("{0:0.##}x", captured), () => OnSetSpeed(captured));
                btn.style.minWidth = 50f;
                btn.style.flexGrow = 1f;
                _speedButtons.Add(btn);
                speedRow.Add(btn);
            }
            controlsSection.Add(speedRow);
            content.Add(controlsSection);

            // Simulation details
            content.Add(CreateSectionTitle("Simulation"));
            var simSection = CreateSection();
            _planValue = AddKeyValue(simSection, "Plan");
            _targetTileValue = AddKeyValue(simSection, "Target Tile");
            _movementValue = AddKeyValue(simSection, "Movement");
            _executionValue = AddKeyValue(simSection, "Execution");
            _pathValue = AddKeyValue(simSection, "Path");
            _temperatureValue = AddKeyValue(simSection, "Temperature");
            _reservedValue = AddKeyValue(simSection, "Reserved Pts");
            _occupiedValue = AddKeyValue(simSection, "Occupied Pts");
            content.Add(simSection);

            // Goal Scores
            content.Add(CreateSectionTitle("Goal Scores"));
            _scoresContainer = CreateSection();
            content.Add(_scoresContainer);

            // Trace Log
            content.Add(CreateSectionTitle("Trace Log"));
            var traceSection = CreateSection();
            traceSection.style.maxHeight = 400f;
            traceSection.style.overflow = Overflow.Hidden;
            var traceScroll = new ScrollView(ScrollViewMode.Vertical);
            traceScroll.style.flexGrow = 1f;
            _traceContainer = new VisualElement();
            traceScroll.Add(_traceContainer);
            traceSection.Add(traceScroll);
            content.Add(traceSection);
        }

        // ─────────────────────────────────────────
        // Refresh
        // ─────────────────────────────────────────

        private void Refresh()
        {
            if (_readyValue == null) return;

            var isReady = _runner != null && _runner.IsReady;
            _readyValue.text = isReady ? "Yes" : "No";
            _readyValue.style.color = isReady ? EnergyColor : ThermalColor;

            _tickValue.text = _runner != null ? _runner.CurrentTick.ToString() : "0";

            var isPaused = _runner != null && _runner.IsPaused;
            _pausedValue.text = isPaused ? "Yes" : "No";
            _pausedValue.style.color = isPaused ? HungerColor : EnergyColor;

            var speed = _runner != null ? _runner.SpeedMultiplier : 0f;
            _speedValue.text = string.Format("{0:0.##}x", speed);

            UpdateSpeedButtonHighlights(speed);

            var snapshot = _runner != null ? _runner.CreateSnapshot() : null;
            if (snapshot == null || snapshot.Npcs.Count == 0)
            {
                SetFallback();
                UpdateScores(null);
                UpdateTrace(null);
                return;
            }

            var npc = snapshot.Npcs[0];
            _hudNpcName.text = string.Format("NPC #{0}", npc.Id.Value);
            _hudTileValue.text = string.Format("({0}, {1})", npc.Tile.Q, npc.Tile.R);
            _hudGoalValue.text = npc.CurrentGoal;
            _hudGoalValue.style.color = GetGoalColor(npc.CurrentGoal);
            _hudInteractionValue.text = npc.CurrentInteraction;

            SetNeedBar(_hungerBar, _hungerLabel, "Hunger", npc.Hunger, HungerColor);
            SetNeedBar(_energyBar, _energyLabel, "Energy", npc.Energy, EnergyColor);
            SetNeedBar(_comfortBar, _comfortLabel, "Comfort", npc.Comfort, ComfortColor);
            SetNeedBar(_thermalBar, _thermalLabel, "Thermal", npc.ThermalDiscomfort, ThermalColor);

            _planValue.text = npc.PlanStatus;
            _targetTileValue.text = FormatTile(npc.TargetTile);
            _movementValue.text = npc.MovementStatus;
            _movementValue.style.color = GetMovementColor(npc.MovementStatus);
            _executionValue.text = npc.ExecutionStatus;
            _executionValue.style.color = GetExecutionColor(npc.ExecutionStatus);
            _pathValue.text = npc.Path.Count == 0 ? "-" : FormatPath(npc.Path);
            _temperatureValue.text = string.Format("{0:0.0} C", snapshot.Temperature);
            _temperatureValue.style.color = snapshot.Temperature < 12f ? ThermalColor : EnergyColor;
            _reservedValue.text = CountReserved(snapshot).ToString();
            _occupiedValue.text = CountOccupied(snapshot).ToString();

            UpdateScores(npc);
            UpdateTrace(snapshot);
        }

        private void SetFallback()
        {
            _hudNpcName.text = "-";
            _hudTileValue.text = "-";
            _hudGoalValue.text = "-";
            _hudGoalValue.style.color = TextSecondary;
            _hudInteractionValue.text = "-";
            _planValue.text = "-";
            _targetTileValue.text = "-";
            _movementValue.text = "-";
            _movementValue.style.color = TextPrimary;
            _executionValue.text = "-";
            _executionValue.style.color = TextPrimary;
            _pathValue.text = "-";
            _temperatureValue.text = "-";
            _temperatureValue.style.color = TextPrimary;
            _reservedValue.text = "-";
            _occupiedValue.text = "-";
            SetNeedBar(_hungerBar, _hungerLabel, "Hunger", 0f, HungerColor);
            SetNeedBar(_energyBar, _energyLabel, "Energy", 0f, EnergyColor);
            SetNeedBar(_comfortBar, _comfortLabel, "Comfort", 0f, ComfortColor);
            SetNeedBar(_thermalBar, _thermalLabel, "Thermal", 0f, ThermalColor);
        }

        private void UpdateScores(NpcSnapshot npc)
        {
            _scoresContainer.Clear();

            if (npc == null || npc.GoalScores.Count == 0)
            {
                _scoresContainer.Add(CreateValueLabel("-"));
                return;
            }

            foreach (var score in npc.GoalScores)
            {
                var row = new VisualElement();
                row.style.marginBottom = 6f;

                // Header row: goal name + score value
                var headerRow = CreateRow();
                headerRow.style.marginBottom = 2f;
                var nameLabel = new Label(score.Goal);
                nameLabel.style.color = GetGoalColor(score.Goal);
                nameLabel.style.fontSize = 11;
                nameLabel.style.flexGrow = 1f;
                var valueLabel = new Label(string.Format("{0:0.000}", score.FinalScore));
                valueLabel.style.color = TextSecondary;
                valueLabel.style.fontSize = 11;
                headerRow.Add(nameLabel);
                headerRow.Add(valueLabel);
                row.Add(headerRow);

                // Bar
                var barContainer = CreateBarTrack(14f);
                var fill = CreateBarFill(Mathf.Clamp01(score.FinalScore), ScoreBarColor);
                barContainer.Add(fill);
                row.Add(barContainer);

                _scoresContainer.Add(row);
            }
        }

        private void UpdateTrace(WorldSnapshot snapshot)
        {
            _traceContainer.Clear();

            if (snapshot == null || snapshot.TraceEvents.Count == 0)
            {
                _traceContainer.Add(CreateValueLabel("No events yet (buffer empty)"));
                return;
            }

            // Show event count header
            var countLabel = new Label(string.Format("Events in buffer: {0}", snapshot.TraceEvents.Count));
            countLabel.style.color = TextMuted;
            countLabel.style.fontSize = 9;
            countLabel.style.marginBottom = 4f;
            _traceContainer.Add(countLabel);

            var start = Mathf.Max(0, snapshot.TraceEvents.Count - 30);
            for (var i = start; i < snapshot.TraceEvents.Count; i++)
            {
                var trace = snapshot.TraceEvents[i];

                var entry = new VisualElement();
                entry.style.marginBottom = 3f;
                entry.style.paddingBottom = 3f;
                if (i < snapshot.TraceEvents.Count - 1)
                {
                    entry.style.borderBottomWidth = 1f;
                    entry.style.borderBottomColor = new Color(0.18f, 0.18f, 0.22f);
                }

                var tickLabel = new Label(string.Format("[{0}]", trace.Tick));
                tickLabel.style.color = AccentBlue;
                tickLabel.style.fontSize = 10;

                var typeLabel = new Label(trace.Type);
                typeLabel.style.color = TextSecondary;
                typeLabel.style.fontSize = 10;
                typeLabel.style.marginLeft = 4f;
                typeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

                var topRow = CreateRow();
                topRow.style.marginBottom = 1f;
                topRow.Add(tickLabel);
                topRow.Add(typeLabel);
                entry.Add(topRow);

                var msgLabel = new Label(trace.Message);
                msgLabel.style.color = TextPrimary;
                msgLabel.style.fontSize = 10;
                msgLabel.style.whiteSpace = WhiteSpace.Normal;
                entry.Add(msgLabel);

                _traceContainer.Add(entry);
            }
        }

        private void UpdateSpeedButtonHighlights(float currentSpeed)
        {
            for (var i = 0; i < _speedButtons.Count && i < _speedPresets.Length; i++)
            {
                var isActive = Mathf.Approximately(_speedPresets[i], currentSpeed);
                _speedButtons[i].style.backgroundColor = isActive ? AccentBlue : ButtonBg;
                _speedButtons[i].style.color = isActive ? Color.white : TextPrimary;
            }
        }

        // ─────────────────────────────────────────
        // Actions
        // ─────────────────────────────────────────

        private void OnTogglePause()
        {
            if (_runner != null) _runner.TogglePause();
        }

        private void OnStepTick()
        {
            if (_runner != null) _runner.StepSingleTick();
        }

        private void OnSetSpeed(float speed)
        {
            if (_runner != null) _runner.SetSpeed(speed);
        }

        // ─────────────────────────────────────────
        // Custom Need Bar (no ProgressBar widget)
        // ─────────────────────────────────────────

        private static VisualElement AddNeedBar(VisualElement parent, string label, string description,
            Color color, out Label valueLabel)
        {
            var container = new VisualElement();
            container.style.marginBottom = 8f;

            // Description above bar
            var desc = new Label(description);
            desc.style.color = TextMuted;
            desc.style.fontSize = 9;
            desc.style.marginBottom = 3f;
            container.Add(desc);

            // Bar track — taller to fit text inside
            var track = CreateBarTrack(22f);

            // Fill
            var fill = CreateBarFill(0f, color);
            fill.name = "fill";
            track.Add(fill);

            // Overlay row with label + value inside the bar
            var overlay = new VisualElement();
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0f;
            overlay.style.right = 0f;
            overlay.style.top = 0f;
            overlay.style.bottom = 0f;
            overlay.style.flexDirection = FlexDirection.Row;
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.SpaceBetween;
            overlay.style.paddingLeft = 8f;
            overlay.style.paddingRight = 8f;

            var nameLabel = new Label(label);
            nameLabel.style.color = TextPrimary;
            nameLabel.style.fontSize = 11;
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            // Text shadow via outline for readability over bar
            nameLabel.style.textShadow = new TextShadow
            {
                offset = new Vector2(0f, 1f),
                blurRadius = 3f,
                color = new Color(0f, 0f, 0f, 0.8f)
            };

            valueLabel = new Label("0%");
            valueLabel.style.color = TextPrimary;
            valueLabel.style.fontSize = 11;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.textShadow = new TextShadow
            {
                offset = new Vector2(0f, 1f),
                blurRadius = 3f,
                color = new Color(0f, 0f, 0f, 0.8f)
            };

            overlay.Add(nameLabel);
            overlay.Add(valueLabel);
            track.Add(overlay);

            container.Add(track);
            parent.Add(container);
            return track;
        }

        private static void SetNeedBar(VisualElement barTrack, Label valueLabel, string label,
            float normalizedValue, Color color)
        {
            var clamped = Mathf.Clamp01(normalizedValue);
            var fill = barTrack.Q("fill");
            if (fill != null)
            {
                fill.style.width = Length.Percent(clamped * 100f);
                fill.style.backgroundColor = LerpBarColor(color, clamped);
            }

            valueLabel.text = string.Format("{0:0}%", clamped * 100f);
        }

        private static Color LerpBarColor(Color baseColor, float value)
        {
            // Dim the color slightly at low values, brighten at high
            var brightness = Mathf.Lerp(0.5f, 1.0f, value);
            return new Color(
                baseColor.r * brightness,
                baseColor.g * brightness,
                baseColor.b * brightness,
                1f
            );
        }

        // ─────────────────────────────────────────
        // UI Primitives
        // ─────────────────────────────────────────

        private static VisualElement CreateBarTrack(float height)
        {
            var track = new VisualElement();
            track.style.height = height;
            track.style.backgroundColor = BarTrack;
            track.style.borderTopLeftRadius = 4f;
            track.style.borderTopRightRadius = 4f;
            track.style.borderBottomLeftRadius = 4f;
            track.style.borderBottomRightRadius = 4f;
            track.style.overflow = Overflow.Hidden;
            return track;
        }

        private static VisualElement CreateBarFill(float normalizedValue, Color color)
        {
            var fill = new VisualElement();
            fill.style.height = Length.Percent(100f);
            fill.style.width = Length.Percent(Mathf.Clamp01(normalizedValue) * 100f);
            fill.style.backgroundColor = color;
            fill.style.borderTopLeftRadius = 4f;
            fill.style.borderTopRightRadius = 4f;
            fill.style.borderBottomLeftRadius = 4f;
            fill.style.borderBottomRightRadius = 4f;
            fill.style.position = Position.Absolute;
            fill.style.left = 0f;
            fill.style.top = 0f;
            return fill;
        }

        private static VisualElement CreatePanel(float width)
        {
            var panel = new VisualElement();
            panel.style.width = width;
            panel.style.maxHeight = Length.Percent(96);
            panel.style.backgroundColor = PanelBg;
            panel.style.borderBottomColor = PanelBorder;
            panel.style.borderTopColor = PanelBorder;
            panel.style.borderLeftColor = PanelBorder;
            panel.style.borderRightColor = PanelBorder;
            panel.style.borderBottomWidth = 1f;
            panel.style.borderTopWidth = 1f;
            panel.style.borderLeftWidth = 1f;
            panel.style.borderRightWidth = 1f;
            panel.style.borderTopLeftRadius = 10f;
            panel.style.borderTopRightRadius = 10f;
            panel.style.borderBottomLeftRadius = 10f;
            panel.style.borderBottomRightRadius = 10f;
            panel.style.paddingLeft = 14f;
            panel.style.paddingRight = 14f;
            panel.style.paddingTop = 14f;
            panel.style.paddingBottom = 14f;
            panel.style.overflow = Overflow.Hidden;
            return panel;
        }

        private static VisualElement CreateSection()
        {
            var section = new VisualElement();
            section.style.backgroundColor = SectionBg;
            section.style.borderTopLeftRadius = 6f;
            section.style.borderTopRightRadius = 6f;
            section.style.borderBottomLeftRadius = 6f;
            section.style.borderBottomRightRadius = 6f;
            section.style.paddingLeft = 10f;
            section.style.paddingRight = 10f;
            section.style.paddingTop = 8f;
            section.style.paddingBottom = 8f;
            section.style.marginBottom = 6f;
            return section;
        }

        private static PanelSettings LoadOrCreatePanelSettings()
        {
            var loaded = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (loaded != null)
            {
                return loaded;
            }

            UnityEngine.Debug.LogWarning(
                "[HexLive] DebugPanelSettings asset not found in Resources. " +
                "Text may not render. Open Unity Editor to auto-generate it " +
                "(or use menu HexLive > Regenerate Debug Panel Settings).");

            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            panelSettings.sortingOrder = 200;
            panelSettings.targetDisplay = 0;
            return panelSettings;
        }

        private static Label CreateHeader(string text)
        {
            var label = new Label(text);
            label.style.color = TextPrimary;
            label.style.fontSize = 16;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 8f;
            label.style.letterSpacing = 0.5f;
            return label;
        }

        private static Label CreateSectionTitle(string text)
        {
            var label = new Label(text.ToUpperInvariant());
            label.style.color = TextMuted;
            label.style.fontSize = 10;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 6f;
            label.style.marginBottom = 4f;
            label.style.letterSpacing = 1.2f;
            return label;
        }

        private static Label AddKeyValue(VisualElement parent, string key)
        {
            var row = CreateRow();
            row.style.marginBottom = 3f;

            var keyLabel = new Label(key);
            keyLabel.style.minWidth = 100f;
            keyLabel.style.color = TextSecondary;
            keyLabel.style.fontSize = 11;

            var valueLabel = CreateValueLabel("-");
            valueLabel.style.flexGrow = 1f;
            valueLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            row.Add(keyLabel);
            row.Add(valueLabel);
            parent.Add(row);
            return valueLabel;
        }

        private static VisualElement CreateRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexWrap = Wrap.Wrap;
            return row;
        }

        private static Label CreateValueLabel(string text)
        {
            var label = new Label(text);
            label.style.color = TextPrimary;
            label.style.fontSize = 11;
            return label;
        }

        private static Button CreateStyledButton(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.marginRight = 4f;
            button.style.marginBottom = 4f;
            button.style.height = 26f;
            button.style.paddingLeft = 10f;
            button.style.paddingRight = 10f;
            button.style.backgroundColor = ButtonBg;
            button.style.color = TextPrimary;
            button.style.fontSize = 11;
            button.style.borderTopLeftRadius = 5f;
            button.style.borderTopRightRadius = 5f;
            button.style.borderBottomLeftRadius = 5f;
            button.style.borderBottomRightRadius = 5f;
            button.style.borderBottomColor = new Color(0.28f, 0.28f, 0.32f);
            button.style.borderTopColor = new Color(0.28f, 0.28f, 0.32f);
            button.style.borderLeftColor = new Color(0.28f, 0.28f, 0.32f);
            button.style.borderRightColor = new Color(0.28f, 0.28f, 0.32f);
            button.style.borderBottomWidth = 1f;
            button.style.borderTopWidth = 1f;
            button.style.borderLeftWidth = 1f;
            button.style.borderRightWidth = 1f;

            button.RegisterCallback<MouseEnterEvent>(_ => {
                if (button.style.backgroundColor != new StyleColor(AccentBlue))
                    button.style.backgroundColor = ButtonHover;
            });
            button.RegisterCallback<MouseLeaveEvent>(_ => {
                if (button.style.backgroundColor != new StyleColor(AccentBlue))
                    button.style.backgroundColor = ButtonBg;
            });
            button.RegisterCallback<MouseDownEvent>(_ => button.style.backgroundColor = ButtonActive);
            button.RegisterCallback<MouseUpEvent>(_ => button.style.backgroundColor = ButtonHover);

            return button;
        }

        // ─────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────

        private static Color GetGoalColor(string goal)
        {
            if (string.IsNullOrEmpty(goal) || goal == "-" || goal == "None")
                return TextSecondary;
            if (goal == "Eat") return HungerColor;
            if (goal == "Sleep") return EnergyColor;
            if (goal == "Sit") return ComfortColor;
            if (goal == "Dress") return ThermalColor;
            if (goal == "Idle") return TextMuted;
            return TextPrimary;
        }

        private static Color GetMovementColor(string status)
        {
            if (status == "Moving") return EnergyColor;
            if (status == "Rotating") return AccentBlue;
            if (status == "Arrived") return new Color(0.40f, 0.85f, 0.70f);
            if (status == "Blocked") return ThermalColor;
            return TextPrimary;
        }

        private static Color GetExecutionColor(string status)
        {
            if (status == "InProgress") return AccentBlue;
            if (status == "Completed") return EnergyColor;
            if (status == "Failed") return ThermalColor;
            if (status == "Starting") return HungerColor;
            return TextPrimary;
        }

        private static string FormatTile(Simulation.Common.TileCoord? tile)
        {
            return tile == null ? "-" : string.Format("({0}, {1})", tile.Value.Q, tile.Value.R);
        }

        private static string FormatPath(IReadOnlyList<Simulation.Common.JunctionId> path)
        {
            var parts = new string[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                parts[i] = path[i].Value.ToString();
            }
            return string.Join(" -> ", parts);
        }

        private static int CountReserved(WorldSnapshot snapshot)
        {
            var total = 0;
            foreach (var point in snapshot.Junctions)
            {
                if (point.Reserved) total++;
            }
            return total;
        }

        private static int CountOccupied(WorldSnapshot snapshot)
        {
            var total = 0;
            foreach (var point in snapshot.Junctions)
            {
                if (point.Occupied) total++;
            }
            return total;
        }
    }
}
