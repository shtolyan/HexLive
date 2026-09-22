#nullable enable
using System;
using System.Globalization;
using System.Linq;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class PlayerAssignmentDialog : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        private SimulationRunnerBehaviour? _runner;
        private UIDocument _document = null!;
        private PanelSettings? _settings;
        private string _worldId = string.Empty;
        private long _sequence;
        private string _language = string.Empty;

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            var settings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (settings != null)
            {
                _settings = Instantiate(settings);
                _settings.sortingOrder = 350;
                _document.panelSettings = _settings;
            }
        }

        private void OnDisable()
        {
            IsOpen = false;
            _sequence = 0;
            _document.rootVisualElement.Clear();
        }

        private void OnDestroy()
        {
            if (_settings != null) Destroy(_settings);
        }

        private void Update()
        {
            if (_runner == null) return;
            if (_runner.Link.State != LinkState.Live) return;
            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null) return;
            var notices = _runner.AssignmentNotices;
            var worldId = _runner.AssignmentWorldId;
            if (notices.Length == 0)
            {
                if (IsOpen) { _document.rootVisualElement.Clear(); IsOpen = false; }
                _sequence = 0;
                return;
            }
            var sequence = notices[notices.Length - 1].Sequence;
            if (IsOpen && worldId == _worldId && sequence == _sequence && _language == Loc.Code) return;
            _worldId = worldId;
            _sequence = sequence;
            _language = Loc.Code;
            var root = _document.rootVisualElement;
            root.Clear();
            root.pickingMode = PickingMode.Ignore;
            var sheet = Resources.Load<StyleSheet>("HexLive/PlayerAssignmentDialog");
            if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            var overlay = new VisualElement();
            overlay.AddToClassList("assignment-overlay");
            var card = new VisualElement();
            card.AddToClassList("assignment-card");
            card.Add(Text(Loc.Get("assignment.title"), "assignment-title"));
            var scroll = new ScrollView();
            scroll.AddToClassList("assignment-scroll");
            var hasLoss = notices.Any(n => n.Kind != "assigned");
            foreach (var notice in notices.Where(n => n.Kind != "assigned"))
            {
                var name = string.IsNullOrEmpty(notice.Name) ? $"#{notice.NpcId}" : Loc.NpcName(notice.Name);
                scroll.Add(Text(string.Format(Loc.Get("assignment." + notice.Kind), name)));
            }
            foreach (var notice in notices.Where(n => n.Kind == "assigned"))
            {
                var npc = snapshot.Npcs.FirstOrDefault(n => n.Id.Value == notice.NpcId && _runner.IsAssignedNpc(n.Id));
                // An unacknowledged introduction may already have been superseded by death.
                if (npc == null) continue;
                var name = Loc.NpcName(npc.DisplayName);
                scroll.Add(Text(string.Format(Loc.Get(hasLoss ? "assignment.replacement" : "assignment.introduction"), name), "assignment-name"));
                foreach (var row in npc.Attributes)
                {
                    var parts = row.Split('\t');
                    if (parts.Length != 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                    scroll.Add(Text($"{Loc.Get("attr." + parts[0].ToLowerInvariant())}: {Mathf.RoundToInt(Mathf.Max(0, value) * 10)}"));
                }
                scroll.Add(Text(Loc.Get("assignment.traits"), "assignment-name"));
                if (npc.Traits.Count == 0) scroll.Add(Text(Loc.Get("sheet.character.empty")));
                foreach (var trait in npc.Traits)
                    scroll.Add(Text($"{Loc.Get(trait + ".title")} — {Loc.Get(trait + ".desc")}"));
            }
            var assigned = snapshot.Npcs.Where(n => _runner.IsAssignedNpc(n.Id)).ToArray();
            scroll.Add(Text(Loc.Get(assigned.Length == 0 ? "assignment.waiting" : "assignment.persistence")));
            card.Add(scroll);
            var ok = new Button(() =>
            {
                _runner.AcknowledgeAssignmentNotices(worldId, sequence);
                root.Clear();
                IsOpen = false;
                if (assigned.Length > 0 && _runner.AssignmentWorldId == worldId)
                    NpcSelection.Replace(assigned[0].Id.Value, requestFrame: true);
            }) { text = Loc.Get("assignment.ok") };
            ok.AddToClassList("assignment-button");
            card.Add(ok);
            overlay.Add(card);
            root.Add(overlay);
            IsOpen = true;
            ok.Focus();
        }

        private static Label Text(string text, string? className = null)
        {
            var label = new Label(text) { enableRichText = false };
            label.AddToClassList("assignment-text");
            if (className != null) label.AddToClassList(className);
            return label;
        }
    }
}
