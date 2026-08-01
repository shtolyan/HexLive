#nullable enable
using System.Collections.Generic;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.History;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class GameHistoryPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour? _runner;

        private const int PageSize = GameHistoryLog.DefaultTailEntries;
        private const float RefreshSeconds = 1.2f;

        private static readonly Color Panel = new(0.075f, 0.087f, 0.102f, 0.96f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Text = new(0.88f, 0.90f, 0.92f);
        private static readonly Color Muted = new(0.56f, 0.61f, 0.66f);
        private static readonly Color Gold = new(0.90f, 0.68f, 0.36f);
        private static readonly Color Good = new(0.34f, 0.72f, 0.45f);
        private static readonly Color Bad = new(0.78f, 0.35f, 0.34f);
        private static readonly Color Social = new(0.62f, 0.48f, 0.88f);
        private static readonly Color BuildTone = new(0.40f, 0.66f, 0.92f);

        private UIDocument? _document;
        private VisualElement? _box;
        private VisualElement? _expandTab;
        private Label? _expandLabel;
        private Label? _title;
        private Label? _subtitle;
        private Label? _emptyLabel;
        private Button? _showMoreButton;
        private Button? _refreshButton;
        private VisualElement? _entries;
        private int _visibleCount = PageSize;
        private int _lastSeed = int.MinValue;
        private float _nextRefreshTime;
        private bool _collapsed = true;
        private readonly List<GameHistoryRecord> _scratch = new();

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "GameHistoryPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 171;
                _document.panelSettings = settings;
            }

            Loc.LanguageChanged += OnLanguageChanged;
            Build();
        }

        private void OnDestroy()
        {
            Loc.LanguageChanged -= OnLanguageChanged;
        }

        private void Update()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (_collapsed || Time.unscaledTime < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.unscaledTime + RefreshSeconds;
            Refresh(false);
        }

        private void Build()
        {
            if (_document == null)
            {
                return;
            }

            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.pickingMode = PickingMode.Ignore;

            _box = new VisualElement();
            _box.style.position = Position.Absolute;
            _box.style.left = 200f;
            _box.style.top = 150f;
            _box.style.width = 420f;
            _box.style.maxHeight = 620f;
            _box.style.flexDirection = FlexDirection.Column;
            _box.style.backgroundColor = Panel;
            _box.style.paddingLeft = 10f;
            _box.style.paddingRight = 10f;
            _box.style.paddingTop = 10f;
            _box.style.paddingBottom = 10f;
            SetBorder(_box, Stroke);
            SetRadius(_box, 12f);
            RegisterUiBlock(_box);
            root.Add(_box);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 8f;
            _box.Add(header);

            var titleGroup = new VisualElement();
            titleGroup.style.flexDirection = FlexDirection.Column;
            header.Add(titleGroup);

            _title = new Label();
            _title.style.color = Text;
            _title.style.fontSize = 15;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleGroup.Add(_title);

            _subtitle = new Label();
            _subtitle.style.color = Muted;
            _subtitle.style.fontSize = 10;
            titleGroup.Add(_subtitle);

            var close = MakeHeaderButton("‹", () => SetCollapsed(true));
            header.Add(close);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.maxHeight = 500f;
            scroll.style.flexGrow = 1f;
            _entries = new VisualElement();
            _entries.style.flexDirection = FlexDirection.Column;
            scroll.Add(_entries);
            _box.Add(scroll);

            _emptyLabel = new Label();
            _emptyLabel.style.color = Muted;
            _emptyLabel.style.fontSize = 12;
            _emptyLabel.style.paddingTop = 16f;
            _emptyLabel.style.paddingBottom = 16f;
            _emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _box.Add(_emptyLabel);

            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            footer.style.justifyContent = Justify.SpaceBetween;
            footer.style.marginTop = 8f;
            _box.Add(footer);

            var more = MakeFooterButton(() =>
            {
                _visibleCount += PageSize;
                Refresh(true);
            });
            _showMoreButton = more;
            footer.Add(more);

            var refresh = MakeFooterButton(() => Refresh(true));
            _refreshButton = refresh;
            footer.Add(refresh);

            BuildExpandTab(root);
            LocalizeStatic();
            ApplyCollapsed();
        }

        private void BuildExpandTab(VisualElement root)
        {
            _expandTab = new VisualElement();
            _expandTab.style.position = Position.Absolute;
            _expandTab.style.left = 52f;
            _expandTab.style.top = 150f;
            _expandTab.style.height = 30f;
            _expandTab.style.minWidth = 92f;
            _expandTab.style.flexDirection = FlexDirection.Row;
            _expandTab.style.alignItems = Align.Center;
            _expandTab.style.justifyContent = Justify.Center;
            _expandTab.style.backgroundColor = Panel;
            _expandTab.style.paddingLeft = 10f;
            _expandTab.style.paddingRight = 10f;
            SetBorder(_expandTab, Stroke);
            SetRadius(_expandTab, 8f);
            RegisterUiBlock(_expandTab);
            _expandTab.RegisterCallback<MouseDownEvent>(evt =>
            {
                SetCollapsed(false);
                evt.StopPropagation();
            });

            _expandLabel = new Label();
            _expandLabel.style.color = Gold;
            _expandLabel.style.fontSize = 12;
            _expandLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _expandTab.Add(_expandLabel);
            root.Add(_expandTab);
        }

        private void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
            ApplyCollapsed();
            if (!_collapsed)
            {
                Refresh(true);
            }
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

        private void Refresh(bool force)
        {
            if (_runner == null || !_runner.IsReady || _entries == null || _emptyLabel == null)
            {
                return;
            }

            _runner.GameHistory.Flush();
            // The history file is keyed by seed; a client learns it from the
            // handshake, so read it off the source, not off WorldState.
            var seed = _runner.Seed;
            if (seed != _lastSeed)
            {
                _lastSeed = seed;
                _visibleCount = PageSize;
                force = true;
            }

            if (!force && _scratch.Count > 0)
            {
                var latest = GameHistoryLog.ReadTail(seed, 1);
                if (latest.Count > 0 && latest[latest.Count - 1].Tick == _scratch[_scratch.Count - 1].Tick)
                {
                    return;
                }
            }

            _scratch.Clear();
            _scratch.AddRange(GameHistoryLog.ReadTail(seed, _visibleCount));
            _entries.Clear();
            _emptyLabel.style.display = _scratch.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var record in _scratch)
            {
                _entries.Add(MakeEntry(GameHistoryFormatter.Format(record)));
            }

            LocalizeStatic();
        }

        private VisualElement MakeEntry(GameHistoryText text)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.marginBottom = 7f;
            row.style.paddingLeft = 9f;
            row.style.paddingRight = 9f;
            row.style.paddingTop = 7f;
            row.style.paddingBottom = 7f;
            row.style.backgroundColor = new Color(1f, 1f, 1f, 0.045f);
            SetRadius(row, 8f);
            SetBorder(row, new Color(1f, 1f, 1f, 0.08f));

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 3f;
            row.Add(top);

            var dot = new VisualElement();
            dot.style.width = 7f;
            dot.style.height = 7f;
            dot.style.marginRight = 7f;
            dot.style.backgroundColor = ColorFor(text.Tone);
            SetRadius(dot, 4f);
            top.Add(dot);

            var time = new Label(text.Time);
            time.style.color = Muted;
            time.style.fontSize = 10;
            top.Add(time);

            var title = new Label(text.Title);
            title.style.color = Text;
            title.style.fontSize = 12;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(title);

            if (!string.IsNullOrEmpty(text.Detail))
            {
                var detail = new Label(text.Detail);
                detail.style.color = Muted;
                detail.style.fontSize = 10;
                detail.style.whiteSpace = WhiteSpace.Normal;
                detail.style.marginTop = 2f;
                row.Add(detail);
            }

            return row;
        }

        private void OnLanguageChanged()
        {
            LocalizeStatic();
            if (!_collapsed)
            {
                Refresh(true);
            }
        }

        private void LocalizeStatic()
        {
            if (_title != null) _title.text = Loc.Get("history.title");
            if (_subtitle != null) _subtitle.text = Loc.Get("history.subtitle");
            if (_emptyLabel != null) _emptyLabel.text = Loc.Get("history.empty");
            if (_showMoreButton != null) _showMoreButton.text = Loc.Get("history.show_older");
            if (_refreshButton != null) _refreshButton.text = Loc.Get("history.refresh");
            if (_expandLabel != null) _expandLabel.text = Loc.Get("history.title");
        }

        private static Button MakeHeaderButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.width = 30f;
            button.style.height = 28f;
            button.style.backgroundColor = Raised;
            button.style.color = Text;
            button.style.fontSize = 16;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            SetRadius(button, 7f);
            SetBorder(button, Stroke);
            return button;
        }

        private static Button MakeFooterButton(System.Action onClick)
        {
            var button = new Button(onClick);
            button.style.height = 28f;
            button.style.backgroundColor = Raised;
            button.style.color = Text;
            button.style.fontSize = 11;
            button.style.paddingLeft = 10f;
            button.style.paddingRight = 10f;
            SetRadius(button, 7f);
            SetBorder(button, Stroke);
            return button;
        }

        private static void RegisterUiBlock(VisualElement element)
        {
            element.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
            element.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
        }

        private static Color ColorFor(GameHistoryTone tone) => tone switch
        {
            GameHistoryTone.Good => Good,
            GameHistoryTone.Bad => Bad,
            GameHistoryTone.Danger => new Color(0.92f, 0.32f, 0.28f),
            GameHistoryTone.Social => Social,
            GameHistoryTone.Build => BuildTone,
            _ => Gold
        };

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
