using System.Collections.Generic;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Always-visible time controls, centered at the top of the screen: pause /
    /// play and the 1× / 2× / 4× / 50× speed presets. Talks straight to the
    /// simulation runner. Resolution-scaled like the character bar.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SimSpeedBar : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;

        private static readonly float[] Speeds = { 1f, 2f, 4f, 50f };

        private UIDocument _document;
        private VisualElement _pauseButton;
        private VectorIcon _pauseIcon;
        private VectorIcon _playIcon;
        private readonly List<VisualElement> _speedButtons = new();

        // Weather widget (top-left): temperature, rain/clear, clock + phase.
        private VectorIcon _sunIcon;
        private VectorIcon _rainIcon;
        private Label _tempLabel;
        private Label _weatherLabel;
        private Label _clockLabel;
        private int _weatherTick = -1;
        private Language _weatherLanguage;

        // Spec 42: model degrees -> player-facing Celsius (display only).
        public const float DisplayCelsiusOffset = 10f;

        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.94f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Text = new(0.86f, 0.89f, 0.90f);
        private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);
        private static readonly Color Ink = new(0.06f, 0.086f, 0.102f);

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "SpeedBarPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 160;
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

            Refresh();
            RefreshWeather();
        }

        private void Build()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.style.justifyContent = Justify.FlexStart;
            root.style.alignItems = Align.Center;
            root.pickingMode = PickingMode.Ignore;

            var pill = new VisualElement();
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.marginTop = 14f;
            pill.style.backgroundColor = Panel;
            SetBorder(pill, Stroke);
            SetRadius(pill, 12f);
            pill.style.paddingLeft = 5f;
            pill.style.paddingRight = 5f;
            pill.style.paddingTop = 5f;
            pill.style.paddingBottom = 5f;
            root.Add(pill);

            _pauseButton = MakeButton();
            _pauseIcon = new VectorIcon(VectorIcon.Kind.Pause, Text);
            _playIcon = new VectorIcon(VectorIcon.Kind.Play, Ink);
            SizeIcon(_pauseIcon);
            SizeIcon(_playIcon);
            _playIcon.style.display = DisplayStyle.None;
            _pauseButton.Add(_pauseIcon);
            _pauseButton.Add(_playIcon);
            _pauseButton.RegisterCallback<MouseDownEvent>(_ =>
            {
                if (_runner != null) _runner.TogglePause();
            });
            pill.Add(_pauseButton);

            var sep = new VisualElement();
            sep.style.width = 1f;
            sep.style.height = 22f;
            sep.style.backgroundColor = Stroke;
            sep.style.marginLeft = 5f;
            sep.style.marginRight = 5f;
            pill.Add(sep);

            _speedButtons.Clear();
            foreach (var speed in Speeds)
            {
                var captured = speed;
                var button = MakeButton();
                button.style.minWidth = 42f;

                var label = new Label($"{speed:0}x");
                label.style.color = Text;
                label.style.fontSize = 14;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                button.Add(label);
                button.userData = label;

                button.RegisterCallback<MouseDownEvent>(_ => OnSpeed(captured));
                _speedButtons.Add(button);
                pill.Add(button);
            }

            BuildWeatherWidget(root);
        }

        // Top-left: 24°C · ☔/☀ Rain/Clear · 14:20 (day).
        private void BuildWeatherWidget(VisualElement root)
        {
            var box = new VisualElement();
            box.style.position = Position.Absolute;
            box.style.left = 14f;
            box.style.top = 14f;
            box.style.flexDirection = FlexDirection.Row;
            box.style.alignItems = Align.Center;
            box.style.backgroundColor = Panel;
            SetBorder(box, Stroke);
            SetRadius(box, 12f);
            box.style.paddingLeft = 12f;
            box.style.paddingRight = 14f;
            box.style.paddingTop = 7f;
            box.style.paddingBottom = 7f;
            root.Add(box);

            _tempLabel = new Label("—");
            _tempLabel.style.color = Text;
            _tempLabel.style.fontSize = 17;
            _tempLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _tempLabel.style.marginRight = 10f;
            box.Add(_tempLabel);

            _sunIcon = new VectorIcon(VectorIcon.Kind.Sun, Gold);
            _sunIcon.style.width = 16f;
            _sunIcon.style.height = 16f;
            _sunIcon.style.marginRight = 5f;
            box.Add(_sunIcon);

            _rainIcon = new VectorIcon(VectorIcon.Kind.Thirst, new Color(0.278f, 0.714f, 0.902f));
            _rainIcon.style.width = 16f;
            _rainIcon.style.height = 16f;
            _rainIcon.style.marginRight = 5f;
            _rainIcon.style.display = DisplayStyle.None;
            box.Add(_rainIcon);

            _weatherLabel = new Label("—");
            _weatherLabel.style.color = Text;
            _weatherLabel.style.fontSize = 13;
            _weatherLabel.style.marginRight = 10f;
            box.Add(_weatherLabel);

            _clockLabel = new Label("—");
            _clockLabel.style.color = new Color(0.604f, 0.651f, 0.678f);
            _clockLabel.style.fontSize = 13;
            box.Add(_clockLabel);
        }

        private void RefreshWeather()
        {
            if (_tempLabel == null || _runner == null || !_runner.IsReady)
            {
                return;
            }

            var snapshot = _runner.CreateSnapshot(); // cached per tick
            if (snapshot == null ||
                (snapshot.Tick == _weatherTick && Loc.Current == _weatherLanguage))
            {
                return;
            }

            _weatherTick = snapshot.Tick;
            _weatherLanguage = Loc.Current;

            // Spec 42: sim temperatures are RELATIVE units tuned for the
            // comfort math; the player reads familiar Celsius, so the HUD
            // shows model + 10 (model 23° -> "33°C", night 8 -> "18°C").
            // Display-only — every decision/soak stays on the model value.
            _tempLabel.text = $"{Mathf.RoundToInt(snapshot.Temperature + DisplayCelsiusOffset)}°C";
            _tempLabel.style.color = snapshot.Temperature < 12f
                ? new Color(0.278f, 0.714f, 0.902f)
                : snapshot.Temperature > 20f ? new Color(0.910f, 0.455f, 0.420f) : Text;

            var raining = snapshot.IsRaining;
            _sunIcon.style.display = raining ? DisplayStyle.None : DisplayStyle.Flex;
            _rainIcon.style.display = raining ? DisplayStyle.Flex : DisplayStyle.None;
            _weatherLabel.text = Loc.Get(raining ? "weather.rain" : "weather.clear");

            _clockLabel.text = $"{snapshot.Clock} · {Loc.Get("phase." + snapshot.DayPhase)}";
        }

        private void OnSpeed(float speed)
        {
            if (_runner == null)
            {
                return;
            }

            _runner.SetSpeed(speed);
            if (_runner.IsPaused)
            {
                _runner.TogglePause(); // clicking a speed also resumes
            }
        }

        private void Refresh()
        {
            if (_pauseIcon == null)
            {
                return;
            }

            var paused = _runner != null && _runner.IsPaused;
            _pauseIcon.style.display = paused ? DisplayStyle.None : DisplayStyle.Flex;
            _playIcon.style.display = paused ? DisplayStyle.Flex : DisplayStyle.None;
            _pauseButton.style.backgroundColor = paused ? Gold : Raised;

            var speed = _runner != null ? _runner.SpeedMultiplier : 1f;
            for (var i = 0; i < _speedButtons.Count; i++)
            {
                var active = !paused && Mathf.Approximately(speed, Speeds[i]);
                _speedButtons[i].style.backgroundColor = active ? Gold : Raised;
                if (_speedButtons[i].userData is Label label)
                {
                    label.style.color = active ? Ink : Text;
                }
            }
        }

        private static VisualElement MakeButton()
        {
            var b = new VisualElement();
            b.style.flexDirection = FlexDirection.Row;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            b.style.height = 30f;
            b.style.minWidth = 34f;
            b.style.paddingLeft = 8f;
            b.style.paddingRight = 8f;
            b.style.marginLeft = 2f;
            b.style.marginRight = 2f;
            b.style.backgroundColor = Raised;
            SetRadius(b, 8f);
            return b;
        }

        private static void SizeIcon(VectorIcon icon)
        {
            icon.style.width = 16f;
            icon.style.height = 16f;
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
