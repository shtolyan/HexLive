using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// The Sims-style character bar: a compact strip pinned to the bottom that
    /// slides in when an NPC is selected and slides out on deselect. Shows a
    /// live portrait, well-being needs, the current "thought", and
    /// relationships. Fully localized (RU/EN) and resolution-scaled
    /// (designed at 1920×1080, scales like the old CanvasScaler).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CharacterPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;

        [Tooltip("Extra UI scale on top of the 1080p reference. 1 = design size.")]
        [SerializeField] private float _uiScale = 1f;

        [Tooltip("Slide in/out duration in seconds.")]
        [SerializeField] private float _slideDuration = 0.22f;

        private PortraitStage _portraitStage;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _stage;
        private VisualElement _card;

        // Live elements
        private VisualElement _portrait;
        private Label _nameLabel;
        private Label _roleLabel;
        private Label _statusLabel;
        private VisualElement _statusDot;
        private Label _thoughtValue;
        private VisualElement _healthFill;
        private VisualElement _healthLockedFill;
        private Label _healthValue;
        private Label _starvingBadge;
        private VisualElement _needsContainer;
        private VisualElement _relationsContainer;
        private VisualElement _thought;

        // Static (re-labeled on language change)
        private Label _needsTitle;
        private Label _relationsTitle;
        private Label _thoughtLabel;
        private Button _langButton;

        private readonly List<NeedBinding> _needBindings = new();

        // Bipolar temperature cell (signed ThermalComfort: − cold, + hot).
        private Label _thermalLabel;
        private Label _thermalPct;
        private VisualElement _thermalFill;

        private int _boundActorId = -1;

        // Sim state only changes on a new tick, so the panel re-reads the
        // snapshot at most once per tick (language change invalidates too).
        private int _refreshedTick = -1;
        private int _refreshedActorId = -1;

        // Slide animation
        private bool _shown;
        private float _anim; // 0 = hidden (off-screen), 1 = fully shown

        // Collapsed: the character stays selected (camera keeps following),
        // only the bar slides away; a small bottom tab brings it back.
        private bool _collapsed;
        private VisualElement _expandTab;

        // ── palette ───────────────────────────────────────────────────────
        private static readonly Color Text = new(0.906f, 0.925f, 0.937f);
        private static readonly Color TextDim = new(0.604f, 0.651f, 0.678f);
        private static readonly Color TextMute = new(0.400f, 0.447f, 0.478f);
        private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.97f);
        private static readonly Color PanelMid = new(0.102f, 0.125f, 0.149f, 0.97f);
        private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
        private static readonly Color Stroke = new(1f, 1f, 1f, 0.10f);
        private static readonly Color StrokeStrong = new(1f, 1f, 1f, 0.13f);
        private static readonly Color Track = new(0.051f, 0.067f, 0.078f);
        private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);
        private static readonly Color GoldDim = new(0.541f, 0.416f, 0.204f);

        private static readonly Color Good = new(0.373f, 0.769f, 0.416f);
        private static readonly Color Warn = new(0.910f, 0.698f, 0.235f);
        private static readonly Color Crit = new(0.910f, 0.341f, 0.310f);
        private static readonly Color Health = new(0.910f, 0.337f, 0.439f);

        private static readonly Color Hunger = new(0.910f, 0.569f, 0.235f);
        private static readonly Color Thirst = new(0.278f, 0.714f, 0.902f);
        private static readonly Color Energy = new(0.373f, 0.769f, 0.416f);
        private static readonly Color Comfort = new(0.490f, 0.580f, 0.918f);
        private static readonly Color Social = new(0.710f, 0.545f, 0.886f);
        private static readonly Color Thermal = new(0.910f, 0.455f, 0.420f);

        private struct NeedConfig
        {
            public string Key;
            public VectorIcon.Kind Icon;
            public Color Color;
            public bool Pressure; // raw: higher = worse → invert for well-being

            // Show the raw pressure itself: a FULL red bar means "maxed out"
            // (stress reads this way — 100% stressed, not 0% well-being).
            public bool Direct;
        }

        private struct NeedBinding
        {
            public NeedConfig Config;
            public Label Label;
            public Label Pct;
            public VisualElement Fill;
        }

        private static readonly NeedConfig[] Needs =
        {
            new() { Key = "need.hunger", Icon = VectorIcon.Kind.Hunger, Color = Hunger, Pressure = true },
            new() { Key = "need.thirst", Icon = VectorIcon.Kind.Thirst, Color = Thirst, Pressure = true },
            new() { Key = "need.energy", Icon = VectorIcon.Kind.Energy, Color = Energy, Pressure = false },
            new() { Key = "need.comfort", Icon = VectorIcon.Kind.Comfort, Color = Comfort, Pressure = false },
            new() { Key = "need.social", Icon = VectorIcon.Kind.Social, Color = Social, Pressure = false },
            // Temperature is NOT here — it renders as a special bipolar bar
            // (0 center = comfy, red right = hot, blue left = cold).
            // Spec 40: survivor params. Stamina/Blood/Hygiene read high = good;
            // Stress reads high = bad (Pressure). Icons reuse existing glyphs
            // for the first pass — swap in dedicated ones later.
            new() { Key = "need.stamina", Icon = VectorIcon.Kind.Energy, Color = Energy, Pressure = false },
            new() { Key = "need.blood", Icon = VectorIcon.Kind.Health, Color = Health, Pressure = false },
            new() { Key = "need.hygiene", Icon = VectorIcon.Kind.Thirst, Color = Thirst, Pressure = false },
            new() { Key = "need.stress", Icon = VectorIcon.Kind.Think, Color = Social, Direct = true },
        };

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        public void SetPortraitStage(PortraitStage stage) => _portraitStage = stage;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();

            // CanvasScaler-equivalent: design at 1080p, scale uniformly to the
            // real resolution (so 4K looks like Full HD). Clone the debug panel
            // settings to inherit its runtime theme/font.
            var reference = new Vector2Int(
                Mathf.RoundToInt(1920f / Mathf.Max(0.25f, _uiScale)),
                Mathf.RoundToInt(1080f / Mathf.Max(0.25f, _uiScale)));

            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "CharacterPanelSettings";
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.referenceResolution = reference;
                settings.match = 1f; // match height — a bottom bar keeps its share of screen height
                settings.sortingOrder = 150;
                _document.panelSettings = settings;
            }
            else if (_document.panelSettings == null)
            {
                var settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = reference;
                settings.match = 1f;
                settings.sortingOrder = 150;
                _document.panelSettings = settings;
            }

            BuildUi();
            ApplyLanguage();
            _anim = 0f;
            _shown = false;
            _root.style.display = DisplayStyle.None;
        }

        private void OnEnable()
        {
            NpcSelection.SelectionChanged += OnSelectionChanged;
            Loc.LanguageChanged += ApplyLanguage;
        }

        private void OnDisable()
        {
            NpcSelection.SelectionChanged -= OnSelectionChanged;
            Loc.LanguageChanged -= ApplyLanguage;
        }

        private void Update()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame)
            {
                Loc.Toggle();
            }

            if (NpcSelection.HasSelection)
            {
                Refresh();
            }

            Animate();
            UpdatePointerOverUi();
        }

        // Robust "is the cursor over the bar" test, computed every frame from
        // the bar's geometry (the bar is a full-width bottom strip, so only the
        // vertical extent matters). Avoids fragile MouseEnter/Leave events that
        // could stick the flag on and block world-picking.
        private void UpdatePointerOverUi()
        {
            var mouse = Mouse.current;
            var panelHeight = _root != null ? _root.layout.height : 0f;
            if (!_shown || _card == null || mouse == null || panelHeight < 1f)
            {
                NpcSelection.PointerOverUi = false;
                return;
            }

            var scale = Screen.height / panelHeight;          // pixels per panel unit
            var mousePos = mouse.position.ReadValue();        // bottom-left origin

            if (_collapsed)
            {
                // Only the little expand tab occupies the screen.
                if (_expandTab == null)
                {
                    NpcSelection.PointerOverUi = false;
                    return;
                }

                var tab = _expandTab.layout;
                var withinY = mousePos.y <= tab.height * scale + 4f;
                var halfWidth = tab.width * scale * 0.5f + 4f;
                var withinX = Mathf.Abs(mousePos.x - Screen.width * 0.5f) <= halfWidth;
                NpcSelection.PointerOverUi = withinY && withinX;
                return;
            }

            var barPixels = _card.layout.height * scale;      // bar height from the bottom
            NpcSelection.PointerOverUi = mousePos.y <= barPixels;
        }

        private void OnSelectionChanged(int npcId)
        {
            _shown = npcId >= 0;
            if (npcId >= 0)
            {
                _root.style.display = DisplayStyle.Flex;
            }
            else
            {
                _boundActorId = -1;
                _collapsed = false; // next selection opens the bar again
                NpcSelection.PointerOverUi = false;
                // Stop the portrait camera when nobody is selected.
                if (_portraitStage != null)
                {
                    _portraitStage.SetTarget(-1);
                }
            }
        }

        // Slide the bar up from below the screen edge and back down.
        private void Animate()
        {
            var expanded = _shown && !_collapsed;
            var target = expanded ? 1f : 0f;
            if (_slideDuration > 0.001f)
            {
                _anim = Mathf.MoveTowards(_anim, target, Time.unscaledDeltaTime / _slideDuration);
            }
            else
            {
                _anim = target;
            }

            if (_expandTab != null)
            {
                _expandTab.style.display = _shown && _collapsed
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }

            if (!_shown && _anim <= 0.001f)
            {
                _root.style.display = DisplayStyle.None;
                NpcSelection.PointerOverUi = false;
                return;
            }

            var eased = 1f - Mathf.Pow(1f - _anim, 3f); // easeOutCubic
            var height = _stage.layout.height > 1f ? _stage.layout.height + 40f : 320f;
            _stage.style.translate = new Translate(0f, (1f - eased) * height);
        }

        // ── refresh ───────────────────────────────────────────────────────

        private void Refresh()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null)
            {
                return;
            }

            if (snapshot.Tick == _refreshedTick && NpcSelection.SelectedId == _refreshedActorId)
            {
                return;
            }

            var npc = FindNpc(snapshot, NpcSelection.SelectedId);
            if (npc == null)
            {
                return;
            }

            _refreshedTick = snapshot.Tick;
            _refreshedActorId = npc.Id.Value;

            _nameLabel.text = string.IsNullOrEmpty(npc.DisplayName) ? $"NPC #{npc.Id.Value}" : npc.DisplayName;
            _roleLabel.text = $"{Loc.Get("panel.role")} · #{npc.Id.Value}";
            _thoughtValue.text = Loc.Goal(npc.CurrentGoal);

            if (_boundActorId != npc.Id.Value)
            {
                _boundActorId = npc.Id.Value;
                BindPortrait(npc.Id.Value);
            }

            var hp = Mathf.Clamp01(npc.Health);
            _healthFill.style.width = Length.Percent(hp * 100f);
            // Red right segment: HP that will NOT regen until wounds close.
            var locked = Mathf.Clamp01(npc.WoundLockedHp);
            _healthLockedFill.style.width = Length.Percent(locked * 100f);
            _healthValue.text = locked > 0.005f
                ? $"{Mathf.RoundToInt(hp * 100f)}% (-{Mathf.RoundToInt(locked * 100f)})"
                : $"{Mathf.RoundToInt(hp * 100f)}%";

            UpdateStatus(npc);

            var showBadge = npc.IsStarving || npc.IsFighting;
            _starvingBadge.style.display = showBadge ? DisplayStyle.Flex : DisplayStyle.None;
            _starvingBadge.text = npc.IsFighting ? Loc.Get("badge.fighting") : Loc.Get("badge.starving");

            UpdateNeeds(npc);
            UpdateRelations(npc);
        }

        private void BindPortrait(int npcId)
        {
            if (_portraitStage == null)
            {
                return;
            }

            _portraitStage.SetTarget(npcId);
            var tex = _portraitStage.Texture;
            if (tex != null)
            {
                _portrait.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(tex));
            }
        }

        private void UpdateStatus(NpcSnapshot npc)
        {
            string key;
            Color dot;
            if (npc.MovementStatus == "Moving")
            {
                key = "state.moving";
                dot = Thirst;
            }
            else if (npc.CurrentGoal == "Idle" || npc.CurrentGoal == "None")
            {
                key = "state.resting";
                dot = TextMute;
            }
            else
            {
                key = "state.busy";
                dot = Energy;
            }

            _statusLabel.text = Loc.Get(key);
            _statusDot.style.backgroundColor = dot;
        }

        private void UpdateNeeds(NpcSnapshot npc)
        {
            for (var i = 0; i < _needBindings.Count; i++)
            {
                var b = _needBindings[i];
                var raw = Mathf.Clamp01(RawNeed(npc, b.Config.Key));

                // Direct (stress): the bar IS the pressure — 100% full and red
                // when maxed, a sliver of green when calm.
                // Otherwise: well-being — full green is good.
                var display = b.Config.Direct ? raw
                    : b.Config.Pressure ? 1f - raw : raw;
                var goodness = b.Config.Direct ? 1f - raw : display;

                b.Fill.style.width = Length.Percent(display * 100f);
                b.Fill.style.backgroundColor = LevelColor(goodness);
                b.Pct.text = $"{Mathf.RoundToInt(display * 100f)}%";

                var alarm = goodness < 0.30f;
                b.Pct.style.color = alarm ? Crit : TextDim;
                b.Label.style.color = alarm ? new Color(0.949f, 0.769f, 0.753f) : Text;
            }

            UpdateThermal(npc.ThermalComfort);
        }

        // signed: 0 = comfy, +1 = boiling (red, grows right), −1 = freezing
        // (blue, grows left from the center notch).
        private void UpdateThermal(float signed)
        {
            if (_thermalFill == null)
            {
                return;
            }

            signed = Mathf.Clamp(signed, -1f, 1f);
            var magnitude = Mathf.Abs(signed);
            var half = magnitude * 50f;

            _thermalFill.style.width = Length.Percent(half);
            _thermalFill.style.left = Length.Percent(signed >= 0f ? 50f : 50f - half);

            var cold = new Color(0.278f, 0.714f, 0.902f);
            var hot = Crit;
            _thermalFill.style.backgroundColor = signed >= 0f ? hot : cold;

            if (magnitude < 0.05f)
            {
                _thermalPct.text = "OK";
                _thermalPct.style.color = Good;
                _thermalLabel.style.color = Text;
            }
            else
            {
                _thermalPct.text = $"{(signed > 0f ? "+" : "−")}{Mathf.RoundToInt(magnitude * 100f)}%";
                var severe = magnitude >= 0.5f;
                _thermalPct.style.color = signed > 0f ? hot : cold;
                _thermalLabel.style.color = severe
                    ? (signed > 0f ? new Color(0.949f, 0.769f, 0.753f) : new Color(0.753f, 0.878f, 0.949f))
                    : Text;
            }
        }

        private static float RawNeed(NpcSnapshot npc, string key)
        {
            return key switch
            {
                "need.hunger" => npc.Hunger,
                "need.thirst" => npc.Thirst,
                "need.energy" => npc.Energy,
                "need.comfort" => npc.Comfort,
                "need.social" => npc.Social,
                "need.stamina" => npc.Stamina,
                "need.blood" => npc.Blood,
                "need.hygiene" => npc.Hygiene,
                "need.stress" => npc.Stress,
                _ => 0f
            };
        }

        private void UpdateRelations(NpcSnapshot npc)
        {
            _relationsContainer.Clear();

            if (npc.RelationshipDetails.Count == 0)
            {
                var none = new Label(Loc.Get("panel.none"));
                none.style.color = TextMute;
                none.style.fontSize = 11;
                _relationsContainer.Add(none);
                return;
            }

            var shown = 0;
            foreach (var rel in npc.RelationshipDetails)
            {
                _relationsContainer.Add(BuildRelationChip(rel));
                if (++shown >= 3)
                {
                    break;
                }
            }
        }

        private VisualElement BuildRelationChip(RelationshipSnapshot rel)
        {
            var chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.backgroundColor = Panel;
            SetBorder(chip, Stroke, 1f);
            SetRadius(chip, 8f);
            chip.style.paddingLeft = 10f;
            chip.style.paddingRight = 10f;
            chip.style.paddingTop = 7f;
            chip.style.paddingBottom = 7f;
            chip.style.marginBottom = 8f;

            var av = new VisualElement();
            av.style.width = 32f;
            av.style.height = 32f;
            av.style.flexShrink = 0f;
            SetRadius(av, 16f);
            av.style.backgroundColor = AvatarColor(rel.OtherId);
            av.style.alignItems = Align.Center;
            av.style.justifyContent = Justify.Center;
            var initial = new Label(InitialOf(rel.OtherName));
            initial.style.color = new Color(0.06f, 0.086f, 0.102f);
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.fontSize = 14;
            av.Add(initial);
            chip.Add(av);

            var mid = new VisualElement();
            mid.style.flexGrow = 1f;
            mid.style.marginLeft = 10f;
            var rn = new Label(rel.OtherName);
            rn.style.color = Text;
            rn.style.fontSize = 14;
            rn.style.unityFontStyleAndWeight = FontStyle.Bold;
            var rk = new Label(RelationKind(rel.Affinity));
            rk.style.color = TextMute;
            rk.style.fontSize = 10;
            mid.Add(rn);
            mid.Add(rk);
            chip.Add(mid);

            var hearts = new VisualElement();
            hearts.style.flexDirection = FlexDirection.Row;
            hearts.style.flexShrink = 0f;
            var filled = HeartsFor(rel.Affinity);
            for (var i = 0; i < 4; i++)
            {
                var heart = new VectorIcon(VectorIcon.Kind.HeartFill,
                    i < filled ? Health : new Color(0.184f, 0.216f, 0.239f));
                heart.style.width = 14f;
                heart.style.height = 14f;
                heart.style.marginLeft = 3f;
                hearts.Add(heart);
            }
            chip.Add(hearts);

            var otherId = rel.OtherId;
            chip.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(chip, GoldDim));
            chip.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(chip, Stroke));
            chip.RegisterCallback<MouseDownEvent>(_ => NpcSelection.Select(otherId));

            return chip;
        }

        // ── UI construction ───────────────────────────────────────────────

        private void BuildUi()
        {
            _root = _document.rootVisualElement;
            _root.Clear();
            _root.style.flexGrow = 1f;
            _root.style.justifyContent = Justify.FlexEnd;
            _root.style.alignItems = Align.Stretch;
            _root.pickingMode = PickingMode.Ignore;

            // Full-width bar, lifted slightly off the bottom edge so nothing
            // hugs the screen border.
            _stage = new VisualElement();
            _stage.style.flexDirection = FlexDirection.Column;
            _stage.style.alignItems = Align.Stretch;
            _stage.style.width = Length.Percent(100);
            _stage.style.marginBottom = 12f;
            _stage.pickingMode = PickingMode.Ignore;
            _root.Add(_stage);

            BuildThought();

            // Three-zone card; height fits two rows of needs.
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.width = Length.Percent(100);
            card.style.height = 224f;
            card.style.backgroundColor = Stroke;
            SetBorder(card, StrokeStrong, 1f);
            SetRadius(card, 16f);
            card.style.overflow = Overflow.Hidden;
            card.pickingMode = PickingMode.Position;
            _card = card;
            _stage.Add(card);

            card.Add(BuildIdentityColumn());
            card.Add(BuildNeedsColumn());
            card.Add(BuildRelationsColumn());

            card.Add(BuildCollapseButton());
            BuildExpandTab();
        }

        // Small chevron-down in the card's top-right corner: hide the bar but
        // keep the character selected (camera keeps following them).
        private VisualElement BuildCollapseButton()
        {
            var button = new VisualElement();
            button.style.position = Position.Absolute;
            button.style.top = 8f;
            button.style.right = 12f;
            button.style.width = 30f;
            button.style.height = 24f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.backgroundColor = Raised;
            SetBorder(button, Stroke, 1f);
            SetRadius(button, 6f);

            var icon = new VectorIcon(VectorIcon.Kind.ChevronDown, TextDim);
            icon.style.width = 16f;
            icon.style.height = 16f;
            button.Add(icon);

            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, GoldDim));
            button.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(button, Stroke));
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                _collapsed = true;
                evt.StopPropagation();
            });

            return button;
        }

        // Bottom-center tab with a chevron-up, shown while the bar is hidden.
        private void BuildExpandTab()
        {
            _expandTab = new VisualElement();
            _expandTab.style.position = Position.Absolute;
            _expandTab.style.bottom = 0f;
            _expandTab.style.left = Length.Percent(50);
            _expandTab.style.translate = new Translate(Length.Percent(-50), 0f);
            _expandTab.style.flexDirection = FlexDirection.Row;
            _expandTab.style.alignItems = Align.Center;
            _expandTab.style.justifyContent = Justify.Center;
            _expandTab.style.width = 96f;
            _expandTab.style.height = 30f;
            _expandTab.style.backgroundColor = Raised;
            SetBorder(_expandTab, StrokeStrong, 1f);
            _expandTab.style.borderTopLeftRadius = 10f;
            _expandTab.style.borderTopRightRadius = 10f;
            _expandTab.style.borderBottomWidth = 0f;
            _expandTab.style.display = DisplayStyle.None;
            _expandTab.pickingMode = PickingMode.Position;

            var icon = new VectorIcon(VectorIcon.Kind.ChevronUp, Gold);
            icon.style.width = 18f;
            icon.style.height = 18f;
            _expandTab.Add(icon);

            _expandTab.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(_expandTab, GoldDim));
            _expandTab.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(_expandTab, StrokeStrong));
            _expandTab.RegisterCallback<MouseDownEvent>(evt =>
            {
                _collapsed = false;
                evt.StopPropagation();
            });

            _root.Add(_expandTab);
        }

        private void BuildThought()
        {
            _thought = new VisualElement();
            _thought.style.flexDirection = FlexDirection.Row;
            _thought.style.alignItems = Align.Center;
            _thought.style.alignSelf = Align.FlexStart;
            _thought.style.marginLeft = 20f;
            _thought.style.marginBottom = 7f;
            _thought.style.backgroundColor = Raised;
            SetBorder(_thought, StrokeStrong, 1f);
            SetRadius(_thought, 999f);
            _thought.style.paddingLeft = 11f;
            _thought.style.paddingRight = 14f;
            _thought.style.paddingTop = 6f;
            _thought.style.paddingBottom = 6f;

            var icon = new VectorIcon(VectorIcon.Kind.Think, Gold);
            icon.style.width = 17f;
            icon.style.height = 17f;
            icon.style.marginRight = 9f;
            icon.style.flexShrink = 0f;
            _thought.Add(icon);

            _thoughtLabel = MakeCaption("");
            _thoughtLabel.style.marginRight = 9f;
            _thoughtValue = new Label();
            _thoughtValue.style.color = Text;
            _thoughtValue.style.fontSize = 15;
            _thoughtValue.style.unityFontStyleAndWeight = FontStyle.Bold;

            _thought.Add(_thoughtLabel);
            _thought.Add(_thoughtValue);
            _stage.Add(_thought);
        }

        private VisualElement BuildIdentityColumn()
        {
            var col = new VisualElement();
            col.style.width = 396f;
            col.style.flexShrink = 0f;
            col.style.flexDirection = FlexDirection.Row;
            col.style.alignItems = Align.Center;
            col.style.backgroundColor = Panel;
            col.style.paddingLeft = 22f;
            col.style.paddingRight = 18f;
            col.style.marginRight = 2f;

            // Portrait
            var wrap = new VisualElement();
            wrap.style.width = 156f;
            wrap.style.height = 156f;
            wrap.style.flexShrink = 0f;
            _portrait = new VisualElement();
            _portrait.style.width = 156f;
            _portrait.style.height = 156f;
            SetRadius(_portrait, 78f);
            _portrait.style.overflow = Overflow.Hidden;
            SetBorder(_portrait, Gold, 2.5f);
            _portrait.style.backgroundColor = new Color(0.10f, 0.12f, 0.14f);
            wrap.Add(_portrait);
            wrap.Add(BuildLiveBadge());
            col.Add(wrap);

            // Info column
            var info = new VisualElement();
            info.style.flexGrow = 1f;
            info.style.marginLeft = 18f;

            _nameLabel = new Label("—");
            _nameLabel.style.color = Text;
            _nameLabel.style.fontSize = 23;
            _nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            info.Add(_nameLabel);

            _roleLabel = new Label();
            _roleLabel.style.color = TextMute;
            _roleLabel.style.fontSize = 11;
            _roleLabel.style.marginBottom = 12f;
            info.Add(_roleLabel);

            // Health caption + value
            var hRow = new VisualElement();
            hRow.style.flexDirection = FlexDirection.Row;
            hRow.style.alignItems = Align.Center;
            hRow.style.marginBottom = 5f;
            var hIcon = new VectorIcon(VectorIcon.Kind.Health, Health);
            hIcon.style.width = 14f;
            hIcon.style.height = 14f;
            hIcon.style.marginRight = 6f;
            hIcon.style.flexShrink = 0f;
            var hCap = MakeCaption("HP");
            hCap.style.flexGrow = 1f;
            _healthValue = new Label("—");
            _healthValue.style.color = Text;
            _healthValue.style.fontSize = 11;
            _healthValue.style.flexShrink = 0f;
            hRow.Add(hIcon);
            hRow.Add(hCap);
            hRow.Add(_healthValue);
            info.Add(hRow);

            // Spec 40.8B: Fallout-style HP bar — green = current health,
            // red (right-anchored) = HP locked by open wounds; regen can only
            // fill the gap between them, the red shrinks as wounds close.
            var hTrack = MakeTrack(5f);
            _healthFill = MakeFill(new Color(0.36f, 0.72f, 0.33f));
            hTrack.Add(_healthFill);
            _healthLockedFill = new VisualElement();
            _healthLockedFill.style.position = Position.Absolute;
            _healthLockedFill.style.right = 0f;
            _healthLockedFill.style.top = 0f;
            _healthLockedFill.style.bottom = 0f;
            _healthLockedFill.style.width = Length.Percent(0f);
            _healthLockedFill.style.backgroundColor = new Color(0.72f, 0.16f, 0.14f);
            hTrack.Add(_healthLockedFill);
            info.Add(hTrack);

            // Status chip + badge in a row
            var statusRow = new VisualElement();
            statusRow.style.flexDirection = FlexDirection.Row;
            statusRow.style.alignItems = Align.Center;
            statusRow.style.marginTop = 8f;

            _statusDot = new VisualElement();
            _statusDot.style.width = 7f;
            _statusDot.style.height = 7f;
            _statusDot.style.flexShrink = 0f;
            SetRadius(_statusDot, 3.5f);
            _statusDot.style.backgroundColor = Energy;
            _statusDot.style.marginRight = 6f;
            _statusLabel = new Label();
            _statusLabel.style.color = TextDim;
            _statusLabel.style.fontSize = 13;
            _statusLabel.style.flexGrow = 1f;
            statusRow.Add(_statusDot);
            statusRow.Add(_statusLabel);

            _starvingBadge = new Label();
            _starvingBadge.style.color = Color.white;
            _starvingBadge.style.backgroundColor = new Color(0.75f, 0.12f, 0.12f);
            _starvingBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _starvingBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            _starvingBadge.style.flexShrink = 0f;
            _starvingBadge.style.paddingLeft = 7f;
            _starvingBadge.style.paddingRight = 7f;
            _starvingBadge.style.paddingTop = 2f;
            _starvingBadge.style.paddingBottom = 2f;
            SetRadius(_starvingBadge, 5f);
            _starvingBadge.style.fontSize = 10;
            _starvingBadge.style.display = DisplayStyle.None;
            statusRow.Add(_starvingBadge);

            info.Add(statusRow);
            col.Add(info);

            return col;
        }

        private VisualElement BuildLiveBadge()
        {
            var live = new VisualElement();
            live.style.position = Position.Absolute;
            live.style.bottom = 2f;
            live.style.right = 2f;
            live.style.width = 12f;
            live.style.height = 12f;
            SetRadius(live, 6f);
            live.style.backgroundColor = new Color(1f, 0.353f, 0.302f);
            SetBorder(live, new Color(0.075f, 0.094f, 0.110f), 2f);
            return live;
        }

        private VisualElement BuildNeedsColumn()
        {
            var col = new VisualElement();
            col.style.flexGrow = 1f;
            col.style.backgroundColor = PanelMid;
            col.style.paddingLeft = 22f;
            col.style.paddingRight = 22f;
            col.style.paddingTop = 16f;
            col.style.paddingBottom = 16f;
            col.style.marginRight = 2f;
            col.style.justifyContent = Justify.Center;

            _needsTitle = MakeSectionTitle("");
            col.Add(_needsTitle);

            _needsContainer = new VisualElement();
            _needsContainer.style.flexDirection = FlexDirection.Row;
            _needsContainer.style.flexWrap = Wrap.Wrap;
            col.Add(_needsContainer);

            _needBindings.Clear();
            for (var i = 0; i < Needs.Length; i++)
            {
                _needsContainer.Add(BuildNeedCell(Needs[i]));
                if (i == 4)
                {
                    // Temperature opens the second row, right under "Fed".
                    _needsContainer.Add(BuildThermalCell());
                }
            }

            return col;
        }

        // Bipolar bar: the fill grows from the CENTER — right and red when
        // hot (+1), left and blue when cold (−1); an empty bar means comfy.
        private VisualElement BuildThermalCell()
        {
            var cell = new VisualElement();
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.width = Length.Percent(20f);
            cell.style.paddingRight = 16f;
            cell.style.marginTop = 9f;
            cell.style.marginBottom = 9f;

            var icon = new VectorIcon(VectorIcon.Kind.Thermal, Thermal);
            icon.style.width = 20f;
            icon.style.height = 20f;
            icon.style.marginRight = 9f;
            icon.style.flexShrink = 0f;
            cell.Add(icon);

            var body = new VisualElement();
            body.style.flexGrow = 1f;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 6f;
            _thermalLabel = new Label();
            _thermalLabel.style.color = Text;
            _thermalLabel.style.fontSize = 12;
            _thermalLabel.style.flexGrow = 1f;
            _thermalLabel.style.overflow = Overflow.Hidden;
            _thermalLabel.style.textOverflow = TextOverflow.Ellipsis;
            _thermalLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _thermalPct = new Label("—");
            _thermalPct.style.color = TextDim;
            _thermalPct.style.fontSize = 11;
            _thermalPct.style.unityFontStyleAndWeight = FontStyle.Bold;
            _thermalPct.style.flexShrink = 0f;
            _thermalPct.style.marginLeft = 6f;
            top.Add(_thermalLabel);
            top.Add(_thermalPct);
            body.Add(top);

            var track = MakeTrack(9f);

            _thermalFill = new VisualElement();
            _thermalFill.style.position = Position.Absolute;
            _thermalFill.style.top = 0f;
            _thermalFill.style.bottom = 0f;
            _thermalFill.style.left = Length.Percent(50f);
            _thermalFill.style.width = Length.Percent(0f);
            track.Add(_thermalFill);

            // Center notch marking the "comfortable" zero point.
            var notch = new VisualElement();
            notch.style.position = Position.Absolute;
            notch.style.top = 0f;
            notch.style.bottom = 0f;
            notch.style.left = Length.Percent(50f);
            notch.style.width = 2f;
            notch.style.marginLeft = -1f;
            notch.style.backgroundColor = new Color(1f, 1f, 1f, 0.35f);
            track.Add(notch);

            body.Add(track);
            cell.Add(body);
            return cell;
        }

        private VisualElement BuildNeedCell(NeedConfig config)
        {
            // 5 across, 2 rows (fits the 10 survivor needs without clipping).
            var cell = new VisualElement();
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.width = Length.Percent(20f);
            cell.style.paddingRight = 16f;
            cell.style.marginTop = 9f;
            cell.style.marginBottom = 9f;

            var icon = new VectorIcon(config.Icon, config.Color);
            icon.style.width = 20f;
            icon.style.height = 20f;
            icon.style.marginRight = 9f;
            icon.style.flexShrink = 0f;
            cell.Add(icon);

            var body = new VisualElement();
            body.style.flexGrow = 1f;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 6f;
            var label = new Label();
            label.style.color = Text;
            label.style.fontSize = 12;
            label.style.flexGrow = 1f;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            var pct = new Label("—");
            pct.style.color = TextDim;
            pct.style.fontSize = 11;
            pct.style.unityFontStyleAndWeight = FontStyle.Bold;
            pct.style.flexShrink = 0f;
            pct.style.marginLeft = 6f;
            top.Add(label);
            top.Add(pct);
            body.Add(top);

            var track = MakeTrack(9f);
            var fill = MakeFill(config.Color);
            track.Add(fill);
            body.Add(track);

            cell.Add(body);

            _needBindings.Add(new NeedBinding
            {
                Config = config,
                Label = label,
                Pct = pct,
                Fill = fill
            });

            return cell;
        }

        private VisualElement BuildRelationsColumn()
        {
            var col = new VisualElement();
            col.style.width = 372f;
            col.style.flexShrink = 0f;
            col.style.backgroundColor = Panel;
            col.style.paddingLeft = 22f;
            col.style.paddingRight = 22f;
            col.style.paddingTop = 16f;
            col.style.paddingBottom = 16f;
            col.style.justifyContent = Justify.Center;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 8f;
            _relationsTitle = MakeSectionTitle("");
            _relationsTitle.style.marginBottom = 0f;
            _relationsTitle.style.flexGrow = 1f;
            header.Add(_relationsTitle);

            _langButton = new Button(Loc.Toggle) { text = Loc.Code };
            _langButton.style.backgroundColor = Raised;
            SetBorder(_langButton, Stroke, 1f);
            SetRadius(_langButton, 6f);
            _langButton.style.color = TextDim;
            _langButton.style.fontSize = 10;
            _langButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _langButton.style.paddingLeft = 8f;
            _langButton.style.paddingRight = 8f;
            _langButton.style.paddingTop = 2f;
            _langButton.style.paddingBottom = 2f;
            _langButton.style.marginTop = 0f;
            _langButton.style.marginBottom = 0f;
            _langButton.style.marginLeft = 0f;
            _langButton.style.marginRight = 0f;
            header.Add(_langButton);
            col.Add(header);

            _relationsContainer = new VisualElement();
            col.Add(_relationsContainer);

            return col;
        }

        // ── language ──────────────────────────────────────────────────────

        private void ApplyLanguage()
        {
            if (_needsTitle == null)
            {
                return;
            }

            _refreshedTick = -1; // texts set in Refresh() need re-localizing

            _needsTitle.text = Loc.Get("panel.needs");
            _relationsTitle.text = Loc.Get("panel.relations");
            _thoughtLabel.text = Loc.Get("panel.wants");
            if (_langButton != null)
            {
                _langButton.text = Loc.Code;
            }

            for (var i = 0; i < _needBindings.Count; i++)
            {
                _needBindings[i].Label.text = Loc.Get(_needBindings[i].Config.Key);
            }

            if (_thermalLabel != null)
            {
                _thermalLabel.text = Loc.Get("need.temperature");
            }
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static NpcSnapshot FindNpc(WorldSnapshot snapshot, int id)
        {
            if (snapshot == null)
            {
                return null;
            }

            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value == id)
                {
                    return npc;
                }
            }

            return null;
        }

        private static Color LevelColor(float v)
        {
            if (v >= 0.55f) return Good;
            if (v >= 0.30f) return Warn;
            return Crit;
        }

        private static int HeartsFor(float affinity)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(affinity) * 4f), 0, 4);
        }

        private static string RelationKind(float affinity)
        {
            if (affinity >= 0.75f) return Loc.Get("rel.close");
            if (affinity >= 0.50f) return Loc.Get("rel.friend");
            if (affinity >= 0.25f) return Loc.Get("rel.acquaint");
            if (affinity >= 0.10f) return Loc.Get("rel.neutral");
            return Loc.Get("rel.tense");
        }

        private static string InitialOf(string name)
        {
            return string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
        }

        private static Color AvatarColor(int id)
        {
            return (id % 4) switch
            {
                1 => new Color(0.902f, 0.753f, 0.478f),
                2 => new Color(0.561f, 0.816f, 0.753f),
                3 => new Color(0.843f, 0.620f, 0.812f),
                _ => new Color(0.788f, 0.694f, 0.561f)
            };
        }

        private static Label MakeSectionTitle(string text)
        {
            var label = new Label(text);
            label.style.color = TextMute;
            label.style.fontSize = 11;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 1.5f;
            label.style.marginBottom = 10f;
            return label;
        }

        private static Label MakeCaption(string text)
        {
            var label = new Label(text);
            label.style.color = TextMute;
            label.style.fontSize = 9;
            label.style.letterSpacing = 0.6f;
            return label;
        }

        private static VisualElement MakeTrack(float height)
        {
            var track = new VisualElement();
            track.style.height = height;
            track.style.width = Length.Percent(100);
            track.style.backgroundColor = Track;
            SetBorder(track, new Color(1f, 1f, 1f, 0.06f), 1f);
            SetRadius(track, height * 0.5f);
            track.style.overflow = Overflow.Hidden;
            return track;
        }

        // The fill is a flat left-anchored bar; the track's rounded overflow
        // clip gives it clean rounded ends (no floating "pill").
        private static VisualElement MakeFill(Color color)
        {
            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0f;
            fill.style.top = 0f;
            fill.style.bottom = 0f;
            fill.style.width = Length.Percent(0f);
            fill.style.minWidth = 4f;
            fill.style.backgroundColor = color;
            return fill;
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
            SetBorderColor(e, color);
        }

        private static void SetBorderColor(VisualElement e, Color color)
        {
            e.style.borderTopColor = color;
            e.style.borderBottomColor = color;
            e.style.borderLeftColor = color;
            e.style.borderRightColor = color;
        }
    }
}
