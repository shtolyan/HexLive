using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class EndSummaryPanel : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }

        [SerializeField] private SimulationRunnerBehaviour _runner;

        private UIDocument _document;
        private VisualElement _overlay;
        private VisualElement _survivorList;
        private VisualElement _fallenList;
        private Label _title;
        private Label _subtitle;
        private Label _survivorMetric;
        private Label _fallenMetric;
        private Label _dayMetric;
        private Label _timeMetric;
        private Label _raftMetric;
        private Label _healthMetric;
        private Label _survivorsHeader;
        private Label _fallenHeader;
        private Label _noFallenLabel;

        private int _lastTick = -1;
        private int _lastSurvivorCount = -1;
        private int _lastDeathCount = -1;
        private Language _lastLanguage;
        private bool _dismissed;

        private static readonly Color Dim = new(0f, 0f, 0f, 0.68f);
        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.98f);
        private static readonly Color PanelMid = new(0.102f, 0.125f, 0.149f, 0.98f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.12f);
        private static readonly Color StrokeStrong = new(1f, 1f, 1f, 0.22f);
        private static readonly Color Text = new(0.906f, 0.925f, 0.937f);
        private static readonly Color TextDim = new(0.604f, 0.651f, 0.678f);
        private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);
        private static readonly Color Green = new(0.298f, 0.761f, 0.522f);
        private static readonly Color Red = new(0.824f, 0.314f, 0.275f);

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "EndSummaryPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 1f;
                settings.sortingOrder = 240;
                _document.panelSettings = settings;
            }

            Build();
            IsOpen = false;
        }

        private void OnEnable() => Loc.LanguageChanged += MarkDirty;

        private void OnDisable()
        {
            Loc.LanguageChanged -= MarkDirty;
            IsOpen = false;
            if (NpcSelection.PointerOverUi)
            {
                NpcSelection.PointerOverUi = false;
            }
        }

        private void Update()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (LoadingScreen.IsActive || _runner == null || !_runner.IsReady)
            {
                Hide();
                return;
            }

            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null || !snapshot.Completed)
            {
                _dismissed = false;
                Hide();
                return;
            }

            if (_dismissed)
            {
                Hide();
                return;
            }

            Show();
            if (NeedsRefresh(snapshot))
            {
                Refresh(snapshot);
            }
        }

        private bool NeedsRefresh(WorldSnapshot snapshot)
        {
            return snapshot.Tick != _lastTick ||
                   snapshot.Npcs.Count != _lastSurvivorCount ||
                   snapshot.DeathRecords.Count != _lastDeathCount ||
                   Loc.Current != _lastLanguage;
        }

        private void MarkDirty()
        {
            _lastTick = -1;
        }

        private void Show()
        {
            if (_overlay != null)
            {
                _overlay.style.display = DisplayStyle.Flex;
            }

            IsOpen = true;
            NpcSelection.PointerOverUi = true;
        }

        private void Hide()
        {
            var wasOpen = IsOpen;
            if (_overlay != null)
            {
                _overlay.style.display = DisplayStyle.None;
            }

            IsOpen = false;
            if (wasOpen && NpcSelection.PointerOverUi)
            {
                NpcSelection.PointerOverUi = false;
            }
        }

        private void Dismiss()
        {
            _dismissed = true;
            Hide();
            NpcSelection.PointerOverUi = false;
        }

        private void Build()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.pickingMode = PickingMode.Ignore;

            _overlay = new VisualElement();
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0f;
            _overlay.style.right = 0f;
            _overlay.style.top = 0f;
            _overlay.style.bottom = 0f;
            _overlay.style.backgroundColor = Dim;
            _overlay.style.alignItems = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _overlay.style.paddingLeft = 24f;
            _overlay.style.paddingRight = 24f;
            _overlay.style.paddingTop = 24f;
            _overlay.style.paddingBottom = 24f;
            _overlay.style.display = DisplayStyle.None;
            _overlay.pickingMode = PickingMode.Position;
            _overlay.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
            _overlay.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
            root.Add(_overlay);

            var card = new VisualElement();
            card.style.width = Length.Percent(92f);
            card.style.maxWidth = 980f;
            card.style.maxHeight = Length.Percent(88f);
            card.style.backgroundColor = Panel;
            card.style.paddingLeft = 28f;
            card.style.paddingRight = 28f;
            card.style.paddingTop = 24f;
            card.style.paddingBottom = 24f;
            card.style.overflow = Overflow.Hidden;
            SetBorder(card, StrokeStrong, 1f);
            SetRadius(card, 8f);
            _overlay.Add(card);

            BuildHeader(card);
            BuildMetrics(card);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.marginTop = 18f;
            scroll.style.maxHeight = 470f;
            card.Add(scroll);

            _survivorsHeader = MakeSectionHeader();
            scroll.Add(_survivorsHeader);

            _survivorList = new VisualElement();
            _survivorList.style.flexDirection = FlexDirection.Column;
            scroll.Add(_survivorList);

            _fallenHeader = MakeSectionHeader();
            _fallenHeader.style.marginTop = 18f;
            scroll.Add(_fallenHeader);

            _fallenList = new VisualElement();
            _fallenList.style.flexDirection = FlexDirection.Column;
            scroll.Add(_fallenList);

            _noFallenLabel = new Label();
            _noFallenLabel.style.color = TextDim;
            _noFallenLabel.style.fontSize = 14;
            _noFallenLabel.style.marginTop = 6f;
            _noFallenLabel.style.whiteSpace = WhiteSpace.Normal;
            scroll.Add(_noFallenLabel);
        }

        private void BuildHeader(VisualElement card)
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.FlexStart;
            card.Add(header);

            var textCol = new VisualElement();
            textCol.style.flexGrow = 1f;
            textCol.style.paddingRight = 18f;
            header.Add(textCol);

            var eyebrow = new Label(Loc.Get("end.eyebrow"));
            eyebrow.style.color = Gold;
            eyebrow.style.fontSize = 12;
            eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            eyebrow.style.marginBottom = 6f;
            textCol.Add(eyebrow);

            _title = new Label();
            _title.style.color = Text;
            _title.style.fontSize = 34;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.whiteSpace = WhiteSpace.Normal;
            textCol.Add(_title);

            _subtitle = new Label();
            _subtitle.style.color = TextDim;
            _subtitle.style.fontSize = 15;
            _subtitle.style.marginTop = 8f;
            _subtitle.style.whiteSpace = WhiteSpace.Normal;
            textCol.Add(_subtitle);

            var close = new VisualElement();
            close.tooltip = Loc.Get("end.close_tooltip");
            close.style.width = 34f;
            close.style.height = 34f;
            close.style.alignItems = Align.Center;
            close.style.justifyContent = Justify.Center;
            close.style.backgroundColor = Raised;
            close.style.flexShrink = 0f;
            SetBorder(close, Stroke, 1f);
            SetRadius(close, 6f);
            close.RegisterCallback<MouseEnterEvent>(_ => close.style.backgroundColor = PanelMid);
            close.RegisterCallback<MouseLeaveEvent>(_ => close.style.backgroundColor = Raised);
            close.RegisterCallback<MouseDownEvent>(_ => Dismiss());
            header.Add(close);

            var x = new Label("X");
            x.style.color = Text;
            x.style.fontSize = 15;
            x.style.unityFontStyleAndWeight = FontStyle.Bold;
            x.style.unityTextAlign = TextAnchor.MiddleCenter;
            close.Add(x);
        }

        private void BuildMetrics(VisualElement card)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 22f;
            row.style.marginLeft = -5f;
            row.style.marginRight = -5f;
            card.Add(row);

            _survivorMetric = AddMetric(row, "end.survivors", Green);
            _fallenMetric = AddMetric(row, "end.fallen", Red);
            _dayMetric = AddMetric(row, "end.day", Gold);
            _timeMetric = AddMetric(row, "end.time", Text);
            _raftMetric = AddMetric(row, "end.raft", Gold);
            _healthMetric = AddMetric(row, "end.avg_health", Green);
        }

        private Label AddMetric(VisualElement row, string labelKey, Color valueColor)
        {
            var box = new VisualElement();
            box.style.minWidth = 140f;
            box.style.flexGrow = 1f;
            box.style.marginLeft = 5f;
            box.style.marginRight = 5f;
            box.style.marginBottom = 10f;
            box.style.paddingLeft = 14f;
            box.style.paddingRight = 14f;
            box.style.paddingTop = 10f;
            box.style.paddingBottom = 10f;
            box.style.backgroundColor = PanelMid;
            SetBorder(box, Stroke, 1f);
            SetRadius(box, 8f);
            row.Add(box);

            var label = new Label(Loc.Get(labelKey));
            label.style.color = TextDim;
            label.style.fontSize = 11;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            box.Add(label);

            var value = new Label("0");
            value.style.color = valueColor;
            value.style.fontSize = 22;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            value.style.marginTop = 2f;
            value.style.whiteSpace = WhiteSpace.NoWrap;
            box.Add(value);

            return value;
        }

        private static Label MakeSectionHeader()
        {
            var label = new Label();
            label.style.color = Text;
            label.style.fontSize = 17;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            return label;
        }

        // §72: the run summary is about the COLONY. The outsider is on the
        // island, is selectable, and is emphatically not one of the survivors
        // being counted — listing him would inflate survivorCount/total and
        // read as "we rescued five".
        private static List<NpcSnapshot> ColonyOf(WorldSnapshot snapshot)
        {
            var colony = new List<NpcSnapshot>();
            foreach (var npc in snapshot.Npcs)
            {
                if (!npc.IsHostileToColony)
                {
                    colony.Add(npc);
                }
            }

            return colony;
        }

        private void Refresh(WorldSnapshot snapshot)
        {
            var colony = ColonyOf(snapshot);
            _lastTick = snapshot.Tick;
            _lastSurvivorCount = snapshot.Npcs.Count;
            _lastDeathCount = snapshot.DeathRecords.Count;
            _lastLanguage = Loc.Current;

            var survivorCount = colony.Count;
            var deathCount = snapshot.DeathRecords.Count;
            var total = Mathf.Max(survivorCount + deathCount, survivorCount);
            var finalDay = DayNumber(snapshot.Tick);

            _title.text = Loc.Get("end.title");
            _subtitle.text = string.Format(Loc.Get("end.subtitle"), survivorCount, total);
            _survivorMetric.text = $"{survivorCount}/{total}";
            _fallenMetric.text = deathCount.ToString();
            _dayMetric.text = finalDay.ToString();
            _timeMetric.text = snapshot.Clock;
            _raftMetric.text = $"{snapshot.RaftProgress}/{snapshot.RaftTarget}";
            _healthMetric.text = survivorCount > 0
                ? $"{Mathf.RoundToInt(AverageHealth(colony) * 100f)}%"
                : "0%";

            _survivorsHeader.text = Loc.Get("end.survivors_title");
            _fallenHeader.text = Loc.Get("end.fallen_title");
            _noFallenLabel.text = Loc.Get("end.no_fallen");
            _noFallenLabel.style.display = deathCount == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            RebuildSurvivors(colony);
            RebuildDeaths(snapshot.DeathRecords);
        }

        private void RebuildSurvivors(IReadOnlyList<NpcSnapshot> survivors)
        {
            _survivorList.Clear();
            foreach (var npc in survivors)
            {
                var wounds = npc.Wounds.Count + npc.SeveredParts.Count;
                var detail =
                    $"{Loc.Get("end.hp")} {Percent(npc.Health)} · " +
                    $"{Loc.Get("end.blood")} {Percent(npc.Blood)} · " +
                    $"{Loc.Get("end.stamina")} {Percent(npc.Stamina)} · " +
                    $"{Loc.Get("end.wounds")} {wounds}";
                // §74: DisplayName is a name ID; the player sees the localized term.
                _survivorList.Add(MakeCharacterRow(Loc.NpcName(npc.DisplayName), detail, Green));
            }
        }

        private void RebuildDeaths(IReadOnlyList<DeathRecordSnapshot> deaths)
        {
            _fallenList.Clear();
            foreach (var death in deaths)
            {
                var name = string.IsNullOrWhiteSpace(death.DisplayName)
                    ? $"NPC #{death.EntityId}"
                    : Loc.NpcName(death.DisplayName);
                var detail =
                    $"{Loc.Get("end.on_day")} {DayNumber(death.Tick)} · " +
                    $"{Loc.Get("end.tile")} {death.Tile.Q},{death.Tile.R} · " +
                    $"{HumanizeDeathCause(death.Cause)}";
                _fallenList.Add(MakeCharacterRow(name, detail, Red));
            }
        }

        private static VisualElement MakeCharacterRow(string nameText, string detailText, Color accent)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 8f;
            row.style.paddingLeft = 12f;
            row.style.paddingRight = 12f;
            row.style.paddingTop = 10f;
            row.style.paddingBottom = 10f;
            row.style.backgroundColor = Raised;
            SetBorder(row, Stroke, 1f);
            SetRadius(row, 8f);

            var stripe = new VisualElement();
            stripe.style.width = 4f;
            stripe.style.alignSelf = Align.Stretch;
            stripe.style.backgroundColor = accent;
            stripe.style.marginRight = 12f;
            SetRadius(stripe, 2f);
            row.Add(stripe);

            var col = new VisualElement();
            col.style.flexGrow = 1f;
            row.Add(col);

            var name = new Label(string.IsNullOrWhiteSpace(nameText) ? Loc.Get("end.unknown") : nameText);
            name.style.color = Text;
            name.style.fontSize = 16;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            col.Add(name);

            var detail = new Label(detailText);
            detail.style.color = TextDim;
            detail.style.fontSize = 13;
            detail.style.marginTop = 2f;
            detail.style.whiteSpace = WhiteSpace.Normal;
            col.Add(detail);

            return row;
        }

        private static int DayNumber(int tick)
        {
            return EnvironmentSystem.CalendarDay(tick);
        }

        private static float AverageHealth(IReadOnlyList<NpcSnapshot> npcs)
        {
            if (npcs.Count == 0)
            {
                return 0f;
            }

            var total = 0f;
            for (var i = 0; i < npcs.Count; i++)
            {
                total += Mathf.Clamp01(npcs[i].Health);
            }

            return total / npcs.Count;
        }

        private static string Percent(float value)
        {
            return $"{Mathf.RoundToInt(Mathf.Clamp01(value) * 100f)}%";
        }

        private static string HumanizeDeathCause(string cause)
        {
            if (string.IsNullOrWhiteSpace(cause))
            {
                return Loc.Get("end.cause_unknown");
            }

            var primary = cause;
            var semi = primary.IndexOf(';');
            if (semi >= 0)
            {
                primary = primary.Substring(0, semi);
            }

            var colon = primary.IndexOf(':');
            var type = colon >= 0 ? primary.Substring(0, colon).Trim() : primary.Trim();

            return type switch
            {
                "BledOut" => Loc.Get("end.cause_bled_out"),
                "DogFight" => Loc.Get("end.cause_dog"),
                "Heatstroke" => Loc.Get("end.cause_heat"),
                "Hypothermia" => Loc.Get("end.cause_cold"),
                "LimbSevered" => Loc.Get("end.cause_limb"),
                "PreyFoughtBack" => Loc.Get("end.cause_self_defense"),
                "Preyed" => Loc.Get("end.cause_predation"),
                "StarvedToDeath" => Loc.Get("end.cause_starved"),
                "Sunburn" => Loc.Get("end.cause_sun"),
                "VitalPartDestroyed" => Loc.Get("end.cause_vital"),
                _ => primary.Trim()
            };
        }

        private static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static void SetBorder(VisualElement e, Color color, float width)
        {
            e.style.borderTopWidth = width;
            e.style.borderBottomWidth = width;
            e.style.borderLeftWidth = width;
            e.style.borderRightWidth = width;
            e.style.borderTopColor = color;
            e.style.borderBottomColor = color;
            e.style.borderLeftColor = color;
            e.style.borderRightColor = color;
        }
    }
}
