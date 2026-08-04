using System;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// The in-game micro bug tracker (opened from the debug panel): type a bug,
    /// press send, see every filed bug with its status. Statuses travel through
    /// BUGS.json — «создан» is set here, «исправлен» is set by the agent from a
    /// session, and a fixed bug can be bounced back to «доработка» with a
    /// comment. Each submit auto-attaches seed/tick/selected NPC so the report
    /// is reproducible without asking.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class BugReportPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;

        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.97f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Text = new(0.86f, 0.89f, 0.90f);
        private static readonly Color Dim = new(0.55f, 0.60f, 0.63f);
        private static readonly Color Accent = new(0.30f, 0.55f, 0.38f);
        private static readonly Color ChipCreated = new(0.72f, 0.55f, 0.20f);
        private static readonly Color ChipFixed = new(0.30f, 0.55f, 0.38f);
        private static readonly Color ChipRework = new(0.72f, 0.34f, 0.28f);
        private static readonly Color Danger = new(0.72f, 0.28f, 0.30f);

        private UIDocument _document;
        private VisualElement _window;
        private TextField _input;
        private ScrollView _list;
        private bool _visible;
        private float _nextExternalCheck;

        // Two-click delete: first click arms this id, second click deletes.
        private int _armedDeleteId = -1;
        // The report whose inline rework-comment editor is open, -1 = none.
        private int _reworkEditorId = -1;

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        public bool Visible => _visible;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "BugReportPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 180; // above the debug buttons
                _document.panelSettings = settings;
            }

            Build();
            ApplyVisible();
        }

        private void Update()
        {
            if (!_visible)
            {
                return;
            }

            // The agent edits BUGS.json from outside Play mode — pick that up
            // live, but not every frame (mtime stat is a filesystem hit).
            if (Time.unscaledTime >= _nextExternalCheck)
            {
                _nextExternalCheck = Time.unscaledTime + 2f;
                if (BugReportStore.CheckExternalChange())
                {
                    RebuildList();
                }
            }
        }

        public void Toggle() => SetVisible(!_visible);

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible)
            {
                _armedDeleteId = -1;
                _reworkEditorId = -1;
                BugReportStore.CheckExternalChange();
                RebuildList();
            }
            else
            {
                NpcSelection.PointerOverUi = false;
            }

            ApplyVisible();
        }

        private void ApplyVisible()
        {
            if (_window != null)
            {
                _window.style.display = _visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void Build()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;
            root.pickingMode = PickingMode.Ignore;

            _window = new VisualElement();
            _window.style.width = 560f;
            _window.style.maxHeight = Length.Percent(78f);
            _window.style.flexDirection = FlexDirection.Column;
            _window.style.backgroundColor = Panel;
            SetBorder(_window, Stroke);
            SetRadius(_window, 12f);
            _window.style.paddingLeft = 14f;
            _window.style.paddingRight = 14f;
            _window.style.paddingTop = 12f;
            _window.style.paddingBottom = 12f;
            _window.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
            root.Add(_window);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 8f;
            _window.Add(header);

            var title = new Label("БАГ-ТРЕКЕР");
            title.style.color = new Color(0.604f, 0.651f, 0.678f);
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);

            var close = new Label("✕");
            close.style.color = Text;
            close.style.fontSize = 14;
            close.style.unityFontStyleAndWeight = FontStyle.Bold;
            close.style.paddingLeft = 8f;
            close.style.paddingRight = 4f;
            close.RegisterCallback<MouseDownEvent>(evt => { SetVisible(false); evt.StopPropagation(); });
            header.Add(close);

            _input = new TextField { multiline = true };
            _input.style.fontSize = 13;
            _input.style.minHeight = 64f;
            _input.style.marginBottom = 6f;
            _input.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_input);

            var sendRow = new VisualElement();
            sendRow.style.flexDirection = FlexDirection.Row;
            sendRow.style.justifyContent = Justify.FlexEnd;
            sendRow.style.marginBottom = 10f;
            _window.Add(sendRow);

            sendRow.Add(MakeButton("Отправить", Accent, Submit));

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1f;
            _window.Add(_list);
        }

        private void Submit()
        {
            var text = _input.value?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            BugReportStore.Add(text, BuildContext());
            _input.value = string.Empty;
            RebuildList();
        }

        // Seed/tick/selected NPC at the moment of filing — the repro line the
        // reader would otherwise have to ask for.
        private string BuildContext()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            var context = string.Empty;
            if (_runner != null)
            {
                context = $"seed={_runner.Seed} tick={_runner.CurrentTick}";
            }

            if (NpcSelection.HasSelection)
            {
                context += $" npc={NpcSelection.SelectedId}";
            }

            return context.Trim();
        }

        private void RebuildList()
        {
            if (_list == null)
            {
                return;
            }

            _list.Clear();

            var reports = BugReportStore.Reports;
            if (reports.Count == 0)
            {
                var empty = new Label("Пока пусто. Опиши баг и нажми «Отправить».");
                empty.style.color = Dim;
                empty.style.fontSize = 12;
                _list.Add(empty);
                return;
            }

            // Newest on top — the just-filed bug should be the first thing seen.
            for (var i = reports.Count - 1; i >= 0; i--)
            {
                _list.Add(BuildRow(reports[i]));
            }
        }

        private VisualElement BuildRow(BugReportStore.Report report)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.backgroundColor = Raised;
            SetRadius(row, 8f);
            row.style.paddingLeft = 10f;
            row.style.paddingRight = 10f;
            row.style.paddingTop = 8f;
            row.style.paddingBottom = 8f;
            row.style.marginBottom = 6f;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 4f;
            row.Add(top);

            var chip = new Label(StatusLabel(report.status));
            chip.style.color = Color.white;
            chip.style.backgroundColor = StatusColor(report.status);
            chip.style.fontSize = 10;
            chip.style.unityFontStyleAndWeight = FontStyle.Bold;
            chip.style.paddingLeft = 6f;
            chip.style.paddingRight = 6f;
            chip.style.paddingTop = 1f;
            chip.style.paddingBottom = 1f;
            SetRadius(chip, 6f);
            top.Add(chip);

            var meta = new Label($"  #{report.id} · {report.createdUtc}");
            meta.style.color = Dim;
            meta.style.fontSize = 10;
            top.Add(meta);

            var body = new Label(report.text);
            body.style.color = Text;
            body.style.fontSize = 12;
            body.style.whiteSpace = WhiteSpace.Normal;
            row.Add(body);

            if (!string.IsNullOrEmpty(report.context))
            {
                var context = new Label(report.context);
                context.style.color = Dim;
                context.style.fontSize = 10;
                context.style.marginTop = 2f;
                row.Add(context);
            }

            foreach (var comment in report.comments)
            {
                var author = comment.author == "claude" ? "Claude" : "Я";
                var line = new Label($"{author}: {comment.text}");
                line.style.color = comment.author == "claude"
                    ? new Color(0.62f, 0.74f, 0.86f)
                    : new Color(0.80f, 0.74f, 0.58f);
                line.style.fontSize = 11;
                line.style.whiteSpace = WhiteSpace.Normal;
                line.style.marginTop = 3f;
                line.style.marginLeft = 8f;
                row.Add(line);
            }

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.FlexEnd;
            actions.style.marginTop = 6f;
            row.Add(actions);

            if (report.status == BugReportStore.StatusFixed)
            {
                actions.Add(MakeSmallButton("В доработку", Raised, () =>
                {
                    _reworkEditorId = _reworkEditorId == report.id ? -1 : report.id;
                    RebuildList();
                }));
            }

            var deleteLabel = _armedDeleteId == report.id ? "Точно удалить?" : "Удалить";
            actions.Add(MakeSmallButton(deleteLabel, _armedDeleteId == report.id ? Danger : Raised, () =>
            {
                if (_armedDeleteId == report.id)
                {
                    _armedDeleteId = -1;
                    BugReportStore.Remove(report.id);
                }
                else
                {
                    _armedDeleteId = report.id;
                }

                RebuildList();
            }));

            if (_reworkEditorId == report.id)
            {
                var reworkInput = new TextField { multiline = true };
                reworkInput.style.fontSize = 12;
                reworkInput.style.minHeight = 44f;
                reworkInput.style.marginTop = 6f;
                reworkInput.style.whiteSpace = WhiteSpace.Normal;
                row.Add(reworkInput);

                var reworkRow = new VisualElement();
                reworkRow.style.flexDirection = FlexDirection.Row;
                reworkRow.style.justifyContent = Justify.FlexEnd;
                reworkRow.style.marginTop = 4f;
                row.Add(reworkRow);

                reworkRow.Add(MakeSmallButton("Вернуть с комментарием", ChipRework, () =>
                {
                    BugReportStore.SendToRework(report.id, reworkInput.value);
                    _reworkEditorId = -1;
                    RebuildList();
                }));
            }

            return row;
        }

        private static string StatusLabel(string status) => status switch
        {
            BugReportStore.StatusCreated => "СОЗДАН",
            BugReportStore.StatusFixed => "ИСПРАВЛЕН",
            BugReportStore.StatusRework => "ДОРАБОТКА",
            _ => status?.ToUpperInvariant() ?? "?"
        };

        private static Color StatusColor(string status) => status switch
        {
            BugReportStore.StatusCreated => ChipCreated,
            BugReportStore.StatusFixed => ChipFixed,
            BugReportStore.StatusRework => ChipRework,
            _ => new Color(0.4f, 0.44f, 0.48f)
        };

        // ---- ui helpers (same look as DebugControlsPanel) ----

        private static VisualElement MakeButton(string text, Color accent, Action onClick)
        {
            var b = new VisualElement();
            b.style.flexDirection = FlexDirection.Row;
            b.style.alignItems = Align.Center;
            b.style.height = 30f;
            b.style.paddingLeft = 14f;
            b.style.paddingRight = 14f;
            b.style.backgroundColor = accent;
            SetRadius(b, 8f);

            var label = new Label(text);
            label.style.color = Text;
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.Add(label);

            b.RegisterCallback<MouseDownEvent>(evt => { onClick(); evt.StopPropagation(); });
            return b;
        }

        private static VisualElement MakeSmallButton(string text, Color accent, Action onClick)
        {
            var b = MakeButton(text, accent, onClick);
            b.style.height = 24f;
            b.style.paddingLeft = 10f;
            b.style.paddingRight = 10f;
            b.style.marginLeft = 6f;
            ((Label)b[0]).style.fontSize = 11;
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
