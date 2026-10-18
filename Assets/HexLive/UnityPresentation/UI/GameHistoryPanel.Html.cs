#nullable enable
using HexLive.UnityPresentation.History;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the colony history as the page's «history» screen. Refresh still
    /// reads the log (GameHistoryLog.ReadTail) and formats each record with
    /// GameHistoryFormatter; on the web it sends them, newest first, instead of
    /// building UI Toolkit rows. Folded, the screen is one pill; it lives only
    /// while a world is on screen.
    /// </summary>
    public sealed partial class GameHistoryPanel
    {
        private const string HtmlScreen = "history";
        private bool _htmlDirty = true;
        private string? _lastHtmlJson;
        private JArray _htmlEntries = new();
        private bool _htmlMore;

        private void UpdateHtmlShell()
        {
            if (!HtmlUi.Active) return;
            var inWorld = _runner != null && _runner.IsReady && !LoadingScreen.IsActive;
            if (!inWorld)
            {
                if (_lastHtmlJson != null) { HtmlUi.Hide(HtmlScreen); _lastHtmlJson = null; }
                return;
            }

            if (!_htmlDirty && _lastHtmlJson != null) return;
            _htmlDirty = false;
            if (_box != null) _box.style.display = DisplayStyle.None;
            if (_expandTab != null) _expandTab.style.display = DisplayStyle.None;
            var model = new JObject
            {
                ["collapsed"] = _collapsed,
                ["entries"] = _collapsed ? new JArray() : _htmlEntries,
                ["more"] = !_collapsed && _htmlMore,
            };
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        /// <summary>The records Refresh just read, as the page's timeline.</summary>
        private void PushHtmlEntries()
        {
            var entries = new JArray();
            for (var i = _scratch.Count - 1; i >= 0; i--) // the log is oldest first
            {
                var record = _scratch[i];
                var text = GameHistoryFormatter.Format(record);
                entries.Add(new JObject
                {
                    ["id"] = $"{record.Tick}:{i}",
                    ["time"] = text.Time,
                    ["title"] = text.Title,
                    ["detail"] = string.IsNullOrEmpty(text.Detail) ? null : text.Detail,
                    ["tone"] = text.Tone.ToString().ToLowerInvariant(),
                });
            }
            _htmlEntries = entries;
            // A full page means the log may hold older records.
            _htmlMore = _scratch.Count >= _visibleCount;
            _htmlDirty = true;
            UpdateHtmlShell();
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            switch (command)
            {
                case "expand": SetCollapsed(false); break;
                case "collapse": SetCollapsed(true); break;
                case "more":
                    if (_collapsed) return;
                    _visibleCount += PageSize;
                    Refresh(true);
                    break;
            }
            _htmlDirty = true;
        }
    }
}
