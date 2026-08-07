using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Two deliberately separate surfaces over BUGS.json: a small quick-report
    /// dialog and a large tabbed manager. The store remains the source of truth.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class BugReportPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;

        private enum ManagerTab { Open, Fixed, Archive }

        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.97f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Text = new(0.86f, 0.89f, 0.90f);
        private static readonly Color Dim = new(0.55f, 0.60f, 0.63f);
        private static readonly Color Accent = new(0.30f, 0.55f, 0.38f);
        private static readonly Color ChipCreated = new(0.72f, 0.55f, 0.20f);
        private static readonly Color ChipInProgress = new(0.78f, 0.43f, 0.16f);
        private static readonly Color ChipReadyForTest = new(0.34f, 0.46f, 0.78f);
        private static readonly Color ChipFixed = new(0.30f, 0.55f, 0.38f);
        private static readonly Color ChipRework = new(0.72f, 0.34f, 0.28f);

        private VisualElement _quickWindow;
        private VisualElement _managerWindow;
        private VisualElement _root;
        private TextField _quickInput;
        private ScrollView _managerList;
        private bool _quickVisible;
        private bool _managerVisible;
        private float _nextExternalCheck;
        private ManagerTab _tab;
        private string _statusFilter;
        private int _reportEditorId = -1;
        private int _reworkEditorId = -1;
        private int _commentEditorId = -1;
        private int _editingCommentIndex = -1;

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;
        public bool Visible => _quickVisible || _managerVisible;

        private void Awake()
        {
            var document = GetComponent<UIDocument>();
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "BugReportPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 180;
                document.panelSettings = settings;
            }

            Build(document.rootVisualElement);
            ApplyVisible();
        }

        private void OnEnable() => Loc.LanguageChanged += Relocalize;

        private void OnDisable() => Loc.LanguageChanged -= Relocalize;

        private void Relocalize()
        {
            if (_root == null)
            {
                return;
            }

            var draft = _quickInput?.value ?? string.Empty;
            Build(_root);
            _quickInput.value = draft;
            ApplyVisible();
            if (_managerVisible)
            {
                RebuildManager();
            }
        }

        private void Update()
        {
            if (!Visible || Time.unscaledTime < _nextExternalCheck)
            {
                return;
            }

            _nextExternalCheck = Time.unscaledTime + 2f;
            if (BugReportStore.CheckExternalChange() && _managerVisible)
            {
                RebuildManager();
            }
        }

        // Compatibility: the original debug button now opens the quick form.
        public void Toggle() => ToggleQuick();
        public void ToggleQuick() => SetQuickVisible(!_quickVisible);
        public void ToggleManager() => SetManagerVisible(!_managerVisible);

        public void SetVisible(bool visible) => SetQuickVisible(visible);

        private void SetQuickVisible(bool visible)
        {
            _quickVisible = visible;
            if (visible)
            {
                _managerVisible = false;
                BugReportStore.CheckExternalChange();
                _quickInput?.Focus();
            }

            ApplyVisible();
        }

        private void SetManagerVisible(bool visible)
        {
            _managerVisible = visible;
            if (visible)
            {
                _quickVisible = false;
                _reportEditorId = -1;
                _reworkEditorId = -1;
                _commentEditorId = -1;
                _editingCommentIndex = -1;
                BugReportStore.CheckExternalChange();
                RebuildManager();
            }

            ApplyVisible();
        }

        private void ApplyVisible()
        {
            if (_quickWindow != null)
            {
                _quickWindow.style.display = _quickVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_managerWindow != null)
            {
                _managerWindow.style.display = _managerVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (!Visible)
            {
                NpcSelection.PointerOverUi = false;
            }
        }

        private void Build(VisualElement root)
        {
            _root = root;
            root.Clear();
            root.style.flexGrow = 1f;
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;
            root.pickingMode = PickingMode.Ignore;

            _quickWindow = MakeWindow(480f, 290f);
            root.Add(_quickWindow);
            BuildQuickWindow();

            _managerWindow = MakeWindow(1040f, Length.Percent(88f));
            root.Add(_managerWindow);
            BuildManagerWindow();
        }

        private VisualElement MakeWindow(Length width, Length maxHeight)
        {
            var window = new VisualElement();
            window.style.width = width;
            window.style.maxHeight = maxHeight;
            window.style.flexDirection = FlexDirection.Column;
            window.style.backgroundColor = Panel;
            window.style.paddingLeft = 16f;
            window.style.paddingRight = 16f;
            window.style.paddingTop = 14f;
            window.style.paddingBottom = 14f;
            SetBorder(window, Stroke);
            SetRadius(window, 12f);
            window.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
            window.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
            return window;
        }

        private void BuildQuickWindow()
        {
            _quickWindow.Add(BuildHeader(Loc.Get("bugs.quick.title"), () => SetQuickVisible(false)));
            var hint = new Label(Loc.Get("bugs.quick.hint"));
            hint.style.color = Dim;
            hint.style.fontSize = 11;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 8f;
            _quickWindow.Add(hint);

            _quickInput = new TextField { multiline = true };
            _quickInput.style.fontSize = 13;
            _quickInput.style.minHeight = 110f;
            _quickInput.style.whiteSpace = WhiteSpace.Normal;
            _quickWindow.Add(_quickInput);

            var actions = MakeActionRow();
            actions.style.marginTop = 10f;
            actions.Add(MakeButton(Loc.Get("bugs.open_manager"), Raised, () => SetManagerVisible(true)));
            actions.Add(MakeButton(Loc.Get("bugs.submit"), Accent, Submit));
            _quickWindow.Add(actions);
        }

        private void BuildManagerWindow()
        {
            _managerWindow.Add(BuildHeader(Loc.Get("bugs.manager.title"), () => SetManagerVisible(false)));

            var tabs = new VisualElement();
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.marginBottom = 10f;
            tabs.Add(MakeTab(Loc.Get("bugs.tab.open"), ManagerTab.Open));
            tabs.Add(MakeTab(Loc.Get("bugs.tab.fixed"), ManagerTab.Fixed));
            tabs.Add(MakeTab(Loc.Get("bugs.tab.archive"), ManagerTab.Archive));
            tabs.Add(MakeButton(Loc.Get("bugs.new"), Accent, () => SetQuickVisible(true)));
            _managerWindow.Add(tabs);

            _managerWindow.Add(BuildStatusFilters());

            _managerList = new ScrollView(ScrollViewMode.Vertical);
            _managerList.style.flexGrow = 1f;
            _managerWindow.Add(_managerList);
        }

        private VisualElement BuildHeader(string titleText, Action closeAction)
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 8f;

            var title = new Label(titleText);
            title.style.color = new Color(0.604f, 0.651f, 0.678f);
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);

            var close = new Label("✕");
            close.style.color = Text;
            close.style.fontSize = 14;
            close.style.unityFontStyleAndWeight = FontStyle.Bold;
            close.style.paddingLeft = 10f;
            close.RegisterCallback<MouseDownEvent>(evt => { closeAction(); evt.StopPropagation(); });
            header.Add(close);
            return header;
        }

        private VisualElement MakeTab(string text, ManagerTab tab)
        {
            var button = MakeButton(text, _tab == tab ? Accent : Raised, () =>
            {
                _tab = tab;
                BuildManagerTabsAgain();
                RebuildManager();
            });
            button.style.marginRight = 6f;
            return button;
        }

        private void BuildManagerTabsAgain()
        {
            var oldTabs = _managerWindow.ElementAt(1);
            oldTabs.Clear();
            oldTabs.Add(MakeTab(Loc.Get("bugs.tab.open"), ManagerTab.Open));
            oldTabs.Add(MakeTab(Loc.Get("bugs.tab.fixed"), ManagerTab.Fixed));
            oldTabs.Add(MakeTab(Loc.Get("bugs.tab.archive"), ManagerTab.Archive));
            oldTabs.Add(MakeButton(Loc.Get("bugs.new"), Accent, () => SetQuickVisible(true)));
        }

        private VisualElement BuildStatusFilters()
        {
            var filters = new VisualElement();
            filters.style.flexDirection = FlexDirection.Row;
            filters.style.flexWrap = Wrap.Wrap;
            filters.style.marginBottom = 8f;
            filters.Add(MakeStatusFilter(Loc.Get("bugs.filter.all"), null));
            filters.Add(MakeStatusFilter(Loc.Get("bugs.status.created"), BugReportStore.StatusCreated));
            filters.Add(MakeStatusFilter(Loc.Get("bugs.status.in_progress"), BugReportStore.StatusInProgress));
            filters.Add(MakeStatusFilter(Loc.Get("bugs.status.ready_for_test"), BugReportStore.StatusReadyForTest));
            filters.Add(MakeStatusFilter(Loc.Get("bugs.status.rework"), BugReportStore.StatusRework));
            filters.Add(MakeStatusFilter(Loc.Get("bugs.status.fixed"), BugReportStore.StatusFixed));
            return filters;
        }

        private VisualElement MakeStatusFilter(string text, string status)
        {
            var active = _statusFilter == status;
            var button = MakeSmallButton(text, active ? Accent : Raised, () =>
            {
                _statusFilter = status;
                if (_tab != ManagerTab.Archive)
                {
                    _tab = status == BugReportStore.StatusFixed ? ManagerTab.Fixed : ManagerTab.Open;
                }

                BuildManagerTabsAgain();
                var oldFilters = _managerWindow.ElementAt(2);
                _managerWindow.Insert(2, BuildStatusFilters());
                oldFilters.RemoveFromHierarchy();
                RebuildManager();
            });
            button.style.marginRight = 4f;
            button.style.marginBottom = 4f;
            return button;
        }

        private void Submit()
        {
            var text = _quickInput.value?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            BugReportStore.Add(text, BuildContext());
            _quickInput.value = string.Empty;
            SetQuickVisible(false);
            _tab = ManagerTab.Open;
            SetManagerVisible(true);
        }

        private string BuildContext()
        {
            _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
            var context = _runner != null ? $"seed={_runner.Seed} tick={_runner.CurrentTick}" : string.Empty;
            if (NpcSelection.HasSelection)
            {
                context += $" npc={NpcSelection.SelectedId}";
            }

            return context.Trim();
        }

        private void RebuildManager()
        {
            if (_managerList == null)
            {
                return;
            }

            _managerList.Clear();
            var reports = BugReportStore.Reports;
            var count = 0;
            for (var i = reports.Count - 1; i >= 0; i--)
            {
                var report = reports[i];
                if (!BelongsToCurrentTab(report))
                {
                    continue;
                }

                _managerList.Add(BuildRow(report));
                count++;
            }

            if (count == 0)
            {
                var empty = new Label(Loc.Get("bugs.empty"));
                empty.style.color = Dim;
                empty.style.fontSize = 12;
                _managerList.Add(empty);
            }
        }

        private bool BelongsToCurrentTab(BugReportStore.Report report)
        {
            if (_statusFilter != null && report.status != _statusFilter)
            {
                return false;
            }

            return _tab switch
            {
                ManagerTab.Archive => report.archived,
                ManagerTab.Fixed => !report.archived && report.status == BugReportStore.StatusFixed,
                _ => !report.archived && report.status != BugReportStore.StatusFixed
            };
        }

        private VisualElement BuildRow(BugReportStore.Report report)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.backgroundColor = Raised;
            row.style.paddingLeft = 12f;
            row.style.paddingRight = 12f;
            row.style.paddingTop = 9f;
            row.style.paddingBottom = 9f;
            row.style.marginBottom = 7f;
            SetRadius(row, 8f);

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 4f;
            row.Add(top);

            var chip = new Label(StatusLabel(report));
            chip.style.color = Color.white;
            chip.style.backgroundColor = StatusColor(report);
            chip.style.fontSize = 10;
            chip.style.unityFontStyleAndWeight = FontStyle.Bold;
            chip.style.paddingLeft = 6f;
            chip.style.paddingRight = 6f;
            chip.style.paddingTop = 1f;
            chip.style.paddingBottom = 1f;
            SetRadius(chip, 6f);
            top.Add(chip);

            var versions = VersionMeta(report);
            var meta = new Label($"  #{report.id} · {report.createdUtc}{versions}");
            meta.style.color = Dim;
            meta.style.fontSize = 10;
            top.Add(meta);

            var buildState = BuildStateLabel(report);
            if (buildState.HasValue)
            {
                var state = buildState.Value;
                var buildChip = new Label(state.text);
                buildChip.style.color = Color.white;
                buildChip.style.backgroundColor = state.color;
                buildChip.style.fontSize = 10;
                buildChip.style.paddingLeft = 6f;
                buildChip.style.paddingRight = 6f;
                buildChip.style.paddingTop = 1f;
                buildChip.style.paddingBottom = 1f;
                buildChip.style.marginLeft = 6f;
                SetRadius(buildChip, 6f);
                top.Add(buildChip);
            }

            if (_reportEditorId == report.id)
            {
                BuildReportTextEditor(row, report);
            }
            else
            {
                var body = new Label(report.text);
                body.style.color = Text;
                body.style.fontSize = 12;
                body.style.whiteSpace = WhiteSpace.Normal;
                row.Add(body);
            }

            if (!string.IsNullOrEmpty(report.context))
            {
                var context = new Label(report.context);
                context.style.color = Dim;
                context.style.fontSize = 10;
                context.style.marginTop = 2f;
                row.Add(context);
            }

            var commits = CommitsMeta(report);
            if (!string.IsNullOrEmpty(commits))
            {
                var commitLine = new Label(commits);
                commitLine.style.color = Dim;
                commitLine.style.fontSize = 10;
                commitLine.style.marginTop = 2f;
                commitLine.style.whiteSpace = WhiteSpace.Normal;
                row.Add(commitLine);
            }

            for (var i = 0; i < report.comments.Count; i++)
            {
                BuildComment(row, report, i);
            }

            var actions = MakeActionRow();
            actions.style.marginTop = 7f;
            row.Add(actions);

            actions.Add(MakeSmallButton(Loc.Get("bugs.edit_report"), Raised, () =>
            {
                _reportEditorId = _reportEditorId == report.id ? -1 : report.id;
                RebuildManager();
            }));

            actions.Add(MakeSmallButton(Loc.Get("bugs.comment"), Raised, () =>
            {
                _commentEditorId = _commentEditorId == report.id ? -1 : report.id;
                _editingCommentIndex = -1;
                RebuildManager();
            }));

            if (report.status == BugReportStore.StatusReadyForTest && !report.archived)
            {
                actions.Add(MakeSmallButton(Loc.Get("bugs.mark_fixed"), ChipFixed, () =>
                {
                    BugReportStore.MarkFixed(report.id);
                    _tab = ManagerTab.Fixed;
                    BuildManagerTabsAgain();
                    RebuildManager();
                }));
                actions.Add(MakeSmallButton(Loc.Get("bugs.rework"), Raised, () =>
                {
                    _reworkEditorId = _reworkEditorId == report.id ? -1 : report.id;
                    RebuildManager();
                }));
            }

            if (report.archived || report.status == BugReportStore.StatusFixed)
            {
                actions.Add(MakeSmallButton(report.archived ? Loc.Get("bugs.restore") : Loc.Get("bugs.archive"), Raised, () =>
                {
                    BugReportStore.SetArchived(report.id, !report.archived);
                    RebuildManager();
                }));
            }

            if (_commentEditorId == report.id)
            {
                BuildCommentEditor(row, report);
            }

            if (_reworkEditorId == report.id)
            {
                BuildReworkEditor(row, report);
            }

            return row;
        }

        private void BuildReportTextEditor(VisualElement row, BugReportStore.Report report)
        {
            var editor = new TextField { multiline = true, value = report.text };
            editor.style.fontSize = 12;
            editor.style.minHeight = 70f;
            editor.style.whiteSpace = WhiteSpace.Normal;
            row.Add(editor);

            var buttons = MakeActionRow();
            buttons.style.marginTop = 4f;
            buttons.Add(MakeSmallButton(Loc.Get("bugs.cancel"), Raised, () =>
            {
                _reportEditorId = -1;
                RebuildManager();
            }));
            buttons.Add(MakeSmallButton(Loc.Get("bugs.save"), Accent, () =>
            {
                BugReportStore.EditReportText(report.id, editor.value);
                _reportEditorId = -1;
                RebuildManager();
            }));
            row.Add(buttons);
        }

        private void BuildComment(VisualElement row, BugReportStore.Report report, int index)
        {
            var comment = report.comments[index];
            var isUser = string.Equals(comment.author, "user", StringComparison.OrdinalIgnoreCase);
            var author = isUser ? Loc.Get("bugs.author.user") : string.Equals(comment.author, "codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : "Claude";
            var lineRow = new VisualElement();
            lineRow.style.flexDirection = FlexDirection.Row;
            lineRow.style.alignItems = Align.FlexStart;
            lineRow.style.marginTop = 3f;
            lineRow.style.marginLeft = 8f;
            row.Add(lineRow);

            var line = new Label($"{author}: {comment.text}");
            line.style.color = isUser ? new Color(0.80f, 0.74f, 0.58f) : new Color(0.62f, 0.74f, 0.86f);
            line.style.fontSize = 11;
            line.style.whiteSpace = WhiteSpace.Normal;
            line.style.flexGrow = 1f;
            lineRow.Add(line);

            if (isUser)
            {
                var capturedIndex = index;
                lineRow.Add(MakeSmallButton(Loc.Get("bugs.edit"), Raised, () =>
                {
                    _commentEditorId = report.id;
                    _editingCommentIndex = capturedIndex;
                    RebuildManager();
                }));
            }
        }

        private void BuildCommentEditor(VisualElement row, BugReportStore.Report report)
        {
            var editor = new TextField { multiline = true };
            editor.style.fontSize = 12;
            editor.style.minHeight = 44f;
            editor.style.marginTop = 6f;
            editor.style.whiteSpace = WhiteSpace.Normal;
            if (_editingCommentIndex >= 0 && _editingCommentIndex < report.comments.Count)
            {
                editor.value = report.comments[_editingCommentIndex].text;
            }
            row.Add(editor);

            var buttons = MakeActionRow();
            buttons.style.marginTop = 4f;
            buttons.Add(MakeSmallButton(_editingCommentIndex >= 0 ? Loc.Get("bugs.save") : Loc.Get("bugs.add"), Accent, () =>
            {
                if (_editingCommentIndex >= 0)
                {
                    BugReportStore.EditUserComment(report.id, _editingCommentIndex, editor.value);
                }
                else
                {
                    BugReportStore.AddComment(report.id, editor.value);
                }

                _commentEditorId = -1;
                _editingCommentIndex = -1;
                RebuildManager();
            }));
            row.Add(buttons);
        }

        private void BuildReworkEditor(VisualElement row, BugReportStore.Report report)
        {
            var editor = new TextField { multiline = true };
            editor.style.fontSize = 12;
            editor.style.minHeight = 44f;
            editor.style.marginTop = 6f;
            editor.style.whiteSpace = WhiteSpace.Normal;
            row.Add(editor);

            var buttons = MakeActionRow();
            buttons.style.marginTop = 4f;
            buttons.Add(MakeSmallButton(Loc.Get("bugs.return_with_comment"), ChipRework, () =>
            {
                BugReportStore.SendToRework(report.id, editor.value);
                _reworkEditorId = -1;
                _tab = ManagerTab.Open;
                BuildManagerTabsAgain();
                RebuildManager();
            }));
            row.Add(buttons);
        }

        private static string StatusLabel(BugReportStore.Report report)
        {
            return report.status switch
            {
                BugReportStore.StatusInProgress => Loc.Get("bugs.status.in_progress"),
                BugReportStore.StatusReadyForTest => Loc.Get("bugs.status.ready_for_test"),
                BugReportStore.StatusRework => Loc.Get("bugs.status.rework"),
                BugReportStore.StatusFixed => Loc.Get("bugs.status.fixed"),
                _ => Loc.Get("bugs.status.created")
            };
        }

        private static Color StatusColor(BugReportStore.Report report)
        {
            if (report.status == BugReportStore.StatusRework)
            {
                return ChipRework;
            }

            if (report.status == BugReportStore.StatusInProgress)
            {
                return ChipInProgress;
            }

            if (report.status == BugReportStore.StatusReadyForTest)
            {
                return ChipReadyForTest;
            }

            if (report.status != BugReportStore.StatusFixed)
            {
                return ChipCreated;
            }

            return ChipFixed;
        }

        private static string VersionMeta(BugReportStore.Report report)
        {
            var agent = string.IsNullOrEmpty(report.assignedAgent)
                ? string.Empty
                : " · " + string.Format(Loc.Get("bugs.assigned_agent"), report.assignedAgent);
            var reported = string.IsNullOrEmpty(report.reportedInVersion)
                ? string.Empty
                : " · " + string.Format(Loc.Get("bugs.reported_version"), report.reportedInVersion);
            var readyIn = string.IsNullOrEmpty(report.readyForTestInVersion)
                ? string.Empty
                : " · " + string.Format(Loc.Get("bugs.ready_version"), report.readyForTestInVersion);
            var fixedIn = string.IsNullOrEmpty(report.fixedInVersion)
                ? string.Empty
                : " · " + string.Format(Loc.Get("bugs.fixed_version"), report.fixedInVersion);
            return agent + reported + readyIn + fixedIn;
        }

        private static string CommitsMeta(BugReportStore.Report report)
        {
            var commits = report.fixCommits ?? new List<string>();
            if (commits.Count == 0 && !string.IsNullOrEmpty(report.fixCommit))
            {
                return string.Format(Loc.Get("bugs.fix_commits"), ShortCommit(report.fixCommit));
            }

            var shown = new List<string>(commits.Count);
            foreach (var commit in commits)
            {
                if (!string.IsNullOrEmpty(commit))
                {
                    shown.Add(ShortCommit(commit));
                }
            }

            return shown.Count == 0 ? string.Empty : string.Format(Loc.Get("bugs.fix_commits"), string.Join(", ", shown));
        }

        private static string ShortCommit(string commit) => commit.Length <= 12 ? commit : commit[..12];

        private static (string text, Color color)? BuildStateLabel(BugReportStore.Report report)
        {
            if (report.status != BugReportStore.StatusReadyForTest && report.status != BugReportStore.StatusFixed)
            {
                return null;
            }

            var includedVersion = !string.IsNullOrEmpty(report.readyForTestInVersion)
                ? report.readyForTestInVersion
                : report.fixedInVersion;
            return !string.IsNullOrEmpty(includedVersion) && IsVersionInCurrentBuild(includedVersion)
                ? (Loc.Get("bugs.build.included"), ChipFixed)
                : (Loc.Get("bugs.build.not_included"), ChipCreated);
        }

        private static bool IsVersionInCurrentBuild(string version)
        {
            if (!Version.TryParse(NormalizeVersion(version), out var fixedVersion) ||
                !Version.TryParse(NormalizeVersion(Application.version), out var currentVersion))
            {
                return string.Equals(version, Application.version, StringComparison.OrdinalIgnoreCase);
            }

            return fixedVersion <= currentVersion;
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return "0.0.0";
            }

            return version.Trim().TrimStart('v', 'V');
        }

        private static VisualElement MakeActionRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.FlexEnd;
            return row;
        }

        private static VisualElement MakeButton(string text, Color accent, Action onClick)
        {
            var button = new VisualElement();
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.height = 30f;
            button.style.paddingLeft = 14f;
            button.style.paddingRight = 14f;
            button.style.marginLeft = 6f;
            button.style.backgroundColor = accent;
            SetRadius(button, 8f);

            var label = new Label(text);
            label.style.color = Text;
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.Add(label);
            button.RegisterCallback<MouseDownEvent>(evt => { onClick(); evt.StopPropagation(); });
            return button;
        }

        private static VisualElement MakeSmallButton(string text, Color accent, Action onClick)
        {
            var button = MakeButton(text, accent, onClick);
            button.style.height = 24f;
            button.style.paddingLeft = 10f;
            button.style.paddingRight = 10f;
            ((Label)button[0]).style.fontSize = 11;
            return button;
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        private static void SetBorder(VisualElement element, Color color)
        {
            element.style.borderTopWidth = 1f;
            element.style.borderBottomWidth = 1f;
            element.style.borderLeftWidth = 1f;
            element.style.borderRightWidth = 1f;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }
    }
}
