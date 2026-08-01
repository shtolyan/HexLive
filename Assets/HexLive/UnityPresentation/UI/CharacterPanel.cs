using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Content;
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
        private VectorIcon _uvIcon;
        private Label _uvLabel;
        private VisualElement _statusDot;
        private Label _thoughtValue;
        private VisualElement _healthFill;
        private VisualElement _healthLockedFill;
        private Label _healthValue;
        private Label _starvingBadge;
        private VisualElement _needsContainer;
        private VisualElement _relationsContainer;
        private VisualElement _thought;
        // Spec §64: the dream pill (aspiration) — a sibling of the thought pill.
        private VisualElement _dream;
        private Label _dreamValue;

        // Spec §48: status-effect chips (buff/debuff circles) + hover tooltip.
        private VisualElement _effectsRow;
        private VisualElement _effectTooltip;
        private Label _effectTooltipIcon;
        private Label _effectTooltipTitle;
        private Label _effectTooltipDesc;
        // Only rebuild the chips when the SET of effects changes, so hovering
        // stays stable across ticks (intensity-only shifts recolour in place).
        private string _effectSig = null;

        // Spec §51: character inventory — a backpack button on the identity
        // column pops a floating window listing worn + carried items; clicking
        // one swaps to a detail "item view" (name, category, description, stats).
        private VisualElement _inventoryButton;
        private Label _inventoryButtonLabel;
        private VisualElement _inventoryWindow;
        private Label _inventoryTitle;
        private Label _inventoryCapacity;
        private VisualElement _invListView;   // the item list (master)
        private VisualElement _invListBody;
        private VisualElement _invDetailView; // the item detail (detail)
        private Label _invDetailEmoji;
        private Image _invDetailIcon;
        private Label _invDetailName;
        private Label _invDetailCategory;
        private Label _invDetailDesc;
        private VisualElement _invDetailStats;
        private Label _invBackLabel;
        private bool _inventoryOpen;
        private string _invSig;               // rebuild the list only on change
        private string _invSelectedId;        // item shown in the detail view
        private bool _invSelectedWorn;

        // Spec §57: limb-health window — click the HP row to pop a floating
        // window with the rotating body doll (per-zone green→yellow→red mesh)
        // and a per-limb readout list.
        private HealthDollStage _healthDollStage;
        private NpcPortraitCache _portraitCache;
        private VisualElement _healthWindow;
        private Label _healthTitle;
        private VisualElement _healthDollImage;
        private bool _healthOpen;
        private readonly List<ZoneRowBinding> _zoneRows = new();

        private struct ZoneRowBinding
        {
            public string Zone;
            public VisualElement Dot;
            public Label Name;
            public Label Armor; // 🛡 worn-armor absorption for this zone
            public Label Value;
        }

        private struct WaterContainerState
        {
            public float Amount;
            public float Capacity;
        }

        // Static (re-labeled on language change)
        private Label _needsTitle;

        // §76: the character-sheet pages sharing the middle column with needs.
        private Label _natureTitle;
        private Label _skillsTitle;
        private VisualElement _natureContainer;
        private VisualElement _skillsContainer;
        private VisualElement _perkRow;
        private SheetTab _sheetTab = SheetTab.Needs;
        private readonly List<SheetBinding> _attrBindings = new();
        private readonly List<SheetBinding> _skillBindings = new();
        private string _perkSig;
        private Label _relationsTitle;
        private Button _langButton;

        private readonly List<NeedBinding> _needBindings = new();
        private int _selectedRelationId = -1;

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

        private const float PanelBottomOffset = 0f;
        private const float CharacterCardHeight = 286f;
        private const float FloatingInventoryGap = 12f;
        private const float InventoryWindowBottom = PanelBottomOffset + CharacterCardHeight + FloatingInventoryGap;

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
        // Spec §53: compassion — a warm rose, high = good (at peace).
        private static readonly Color Compassion = new(0.925f, 0.451f, 0.588f);
        private static readonly Color Thermal = new(0.910f, 0.455f, 0.420f);

        // Spec §51: per-category accent for the inventory item slots/tags.
        private static Color CategoryColor(ItemCategory category)
        {
            return category switch
            {
                ItemCategory.Weapon => new Color(0.910f, 0.451f, 0.408f),
                ItemCategory.Tool => new Color(0.639f, 0.682f, 0.729f),
                ItemCategory.Clothing => new Color(0.490f, 0.580f, 0.918f),
                ItemCategory.Armor => new Color(0.710f, 0.545f, 0.886f),
                ItemCategory.Food => new Color(0.910f, 0.569f, 0.235f),
                ItemCategory.Water => new Color(0.278f, 0.714f, 0.902f),
                ItemCategory.Medicine => new Color(0.373f, 0.769f, 0.416f),
                ItemCategory.Resource => new Color(0.780f, 0.690f, 0.529f),
                _ => TextMute
            };
        }

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

        // §76: one row of the character sheet — an innate attribute or a
        // learned trade. Deliberately thinner than NeedConfig: a sheet row has
        // no pressure/direct polarity (more is always better) and no icon (the
        // 16 VectorIcon glyphs are all need-semantic — a droplet for Wits would
        // read worse than no glyph at all; dedicated icons are a polish pass).
        private struct SheetConfig
        {
            public string Id;    // matches AttributeKind / SkillKind
            public string Key;   // I2 term
            public Color Color;
        }

        private struct SheetBinding
        {
            public SheetConfig Config;
            public Label Label;
            public Label Value;
            public VisualElement Fill;
        }

        // §76.1 — the six innate characteristics. Order matches AttributeSet.All
        // so the sheet reads the same way the simulation does.
        private static readonly SheetConfig[] AttributeRows =
        {
            new() { Id = "Strength", Key = "attr.strength", Color = Health },
            new() { Id = "Agility", Key = "attr.agility", Color = Thirst },
            new() { Id = "Endurance", Key = "attr.endurance", Color = Energy },
            new() { Id = "Toughness", Key = "attr.toughness", Color = Gold },
            new() { Id = "Hardiness", Key = "attr.hardiness", Color = Hunger },
            new() { Id = "Wits", Key = "attr.wits", Color = Social },
        };

        // §76.5 — the eight learned trades, in SkillSet.All order.
        private static readonly SheetConfig[] SkillRows =
        {
            new() { Id = "Combat", Key = "skill.combat", Color = Health },
            new() { Id = "Harvesting", Key = "skill.harvesting", Color = Energy },
            new() { Id = "Crafting", Key = "skill.crafting", Color = Gold },
            new() { Id = "Building", Key = "skill.building", Color = Hunger },
            new() { Id = "Cooking", Key = "skill.cooking", Color = Thermal },
            new() { Id = "Medicine", Key = "skill.medicine", Color = Good },
            new() { Id = "Survival", Key = "skill.survival", Color = Comfort },
            new() { Id = "Social", Key = "skill.social", Color = Social },
        };

        // §76: which page of the middle column is showing. Needs is the default
        // because it is what the player checks every few seconds; the sheet is
        // reference material she consults once per colonist.
        private enum SheetTab { Needs, Nature, Skills }

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
            // Spec §53: compassion (high = at peace). Appended last so it doesn't
            // shift the i==4 thermal special-case cell in the row-wrap layout.
            new() { Key = "need.compassion", Icon = VectorIcon.Kind.HeartFill, Color = Compassion, Pressure = false },
            // §71: breath — the sprint reserve, spent running and refilled at a
            // walk. Distinct from Stamina on purpose (that one feeds the Sit
            // bid; this one only governs gait). High = good, like stamina.
            new() { Key = "need.breath", Icon = VectorIcon.Kind.Energy, Color = Thirst, Pressure = false },
        };

        public void SetRunner(SimulationRunnerBehaviour runner) => _runner = runner;

        public void SetPortraitStage(PortraitStage stage) => _portraitStage = stage;

        public void SetHealthDollStage(HealthDollStage stage) => _healthDollStage = stage;

        // §80: кэш снятых лиц. Необязателен — без него отношения рисуются
        // прежними цветными кружками с буквой.
        public void SetPortraitCache(NpcPortraitCache cache) => _portraitCache = cache;

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

            var overBar = PointerOverElement(_stage, mousePos, scale);

            // The floating windows (inventory / limb health) sit ABOVE the bar —
            // their bounds must also swallow clicks, or picking inside them
            // deselects the NPC.
            NpcSelection.PointerOverUi = overBar ||
                PointerOverFloating(_inventoryOpen, _inventoryWindow, mousePos, scale) ||
                PointerOverFloating(_healthOpen, _healthWindow, mousePos, scale);
        }

        private static bool PointerOverElement(VisualElement element, Vector2 mousePos, float scale)
        {
            if (element == null)
            {
                return false;
            }

            var wb = element.worldBound;
            if (float.IsNaN(wb.x) || wb.width < 1f || wb.height < 1f)
            {
                return false;
            }

            var left = wb.xMin * scale;
            var right = wb.xMax * scale;
            var top = Screen.height - wb.yMin * scale;
            var bottom = Screen.height - wb.yMax * scale;
            return mousePos.x >= left && mousePos.x <= right &&
                   mousePos.y >= bottom && mousePos.y <= top;
        }

        // Is the cursor within an open floating window? worldBound is in panel
        // units with a top-left origin; convert to bottom-left screen pixels.
        private static bool PointerOverFloating(bool open, VisualElement window, Vector2 mousePos, float scale)
        {
            if (!open || window == null)
            {
                return false;
            }

            var wb = window.worldBound;
            if (float.IsNaN(wb.x) || wb.width < 1f)
            {
                return false;
            }

            var left = wb.xMin * scale;
            var right = wb.xMax * scale;
            var top = Screen.height - wb.yMin * scale;     // higher on screen
            var bottom = Screen.height - wb.yMax * scale;   // lower on screen
            return mousePos.x >= left && mousePos.x <= right &&
                   mousePos.y >= bottom && mousePos.y <= top;
        }

        private void OnSelectionChanged(int npcId)
        {
            _shown = npcId >= 0;
            CloseInventory(); // a new/cleared selection resets the backpack
            CloseHealth();    // …and the limb-health window
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
                NpcSelection.BottomUiCoverage = 0f;
                return;
            }

            var eased = 1f - Mathf.Pow(1f - _anim, 3f); // easeOutCubic
            var height = _stage.layout.height > 1f ? _stage.layout.height + 40f : 320f;
            _stage.style.translate = new Translate(0f, (1f - eased) * height);

            // Publish how much of the screen the bar covers (slide-in scales it)
            // so the orbit camera can re-center the NPC in the visible strip.
            var rootHeight = _root.layout.height;
            NpcSelection.BottomUiCoverage = rootHeight > 1f
                ? Mathf.Clamp01(_stage.layout.height * eased / rootHeight)
                : 0f;
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

            // §74: DisplayName is a name ID; the player sees the localized term.
            _nameLabel.text = string.IsNullOrEmpty(npc.DisplayName)
                ? $"NPC #{npc.Id.Value}"
                : Loc.NpcName(npc.DisplayName);
            _roleLabel.text = $"{Loc.Get("panel.role")} · #{npc.Id.Value}";
            _thoughtValue.text = Loc.Goal(npc.CurrentGoal);

            // Spec §64: the dream pill — show her aspiration, hide it when she has
            // nothing left to dream of ("None"/empty).
            var hasDream = !string.IsNullOrEmpty(npc.CurrentDream) && npc.CurrentDream != "None";
            _dream.style.display = hasDream ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasDream)
            {
                _dreamValue.text = Loc.Dream(npc.CurrentDream);
            }

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
            UpdateUv(npc);

            var showBadge = npc.IsStarving || npc.IsFighting;
            _starvingBadge.style.display = showBadge ? DisplayStyle.Flex : DisplayStyle.None;
            _starvingBadge.text = npc.IsFighting ? Loc.Get("badge.fighting") : Loc.Get("badge.starving");

            UpdateNeeds(npc);
            UpdateSheet(npc);
            UpdateEffects(npc);
            UpdateRelations(npc);
            RefreshInventory(npc);
            RefreshHealth(npc);
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

        // UV: none (night/indoor/water) → low → medium → high; a shaded NPC
        // gets the "(in shade)" suffix and the icon dims with the level.
        private void UpdateUv(NpcSnapshot npc)
        {
            if (_uvLabel == null)
            {
                return;
            }

            var uv = npc.EffectiveUv;
            string key;
            Color color;
            if (uv < 0.02f)
            {
                key = "uv.none";
                color = TextMute;
            }
            else if (uv < 0.3f)
            {
                key = "uv.low";
                color = Good;
            }
            else if (uv < 0.6f)
            {
                key = "uv.mid";
                color = Warn;
            }
            else
            {
                key = "uv.high";
                color = Crit;
            }

            var text = Loc.Get(key);
            if (npc.IsShaded && uv >= 0.02f)
            {
                text += $" · {Loc.Get("uv.shade")}";
            }

            _uvLabel.text = text;
            _uvLabel.style.color = color;
            _uvIcon.SetColor(uv < 0.02f ? TextMute : Gold);
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

        // §76: the character sheet. Both pages are refreshed whichever tab is
        // showing — they are 14 label writes on an already-parsed snapshot, and
        // keeping them in sync means switching tabs is instant instead of
        // showing a stale frame.
        private void UpdateSheet(NpcSnapshot npc)
        {
            ApplySheet(_attrBindings, npc.Attributes);
            ApplySheet(_skillBindings, npc.Skills);
            UpdatePerks(npc);
        }

        // The snapshot carries "Strength\t0.62" rows (the §48 Effects idiom).
        // Matched by id rather than by index so reordering either enum — or
        // appending a seventh attribute — cannot silently shift every bar by one.
        private static void ApplySheet(List<SheetBinding> bindings, List<string> rows)
        {
            for (var i = 0; i < bindings.Count; i++)
            {
                var b = bindings[i];
                var value = 0f;
                for (var r = 0; r < rows.Count; r++)
                {
                    var tab = rows[r].IndexOf('\t');
                    if (tab <= 0 ||
                        string.CompareOrdinal(rows[r], 0, b.Config.Id, 0, tab) != 0 ||
                        tab != b.Config.Id.Length)
                    {
                        continue;
                    }

                    float.TryParse(rows[r].Substring(tab + 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value);
                    break;
                }

                value = Mathf.Clamp01(value);
                b.Fill.style.width = Length.Percent(value * 100f);
                // Levels out of ten, not percent: the sheet is an RPG reading
                // ("she is a 7 at crafting"), the needs grid is a gauge.
                b.Value.text = $"{Mathf.RoundToInt(value * 10f)}/10";
                b.Value.style.color = value >= 0.7f ? Gold : TextDim;
            }
        }

        // §76.6: the badges an extreme attribute earns. The sim hands over
        // finished term keys — the bands are its knobs, not the view's — so
        // this only localizes and lays out. Rebuilt only when the SET changes,
        // like the effect chips: perks are permanent, and re-creating twelve
        // elements every tick would be pure churn.
        private void UpdatePerks(NpcSnapshot npc)
        {
            var signature = string.Join("|", npc.Perks);
            if (signature == _perkSig)
            {
                return;
            }

            _perkSig = signature;
            _perkRow.Clear();
            foreach (var key in npc.Perks)
            {
                var badge = new Label(Loc.Get(key));
                badge.style.fontSize = 10;
                // A gift reads gold, a flaw reads muted — the key's own suffix
                // is the only thing that distinguishes them.
                badge.style.color = key.EndsWith(".high") ? Gold : TextMute;
                badge.style.backgroundColor = Raised;
                badge.style.paddingLeft = 8f;
                badge.style.paddingRight = 8f;
                badge.style.paddingTop = 3f;
                badge.style.paddingBottom = 3f;
                badge.style.marginRight = 6f;
                badge.style.marginTop = 3f;
                SetRadius(badge, 8f);
                SetBorder(badge, key.EndsWith(".high") ? GoldDim : Stroke, 1f);
                _perkRow.Add(badge);
            }
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

        // ── status effects (spec §48) ─────────────────────────────────────

        private void BuildEffectsRow()
        {
            _effectsRow = new VisualElement();
            _effectsRow.style.flexDirection = FlexDirection.Row;
            _effectsRow.style.flexWrap = Wrap.Wrap;
            _effectsRow.style.alignSelf = Align.FlexStart;
            _effectsRow.style.alignItems = Align.Center;
            _effectsRow.style.marginLeft = 20f;
            _effectsRow.style.marginBottom = 7f;
            _effectsRow.style.maxWidth = Length.Percent(70);
            _effectsRow.style.display = DisplayStyle.None;
            _stage.Add(_effectsRow);
        }

        // One shared tooltip card — it always pops in the SAME spot (top-left,
        // just above the chip row), so it never chases a chip off-screen. Any
        // chip hovered fills it with that effect's icon, title and description.
        private void BuildEffectTooltip()
        {
            _effectTooltip = new VisualElement();
            _effectTooltip.style.position = Position.Absolute;
            _effectTooltip.style.width = 340f;
            _effectTooltip.style.backgroundColor = Panel;
            SetBorder(_effectTooltip, StrokeStrong, 1f);
            SetRadius(_effectTooltip, 12f);
            _effectTooltip.style.paddingLeft = 16f;
            _effectTooltip.style.paddingRight = 16f;
            _effectTooltip.style.paddingTop = 13f;
            _effectTooltip.style.paddingBottom = 13f;
            _effectTooltip.pickingMode = PickingMode.Ignore;
            _effectTooltip.style.display = DisplayStyle.None;

            // Header: big emoji + bold title (title tinted by polarity).
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 7f;

            _effectTooltipIcon = new Label();
            _effectTooltipIcon.style.fontSize = 26;
            _effectTooltipIcon.style.marginRight = 11f;
            _effectTooltipIcon.style.flexShrink = 0f;
            _effectTooltipIcon.style.unityTextAlign = TextAnchor.MiddleCenter;
            header.Add(_effectTooltipIcon);

            _effectTooltipTitle = new Label();
            _effectTooltipTitle.style.color = Text;
            _effectTooltipTitle.style.fontSize = 17;
            _effectTooltipTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _effectTooltipTitle.style.flexGrow = 1f;
            header.Add(_effectTooltipTitle);
            _effectTooltip.Add(header);

            _effectTooltipDesc = new Label();
            _effectTooltipDesc.style.color = TextDim;
            _effectTooltipDesc.style.fontSize = 13;
            _effectTooltipDesc.style.whiteSpace = WhiteSpace.Normal;
            _effectTooltip.Add(_effectTooltipDesc);

            _root.Add(_effectTooltip);
        }

        private struct EffectView
        {
            public EffectKind Kind;
            public float Intensity;
        }

        // Rebuild the chip row from the snapshot's "Kind\tintensity" list —
        // debuffs first, each sorted worst (most intense) to the left. The row
        // is rebuilt only when the ordered set changes (keeps hover stable);
        // otherwise only the ring colours refresh.
        private void UpdateEffects(NpcSnapshot npc)
        {
            var parsed = new List<EffectView>(npc.Effects.Count);
            foreach (var raw in npc.Effects)
            {
                var tab = raw.IndexOf('\t');
                var kindStr = tab >= 0 ? raw.Substring(0, tab) : raw;
                if (!Enum.TryParse<EffectKind>(kindStr, out var kind) ||
                    !EffectCatalog.TryGet(kind, out _))
                {
                    continue;
                }

                var intensity = 0f;
                if (tab >= 0)
                {
                    float.TryParse(
                        raw.Substring(tab + 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out intensity);
                }

                parsed.Add(new EffectView { Kind = kind, Intensity = intensity });
            }

            parsed.Sort((a, b) =>
            {
                var pa = EffectCatalog.Get(a.Kind).Polarity == EffectPolarity.Debuff ? 0 : 1;
                var pb = EffectCatalog.Get(b.Kind).Polarity == EffectPolarity.Debuff ? 0 : 1;
                if (pa != pb)
                {
                    return pa - pb;
                }

                return b.Intensity.CompareTo(a.Intensity);
            });

            var sig = string.Empty;
            for (var i = 0; i < parsed.Count; i++)
            {
                sig += (int)parsed[i].Kind + ",";
            }

            if (sig != _effectSig)
            {
                _effectSig = sig;
                HideEffectTooltip();
                _effectsRow.Clear();
                foreach (var e in parsed)
                {
                    _effectsRow.Add(BuildEffectChip(e.Kind, e.Intensity));
                }
            }
            else
            {
                for (var i = 0; i < _effectsRow.childCount && i < parsed.Count; i++)
                {
                    var def = EffectCatalog.Get(parsed[i].Kind);
                    SetBorderColor(_effectsRow[i], EffectRing(def.Polarity, parsed[i].Intensity));
                }
            }

            _effectsRow.style.display = parsed.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private VisualElement BuildEffectChip(EffectKind kind, float intensity)
        {
            var def = EffectCatalog.Get(kind);

            var chip = new VisualElement();
            chip.style.width = 60f;
            chip.style.height = 60f;
            chip.style.flexShrink = 0f;
            chip.style.marginRight = 11f;
            chip.style.marginBottom = 7f;
            chip.style.alignItems = Align.Center;
            chip.style.justifyContent = Justify.Center;
            chip.style.backgroundColor = Raised;
            SetRadius(chip, 30f);
            SetBorder(chip, EffectRing(def.Polarity, intensity), 3f);

            // Emoji placeholder (spec §48.3) — the polarity ring keeps the chip
            // readable even if the runtime font can't render the glyph.
            var glyph = new Label(def.Emoji);
            glyph.style.fontSize = 28;
            glyph.style.color = Text;
            glyph.pickingMode = PickingMode.Ignore;
            glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
            chip.Add(glyph);

            chip.RegisterCallback<MouseEnterEvent>(_ => ShowEffectTooltip(kind));
            chip.RegisterCallback<MouseLeaveEvent>(_ => HideEffectTooltip());

            return chip;
        }

        private static Color EffectRing(EffectPolarity polarity, float intensity)
        {
            // Buffs read green; debuffs shade amber (mild) → red (severe).
            return polarity == EffectPolarity.Buff
                ? Good
                : Color.Lerp(Warn, Crit, Mathf.Clamp01(intensity));
        }

        private void ShowEffectTooltip(EffectKind kind)
        {
            if (_effectTooltip == null || !EffectCatalog.TryGet(kind, out var def))
            {
                return;
            }

            _effectTooltipIcon.text = def.Emoji;
            _effectTooltipTitle.text = Loc.Get(def.TitleKey);
            _effectTooltipTitle.style.color = def.Polarity == EffectPolarity.Buff
                ? Good
                : new Color(0.949f, 0.769f, 0.753f); // soft red for debuffs
            _effectTooltipDesc.text = Loc.Get(def.DescKey);

            // Always the SAME spot: pinned to the row's left edge and lifted
            // fully above it (percentage translate is of the tooltip's own
            // height, so no measuring needed). The row's left sits ~20px from
            // the screen edge, so the card can never run off-screen.
            var rb = _effectsRow.worldBound;
            if (!float.IsNaN(rb.x))
            {
                _effectTooltip.style.left = rb.x;
                _effectTooltip.style.top = rb.y - 10f;
                _effectTooltip.style.translate = new Translate(0f, Length.Percent(-100));
            }

            _effectTooltip.style.display = DisplayStyle.Flex;
        }

        private void HideEffectTooltip()
        {
            if (_effectTooltip != null)
            {
                _effectTooltip.style.display = DisplayStyle.None;
            }
        }

        // ── inventory / backpack (spec §51) ───────────────────────────────

        // The floating window. Two stacked sub-views (list + detail); only one
        // is visible at a time. Anchored to the identity column, floating just
        // above the card so it reads as "this character's things".
        private void BuildInventoryWindow()
        {
            _inventoryWindow = new VisualElement();
            _inventoryWindow.style.position = Position.Absolute;
            _inventoryWindow.style.left = 20f;
            _inventoryWindow.style.bottom = InventoryWindowBottom;
            _inventoryWindow.style.width = 440f;
            _inventoryWindow.style.maxHeight = 470f;
            _inventoryWindow.style.backgroundColor = Panel;
            SetBorder(_inventoryWindow, StrokeStrong, 1f);
            SetRadius(_inventoryWindow, 14f);
            _inventoryWindow.style.paddingLeft = 16f;
            _inventoryWindow.style.paddingRight = 16f;
            _inventoryWindow.style.paddingTop = 13f;
            _inventoryWindow.style.paddingBottom = 14f;
            _inventoryWindow.style.display = DisplayStyle.None;
            _inventoryWindow.pickingMode = PickingMode.Position;

            // Header: 📦 title + capacity + close (×).
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 11f;

            var headGlyph = new Label("🎒");
            headGlyph.style.fontSize = 18;
            headGlyph.style.marginRight = 9f;
            headGlyph.pickingMode = PickingMode.Ignore;
            header.Add(headGlyph);

            _inventoryTitle = new Label(Loc.Get("panel.inventory"));
            _inventoryTitle.style.color = Text;
            _inventoryTitle.style.fontSize = 17;
            _inventoryTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(_inventoryTitle);

            _inventoryCapacity = new Label();
            _inventoryCapacity.style.color = TextMute;
            _inventoryCapacity.style.fontSize = 12;
            _inventoryCapacity.style.flexGrow = 1f;
            _inventoryCapacity.style.marginLeft = 9f;
            header.Add(_inventoryCapacity);

            var close = new Label("✕");
            close.style.color = TextDim;
            close.style.fontSize = 15;
            close.style.unityFontStyleAndWeight = FontStyle.Bold;
            close.style.width = 24f;
            close.style.height = 24f;
            close.style.unityTextAlign = TextAnchor.MiddleCenter;
            close.style.flexShrink = 0f;
            SetRadius(close, 6f);
            close.RegisterCallback<MouseEnterEvent>(_ => close.style.color = Text);
            close.RegisterCallback<MouseLeaveEvent>(_ => close.style.color = TextDim);
            close.RegisterCallback<MouseDownEvent>(evt =>
            {
                CloseInventory();
                evt.StopPropagation();
            });
            header.Add(close);
            _inventoryWindow.Add(header);

            // ── list view ──────────────────────────────────────────────
            _invListView = new VisualElement();
            var listScroll = new ScrollView(ScrollViewMode.Vertical);
            listScroll.style.maxHeight = 400f;
            _invListBody = listScroll.contentContainer;
            _invListView.Add(listScroll);
            _inventoryWindow.Add(_invListView);

            // ── detail view (hidden until an item is clicked) ──────────
            _invDetailView = new VisualElement();
            _invDetailView.style.display = DisplayStyle.None;
            BuildInventoryDetail(_invDetailView);
            _inventoryWindow.Add(_invDetailView);

            _root.Add(_inventoryWindow);
        }

        private void BuildInventoryDetail(VisualElement parent)
        {
            // Back to the list.
            var back = new VisualElement();
            back.style.flexDirection = FlexDirection.Row;
            back.style.alignItems = Align.Center;
            back.style.alignSelf = Align.FlexStart;
            back.style.marginBottom = 12f;
            back.RegisterCallback<MouseDownEvent>(evt =>
            {
                ShowItemList();
                evt.StopPropagation();
            });
            var backArrow = new Label("‹");
            backArrow.style.color = Gold;
            backArrow.style.fontSize = 20;
            backArrow.style.marginRight = 6f;
            backArrow.pickingMode = PickingMode.Ignore;
            back.Add(backArrow);
            _invBackLabel = new Label(Loc.Get("inv.back"));
            _invBackLabel.style.color = Gold;
            _invBackLabel.style.fontSize = 13;
            _invBackLabel.pickingMode = PickingMode.Ignore;
            back.Add(_invBackLabel);
            parent.Add(back);

            // Hero row: big glyph tile + name/category.
            var hero = new VisualElement();
            hero.style.flexDirection = FlexDirection.Row;
            hero.style.alignItems = Align.Center;
            hero.style.marginBottom = 13f;

            var tile = new VisualElement();
            tile.style.width = 72f;
            tile.style.height = 72f;
            tile.style.flexShrink = 0f;
            tile.style.marginRight = 15f;
            tile.style.alignItems = Align.Center;
            tile.style.justifyContent = Justify.Center;
            tile.style.backgroundColor = Raised;
            SetBorder(tile, StrokeStrong, 1.5f);
            SetRadius(tile, 12f);
            _invDetailEmoji = new Label();
            _invDetailEmoji.style.fontSize = 36;
            _invDetailEmoji.pickingMode = PickingMode.Ignore;
            _invDetailEmoji.style.unityTextAlign = TextAnchor.MiddleCenter;
            tile.Add(_invDetailEmoji);
            _invDetailIcon = new Image();
            _invDetailIcon.scaleMode = ScaleMode.ScaleToFit;
            _invDetailIcon.style.width = 60f;
            _invDetailIcon.style.height = 60f;
            _invDetailIcon.pickingMode = PickingMode.Ignore;
            _invDetailIcon.style.display = DisplayStyle.None;
            tile.Add(_invDetailIcon);
            hero.Add(tile);

            var heroText = new VisualElement();
            heroText.style.flexGrow = 1f;
            heroText.style.flexShrink = 1f;
            _invDetailName = new Label();
            _invDetailName.style.color = Text;
            _invDetailName.style.fontSize = 19;
            _invDetailName.style.unityFontStyleAndWeight = FontStyle.Bold;
            _invDetailName.style.whiteSpace = WhiteSpace.Normal;
            heroText.Add(_invDetailName);
            _invDetailCategory = new Label();
            _invDetailCategory.style.fontSize = 12;
            _invDetailCategory.style.unityFontStyleAndWeight = FontStyle.Bold;
            _invDetailCategory.style.letterSpacing = 1f;
            _invDetailCategory.style.marginTop = 4f;
            heroText.Add(_invDetailCategory);
            hero.Add(heroText);
            parent.Add(hero);

            _invDetailDesc = new Label();
            _invDetailDesc.style.color = TextDim;
            _invDetailDesc.style.fontSize = 13.5f;
            _invDetailDesc.style.whiteSpace = WhiteSpace.Normal;
            _invDetailDesc.style.marginBottom = 12f;
            parent.Add(_invDetailDesc);

            _invDetailStats = new VisualElement();
            parent.Add(_invDetailStats);
        }

        private void ToggleInventory()
        {
            if (_inventoryOpen)
            {
                CloseInventory();
            }
            else
            {
                CloseHealth(); // the two floating windows share the same spot
                _inventoryOpen = true;
                _invSig = null; // force a rebuild on the next refresh
                _inventoryWindow.style.display = DisplayStyle.Flex;
                ShowItemList();
                _refreshedTick = -1; // pull a fresh snapshot into the window now
            }
        }

        private void CloseInventory()
        {
            _inventoryOpen = false;
            _invSelectedId = null;
            if (_inventoryWindow != null)
            {
                _inventoryWindow.style.display = DisplayStyle.None;
            }
        }

        // ── limb health window (spec §57) ─────────────────────────────────

        // Floating window over the identity column: the rotating body doll
        // (per-zone green→yellow→red mesh from HealthDollStage) beside a
        // per-limb readout list. Same chrome as the inventory window.
        private void BuildHealthWindow()
        {
            _healthWindow = new VisualElement();
            _healthWindow.style.position = Position.Absolute;
            _healthWindow.style.left = 20f;
            _healthWindow.style.bottom = InventoryWindowBottom;
            _healthWindow.style.width = 470f;
            _healthWindow.style.backgroundColor = Panel;
            SetBorder(_healthWindow, StrokeStrong, 1f);
            SetRadius(_healthWindow, 14f);
            _healthWindow.style.paddingLeft = 16f;
            _healthWindow.style.paddingRight = 16f;
            _healthWindow.style.paddingTop = 13f;
            _healthWindow.style.paddingBottom = 14f;
            _healthWindow.style.display = DisplayStyle.None;
            _healthWindow.pickingMode = PickingMode.Position;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 11f;

            var headIcon = new VectorIcon(VectorIcon.Kind.Health, Health);
            headIcon.style.width = 17f;
            headIcon.style.height = 17f;
            headIcon.style.marginRight = 9f;
            headIcon.style.flexShrink = 0f;
            header.Add(headIcon);

            _healthTitle = new Label(Loc.Get("panel.health"));
            _healthTitle.style.color = Text;
            _healthTitle.style.fontSize = 17;
            _healthTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _healthTitle.style.flexGrow = 1f;
            header.Add(_healthTitle);

            var close = new Label("✕");
            close.style.color = TextDim;
            close.style.fontSize = 15;
            close.style.unityFontStyleAndWeight = FontStyle.Bold;
            close.style.width = 24f;
            close.style.height = 24f;
            close.style.unityTextAlign = TextAnchor.MiddleCenter;
            close.style.flexShrink = 0f;
            SetRadius(close, 6f);
            close.RegisterCallback<MouseEnterEvent>(_ => close.style.color = Text);
            close.RegisterCallback<MouseLeaveEvent>(_ => close.style.color = TextDim);
            close.RegisterCallback<MouseDownEvent>(evt =>
            {
                CloseHealth();
                evt.StopPropagation();
            });
            header.Add(close);
            _healthWindow.Add(header);

            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;

            // Doll viewport (3:4, matches the stage RenderTexture aspect).
            _healthDollImage = new VisualElement();
            _healthDollImage.style.width = 195f;
            _healthDollImage.style.height = 260f;
            _healthDollImage.style.flexShrink = 0f;
            _healthDollImage.style.backgroundColor = Track;
            SetBorder(_healthDollImage, StrokeStrong, 1f);
            SetRadius(_healthDollImage, 10f);
            _healthDollImage.style.overflow = Overflow.Hidden;
            body.Add(_healthDollImage);

            // Per-limb readout rows.
            var list = new VisualElement();
            list.style.flexGrow = 1f;
            list.style.marginLeft = 15f;
            list.style.justifyContent = Justify.Center;

            _zoneRows.Clear();
            foreach (var zone in HealthDollStage.ZoneOrder)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 8f;

                var dot = new VisualElement();
                dot.style.width = 9f;
                dot.style.height = 9f;
                dot.style.flexShrink = 0f;
                SetRadius(dot, 4.5f);
                dot.style.marginRight = 9f;
                row.Add(dot);

                var name = new Label();
                name.style.color = Text;
                name.style.fontSize = 13.5f;
                name.style.flexGrow = 1f;
                row.Add(name);

                // Worn-armor absorption (hidden while the zone is bare).
                var armor = new Label();
                armor.style.color = Comfort;
                armor.style.fontSize = 12f;
                armor.style.flexShrink = 0f;
                armor.style.marginRight = 8f;
                armor.style.display = DisplayStyle.None;
                row.Add(armor);

                var value = new Label();
                value.style.color = TextDim;
                value.style.fontSize = 12.5f;
                value.style.flexShrink = 0f;
                row.Add(value);

                list.Add(row);
                _zoneRows.Add(new ZoneRowBinding
                {
                    Zone = zone, Dot = dot, Name = name, Armor = armor, Value = value
                });
            }

            body.Add(list);
            _healthWindow.Add(body);
            _root.Add(_healthWindow);
        }

        private void ToggleHealth()
        {
            if (_healthOpen)
            {
                CloseHealth();
                return;
            }

            CloseInventory(); // the two floating windows share the same spot
            _healthOpen = true;
            _healthWindow.style.display = DisplayStyle.Flex;
            _healthDollStage?.SetActive(true);
            _refreshedTick = -1; // pull a fresh snapshot into the window now
        }

        private void CloseHealth()
        {
            _healthOpen = false;
            if (_healthWindow != null)
            {
                _healthWindow.style.display = DisplayStyle.None;
            }

            _healthDollStage?.SetActive(false);
        }

        // Called from Refresh() while the window is open: feed the doll stage
        // and rebuild the readout rows off the snapshot's zone lists.
        private void RefreshHealth(NpcSnapshot npc)
        {
            if (!_healthOpen)
            {
                return;
            }

            if (_healthDollStage != null)
            {
                _healthDollStage.SetTarget(npc.ActorMesh);
                _healthDollStage.SetZones(npc.BodyParts, npc.SeveredParts, npc.BandagedZones);
                var tex = _healthDollStage.Texture;
                if (tex != null)
                {
                    _healthDollImage.style.backgroundImage =
                        new StyleBackground(Background.FromRenderTexture(tex));
                }
            }

            foreach (var binding in _zoneRows)
            {
                var hp = 1f;
                foreach (var entry in npc.BodyParts)
                {
                    if (entry.StartsWith(binding.Zone) &&
                        entry.Length > binding.Zone.Length && entry[binding.Zone.Length] == '=')
                    {
                        float.TryParse(entry[(binding.Zone.Length + 1)..],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out hp);
                        break;
                    }
                }

                // Worn-armor absorption for this zone ("Zone=0.35").
                var armor = 0f;
                foreach (var entry in npc.PartArmor)
                {
                    if (entry.StartsWith(binding.Zone) &&
                        entry.Length > binding.Zone.Length && entry[binding.Zone.Length] == '=')
                    {
                        float.TryParse(entry[(binding.Zone.Length + 1)..],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out armor);
                        break;
                    }
                }

                var severed = npc.SeveredParts.Contains(binding.Zone);
                var bandaged = false;
                foreach (var entry in npc.BandagedZones)
                {
                    var bar = entry.IndexOf('|');
                    if ((bar > 0 ? entry[..bar] : entry) == binding.Zone)
                    {
                        bandaged = true;
                        break;
                    }
                }

                var openWounds = 0;
                foreach (var entry in npc.Wounds)
                {
                    // "Zone|Seed|Heal01" — count wounds still visibly open.
                    var parts = entry.Split('|');
                    if (parts.Length >= 3 && parts[0] == binding.Zone &&
                        float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var heal) &&
                        heal < 0.999f)
                    {
                        openWounds++;
                    }
                }

                binding.Name.text = Loc.Get($"zone.{binding.Zone}");
                binding.Dot.style.backgroundColor = HealthDollStage.StatusColor(hp, severed);

                // 🛡 only when something actually covers the zone; a severed
                // limb has nothing left to protect.
                var showArmor = !severed && armor > 0.005f;
                binding.Armor.style.display = showArmor ? DisplayStyle.Flex : DisplayStyle.None;
                if (showArmor)
                {
                    binding.Armor.text = $"🛡 {Mathf.RoundToInt(armor * 100f)}%";
                }

                if (severed)
                {
                    binding.Value.text = Loc.Get("health.severed");
                    binding.Value.style.color = Crit;
                }
                else
                {
                    var text = $"{Mathf.RoundToInt(Mathf.Clamp01(hp) * 100f)}%";
                    if (bandaged)
                    {
                        text = $"🩹 {text}";
                    }

                    if (openWounds > 0)
                    {
                        text = $"🩸{openWounds} · {text}";
                    }

                    binding.Value.text = text;
                    binding.Value.style.color = openWounds > 0 || hp < 0.5f ? Text : TextDim;
                }
            }
        }

        private void ShowItemList()
        {
            _invSelectedId = null;
            if (_invListView != null)
            {
                _invListView.style.display = DisplayStyle.Flex;
            }

            if (_invDetailView != null)
            {
                _invDetailView.style.display = DisplayStyle.None;
            }
        }

        // Called from Refresh() while the window is open. Rebuilds the list only
        // when the item set or a garment's live condition changes.
        private void RefreshInventory(NpcSnapshot npc)
        {
            if (!_inventoryOpen)
            {
                return;
            }

            var capacity = npc.InventoryCapacity > 0 ? npc.InventoryCapacity : 10;
            _inventoryCapacity.text = $"{npc.InventoryUsedSlots}/{capacity} {Loc.Get("inv.slots")}";

            var wornDurability = ParseKv(npc.WornDurability);
            var carriedDurability = ParseKv(npc.InventoryDurability);
            var carriedWater = ParseWaterKv(npc.InventoryWater);
            var carriedStacks = ParseIntKv(npc.InventoryStacks);
            var wornWetness = ParseKv(npc.WornWetness);
            var carriedWetness = ParseKv(npc.InventoryWetness);
            var wornDirtiness = ParseKv(npc.WornDirtiness);
            var carriedDirtiness = ParseKv(npc.InventoryDirtiness);
            // The dirt BAR shows total contamination: dirt + blood, clamped
            // to full. The stains themselves stay separate layers visually.
            MergeContamination(wornDirtiness, ParseKv(npc.WornBloodiness));
            MergeContamination(carriedDirtiness, ParseKv(npc.InventoryBloodiness));

            var sig = string.Join(",", npc.WornItems) + "|" + string.Join(",", npc.InventoryItems)
                + "|" + string.Join(",", npc.WornWetness) + "|" + string.Join(",", npc.WornDurability)
                + "|" + string.Join(",", npc.WornDirtiness)
                + "|" + string.Join(",", npc.WornBloodiness)
                + "|" + string.Join(",", npc.InventoryDurability)
                + "|" + string.Join(",", npc.InventoryWetness)
                + "|" + string.Join(",", npc.InventoryDirtiness)
                + "|" + string.Join(",", npc.InventoryBloodiness)
                + "|" + string.Join(",", npc.InventoryWater)
                + "|" + string.Join(",", npc.InventoryStacks) + "|" + npc.InventoryUsedSlots;
            if (sig == _invSig)
            {
                return;
            }

            _invSig = sig;
            RebuildItemList(npc, wornDurability, carriedDurability, carriedWater, carriedStacks,
                wornWetness, carriedWetness, wornDirtiness, carriedDirtiness);

            // Keep the detail view coherent: if the shown item is still present,
            // re-render it (its wetness/durability may have moved); else drop back.
            if (_invSelectedId != null)
            {
                var present = (_invSelectedWorn ? npc.WornItems : npc.InventoryItems)
                    .Contains(_invSelectedId);
                if (present)
                {
                    var selectedDurability = _invSelectedWorn ? wornDurability : carriedDurability;
                    var selectedWetness = _invSelectedWorn ? wornWetness : carriedWetness;
                    var selectedDirtiness = _invSelectedWorn ? wornDirtiness : carriedDirtiness;
                    ShowItemDetail(_invSelectedId, _invSelectedWorn, selectedDurability,
                        carriedWater, carriedStacks, selectedWetness, selectedDirtiness);
                }
                else
                {
                    ShowItemList();
                }
            }
        }

        private void RebuildItemList(
            NpcSnapshot npc,
            Dictionary<string, float> wornDurability,
            Dictionary<string, float> carriedDurability,
            Dictionary<string, WaterContainerState> carriedWater,
            Dictionary<string, int> carriedStacks,
            Dictionary<string, float> wornWetness,
            Dictionary<string, float> carriedWetness,
            Dictionary<string, float> wornDirtiness,
            Dictionary<string, float> carriedDirtiness)
        {
            _invListBody.Clear();

            var any = false;
            if (npc.WornItems.Count > 0)
            {
                _invListBody.Add(MakeInvSectionHeader(Loc.Get("inv.worn")));
                foreach (var id in npc.WornItems)
                {
                    _invListBody.Add(BuildItemRow(id, true, wornDurability, carriedWater,
                        carriedStacks, wornWetness, wornDirtiness));
                }

                any = true;
            }

            if (npc.InventoryItems.Count > 0)
            {
                _invListBody.Add(MakeInvSectionHeader(Loc.Get("inv.carried")));
                foreach (var id in npc.InventoryItems)
                {
                    _invListBody.Add(BuildItemRow(id, false, carriedDurability, carriedWater,
                        carriedStacks, carriedWetness, carriedDirtiness));
                }

                any = true;
            }

            if (!any)
            {
                var empty = new Label(Loc.Get("inv.empty"));
                empty.style.color = TextMute;
                empty.style.fontSize = 13;
                empty.style.marginTop = 6f;
                empty.style.marginBottom = 6f;
                _invListBody.Add(empty);
            }
        }

        // Real image icon for an item, if one exists at
        // Resources/HexLive/UI/Items/<id>. Returns null so callers fall back
        // to the emoji glyph when no bespoke icon has been added.
        private static Sprite LoadItemIcon(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            return Resources.Load<Sprite>($"HexLive/UI/Items/{id}");
        }

        private VisualElement BuildItemRow(
            string id,
            bool worn,
            Dictionary<string, float> durability,
            Dictionary<string, WaterContainerState> water,
            Dictionary<string, int> stacks,
            Dictionary<string, float> wetness,
            Dictionary<string, float> dirtiness)
        {
            var def = ResolveDef(id);
            var info = ResolveItemInfo(id, def);
            var isWaterContainer = !worn && IsWaterContainerId(id);
            var stackCount = !worn && stacks.TryGetValue(id, out var count) ? count : 1;
            var accent = isWaterContainer ? CategoryColor(ItemCategory.Water) : CategoryColor(info.Category);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = Raised;
            SetBorder(row, Stroke, 1f);
            SetRadius(row, 9f);
            row.style.paddingLeft = 9f;
            row.style.paddingRight = 12f;
            row.style.paddingTop = 8f;
            row.style.paddingBottom = 8f;
            row.style.marginBottom = 7f;

            // Glyph tile.
            var tile = new VisualElement();
            tile.style.width = 40f;
            tile.style.height = 40f;
            tile.style.flexShrink = 0f;
            tile.style.marginRight = 11f;
            tile.style.alignItems = Align.Center;
            tile.style.justifyContent = Justify.Center;
            tile.style.backgroundColor = Panel;
            SetRadius(tile, 8f);
            var icon = LoadItemIcon(id);
            if (icon != null)
            {
                var iconImage = new Image();
                iconImage.sprite = icon;
                iconImage.scaleMode = ScaleMode.ScaleToFit;
                iconImage.style.width = 34f;
                iconImage.style.height = 34f;
                iconImage.pickingMode = PickingMode.Ignore;
                tile.Add(iconImage);
            }
            else
            {
                var glyph = new Label(info.Emoji);
                glyph.style.fontSize = 22;
                glyph.pickingMode = PickingMode.Ignore;
                glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
                tile.Add(glyph);
            }
            row.Add(tile);

            var mid = new VisualElement();
            mid.style.flexGrow = 1f;
            mid.style.flexShrink = 1f;
            var name = new Label(ItemName(def, info));
            name.style.color = Text;
            name.style.fontSize = 14;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            mid.Add(name);
            var cat = new Label(ItemCategoryName(def, info, isWaterContainer));
            cat.style.color = accent;
            cat.style.fontSize = 10.5f;
            cat.style.unityFontStyleAndWeight = FontStyle.Bold;
            cat.style.letterSpacing = 0.6f;
            cat.style.marginTop = 2f;
            mid.Add(cat);
            row.Add(mid);

            if (stackCount > 1)
            {
                var countLabel = new Label($"x{stackCount}");
                countLabel.style.color = Text;
                countLabel.style.fontSize = 12f;
                countLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                countLabel.style.backgroundColor = Panel;
                countLabel.style.paddingLeft = 7f;
                countLabel.style.paddingRight = 7f;
                countLabel.style.paddingTop = 3f;
                countLabel.style.paddingBottom = 3f;
                countLabel.style.marginRight = 10f;
                countLabel.style.flexShrink = 0f;
                SetBorder(countLabel, new Color(accent.r, accent.g, accent.b, 0.35f), 1f);
                SetRadius(countLabel, 8f);
                row.Add(countLabel);
            }

            // A little coloured category pip on the right.
            var pip = new VisualElement();
            pip.style.width = 8f;
            pip.style.height = 8f;
            pip.style.flexShrink = 0f;
            SetRadius(pip, 4f);
            pip.style.backgroundColor = accent;
            row.Add(pip);

            row.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(row, GoldDim));
            row.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(row, Stroke));
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                ShowItemDetail(id, worn, durability, water, stacks, wetness, dirtiness);
                evt.StopPropagation();
            });

            return row;
        }

        private void ShowItemDetail(
            string id,
            bool worn,
            Dictionary<string, float> durability,
            Dictionary<string, WaterContainerState> water,
            Dictionary<string, int> stacks,
            Dictionary<string, float> wetness,
            Dictionary<string, float> dirtiness)
        {
            _invSelectedId = id;
            _invSelectedWorn = worn;

            var def = ResolveDef(id);
            var info = ResolveItemInfo(id, def);
            var isWaterContainer = !worn && IsWaterContainerId(id);
            var accent = isWaterContainer ? CategoryColor(ItemCategory.Water) : CategoryColor(info.Category);

            var detailIcon = LoadItemIcon(id);
            if (detailIcon != null)
            {
                _invDetailIcon.sprite = detailIcon;
                _invDetailIcon.style.display = DisplayStyle.Flex;
                _invDetailEmoji.style.display = DisplayStyle.None;
            }
            else
            {
                _invDetailIcon.style.display = DisplayStyle.None;
                _invDetailEmoji.style.display = DisplayStyle.Flex;
                _invDetailEmoji.text = info.Emoji;
            }
            _invDetailName.text = ItemName(def, info);
            _invDetailCategory.text = ItemCategoryName(def, info, isWaterContainer).ToUpperInvariant();
            _invDetailCategory.style.color = accent;
            _invDetailDesc.text = ItemDesc(def, info);

            BuildItemStats(def, info, worn, durability, water, stacks, wetness, dirtiness);

            _invListView.style.display = DisplayStyle.None;
            _invDetailView.style.display = DisplayStyle.Flex;
        }

        // Derived stat lines: warmth/armor/coverage/layer for apparel, hunger
        // restore for food, plus live per-instance clothing state.
        private void BuildItemStats(
            ObjectDefinition def,
            ItemInfo info,
            bool worn,
            Dictionary<string, float> durability,
            Dictionary<string, WaterContainerState> water,
            Dictionary<string, int> stacks,
            Dictionary<string, float> wetness,
            Dictionary<string, float> dirtiness)
        {
            _invDetailStats.Clear();
            if (!worn && stacks.TryGetValue(info.DefinitionId, out var stackCount) && stackCount > 1)
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.stack"), $"x{stackCount}", CategoryColor(ItemCategory.Resource)));
            }

            if (!worn && water.TryGetValue(info.DefinitionId, out var waterState))
            {
                _invDetailStats.Add(MakeWaterContainerBlock(info.DefinitionId, waterState));
            }

            if ((def == null || def.Layer.HasValue) &&
                durability.TryGetValue(info.DefinitionId, out var itemDurability))
            {
                _invDetailStats.Add(MakeClothingHpBlock(
                    itemDurability,
                    def != null && def.Layer.HasValue ? "inv.clothing_hp" : "inv.durability"));
            }

            if (def != null && def.Layer.HasValue &&
                wetness.TryGetValue(info.DefinitionId, out var wet))
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.wetness"), $"{Mathf.RoundToInt(wet * 100f)}%",
                    Color.Lerp(TextDim, Thirst, Mathf.Clamp01(wet))));
            }

            if (def != null && def.Layer.HasValue &&
                dirtiness.TryGetValue(info.DefinitionId, out var dirt))
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.dirtiness"), $"{Mathf.RoundToInt(dirt * 100f)}%",
                    Color.Lerp(TextDim, Hunger, Mathf.Clamp01(dirt))));
            }

            if (def == null)
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.definition_id"), MissingItemId(info.DefinitionId), TextDim));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.status"), Loc.Get("inv.missing_definition"), Warn));
                return;
            }

            // Aggregate the interaction effects that matter for a summary.
            float warmth = 0f, armor = 0f, hunger = 0f;
            foreach (var interaction in def.Interactions)
            {
                warmth += interaction.Effects.WarmthDelta;
                armor += interaction.Effects.ArmorDelta;
                hunger += interaction.Effects.HungerDelta;
            }

            if (def.Layer.HasValue)
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.layer"), Loc.Get("layer." + def.Layer.Value.ToString().ToLowerInvariant()), TextDim));
            }

            if (def.Covers.Count > 0)
            {
                var parts = new List<string>();
                foreach (var part in def.Covers)
                {
                    parts.Add(Loc.Get("part." + part.ToString().ToLowerInvariant()));
                }

                _invDetailStats.Add(MakeStatRow(Loc.Get("inv.covers"), string.Join(", ", parts), TextDim));
            }

            if (warmth > 0.001f)
            {
                // Spec 42: EquippedWarmth × 10 ≈ °C, so a per-garment delta reads
                // roughly as this many degrees of insulation.
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.warmth"), $"+{warmth * 10f:0.0}°C", CategoryColor(ItemCategory.Water)));
            }

            if (armor > 0.001f)
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.armor"), $"+{Mathf.RoundToInt(armor * 100f)}%", CategoryColor(ItemCategory.Armor)));
            }

            if (hunger < -0.001f)
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.restores"), $"+{Mathf.RoundToInt(-hunger * 100f)}%", CategoryColor(ItemCategory.Food)));
            }

        }

        private VisualElement MakeWaterContainerBlock(string id, WaterContainerState state)
        {
            var capacity = Mathf.Max(0.001f, state.Capacity);
            var amount = Mathf.Clamp(state.Amount, 0f, capacity);
            var value = Mathf.Clamp01(amount / capacity);
            var pct = Mathf.RoundToInt(value * 100f);
            // Display-only volume: the sim counts gulps, the panel shows litres.
            var volumeCapacity = DisplayCapacityLiters(id, capacity);
            var volumeAmount = volumeCapacity * value;
            var millilitres = volumeCapacity < 1f;
            var color = CategoryColor(ItemCategory.Water);

            var block = new VisualElement();
            block.style.backgroundColor = PanelMid;
            block.style.marginTop = 4f;
            block.style.marginBottom = 8f;
            block.style.paddingLeft = 10f;
            block.style.paddingRight = 10f;
            block.style.paddingTop = 9f;
            block.style.paddingBottom = 10f;
            SetBorder(block, new Color(color.r, color.g, color.b, 0.34f), 1f);
            SetRadius(block, 8f);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 8f;

            var label = new Label(Loc.Get("inv.water_left"));
            label.style.color = Text;
            label.style.fontSize = 12.5f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(label);

            var unit = Loc.Get(millilitres ? "inv.milliliters" : "inv.liters");
            var valueLabel = new Label(
                $"{FormatVolume(volumeAmount, millilitres)} / {FormatVolume(volumeCapacity, millilitres)} {unit} - {pct}%");
            valueLabel.style.color = color;
            valueLabel.style.fontSize = 12.5f;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.flexShrink = 0f;
            valueLabel.style.marginLeft = 10f;
            header.Add(valueLabel);
            block.Add(header);

            var track = MakeTrack(12f);
            track.style.backgroundColor = new Color(0.035f, 0.046f, 0.054f);

            var fill = MakeFill(color);
            fill.style.width = Length.Percent(value * 100f);
            fill.style.minWidth = value > 0f ? 4f : 0f;
            track.Add(fill);

            block.Add(track);
            return block;
        }

        private VisualElement MakeClothingHpBlock(float durability, string labelKey = "inv.clothing_hp")
        {
            var value = Mathf.Clamp01(durability);
            var pct = Mathf.RoundToInt(value * 100f);
            var color = DurabilityColor(value);

            var block = new VisualElement();
            block.style.backgroundColor = PanelMid;
            block.style.marginTop = 4f;
            block.style.marginBottom = 8f;
            block.style.paddingLeft = 10f;
            block.style.paddingRight = 10f;
            block.style.paddingTop = 9f;
            block.style.paddingBottom = 10f;
            SetBorder(block, new Color(color.r, color.g, color.b, 0.34f), 1f);
            SetRadius(block, 8f);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 8f;

            var label = new Label(Loc.Get(labelKey));
            label.style.color = Text;
            label.style.fontSize = 12.5f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(label);

            var valueLabel = new Label($"{pct}% - {DurabilityCondition(value)}");
            valueLabel.style.color = color;
            valueLabel.style.fontSize = 12.5f;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.flexShrink = 0f;
            valueLabel.style.marginLeft = 10f;
            header.Add(valueLabel);
            block.Add(header);

            var track = MakeTrack(12f);
            track.style.backgroundColor = new Color(0.035f, 0.046f, 0.054f);

            var fill = MakeFill(color);
            fill.style.width = Length.Percent(value * 100f);
            fill.style.minWidth = value > 0f ? 4f : 0f;

            var shine = new VisualElement();
            shine.style.position = Position.Absolute;
            shine.style.left = 1f;
            shine.style.right = 1f;
            shine.style.top = 1f;
            shine.style.height = 3f;
            shine.style.backgroundColor = new Color(1f, 1f, 1f, 0.22f);
            SetRadius(shine, 2f);
            fill.Add(shine);
            track.Add(fill);

            for (var i = 1; i < 4; i++)
            {
                var tick = new VisualElement();
                tick.style.position = Position.Absolute;
                tick.style.top = 2f;
                tick.style.bottom = 2f;
                tick.style.left = Length.Percent(i * 25f);
                tick.style.width = 1f;
                tick.style.backgroundColor = new Color(1f, 1f, 1f, 0.16f);
                track.Add(tick);
            }

            block.Add(track);
            return block;
        }

        private VisualElement MakeStatRow(string label, string value, Color valueColor)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.paddingTop = 6f;
            row.style.paddingBottom = 6f;
            row.style.borderTopWidth = 1f;
            SetBorderColor(row, Stroke);
            row.style.borderBottomWidth = 0f;
            row.style.borderLeftWidth = 0f;
            row.style.borderRightWidth = 0f;

            var l = new Label(label);
            l.style.color = TextMute;
            l.style.fontSize = 12.5f;
            row.Add(l);

            var v = new Label(value);
            v.style.color = valueColor;
            v.style.fontSize = 12.5f;
            v.style.unityFontStyleAndWeight = FontStyle.Bold;
            v.style.flexShrink = 0f;
            v.style.marginLeft = 12f;
            row.Add(v);
            return row;
        }

        private static Label MakeInvSectionHeader(string text)
        {
            var label = new Label(text.ToUpperInvariant());
            label.style.color = TextMute;
            label.style.fontSize = 10.5f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 1.4f;
            label.style.marginTop = 4f;
            label.style.marginBottom = 8f;
            return label;
        }

        private ObjectDefinition ResolveDef(string id)
        {
            var world = _runner != null ? _runner.Engine?.World : null;
            if (world != null && world.Content.ObjectDefinitions.TryGetValue(id, out var def))
            {
                return def;
            }

            return null;
        }

        private static string ItemName(ObjectDefinition def, ItemInfo info)
        {
            if (Loc.Has(info.NameKey))
            {
                return Loc.Get(info.NameKey);
            }

            if (def != null && !string.IsNullOrEmpty(def.DisplayName))
            {
                return def.DisplayName;
            }

            return Loc.Get("item.unknown.name");
        }

        private static ItemInfo ResolveItemInfo(string id, ObjectDefinition def)
        {
            if (def != null)
            {
                return ItemCatalog.Resolve(def);
            }

            var info = ItemCatalog.Resolve(id);
            return Loc.Has(info.NameKey)
                ? info
                : new ItemInfo(id, ItemCategory.Misc, ItemCatalog.CategoryEmoji(ItemCategory.Misc));
        }

        private static string ItemCategoryName(ObjectDefinition def, ItemInfo info, bool isWaterContainer)
        {
            if (isWaterContainer)
            {
                return Loc.Get("inv.water_container");
            }

            return def == null && !Loc.Has(info.NameKey)
                ? Loc.Get("item.unknown.category")
                : Loc.Get(info.CategoryNameKey);
        }

        private static string ItemDesc(ObjectDefinition def, ItemInfo info)
        {
            if (Loc.Has(info.DescKey))
            {
                return Loc.Get(info.DescKey);
            }

            if (def == null)
            {
                return string.Format(Loc.Get("item.unknown.desc"), MissingItemId(info.DefinitionId));
            }

            return Loc.Get(info.CategoryDescKey);
        }

        private static string MissingItemId(string id) =>
            string.IsNullOrWhiteSpace(id) ? Loc.Get("panel.none") : id;

        private static bool IsWaterContainerId(string id) =>
            ItemCatalog.IsWaterContainerId(id);

        // Parse the "definitionId\tvalue" pairs the exporter packs (spec 40.11).
        // Dirt bar = dirt + blood summed per item, clamped to a full bar (the
        // logical sum may exceed 1 — a fully bloody AND dusty rag stays 100%).
        private static void MergeContamination(Dictionary<string, float> dirt,
            Dictionary<string, float> blood)
        {
            foreach (var pair in blood)
            {
                dirt[pair.Key] = Mathf.Min(1f,
                    (dirt.TryGetValue(pair.Key, out var value) ? value : 0f) + pair.Value);
            }
        }

        private static Dictionary<string, float> ParseKv(List<string> pairs)
        {
            var map = new Dictionary<string, float>();
            foreach (var raw in pairs)
            {
                var tab = raw.IndexOf('\t');
                if (tab < 0)
                {
                    continue;
                }

                var key = raw.Substring(0, tab);
                if (float.TryParse(
                        raw.Substring(tab + 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                {
                    map[key] = value;
                }
            }

            return map;
        }

        // Parse "definitionId\tcount" entries for inventory stacks.
        private static Dictionary<string, int> ParseIntKv(List<string> pairs)
        {
            var map = new Dictionary<string, int>();
            foreach (var raw in pairs)
            {
                var tab = raw.IndexOf('\t');
                if (tab < 0)
                {
                    continue;
                }

                var key = raw.Substring(0, tab);
                if (int.TryParse(
                        raw.Substring(tab + 1),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                {
                    map[key] = value;
                }
            }

            return map;
        }

        // Parse "definitionId\tamountLiters\tcapacityLiters" entries for
        // portable water containers (bottle and pierced coconut).
        private static Dictionary<string, WaterContainerState> ParseWaterKv(List<string> pairs)
        {
            var map = new Dictionary<string, WaterContainerState>();
            foreach (var raw in pairs)
            {
                var parts = raw.Split('\t');
                if (parts.Length < 3)
                {
                    continue;
                }

                if (float.TryParse(
                        parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var amount) &&
                    float.TryParse(
                        parts[2],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var capacity))
                {
                    map[parts[0]] = new WaterContainerState
                    {
                        Amount = amount,
                        Capacity = capacity
                    };
                }
            }

            return map;
        }

        // Spec §52: water volume on screen is COSMETIC. The sim counts gulps
        // (BottleCapacity / CoconutWaterCapacity); the panel turns that into a
        // believable volume — one gulp reads as 0.1 L, so a 4-gulp pierced
        // coconut is 400 ml. Named containers may pin their own volume: the
        // bottle is a round 1 L regardless of how many gulps it holds.
        private const float DisplayLitersPerGulp = 0.1f;

        private static float DisplayCapacityLiters(string id, float gulpCapacity)
        {
            return id == "tool.bottle" ? 1f : gulpCapacity * DisplayLitersPerGulp;
        }

        private static string FormatVolume(float liters, bool millilitres)
        {
            if (millilitres)
            {
                // Round to the nearest 10 ml — gulp fractions shouldn't read
                // as fake precision ("266 ml").
                var ml = Mathf.Round(liters * 100f) * 10f;
                return ml.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            }

            var rounded = Mathf.Round(liters);
            return Mathf.Abs(liters - rounded) < 0.05f
                ? rounded.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                : liters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
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
                "need.compassion" => npc.Compassion,
                "need.breath" => npc.Breath, // §71
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

            var relations = new List<RelationshipSnapshot>(npc.RelationshipDetails);
            relations.Sort((a, b) =>
            {
                var byAffinity = Mathf.Abs(b.Affinity).CompareTo(Mathf.Abs(a.Affinity));
                return byAffinity != 0
                    ? byAffinity
                    : string.Compare(a.OtherName, b.OtherName, StringComparison.OrdinalIgnoreCase);
            });

            var selected = relations.Find(r => r.OtherId == _selectedRelationId);
            if (selected == null)
            {
                selected = relations[0];
                _selectedRelationId = selected.OtherId;
            }

            _relationsContainer.Add(BuildRelationTabs(relations, selected.OtherId, npc));
            _relationsContainer.Add(BuildRelationFocusCard(selected));
        }

        private VisualElement BuildRelationTabs(List<RelationshipSnapshot> relations, int selectedId, NpcSnapshot npc)
        {
            var tabs = new VisualElement();
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.alignItems = Align.Center;
            tabs.style.height = 48f;
            tabs.style.flexShrink = 0f;
            tabs.style.marginBottom = 8f;
            tabs.style.backgroundColor = new Color(0.054f, 0.069f, 0.080f, 0.72f);
            SetBorder(tabs, Stroke, 1f);
            SetRadius(tabs, 10f);
            tabs.style.overflow = Overflow.Hidden;

            var maxTabs = Mathf.Min(relations.Count, 5);
            for (var i = 0; i < maxTabs; i++)
            {
                tabs.Add(BuildRelationTab(relations[i], relations[i].OtherId == selectedId, npc));
            }

            if (relations.Count > maxTabs)
            {
                var more = new Label($"+{relations.Count - maxTabs}");
                more.style.color = TextDim;
                more.style.fontSize = 12;
                more.style.unityFontStyleAndWeight = FontStyle.Bold;
                more.style.unityTextAlign = TextAnchor.MiddleCenter;
                more.style.width = 34f;
                more.style.flexShrink = 0f;
                tabs.Add(more);
            }

            var plusWrap = new VisualElement();
            plusWrap.style.width = 52f;
            plusWrap.style.height = Length.Percent(100);
            plusWrap.style.flexShrink = 0f;
            plusWrap.style.alignItems = Align.Center;
            plusWrap.style.justifyContent = Justify.Center;
            plusWrap.style.backgroundColor = new Color(1f, 1f, 1f, 0.035f);
            SetBorder(plusWrap, new Color(1f, 1f, 1f, 0.06f), 1f);

            var plus = new Label("+");
            plus.style.color = TextDim;
            plus.style.fontSize = 24;
            plus.style.unityFontStyleAndWeight = FontStyle.Bold;
            plus.style.unityTextAlign = TextAnchor.MiddleCenter;
            plus.style.width = 32f;
            plus.style.height = 32f;
            SetRadius(plus, 16f);
            SetBorder(plus, StrokeStrong, 1f);
            plusWrap.Add(plus);
            tabs.Add(plusWrap);

            return tabs;
        }

        private VisualElement BuildRelationTab(RelationshipSnapshot rel, bool selected, NpcSnapshot npc)
        {
            var tab = new VisualElement();
            tab.style.flexDirection = FlexDirection.Row;
            tab.style.alignItems = Align.Center;
            tab.style.height = Length.Percent(100);
            tab.style.flexGrow = 1f;
            tab.style.flexShrink = 1f;
            tab.style.minWidth = 76f;
            tab.style.paddingLeft = 9f;
            tab.style.paddingRight = 9f;
            tab.style.backgroundColor = selected
                ? new Color(1f, 1f, 1f, 0.035f)
                : new Color(0f, 0f, 0f, 0f);

            var avatar = new VisualElement();
            avatar.style.width = selected ? 35f : 31f;
            avatar.style.height = selected ? 35f : 31f;
            avatar.style.flexShrink = 0f;
            SetRadius(avatar, selected ? 17.5f : 15.5f);
            avatar.style.backgroundColor = AvatarColor(rel.OtherId);
            SetBorder(avatar, selected ? RelationColor(rel.Affinity) : StrokeStrong, selected ? 2f : 1f);
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;

            var initial = new Label(InitialOf(rel.OtherName));
            initial.style.color = new Color(0.06f, 0.086f, 0.102f);
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.fontSize = 13;
            initial.style.unityTextAlign = TextAnchor.MiddleCenter;
            avatar.Add(initial);
            ApplyRelationFace(avatar, rel.OtherId, initial);
            tab.Add(avatar);

            var name = new Label(rel.OtherName);
            name.style.color = selected ? Text : TextDim;
            name.style.fontSize = selected ? 13 : 12;
            name.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
            name.style.marginLeft = 8f;
            name.style.flexShrink = 1f;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            tab.Add(name);

            if (selected)
            {
                var pointer = new VisualElement();
                pointer.style.position = Position.Absolute;
                pointer.style.bottom = 0f;
                pointer.style.left = Length.Percent(50);
                pointer.style.translate = new Translate(Length.Percent(-50), 0f);
                pointer.style.width = 24f;
                pointer.style.height = 3f;
                pointer.style.backgroundColor = RelationColor(rel.Affinity);
                SetRadius(pointer, 2f);
                tab.Add(pointer);
            }

            tab.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(avatar, GoldDim));
            tab.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(avatar, selected ? RelationColor(rel.Affinity) : StrokeStrong));
            tab.RegisterCallback<MouseDownEvent>(evt =>
            {
                _selectedRelationId = rel.OtherId;
                _refreshedTick = -1;
                UpdateRelations(npc);
                evt.StopPropagation();
            });

            return tab;
        }

        private VisualElement BuildRelationFocusCard(RelationshipSnapshot rel)
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.flexGrow = 1f;
            card.style.minHeight = 0f;
            card.style.overflow = Overflow.Hidden;
            card.style.backgroundColor = new Color(0.071f, 0.091f, 0.106f, 0.86f);
            SetBorder(card, new Color(0.941f, 0.706f, 0.361f, 0.42f), 1f);
            SetRadius(card, 10f);
            card.style.paddingLeft = 12f;
            card.style.paddingRight = 14f;
            card.style.paddingTop = 12f;
            card.style.paddingBottom = 12f;

            var left = new VisualElement();
            left.style.width = 104f;
            left.style.flexShrink = 0f;
            left.style.alignItems = Align.Center;
            left.style.justifyContent = Justify.Center;

            var portrait = new VisualElement();
            portrait.style.width = 74f;
            portrait.style.height = 74f;
            SetRadius(portrait, 37f);
            portrait.style.backgroundColor = AvatarColor(rel.OtherId);
            SetBorder(portrait, Gold, 2f);
            portrait.style.alignItems = Align.Center;
            portrait.style.justifyContent = Justify.Center;

            var initial = new Label(InitialOf(rel.OtherName));
            initial.style.color = new Color(0.06f, 0.086f, 0.102f);
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.fontSize = 30;
            initial.style.unityTextAlign = TextAnchor.MiddleCenter;
            portrait.Add(initial);
            ApplyRelationFace(portrait, rel.OtherId, initial);
            left.Add(portrait);

            var mood = new Label(RelationMoodGlyph(rel.Affinity));
            mood.style.width = 24f;
            mood.style.height = 24f;
            mood.style.marginTop = -18f;
            mood.style.marginLeft = 50f;
            mood.style.fontSize = 15;
            mood.style.unityTextAlign = TextAnchor.MiddleCenter;
            mood.style.backgroundColor = PanelMid;
            SetRadius(mood, 14.5f);
            SetBorder(mood, Gold, 2f);
            left.Add(mood);

            var relationState = new Label($"{RelationKind(rel.Affinity)}\n{RelationTag(rel.Affinity)}");
            relationState.style.color = TextDim;
            relationState.style.fontSize = 10;
            relationState.style.unityFontStyleAndWeight = FontStyle.Bold;
            relationState.style.unityTextAlign = TextAnchor.MiddleCenter;
            relationState.style.marginTop = 4f;
            relationState.style.whiteSpace = WhiteSpace.Normal;
            left.Add(relationState);

            card.Add(left);

            var body = new VisualElement();
            body.style.flexGrow = 1f;
            body.style.minWidth = 0f;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.FlexStart;
            top.style.marginBottom = 6f;

            var title = new VisualElement();
            title.style.flexGrow = 1f;
            title.style.minWidth = 0f;

            var name = new Label(rel.OtherName);
            name.style.color = Text;
            name.style.fontSize = 21;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            title.Add(name);

            top.Add(title);

            var scoreBox = new VisualElement();
            scoreBox.style.flexDirection = FlexDirection.Row;
            scoreBox.style.alignItems = Align.Center;
            scoreBox.style.flexShrink = 0f;
            scoreBox.style.marginLeft = 10f;

            var heart = new VectorIcon(VectorIcon.Kind.HeartFill, RelationColor(rel.Affinity));
            heart.style.width = 24f;
            heart.style.height = 24f;
            heart.style.marginRight = 6f;
            scoreBox.Add(heart);

            var score = new Label(RelationScore(rel.Affinity));
            score.style.color = RelationColor(rel.Affinity);
            score.style.fontSize = 17;
            score.style.unityFontStyleAndWeight = FontStyle.Bold;
            scoreBox.Add(score);
            top.Add(scoreBox);
            body.Add(top);

            body.Add(BuildRelationRings(rel));

            card.Add(body);

            return card;
        }

        private static VisualElement BuildSocialTag(string text, Color color)
        {
            var tag = new Label(text);
            tag.style.color = TextDim;
            tag.style.fontSize = 10;
            tag.style.unityFontStyleAndWeight = FontStyle.Bold;
            tag.style.backgroundColor = new Color(color.r, color.g, color.b, 0.13f);
            SetBorder(tag, new Color(color.r, color.g, color.b, 0.26f), 1f);
            SetRadius(tag, 999f);
            tag.style.paddingLeft = 8f;
            tag.style.paddingRight = 8f;
            tag.style.paddingTop = 3f;
            tag.style.paddingBottom = 3f;
            tag.style.marginRight = 6f;
            tag.style.marginBottom = 5f;
            return tag;
        }

        private static VisualElement BuildRelationRings(RelationshipSnapshot rel)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceAround;
            row.style.flexGrow = 1f;
            row.style.marginTop = 0f;
            row.style.paddingLeft = 2f;
            row.style.paddingRight = 8f;

            row.Add(BuildRelationRingMetric(
                Loc.Get("rel.affinity"),
                rel.Affinity,
                RelationColor(rel.Affinity),
                VectorIcon.Kind.HeartFill,
                true));
            row.Add(BuildRelationRingMetric(
                Loc.Get("rel.familiarity"),
                rel.Familiarity,
                Social,
                VectorIcon.Kind.Social,
                false));
            row.Add(BuildRelationRingMetric(
                Loc.Get("rel.trust"),
                rel.Trust,
                Good,
                VectorIcon.Kind.Shield,
                false));

            return row;
        }

        private static VisualElement BuildRelationRingMetric(
            string labelText,
            float value,
            Color color,
            VectorIcon.Kind iconKind,
            bool signed)
        {
            var metric = new VisualElement();
            metric.style.flexDirection = FlexDirection.Column;
            metric.style.alignItems = Align.Center;
            metric.style.justifyContent = Justify.Center;
            metric.style.width = 104f;
            metric.style.flexShrink = 0f;

            var pct = signed ? Mathf.RoundToInt(value * 100f) : Mathf.RoundToInt(Mathf.Clamp01(value) * 100f);
            var text = signed && pct > 0 ? $"+{pct}%" : $"{pct}%";
            metric.tooltip = $"{labelText}: {text}";

            var ringWrap = new VisualElement();
            ringWrap.style.width = 72f;
            ringWrap.style.height = 72f;
            ringWrap.style.alignItems = Align.Center;
            ringWrap.style.justifyContent = Justify.Center;
            ringWrap.style.flexShrink = 0f;

            var ring = new RingMeter(signed ? Mathf.Abs(value) : value, color);
            ring.style.position = Position.Absolute;
            ring.style.left = 0f;
            ring.style.right = 0f;
            ring.style.top = 0f;
            ring.style.bottom = 0f;
            ringWrap.Add(ring);

            var icon = new VectorIcon(iconKind, color);
            icon.style.width = 28f;
            icon.style.height = 28f;
            ringWrap.Add(icon);
            metric.Add(ringWrap);

            var valueLabel = new Label(text);
            valueLabel.style.color = signed ? RelationColor(value) : color;
            valueLabel.style.fontSize = 13;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            valueLabel.style.marginTop = 5f;
            metric.Add(valueLabel);

            return metric;
        }

        private static VisualElement BuildSignalDots(RelationshipSnapshot rel)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexShrink = 0f;

            var values = new[]
            {
                rel.Familiarity,
                Mathf.Abs(rel.Affinity),
                rel.Trust,
                Mathf.Clamp01((rel.Familiarity + rel.Trust) * 0.5f),
                Mathf.Clamp01(0.25f + Mathf.Abs(rel.Affinity))
            };

            for (var i = 0; i < values.Length; i++)
            {
                var dot = new Label(SignalGlyph(i, rel.Affinity));
                dot.style.width = 28f;
                dot.style.height = 28f;
                dot.style.marginLeft = 6f;
                dot.style.unityTextAlign = TextAnchor.MiddleCenter;
                dot.style.fontSize = 13;
                dot.style.backgroundColor = new Color(1f, 1f, 1f, 0.045f);
                SetRadius(dot, 14f);
                SetBorder(dot, values[i] > 0.2f ? new Color(1f, 1f, 1f, 0.12f) : Stroke, 1f);
                dot.style.opacity = Mathf.Lerp(0.42f, 1f, Mathf.Clamp01(values[i]));
                row.Add(dot);
            }

            return row;
        }

        private VisualElement BuildRelationChip(RelationshipSnapshot rel)
        {
            var chip = new VisualElement();
            chip.style.backgroundColor = Raised;
            SetBorder(chip, Stroke, 1f);
            SetRadius(chip, 8f);
            chip.style.paddingLeft = 9f;
            chip.style.paddingRight = 9f;
            chip.style.paddingTop = 8f;
            chip.style.paddingBottom = 9f;
            chip.style.marginBottom = 8f;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.marginBottom = 7f;

            var av = new VisualElement();
            av.style.width = 28f;
            av.style.height = 28f;
            av.style.flexShrink = 0f;
            SetRadius(av, 14f);
            av.style.backgroundColor = AvatarColor(rel.OtherId);
            av.style.alignItems = Align.Center;
            av.style.justifyContent = Justify.Center;
            var initial = new Label(InitialOf(rel.OtherName));
            initial.style.color = new Color(0.06f, 0.086f, 0.102f);
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.fontSize = 13;
            av.Add(initial);
            ApplyRelationFace(av, rel.OtherId, initial);
            head.Add(av);

            var mid = new VisualElement();
            mid.style.flexGrow = 1f;
            mid.style.flexShrink = 1f;
            mid.style.marginLeft = 9f;
            var rn = new Label(rel.OtherName);
            rn.style.color = Text;
            rn.style.fontSize = 14;
            rn.style.unityFontStyleAndWeight = FontStyle.Bold;
            rn.style.whiteSpace = WhiteSpace.NoWrap;
            rn.style.overflow = Overflow.Hidden;
            rn.style.textOverflow = TextOverflow.Ellipsis;
            var detail = new VisualElement();
            detail.style.flexDirection = FlexDirection.Row;
            detail.style.alignItems = Align.Center;
            var rk = new Label(RelationKind(rel.Affinity));
            rk.style.color = TextMute;
            rk.style.fontSize = 10;
            rk.style.flexGrow = 1f;
            var score = new Label(RelationScore(rel.Affinity));
            score.style.color = RelationColor(rel.Affinity);
            score.style.fontSize = 10;
            score.style.unityFontStyleAndWeight = FontStyle.Bold;
            score.style.flexShrink = 0f;
            detail.Add(rk);
            detail.Add(score);
            mid.Add(rn);
            mid.Add(detail);
            head.Add(mid);

            var hearts = new VisualElement();
            hearts.style.flexDirection = FlexDirection.Row;
            hearts.style.flexShrink = 0f;
            hearts.style.marginLeft = 8f;
            var filled = HeartsFor(rel.Affinity);
            for (var i = 0; i < 4; i++)
            {
                var heart = new VectorIcon(VectorIcon.Kind.HeartFill,
                    i < filled ? Health : new Color(0.184f, 0.216f, 0.239f));
                heart.style.width = 12f;
                heart.style.height = 12f;
                heart.style.marginLeft = 2f;
                hearts.Add(heart);
            }
            head.Add(hearts);
            chip.Add(head);

            chip.Add(BuildRelationMetric(
                Loc.Get("rel.affinity"), rel.Affinity, RelationColor(rel.Affinity), true));
            chip.Add(BuildRelationMetric(
                Loc.Get("rel.familiarity"), rel.Familiarity, Social, false));
            chip.Add(BuildRelationMetric(
                Loc.Get("rel.trust"), rel.Trust, Good, false));

            var otherId = rel.OtherId;
            chip.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(chip, GoldDim));
            chip.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(chip, Stroke));
            chip.RegisterCallback<MouseDownEvent>(_ => NpcSelection.Select(otherId));

            return chip;
        }

        private static VisualElement BuildRelationMetric(string labelText, float value, Color color, bool signed)
        {
            var row = new VisualElement();
            row.style.marginTop = 4f;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 3f;

            var label = new Label(labelText);
            label.style.color = TextDim;
            label.style.fontSize = 9.5f;
            label.style.flexGrow = 1f;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            top.Add(label);

            var pct = signed ? Mathf.RoundToInt(value * 100f) : Mathf.RoundToInt(Mathf.Clamp01(value) * 100f);
            var valueLabel = new Label(signed && pct > 0 ? $"+{pct}%" : $"{pct}%");
            valueLabel.style.color = signed ? RelationColor(value) : color;
            valueLabel.style.fontSize = 9.5f;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.flexShrink = 0f;
            valueLabel.style.marginLeft = 7f;
            top.Add(valueLabel);
            row.Add(top);

            row.Add(signed ? BuildRelationMeter(value) : BuildPositiveMeter(value, color));
            return row;
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

            // Full-width bar, flush with the bottom edge of the screen
            // (PanelBottomOffset = 0).
            _stage = new VisualElement();
            _stage.style.flexDirection = FlexDirection.Column;
            _stage.style.alignItems = Align.Stretch;
            _stage.style.width = Length.Percent(100);
            _stage.style.marginBottom = PanelBottomOffset;
            _stage.pickingMode = PickingMode.Ignore;
            _root.Add(_stage);

            BuildThought();
            BuildDream();
            BuildEffectsRow();

            // Three-zone card; height fits the full needs grid including
            // Compassion, which can wrap onto its own row in RU/EN layouts.
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.width = Length.Percent(100);
            card.style.height = CharacterCardHeight;
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
            BuildLanguageButton();
            BuildExpandTab();
            BuildEffectTooltip();
            BuildInventoryWindow();
            BuildHealthWindow();
        }

        private void BuildLanguageButton()
        {
            _langButton = new Button(Loc.Toggle) { text = Loc.Code };
            _langButton.style.position = Position.Absolute;
            _langButton.style.top = 16f;
            _langButton.style.right = 18f;
            _langButton.style.width = 44f;
            _langButton.style.height = 28f;
            _langButton.style.backgroundColor = Panel;
            SetBorder(_langButton, StrokeStrong, 1f);
            SetRadius(_langButton, 8f);
            _langButton.style.color = Text;
            _langButton.style.fontSize = 11;
            _langButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _langButton.style.paddingLeft = 0f;
            _langButton.style.paddingRight = 0f;
            _langButton.style.paddingTop = 0f;
            _langButton.style.paddingBottom = 0f;
            _langButton.style.marginTop = 0f;
            _langButton.style.marginBottom = 0f;
            _langButton.style.marginLeft = 0f;
            _langButton.style.marginRight = 0f;
            _root.Add(_langButton);
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
                CloseInventory(); // don't leave the window floating over a hidden bar
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
            _thought.style.minWidth = 214f;
            _thought.style.maxWidth = 360f;
            _thought.style.minHeight = 46f;
            _thought.style.backgroundColor = PanelMid;
            SetBorder(_thought, StrokeStrong, 1f);
            SetRadius(_thought, 12f);
            _thought.style.paddingLeft = 13f;
            _thought.style.paddingRight = 16f;
            _thought.style.paddingTop = 9f;
            _thought.style.paddingBottom = 9f;

            var icon = new VectorIcon(VectorIcon.Kind.Think, Gold);
            icon.style.width = 19f;
            icon.style.height = 19f;
            icon.style.marginRight = 11f;
            icon.style.flexShrink = 0f;
            _thought.Add(icon);

            _thoughtValue = new Label();
            _thoughtValue.style.color = Text;
            _thoughtValue.style.fontSize = 16;
            _thoughtValue.style.unityFontStyleAndWeight = FontStyle.Bold;
            _thoughtValue.style.whiteSpace = WhiteSpace.Normal;
            _thoughtValue.style.flexShrink = 1f;

            _thought.Add(_thoughtValue);
            _stage.Add(_thought);
        }

        // Spec §64: the dream pill — what she aspires to (campfire → own bed).
        // Mirrors the thought pill; hidden when she has no dream left (None).
        private void BuildDream()
        {
            _dream = new VisualElement();
            _dream.style.flexDirection = FlexDirection.Row;
            _dream.style.alignItems = Align.Center;
            _dream.style.alignSelf = Align.FlexStart;
            _dream.style.marginLeft = 20f;
            _dream.style.marginBottom = 7f;
            _dream.style.minWidth = 214f;
            _dream.style.maxWidth = 360f;
            _dream.style.minHeight = 40f;
            _dream.style.backgroundColor = PanelMid;
            SetBorder(_dream, StrokeStrong, 1f);
            SetRadius(_dream, 12f);
            _dream.style.paddingLeft = 13f;
            _dream.style.paddingRight = 16f;
            _dream.style.paddingTop = 8f;
            _dream.style.paddingBottom = 8f;

            var icon = new VectorIcon(VectorIcon.Kind.Dream, new Color(0.62f, 0.74f, 1f));
            icon.style.width = 18f;
            icon.style.height = 18f;
            icon.style.marginRight = 11f;
            icon.style.flexShrink = 0f;
            _dream.Add(icon);

            _dreamValue = new Label();
            _dreamValue.style.color = Text;
            _dreamValue.style.fontSize = 15;
            _dreamValue.style.unityFontStyleAndWeight = FontStyle.Bold;
            _dreamValue.style.whiteSpace = WhiteSpace.Normal;
            _dreamValue.style.flexShrink = 1f;

            _dream.Add(_dreamValue);
            _stage.Add(_dream);
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

            // Spec §57: the HP row is a button — click pops the limb-health
            // window (body doll + per-limb readout).
            void HookHealthClick(VisualElement element)
            {
                element.RegisterCallback<MouseEnterEvent>(_ => hCap.style.color = Gold);
                element.RegisterCallback<MouseLeaveEvent>(_ => hCap.style.color = TextMute);
                element.RegisterCallback<MouseDownEvent>(evt =>
                {
                    ToggleHealth();
                    evt.StopPropagation();
                });
            }

            HookHealthClick(hRow);

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
            HookHealthClick(hTrack);

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

            // Sun exposure: how much UV hits her right now (shade-aware).
            var uvRow = new VisualElement();
            uvRow.style.flexDirection = FlexDirection.Row;
            uvRow.style.alignItems = Align.Center;
            uvRow.style.marginTop = 5f;
            _uvIcon = new VectorIcon(VectorIcon.Kind.Sun, Gold);
            _uvIcon.style.width = 12f;
            _uvIcon.style.height = 12f;
            _uvIcon.style.flexShrink = 0f;
            _uvIcon.style.marginRight = 6f;
            _uvLabel = new Label();
            _uvLabel.style.color = TextDim;
            _uvLabel.style.fontSize = 12;
            uvRow.Add(_uvIcon);
            uvRow.Add(_uvLabel);
            info.Add(uvRow);

            info.Add(BuildInventoryButton());

            col.Add(info);

            return col;
        }

        // Spec §51: "🎒 Backpack" pill under the identity block. Toggles the
        // floating inventory window.
        private VisualElement BuildInventoryButton()
        {
            var button = new VisualElement();
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.alignSelf = Align.FlexStart;
            button.style.marginTop = 10f;
            button.style.backgroundColor = Raised;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 8f);
            button.style.paddingLeft = 11f;
            button.style.paddingRight = 13f;
            button.style.paddingTop = 6f;
            button.style.paddingBottom = 6f;

            var glyph = new Label("🎒");
            glyph.style.fontSize = 16;
            glyph.style.marginRight = 8f;
            glyph.pickingMode = PickingMode.Ignore;
            glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.Add(glyph);

            _inventoryButtonLabel = new Label(Loc.Get("inv.button"));
            _inventoryButtonLabel.style.color = Text;
            _inventoryButtonLabel.style.fontSize = 13;
            _inventoryButtonLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _inventoryButtonLabel.pickingMode = PickingMode.Ignore;
            button.Add(_inventoryButtonLabel);

            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, GoldDim));
            button.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(button, StrokeStrong));
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleInventory();
                evt.StopPropagation();
            });

            _inventoryButton = button;
            return button;
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

            // §76: the section title becomes a three-page tab strip. Text-only
            // and ~24px tall — the relation tabs' 48px avatar strip is the
            // visual model, but it will not fit a 286px card twice over.
            var tabs = new VisualElement();
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.marginBottom = 10f;
            _needsTitle = BuildSheetTab(tabs, SheetTab.Needs);
            _natureTitle = BuildSheetTab(tabs, SheetTab.Nature);
            _skillsTitle = BuildSheetTab(tabs, SheetTab.Skills);
            col.Add(tabs);

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

            // §76: «Природа» — six innate characteristics, three across, plus
            // the perk badges the extremes earn.
            _natureContainer = new VisualElement();
            var attrGrid = new VisualElement();
            attrGrid.style.flexDirection = FlexDirection.Row;
            attrGrid.style.flexWrap = Wrap.Wrap;
            _attrBindings.Clear();
            foreach (var row in AttributeRows)
            {
                attrGrid.Add(BuildSheetCell(row, 33f, _attrBindings));
            }

            _natureContainer.Add(attrGrid);

            _perkRow = new VisualElement();
            _perkRow.style.flexDirection = FlexDirection.Row;
            _perkRow.style.flexWrap = Wrap.Wrap;
            _perkRow.style.marginTop = 6f;
            _natureContainer.Add(_perkRow);
            col.Add(_natureContainer);

            // §76: «Умения» — eight learned trades, four across.
            _skillsContainer = new VisualElement();
            _skillsContainer.style.flexDirection = FlexDirection.Row;
            _skillsContainer.style.flexWrap = Wrap.Wrap;
            _skillBindings.Clear();
            foreach (var row in SkillRows)
            {
                _skillsContainer.Add(BuildSheetCell(row, 25f, _skillBindings));
            }

            col.Add(_skillsContainer);

            SelectSheetTab(SheetTab.Needs);
            return col;
        }

        // One tab of the §76 strip. Selection is a plain field and the strip is
        // restyled wholesale on click — the same shape as the relation tabs,
        // minus their avatars.
        private Label BuildSheetTab(VisualElement strip, SheetTab tab)
        {
            var label = MakeSectionTitle("");
            label.style.marginBottom = 0f;
            label.style.marginRight = 16f;
            label.RegisterCallback<MouseEnterEvent>(_ =>
            {
                if (_sheetTab != tab) label.style.color = TextDim;
            });
            label.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (_sheetTab != tab) label.style.color = TextMute;
            });
            label.RegisterCallback<MouseDownEvent>(evt =>
            {
                SelectSheetTab(tab);
                evt.StopPropagation();
            });
            strip.Add(label);
            return label;
        }

        private void SelectSheetTab(SheetTab tab)
        {
            _sheetTab = tab;
            _needsContainer.style.display = tab == SheetTab.Needs ? DisplayStyle.Flex : DisplayStyle.None;
            _natureContainer.style.display = tab == SheetTab.Nature ? DisplayStyle.Flex : DisplayStyle.None;
            _skillsContainer.style.display = tab == SheetTab.Skills ? DisplayStyle.Flex : DisplayStyle.None;

            _needsTitle.style.color = tab == SheetTab.Needs ? Gold : TextMute;
            _natureTitle.style.color = tab == SheetTab.Nature ? Gold : TextMute;
            _skillsTitle.style.color = tab == SheetTab.Skills ? Gold : TextMute;

            // Refresh() early-returns while the snapshot tick is unchanged, so
            // without this the new page stays blank until the sim ticks over.
            _refreshedTick = -1;
        }

        // §76: a sheet row — name, level out of ten, and a bar. Same anatomy as
        // a need cell minus the icon, so the two pages sit at the same rhythm.
        private VisualElement BuildSheetCell(SheetConfig config, float widthPercent,
            List<SheetBinding> bindings)
        {
            var cell = new VisualElement();
            cell.style.width = Length.Percent(widthPercent);
            cell.style.paddingRight = 16f;
            cell.style.marginTop = 9f;
            cell.style.marginBottom = 9f;

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

            var value = new Label("—");
            value.style.color = TextDim;
            value.style.fontSize = 11;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            value.style.flexShrink = 0f;
            value.style.marginLeft = 6f;

            top.Add(label);
            top.Add(value);
            cell.Add(top);

            var track = MakeTrack(9f);
            var fill = MakeFill(config.Color);
            track.Add(fill);
            cell.Add(track);

            bindings.Add(new SheetBinding
            {
                Config = config,
                Label = label,
                Value = value,
                Fill = fill
            });

            return cell;
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
            col.style.width = 560f;
            col.style.flexShrink = 0f;
            col.style.backgroundColor = Panel;
            col.style.paddingLeft = 14f;
            col.style.paddingRight = 14f;
            col.style.paddingTop = 10f;
            col.style.paddingBottom = 10f;
            col.style.justifyContent = Justify.FlexStart;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 8f;
            _relationsTitle = MakeSectionTitle("");
            _relationsTitle.style.marginBottom = 0f;
            _relationsTitle.style.flexGrow = 1f;
            header.Add(_relationsTitle);

            col.Add(header);

            _relationsContainer = new VisualElement();
            _relationsContainer.style.flexGrow = 1f;
            _relationsContainer.style.minHeight = 0f;
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
            _natureTitle.text = Loc.Get("panel.nature");
            _skillsTitle.text = Loc.Get("panel.skills");
            _relationsTitle.text = Loc.Get("panel.relations");

            // §76: row names, and force the perk badges to re-localize (they
            // are rebuilt only when the perk SET changes, which a language
            // switch does not).
            for (var i = 0; i < _attrBindings.Count; i++)
            {
                _attrBindings[i].Label.text = Loc.Get(_attrBindings[i].Config.Key);
            }

            for (var i = 0; i < _skillBindings.Count; i++)
            {
                _skillBindings[i].Label.text = Loc.Get(_skillBindings[i].Config.Key);
            }

            _perkSig = null;
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

            // Inventory window (spec §51) — static chrome + force a rebuild so
            // the item rows / open detail re-localize on the next refresh.
            if (_inventoryButtonLabel != null)
            {
                _inventoryButtonLabel.text = Loc.Get("inv.button");
            }

            if (_inventoryTitle != null)
            {
                _inventoryTitle.text = Loc.Get("panel.inventory");
            }

            if (_invBackLabel != null)
            {
                _invBackLabel.text = Loc.Get("inv.back");
            }

            _invSig = null;

            // Limb-health window (spec §57) — rows re-localize on the next
            // refresh (the tick gate above is already invalidated).
            if (_healthTitle != null)
            {
                _healthTitle.text = Loc.Get("panel.health");
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

        private static Color DurabilityColor(float v)
        {
            if (v >= 0.65f) return Good;
            if (v >= 0.35f) return Warn;
            return Crit;
        }

        private static string DurabilityCondition(float v)
        {
            if (v >= 0.95f) return Loc.Get("inv.condition_ok");
            if (v >= 0.65f) return Loc.Get("inv.condition_good");
            if (v >= 0.35f) return Loc.Get("inv.condition_worn");
            return Loc.Get("inv.condition_torn");
        }

        private static int HeartsFor(float affinity)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(affinity) * 4f), 0, 4);
        }

        private static VisualElement BuildRelationMeter(float affinity)
        {
            var track = MakeTrack(4f);
            track.style.marginTop = 4f;

            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.top = 0f;
            fill.style.bottom = 0f;
            fill.style.width = Length.Percent(Mathf.Clamp01(Mathf.Abs(affinity)) * 50f);
            fill.style.left = Length.Percent(affinity >= 0f
                ? 50f
                : 50f - Mathf.Clamp01(-affinity) * 50f);
            fill.style.backgroundColor = RelationColor(affinity);
            track.Add(fill);

            return track;
        }

        private static VisualElement BuildPositiveMeter(float value, Color color)
        {
            var track = MakeTrack(4f);
            track.style.marginTop = 4f;

            var fill = MakeFill(color);
            fill.style.width = Length.Percent(Mathf.Clamp01(value) * 100f);
            track.Add(fill);

            return track;
        }

        private static string RelationScore(float affinity)
        {
            var pct = Mathf.RoundToInt(affinity * 100f);
            return pct > 0 ? $"+{pct}%" : $"{pct}%";
        }

        private static Color RelationColor(float affinity)
        {
            var pct = Mathf.RoundToInt(affinity * 100f);
            if (pct > 0) return Health;
            if (pct < 0) return Crit;
            return TextMute;
        }

        private static string RelationKind(float affinity)
        {
            if (affinity <= -0.25f) return Loc.Get("rel.hostile");
            if (affinity < 0f) return Loc.Get("rel.tense");
            if (affinity >= 0.75f) return Loc.Get("rel.close");
            if (affinity >= 0.50f) return Loc.Get("rel.friend");
            if (affinity >= 0.25f) return Loc.Get("rel.acquaint");
            return Loc.Get("rel.neutral");
        }

        private static string RelationMoodGlyph(float affinity)
        {
            if (affinity <= -0.25f) return "!";
            if (affinity < 0f) return "…";
            if (affinity >= 0.50f) return "♥";
            return "☺";
        }

        private static string RelationTag(float affinity)
        {
            if (Loc.Current == Language.Russian)
            {
                if (affinity <= -0.25f) return "напряжение";
                if (affinity < 0f) return "осторожно";
                if (affinity >= 0.50f) return "тепло";
                return "спокойно";
            }

            if (affinity <= -0.25f) return "tense";
            if (affinity < 0f) return "cautious";
            if (affinity >= 0.50f) return "warm";
            return "calm";
        }

        private static string SignalGlyph(int index, float affinity)
        {
            return index switch
            {
                0 => "✦",
                1 => affinity < 0f ? "!" : "♥",
                2 => "•",
                3 => "✧",
                _ => affinity < 0f ? "…" : "+"
            };
        }

        private sealed class RingMeter : VisualElement
        {
            private readonly float _value;
            private readonly Color _color;

            public RingMeter(float value, Color color)
            {
                _value = Mathf.Clamp01(value);
                _color = color;
                pickingMode = PickingMode.Ignore;
                generateVisualContent += OnGenerate;
            }

            private void OnGenerate(MeshGenerationContext ctx)
            {
                var rect = contentRect;
                if (rect.width < 2f || rect.height < 2f)
                {
                    return;
                }

                var p = ctx.painter2D;
                var center = rect.center;
                var radius = Mathf.Min(rect.width, rect.height) * 0.5f - 4f;

                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.lineWidth = 5f;

                p.strokeColor = new Color(1f, 1f, 1f, 0.095f);
                p.BeginPath();
                p.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                p.Stroke();

                if (_value > 0.003f)
                {
                    p.strokeColor = new Color(_color.r, _color.g, _color.b, 0.95f);
                    p.BeginPath();
                    p.Arc(center, radius, Angle.Degrees(-90f), Angle.Degrees(-90f + 359.9f * _value));
                    p.Stroke();
                }
            }
        }

        private static string InitialOf(string name)
        {
            return string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
        }

        // §80: лицо вместо кружка с буквой. Кружок остаётся фолбэком, и это не
        // временная мера: снимок появляется только через игровой час после
        // первой встречи, а до тех пор буква — единственное, что вообще есть.
        // Мёртвые лица тоже показываются: кэш переживает тело.
        private void ApplyRelationFace(VisualElement avatar, int otherId, Label initial)
        {
            if (_portraitCache == null || !_portraitCache.TryGet(otherId, out var face))
            {
                return;
            }

            avatar.style.backgroundImage = new StyleBackground(face);
            // Цвет фона под непрозрачным снимком только пробивался бы по краям
            // скруглённого кружка, а буква поверх лица нечитаема.
            avatar.style.backgroundColor = Color.clear;
            initial.style.display = DisplayStyle.None;
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
