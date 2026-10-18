using System;
using System.Collections.Generic;
using System.Globalization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the bug tracker (§114) as two page screens — «bugReport», the quick
    /// form, and «bugManager», the list with the card of one report. The panel
    /// keeps its open state (RuntimeWindow: world gate, Escape) and BugReportStore
    /// stays the only door to Flashback; under HTML the UI Toolkit windows are
    /// registered but not drawn, and the page's commands call the same store
    /// methods the UI Toolkit buttons do.
    /// </summary>
    public sealed partial class BugReportPanel
    {
        private const string HtmlQuick = "bugReport";
        private const string HtmlManager = "bugManager";
        private int _htmlCard = -1;
        private bool _htmlDirty = true;
        private float _htmlNext;
        private string _lastQuickJson, _lastManagerJson;

        private void HideToolkitUnderHtml()
        {
            if (!HtmlUi.Active) return;
            // Show() keeps the windows registered (world gate, Escape); the page draws them.
            if (_quickWindow != null) _quickWindow.style.display = DisplayStyle.None;
            if (_managerWindow != null) _managerWindow.style.display = DisplayStyle.None;
            if (_quickModal?.Backdrop != null) _quickModal.Backdrop.style.display = DisplayStyle.None;
            if (_managerModal?.Backdrop != null) _managerModal.Backdrop.style.display = DisplayStyle.None;
            _htmlDirty = true;
        }

        private void UpdateHtml()
        {
            if (!HtmlUi.Active) return;
            if (!_quickVisible && _lastQuickJson != null) { HtmlUi.Hide(HtmlQuick); _lastQuickJson = null; }
            if (!_managerVisible && _lastManagerJson != null) { HtmlUi.Hide(HtmlManager); _lastManagerJson = null; }
            if (!Visible) return;
            // The store changes by commands and a 2 s poll; once a second is enough.
            if (!_htmlDirty && Time.unscaledTime < _htmlNext) return;
            _htmlDirty = false;
            _htmlNext = Time.unscaledTime + 1f;
            if (_quickVisible)
                Push(HtmlQuick, new JObject { ["context"] = BuildContext(), ["sending"] = false, ["error"] = null }, ref _lastQuickJson);
            if (_managerVisible) Push(HtmlManager, ManagerModel(), ref _lastManagerJson);
        }

        private static void Push(string screen, JObject model, ref string last)
        {
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == last) return;
            last = json;
            HtmlUi.Show(screen, model);
        }

        private JObject ManagerModel()
        {
            var counts = new Dictionary<ManagerTab, int> { [ManagerTab.Open] = 0, [ManagerTab.Fixed] = 0, [ManagerTab.Archive] = 0 };
            var list = new JArray();
            JObject card = null;
            var reports = BugReportStore.Reports;
            for (var i = reports.Count - 1; i >= 0; i--) // newest first, as in RebuildManager
            {
                var report = reports[i];
                counts[TabOf(report)]++;
                if (report.id == _htmlCard) card = CardModel(report);
                if (!BelongsToCurrentTab(report)) continue;
                list.Add(new JObject
                {
                    ["id"] = report.id, ["status"] = report.status, ["archived"] = report.archived,
                    ["title"] = report.text, ["created"] = When(report.createdUtc),
                    ["comments"] = report.comments?.Count ?? 0, ["build"] = BuildState(report),
                });
            }

            return new JObject
            {
                ["tab"] = _tab switch { ManagerTab.Fixed => "fixed", ManagerTab.Archive => "archive", _ => "open" },
                ["filter"] = _statusFilter,
                ["counts"] = new JObject
                {
                    ["open"] = counts[ManagerTab.Open], ["fixed"] = counts[ManagerTab.Fixed], ["archive"] = counts[ManagerTab.Archive],
                },
                ["reports"] = list,
                ["card"] = card,
            };
        }

        private static ManagerTab TabOf(BugReportStore.Report report) =>
            report.archived ? ManagerTab.Archive
            : report.status == BugReportStore.StatusFixed ? ManagerTab.Fixed
            : ManagerTab.Open;

        private static string BuildState(BugReportStore.Report report)
        {
            var state = BuildStateLabel(report);
            if (!state.HasValue) return null;
            return state.Value.text == Localization.Loc.Get("bugs.build.included") ? "included" : "not_included";
        }

        /// <summary>«18.10 14:02» in the player's clock; the raw string if it is not a date.</summary>
        private static string When(string utc)
        {
            if (string.IsNullOrEmpty(utc)) return string.Empty;
            return DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                : utc;
        }

        private JObject CardModel(BugReportStore.Report report)
        {
            // The same meta line the UI Toolkit row writes, split into parts.
            var meta = new JArray();
            foreach (var part in VersionMeta(report).Split(new[] { " · " }, StringSplitOptions.RemoveEmptyEntries))
                meta.Add(part.Trim());
            var commits = new JArray();
            foreach (var sha in report.fixCommits ?? new List<string>())
                if (!string.IsNullOrEmpty(sha)) commits.Add(ShortCommit(sha));
            if (commits.Count == 0 && !string.IsNullOrEmpty(report.fixCommit)) commits.Add(ShortCommit(report.fixCommit));
            var comments = new JArray();
            foreach (var comment in report.comments ?? new List<BugReportStore.Comment>())
            {
                var author = (comment.author ?? "claude").ToLowerInvariant();
                comments.Add(new JObject { ["author"] = author, ["text"] = comment.text, ["editable"] = author == "user" });
            }

            var ready = report.status == BugReportStore.StatusReadyForTest && !report.archived;
            return new JObject
            {
                ["id"] = report.id, ["status"] = report.status, ["archived"] = report.archived,
                ["created"] = When(report.createdUtc), ["meta"] = meta, ["build"] = BuildState(report),
                ["text"] = report.text, ["context"] = report.context, ["commits"] = commits, ["comments"] = comments,
                ["can"] = new JObject
                {
                    ["fix"] = ready, ["rework"] = ready,
                    ["archive"] = !report.archived && report.status == BugReportStore.StatusFixed,
                    ["restore"] = report.archived,
                },
            };
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            _htmlDirty = true;
            var id = (int?)args["id"] ?? -1;
            var text = ((string)args["text"])?.Trim();
            switch (command)
            {
                case "close":
                    SetQuickVisible(false);
                    SetManagerVisible(false);
                    return;
                case "submit":
                    if (string.IsNullOrEmpty(text) || !_quickVisible) return;
                    var created = BugReportStore.Add(text, BuildContext());
                    SetQuickVisible(false);
                    _tab = ManagerTab.Open;
                    _statusFilter = null;
                    SetManagerVisible(true);
                    _htmlCard = created?.id ?? -1;
                    return;
                case "manager":
                    SetManagerVisible(true);
                    return;
                case "new":
                    SetQuickVisible(true);
                    return;
            }

            if (!_managerVisible) return; // queued before the manager closed
            switch (command)
            {
                case "tab":
                    _tab = (string)args["value"] switch { "fixed" => ManagerTab.Fixed, "archive" => ManagerTab.Archive, _ => ManagerTab.Open };
                    _htmlCard = -1;
                    break;
                case "filter":
                    _statusFilter = (string)args["status"];
                    break;
                case "open":
                    _htmlCard = id;
                    break;
                case "back":
                    _htmlCard = -1;
                    break;
                case "editText":
                    if (!string.IsNullOrEmpty(text)) BugReportStore.EditReportText(id, text);
                    break;
                case "comment":
                    if (!string.IsNullOrEmpty(text)) BugReportStore.AddComment(id, text);
                    break;
                case "editComment":
                    if (!string.IsNullOrEmpty(text)) BugReportStore.EditUserComment(id, (int?)args["index"] ?? -1, text);
                    break;
                case "fixed":
                    BugReportStore.MarkFixed(id);
                    _htmlCard = -1; // §114.3B: the card closes after «fixed»
                    if (_statusFilter == null) _tab = ManagerTab.Fixed;
                    break;
                case "rework":
                    if (string.IsNullOrEmpty(text)) return; // the rework comment is required
                    BugReportStore.SendToRework(id, text);
                    if (_statusFilter == null) _tab = ManagerTab.Open;
                    break;
                case "archive":
                    BugReportStore.SetArchived(id, (bool?)args["value"] ?? true);
                    _htmlCard = -1;
                    break;
            }
            RebuildManager(); // keeps the hidden UI Toolkit list in step
        }
    }
}
