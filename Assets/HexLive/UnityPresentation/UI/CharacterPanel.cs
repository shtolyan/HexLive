using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
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
    // §136: дневник живёт в CharacterPanel.Journal.cs — тот же класс, потому
    // что окно делит место и взаимное исключение с рюкзаком и куклой здоровья.
    public sealed partial class CharacterPanel : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;
        private HexWorldRenderer _worldRenderer;

        [Tooltip("Extra UI scale on top of the 1080p reference. 1 = design size.")]
        [SerializeField] private float _uiScale = 1f;

        [Tooltip("Slide in/out duration in seconds.")]
        [SerializeField] private float _slideDuration = 0.22f;

        private PortraitStage _portraitStage;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _stage;
        private VisualElement _card;
        private VisualElement _groupCard;
        private Label _groupTitle;
        private Label _groupManualSummary;
        private Label _groupAlerts;
        private Label _groupControlLabel;
        private Label _groupOrderResult;
        private Label _groupStopLabel;
        private VisualElement _groupPortraits;
        private bool _groupAllManual;

        // §123/§150: the right roster is visible even with no selection, but
        // non-owned people enter it only while personally perceived.
        private VisualElement _roster;
        private VisualElement _clanRosterList;
        private VisualElement _outsiderRosterList;
        private VisualElement _outsiderRosterSection;
        private Label _clanRosterHeader;
        private Label _outsiderRosterHeader;
        private int _rosterTick = int.MinValue;
        private bool _rosterVisibilityReady;
        private readonly List<RosterBinding> _rosterBindings = new();

        private sealed class RosterBinding
        {
            public int Id;
            public bool Fighting;
            public VisualElement Card;
            public VisualElement Dot;
        }

        // Live elements
        private VisualElement _portrait;
        private Label _nameLabel;
        private Label _roleLabel;
        private Label _statusLabel;
        private VectorIcon _uvIcon;
        private Label _uvLabel;
        private VisualElement _statusDot;
        private Label _thoughtValue;
        // §105: компактный круг HP поверх полноразмерного портретного фона.
        // Лицо больше не обводится: здоровье — самостоятельный instrument.
        private RingMeter _healthRing;
        private VisualElement _healthButton;
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
        private VisualElement _effectTooltipImpacts;
        private NpcSnapshot _currentTooltipNpc;
        private NeedKind? _hoveredNeed;
        private VisualElement _hoveredNeedAnchor;
        // Only rebuild the chips when the SET of effects changes, so hovering
        // stays stable across ticks (intensity-only shifts recolour in place).
        private readonly List<EffectKind> _effectSigKinds = new();
        private readonly List<string> _effectSigDetails = new();
        private readonly List<EffectView> _effectParseScratch = new();
        private readonly List<EffectImpactView> _effectImpactParseScratch = new();

        // Spec §51: character inventory — a backpack button on the identity
        // column opens the body/container layout. Its existing item card is a
        // single anchored popover, not a second inventory view or item store.
        private VisualElement _inventoryButton;

        // §121: тумблер «ИИ / Ручное» и последний прочитанный из снапшота режим.
        private VisualElement _controlButton;
        private VisualElement _stopButton;
        private int _manualEdgeNpcId = -1;
        private bool _manualEdgeWas;
        private VisualElement _controlAiSegment;
        private VisualElement _controlPlayerSegment;
        private Label _controlAiGlyph;
        private Label _controlPlayerGlyph;
        private VisualElement _controlAiIcon;
        private VisualElement _controlPlayerIcon;
        private Label _orderToast;
        // §123.5: дубль тоста отказа ВНУТРИ окна инвентаря — карточка
        // персонажа с основным тостом закрыта этим окном (940×620), и отказ
        // «выбросить нельзя» был игроку не виден вовсе.
        private Label _invOrderToast;
        private bool _manualControlNow;
        private bool _controlAvailable;
        private VisualElement _inventoryWindow;
        private Label _inventoryTitle;
        private Label _inventoryCapacity;
        // §133.9 / #193: authoritative per-NPC outfit lock shown in the
        // inventory header. The switch never caches authority: snapshot wins.
        private VisualElement _outfitLockButton;
        private VisualElement _outfitLockTrack;
        private VisualElement _outfitLockThumb;
        private Label _outfitLockLabel;
        private bool _outfitLockedNow;
        // §138: the backpack and manual recipe browser share one floating
        // window. Crafting is local-only and never exists for group/AI views.
        private VisualElement _inventoryTabs;
        private VisualElement _inventoryBackpackTab;
        private VisualElement _inventoryClothesTab;
        private VisualElement _inventoryCraftTab;
        private Label _inventoryBackpackTabLabel;
        private Label _inventoryClothesTabLabel;
        private Label _inventoryCraftTabLabel;
        private VisualElement _craftView;
        private VisualElement _craftRecipeGrid;
        private VisualElement _craftDetail;
        private Image _craftDetailIcon;
        private Label _craftDetailEmoji;
        private Label _craftDetailName;
        private Label _craftResourcesTitle;
        private VisualElement _craftIngredients;
        private Label _craftStation;
        private Label _craftProgress;
        private Label _craftReason;
        private VisualElement _craftAction;
        private Label _craftActionLabel;
        private readonly List<CraftRecipeOption> _craftOptions = new();
        private readonly Dictionary<int, int> _selectedCraftGoalByNpc = new();
        private int _selectedCraftGoal = -1;
        private string _craftSig;
        private bool _craftPage;
        private bool _clothesPage;
        private bool _craftAvailable;
        private VisualElement _invListView;   // elastic flat grid + aspect-locked doll
        private VisualElement _invItemsPane;
        private VisualElement _invItemsContent;
        private VisualElement _invDollPane;
        private VisualElement _invLayerBar;
        private VisualElement _invPreviewView;
        private VisualElement _invPreviewHoverAnchor;
        private CharacterDollStage _characterDollStage;
        private VisualElement _invDetailView; // one reused item-card popover
        private VisualElement _invDetailAnchor;
        private InventoryDetailPlacement _invDetailPlacement = InventoryDetailPlacement.Item;
        private Label _invBackLabel;
        private Label _invDetailEmoji;
        private Image _invDetailIcon;
        private Label _invDetailName;
        private Label _invDetailCategory;
        private Label _invDetailDesc;
        private VisualElement _invDetailStats;
        private VisualElement _invPrimaryAction;
        private VisualElement _invDropAction;
        private Label _invPrimaryActionLabel;
        private Label _invDropActionLabel;
        private Label _invReadOnlyLabel;
        private int _inventoryActorId = -1;
        private bool _inventoryMutable;
        private string _invDraggedId;
        private bool _invDraggedWorn;
        private bool _inventoryOpen;
        private string _invSig;               // rebuild the list only on change
        private string _invSelectedId;        // item shown in the detail view
        private bool _invSelectedWorn;
        // §123.5: авторитетный индекс выбранного carried-предмета в
        // npc.Inventory.Items — приходит из InventorySlotSnapshot.SourceIndex.
        // Легаси-поиск по npc.InventoryItems давал НЕВЕРНЫЙ индекс: экспортер
        // снапшота переупорядочивает стаки, и симуляция молча отвечала
        // StaleItem — «выбросить из рюкзака» не работало вовсе.
        private int _invSelectedSourceIndex = -1;
        private bool _invHeldSlotMarked;
        private bool _invPreviewDragging;
        private int _invPreviewPointerId = -1;
        private Vector2 _invPreviewPointerPosition;
        private Vector2 _invPreviewPointerDownPosition;
        private bool _invPreviewRotated;
        private string _invPointerItemId;
        private bool _invPointerItemWorn;
        private int _invPointerId = -1;
        private Vector2 _invPointerDownPosition;
        private bool _invPointerMoved;

        // §128.4: двойной клик по ячейке = «надеть/снять», то же правило и тот
        // же порог, что в окне обмена. Решение живёт в InventoryQuickActions;
        // приказ по-прежнему уходит отсюда, штатным EnqueueInventoryAction.
        private readonly DoubleClickWatch _invDoubleClick = new();
        private bool _invPointerDoubleClick;
        private string _invHoveredWornId;
        private bool _invHoverDetailFocus;
        private VisualWearLayer _invVisibleWearLayer = VisualWearLayer.Bags;
        private readonly Dictionary<string, VisualElement> _invItemAnchors = new();
        private readonly Dictionary<VisualWearLayer, VisualElement> _invLayerButtons = new();
        private readonly Dictionary<VisualWearLayer, VectorIcon> _invLayerIcons = new();
        private readonly List<VisualElement> _invSlotCells = new();
        private readonly List<VisualElement> _invSlotIcons = new();
        private readonly List<Label> _invSlotGlyphs = new();
        private readonly List<Label> _invSlotBadges = new();
        private int _invDensityTier;
        private float _invFitPaneWidth = -1f;
        private float _invFitPaneHeight = -1f;
        // The flat inventory grid deliberately leaves future headroom: every
        // tier is 20% smaller than the first approved large-icon pass while
        // preserving one shared cell component for every physical slot.
        private static readonly float[] InventoryCellSizes = { 106f, 93f, 80f, 70f, 61f };
        private static readonly float[] InventoryGaps = { 8f, 6f, 6f, 5f, 4f };
        private const float InventoryLayerBarHeight = 48f;
        private const float InventoryLayerBarGap = 8f;
        private const float InventoryDragThreshold = 6f;
        private Dictionary<string, float> _invWornDurability = new();
        private Dictionary<string, WaterContainerState> _invCarriedWater = new();
        private Dictionary<string, int> _invCarriedStacks = new();
        private Dictionary<string, float> _invWornWetness = new();
        private Dictionary<string, float> _invWornDirtiness = new();
        private List<int> _invCarriedOwnerIds = new();
        private List<int> _invWornOwnerIds = new();
        private List<string> _invWornItemIds = new();
        private readonly Dictionary<int, string> _invOwnerNames = new();
        private MeleeStatsSnapshot _meleeStats = new();

        // Spec §57: limb-health window — click the HP row to pop a floating
        // window with the rotating body doll (per-zone green→yellow→red mesh)
        // and a per-limb readout list.
        private NpcPortraitCache _portraitCache;
        private VisualElement _healthWindow;
        private Label _healthTitle;
        private VisualElement _healthDollImage;
        private Label _healthDollError;
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

        // §126: страница «Характер» — черты карточками с описанием прямо на
        // ней, а не бейджами с тултипом. Черт у человека одна-две, объяснение
        // им нужно ровно там же, где имя: «Неряха» само по себе не говорит,
        // что она никогда не пойдёт мыться.
        private Label _characterTitle;
        private VisualElement _characterContainer;
        private Label _characterEmpty;
        private readonly List<string> _traitSig = new();
        private SheetTab _sheetTab = SheetTab.Needs;
        // §76: what the sheet tooltip pops above — the whole middle column, so
        // the card lands in one predictable place instead of chasing the row
        // and clipping at the panel edge.
        private VisualElement _sheetTooltipAnchor;
        private readonly List<SheetBinding> _attrBindings = new();
        private readonly List<SheetBinding> _skillBindings = new();
        private readonly List<string> _perkSig = new();
        private Label _relationsTitle;
        private Button _langButton;
        private Label _diagnosticsLabel;
        private float _diagnosticsElapsed;
        private int _diagnosticsFrames;

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
        private const float InventoryWindowMaxWidth = 940f;
        private const float InventoryWindowMaxHeight = 700f;

        // Both viewports keep the doll RenderTexture's exact aspect, so
        // BackgroundSizeType.Contain has nothing left to letterbox. The health
        // window is anchored by its BOTTOM and the doll box is its last row, so
        // a taller box grows upward only — the feet stay put and the extra room
        // opens above the head.
        private const float DollAspectHeight = 896f / 512f;
        private const float DollViewportWidth = 195f;
        private const float MaxDollPaneWidthFraction = 0.45f;
        private const float HealthWindowWidth = 540f;
        private const float HealthColumnGap = 12f;
        private const float HealthZoneRowGap = 6f;

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
        private static readonly Color IdentityGlass = new(0.025f, 0.050f, 0.067f, 0.82f);
        private static readonly Color NeonCyan = new(0.18f, 0.92f, 0.88f);
        private static readonly Color NeonCyanDim = new(0.18f, 0.92f, 0.88f, 0.34f);
        // CharacterDollStage clears to this transparent RGB. Repeating the
        // same opaque colour across the whole pane turns the aspect-fit side
        // space into one studio surface instead of two accidental gutters.
        private static readonly Color InventoryDollBackdrop =
            new(0.035f, 0.047f, 0.055f, 1f);
        // §80: подложка под прозрачные снимки лиц (тот же тон, что был залит
        // в саму текстуру, пока фон был непрозрачным).
        private static readonly Color PortraitBackdrop = new(0.10f, 0.12f, 0.14f);
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
            public VisualElement Cell;
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
            public bool Percent;
        }

        private struct SheetBinding
        {
            public SheetConfig Config;
            public Label Label;
            public Label Value;
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
            new() { Id = "Perception", Key = "attr.perception", Color = Comfort },
            new() { Id = "CompassionTrait", Key = "trait.compassion", Color = Compassion, Percent = true },
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
        // §126 дописал Character в конец: страница про то, КАКОЙ она человек —
        // рядом с «Природой» (что может тело) и «Умениями» (что умеют руки),
        // но своей страницей, потому что черта это не число на шкале.
        private enum SheetTab { Needs, Nature, Skills, Character }

        private enum InventoryDetailPlacement
        {
            Preserve,
            Item,
            Doll
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

        public void SetCharacterDollStage(CharacterDollStage stage) =>
            _characterDollStage = stage;

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
            _stage.style.display = DisplayStyle.None;
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
            UpdateDiagnostics();

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

            RefreshRoster();
            AnimateRoster();

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
            if (mouse == null || panelHeight < 1f)
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
                NpcSelection.PointerOverUi = (withinY && withinX) ||
                    PointerOverElement(_roster, mousePos, scale);
                return;
            }

            var overBar = _shown && PointerOverElement(_stage, mousePos, scale);
            var overRoster = PointerOverElement(_roster, mousePos, scale);

            // The floating windows (inventory / limb health) sit ABOVE the bar —
            // their bounds must also swallow clicks, or picking inside them
            // deselects the NPC.
            NpcSelection.PointerOverUi = overBar || overRoster ||
                PointerOverFloating(_inventoryOpen, _inventoryWindow, mousePos, scale) ||
                PointerOverFloating(
                    _inventoryOpen && _invSelectedId != null,
                    _invDetailView, mousePos, scale) ||
                PointerOverFloating(_healthOpen, _healthWindow, mousePos, scale) ||
                // §136: без этой строки клик внутри дневника снимал бы
                // выделение с колонистки, чей дневник открыт.
                PointerOverFloating(_journalOpen, _journalWindow, mousePos, scale);
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

        private void OnSelectionChanged(IReadOnlyList<int> selection)
        {
            _shown = NpcSelection.HasSelection;
            _refreshedTick = -1;
            _rosterTick = int.MinValue;
            CloseInventory(); // a new/cleared selection resets the backpack
            CloseHealth();    // …and the limb-health window
            CloseJournal();   // …and the journal (§136)
            if (_shown)
            {
                _stage.style.display = DisplayStyle.Flex;
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
                _stage.style.display = DisplayStyle.None;
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

            if (NpcSelection.Count > 1)
            {
                RefreshGroup(snapshot);
                return;
            }

            _groupCard.style.display = DisplayStyle.None;
            _card.style.display = DisplayStyle.Flex;

            if (snapshot.Tick == _refreshedTick && NpcSelection.PrimaryId == _refreshedActorId)
            {
                return;
            }

            var npc = FindNpc(snapshot, NpcSelection.PrimaryId, out var isDead);
            if (npc == null)
            {
                return;
            }

            _refreshedTick = snapshot.Tick;
            _refreshedActorId = npc.Id.Value;
            _currentTooltipNpc = npc;

            // §74: DisplayName is a name ID; the player sees the localized term.
            _nameLabel.text = string.IsNullOrEmpty(npc.DisplayName)
                ? $"NPC #{npc.Id.Value}"
                : Loc.NpcName(npc.DisplayName);
            _roleLabel.text = $"{Loc.Get("panel.role")} · #{npc.Id.Value}";
            // §40.6 r14 (#175): весь черёд поштучной стирки идёт под целью
            // Bathe, и панель писала «купается», пока она тёрла вещь на песке.
            // Такт стирки виден по интеракции — показываем «стирает» (термин
            // цели WashClothes, новых строк нет).
            _thoughtValue.text = isDead ? Loc.Get("state.dead")
                : npc.CurrentInteraction == "WashClothes" &&
                  npc.ExecutionStatus == "InProgress"
                    ? Loc.Goal("WashClothes")
                    : Loc.Goal(npc.CurrentGoal);
            RefreshControlToggle(npc); // §121

            // Spec §64: the dream pill — show her aspiration, hide it when she has
            // nothing left to dream of ("None"/empty).
            var hasDream = !isDead && !string.IsNullOrEmpty(npc.CurrentDream) &&
                npc.CurrentDream != "None";
            _dream.style.display = hasDream ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasDream)
            {
                _dreamValue.text = Loc.Dream(npc.CurrentDream);
            }

            if (_boundActorId != npc.Id.Value)
            {
                _boundActorId = npc.Id.Value;
                BindPortrait(npc.Id.Value);
                // A different colonist's relations must redraw even if the row
                // count and numbers happen to line up — the cached signature is
                // about HER list, so it does not carry over.
                _relationSig.Clear();
                _relationSigSelected = int.MinValue;
                _relationsBuiltEmpty = false;
            }

            // §105 r3: ХП — витальное здоровье, придавленное конечностями
            // (`BodyState.DisplayHealth`). Считает сим: «что такое витальная
            // зона» и сколько весит нога — не знание вида. Цвет берётся у той
            // же функции, что красит куклу §57, — два мнения о «насколько всё
            // плохо» разошлись бы.
            var hp = isDead ? 0f : Mathf.Clamp01(npc.DisplayHealth);
            _healthRing.Set(hp, CharacterDollStage.StatusColor(hp, false));
            _healthValue.text = $"{Mathf.RoundToInt(hp * 100f)}%";

            UpdateStatus(npc, isDead);
            UpdateUv(npc);

            var showBadge = npc.IsStarving || npc.IsFighting;
            _starvingBadge.style.display = showBadge ? DisplayStyle.Flex : DisplayStyle.None;
            _starvingBadge.text = npc.IsFighting ? Loc.Get("badge.fighting") : Loc.Get("badge.starving");

            UpdateNeeds(npc);
            UpdateSheet(npc);
            UpdateEffects(npc);
            UpdateRelations(npc);
            _meleeStats = npc.MeleeStats ?? new MeleeStatsSnapshot();
            RefreshInventory(snapshot, npc);
            RefreshHealth(npc);

            // §136: бейдж считается ВСЕГДА — он и существует ради того, чтобы
            // сказать «у неё что-то случилось» до того, как окно откроют.
            UpdateJournalBadge(snapshot, npc);
            RefreshJournal(snapshot, npc);
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

        private void UpdateStatus(NpcSnapshot npc, bool isDead)
        {
            ResolveStatus(npc, isDead, out var key, out var dot);
            _statusLabel.text = Loc.Get(key);
            _statusDot.style.backgroundColor = dot;
        }

        /// <summary>§123 shared resolver for the roster and detail card.</summary>
        private static void ResolveStatus(NpcSnapshot npc, out string key, out Color dot)
        {
            ResolveStatus(npc, isDead: false, out key, out dot);
        }

        private static void ResolveStatus(
            NpcSnapshot npc, bool isDead, out string key, out Color dot)
        {
            // §105 r5: СОСТОЯНИЕ ТЕЛА идёт первым и по убыванию тяжести.
            // Раньше строка знала три вещи — «идёт», «занята», «отдыхает» — и
            // умирающая, лежащая в коме и спящая одинаково попадали в
            // «отдыхает» (цель у всех троих None). Панель сообщала «просто
            // существует» ровно тогда, когда происходило самое важное.
            if (isDead)
            {
                key = "state.dead";
                dot = Crit;
            }
            else if (npc.IsDying)
            {
                key = "state.dying";
                dot = Crit;
            }
            else if (npc.IsUnconscious)
            {
                key = "state.coma";
                dot = Crit;
            }
            else if (npc.IsFainted)
            {
                key = "state.fainted";
                dot = Warn;
            }
            // §105.14: она В СОЗНАНИИ и решает сама — поэтому НИЖЕ обморока и
            // отдельной строкой: игрок должен понимать, что тело на земле
            // живое и ждёт, пока волк уйдёт, а не отключилось.
            else if (npc.IsPlayingDead)
            {
                key = "state.playdead";
                dot = Warn;
            }
            else if (npc.CurrentInteraction == "Sleep" && npc.ExecutionStatus == "InProgress")
            {
                key = "state.sleeping";
                dot = Energy;
            }
            else if (npc.MovementStatus == "Moving")
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
                var bloodDeficit = b.Config.Key == "need.blood"
                    ? Mathf.Clamp01(npc.BloodDeficit)
                    : 0f;

                // Direct (stress): the bar IS the pressure — 100% full and red
                // when maxed, a sliver of green when calm.
                // Otherwise: well-being — full green is good.
                var display = b.Config.Direct ? raw
                    : b.Config.Pressure ? 1f - raw : raw;
                var goodness = b.Config.Direct ? 1f - raw : display;

                // §118: after ordinary blood reaches zero the same track
                // becomes the red negative reserve. The signed label prevents
                // a 40% deficit from looking like 40% healthy blood.
                if (bloodDeficit > 0f)
                {
                    display = bloodDeficit;
                    goodness = 0f;
                }

                b.Fill.style.width = Length.Percent(display * 100f);
                b.Fill.style.backgroundColor = bloodDeficit > 0f
                    ? Crit
                    : LevelColor(goodness);
                b.Pct.text = bloodDeficit > 0f
                    ? $"−{Mathf.RoundToInt(bloodDeficit * 100f)}%"
                    : $"{Mathf.RoundToInt(display * 100f)}%";

                var alarm = goodness < 0.30f;
                b.Pct.style.color = alarm ? Crit : TextDim;
                b.Label.style.color = alarm ? new Color(0.949f, 0.769f, 0.753f) : Text;
            }

            UpdateThermal(npc.ThermalComfort);

            if (_hoveredNeed is { } hovered && _hoveredNeedAnchor != null)
            {
                ShowNeedTooltip(hovered, _hoveredNeedAnchor, npc);
            }
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
            UpdateTraits(npc);
        }

        // §126: карточки черт. Сим отдаёт ГОТОВЫЕ базовые ключи ("trait.slob"),
        // вид дописывает ".title"/".desc" — два суффикса в одном месте вместо
        // двух строк на черту в снапшоте. Перестраивается только при СМЕНЕ
        // набора, как перки и чипы эффектов: черта постоянна, и пересоздавать
        // карточки каждый тик было бы чистым мусором.
        private void UpdateTraits(NpcSnapshot npc)
        {
            var changed = _traitSig.Count != npc.Traits.Count;
            for (var i = 0; !changed && i < npc.Traits.Count; i++)
            {
                changed = _traitSig[i] != npc.Traits[i];
            }

            if (!changed)
            {
                return;
            }

            _traitSig.Clear();
            _traitSig.AddRange(npc.Traits);

            // Заглушка живёт отдельным полем и переиспользуется, а не
            // пересоздаётся: у большинства колонисток черт нет вовсе, и это
            // самый частый вид страницы.
            for (var i = _characterContainer.childCount - 1; i >= 0; i--)
            {
                if (_characterContainer[i] != _characterEmpty)
                {
                    _characterContainer.RemoveAt(i);
                }
            }

            _characterEmpty.text = Loc.Get("sheet.character.empty");
            _characterEmpty.style.display = npc.Traits.Count == 0
                ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var key in npc.Traits)
            {
                _characterContainer.Add(BuildTraitCard(key));
            }
        }

        // Одна черта: имя и под ним объяснение, что она меняет в поведении.
        private VisualElement BuildTraitCard(string key)
        {
            var card = new VisualElement();
            card.style.backgroundColor = Raised;
            card.style.paddingLeft = 10f;
            card.style.paddingRight = 10f;
            card.style.paddingTop = 7f;
            card.style.paddingBottom = 8f;
            card.style.marginTop = 5f;
            SetRadius(card, 8f);
            SetBorder(card, Stroke, 1f);

            var title = new Label(Loc.Get(key + ".title"));
            title.style.color = Gold;
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(title);

            var desc = new Label(Loc.Get(key + ".desc"));
            desc.style.color = TextDim;
            desc.style.fontSize = 11;
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.marginTop = 3f;
            card.Add(desc);

            return card;
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

                // A rating out of ten, with NO denominator and no bar: the sheet
                // is an RPG reading ("she is a 7 at crafting"), and the number
                // is not capped at the display's ten — it just reads high.
                // Clamped only at zero; a value above 1.0 shows as 11, 12, …
                // rather than pretending the scale ended.
                value = Mathf.Max(0f, value);
                b.Value.text = b.Config.Percent
                    ? $"{Mathf.RoundToInt(value * 100f)}%"
                    : Mathf.RoundToInt(value * 10f).ToString();
                // Same hue always (the row keeps its identity), brightness
                // carries the magnitude. Deliberately NOT red-for-low: on a
                // point-buy sheet a low line is a trade-off, not a defect, and
                // an alarm colour would read as "something is wrong with her".
                b.Value.style.color = Color.Lerp(TextMute, b.Config.Color, Mathf.Clamp01(value));
            }
        }

        // §76.6: the badges an extreme attribute earns. The sim hands over
        // finished term keys — the bands are its knobs, not the view's — so
        // this only localizes and lays out. Rebuilt only when the SET changes,
        // like the effect chips: perks are permanent, and re-creating twelve
        // elements every tick would be pure churn.
        private void UpdatePerks(NpcSnapshot npc)
        {
            // PERF: was string.Join every tick — a fresh string allocated only to
            // find out the perk set had not moved. Perks are permanent; compare
            // the keys directly.
            var changed = _perkSig.Count != npc.Perks.Count;
            for (var i = 0; !changed && i < npc.Perks.Count; i++)
            {
                changed = _perkSig[i] != npc.Perks[i];
            }

            if (!changed)
            {
                return;
            }

            _perkSig.Clear();
            _perkSig.AddRange(npc.Perks);
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

            _effectTooltipImpacts = new VisualElement();
            _effectTooltipImpacts.style.marginTop = 5f;
            _effectTooltipImpacts.style.display = DisplayStyle.None;
            _effectTooltip.Add(_effectTooltipImpacts);

            _root.Add(_effectTooltip);
        }

        private struct EffectView
        {
            public EffectKind Kind;
            public float Intensity;
            public string DetailKey;
        }

        private struct EffectImpactView
        {
            public EffectKind Kind;
            public EffectImpactDirection Direction;
        }

        // Rebuild the chip row from the snapshot's "Kind\tintensity" list —
        // debuffs first, each sorted worst (most intense) to the left. The row
        // is rebuilt only when the ordered set changes (keeps hover stable);
        // otherwise only the ring colours refresh.
        private void UpdateEffects(NpcSnapshot npc)
        {
            // PERF: scratch list reused across ticks; the intensity is parsed off
            // a span so the "Kind\t0.42" row costs one short string (the kind,
            // which Enum.TryParse has no span overload for) instead of two.
            var parsed = _effectParseScratch;
            parsed.Clear();
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
                var detailKey = string.Empty;
                if (tab >= 0)
                {
                    var detailTab = raw.IndexOf('\t', tab + 1);
                    var intensitySpan = detailTab >= 0
                        ? raw.AsSpan(tab + 1, detailTab - tab - 1)
                        : raw.AsSpan(tab + 1);
                    float.TryParse(
                        intensitySpan,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out intensity);
                    if (detailTab >= 0 && detailTab + 1 < raw.Length)
                    {
                        detailKey = raw.Substring(detailTab + 1);
                    }
                }

                parsed.Add(new EffectView
                {
                    Kind = kind,
                    Intensity = intensity,
                    DetailKey = detailKey,
                });
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

            // PERF: was a string built by += in a loop — quadratic garbage every
            // tick to answer a yes/no question. The kinds are compared directly.
            var changed = _effectSigKinds.Count != parsed.Count ||
                _effectSigDetails.Count != parsed.Count;
            for (var i = 0; !changed && i < parsed.Count; i++)
            {
                changed = _effectSigKinds[i] != parsed[i].Kind ||
                    _effectSigDetails[i] != parsed[i].DetailKey;
            }

            if (changed)
            {
                _effectSigKinds.Clear();
                _effectSigDetails.Clear();
                for (var i = 0; i < parsed.Count; i++)
                {
                    _effectSigKinds.Add(parsed[i].Kind);
                    _effectSigDetails.Add(parsed[i].DetailKey);
                }

                HideEffectTooltip();
                _effectsRow.Clear();
                foreach (var e in parsed)
                {
                    _effectsRow.Add(BuildEffectChip(e.Kind, e.Intensity, e.DetailKey));
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

        private VisualElement BuildEffectChip(
            EffectKind kind, float intensity, string detailKey)
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

            chip.RegisterCallback<MouseEnterEvent>(_ => ShowEffectTooltip(kind, detailKey));
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

        private void ShowEffectTooltip(EffectKind kind, string detailKey)
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
            _effectTooltipDesc.text = Loc.Get(
                string.IsNullOrEmpty(detailKey) ? def.DescKey : detailKey);
            _effectTooltipDesc.style.display = DisplayStyle.Flex;
            _effectTooltipImpacts.style.display = DisplayStyle.None;

            PopTooltipAbove(_effectsRow);
        }

        // §48.7: the stat card does not infer causes from the final number.
        // It renders only the rows recorded by the simulation at the real
        // mutation sites and delivered through NpcSnapshot.EffectImpacts.
        private void ShowNeedTooltip(NeedKind need, VisualElement anchor, NpcSnapshot npc)
        {
            if (_effectTooltip == null || npc == null)
            {
                return;
            }

            var parsed = _effectImpactParseScratch;
            parsed.Clear();
            foreach (var raw in npc.EffectImpacts)
            {
                var fields = raw.Split('\t');
                if (fields.Length < 3 ||
                    !Enum.TryParse(fields[0], out NeedKind rowNeed) || rowNeed != need ||
                    !Enum.TryParse(fields[1], out EffectKind kind) ||
                    !EffectCatalog.TryGet(kind, out _) ||
                    !Enum.TryParse(fields[2], out EffectImpactDirection direction))
                {
                    continue;
                }

                if (!HasVisibleImpact(parsed, kind, direction))
                {
                    parsed.Add(new EffectImpactView { Kind = kind, Direction = direction });
                }
            }

            parsed.Sort((a, b) =>
            {
                var direction = a.Direction.CompareTo(b.Direction); // Negative first.
                return direction != 0 ? direction : a.Kind.CompareTo(b.Kind);
            });

            _effectTooltipIcon.text = "↕";
            _effectTooltipTitle.text = Loc.Get(NeedLocKey(need));
            _effectTooltipTitle.style.color = Text;
            _effectTooltipDesc.text = Loc.Get(
                parsed.Count > 0 ? "effect.impacts.current" : "effect.impacts.none");
            _effectTooltipDesc.style.display = DisplayStyle.Flex;

            _effectTooltipImpacts.Clear();
            foreach (var impact in parsed)
            {
                _effectTooltipImpacts.Add(BuildNeedImpactRow(impact));
            }
            _effectTooltipImpacts.style.display = parsed.Count > 0
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            PopTooltipAbove(_sheetTooltipAnchor ?? anchor);
        }

        private static bool HasVisibleImpact(
            List<EffectImpactView> impacts,
            EffectKind kind,
            EffectImpactDirection direction)
        {
            for (var i = 0; i < impacts.Count; i++)
            {
                if (impacts[i].Kind == kind && impacts[i].Direction == direction)
                {
                    return true;
                }
            }

            return false;
        }

        private VisualElement BuildNeedImpactRow(EffectImpactView impact)
        {
            var def = EffectCatalog.Get(impact.Kind);
            var positive = impact.Direction == EffectImpactDirection.Positive;
            var color = positive ? Good : new Color(0.949f, 0.769f, 0.753f);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 5f;
            row.style.paddingTop = 5f;
            row.style.paddingBottom = 5f;
            row.style.paddingLeft = 8f;
            row.style.paddingRight = 8f;
            row.style.backgroundColor = new Color(color.r, color.g, color.b, 0.08f);
            SetRadius(row, 7f);

            var icon = new Label(def.Emoji);
            icon.style.fontSize = 18;
            icon.style.width = 28f;
            icon.style.flexShrink = 0f;
            icon.pickingMode = PickingMode.Ignore;
            row.Add(icon);

            var title = new Label(Loc.Get(def.TitleKey));
            title.style.color = Text;
            title.style.fontSize = 12;
            title.style.flexGrow = 1f;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.pickingMode = PickingMode.Ignore;
            row.Add(title);

            var arrow = new Label(positive ? "↑" : "↓");
            arrow.style.color = color;
            arrow.style.fontSize = 18;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            arrow.style.marginLeft = 8f;
            arrow.style.flexShrink = 0f;
            arrow.pickingMode = PickingMode.Ignore;
            row.Add(arrow);

            return row;
        }

        // Always the SAME spot for a given row: pinned to the anchor's left edge
        // and lifted fully above it (a percentage translate is of the tooltip's
        // own height, so nothing needs measuring). Anchoring beats following the
        // cursor here — a card that chases the mouse runs off-screen at the
        // panel's edges, and these rows sit close to them.
        private void PopTooltipAbove(VisualElement anchor)
        {
            var rb = anchor.worldBound;
            if (!float.IsNaN(rb.x))
            {
                _effectTooltip.style.left = rb.x;
                _effectTooltip.style.top = rb.y - 10f;
                _effectTooltip.style.translate = new Translate(0f, Length.Percent(-100));
            }

            _effectTooltip.style.display = DisplayStyle.Flex;
        }

        // §76: the character-sheet tooltip. Shares the one card with the effect
        // chips — same look, one place to restyle — but anchors to the sheet
        // column so the explanation pops next to what you are pointing at.
        private void ShowSheetTooltip(SheetConfig config, VisualElement anchor)
        {
            if (_effectTooltip == null)
            {
                return;
            }

            _effectTooltipIcon.text = "?";
            _effectTooltipTitle.text = Loc.Get(config.Key);
            _effectTooltipTitle.style.color = Text;
            _effectTooltipDesc.text = Loc.Get(config.Key + ".desc");
            _effectTooltipDesc.style.display = DisplayStyle.Flex;
            _effectTooltipImpacts.style.display = DisplayStyle.None;
            PopTooltipAbove(anchor);
        }

        private void HideEffectTooltip()
        {
            if (_effectTooltip != null)
            {
                _effectTooltip.style.display = DisplayStyle.None;
            }
        }

        // ── inventory / backpack (spec §51) ───────────────────────────────

        // The floating body/container layout. It stays visible while the
        // unchanged legacy item card opens beside the deliberately clicked item.
        private void BuildInventoryWindow()
        {
            _inventoryWindow = new VisualElement();
            _inventoryWindow.style.position = Position.Absolute;
            _inventoryWindow.style.bottom = InventoryWindowBottom;
            _inventoryWindow.style.width = InventoryWindowMaxWidth;
            _inventoryWindow.style.height = InventoryWindowMaxHeight;
            _inventoryWindow.style.backgroundColor = Panel;
            SetBorder(_inventoryWindow, StrokeStrong, 1f);
            SetRadius(_inventoryWindow, 16f);
            _inventoryWindow.style.paddingLeft = 14f;
            _inventoryWindow.style.paddingRight = 14f;
            _inventoryWindow.style.paddingTop = 12f;
            _inventoryWindow.style.paddingBottom = 12f;
            _inventoryWindow.style.overflow = Overflow.Hidden;
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

            header.Add(BuildOutfitLockToggle());

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

            BuildInventoryTabs();

            // ── approved strict 50/50 layout ───────────────────────────
            _invListView = new VisualElement();
            _invListView.style.flexDirection = FlexDirection.Row;
            _invListView.style.flexGrow = 1f;
            _invListView.style.minHeight = 0f;

            _invItemsPane = new VisualElement();
            // The item list takes every pixel the portrait does not need. A
            // pane pinned to half the window left tall empty strips beside a
            // portrait whose aspect is much narrower than half the window.
            _invItemsPane.style.flexGrow = 1f;
            _invItemsPane.style.flexShrink = 1f;
            _invItemsPane.style.minWidth = 0f;
            _invItemsPane.style.minHeight = 0f;
            _invItemsPane.style.paddingRight = 5f;
            _invItemsPane.style.overflow = Overflow.Visible;

            _invItemsContent = new VisualElement();
            _invItemsContent.style.flexDirection = FlexDirection.Row;
            _invItemsContent.style.flexWrap = Wrap.Wrap;
            _invItemsContent.style.alignContent = Align.FlexStart;
            _invItemsContent.style.justifyContent = Justify.FlexStart;
            _invItemsContent.style.width = Length.Percent(100f);
            _invItemsContent.style.paddingTop = 2f;
            _invItemsContent.style.paddingLeft = 2f;
            _invItemsPane.Add(_invItemsContent);
            _invListView.Add(_invItemsPane);

            // §123.5: тост отказа внутри окна — основной живёт в карточке
            // персонажа, которую это окно закрывает собой.
            _invOrderToast = new Label
            {
                style =
                {
                    color = Warn,
                    fontSize = 11f,
                    position = Position.Absolute,
                    left = 200f,
                    right = 200f,
                    bottom = 10f,
                    paddingLeft = 8f,
                    paddingRight = 8f,
                    paddingTop = 5f,
                    paddingBottom = 5f,
                    backgroundColor = Panel,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    whiteSpace = WhiteSpace.Normal,
                    display = DisplayStyle.None,
                }
            };
            SetRadius(_invOrderToast, 7f);
            SetBorder(_invOrderToast, new Color(Warn.r, Warn.g, Warn.b, 0.32f), 1f);
            _invOrderToast.pickingMode = PickingMode.Ignore;

            _invDollPane = new VisualElement();
            // Width is AUTO on purpose: the card is exactly as wide as the
            // portrait inside it, so there is no dead field to either side.
            _invDollPane.style.flexGrow = 0f;
            _invDollPane.style.flexShrink = 0f;
            _invDollPane.style.flexDirection = FlexDirection.Column;
            _invDollPane.style.alignItems = Align.Center;
            _invDollPane.style.justifyContent = Justify.Center;
            _invDollPane.style.backgroundColor = InventoryDollBackdrop;
            _invDollPane.style.overflow = Overflow.Hidden;
            SetBorder(_invDollPane, Stroke, 1f);
            SetRadius(_invDollPane, 13f);

            _invLayerBar = new VisualElement { name = "inventory-wear-layer-bar" };
            _invLayerBar.style.height = InventoryLayerBarHeight;
            _invLayerBar.style.flexShrink = 0f;
            _invLayerBar.style.flexDirection = FlexDirection.Row;
            _invLayerBar.style.alignItems = Align.Center;
            _invLayerBar.style.justifyContent = Justify.Center;
            _invLayerBar.style.marginBottom = InventoryLayerBarGap;
            _invLayerBar.style.paddingLeft = 5f;
            _invLayerBar.style.paddingRight = 5f;
            _invLayerBar.style.backgroundColor = new Color(0.025f, 0.035f, 0.041f, 0.92f);
            SetBorder(_invLayerBar, Stroke, 1f);
            SetRadius(_invLayerBar, 11f);
            BuildInventoryWearLayerButtons();
            _invDollPane.Add(_invLayerBar);

            _invPreviewView = new VisualElement();
            _invPreviewView.style.position = Position.Relative;
            _invPreviewView.style.flexGrow = 0f;
            _invPreviewView.style.flexShrink = 0f;
            _invPreviewView.style.width = DollViewportWidth;
            _invPreviewView.style.height = DollViewportWidth * DollAspectHeight;
            _invPreviewView.style.backgroundColor = InventoryDollBackdrop;
            _invPreviewView.style.backgroundSize = new BackgroundSize(
                BackgroundSizeType.Contain);
            RegisterInventoryPreviewInput();

            // A tiny non-pickable anchor lets the legacy card open beside the
            // actual point on the 3D preview instead of beside the whole view.
            _invPreviewHoverAnchor = new VisualElement();
            _invPreviewHoverAnchor.style.position = Position.Absolute;
            _invPreviewHoverAnchor.style.width = 2f;
            _invPreviewHoverAnchor.style.height = 2f;
            _invPreviewHoverAnchor.pickingMode = PickingMode.Ignore;
            _invPreviewView.Add(_invPreviewHoverAnchor);
            _invDollPane.Add(_invPreviewView);
            _invListView.Add(_invDollPane);
            // Sized from the ROW's height, never from the pane's own width —
            // that width now follows the portrait and the two would chase each
            // other around the layout.
            _invListView.RegisterCallback<GeometryChangedEvent>(_ => FitInventoryDollViewport());
            _inventoryWindow.Add(_invListView);
            BuildCraftingView();
            _inventoryWindow.Add(_craftView);
            // Поверх листа, чтобы тост не тонул под сеткой слотов.
            _inventoryWindow.Add(_invOrderToast);

            // Item removal is an explicit action inside the selected item's
            // detail card. The old full-width drag target looked interactive
            // but gave no clear affordance for how to begin or finish a drag.
            // Cards now open on click; "Drop" is shown only for a real item.
            // Wear/stow still keeps its compact drag gesture, so track it after
            // the pointer leaves the originating card.
            _invItemsPane.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_invDraggedId == null || !_invDraggedWorn) return;
                CompleteInventoryDrag(InventoryAction.Stow);
                evt.StopPropagation();
            });
            _root.RegisterCallback<PointerMoveEvent>(UpdateInventoryPointerGesture);
            _root.RegisterCallback<PointerUpEvent>(_ => ClearInventoryDrag());

            _invItemsPane.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var width = evt.newRect.width;
                var height = evt.newRect.height;
                if (Mathf.Abs(width - _invFitPaneWidth) < 0.5f &&
                    Mathf.Abs(height - _invFitPaneHeight) < 0.5f)
                {
                    return;
                }

                _invFitPaneWidth = width;
                _invFitPaneHeight = height;
                ResetInventoryDensity();
            });

            // ── the original item card, unchanged, as one popover ────────
            _invDetailView = new VisualElement();
            _invDetailView.style.position = Position.Absolute;
            _invDetailView.style.width = 440f;
            _invDetailView.style.maxHeight = 470f;
            _invDetailView.style.backgroundColor = Panel;
            SetBorder(_invDetailView, StrokeStrong, 1f);
            SetRadius(_invDetailView, 14f);
            _invDetailView.style.paddingLeft = 16f;
            _invDetailView.style.paddingRight = 16f;
            _invDetailView.style.paddingTop = 13f;
            _invDetailView.style.paddingBottom = 14f;
            _invDetailView.style.display = DisplayStyle.None;
            BuildInventoryDetail(_invDetailView);

            // Standard popover dismissal: an ordinary click inside the card
            // stays inside it, while PointerUp on any uncovered part of the
            // inventory window closes it. A real drag is allowed to bubble to
            // the root cleanup so releasing over the popover cannot stick it.
            _invDetailView.RegisterCallback<PointerUpEvent>(KeepInventoryDetailOpen);
            _invDetailView.RegisterCallback<PointerEnterEvent>(_ =>
                _invHoverDetailFocus = true);
            _invDetailView.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _invHoverDetailFocus = false;
                ScheduleInventoryHoverDetailHide();
            });
            _inventoryWindow.RegisterCallback<PointerUpEvent>(
                DismissInventoryDetailOnBackgroundClick);

            _root.Add(_inventoryWindow);
            // The item card intentionally lives on the root overlay instead
            // of inside the clipped inventory window. This lets it occupy one
            // stable place just beyond the doll-side edge without covering the
            // RenderTexture or being cut off by the window's rounded bounds.
            _root.Add(_invDetailView);
            _root.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                FitInventoryWindow();
                FitJournalWindow(); // §136
            });
            _invDetailView.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_invSelectedId != null)
                {
                    PositionInventoryDetail();
                }
            });
        }

        private VisualElement BuildOutfitLockToggle()
        {
            var button = new VisualElement { name = "inventory-outfit-lock" };
            button.style.height = 28f;
            button.style.marginRight = 12f;
            button.style.paddingLeft = 9f;
            button.style.paddingRight = 7f;
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.backgroundColor = IdentityGlass;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 9f);
            button.tooltip = Loc.Get("inv.outfit_lock.tooltip");

            _outfitLockLabel = new Label(Loc.Get("inv.outfit_lock"));
            _outfitLockLabel.style.fontSize = 11f;
            _outfitLockLabel.style.color = TextDim;
            _outfitLockLabel.style.marginRight = 8f;
            _outfitLockLabel.pickingMode = PickingMode.Ignore;
            button.Add(_outfitLockLabel);

            _outfitLockTrack = new VisualElement();
            _outfitLockTrack.style.width = 38f;
            _outfitLockTrack.style.height = 20f;
            _outfitLockTrack.style.backgroundColor = Track;
            SetRadius(_outfitLockTrack, 10f);
            _outfitLockTrack.pickingMode = PickingMode.Ignore;

            _outfitLockThumb = new VisualElement();
            _outfitLockThumb.style.position = Position.Absolute;
            _outfitLockThumb.style.left = 3f;
            _outfitLockThumb.style.top = 3f;
            _outfitLockThumb.style.width = 14f;
            _outfitLockThumb.style.height = 14f;
            _outfitLockThumb.style.backgroundColor = TextDim;
            SetRadius(_outfitLockThumb, 7f);
            _outfitLockThumb.pickingMode = PickingMode.Ignore;
            _outfitLockTrack.Add(_outfitLockThumb);
            button.Add(_outfitLockTrack);

            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleOutfitLock();
                evt.StopPropagation();
            });
            _outfitLockButton = button;
            return button;
        }

        private void ToggleOutfitLock()
        {
            if (_runner == null || !_runner.SupportsNpcCommands ||
                !_inventoryMutable || _inventoryActorId < 0)
            {
                return;
            }

            _runner.EnqueueCommand(new SetOutfitLockCommand(
                new HexLive.Simulation.Common.EntityId(_inventoryActorId),
                !_outfitLockedNow));
        }

        private void RefreshOutfitLockToggle(NpcSnapshot npc)
        {
            _outfitLockedNow = npc.OutfitLocked;
            if (_outfitLockButton == null)
            {
                return;
            }

            _outfitLockButton.style.opacity = _inventoryMutable ? 1f : 0.45f;
            _outfitLockButton.pickingMode = _inventoryMutable
                ? PickingMode.Position : PickingMode.Ignore;
            _outfitLockTrack.style.backgroundColor = npc.OutfitLocked
                ? new Color(Gold.r, Gold.g, Gold.b, 0.34f) : Track;
            _outfitLockThumb.style.left = npc.OutfitLocked ? 21f : 3f;
            _outfitLockThumb.style.backgroundColor = npc.OutfitLocked ? Gold : TextDim;
            _outfitLockLabel.style.color = npc.OutfitLocked ? Text : TextDim;
            SetBorderColor(_outfitLockButton, npc.OutfitLocked ? GoldDim : StrokeStrong);
        }

        private void BuildInventoryTabs()
        {
            _inventoryTabs = new VisualElement { name = "inventory-tabs" };
            _inventoryTabs.style.flexDirection = FlexDirection.Row;
            _inventoryTabs.style.height = 38f;
            _inventoryTabs.style.flexShrink = 0f;
            _inventoryTabs.style.marginBottom = 10f;
            _inventoryTabs.style.backgroundColor = Track;
            _inventoryTabs.style.paddingLeft = 3f;
            _inventoryTabs.style.paddingRight = 3f;
            _inventoryTabs.style.paddingTop = 3f;
            _inventoryTabs.style.paddingBottom = 3f;
            SetRadius(_inventoryTabs, 10f);

            _inventoryBackpackTab = BuildInventoryTab(
                "inventory-tab-backpack", "🎒", out _inventoryBackpackTabLabel);
            _inventoryBackpackTabLabel.text = Loc.Get("craft.tab.backpack");
            _inventoryBackpackTab.RegisterCallback<MouseDownEvent>(evt =>
            {
                SelectInventoryPage(craft: false, clothes: false);
                evt.StopPropagation();
            });
            _inventoryTabs.Add(_inventoryBackpackTab);

            _inventoryClothesTab = BuildInventoryTab(
                "inventory-tab-clothes", "👕", out _inventoryClothesTabLabel);
            _inventoryClothesTabLabel.text = Loc.Get("inv.tab.clothes");
            _inventoryClothesTab.RegisterCallback<MouseDownEvent>(evt =>
            {
                SelectInventoryPage(craft: false, clothes: true);
                evt.StopPropagation();
            });
            _inventoryTabs.Add(_inventoryClothesTab);

            _inventoryCraftTab = BuildInventoryTab(
                "inventory-tab-craft", "🛠", out _inventoryCraftTabLabel);
            _inventoryCraftTabLabel.text = Loc.Get("craft.tab.craft");
            _inventoryCraftTab.style.display = DisplayStyle.None;
            _inventoryCraftTab.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (_craftAvailable) SelectInventoryPage(craft: true);
                evt.StopPropagation();
            });
            _inventoryTabs.Add(_inventoryCraftTab);
            _inventoryWindow.Add(_inventoryTabs);
            RefreshInventoryTabStyle();
        }

        private static VisualElement BuildInventoryTab(
            string name, string glyph, out Label text)
        {
            var tab = new VisualElement { name = name };
            tab.style.flexBasis = 0f;
            tab.style.flexGrow = 1f;
            tab.style.height = 32f;
            tab.style.flexDirection = FlexDirection.Row;
            tab.style.alignItems = Align.Center;
            tab.style.justifyContent = Justify.Center;
            tab.style.marginLeft = 2f;
            tab.style.marginRight = 2f;
            SetRadius(tab, 8f);

            var icon = new Label(glyph);
            icon.style.fontSize = 14f;
            icon.style.marginRight = 7f;
            icon.pickingMode = PickingMode.Ignore;
            tab.Add(icon);

            text = new Label();
            text.style.fontSize = 12.5f;
            text.style.unityFontStyleAndWeight = FontStyle.Bold;
            text.pickingMode = PickingMode.Ignore;
            tab.Add(text);
            return tab;
        }

        private void BuildCraftingView()
        {
            _craftView = new VisualElement { name = "manual-crafting-view" };
            _craftView.style.flexDirection = FlexDirection.Row;
            _craftView.style.flexGrow = 1f;
            _craftView.style.minHeight = 0f;
            _craftView.style.display = DisplayStyle.None;

            _craftRecipeGrid = new VisualElement { name = "craft-recipe-grid" };
            _craftRecipeGrid.style.flexDirection = FlexDirection.Row;
            _craftRecipeGrid.style.flexWrap = Wrap.Wrap;
            _craftRecipeGrid.style.alignContent = Align.FlexStart;
            _craftRecipeGrid.style.flexGrow = 1f;
            _craftRecipeGrid.style.flexShrink = 1f;
            _craftRecipeGrid.style.minWidth = 0f;
            _craftRecipeGrid.style.paddingTop = 2f;
            _craftRecipeGrid.style.paddingLeft = 2f;
            _craftRecipeGrid.style.paddingRight = 8f;
            _craftRecipeGrid.style.overflow = Overflow.Hidden;
            _craftView.Add(_craftRecipeGrid);

            _craftDetail = new VisualElement { name = "craft-recipe-detail" };
            _craftDetail.style.width = 326f;
            _craftDetail.style.flexShrink = 0f;
            _craftDetail.style.paddingLeft = 14f;
            _craftDetail.style.paddingRight = 14f;
            _craftDetail.style.paddingTop = 13f;
            _craftDetail.style.paddingBottom = 13f;
            _craftDetail.style.backgroundColor = PanelMid;
            SetBorder(_craftDetail, StrokeStrong, 1f);
            SetRadius(_craftDetail, 13f);

            var hero = new VisualElement();
            hero.style.height = 82f;
            hero.style.flexShrink = 0f;
            hero.style.flexDirection = FlexDirection.Row;
            hero.style.alignItems = Align.Center;

            var iconFrame = new VisualElement();
            iconFrame.style.width = 72f;
            iconFrame.style.height = 72f;
            iconFrame.style.flexShrink = 0f;
            iconFrame.style.alignItems = Align.Center;
            iconFrame.style.justifyContent = Justify.Center;
            iconFrame.style.backgroundColor = Track;
            SetRadius(iconFrame, 11f);

            _craftDetailIcon = new Image { scaleMode = ScaleMode.ScaleToFit };
            _craftDetailIcon.style.width = 62f;
            _craftDetailIcon.style.height = 62f;
            _craftDetailIcon.pickingMode = PickingMode.Ignore;
            iconFrame.Add(_craftDetailIcon);

            _craftDetailEmoji = new Label();
            _craftDetailEmoji.style.fontSize = 42f;
            _craftDetailEmoji.style.unityTextAlign = TextAnchor.MiddleCenter;
            _craftDetailEmoji.pickingMode = PickingMode.Ignore;
            iconFrame.Add(_craftDetailEmoji);
            hero.Add(iconFrame);

            _craftDetailName = new Label();
            _craftDetailName.style.flexGrow = 1f;
            _craftDetailName.style.marginLeft = 13f;
            _craftDetailName.style.color = Text;
            _craftDetailName.style.fontSize = 17f;
            _craftDetailName.style.unityFontStyleAndWeight = FontStyle.Bold;
            _craftDetailName.style.whiteSpace = WhiteSpace.Normal;
            hero.Add(_craftDetailName);
            _craftDetail.Add(hero);

            _craftResourcesTitle = MakeInvSectionHeader(Loc.Get("craft.resources"));
            _craftResourcesTitle.style.marginTop = 8f;
            _craftDetail.Add(_craftResourcesTitle);

            _craftIngredients = new VisualElement { name = "craft-ingredients" };
            _craftIngredients.style.flexShrink = 0f;
            _craftDetail.Add(_craftIngredients);

            _craftStation = new Label();
            _craftStation.style.color = TextDim;
            _craftStation.style.fontSize = 12f;
            _craftStation.style.marginTop = 10f;
            _craftStation.style.whiteSpace = WhiteSpace.Normal;
            _craftDetail.Add(_craftStation);

            _craftProgress = new Label();
            _craftProgress.style.color = Gold;
            _craftProgress.style.fontSize = 11.5f;
            _craftProgress.style.marginTop = 5f;
            _craftDetail.Add(_craftProgress);

            _craftReason = new Label();
            _craftReason.style.color = Crit;
            _craftReason.style.fontSize = 11.5f;
            _craftReason.style.marginTop = 7f;
            _craftReason.style.whiteSpace = WhiteSpace.Normal;
            _craftReason.style.flexGrow = 1f;
            _craftDetail.Add(_craftReason);

            _craftAction = new VisualElement { name = "craft-action" };
            _craftAction.style.height = 42f;
            _craftAction.style.flexShrink = 0f;
            _craftAction.style.alignItems = Align.Center;
            _craftAction.style.justifyContent = Justify.Center;
            _craftAction.style.backgroundColor = GoldDim;
            SetBorder(_craftAction, Gold, 1f);
            SetRadius(_craftAction, 10f);
            _craftAction.RegisterCallback<MouseDownEvent>(evt =>
            {
                EnqueueSelectedCraft();
                evt.StopPropagation();
            });
            _craftActionLabel = new Label();
            _craftActionLabel.style.color = Text;
            _craftActionLabel.style.fontSize = 13f;
            _craftActionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _craftActionLabel.pickingMode = PickingMode.Ignore;
            _craftAction.Add(_craftActionLabel);
            _craftDetail.Add(_craftAction);
            _craftView.Add(_craftDetail);
        }

        private void SelectInventoryPage(bool craft, bool clothes = false)
        {
            _craftPage = craft && _craftAvailable;
            _clothesPage = !_craftPage && clothes;
            if (_invListView != null)
            {
                _invListView.style.display = _craftPage
                    ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (_craftView != null)
            {
                _craftView.style.display = _craftPage
                    ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_inventoryCapacity != null)
            {
                _inventoryCapacity.style.display = _craftPage
                    ? DisplayStyle.None : DisplayStyle.Flex;
            }

            HideItemDetail();
            _invSig = null;
            _refreshedTick = -1;
            _characterDollStage?.SetMode(_craftPage
                ? CharacterDollMode.Hidden : CharacterDollMode.Inventory);
            RefreshInventoryTabStyle();
            if (_craftPage)
            {
                RebuildCraftRecipeGrid();
                RefreshCraftDetail();
            }
        }

        private void RefreshInventoryTabStyle()
        {
            if (_inventoryBackpackTab == null || _inventoryClothesTab == null ||
                _inventoryCraftTab == null)
            {
                return;
            }

            _inventoryBackpackTab.style.backgroundColor = !_craftPage && !_clothesPage
                ? Raised : Color.clear;
            _inventoryClothesTab.style.backgroundColor = _clothesPage
                ? Raised : Color.clear;
            _inventoryCraftTab.style.backgroundColor = _craftPage
                ? Raised : Color.clear;
            _inventoryBackpackTabLabel.style.color = _craftPage || _clothesPage ? TextDim : Text;
            _inventoryClothesTabLabel.style.color = _clothesPage ? Text : TextDim;
            _inventoryCraftTabLabel.style.color = _craftPage ? Text : TextDim;
            SetBorder(_inventoryBackpackTab,
                !_craftPage && !_clothesPage ? StrokeStrong : Color.clear, 1f);
            SetBorder(_inventoryClothesTab, _clothesPage ? GoldDim : Color.clear, 1f);
            SetBorder(_inventoryCraftTab, _craftPage ? GoldDim : Color.clear, 1f);
        }

        private void RefreshCrafting(NpcSnapshot npc)
        {
            var eligible = _runner != null && _runner.SupportsNpcCommands &&
                NpcSelection.Count == 1 && npc.IsManualControl && npc.Health > 0f &&
                _runner.CanControlNpc(npc.Id);
            _craftOptions.Clear();
            _craftAvailable = eligible && _runner.TryGetCraftingOptions(
                new HexLive.Simulation.Common.EntityId(npc.Id.Value), _craftOptions);
            _inventoryCraftTab.style.display = _craftAvailable
                ? DisplayStyle.Flex : DisplayStyle.None;

            if (!_craftAvailable)
            {
                _craftSig = null;
                if (_craftPage) SelectInventoryPage(craft: false);
                return;
            }

            if (_selectedCraftGoal < 0 &&
                _selectedCraftGoalByNpc.TryGetValue(npc.Id.Value, out var remembered))
            {
                _selectedCraftGoal = remembered;
            }
            if (FindSelectedCraft() == null && _craftOptions.Count > 0)
            {
                _selectedCraftGoal = (int)_craftOptions[0].Goal;
                _selectedCraftGoalByNpc[npc.Id.Value] = _selectedCraftGoal;
            }

            var sig = CraftingSignature();
            if (sig == _craftSig)
            {
                return;
            }

            _craftSig = sig;
            if (_craftPage)
            {
                RebuildCraftRecipeGrid();
                RefreshCraftDetail();
            }
        }

        private string CraftingSignature()
        {
            var result = new System.Text.StringBuilder();
            result.Append(_selectedCraftGoal).Append('|');
            foreach (var option in _craftOptions)
            {
                result.Append((int)option.Goal).Append(':')
                    .Append(option.OutputDefinitionId).Append(':')
                    .Append(option.StationTag).Append(':')
                    .Append(option.StationObjectId?.Value ?? -1).Append(':')
                    .Append(option.ProjectObjectId?.Value ?? -1).Append(':')
                    .Append(option.WorkDone).Append('/').Append(option.WorkRequired).Append(':')
                    .Append(option.CanCraft ? '1' : '0')
                    .Append(option.IsActive ? '1' : '0')
                    .Append(option.IsResume ? '1' : '0').Append(':')
                    .Append((int)option.BlockReason);
                foreach (var ingredient in option.Ingredients)
                {
                    result.Append('[').Append(ingredient.DefinitionId).Append(':')
                        .Append(ingredient.Available).Append('/')
                        .Append(ingredient.Required).Append(']');
                }
                result.Append('|');
            }
            return result.ToString();
        }

        private void RebuildCraftRecipeGrid()
        {
            if (_craftRecipeGrid == null) return;
            _craftRecipeGrid.Clear();
            foreach (var option in _craftOptions)
            {
                var selected = (int)option.Goal == _selectedCraftGoal;
                var card = new VisualElement { name = "craft-recipe-card" };
                card.style.width = 104f;
                card.style.height = 122f;
                card.style.marginRight = 7f;
                card.style.marginBottom = 7f;
                card.style.paddingLeft = 6f;
                card.style.paddingRight = 6f;
                card.style.paddingTop = 6f;
                card.style.paddingBottom = 5f;
                card.style.alignItems = Align.Center;
                card.style.backgroundColor = selected ? Raised : Track;
                SetBorder(card, selected ? Gold : StrokeStrong, selected ? 2f : 1f);
                SetRadius(card, 11f);

                var sprite = LoadItemIcon(option.OutputDefinitionId);
                if (sprite != null)
                {
                    var image = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit };
                    image.style.width = 64f;
                    image.style.height = 64f;
                    image.pickingMode = PickingMode.Ignore;
                    card.Add(image);
                }
                else
                {
                    var outputDef = ResolveDef(option.OutputDefinitionId);
                    var outputInfo = ResolveItemInfo(option.OutputDefinitionId, outputDef);
                    var glyph = new Label(outputInfo.Emoji);
                    glyph.style.height = 64f;
                    glyph.style.fontSize = 40f;
                    glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
                    glyph.pickingMode = PickingMode.Ignore;
                    card.Add(glyph);
                }

                var definition = ResolveDef(option.OutputDefinitionId);
                var info = ResolveItemInfo(option.OutputDefinitionId, definition);
                var name = new Label(ItemName(definition, info));
                name.style.height = 38f;
                name.style.width = Length.Percent(100f);
                name.style.color = Text;
                name.style.fontSize = 10.5f;
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.unityTextAlign = TextAnchor.MiddleCenter;
                name.style.whiteSpace = WhiteSpace.Normal;
                name.pickingMode = PickingMode.Ignore;
                card.Add(name);

                if (option.IsActive)
                {
                    var active = new Label(Loc.Get("craft.working"));
                    active.style.position = Position.Absolute;
                    active.style.left = 4f;
                    active.style.right = 4f;
                    active.style.top = 4f;
                    active.style.height = 17f;
                    active.style.color = Text;
                    active.style.backgroundColor = GoldDim;
                    active.style.fontSize = 8.5f;
                    active.style.unityFontStyleAndWeight = FontStyle.Bold;
                    active.style.unityTextAlign = TextAnchor.MiddleCenter;
                    active.pickingMode = PickingMode.Ignore;
                    SetRadius(active, 5f);
                    card.Add(active);
                }

                card.RegisterCallback<MouseDownEvent>(evt =>
                {
                    _selectedCraftGoal = (int)option.Goal;
                    if (_inventoryActorId >= 0)
                    {
                        _selectedCraftGoalByNpc[_inventoryActorId] = _selectedCraftGoal;
                    }
                    _craftSig = CraftingSignature();
                    RebuildCraftRecipeGrid();
                    RefreshCraftDetail();
                    evt.StopPropagation();
                });
                _craftRecipeGrid.Add(card);
            }
        }

        private CraftRecipeOption FindSelectedCraft()
        {
            foreach (var option in _craftOptions)
            {
                if ((int)option.Goal == _selectedCraftGoal) return option;
            }
            return null;
        }

        private void RefreshCraftDetail()
        {
            var option = FindSelectedCraft();
            if (option == null || _craftDetail == null)
            {
                if (_craftDetail != null) _craftDetail.style.display = DisplayStyle.None;
                return;
            }
            _craftDetail.style.display = DisplayStyle.Flex;

            var definition = ResolveDef(option.OutputDefinitionId);
            var info = ResolveItemInfo(option.OutputDefinitionId, definition);
            var sprite = LoadItemIcon(option.OutputDefinitionId);
            _craftDetailIcon.sprite = sprite;
            _craftDetailIcon.style.display = sprite != null
                ? DisplayStyle.Flex : DisplayStyle.None;
            _craftDetailEmoji.text = info.Emoji;
            _craftDetailEmoji.style.display = sprite == null
                ? DisplayStyle.Flex : DisplayStyle.None;
            _craftDetailName.text = ItemName(definition, info);

            _craftIngredients.Clear();
            foreach (var ingredient in option.Ingredients)
            {
                _craftIngredients.Add(BuildCraftIngredientRow(ingredient));
            }

            var place = string.IsNullOrEmpty(option.StationTag)
                ? Loc.Get("craft.place.in_place")
                : Loc.Get("craft.station." + option.StationTag.ToLowerInvariant());
            _craftStation.text = $"{Loc.Get("craft.place")}: {place}";

            var hasProgress = option.IsResume && option.WorkRequired > 0;
            _craftProgress.style.display = hasProgress
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasProgress)
            {
                var percent = Mathf.Clamp(Mathf.RoundToInt(
                    option.WorkDone * 100f / option.WorkRequired), 0, 100);
                _craftProgress.text = $"{Loc.Get("craft.progress")}: {percent}%";
            }

            var blocked = !option.CanCraft && !option.IsActive;
            _craftReason.style.display = blocked ? DisplayStyle.Flex : DisplayStyle.None;
            _craftReason.text = blocked
                ? Loc.Get("craft.block." + option.BlockReason) : string.Empty;

            _craftActionLabel.text = option.IsActive
                ? Loc.Get("craft.working")
                : option.IsResume ? Loc.Get("craft.continue") : Loc.Get("craft.create");
            _craftAction.style.opacity = option.CanCraft ? 1f : 0.46f;
            _craftAction.pickingMode = option.CanCraft
                ? PickingMode.Position : PickingMode.Ignore;
            _craftAction.style.backgroundColor = option.CanCraft ? GoldDim : Track;
            SetBorder(_craftAction, option.CanCraft ? Gold : Stroke, 1f);
        }

        private VisualElement BuildCraftIngredientRow(CraftIngredientOption ingredient)
        {
            var enough = ingredient.Available >= ingredient.Required;
            var row = new VisualElement { name = "craft-ingredient-row" };
            row.style.height = 42f;
            row.style.flexShrink = 0f;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 5f;
            row.style.paddingLeft = 7f;
            row.style.paddingRight = 8f;
            row.style.backgroundColor = Track;
            SetBorder(row, enough
                ? new Color(Good.r, Good.g, Good.b, 0.32f)
                : new Color(Crit.r, Crit.g, Crit.b, 0.38f), 1f);
            SetRadius(row, 8f);

            var sprite = LoadItemIcon(ingredient.DefinitionId);
            if (sprite != null)
            {
                var image = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit };
                image.style.width = 30f;
                image.style.height = 30f;
                image.style.flexShrink = 0f;
                image.pickingMode = PickingMode.Ignore;
                row.Add(image);
            }
            else
            {
                var ingredientDef = ResolveDef(ingredient.DefinitionId);
                var ingredientInfo = ResolveItemInfo(ingredient.DefinitionId, ingredientDef);
                var glyph = new Label(ingredientInfo.Emoji);
                glyph.style.width = 30f;
                glyph.style.fontSize = 21f;
                glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
                glyph.pickingMode = PickingMode.Ignore;
                row.Add(glyph);
            }

            var def = ResolveDef(ingredient.DefinitionId);
            var info = ResolveItemInfo(ingredient.DefinitionId, def);
            var name = new Label(ItemName(def, info));
            name.style.flexGrow = 1f;
            name.style.marginLeft = 8f;
            name.style.color = TextDim;
            name.style.fontSize = 11.5f;
            name.style.whiteSpace = WhiteSpace.Normal;
            row.Add(name);

            var count = new Label(
                $"{(enough ? "✓" : "✕")} {ingredient.Available} / {ingredient.Required}");
            count.style.flexShrink = 0f;
            count.style.marginLeft = 8f;
            count.style.color = enough ? Good : Crit;
            count.style.fontSize = 12.5f;
            count.style.unityFontStyleAndWeight = FontStyle.Bold;
            count.pickingMode = PickingMode.Ignore;
            row.Add(count);
            return row;
        }

        private void EnqueueSelectedCraft()
        {
            var option = FindSelectedCraft();
            if (!(_craftAvailable && option?.CanCraft == true) ||
                _runner == null || _inventoryActorId < 0)
            {
                return;
            }

            _runner.EnqueueCommand(new CraftItemCommand(
                new HexLive.Simulation.Common.EntityId(_inventoryActorId), option.Goal));
            _craftSig = null;
            _refreshedTick = -1;
        }

        private void BuildInventoryWearLayerButtons()
        {
            _invLayerButtons.Clear();
            _invLayerIcons.Clear();
            foreach (var layer in new[]
                     {
                         VisualWearLayer.Underwear,
                         VisualWearLayer.Wear,
                         VisualWearLayer.Outerwear,
                         VisualWearLayer.Bags
                     })
            {
                var button = new VisualElement { name = $"inventory-layer-{layer}" };
                button.style.position = Position.Relative;
                button.style.flexBasis = 0f;
                button.style.flexGrow = 1f;
                button.style.maxWidth = 54f;
                button.style.minWidth = 0f;
                button.style.height = 36f;
                button.style.flexShrink = 1f;
                button.style.marginLeft = 2f;
                button.style.marginRight = 2f;
                button.style.alignItems = Align.Center;
                button.style.justifyContent = Justify.Center;
                SetRadius(button, 9f);

                var icon = new VectorIcon(InventoryLayerIconKind(layer), TextDim);
                icon.style.width = 25f;
                icon.style.height = 25f;
                button.Add(icon);

                button.tooltip = Loc.Get(InventoryLayerLocKey(layer));
                button.RegisterCallback<MouseDownEvent>(evt =>
                {
                    SelectInventoryWearLayer(layer);
                    evt.StopPropagation();
                });
                _invLayerButtons[layer] = button;
                _invLayerIcons[layer] = icon;
                _invLayerBar.Add(button);
            }

            RefreshInventoryWearLayerButtonStyle();
        }

        private static VectorIcon.Kind InventoryLayerIconKind(VisualWearLayer layer) => layer switch
        {
            VisualWearLayer.Underwear => VectorIcon.Kind.Underwear,
            VisualWearLayer.Wear => VectorIcon.Kind.Shirt,
            VisualWearLayer.Outerwear => VectorIcon.Kind.Coat,
            _ => VectorIcon.Kind.Bag
        };

        private static string InventoryLayerLocKey(VisualWearLayer layer) => layer switch
        {
            VisualWearLayer.Underwear => "layer.underwear",
            VisualWearLayer.Wear => "layer.wear",
            VisualWearLayer.Outerwear => "layer.outerwear",
            _ => "layer.bags"
        };

        private void SelectInventoryWearLayer(VisualWearLayer layer)
        {
            if (_invVisibleWearLayer == layer)
            {
                return;
            }

            _invVisibleWearLayer = layer;
            HideItemDetail();
            RefreshInventoryWearLayerButtonStyle();
            _characterDollStage?.SetVisibleWearLayer(layer);
        }

        private void RefreshInventoryWearLayerButtonStyle()
        {
            foreach (var pair in _invLayerButtons)
            {
                var active = pair.Key == _invVisibleWearLayer;
                pair.Value.style.backgroundColor = active
                    ? new Color(0.10f, 0.36f, 0.39f, 0.94f)
                    : new Color(0.035f, 0.055f, 0.064f, 0.82f);
                pair.Value.style.opacity = active ? 1f : 0.72f;
                SetBorder(pair.Value, active
                    ? new Color(0.20f, 0.88f, 0.86f, 0.95f)
                    : Stroke, active ? 1.5f : 1f);
                if (_invLayerIcons.TryGetValue(pair.Key, out var icon))
                {
                    icon.SetColor(active ? Color.white : TextDim);
                }
            }
        }

        private void KeepInventoryDetailOpen(PointerUpEvent evt)
        {
            if (_invDraggedId == null && !_invPointerMoved)
            {
                evt.StopPropagation();
            }
        }

        private void DismissInventoryDetailOnBackgroundClick(PointerUpEvent evt)
        {
            if (evt.button != 0 || _invSelectedId == null ||
                _invDraggedId != null || _invPointerMoved || _invPreviewRotated)
            {
                return;
            }

            HideItemDetail();
        }

        private void BuildInventoryDetail(VisualElement parent)
        {
            // Kept visually identical to the pre-layout inventory card.
            var back = new VisualElement();
            back.style.flexDirection = FlexDirection.Row;
            back.style.alignItems = Align.Center;
            back.style.alignSelf = Align.FlexStart;
            back.style.marginBottom = 12f;
            back.RegisterCallback<MouseDownEvent>(evt =>
            {
                HideItemDetail();
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

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 12f;
            _invPrimaryAction = InventoryActionButton(
                () => EnqueueInventoryAction(_invSelectedWorn
                    ? InventoryAction.Stow : InventoryAction.Wear));
            _invPrimaryActionLabel = new Label();
            _invPrimaryActionLabel.style.color = Text;
            _invPrimaryActionLabel.style.fontSize = 13f;
            _invPrimaryActionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _invPrimaryActionLabel.pickingMode = PickingMode.Ignore;
            _invPrimaryAction.Add(_invPrimaryActionLabel);
            actions.Add(_invPrimaryAction);

            _invDropAction = InventoryActionButton(
                () => EnqueueInventoryAction(InventoryAction.Drop));
            _invDropAction.style.marginLeft = 8f;
            _invDropActionLabel = new Label(Loc.Get("inv.action.drop"));
            _invDropActionLabel.style.color = Crit;
            _invDropActionLabel.style.fontSize = 13f;
            _invDropActionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _invDropActionLabel.pickingMode = PickingMode.Ignore;
            _invDropAction.Add(_invDropActionLabel);
            actions.Add(_invDropAction);
            parent.Add(actions);

            _invReadOnlyLabel = new Label(Loc.Get("inv.readonly"));
            _invReadOnlyLabel.style.color = TextMute;
            _invReadOnlyLabel.style.fontSize = 12f;
            _invReadOnlyLabel.style.marginTop = 9f;
            _invReadOnlyLabel.style.display = DisplayStyle.None;
            parent.Add(_invReadOnlyLabel);
        }

        private static VisualElement InventoryActionButton(Action action)
        {
            var button = new VisualElement();
            button.style.height = 34f;
            button.style.minWidth = 104f;
            button.style.paddingLeft = 12f;
            button.style.paddingRight = 12f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.backgroundColor = Raised;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 8f);
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                action?.Invoke();
                evt.StopPropagation();
            });
            return button;
        }

        private void RegisterInventoryPreviewInput()
        {
            _invPreviewView.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                // Once the player starts manipulating the doll, the clothing
                // hover card must get out of the way immediately.
                if (_invSelectedWorn)
                {
                    _invHoverDetailFocus = false;
                    HideItemDetail();
                }
                ClearInventoryDrag();
                _invPreviewDragging = true;
                _invPreviewPointerId = evt.pointerId;
                _invPreviewPointerPosition = new Vector2(evt.position.x, evt.position.y);
                _invPreviewPointerDownPosition = _invPreviewPointerPosition;
                _invPreviewRotated = false;
                _invPreviewView.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            _invPreviewView.RegisterCallback<PointerEnterEvent>(_ =>
                _invHoverDetailFocus = true);
            _invPreviewView.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_invPreviewDragging && evt.pointerId == _invPreviewPointerId &&
                    _invPreviewView.HasPointerCapture(evt.pointerId))
                {
                    var position = new Vector2(evt.position.x, evt.position.y);
                    if (!_invPreviewRotated &&
                        (position - _invPreviewPointerDownPosition).sqrMagnitude <
                        InventoryDragThreshold * InventoryDragThreshold)
                    {
                        _invPreviewPointerPosition = position;
                        return;
                    }

                    _invPreviewRotated = true;
                    _characterDollStage?.Rotate((position.x - _invPreviewPointerPosition.x) * -0.55f);
                    _invPreviewPointerPosition = position;
                    return;
                }

                HoverInventoryPreview(new Vector2(evt.localPosition.x, evt.localPosition.y));
            });
            _invPreviewView.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_invDraggedId != null)
                {
                    if (!_invDraggedWorn) CompleteInventoryDrag(InventoryAction.Wear);
                    else ClearInventoryDrag();
                    evt.StopPropagation();
                    return;
                }
                if (evt.pointerId != _invPreviewPointerId)
                {
                    return;
                }

                if (!_invPreviewRotated)
                {
                    var localPosition = new Vector2(evt.localPosition.x, evt.localPosition.y);
                    if (_characterDollStage == null ||
                        !_characterDollStage.TryPickWorn(
                            localPosition,
                            new Vector2(
                                _invPreviewView.resolvedStyle.width,
                                _invPreviewView.resolvedStyle.height),
                            out _))
                    {
                        HideItemDetail();
                    }
                }

                _invPreviewDragging = false;
                _invPreviewPointerId = -1;
                _invPreviewRotated = false;
                if (_invPreviewView.HasPointerCapture(evt.pointerId))
                {
                    _invPreviewView.ReleasePointer(evt.pointerId);
                }
                evt.StopPropagation();
            });
            _invPreviewView.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (!_invPreviewDragging)
                {
                    _invHoverDetailFocus = false;
                    SetHoveredWorn(string.Empty);
                    ScheduleInventoryHoverDetailHide();
                }
            });
        }

        private void HoverInventoryPreview(Vector2 localPosition)
        {
            if (_characterDollStage != null &&
                _characterDollStage.TryPickWorn(
                    localPosition,
                    new Vector2(_invPreviewView.resolvedStyle.width, _invPreviewView.resolvedStyle.height),
                    out var wornId))
            {
                SetHoveredWorn(wornId);
                _invHoverDetailFocus = true;
                var changedItem = !string.Equals(
                    _invSelectedId, wornId, StringComparison.Ordinal) || !_invSelectedWorn;
                _invPreviewHoverAnchor.style.left = localPosition.x - 1f;
                _invPreviewHoverAnchor.style.top = localPosition.y - 1f;
                if (changedItem)
                {
                    ShowItemDetail(
                        wornId, true, _invWornDurability, _invCarriedWater,
                        _invCarriedStacks, _invWornWetness, _invWornDirtiness,
                        _invPreviewHoverAnchor, -1, InventoryDetailPlacement.Doll);
                }
                return;
            }

            SetHoveredWorn(string.Empty);
            _invHoverDetailFocus = false;
            ScheduleInventoryHoverDetailHide();
        }

        private void ScheduleInventoryHoverDetailHide()
        {
            _invDetailView?.schedule.Execute(() =>
            {
                if (!_invHoverDetailFocus && !_invPreviewDragging &&
                    _invSelectedId != null && _invSelectedWorn)
                {
                    HideItemDetail();
                }
            }).StartingIn(80);
        }

        private void ToggleInventory()
        {
            if (_inventoryOpen)
            {
                CloseInventory();
            }
            else
            {
                CloseHealth(); // the floating windows share the same spot
                CloseJournal(); // §136
                _inventoryOpen = true;
                _invVisibleWearLayer = VisualWearLayer.Bags;
                _invSig = null; // force a rebuild on the next refresh
                _craftSig = null;
                _craftPage = false;
                FitInventoryWindow();
                _inventoryWindow.style.display = DisplayStyle.Flex;
                SelectInventoryPage(craft: false, clothes: false);
                _characterDollStage?.SetMode(CharacterDollMode.Inventory);
                _characterDollStage?.SetVisibleWearLayer(_invVisibleWearLayer);
                RefreshInventoryWearLayerButtonStyle();
                HideItemDetail();
                _refreshedTick = -1; // pull a fresh snapshot into the window now
            }
        }

        private void CloseInventory()
        {
            _inventoryOpen = false;
            _craftPage = false;
            _clothesPage = false;
            _invHoverDetailFocus = false;
            _invSelectedId = null;
            _invDetailAnchor = null;
            // Закрытое окно обрывает пару: клик до закрытия и клик после — не
            // двойной клик, даже если между ними меньше порога.
            _invDoubleClick.Reset();
            if (!_healthOpen)
            {
                _characterDollStage?.SetMode(CharacterDollMode.Hidden);
            }
            SetHoveredWorn(string.Empty);
            if (_inventoryWindow != null)
            {
                _inventoryWindow.style.display = DisplayStyle.None;
            }

            if (_invListView != null) _invListView.style.display = DisplayStyle.Flex;
            if (_craftView != null) _craftView.style.display = DisplayStyle.None;
            if (_inventoryCapacity != null)
                _inventoryCapacity.style.display = DisplayStyle.Flex;
            RefreshInventoryTabStyle();

            if (_invDetailView != null)
            {
                _invDetailView.style.display = DisplayStyle.None;
            }
        }

        private void FitInventoryWindow()
        {
            if (_root == null || _inventoryWindow == null || _root.layout.width < 1f)
            {
                return;
            }

            var width = Mathf.Min(
                InventoryWindowMaxWidth, Mathf.Max(1f, _root.layout.width - 32f));
            var availableHeight = _root.layout.height - InventoryWindowBottom - 16f;
            var height = Mathf.Min(
                InventoryWindowMaxHeight, Mathf.Max(300f, availableHeight));
            _inventoryWindow.style.width = width;
            _inventoryWindow.style.height = height;
            _inventoryWindow.style.left = Mathf.Max(8f, (_root.layout.width - width) * 0.5f);
            ResetInventoryDensity();
        }

        /// <summary>
        /// Gives the inventory preview the doll RenderTexture's exact aspect,
        /// sized by the row's HEIGHT. Its pane then hugs that width, so the
        /// portrait has no dead field beside it and Contain has nothing to
        /// letterbox. Reading the pane's own width here would be circular —
        /// the pane is as wide as this box.
        /// </summary>
        private void FitInventoryDollViewport()
        {
            if (_invListView == null || _invPreviewView == null)
            {
                return;
            }

            var room = _invListView.contentRect;
            if (room.width < 1f || room.height < 1f ||
                float.IsNaN(room.width) || float.IsNaN(room.height))
            {
                return;
            }

            // No floor: a box taller than the room it has would simply overflow
            // its clipped parent, and the clipping takes the head and the feet.
            // The width cap keeps a very tall window from starving the item
            // list, since the portrait is otherwise sized by height alone.
            var previewRoomHeight = Mathf.Max(
                1f, room.height - InventoryLayerBarHeight - InventoryLayerBarGap);
            var width = Mathf.Min(
                previewRoomHeight / DollAspectHeight,
                room.width * MaxDollPaneWidthFraction);
            var height = width * DollAspectHeight;
            // Writing a size from inside a geometry callback re-triggers that
            // callback; only an actual change may be written, or the window
            // relayouts itself forever.
            if (Mathf.Abs(_invPreviewView.resolvedStyle.height - height) < 0.5f &&
                Mathf.Abs(_invPreviewView.resolvedStyle.width - width) < 0.5f)
            {
                return;
            }

            _invPreviewView.style.height = height;
            _invPreviewView.style.width = width;
            _invDollPane.style.width = width;
            _invLayerBar.style.width = width;
        }

        private void ResetInventoryDensity()
        {
            if (_invItemsPane == null || _invItemsContent == null)
            {
                return;
            }

            _invDensityTier = 0;
            ApplyInventoryDensity();
            _invItemsPane.schedule.Execute(FitInventoryContent);
        }

        private void FitInventoryContent()
        {
            if (_invItemsPane == null || _invItemsContent == null ||
                _invItemsPane.resolvedStyle.display == DisplayStyle.None)
            {
                return;
            }

            var available = _invItemsPane.contentRect.height;
            var required = _invItemsContent.layout.height;
            if ((float.IsNaN(available) || float.IsNaN(required) || available < 1f) &&
                _invDensityTier == 0)
            {
                return;
            }

            if (required <= available + 0.5f ||
                _invDensityTier >= InventoryCellSizes.Length - 1)
            {
                return;
            }

            _invDensityTier++;
            ApplyInventoryDensity();
            _invItemsPane.schedule.Execute(FitInventoryContent);
        }

        private void ApplyInventoryDensity()
        {
            var tier = Mathf.Clamp(_invDensityTier, 0, InventoryCellSizes.Length - 1);
            var size = InventoryCellSizes[tier];
            var gap = InventoryGaps[tier];

            foreach (var cell in _invSlotCells)
            {
                cell.style.width = size;
                cell.style.height = size;
                cell.style.marginRight = gap;
                cell.style.marginBottom = gap;
                SetRadius(cell, Mathf.Max(9f, 15f - tier));
            }

            var iconSize = Mathf.Max(46f, size - 14f);
            foreach (var icon in _invSlotIcons)
            {
                icon.style.width = iconSize;
                icon.style.height = iconSize;
            }
            foreach (var glyph in _invSlotGlyphs)
            {
                glyph.style.fontSize = Mathf.Max(32f, size * 0.46f);
            }
            foreach (var badge in _invSlotBadges)
            {
                badge.style.fontSize = Mathf.Max(10f, size * 0.12f);
                badge.style.maxWidth = Mathf.Max(42f, size - 12f);
            }
        }

        // ── limb health window (spec §57) ─────────────────────────────────

        // Floating window over the identity column: the rotating body doll
        // (per-zone green→yellow→red mesh from CharacterDollStage) beside a
        // per-limb readout list. Same chrome as the inventory window.
        private void BuildHealthWindow()
        {
            _healthWindow = new VisualElement();
            _healthWindow.style.position = Position.Absolute;
            _healthWindow.style.left = 20f;
            _healthWindow.style.bottom = InventoryWindowBottom;
            _healthWindow.style.width = HealthWindowWidth;
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
            body.style.alignItems = Align.Stretch;

            // Doll viewport. Its box has the exact aspect of the stage
            // RenderTexture (512x896): any other aspect makes Contain pillarbox the
            // portrait and the doll stops filling the frame it was drawn for.
            _healthDollImage = new VisualElement();
            _healthDollImage.style.width = DollViewportWidth;
            _healthDollImage.style.height = DollViewportWidth * DollAspectHeight;
            _healthDollImage.style.flexShrink = 0f;
            _healthDollImage.style.position = Position.Relative;
            _healthDollImage.style.backgroundColor = Track;
            _healthDollImage.style.backgroundSize = new BackgroundSize(
                BackgroundSizeType.Contain);
            SetBorder(_healthDollImage, StrokeStrong, 1f);
            SetRadius(_healthDollImage, 10f);
            _healthDollImage.style.overflow = Overflow.Hidden;

            _healthDollError = new Label(Loc.Get("health.doll_unavailable"));
            _healthDollError.style.position = Position.Absolute;
            _healthDollError.style.left = 9f;
            _healthDollError.style.right = 9f;
            _healthDollError.style.bottom = 9f;
            _healthDollError.style.paddingLeft = 7f;
            _healthDollError.style.paddingRight = 7f;
            _healthDollError.style.paddingTop = 5f;
            _healthDollError.style.paddingBottom = 5f;
            _healthDollError.style.backgroundColor = new Color(0.16f, 0.06f, 0.06f, 0.92f);
            _healthDollError.style.color = Warn;
            _healthDollError.style.fontSize = 10f;
            _healthDollError.style.whiteSpace = WhiteSpace.Normal;
            _healthDollError.style.unityTextAlign = TextAnchor.MiddleCenter;
            _healthDollError.style.display = DisplayStyle.None;
            _healthDollError.pickingMode = PickingMode.Ignore;
            SetRadius(_healthDollError, 7f);
            _healthDollImage.Add(_healthDollError);
            body.Add(_healthDollImage);

            // Per-limb readout rows. The column deliberately matches the
            // portrait height: after the RenderTexture grew upward, centering
            // the old compact list left a large dead band beside the doll.
            // Equal flex cards now make both sides read as one aligned block.
            var list = new VisualElement();
            list.style.flexGrow = 1f;
            list.style.minWidth = 0f;
            list.style.height = DollViewportWidth * DollAspectHeight;
            list.style.marginLeft = HealthColumnGap;

            _zoneRows.Clear();
            for (var zoneIndex = 0; zoneIndex < CharacterDollStage.ZoneOrder.Length; zoneIndex++)
            {
                var zone = CharacterDollStage.ZoneOrder[zoneIndex];
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.flexGrow = 1f;
                row.style.flexBasis = 0f;
                row.style.minHeight = 0f;
                row.style.paddingLeft = 9f;
                row.style.paddingRight = 9f;
                row.style.backgroundColor = PanelMid;
                if (zoneIndex + 1 < CharacterDollStage.ZoneOrder.Length)
                {
                    row.style.marginBottom = HealthZoneRowGap;
                }
                SetBorder(row, Stroke, 1f);
                SetRadius(row, 8f);

                var dot = new VisualElement();
                dot.style.width = 8f;
                dot.style.height = 8f;
                dot.style.flexShrink = 0f;
                SetRadius(dot, 4f);
                dot.style.marginRight = 8f;
                row.Add(dot);

                var name = new Label();
                name.style.color = Text;
                name.style.fontSize = 13f;
                name.style.flexGrow = 1f;
                name.style.minWidth = 0f;
                name.style.overflow = Overflow.Hidden;
                name.style.textOverflow = TextOverflow.Ellipsis;
                name.style.whiteSpace = WhiteSpace.NoWrap;
                name.pickingMode = PickingMode.Ignore;
                row.Add(name);

                // Worn-armor absorption (hidden while the zone is bare).
                var armor = new Label();
                armor.style.color = Comfort;
                armor.style.fontSize = 12f;
                armor.style.flexShrink = 0f;
                armor.style.marginRight = 8f;
                armor.style.display = DisplayStyle.None;
                armor.style.unityTextAlign = TextAnchor.MiddleRight;
                armor.pickingMode = PickingMode.Ignore;
                row.Add(armor);

                var value = new Label();
                value.style.color = TextDim;
                value.style.fontSize = 12.5f;
                value.style.flexShrink = 0f;
                value.style.minWidth = 44f;
                value.style.unityTextAlign = TextAnchor.MiddleRight;
                value.pickingMode = PickingMode.Ignore;
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

            CloseInventory(); // the floating windows share the same spot
            CloseJournal();   // §136
            _healthOpen = true;
            _healthWindow.style.display = DisplayStyle.Flex;
            _characterDollStage?.SetMode(CharacterDollMode.Health);
            _refreshedTick = -1; // pull a fresh snapshot into the window now
        }

        private void CloseHealth()
        {
            _healthOpen = false;
            if (_healthWindow != null)
            {
                _healthWindow.style.display = DisplayStyle.None;
            }

            if (!_inventoryOpen)
            {
                _characterDollStage?.SetMode(CharacterDollMode.Hidden);
            }
        }

        // Called from Refresh() while the window is open: feed the doll stage
        // and rebuild the readout rows off the snapshot's zone lists.
        private void RefreshHealth(NpcSnapshot npc)
        {
            if (!_healthOpen)
            {
                return;
            }

            if (_characterDollStage != null)
            {
                _characterDollStage.SetTarget(npc.Id.Value, npc.ActorMesh, npc.WornItems);
                _characterDollStage.SetZones(
                    npc.BodyParts, npc.SeveredParts, npc.BandagedZones,
                    npc.BodyPartConditions);
                var tex = _characterDollStage.Texture;
                if (tex != null)
                {
                    _healthDollImage.style.backgroundImage =
                        new StyleBackground(Background.FromRenderTexture(tex));
                }
                if (_healthDollError != null)
                {
                    _healthDollError.style.display =
                        _characterDollStage.HealthOverlayAvailable ||
                        string.IsNullOrEmpty(_characterDollStage.BuildError)
                            ? DisplayStyle.None
                            : DisplayStyle.Flex;
                }
            }

            foreach (var binding in _zoneRows)
            {
                var hp = 1f;
                BodyPartConditionSnapshot condition = null;
                foreach (var typed in npc.BodyPartConditions)
                {
                    if (typed.Part.ToString() == binding.Zone)
                    {
                        condition = typed;
                        hp = typed.Health;
                        break;
                    }
                }

                // v11/local fallback while an older snapshot source is still
                // connected. v12 always takes the typed branch above.
                foreach (var entry in npc.BodyParts)
                {
                    if (condition == null && entry.StartsWith(binding.Zone) &&
                        entry.Length > binding.Zone.Length && entry[binding.Zone.Length] == '=')
                    {
                        float.TryParse(entry[(binding.Zone.Length + 1)..],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out hp);
                        break;
                    }
                }

                // Worn-armor absorption for this zone ("Zone=0.35").
                var armor = condition?.Armor ?? 0f;
                foreach (var entry in npc.PartArmor)
                {
                    if (condition == null && entry.StartsWith(binding.Zone) &&
                        entry.Length > binding.Zone.Length && entry[binding.Zone.Length] == '=')
                    {
                        float.TryParse(entry[(binding.Zone.Length + 1)..],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out armor);
                        break;
                    }
                }

                var severed = condition?.Severed ?? npc.SeveredParts.Contains(binding.Zone);
                var bandaged = !string.IsNullOrEmpty(condition?.BandageKind);
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
                var cutDamage = 0f;
                var bleeding = false;
                foreach (var wound in npc.OpenWounds)
                {
                    if (wound.Part.ToString() != binding.Zone || wound.Heal01 >= 0.999f)
                    {
                        continue;
                    }

                    openWounds++;
                    cutDamage += wound.Severity * (1f - wound.Heal01);
                    bleeding |= !wound.Stabilized && wound.Clot01 < 0.999f;
                }

                if (npc.OpenWounds.Count == 0)
                {
                    foreach (var entry in npc.Wounds)
                    {
                        // "Zone|Seed|Heal01" legacy fallback.
                        var parts = entry.Split('|');
                        if (parts.Length >= 3 && parts[0] == binding.Zone &&
                            float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var heal) &&
                            heal < 0.999f)
                        {
                            openWounds++;
                        }
                    }
                }

                binding.Name.text = Loc.Get($"zone.{binding.Zone}");
                var critical = condition?.CriticalTrauma ?? 0f;
                var prosthetic = condition?.Prosthetic;
                var prostheticCondition01 = prosthetic != null && prosthetic.MaxCondition > 0f
                    ? Mathf.Clamp01(prosthetic.Condition / prosthetic.MaxCondition)
                    : 0f;
                binding.Dot.style.backgroundColor = prosthetic != null
                    ? CharacterDollStage.StatusColor(prostheticCondition01, false)
                    : CharacterDollStage.StatusColor(hp - critical, severed);

                // 🛡 only when something actually covers the zone; a severed
                // limb has nothing left to protect.
                var showArmor = !severed && armor > 0.005f;
                binding.Armor.style.display = showArmor ? DisplayStyle.Flex : DisplayStyle.None;
                if (showArmor)
                {
                    binding.Armor.text = $"🛡 {Mathf.RoundToInt(armor * 100f)}%";
                }

                if (severed && prosthetic == null)
                {
                    binding.Value.text = Loc.Get("health.severed");
                    binding.Value.style.color = Crit;
                    binding.Value.tooltip = Loc.Get("health.severed");
                }
                else if (prosthetic != null)
                {
                    var currentHp = Mathf.Max(0, Mathf.RoundToInt(prosthetic.Condition * 100f));
                    var maxHp = Mathf.Max(0, Mathf.RoundToInt(prosthetic.MaxCondition * 100f));
                    var baseFunction = Mathf.Clamp01(prosthetic.Function);
                    var effectiveFunction = baseFunction * prostheticCondition01;
                    binding.Value.text = $"🦾 {currentHp}/{maxHp} HP · " +
                                         $"{Mathf.RoundToInt(effectiveFunction * 100f)}%";
                    binding.Value.style.color = prostheticCondition01 < 0.25f ? Crit : TextDim;

                    var def = ResolveDef(prosthetic.DefinitionId);
                    var info = ResolveItemInfo(prosthetic.DefinitionId, def);
                    binding.Value.tooltip =
                        $"{ItemName(def, info)}\n" +
                        $"{Loc.Get("health.prosthetic_hp")}: {currentHp}/{maxHp} HP " +
                        $"({Mathf.RoundToInt(prostheticCondition01 * 100f)}%)\n" +
                        $"{Loc.Get("health.prosthetic_base_function")}: " +
                        $"{Mathf.RoundToInt(baseFunction * 100f)}%\n" +
                        $"{Loc.Get("health.prosthetic_effective_function")}: " +
                        $"{Mathf.RoundToInt(effectiveFunction * 100f)}%\n" +
                        ItemDesc(def, info);
                }
                else
                {
                    var text = $"{Mathf.RoundToInt(Mathf.Clamp01(hp) * 100f)}%";
                    if (critical > 0f)
                    {
                        text += $" / −{Mathf.RoundToInt(Mathf.Clamp01(critical) * 100f)}%";
                    }
                    if (bandaged)
                    {
                        text = $"🩹 {text}";
                    }

                    if (bleeding)
                    {
                        text = $"🩸 {text}";
                    }

                    if (condition?.SplintSupport > 0f)
                    {
                        text += $" · 🩼{Mathf.RoundToInt(condition.SplintSupport * 100f)}";
                    }

                    binding.Value.text = text;
                    binding.Value.style.color = critical > 0f ? Crit :
                        bleeding || hp < 0.5f ? Text : TextDim;
                    var blunt = condition?.BluntDamage ?? 0f;
                    binding.Value.tooltip =
                        $"{Loc.Get("inv.cut")}: {Mathf.RoundToInt(cutDamage * 100f)}% · " +
                        $"{Loc.Get("inv.blunt")}: {Mathf.RoundToInt(blunt * 100f)}%";
                }
            }

        }

        private void HideItemDetail()
        {
            _invSelectedId = null;
            _invDetailAnchor = null;
            _invDetailPlacement = InventoryDetailPlacement.Item;
            SetHoveredWorn(string.Empty);
            if (_invDetailView != null)
            {
                _invDetailView.style.display = DisplayStyle.None;
            }
        }

        private void SetHoveredWorn(string itemId)
        {
            itemId ??= string.Empty;
            _invHoveredWornId = itemId;
        }

        private void PositionInventoryDetail()
        {
            if (_root == null || _invDetailView == null || _inventoryWindow == null ||
                _invDetailView.resolvedStyle.display == DisplayStyle.None)
            {
                return;
            }

            var root = _root.worldBound;
            var window = _inventoryWindow.worldBound;
            if (root.width < 1f || window.width < 1f)
            {
                return;
            }

            var allowedHeight = Mathf.Min(470f, Mathf.Max(1f, root.height - 16f));
            _invDetailView.style.maxHeight = allowedHeight;
            var detailWidth = _invDetailView.resolvedStyle.width;
            var detailHeight = _invDetailView.resolvedStyle.height;
            if (float.IsNaN(detailWidth) || detailWidth < 1f) detailWidth = 440f;
            if (float.IsNaN(detailHeight) || detailHeight < 1f) detailHeight = 470f;
            detailHeight = Mathf.Min(detailHeight, allowedHeight);

            float x;
            float y;
            if (_invDetailPlacement == InventoryDetailPlacement.Item &&
                _invDetailAnchor != null)
            {
                // A clicked grid item owns the card spatially. Prefer its right
                // edge, fall back to the left, then clamp only when neither side
                // can contain the legacy card (small resolutions).
                var anchor = _invDetailAnchor.worldBound;
                var right = anchor.xMax - root.xMin + 10f;
                var left = anchor.xMin - root.xMin - detailWidth - 10f;
                var rightFits = right + detailWidth <= root.width - 8f;
                var leftFits = left >= 8f;
                if (rightFits)
                {
                    x = right;
                }
                else if (leftFits)
                {
                    x = left;
                }
                else
                {
                    var roomRight = root.xMax - anchor.xMax;
                    var roomLeft = anchor.xMin - root.xMin;
                    x = roomRight >= roomLeft ? right : left;
                    x = Mathf.Clamp(x, 8f, Mathf.Max(8f, root.width - detailWidth - 8f));
                }

                y = anchor.center.y - root.yMin - detailHeight * 0.5f;
            }
            else
            {
                // A doll hover has one predictable resting place: just beyond
                // the inventory window's doll-side edge. It never chases the
                // pointer across the body and therefore does not obstruct drag.
                x = window.xMax - root.xMin + 12f;
                x = Mathf.Min(x, Mathf.Max(8f, root.width - detailWidth - 8f));
                x = Mathf.Max(8f, x);

                var doll = _invDollPane != null ? _invDollPane.worldBound : window;
                y = doll.center.y - root.yMin - detailHeight * 0.5f;
            }

            y = Mathf.Clamp(y, 8f, Mathf.Max(8f, root.height - detailHeight - 8f));
            _invDetailView.style.left = x;
            _invDetailView.style.top = y;
        }

        // Called from Refresh() while the window is open. Rebuilds the list only
        // when the item set or a garment's live condition changes.
        private void RefreshInventory(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            if (_inventoryActorId >= 0 && _inventoryActorId != npc.Id.Value)
            {
                if (_selectedCraftGoal >= 0)
                {
                    _selectedCraftGoalByNpc[_inventoryActorId] = _selectedCraftGoal;
                }
                _selectedCraftGoal = -1;
                ClearInventoryDrag();
                HideItemDetail();
                _invSig = null;
                _craftSig = null;
            }
            _inventoryActorId = npc.Id.Value;
            _inventoryMutable = _runner != null && _runner.SupportsNpcCommands &&
                NpcSelection.Count == 1 &&
                _runner.CanControlNpc(npc.Id) && npc.Health > 0f;
            RefreshOutfitLockToggle(npc);
            if (!_inventoryOpen)
            {
                return;
            }

            RefreshCrafting(npc);

            var capacity = Mathf.Max(0, npc.InventoryCapacity);
            _inventoryCapacity.text = $"{npc.InventoryUsedSlots}/{capacity} {Loc.Get("inv.slots")}";

            // §123.5: отказ симуляции (StaleItem/InsufficientSpace/NoDropSpot)
            // виден прямо в окне — основной тост карточки этим окном закрыт.
            if (_invOrderToast != null)
            {
                var toastFresh = ManualOrderFeedback.IsFresh(npc.Id.Value);
                _invOrderToast.style.display =
                    toastFresh ? DisplayStyle.Flex : DisplayStyle.None;
                if (toastFresh)
                {
                    _invOrderToast.text = Loc.Get(ManualOrderFeedback.ReasonKey);
                }
            }
            if (_characterDollStage != null)
            {
                _characterDollStage.SetTarget(npc.Id.Value, npc.ActorMesh, npc.WornItems);
                _characterDollStage.SetVisibleWearLayer(_invVisibleWearLayer);
                _characterDollStage.SetZones(
                    npc.BodyParts, npc.SeveredParts, npc.BandagedZones,
                    npc.BodyPartConditions);
                var texture = _characterDollStage.Texture;
                if (texture != null)
                {
                    _invPreviewView.style.backgroundImage =
                        new StyleBackground(Background.FromRenderTexture(texture));
                }
            }

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
            _invWornDurability = wornDurability;
            _invCarriedWater = carriedWater;
            _invCarriedStacks = carriedStacks;
            _invWornWetness = wornWetness;
            _invWornDirtiness = wornDirtiness;
            _invCarriedOwnerIds = npc.InventoryOwnerIds;
            _invWornOwnerIds = npc.WornOwnerIds;
            _invWornItemIds = npc.WornItems;
            RefreshInventoryOwnerNames(snapshot);

            var sig = string.Join(",", npc.WornItems) + "|" + string.Join(",", npc.InventoryItems)
                + "|" + string.Join(",", npc.WornWetness) + "|" + string.Join(",", npc.WornDurability)
                + "|" + string.Join(",", npc.WornDirtiness)
                + "|" + string.Join(",", npc.WornBloodiness)
                + "|" + string.Join(",", npc.InventoryDurability)
                + "|" + string.Join(",", npc.InventoryWetness)
                + "|" + string.Join(",", npc.InventoryDirtiness)
                + "|" + string.Join(",", npc.InventoryBloodiness)
                + "|" + string.Join(",", npc.InventoryWater)
                + "|" + string.Join(",", npc.InventoryOwnerIds)
                + "|" + string.Join(",", npc.InventoryStacks) + "|" + npc.InventoryUsedSlots
                + "|" + npc.HeldItemId + "|" + npc.FavoriteWeaponId
                + "|lock=" + (npc.OutfitLocked ? "1" : "0")
                + "|wornOwners=" + string.Join(",", npc.WornOwnerIds)
                + "|page=" + (_clothesPage ? "clothes" : "backpack")
                + "|" + InventoryLayoutSignature(npc);
            if (sig == _invSig)
            {
                return;
            }

            _invSig = sig;
            RebuildItemList(npc, wornDurability, carriedDurability, carriedWater,
                carriedStacks, wornWetness, carriedWetness, wornDirtiness,
                carriedDirtiness);

            // Keep the detail view coherent: if the shown item is still present,
            // re-render it (its wetness/durability may have moved); else drop back.
            if (_invSelectedId != null)
            {
                var present = _invSelectedWorn
                    ? npc.WornItems.Contains(_invSelectedId)
                    : npc.InventoryContainers.Exists(c =>
                        c.Slots.Exists(s => s.ItemDefinitionId == _invSelectedId));
                if (present)
                {
                    var selectedDurability = _invSelectedWorn ? wornDurability : carriedDurability;
                    var selectedWetness = _invSelectedWorn ? wornWetness : carriedWetness;
                    var selectedDirtiness = _invSelectedWorn ? wornDirtiness : carriedDirtiness;
                    _invItemAnchors.TryGetValue(ItemAnchorKey(_invSelectedId, _invSelectedWorn),
                        out var selectedAnchor);
                    // §123.5: инвентарь пересобрался — SourceIndex мог уехать.
                    // Если старый индекс больше не указывает на этот предмет,
                    // перечитать его из layout, а не тащить протухший.
                    var refreshedIndex = _invSelectedSourceIndex;
                    if (_invSelectedWorn)
                    {
                        var stillValid = refreshedIndex >= 0 &&
                            refreshedIndex < npc.WornItems.Count &&
                            npc.WornItems[refreshedIndex] == _invSelectedId;
                        if (!stillValid) refreshedIndex = -1;
                    }
                    else
                    {
                        var stillValid = refreshedIndex >= 0 &&
                            npc.InventoryContainers.Exists(c => c.Slots.Exists(s =>
                                s.SourceIndex == refreshedIndex &&
                                s.ItemDefinitionId == _invSelectedId));
                        if (!stillValid)
                        {
                            refreshedIndex = FindCarriedSourceIndex(npc, _invSelectedId);
                        }
                    }

                    ShowItemDetail(_invSelectedId, _invSelectedWorn, selectedDurability,
                        carriedWater, carriedStacks, selectedWetness, selectedDirtiness,
                        selectedAnchor, refreshedIndex);
                }
                else
                {
                    HideItemDetail();
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
            _invItemsContent.Clear();
            _invItemAnchors.Clear();
            _invSlotCells.Clear();
            _invSlotIcons.Clear();
            _invSlotGlyphs.Clear();
            _invSlotBadges.Clear();
            _invHoveredWornId = string.Empty;
            _invHeldSlotMarked = false;

            if (_clothesPage)
            {
                // §123.5 / #204: mesh picking on the doll remains a convenient
                // shortcut, but never the only way to reach worn gear. This
                // stable list uses the authoritative WornItems index.
                for (var wornIndex = 0; wornIndex < npc.WornItems.Count; wornIndex++)
                {
                    _invItemsContent.Add(BuildWornItemCell(
                        npc.WornItems[wornIndex], wornIndex, wornDurability,
                        carriedWater, carriedStacks, wornWetness, wornDirtiness));
                }
            }
            else
            {
                // The layout owns deterministic physical placement. Every real
                // carried slot appears exactly once, in snapshot order.
                foreach (var container in npc.InventoryContainers)
                {
                    foreach (var slot in container.Slots)
                    {
                        var cell = BuildInventorySlotCell(
                            npc, slot, carriedDurability, carriedWater, carriedStacks,
                            carriedWetness, carriedDirtiness);
                        if (container.Kind == InventoryContainerKind.Overflow)
                        {
                            SetBorder(cell, Warn, 2f);
                        }
                        _invItemsContent.Add(cell);
                    }
                }
            }

            ResetInventoryDensity();
        }

        private static string InventoryLayoutSignature(NpcSnapshot npc)
        {
            var result = new System.Text.StringBuilder();
            foreach (var container in npc.InventoryContainers)
            {
                result.Append(container.Id).Append(':').Append((int)container.Kind).Append(':')
                    .Append(container.OwnerItemDefinitionId).Append(':').Append(container.OwnerSourceIndex)
                    .Append(':').Append((int)container.BodyAnchor)
                    .Append(':').Append(container.Capacity).Append(':').Append(container.BaseCapacity)
                    .Append(':').Append(container.StrengthBonus).Append(':').Append(container.BackpackCapacity);
                foreach (var slot in container.Slots)
                {
                    result.Append('[').Append(slot.Index).Append(':').Append(slot.SourceIndex)
                        .Append(':').Append(slot.ItemDefinitionId)
                        .Append(':').Append(slot.StackCount).Append(':')
                        .Append(slot.AcceptedItemDefinitionId).Append(']');
                }
                result.Append('|');
            }
            return result.ToString();
        }

        private VisualElement BuildInventorySlotCell(
            NpcSnapshot npc,
            InventorySlotSnapshot slot,
            Dictionary<string, float> carriedDurability,
            Dictionary<string, WaterContainerState> carriedWater,
            Dictionary<string, int> carriedStacks,
            Dictionary<string, float> carriedWetness,
            Dictionary<string, float> carriedDirtiness)
        {
            var cell = new VisualElement { name = "inventory-flat-slot" };
            cell.style.width = InventoryCellSizes[0];
            cell.style.height = InventoryCellSizes[0];
            cell.style.marginRight = InventoryGaps[0];
            cell.style.marginBottom = InventoryGaps[0];
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.position = Position.Relative;
            cell.style.backgroundColor = Track;
            SetBorder(cell, StrokeStrong, 1f);
            SetRadius(cell, 12f);
            _invSlotCells.Add(cell);

            var itemId = slot.ItemDefinitionId;
            if (!string.IsNullOrEmpty(itemId))
            {
                var def = ResolveDef(itemId);
                var info = ResolveItemInfo(itemId, def);
                var icon = LoadItemIcon(itemId);
                if (icon != null)
                {
                    var image = new Image { sprite = icon, scaleMode = ScaleMode.ScaleToFit };
                    image.style.width = InventoryCellSizes[0] - 14f;
                    image.style.height = InventoryCellSizes[0] - 14f;
                    image.pickingMode = PickingMode.Ignore;
                    image.AddToClassList("inventory-slot-icon");
                    cell.Add(image);
                    _invSlotIcons.Add(image);
                }
                else
                {
                    var glyph = new Label(info.Emoji);
                    glyph.style.fontSize = 58f;
                    glyph.pickingMode = PickingMode.Ignore;
                    glyph.AddToClassList("inventory-slot-glyph");
                    cell.Add(glyph);
                    _invSlotGlyphs.Add(glyph);
                }

                cell.tooltip = ItemName(def, info);
            }

            if (slot.StackCount > 1)
            {
                var count = new Label("×" + slot.StackCount);
                count.style.position = Position.Absolute;
                count.style.right = 3f;
                count.style.bottom = 1f;
                count.style.color = Text;
                count.style.fontSize = 9.5f;
                count.style.unityFontStyleAndWeight = FontStyle.Bold;
                count.pickingMode = PickingMode.Ignore;
                count.AddToClassList("inventory-slot-badge");
                cell.Add(count);
                _invSlotBadges.Add(count);
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var inUse = !_invHeldSlotMarked && itemId == npc.HeldItemId;
                if (inUse)
                {
                    _invHeldSlotMarked = true;
                    SetBorder(cell, new Color(0.37f, 0.78f, 0.91f, 0.95f), 2f);
                    var use = new Label(Loc.Get("inv.in_use"));
                    use.style.position = Position.Absolute;
                    use.style.left = 2f;
                    use.style.top = 1f;
                    use.style.color = new Color(0.55f, 0.88f, 1f);
                    use.style.fontSize = 7.5f;
                    use.style.unityFontStyleAndWeight = FontStyle.Bold;
                    use.pickingMode = PickingMode.Ignore;
                    use.AddToClassList("inventory-slot-badge");
                    cell.Add(use);
                    _invSlotBadges.Add(use);
                }

                if (itemId == npc.FavoriteWeaponId)
                {
                    var favorite = new Label("★");
                    favorite.style.position = Position.Absolute;
                    favorite.style.left = 3f;
                    favorite.style.bottom = 0f;
                    favorite.style.color = Gold;
                    favorite.style.fontSize = 10f;
                    favorite.pickingMode = PickingMode.Ignore;
                    favorite.AddToClassList("inventory-slot-badge");
                    cell.Add(favorite);
                    _invSlotBadges.Add(favorite);
                }

                RememberItemAnchor(itemId, false, cell);
                cell.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    SetBorderColor(cell, GoldDim);
                });
                cell.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    SetBorderColor(cell, inUse
                        ? new Color(0.37f, 0.78f, 0.91f, 0.95f)
                        : StrokeStrong);
                });
                RegisterInventoryItemInteraction(
                    cell, itemId, false, carriedDurability, carriedWater,
                    carriedStacks, carriedWetness, carriedDirtiness,
                    slot.SourceIndex);
            }

            return cell;
        }

        private VisualElement BuildWornItemCell(
            string itemId,
            int sourceIndex,
            Dictionary<string, float> durability,
            Dictionary<string, WaterContainerState> water,
            Dictionary<string, int> stacks,
            Dictionary<string, float> wetness,
            Dictionary<string, float> dirtiness)
        {
            var cell = new VisualElement { name = "inventory-worn-item" };
            cell.style.width = InventoryCellSizes[0];
            cell.style.height = InventoryCellSizes[0];
            cell.style.marginRight = InventoryGaps[0];
            cell.style.marginBottom = InventoryGaps[0];
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.position = Position.Relative;
            cell.style.backgroundColor = Track;
            SetBorder(cell, GoldDim, 1f);
            SetRadius(cell, 12f);
            _invSlotCells.Add(cell);

            var def = ResolveDef(itemId);
            var info = ResolveItemInfo(itemId, def);
            var icon = LoadItemIcon(itemId);
            if (icon != null)
            {
                var image = new Image { sprite = icon, scaleMode = ScaleMode.ScaleToFit };
                image.style.width = InventoryCellSizes[0] - 14f;
                image.style.height = InventoryCellSizes[0] - 14f;
                image.pickingMode = PickingMode.Ignore;
                image.AddToClassList("inventory-slot-icon");
                cell.Add(image);
                _invSlotIcons.Add(image);
            }
            else
            {
                var glyph = new Label(info.Emoji);
                glyph.style.fontSize = 58f;
                glyph.pickingMode = PickingMode.Ignore;
                glyph.AddToClassList("inventory-slot-glyph");
                cell.Add(glyph);
                _invSlotGlyphs.Add(glyph);
            }

            var wornBadge = new Label(Loc.Get("inv.worn"));
            wornBadge.style.position = Position.Absolute;
            wornBadge.style.left = 3f;
            wornBadge.style.top = 1f;
            wornBadge.style.color = Gold;
            wornBadge.style.fontSize = 7.5f;
            wornBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            wornBadge.pickingMode = PickingMode.Ignore;
            wornBadge.AddToClassList("inventory-slot-badge");
            cell.Add(wornBadge);
            _invSlotBadges.Add(wornBadge);
            cell.tooltip = ItemName(def, info);

            RememberItemAnchor(itemId, true, cell);
            cell.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(cell, Gold));
            cell.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(cell, GoldDim));
            RegisterInventoryItemInteraction(
                cell, itemId, true, durability, water, stacks, wetness, dirtiness,
                sourceIndex);
            return cell;
        }

        private void RememberItemAnchor(string itemId, bool worn, VisualElement anchor)
        {
            var key = ItemAnchorKey(itemId, worn);
            if (!_invItemAnchors.ContainsKey(key))
            {
                _invItemAnchors[key] = anchor;
            }
        }

        private static string ItemAnchorKey(string itemId, bool worn) =>
            (worn ? "w:" : "i:") + itemId;

        // Real image icon for an item, if one exists at
        // Resources/HexLive/UI/Items/<id>. Returns null so callers fall back
        // to the emoji glyph when no bespoke icon has been added.
        private static Sprite LoadItemIcon(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            return Wearing.Garments.ItemIcons.Load(id);
        }

        private void ShowItemDetail(
            string id,
            bool worn,
            Dictionary<string, float> durability,
            Dictionary<string, WaterContainerState> water,
            Dictionary<string, int> stacks,
            Dictionary<string, float> wetness,
            Dictionary<string, float> dirtiness,
            VisualElement anchor = null,
            int sourceIndex = -1,
            InventoryDetailPlacement placement = InventoryDetailPlacement.Preserve)
        {
            _invSelectedId = id;
            _invSelectedWorn = worn;
            _invSelectedSourceIndex = sourceIndex;
            SetHoveredWorn(worn ? id : string.Empty);
            if (placement != InventoryDetailPlacement.Preserve)
            {
                _invDetailPlacement = placement;
            }
            if (anchor != null)
            {
                _invDetailAnchor = anchor;
            }

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

            BuildItemStats(
                def, info, worn, durability, water, stacks, wetness, dirtiness,
                ResolveInventoryOwnerId(id, worn, sourceIndex));

            var wearable = def != null && def.Layer.HasValue;
            var outfitChangeBlocked = _outfitLockedNow && (worn || wearable);
            _invPrimaryAction.style.display = _inventoryMutable &&
                (worn || wearable) && !outfitChangeBlocked
                ? DisplayStyle.Flex : DisplayStyle.None;
            _invDropAction.style.display = _inventoryMutable && !(_outfitLockedNow && worn)
                ? DisplayStyle.Flex : DisplayStyle.None;
            _invReadOnlyLabel.style.display = !_inventoryMutable || outfitChangeBlocked
                ? DisplayStyle.Flex : DisplayStyle.None;
            _invPrimaryActionLabel.text = Loc.Get(worn
                ? "inv.action.stow" : "inv.action.wear");
            _invDropActionLabel.text = Loc.Get("inv.action.drop");
            _invReadOnlyLabel.text = Loc.Get(outfitChangeBlocked
                ? "inv.outfit_locked" : "inv.readonly");

            _invDetailView.style.display = DisplayStyle.Flex;
            _invDetailView.BringToFront();
            _invDetailView.schedule.Execute(PositionInventoryDetail);
        }

        private void EnqueueInventoryAction(InventoryAction action)
        {
            if (!_inventoryMutable || _runner == null || _invSelectedId == null ||
                _inventoryActorId < 0) return;
            var snapshot = _runner.IsReady ? _runner.CreateSnapshot() : null;
            var npc = snapshot != null ? FindNpc(snapshot, _inventoryActorId) : null;
            if (npc == null) return;
            int index;
            if (_invSelectedWorn)
            {
                // WornItems снапшота параллелен симовому списку — индекс по
                // нему честный.
                index = _invSelectedSourceIndex;
                if (index < 0 || index >= npc.WornItems.Count ||
                    npc.WornItems[index] != _invSelectedId)
                {
                    index = -1;
                    for (var i = 0; i < npc.WornItems.Count; i++)
                    {
                        if (npc.WornItems[i] == _invSelectedId)
                        {
                            index = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                // §123.5: carried-индекс берётся ТОЛЬКО из SourceIndex слота
                // (как LootTransferPanel) — npc.InventoryItems переупорядочен
                // стаками и врёт.
                index = _invSelectedSourceIndex >= 0
                    ? _invSelectedSourceIndex
                    : FindCarriedSourceIndex(npc, _invSelectedId);
            }
            if (index < 0) return;
            var itemRef = new InventoryItemRef(
                _invSelectedWorn ? InventoryItemSource.Worn : InventoryItemSource.Carried,
                index, _invSelectedId);
            _runner.EnqueueCommand(new ManageInventoryCommand(
                new HexLive.Simulation.Common.EntityId(_inventoryActorId), itemRef, action));
            HideItemDetail();
            _invSig = null;
        }

        private void RegisterInventoryItemInteraction(
            VisualElement element,
            string itemId,
            bool worn,
            Dictionary<string, float> durability,
            Dictionary<string, WaterContainerState> water,
            Dictionary<string, int> stacks,
            Dictionary<string, float> wetness,
            Dictionary<string, float> dirtiness,
            int sourceIndex = -1)
        {
            element.RegisterCallback<PointerDownEvent>(evt =>
                BeginInventoryPointerGesture(itemId, worn, sourceIndex, evt));
            element.RegisterCallback<PointerUpEvent>(evt =>
            {
                var quick = _invPointerDoubleClick;
                if (!FinishInventoryClick(itemId, worn, evt))
                {
                    return;
                }

                if (quick && TryInventoryQuickAction(itemId, worn, sourceIndex))
                {
                    evt.StopPropagation();
                    return;
                }

                ShowItemDetail(
                    itemId, worn, durability, water, stacks, wetness, dirtiness,
                    element, sourceIndex, InventoryDetailPlacement.Item);
                evt.StopPropagation();
            });
        }

        // §123.5: запасной поиск авторитетного индекса, когда карточка открыта
        // не с ячейки (например, пере-показ после rebuild): первый слот layout
        // с этим definitionId. Дубликаты неразличимы — но это честный индекс в
        // npc.Inventory.Items, а не легаси-перебор по переупорядоченному списку.
        private static int FindCarriedSourceIndex(NpcSnapshot npc, string itemId)
        {
            foreach (var container in npc.InventoryContainers)
            {
                foreach (var slot in container.Slots)
                {
                    if (slot.ItemDefinitionId == itemId && slot.SourceIndex >= 0)
                    {
                        return slot.SourceIndex;
                    }
                }
            }

            return -1;
        }

        private void BeginInventoryPointerGesture(
            string itemId, bool worn, int sourceIndex, PointerDownEvent evt)
        {
            if (evt.button != 0 || string.IsNullOrEmpty(itemId)) return;
            ClearInventoryDrag();
            _invPointerItemId = itemId;
            _invPointerItemWorn = worn;
            _invPointerId = evt.pointerId;
            _invPointerDownPosition = new Vector2(evt.position.x, evt.position.y);
            _invPointerMoved = false;
            // §128.4: пару засекаем на нажатии, а исполняем на отпускании —
            // иначе второй клик успел бы стать началом перетаскивания.
            _invPointerDoubleClick = _invDoubleClick.Accept(
                InventoryQuickActions.CellKey(
                    _inventoryActorId,
                    worn ? InventoryItemSource.Worn : InventoryItemSource.Carried,
                    sourceIndex,
                    itemId),
                evt.clickCount);
        }

        /// <summary>§128.4 «своя панель»: надеть носимое, снять надетое. Отказ
        /// возвращает false, и ячейка ведёт себя как при обычном клике.</summary>
        private bool TryInventoryQuickAction(string itemId, bool worn, int sourceIndex)
        {
            if (!_inventoryMutable || _runner == null || _inventoryActorId < 0) return false;
            var quick = InventoryQuickActions.Resolve(
                ownSide: true, worn, InventoryQuickActions.IsWearable(_runner, itemId));
            if (quick == InventoryQuickAction.None) return false;

            _invSelectedId = itemId;
            _invSelectedWorn = worn;
            _invSelectedSourceIndex = sourceIndex;
            EnqueueInventoryAction(quick == InventoryQuickAction.TakeOff
                ? InventoryAction.Stow
                : InventoryAction.Wear);
            return true;
        }

        private void UpdateInventoryPointerGesture(PointerMoveEvent evt)
        {
            if (_invPointerItemId == null || evt.pointerId != _invPointerId ||
                _invPointerMoved)
            {
                return;
            }

            var position = new Vector2(evt.position.x, evt.position.y);
            if ((position - _invPointerDownPosition).sqrMagnitude <
                InventoryDragThreshold * InventoryDragThreshold)
            {
                return;
            }

            _invPointerMoved = true;
            if (_inventoryMutable)
            {
                BeginInventoryDrag(_invPointerItemId, _invPointerItemWorn);
            }
        }

        private bool FinishInventoryClick(string itemId, bool worn, PointerUpEvent evt)
        {
            if (evt.button != 0 || evt.pointerId != _invPointerId ||
                !string.Equals(itemId, _invPointerItemId, StringComparison.Ordinal) ||
                worn != _invPointerItemWorn || _invPointerMoved || _invDraggedId != null)
            {
                return false;
            }

            ClearInventoryDrag();
            return true;
        }

        private void BeginInventoryDrag(string itemId, bool worn)
        {
            _invDraggedId = itemId;
            _invDraggedWorn = worn;
        }

        private void CompleteInventoryDrag(InventoryAction action)
        {
            if (_invDraggedId == null) return;
            _invSelectedId = _invDraggedId;
            _invSelectedWorn = _invDraggedWorn;
            EnqueueInventoryAction(action);
            ClearInventoryDrag();
        }

        private void ClearInventoryDrag()
        {
            _invDraggedId = null;
            _invDraggedWorn = false;
            _invPointerItemId = null;
            _invPointerItemWorn = false;
            _invPointerDoubleClick = false;
            _invPointerId = -1;
            _invPointerMoved = false;
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
            Dictionary<string, float> dirtiness,
            int ownerId)
        {
            _invDetailStats.Clear();
            var affinity = ItemAffinity.For(_boundActorId, info.DefinitionId);
            _invDetailStats.Add(MakeStatRow(
                Loc.Get("inv.affinity"), $"{Mathf.RoundToInt(affinity * 100f)}%",
                Color.Lerp(Warn, Gold, affinity)));
            if (def != null && def.Layer.HasValue)
            {
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.owner"), InventoryOwnerName(ownerId), TextDim));
            }
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

            var gear = GearCatalog.For(info.DefinitionId);
            if (gear.Id == info.DefinitionId && gear.MeleePriority > 0)
            {
                var baseline = CombatStatBreakdown.For(
                    info.DefinitionId, 1f, 1f, 1f, 1f);
                var stats = CombatStatBreakdown.For(
                    info.DefinitionId,
                    _meleeStats.LimbMultiplier,
                    _meleeStats.StrengthMultiplier,
                    _meleeStats.CombatMultiplier,
                    _meleeStats.AgilityRecoveryMultiplier);
                var modifiers = $"{Loc.Get("attr.strength")} {SignedPercent(stats.StrengthMultiplier)}, " +
                    $"{Loc.Get("skill.combat")} {SignedPercent(stats.CombatMultiplier)}, " +
                    $"{Loc.Get("inv.hands")} {SignedPercent(stats.LimbMultiplier)}";
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.damage"),
                    $"{stats.BaseDamage:0.###} → {stats.EffectiveDamage:0.###} ({modifiers})",
                    StatComparisonColor(baseline.EffectiveDamage, stats.EffectiveDamage, 0.001f)));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.cut"),
                    $"{stats.BaseCutDamage:0.###} → {stats.EffectiveCutDamage:0.###}",
                    StatComparisonColor(baseline.EffectiveCutDamage, stats.EffectiveCutDamage, 0.001f)));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.blunt"),
                    $"{stats.BaseBluntDamage:0.###} → {stats.EffectiveBluntDamage:0.###}",
                    StatComparisonColor(baseline.EffectiveBluntDamage, stats.EffectiveBluntDamage, 0.001f)));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.blood_loss"),
                    $"{stats.InstantBloodLoss:0.###}",
                    StatComparisonColor(baseline.InstantBloodLoss, stats.InstantBloodLoss, 0.001f)));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.cadence"),
                    $"{stats.AttacksPerMinute:0.#} {Loc.Get("inv.attacks_per_minute")} · " +
                    $"{stats.CycleSeconds:0.##} {Loc.Get("inv.seconds_cycle")}",
                    StatComparisonColor(
                        baseline.CycleSeconds, stats.CycleSeconds, 0.01f,
                        higherIsBetter: false)));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.dps"), $"{stats.DamagePerSecond:0.###}",
                    StatComparisonColor(baseline.DamagePerSecond, stats.DamagePerSecond, 0.001f)));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.hit_moment"), $"{stats.HitDelaySeconds:0.##} {Loc.Get("inv.seconds")}", TextDim));
                _invDetailStats.Add(MakeStatRow(
                    Loc.Get("inv.agility_recovery"),
                    $"{SignedBonusPercent(1f - stats.AgilityRecoveryMultiplier)} · " +
                    $"{stats.RecoverySeconds:0.##} {Loc.Get("inv.seconds")}",
                    StatComparisonColor(
                        baseline.RecoverySeconds, stats.RecoverySeconds, 0.01f,
                        higherIsBetter: false)));

                if (stats.TwoHanded)
                {
                    _invDetailStats.Add(MakeStatRow(
                        Loc.Get("inv.grip"), Loc.Get("inv.two_handed"), Gold));
                }

                if (stats.Capabilities != GearCapability.None)
                {
                    _invDetailStats.Add(MakeStatRow(
                        Loc.Get("inv.tool_profile"), LocalizeCapabilities(stats.Capabilities), TextDim));
                }
            }

        }

        private static string SignedPercent(float multiplier) =>
            SignedBonusPercent(multiplier - 1f);

        private void RefreshInventoryOwnerNames(WorldSnapshot snapshot)
        {
            _invOwnerNames.Clear();
            if (snapshot == null) return;
            foreach (var owner in snapshot.Npcs)
            {
                _invOwnerNames[owner.Id.Value] = owner.DisplayName;
            }
            foreach (var owner in snapshot.Corpses)
            {
                _invOwnerNames[owner.Id.Value] = owner.DisplayName;
            }
        }

        private int ResolveInventoryOwnerId(string itemId, bool worn, int sourceIndex)
        {
            var owners = worn ? _invWornOwnerIds : _invCarriedOwnerIds;
            if (sourceIndex >= 0 && sourceIndex < owners.Count)
            {
                return owners[sourceIndex];
            }

            // Doll picking identifies the visible garment by definition rather
            // than source index. Duplicate definitions are rare; the stable
            // Clothes tab always supplies the exact physical index.
            if (worn)
            {
                for (var i = 0; i < _invWornItemIds.Count && i < owners.Count; i++)
                {
                    if (_invWornItemIds[i] == itemId) return owners[i];
                }
            }
            return 0;
        }

        private string InventoryOwnerName(int ownerId)
        {
            if (ownerId <= 0) return Loc.Get("inv.owner.none");
            return _invOwnerNames.TryGetValue(ownerId, out var nameId) &&
                   !string.IsNullOrEmpty(nameId)
                ? Loc.NpcName(nameId)
                : Loc.Get("inv.owner.unknown");
        }

        private static Color StatComparisonColor(
            float baseline,
            float current,
            float displayedStep,
            bool higherIsBetter = true)
        {
            // A comparison colour must describe the arrow, not the damage type:
            // green = this bearer improves the weapon, red = degrades it.
            // Compare exactly what the player can read: rounded-equal values
            // must never disagree with their colour.
            var baselineShown = Mathf.Round(baseline / displayedStep);
            var currentShown = Mathf.Round(current / displayedStep);
            var delta = currentShown - baselineShown;
            if (Mathf.Abs(delta) < 0.5f)
            {
                return TextDim;
            }

            var improves = higherIsBetter ? delta > 0f : delta < 0f;
            return improves ? Good : Crit;
        }

        private static string SignedBonusPercent(float bonus)
        {
            var value = Mathf.RoundToInt(bonus * 100f);
            return value >= 0 ? $"+{value}%" : $"{value}%";
        }

        private static string LocalizeCapabilities(GearCapability capabilities)
        {
            var names = new List<string>();
            foreach (GearCapability capability in Enum.GetValues(typeof(GearCapability)))
            {
                if (capability == GearCapability.None || (capabilities & capability) == 0)
                {
                    continue;
                }

                names.Add(Loc.Get("gear.capability." + capability.ToString().ToLowerInvariant()));
            }

            return string.Join(", ", names);
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
            // Object definitions are static content, identical whoever owns the
            // world — ask the source rather than reaching into WorldState, which
            // a client that only receives snapshots does not have.
            return _runner != null && _runner.TryGetObjectDefinition(id, out var def) ? def : null;
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

        // §48.7: NeedConfig still uses localization keys because that is what
        // the rest of the panel binds. Keep this mapping exhaustive so a newly
        // displayed parameter cannot silently open another parameter's ledger.
        private static NeedKind NeedKindFor(string key)
        {
            return key switch
            {
                "need.hunger" => NeedKind.Hunger,
                "need.thirst" => NeedKind.Thirst,
                "need.energy" => NeedKind.Energy,
                "need.comfort" => NeedKind.Comfort,
                "need.social" => NeedKind.Social,
                "need.temperature" => NeedKind.Temperature,
                "need.stamina" => NeedKind.Stamina,
                "need.blood" => NeedKind.Blood,
                "need.hygiene" => NeedKind.Hygiene,
                "need.stress" => NeedKind.Stress,
                "need.compassion" => NeedKind.Compassion,
                "need.breath" => NeedKind.Breath,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(key), key, "Unknown character-panel need key")
            };
        }

        private static string NeedLocKey(NeedKind need)
        {
            return need switch
            {
                NeedKind.Hunger => "need.hunger",
                NeedKind.Thirst => "need.thirst",
                NeedKind.Energy => "need.energy",
                NeedKind.Comfort => "need.comfort",
                NeedKind.Social => "need.social",
                NeedKind.Temperature => "need.temperature",
                NeedKind.Stamina => "need.stamina",
                NeedKind.Blood => "need.blood",
                NeedKind.Hygiene => "need.hygiene",
                NeedKind.Stress => "need.stress",
                NeedKind.Compassion => "need.compassion",
                NeedKind.Breath => "need.breath",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(need), need, "Unknown character-panel parameter")
            };
        }

        private void UpdateRelations(NpcSnapshot npc)
        {
            if (npc.RelationshipDetails.Count == 0)
            {
                if (_relationsBuiltEmpty)
                {
                    return;
                }

                _relationsBuiltEmpty = true;
                _relationSig.Clear();
                _relationsContainer.Clear();
                var none = new Label(Loc.Get("panel.none"));
                none.style.color = TextMute;
                none.style.fontSize = 11;
                _relationsContainer.Add(none);
                return;
            }

            var relations = _relationScratch;
            relations.Clear();
            relations.AddRange(npc.RelationshipDetails);
            relations.Sort((a, b) =>
            {
                var byAffinity = Mathf.Abs(b.Affinity).CompareTo(Mathf.Abs(a.Affinity));
                return byAffinity != 0
                    ? byAffinity
                    : string.Compare(Loc.NpcName(a.OtherName), Loc.NpcName(b.OtherName),
                        StringComparison.CurrentCultureIgnoreCase);
            });

            // Plain loop, not List.Find: the predicate would capture `this` for
            // _selectedRelationId, i.e. a closure allocated on every tick.
            RelationshipSnapshot selected = null;
            for (var i = 0; i < relations.Count; i++)
            {
                if (relations[i].OtherId == _selectedRelationId)
                {
                    selected = relations[i];
                    break;
                }
            }

            if (selected == null)
            {
                selected = relations[0];
                _selectedRelationId = selected.OtherId;
            }

            // PERF: this subtree — a tab per relation, each with a portrait, plus
            // the focus card — was torn down and rebuilt EVERY tick, which is
            // where most of the panel's ~211 KB/tick of garbage and its share of
            // the UIElements layout+repaint came from. It only ever changes when
            // a relationship moves or the player picks another tab, so rebuild on
            // exactly that, the way the effect chips and the inventory list
            // already do.
            if (!_relationsBuiltEmpty && RelationsUnchanged(relations))
            {
                return;
            }

            RememberRelations(relations);
            _relationsBuiltEmpty = false;

            _relationsContainer.Clear();
            _relationsContainer.Add(BuildRelationTabs(relations, selected.OtherId, npc));
            _relationsContainer.Add(BuildRelationFocusCard(selected));
        }

        // The signature is kept field-wise rather than as a joined string: a
        // string would allocate every tick just to decide there was nothing to
        // do. Values are COPIED — the snapshot is reused in place between ticks
        // (spec 31.17), so holding the RelationshipSnapshot objects themselves
        // would compare a list against itself and never see a change.
        private readonly struct RelationSig
        {
            public RelationSig(RelationshipSnapshot r)
            {
                OtherId = r.OtherId;
                OtherName = r.OtherName;
                Trust = r.Trust;
                Familiarity = r.Familiarity;
                Affinity = r.Affinity;
            }

            public readonly int OtherId;
            public readonly string OtherName;
            public readonly float Trust;
            public readonly float Familiarity;
            public readonly float Affinity;

            public bool Matches(RelationshipSnapshot r) =>
                OtherId == r.OtherId && OtherName == r.OtherName &&
                Trust == r.Trust && Familiarity == r.Familiarity && Affinity == r.Affinity;
        }

        private readonly List<RelationSig> _relationSig = new();
        private readonly List<RelationshipSnapshot> _relationScratch = new();
        private int _relationSigSelected = int.MinValue;
        private bool _relationsBuiltEmpty;

        private bool RelationsUnchanged(List<RelationshipSnapshot> relations)
        {
            if (_relationSigSelected != _selectedRelationId ||
                _relationSig.Count != relations.Count)
            {
                return false;
            }

            for (var i = 0; i < relations.Count; i++)
            {
                if (!_relationSig[i].Matches(relations[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void RememberRelations(List<RelationshipSnapshot> relations)
        {
            _relationSig.Clear();
            for (var i = 0; i < relations.Count; i++)
            {
                _relationSig.Add(new RelationSig(relations[i]));
            }

            _relationSigSelected = _selectedRelationId;
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

            var initial = new Label(InitialOf(Loc.NpcName(rel.OtherName)));
            initial.style.color = new Color(0.06f, 0.086f, 0.102f);
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.fontSize = 13;
            initial.style.unityTextAlign = TextAnchor.MiddleCenter;
            avatar.Add(initial);
            ApplyRelationFace(avatar, rel.OtherId, initial);
            tab.Add(avatar);

            var name = new Label(Loc.NpcName(rel.OtherName));
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

            var initial = new Label(InitialOf(Loc.NpcName(rel.OtherName)));
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

            var name = new Label(Loc.NpcName(rel.OtherName));
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
            var initial = new Label(InitialOf(Loc.NpcName(rel.OtherName)));
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
            var rn = new Label(Loc.NpcName(rel.OtherName));
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
            BuildGroupCard();
            BuildLanguageButton();
            BuildDiagnostics();
            BuildExpandTab();
            BuildEffectTooltip();
            BuildInventoryWindow();
            BuildHealthWindow();
            BuildJournalWindow(); // §136
            BuildRoster();
        }

        private void BuildGroupCard()
        {
            _groupCard = new VisualElement();
            _groupCard.style.height = 172f;
            _groupCard.style.width = Length.Percent(100f);
            _groupCard.style.flexDirection = FlexDirection.Row;
            _groupCard.style.alignItems = Align.Center;
            _groupCard.style.paddingLeft = 24f;
            _groupCard.style.paddingRight = 24f;
            _groupCard.style.backgroundColor = Panel;
            SetBorder(_groupCard, StrokeStrong, 1f);
            SetRadius(_groupCard, 16f);
            _groupCard.style.display = DisplayStyle.None;
            _groupCard.pickingMode = PickingMode.Position;

            var summary = new VisualElement();
            summary.style.width = 290f;
            summary.style.marginRight = 20f;
            _groupTitle = new Label();
            _groupTitle.style.fontSize = 22f;
            _groupTitle.style.color = Text;
            _groupTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            summary.Add(_groupTitle);
            _groupManualSummary = new Label();
            _groupManualSummary.style.fontSize = 14f;
            _groupManualSummary.style.color = TextDim;
            _groupManualSummary.style.marginTop = 7f;
            summary.Add(_groupManualSummary);
            _groupAlerts = new Label();
            _groupAlerts.style.fontSize = 13f;
            _groupAlerts.style.color = Warn;
            _groupAlerts.style.marginTop = 7f;
            summary.Add(_groupAlerts);
            _groupCard.Add(summary);

            _groupPortraits = new VisualElement();
            _groupPortraits.style.flexDirection = FlexDirection.Row;
            _groupPortraits.style.flexGrow = 1f;
            _groupPortraits.style.flexWrap = Wrap.Wrap;
            _groupPortraits.style.alignContent = Align.Center;
            _groupCard.Add(_groupPortraits);

            var commands = new VisualElement();
            commands.style.width = 280f;
            commands.style.marginLeft = 18f;
            var mode = CommandButton(() => ToggleGroupManual());
            _groupControlLabel = new Label();
            _groupControlLabel.style.color = Text;
            _groupControlLabel.style.fontSize = 14f;
            _groupControlLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _groupControlLabel.pickingMode = PickingMode.Ignore;
            mode.Add(_groupControlLabel);
            commands.Add(mode);

            var stop = CommandButton(StopGroup);
            _groupStopLabel = new Label(Loc.Get("group.stop"));
            _groupStopLabel.style.color = Crit;
            _groupStopLabel.style.fontSize = 14f;
            _groupStopLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _groupStopLabel.pickingMode = PickingMode.Ignore;
            stop.Add(_groupStopLabel);
            stop.style.marginTop = 7f;
            commands.Add(stop);

            _groupOrderResult = new Label();
            _groupOrderResult.style.fontSize = 12f;
            _groupOrderResult.style.color = TextDim;
            _groupOrderResult.style.marginTop = 8f;
            _groupOrderResult.style.whiteSpace = WhiteSpace.Normal;
            commands.Add(_groupOrderResult);
            _groupCard.Add(commands);
            _stage.Add(_groupCard);
        }

        private static VisualElement CommandButton(Action action)
        {
            var button = new VisualElement();
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.height = 36f;
            button.style.backgroundColor = Raised;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 8f);
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                action?.Invoke();
                evt.StopPropagation();
            });
            return button;
        }

        // PERF: скратч вместо новой List каждый кадр — RefreshGroup зовётся
        // ежекадрово, и аллокация с сортировкой стояли ДО каких-либо гейтов.
        private readonly List<NpcSnapshot> _groupSelectedScratch = new();

        private void RefreshGroup(WorldSnapshot snapshot)
        {
            var selected = _groupSelectedScratch;
            selected.Clear();
            foreach (var npc in snapshot.Npcs)
            {
                if (NpcSelection.Contains(npc.Id.Value) &&
                    _runner != null && _runner.CanControlNpc(npc.Id) && npc.Health > 0f)
                {
                    selected.Add(npc);
                }
            }
            if (selected.Count <= 1) return;

            _card.style.display = DisplayStyle.None;
            _groupCard.style.display = DisplayStyle.Flex;
            // Feedback uses unscaled time and may expire while the simulation
            // is paused, so refresh just this line every frame. The portraits
            // and aggregate state below only change with a snapshot tick.
            _groupOrderResult.text = GroupOrderFeedback.IsFresh
                ? GroupOrderFeedback.LocalizedSummary()
                : Loc.Get("group.order_hint");
            if (snapshot.Tick == _refreshedTick && _refreshedActorId == -1)
            {
                return;
            }

            // snapshot.Npcs и так в порядке возрастания id (SortById), но
            // сортировка остаётся страховкой; после тикового гейта она
            // выполняется раз в тик, а не раз в кадр.
            selected.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
            _refreshedTick = snapshot.Tick;
            _refreshedActorId = -1;
            _groupTitle.text = string.Format(Loc.Get("group.selected"), selected.Count);

            var manual = 0;
            var incapacitated = 0;
            var fighting = 0;
            foreach (var npc in selected)
            {
                if (npc.IsManualControl) manual++;
                if (npc.IsDying || npc.IsUnconscious || npc.IsFainted) incapacitated++;
                if (npc.IsFighting) fighting++;
            }
            _groupAllManual = manual == selected.Count;
            _groupManualSummary.text = string.Format(
                Loc.Get("group.manual_summary"), manual, selected.Count);
            _groupControlLabel.text = Loc.Get(manual == 0
                ? "panel.control.ai"
                : manual == selected.Count ? "panel.control.manual" : "panel.control.mixed");
            _groupControlLabel.style.color = manual == 0 ? Text : Gold;

            if (incapacitated > 0 || fighting > 0)
            {
                _groupAlerts.text = string.Format(
                    Loc.Get("group.alerts"), incapacitated, fighting);
                _groupAlerts.style.display = DisplayStyle.Flex;
            }
            else
            {
                _groupAlerts.style.display = DisplayStyle.None;
            }

            _groupPortraits.Clear();
            foreach (var npc in selected)
            {
                var actorId = npc.Id.Value;
                var portrait = new VisualElement();
                portrait.style.width = 52f;
                portrait.style.height = 52f;
                portrait.style.marginRight = 7f;
                portrait.style.marginBottom = 7f;
                portrait.style.backgroundColor = PortraitBackdrop;
                SetRadius(portrait, 26f);
                SetBorder(portrait, npc.IsFighting ? Crit : StrokeStrong, 2f);
                if (_portraitCache != null && _portraitCache.TryGet(actorId, out var face))
                {
                    portrait.style.backgroundImage = new StyleBackground(face);
                }
                else
                {
                    _portraitCache?.RequestNow(actorId);
                    var initial = new Label(InitialOf(Loc.NpcName(npc.DisplayName)));
                    initial.style.color = Text;
                    initial.style.fontSize = 18f;
                    initial.style.unityTextAlign = TextAnchor.MiddleCenter;
                    initial.style.flexGrow = 1f;
                    initial.pickingMode = PickingMode.Ignore;
                    portrait.Add(initial);
                }
                portrait.RegisterCallback<MouseDownEvent>(evt =>
                {
                    NpcSelection.Replace(actorId, requestFrame: true);
                    evt.StopPropagation();
                });
                _groupPortraits.Add(portrait);
            }

        }

        private List<HexLive.Simulation.Common.EntityId> SelectedEntityIds()
        {
            var result = new List<HexLive.Simulation.Common.EntityId>(NpcSelection.Count);
            foreach (var id in NpcSelection.SelectedIds)
            {
                var entityId = new HexLive.Simulation.Common.EntityId(id);
                if (_runner != null && _runner.CanControlNpc(entityId))
                    result.Add(entityId);
            }
            return result;
        }

        private void ToggleGroupManual()
        {
            if (_runner == null || !_runner.SupportsNpcCommands) return;
            _runner.EnqueueCommand(new SetGroupManualControlCommand(
                SelectedEntityIds(), !_groupAllManual));
        }

        private void StopGroup()
        {
            if (_runner == null || !_runner.SupportsNpcCommands) return;
            _runner.EnqueueCommand(new GroupStopCommand(SelectedEntityIds()));
        }

        private void BuildRoster()
        {
            _roster = new VisualElement();
            _roster.style.position = Position.Absolute;
            _roster.style.right = 12f;
            _roster.style.top = 78f;
            _roster.style.width = 232f;
            _roster.style.maxHeight = Length.Percent(78f);
            _roster.style.paddingLeft = 8f;
            _roster.style.paddingRight = 8f;
            _roster.style.paddingTop = 8f;
            _roster.style.paddingBottom = 8f;
            _roster.style.backgroundColor = Panel;
            SetBorder(_roster, StrokeStrong, 1f);
            SetRadius(_roster, 12f);
            _roster.pickingMode = PickingMode.Position;

            var clanHeader = RosterHeader(
                "roster.clan", () => SelectWholeClan(), out _clanRosterHeader);
            _roster.Add(clanHeader);
            _clanRosterList = new VisualElement();
            _roster.Add(_clanRosterList);
            _outsiderRosterSection = new VisualElement();
            var outsidersHeader = RosterHeader(
                "roster.outsiders", null, out _outsiderRosterHeader);
            outsidersHeader.style.marginTop = 9f;
            _outsiderRosterSection.Add(outsidersHeader);
            _outsiderRosterList = new VisualElement();
            _outsiderRosterSection.Add(_outsiderRosterList);
            _roster.Add(_outsiderRosterSection);
            _root.Add(_roster);
        }

        private static VisualElement RosterHeader(string key, Action clicked, out Label label)
        {
            var header = new VisualElement();
            header.style.height = 30f;
            header.style.justifyContent = Justify.Center;
            header.style.paddingLeft = 7f;
            header.style.backgroundColor = Raised;
            SetRadius(header, 7f);
            label = new Label(Loc.Get(key));
            label.style.color = Text;
            label.style.fontSize = 13f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.pickingMode = PickingMode.Ignore;
            header.Add(label);
            if (clicked != null)
            {
                header.RegisterCallback<MouseDownEvent>(evt =>
                {
                    clicked();
                    evt.StopPropagation();
                });
            }
            return header;
        }

        private void SelectWholeClan()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null) return;
            var ids = new List<int>();
            foreach (var npc in snapshot.Npcs)
            {
                if (_runner != null && _runner.CanControlNpc(npc.Id) && npc.Health > 0f)
                    ids.Add(npc.Id.Value);
            }
            ids.Sort();
            NpcSelection.ActivateMany(ids);
        }

        private void RefreshRoster()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null || _roster == null) return;
            NpcSelection.RightUiCoverage = _root.layout.width > 1f
                ? Mathf.Clamp01(244f / _root.layout.width)
                : 0.13f;
            _worldRenderer ??= FindAnyObjectByType<HexWorldRenderer>();
            var visibilityReady = _worldRenderer != null &&
                _worldRenderer.PlayerVisibilityReady;
            if (snapshot.Tick == _rosterTick &&
                visibilityReady == _rosterVisibilityReady) return;
            _rosterTick = snapshot.Tick;
            _rosterVisibilityReady = visibilityReady;
            _rosterBindings.Clear();
            _clanRosterList.Clear();
            _outsiderRosterList.Clear();

            var visibleOutsiders = 0;

            var ordered = new List<NpcSnapshot>(snapshot.Npcs);
            ordered.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            foreach (var npc in ordered)
            {
                if (npc.Health <= 0f) continue;
                var owned = _runner != null && _runner.CanControlNpc(npc.Id);
                if (!owned && (_worldRenderer == null ||
                    !_worldRenderer.IsNpcPickable(npc.Id.Value, npc.Tile, false)))
                {
                    continue;
                }

                var card = BuildRosterCard(npc);
                // §149: «свой» — это КЕМ Я УПРАВЛЯЮ, а не чья фракция. В
                // HugeIsland/Maniac сервер выдаёт девушку любого лагеря, и
                // раскладка по фракции отправляла её в «Чужаки» — выданный
                // персонаж выглядел как невыданный. Faction.Colony остаётся
                // рядом ради анонимного зрителя и локальной игры, где владения
                // нет вовсе, а клан показывать надо.
                if (owned || npc.Faction == HexLive.Simulation.Agents.Faction.Colony)
                    _clanRosterList.Add(card);
                else
                {
                    _outsiderRosterList.Add(card);
                    visibleOutsiders++;
                }
            }

            if (_outsiderRosterSection != null)
            {
                _outsiderRosterSection.style.display = visibleOutsiders > 0
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
        }

        private VisualElement BuildRosterCard(NpcSnapshot npc)
        {
            var card = new VisualElement();
            card.style.height = 53f;
            card.style.marginTop = 5f;
            card.style.paddingLeft = 5f;
            card.style.paddingRight = 6f;
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = NpcSelection.Contains(npc.Id.Value) ? Raised : PanelMid;
            SetRadius(card, 8f);
            SetBorder(card, NpcSelection.Contains(npc.Id.Value) ? GoldDim : Stroke, 1f);

            // §123: the portrait uses the same health truth and colour scale as
            // the large §105 instrument. Fighting keeps pulsing the card/dot;
            // the ring remains an unambiguous body-health signal.
            var hp = Mathf.Clamp01(npc.DisplayHealth);
            var healthRing = new RingMeter(
                hp, CharacterDollStage.StatusColor(hp, false))
            {
                name = "roster-health-ring",
                LineWidth = 3.5f
            };
            healthRing.style.width = 51f;
            healthRing.style.height = 51f;
            healthRing.style.marginRight = 7f;
            healthRing.style.flexShrink = 0f;
            healthRing.style.position = Position.Relative;

            var face = new VisualElement();
            face.style.position = Position.Absolute;
            face.style.left = 6f;
            face.style.top = 6f;
            face.style.width = 39f;
            face.style.height = 39f;
            face.style.backgroundColor = PortraitBackdrop;
            face.pickingMode = PickingMode.Ignore;
            SetRadius(face, 19.5f);
            if (_portraitCache != null && _portraitCache.TryGet(npc.Id.Value, out var texture))
                face.style.backgroundImage = new StyleBackground(texture);
            else
            {
                _portraitCache?.RequestNow(npc.Id.Value);
                var initial = new Label(InitialOf(Loc.NpcName(npc.DisplayName)));
                initial.style.color = TextDim;
                initial.style.fontSize = 15f;
                initial.style.flexGrow = 1f;
                initial.style.unityTextAlign = TextAnchor.MiddleCenter;
                initial.pickingMode = PickingMode.Ignore;
                face.Add(initial);
            }
            healthRing.Add(face);
            card.Add(healthRing);

            var text = new VisualElement();
            text.style.flexGrow = 1f;
            var name = new Label(Loc.NpcName(npc.DisplayName));
            name.style.fontSize = 12f;
            name.style.color = Text;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.pickingMode = PickingMode.Ignore;
            text.Add(name);
            ResolveStatus(npc, out var statusKey, out var statusColor);
            var status = new Label(Loc.Get(statusKey));
            status.style.fontSize = 10f;
            status.style.color = TextDim;
            status.pickingMode = PickingMode.Ignore;
            text.Add(status);
            card.Add(text);

            if (npc.IsFighting)
            {
                var fight = new Label(Loc.Get("badge.fighting"));
                fight.style.fontSize = 9f;
                fight.style.color = Crit;
                fight.pickingMode = PickingMode.Ignore;
                card.Add(fight);
            }
            var dot = new VisualElement();
            dot.style.width = 10f;
            dot.style.height = 10f;
            dot.style.marginLeft = 5f;
            dot.style.backgroundColor = statusColor;
            SetRadius(dot, 5f);
            card.Add(dot);

            var actorId = npc.Id.Value;
            var controllable = _runner != null && _runner.CanControlNpc(npc.Id);
            card.RegisterCallback<MouseDownEvent>(evt =>
            {
                var shift = Keyboard.current != null &&
                    (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
                if (controllable && shift) NpcSelection.Toggle(actorId);
                else NpcSelection.Activate(actorId);
                evt.StopPropagation();
            });
            _rosterBindings.Add(new RosterBinding
                { Id = actorId, Fighting = npc.IsFighting, Card = card, Dot = dot });
            return card;
        }

        private void AnimateRoster()
        {
            var wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 4f);
            foreach (var binding in _rosterBindings)
            {
                if (!binding.Fighting) continue;
                var pulse = Color.Lerp(new Color(Crit.r, Crit.g, Crit.b, 0.35f), Crit, wave);
                SetBorderColor(binding.Card, pulse);
                binding.Dot.style.backgroundColor = pulse;
            }
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

        private void BuildDiagnostics()
        {
            _diagnosticsLabel = new Label();
            _diagnosticsLabel.style.position = Position.Absolute;
            _diagnosticsLabel.style.top = 50f;
            _diagnosticsLabel.style.right = 18f;
            _diagnosticsLabel.style.color = TextMute;
            _diagnosticsLabel.style.fontSize = 10f;
            _diagnosticsLabel.style.unityTextAlign = TextAnchor.UpperRight;
            _diagnosticsLabel.style.whiteSpace = WhiteSpace.Normal;
            _diagnosticsLabel.pickingMode = PickingMode.Ignore;
            _root.Add(_diagnosticsLabel);
        }

        private void UpdateDiagnostics()
        {
            _diagnosticsElapsed += Time.unscaledDeltaTime;
            _diagnosticsFrames++;
            if (_diagnosticsLabel == null || _diagnosticsElapsed < 0.5f)
            {
                return;
            }

            var frameSeconds = _diagnosticsElapsed / Mathf.Max(1, _diagnosticsFrames);
            var fps = Mathf.RoundToInt(1f / Mathf.Max(0.0001f, frameSeconds));
            var allocatedMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() /
                (1024L * 1024L);
            var managedMb = GC.GetTotalMemory(false) / (1024L * 1024L);
            _diagnosticsLabel.text =
                $"v{Application.version} · {fps} FPS · {frameSeconds * 1000f:0.0} ms\n" +
                $"RAM {allocatedMb} MB · GC {managedMb} MB";
            _diagnosticsElapsed = 0f;
            _diagnosticsFrames = 0;
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
            col.style.position = Position.Relative;
            col.style.backgroundColor = PortraitBackdrop;
            col.style.marginRight = 2f;
            col.style.overflow = Overflow.Hidden;

            // The live RenderTexture is the card, not an avatar embedded in it.
            // PortraitStage owns composition and keeps the face on the former
            // circle's centre while the wider frame reveals the neon set.
            _portrait = new VisualElement();
            _portrait.style.position = Position.Absolute;
            _portrait.style.left = 0f;
            _portrait.style.right = 0f;
            _portrait.style.top = 0f;
            _portrait.style.bottom = 0f;
            _portrait.style.backgroundColor = PortraitBackdrop;
            _portrait.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            _portrait.pickingMode = PickingMode.Ignore;
            col.Add(_portrait);

            _nameLabel = new Label("—");
            _nameLabel.style.color = Text;
            _nameLabel.style.fontSize = 22f;
            _nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _nameLabel.style.position = Position.Absolute;
            _nameLabel.style.left = 190f;
            _nameLabel.style.right = 80f;
            _nameLabel.style.top = 17f;
            _nameLabel.style.height = 28f;
            _nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _nameLabel.style.overflow = Overflow.Hidden;
            _nameLabel.style.textOverflow = TextOverflow.Ellipsis;
            _nameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _nameLabel.pickingMode = PickingMode.Ignore;
            col.Add(_nameLabel);

            _roleLabel = new Label();
            _roleLabel.style.color = TextMute;
            _roleLabel.style.fontSize = 10.5f;
            _roleLabel.style.position = Position.Absolute;
            _roleLabel.style.left = 190f;
            _roleLabel.style.right = 80f;
            _roleLabel.style.top = 47f;
            _roleLabel.style.height = 18f;
            _roleLabel.style.overflow = Overflow.Hidden;
            _roleLabel.style.textOverflow = TextOverflow.Ellipsis;
            _roleLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _roleLabel.pickingMode = PickingMode.Ignore;
            col.Add(_roleLabel);

            // One lower-right HUD module: the status rail grows out from under
            // the HP dial. Keeping a shared parent makes the connection stable
            // when the identity card is resized or restyled.
            var vitalsCluster = new VisualElement();
            vitalsCluster.style.position = Position.Absolute;
            vitalsCluster.style.right = 4f;
            vitalsCluster.style.bottom = 12f;
            vitalsCluster.style.width = 218f;
            vitalsCluster.style.height = 96f;

            // HP is the large leading instrument instead of a frame around the
            // face. It remains the entry point to the limb window.
            var healthBadge = new VisualElement();
            healthBadge.style.position = Position.Absolute;
            healthBadge.style.right = 0f;
            healthBadge.style.bottom = 0f;
            healthBadge.style.width = 96f;
            healthBadge.style.height = 96f;
            healthBadge.style.backgroundColor = IdentityGlass;
            SetRadius(healthBadge, 48f);
            SetBorder(healthBadge, StrokeStrong, 1f);
            healthBadge.tooltip = Loc.Get("panel.health");
            _healthButton = healthBadge;

            _healthRing = new RingMeter(1f, CharacterDollStage.StatusColor(1f, false))
            {
                LineWidth = 5f
            };
            _healthRing.style.position = Position.Absolute;
            _healthRing.style.left = 2f;
            _healthRing.style.right = 2f;
            _healthRing.style.top = 2f;
            _healthRing.style.bottom = 2f;
            healthBadge.Add(_healthRing);

            _healthValue = new Label("—");
            _healthValue.style.color = Text;
            _healthValue.style.fontSize = 20f;
            _healthValue.style.unityFontStyleAndWeight = FontStyle.Bold;
            _healthValue.style.position = Position.Absolute;
            _healthValue.style.left = 0f;
            _healthValue.style.right = 0f;
            _healthValue.style.top = 21f;
            _healthValue.style.height = 25f;
            _healthValue.style.unityTextAlign = TextAnchor.MiddleCenter;
            _healthValue.pickingMode = PickingMode.Ignore;
            healthBadge.Add(_healthValue);

            var healthCaption = new Label("HP");
            healthCaption.style.color = Health;
            healthCaption.style.fontSize = 11f;
            healthCaption.style.unityFontStyleAndWeight = FontStyle.Bold;
            healthCaption.style.position = Position.Absolute;
            healthCaption.style.left = 0f;
            healthCaption.style.right = 0f;
            healthCaption.style.top = 49f;
            healthCaption.style.height = 17f;
            healthCaption.style.unityTextAlign = TextAnchor.MiddleCenter;
            healthCaption.pickingMode = PickingMode.Ignore;
            healthBadge.Add(healthCaption);

            healthBadge.RegisterCallback<MouseEnterEvent>(_ =>
                SetBorderColor(healthBadge, GoldDim));
            healthBadge.RegisterCallback<MouseLeaveEvent>(_ =>
                SetBorderColor(healthBadge, StrokeStrong));
            healthBadge.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleHealth();
                evt.StopPropagation();
            });

            // Movement and heat/UV share a single connected glass rail. Its
            // rounded end runs beneath the HP circle so the three readouts read
            // as one instrument rather than unrelated controls.
            var vitalsRail = new VisualElement();
            vitalsRail.style.position = Position.Absolute;
            vitalsRail.style.left = 0f;
            vitalsRail.style.right = 68f;
            vitalsRail.style.top = 9f;
            vitalsRail.style.bottom = 9f;
            vitalsRail.style.backgroundColor = IdentityGlass;
            SetRadius(vitalsRail, 16f);
            SetBorder(vitalsRail, StrokeStrong, 1f);
            vitalsRail.style.overflow = Overflow.Hidden;

            var vitalsRailAccent = new VisualElement();
            vitalsRailAccent.style.position = Position.Absolute;
            vitalsRailAccent.style.left = 0f;
            vitalsRailAccent.style.top = 14f;
            vitalsRailAccent.style.bottom = 14f;
            vitalsRailAccent.style.width = 2f;
            vitalsRailAccent.style.backgroundColor = NeonCyan;
            vitalsRailAccent.pickingMode = PickingMode.Ignore;
            vitalsRail.Add(vitalsRailAccent);

            // Movement/body state occupies the upper half of the shared rail.
            var statusRow = new VisualElement();
            statusRow.style.position = Position.Absolute;
            statusRow.style.left = 0f;
            statusRow.style.right = 0f;
            statusRow.style.top = 0f;
            statusRow.style.height = 39f;
            statusRow.style.flexDirection = FlexDirection.Row;
            statusRow.style.alignItems = Align.Center;
            statusRow.style.paddingLeft = 11f;
            statusRow.style.paddingRight = 25f;

            _statusDot = new VisualElement();
            _statusDot.style.width = 8f;
            _statusDot.style.height = 8f;
            _statusDot.style.flexShrink = 0f;
            SetRadius(_statusDot, 4f);
            _statusDot.style.backgroundColor = Energy;
            _statusDot.style.marginRight = 7f;
            _statusLabel = new Label();
            _statusLabel.style.color = Text;
            _statusLabel.style.fontSize = 11f;
            _statusLabel.style.flexGrow = 1f;
            _statusLabel.style.overflow = Overflow.Hidden;
            _statusLabel.style.textOverflow = TextOverflow.Ellipsis;
            _statusLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _statusLabel.pickingMode = PickingMode.Ignore;
            statusRow.Add(_statusDot);
            statusRow.Add(_statusLabel);
            vitalsRail.Add(statusRow);

            _starvingBadge = new Label();
            _starvingBadge.style.position = Position.Absolute;
            _starvingBadge.style.left = 190f;
            _starvingBadge.style.right = 14f;
            _starvingBadge.style.top = 158f;
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
            _starvingBadge.pickingMode = PickingMode.Ignore;
            col.Add(_starvingBadge);

            var vitalsDivider = new VisualElement();
            vitalsDivider.style.position = Position.Absolute;
            vitalsDivider.style.left = 11f;
            vitalsDivider.style.right = 25f;
            vitalsDivider.style.top = 38f;
            vitalsDivider.style.height = 1f;
            vitalsDivider.style.backgroundColor = Stroke;
            vitalsDivider.pickingMode = PickingMode.Ignore;
            vitalsRail.Add(vitalsDivider);

            // UV gets the lower half of that same rail.
            var uvRow = new VisualElement();
            uvRow.style.position = Position.Absolute;
            uvRow.style.left = 0f;
            uvRow.style.right = 0f;
            uvRow.style.top = 39f;
            uvRow.style.height = 39f;
            uvRow.style.flexDirection = FlexDirection.Row;
            uvRow.style.alignItems = Align.Center;
            uvRow.style.paddingLeft = 11f;
            uvRow.style.paddingRight = 25f;
            _uvIcon = new VectorIcon(VectorIcon.Kind.Sun, Gold);
            _uvIcon.style.width = 14f;
            _uvIcon.style.height = 14f;
            _uvIcon.style.flexShrink = 0f;
            _uvIcon.style.marginRight = 7f;
            _uvLabel = new Label();
            _uvLabel.style.color = Text;
            _uvLabel.style.fontSize = 10.5f;
            _uvLabel.style.flexGrow = 1f;
            _uvLabel.style.overflow = Overflow.Hidden;
            _uvLabel.style.textOverflow = TextOverflow.Ellipsis;
            _uvLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _uvLabel.pickingMode = PickingMode.Ignore;
            uvRow.Add(_uvIcon);
            uvRow.Add(_uvLabel);
            vitalsRail.Add(uvRow);

            // Rail first, dial second: the dial hides the rail's rounded tail
            // and makes the capsule appear to emerge directly from the ring.
            vitalsCluster.Add(vitalsRail);
            vitalsCluster.Add(healthBadge);
            col.Add(vitalsCluster);

            col.Add(BuildControlToggle());
            col.Add(BuildStopButton());
            col.Add(BuildInventoryButton());
            col.Add(BuildJournalButton()); // §136: под рюкзаком у правого края

            // Why a manual order failed: a transient note above the combined
            // vitals module, never over the actor's face or the readouts.
            _orderToast = new Label
            {
                style =
                {
                    color = Warn,
                    fontSize = 10f,
                    position = Position.Absolute,
                    left = 190f,
                    // Leave the right-side button column unobstructed: the
                    // journal now occupies the place directly below backpack.
                    right = 80f,
                    top = 78f,
                    paddingLeft = 7f,
                    paddingRight = 7f,
                    paddingTop = 4f,
                    paddingBottom = 4f,
                    backgroundColor = IdentityGlass,
                    whiteSpace = WhiteSpace.Normal,
                    display = DisplayStyle.None,
                }
            };
            SetRadius(_orderToast, 7f);
            SetBorder(_orderToast, new Color(Warn.r, Warn.g, Warn.b, 0.32f), 1f);
            _orderToast.pickingMode = PickingMode.Ignore;
            col.Add(_orderToast);

            return col;
        }

        // §121: «ИИ / Ручное». Состояние всегда читается из снапшота — тумблер
        // ничего не помнит: авторитет по тому, кто управляет персонажем, — сама
        // симуляция, и кнопка, помнящая своё, рано или поздно показывала бы
        // одно, пока колонистка делает другое.
        private VisualElement BuildControlToggle()
        {
            var button = new VisualElement();
            button.style.position = Position.Absolute;
            button.style.left = 12f;
            button.style.top = 12f;
            button.style.width = 88f;
            button.style.height = 34f;
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.backgroundColor = IdentityGlass;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 10f);
            button.style.overflow = Overflow.Hidden;
            button.tooltip = Loc.Get("panel.control.tooltip");

            VisualElement BuildSegment(
                string iconResource,
                string fallbackGlyph,
                string tooltip,
                out Label glyphLabel,
                out VisualElement iconView)
            {
                var segment = new VisualElement();
                segment.style.width = Length.Percent(50f);
                segment.style.height = Length.Percent(100f);
                segment.style.alignItems = Align.Center;
                segment.style.justifyContent = Justify.Center;
                segment.tooltip = tooltip;

                iconView = new VisualElement();
                iconView.style.width = 27f;
                iconView.style.height = 27f;
                iconView.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                iconView.pickingMode = PickingMode.Ignore;
                var iconTexture = Resources.Load<Texture2D>(iconResource);
                if (iconTexture != null)
                {
                    iconView.style.backgroundImage = new StyleBackground(iconTexture);
                }
                else
                {
                    iconView.style.display = DisplayStyle.None;
                }
                segment.Add(iconView);

                // Text is deliberately only a missing-resource fallback: a
                // bad or delayed content import must not remove the control.
                glyphLabel = new Label(fallbackGlyph);
                glyphLabel.style.fontSize = 17f;
                glyphLabel.style.color = Text;
                glyphLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                glyphLabel.style.display = iconTexture == null
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
                glyphLabel.pickingMode = PickingMode.Ignore;
                segment.Add(glyphLabel);
                return segment;
            }

            _controlAiSegment = BuildSegment(
                "HexLive/UI/IdentityAiIcon", "🧠", Loc.Get("panel.control.ai"),
                out _controlAiGlyph, out _controlAiIcon);
            _controlPlayerSegment = BuildSegment(
                "HexLive/UI/IdentityManualIcon", "🎮", Loc.Get("panel.control.manual"),
                out _controlPlayerGlyph, out _controlPlayerIcon);
            SetRadius(_controlAiSegment, 9f);
            SetRadius(_controlPlayerSegment, 9f);
            button.Add(_controlAiSegment);
            button.Add(_controlPlayerSegment);

            var divider = new VisualElement();
            divider.style.position = Position.Absolute;
            divider.style.left = 43f;
            divider.style.top = 7f;
            divider.style.bottom = 7f;
            divider.style.width = 1f;
            divider.style.backgroundColor = StrokeStrong;
            divider.pickingMode = PickingMode.Ignore;
            button.Add(divider);

            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, GoldDim));
            button.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(
                button,
                !_controlAvailable ? Stroke : _manualControlNow ? GoldDim : NeonCyanDim));
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleManualControl();
                evt.StopPropagation();
            });

            _controlButton = button;
            return button;
        }

        private void ToggleManualControl()
        {
            if (_runner == null || !_runner.SupportsNpcCommands ||
                !_controlAvailable || !NpcSelection.HasSelection)
            {
                return;
            }

            _runner.EnqueueCommand(new HexLive.Simulation.Runtime.SetManualControlCommand(
                new HexLive.Simulation.Common.EntityId(NpcSelection.SelectedId), !_manualControlNow));
        }

        // §121: одиночное «Отставить» — снять текущий приказ, НЕ выключая
        // ручной режим. У группы эта кнопка есть в групповой карточке
        // (GroupStopCommand); у одиночки до сих пор не было способа отменить
        // приказ иначе, чем отдать новый.
        private VisualElement BuildStopButton()
        {
            var button = new Label(Loc.Get("menu.stop"));
            button.style.position = Position.Absolute;
            button.style.left = 12f;
            button.style.top = 52f;
            button.style.width = 88f;
            button.style.height = 24f;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.fontSize = 11f;
            button.style.color = Text;
            button.style.backgroundColor = IdentityGlass;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 10f);
            button.style.display = DisplayStyle.None;
            button.tooltip = Loc.Get("menu.stop.tooltip");
            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, GoldDim));
            button.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(button, StrokeStrong));
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                StopSingle();
                evt.StopPropagation();
            });
            _stopButton = button;
            return button;
        }

        private void StopSingle()
        {
            if (_runner == null || !_runner.SupportsNpcCommands ||
                !_controlAvailable || !_manualControlNow || !NpcSelection.HasSelection)
            {
                return;
            }

            _runner.EnqueueCommand(new HexLive.Simulation.Runtime.StopCommand(
                new HexLive.Simulation.Common.EntityId(NpcSelection.SelectedId)));
        }

        // §121: тумблер прячется целиком, когда приказы отдавать некому —
        // на удалённом мире колония общая, и увести чужую колонистку нельзя.
        private void RefreshControlToggle(NpcSnapshot npc)
        {
            if (_controlButton == null)
            {
                return;
            }

            var available = _runner != null && _runner.SupportsNpcCommands &&
                _runner.CanControlNpc(npc.Id) && npc.Health > 0f;
            _controlAvailable = available;
            var readable = _runner != null;
            _controlButton.style.display = readable ? DisplayStyle.Flex : DisplayStyle.None;
            if (_orderToast != null)
            {
                _orderToast.style.display = DisplayStyle.None;
            }
            if (!available)
            {
                _controlButton.tooltip = Loc.Get("panel.control.readonly");
                _controlAiSegment.style.backgroundColor = Color.clear;
                _controlPlayerSegment.style.backgroundColor = Color.clear;
                _controlAiGlyph.style.color = TextMute;
                _controlPlayerGlyph.style.color = TextMute;
                _controlAiIcon.style.opacity = 0.35f;
                _controlPlayerIcon.style.opacity = 0.35f;
                SetBorderColor(_controlButton, Stroke);
                if (_stopButton != null)
                {
                    _stopButton.style.display = DisplayStyle.None;
                }
                return;
            }

            // §121.9: на удалёнке сервер может снять ручной режим сам (истёк
            // лиз, операторский force-release) — локального события об этом
            // нет, поэтому край «был ручной → стал ИИ» ловится по снапшоту.
            if (_manualEdgeNpcId == npc.Id.Value && _manualEdgeWas &&
                !npc.IsManualControl && _runner != null && _runner.Link.IsRemote)
            {
                Input.ManualOrderFeedback.ReportTerm(npc.Id.Value, "toast.manual_expired");
            }

            _manualEdgeNpcId = npc.Id.Value;
            _manualEdgeWas = npc.IsManualControl;

            _manualControlNow = npc.IsManualControl;
            if (_stopButton != null)
            {
                _stopButton.style.display = _manualControlNow
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
            _controlButton.tooltip = Loc.Get("panel.control.tooltip");
            _controlAiSegment.style.backgroundColor = _manualControlNow
                ? Color.clear
                : new Color(NeonCyan.r, NeonCyan.g, NeonCyan.b, 0.22f);
            _controlPlayerSegment.style.backgroundColor = _manualControlNow
                ? new Color(Gold.r, Gold.g, Gold.b, 0.24f)
                : Color.clear;
            _controlAiGlyph.style.color = _manualControlNow ? TextMute : NeonCyan;
            _controlPlayerGlyph.style.color = _manualControlNow ? Gold : TextMute;
            _controlAiIcon.style.opacity = _manualControlNow ? 0.42f : 1f;
            _controlPlayerIcon.style.opacity = _manualControlNow ? 1f : 0.42f;
            SetBorderColor(_controlButton, _manualControlNow ? GoldDim : NeonCyanDim);

            if (_orderToast != null)
            {
                var fresh = ManualOrderFeedback.IsFresh(npc.Id.Value);
                _orderToast.style.display = fresh ? DisplayStyle.Flex : DisplayStyle.None;
                if (fresh)
                {
                    _orderToast.text = Loc.Get(ManualOrderFeedback.ReasonKey);
                }
            }
        }

        // Spec §51: raised neon-glass backpack control in the card's upper-right.
        private VisualElement BuildInventoryButton()
        {
            var button = new VisualElement();
            button.style.position = Position.Absolute;
            button.style.right = 12f;
            button.style.top = 12f;
            button.style.width = 58f;
            button.style.height = 58f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.backgroundColor = new Color(0.014f, 0.058f, 0.074f, 0.94f);
            SetBorder(button, NeonCyanDim, 1.5f);
            SetRadius(button, 18f);
            button.tooltip = Loc.Get("inv.button");

            // UI Toolkit has no dependable runtime shadow here, so a second
            // translucent plate and a luminous underline give the control the
            // same depth without introducing a texture asset.
            var inventoryButtonInner = new VisualElement();
            inventoryButtonInner.style.position = Position.Absolute;
            inventoryButtonInner.style.left = 4f;
            inventoryButtonInner.style.right = 4f;
            inventoryButtonInner.style.top = 4f;
            inventoryButtonInner.style.bottom = 4f;
            inventoryButtonInner.style.backgroundColor = new Color(0.045f, 0.095f, 0.115f, 0.72f);
            SetBorder(inventoryButtonInner, StrokeStrong, 1f);
            SetRadius(inventoryButtonInner, 14f);
            inventoryButtonInner.pickingMode = PickingMode.Ignore;
            button.Add(inventoryButtonInner);

            var inventoryButtonAccent = new VisualElement();
            inventoryButtonAccent.style.position = Position.Absolute;
            inventoryButtonAccent.style.left = 15f;
            inventoryButtonAccent.style.right = 15f;
            inventoryButtonAccent.style.bottom = 5f;
            inventoryButtonAccent.style.height = 2f;
            inventoryButtonAccent.style.backgroundColor = NeonCyan;
            SetRadius(inventoryButtonAccent, 1f);
            inventoryButtonAccent.pickingMode = PickingMode.Ignore;
            button.Add(inventoryButtonAccent);

            var backpackIcon = new VisualElement();
            backpackIcon.style.width = 46f;
            backpackIcon.style.height = 46f;
            backpackIcon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            backpackIcon.pickingMode = PickingMode.Ignore;
            var backpackTexture = Resources.Load<Texture2D>("HexLive/UI/IdentityBackpackIcon");
            if (backpackTexture != null)
            {
                backpackIcon.style.backgroundImage = new StyleBackground(backpackTexture);
            }
            else
            {
                backpackIcon.style.display = DisplayStyle.None;
            }
            button.Add(backpackIcon);

            var glyph = new Label("🎒");
            glyph.style.fontSize = 26f;
            glyph.style.color = Text;
            glyph.style.display = backpackTexture == null
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            glyph.pickingMode = PickingMode.Ignore;
            glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.Add(glyph);

            button.RegisterCallback<MouseEnterEvent>(_ =>
            {
                button.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.20f);
                inventoryButtonInner.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.12f);
                inventoryButtonAccent.style.backgroundColor = Gold;
                SetBorderColor(button, GoldDim);
            });
            button.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                button.style.backgroundColor = new Color(0.014f, 0.058f, 0.074f, 0.94f);
                inventoryButtonInner.style.backgroundColor = new Color(0.045f, 0.095f, 0.115f, 0.72f);
                inventoryButtonAccent.style.backgroundColor = NeonCyan;
                SetBorderColor(button, NeonCyanDim);
            });
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleInventory();
                evt.StopPropagation();
            });

            _inventoryButton = button;
            return button;
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
            _characterTitle = BuildSheetTab(tabs, SheetTab.Character);
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

            // §76: «Природа» — six innate characteristics, plus the perk badges
            // the extremes earn. Two across: without a bar a row is just
            // "7 · Неприхотливость", so it wants width for the word rather than
            // for a track, and three-up clipped the longer Russian names.
            _natureContainer = new VisualElement();
            var attrGrid = new VisualElement();
            attrGrid.style.flexDirection = FlexDirection.Row;
            attrGrid.style.flexWrap = Wrap.Wrap;
            _attrBindings.Clear();
            foreach (var row in AttributeRows)
            {
                attrGrid.Add(BuildSheetCell(row, 32f, _attrBindings));
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
                _skillsContainer.Add(BuildSheetCell(row, 24f, _skillBindings));
            }

            col.Add(_skillsContainer);

            // §126: «Характер» — черты КАРТОЧКАМИ, во всю ширину и с описанием
            // на самой странице, а не бейджами с тултипом, как перки §76.6.
            // Причина в том, чем черта отличается от перка: перков у человека
            // до дюжины и каждый читается из своей строки характеристик
            // («Сила 9» → «Силачка»), а черт одна-две, и по имени они НЕ
            // читаются — «Неряха» ничего не говорит про то, что она не пойдёт
            // мыться вообще никогда. Объяснение обязано лежать рядом с именем.
            _characterContainer = new VisualElement();
            _characterContainer.style.flexDirection = FlexDirection.Column;

            _characterEmpty = new Label();
            _characterEmpty.style.color = TextMute;
            _characterEmpty.style.fontSize = 12;
            _characterEmpty.style.whiteSpace = WhiteSpace.Normal;
            _characterEmpty.style.marginTop = 4f;
            _characterContainer.Add(_characterEmpty);

            col.Add(_characterContainer);

            _sheetTooltipAnchor = col;
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
            _hoveredNeed = null;
            _hoveredNeedAnchor = null;
            HideEffectTooltip();

            _sheetTab = tab;
            _needsContainer.style.display = tab == SheetTab.Needs ? DisplayStyle.Flex : DisplayStyle.None;
            _natureContainer.style.display = tab == SheetTab.Nature ? DisplayStyle.Flex : DisplayStyle.None;
            _skillsContainer.style.display = tab == SheetTab.Skills ? DisplayStyle.Flex : DisplayStyle.None;
            _characterContainer.style.display = tab == SheetTab.Character ? DisplayStyle.Flex : DisplayStyle.None;

            _needsTitle.style.color = tab == SheetTab.Needs ? Gold : TextMute;
            _natureTitle.style.color = tab == SheetTab.Nature ? Gold : TextMute;
            _skillsTitle.style.color = tab == SheetTab.Skills ? Gold : TextMute;
            _characterTitle.style.color = tab == SheetTab.Character ? Gold : TextMute;

            // Refresh() early-returns while the snapshot tick is unchanged, so
            // without this the new page stays blank until the sim ticks over.
            _refreshedTick = -1;
        }

        // §76: a sheet row — a big number and a name. NO progress bar, on
        // purpose: a bar draws a ceiling, and these have none. A characteristic
        // is a rating that can keep climbing, so "7" is the honest reading
        // while "7/10 filled" would promise a finish line that does not exist.
        // (That is also why the value carries no denominator.)
        //
        // Hovering the cell explains what the line actually does — the numbers
        // are meaningless to a player who has not read the spec, and a rating
        // with no bar gives even less of a hint than one with.
        private VisualElement BuildSheetCell(SheetConfig config, float widthPercent,
            List<SheetBinding> bindings)
        {
            var cell = new VisualElement();
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.width = Length.Percent(widthPercent);
            cell.style.paddingLeft = 10f;
            cell.style.paddingRight = 10f;
            cell.style.paddingTop = 6f;
            cell.style.paddingBottom = 6f;
            cell.style.marginRight = 6f;
            cell.style.marginTop = 4f;
            cell.style.marginBottom = 4f;
            SetRadius(cell, 8f);

            // The rating, reading as the headline of the row.
            var value = new Label("—");
            value.style.color = config.Color;
            value.style.fontSize = 20;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            value.style.flexShrink = 0f;
            value.style.minWidth = 26f;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.marginRight = 9f;
            value.pickingMode = PickingMode.Ignore;
            cell.Add(value);

            var label = new Label();
            label.style.color = TextDim;
            label.style.fontSize = 12;
            label.style.flexGrow = 1f;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.pickingMode = PickingMode.Ignore;
            cell.Add(label);

            // The affordance: a faint "?" that says the row can be read. It
            // brightens with the rest of the cell on hover, so it never nags.
            var hint = new Label("?");
            hint.style.color = new Color(1f, 1f, 1f, 0.18f);
            hint.style.fontSize = 11;
            hint.style.unityFontStyleAndWeight = FontStyle.Bold;
            hint.style.flexShrink = 0f;
            hint.style.marginLeft = 4f;
            hint.pickingMode = PickingMode.Ignore;
            cell.Add(hint);

            cell.RegisterCallback<MouseEnterEvent>(_ =>
            {
                cell.style.backgroundColor = Raised;
                hint.style.color = Gold;
                label.style.color = Text;
                ShowSheetTooltip(config, _sheetTooltipAnchor ?? cell);
            });
            cell.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                cell.style.backgroundColor = Color.clear;
                hint.style.color = new Color(1f, 1f, 1f, 0.18f);
                label.style.color = TextDim;
                HideEffectTooltip();
            });

            bindings.Add(new SheetBinding
            {
                Config = config,
                Label = label,
                Value = value
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
            SetRadius(cell, 8f);

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

            cell.RegisterCallback<MouseEnterEvent>(_ =>
            {
                _hoveredNeed = NeedKind.Temperature;
                _hoveredNeedAnchor = cell;
                cell.style.backgroundColor = Raised;
                ShowNeedTooltip(NeedKind.Temperature, cell, _currentTooltipNpc);
            });
            cell.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (_hoveredNeed == NeedKind.Temperature)
                {
                    _hoveredNeed = null;
                    _hoveredNeedAnchor = null;
                }

                cell.style.backgroundColor = Color.clear;
                HideEffectTooltip();
            });

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
            SetRadius(cell, 8f);

            var need = NeedKindFor(config.Key);

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

            cell.RegisterCallback<MouseEnterEvent>(_ =>
            {
                _hoveredNeed = need;
                _hoveredNeedAnchor = cell;
                cell.style.backgroundColor = Raised;
                ShowNeedTooltip(need, cell, _currentTooltipNpc);
            });
            cell.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (_hoveredNeed == need)
                {
                    _hoveredNeed = null;
                    _hoveredNeedAnchor = null;
                }

                cell.style.backgroundColor = Color.clear;
                HideEffectTooltip();
            });

            _needBindings.Add(new NeedBinding
            {
                Config = config,
                Cell = cell,
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
            _rosterTick = int.MinValue;

            _needsTitle.text = Loc.Get("panel.needs");
            _natureTitle.text = Loc.Get("panel.nature");
            _skillsTitle.text = Loc.Get("panel.skills");
            _characterTitle.text = Loc.Get("panel.character");
            _relationsTitle.text = Loc.Get("panel.relations");
            if (_groupStopLabel != null) _groupStopLabel.text = Loc.Get("group.stop");
            if (_clanRosterHeader != null) _clanRosterHeader.text = Loc.Get("roster.clan");
            if (_outsiderRosterHeader != null)
                _outsiderRosterHeader.text = Loc.Get("roster.outsiders");
            if (_invDropActionLabel != null) _invDropActionLabel.text = Loc.Get("inv.action.drop");
            if (_invReadOnlyLabel != null) _invReadOnlyLabel.text = Loc.Get("inv.readonly");
            if (_invPrimaryActionLabel != null && _invSelectedId != null)
                _invPrimaryActionLabel.text = Loc.Get(_invSelectedWorn
                    ? "inv.action.stow" : "inv.action.wear");

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

            _perkSig.Clear();
            // §126: то же и для карточек черт — они перестраиваются только при
            // смене НАБОРА, а смена языка набор не двигает.
            _traitSig.Clear();
            // The relation tabs carry localized names (Loc.NpcName), so the
            // cached signature must not survive a language switch either.
            _relationSig.Clear();
            _relationSigSelected = int.MinValue;
            _relationsBuiltEmpty = false;
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

            if (_controlButton != null)
            {
                _controlButton.tooltip = Loc.Get("panel.control.tooltip");
                _controlAiSegment.tooltip = Loc.Get("panel.control.ai");
                _controlPlayerSegment.tooltip = Loc.Get("panel.control.manual");
            }

            if (_inventoryButton != null)
            {
                _inventoryButton.tooltip = Loc.Get("inv.button");
            }

            if (_healthButton != null)
            {
                _healthButton.tooltip = Loc.Get("panel.health");
            }

            LocalizeJournal(); // §136

            // Inventory window (spec §51) — static chrome + force a rebuild so
            // the item rows / open detail re-localize on the next refresh.
            if (_inventoryTitle != null)
            {
                _inventoryTitle.text = Loc.Get("panel.inventory");
            }
            if (_outfitLockLabel != null)
            {
                _outfitLockLabel.text = Loc.Get("inv.outfit_lock");
                _outfitLockButton.tooltip = Loc.Get("inv.outfit_lock.tooltip");
            }

            if (_inventoryBackpackTabLabel != null)
            {
                _inventoryBackpackTabLabel.text = Loc.Get("craft.tab.backpack");
            }
            if (_inventoryClothesTabLabel != null)
            {
                _inventoryClothesTabLabel.text = Loc.Get("inv.tab.clothes");
            }
            if (_inventoryCraftTabLabel != null)
            {
                _inventoryCraftTabLabel.text = Loc.Get("craft.tab.craft");
            }
            if (_craftResourcesTitle != null)
            {
                _craftResourcesTitle.text = Loc.Get("craft.resources").ToUpperInvariant();
            }

            if (_invBackLabel != null)
            {
                _invBackLabel.text = Loc.Get("inv.back");
            }
            foreach (var pair in _invLayerButtons)
            {
                pair.Value.tooltip = Loc.Get(InventoryLayerLocKey(pair.Key));
            }

            _invSig = null;
            _craftSig = null;

            // Limb-health window (spec §57) — rows re-localize on the next
            // refresh (the tick gate above is already invalidated).
            if (_healthTitle != null)
            {
                _healthTitle.text = Loc.Get("panel.health");
            }
            if (_healthDollError != null)
            {
                _healthDollError.text = Loc.Get("health.doll_unavailable");
            }

        }

        // ── helpers ───────────────────────────────────────────────────────

        private static NpcSnapshot FindNpc(WorldSnapshot snapshot, int id)
        {
            return FindNpc(snapshot, id, out _);
        }

        private static NpcSnapshot FindNpc(WorldSnapshot snapshot, int id, out bool isDead)
        {
            isDead = false;
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


            foreach (var corpse in snapshot.Corpses)
            {
                if (corpse.Id.Value == id)
                {
                    isDead = true;
                    return corpse;
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
            private float _value;
            private Color _color;

            // Толщина обводки. Кольца нужд оставляют её по умолчанию; компактный
            // HP-instrument задаёт свою толщину независимо от лица.
            public float LineWidth { get; set; } = 5f;

            public RingMeter(float value, Color color)
            {
                _value = Mathf.Clamp01(value);
                _color = color;
                pickingMode = PickingMode.Ignore;
                generateVisualContent += OnGenerate;
            }

            // §105: круг здоровья живёт весь кадр и меняется каждый тик —
            // в отличие от колец нужд, которые пересоздаются вместе со строкой.
            // Перерисовка запрашивается только на РЕАЛЬНОМ изменении: панель
            // обновляется каждый кадр, и безусловный MarkDirtyRepaint гонял бы
            // генератор меша впустую.
            public void Set(float value, Color color)
            {
                value = Mathf.Clamp01(value);
                if (Mathf.Abs(value - _value) < 0.0005f && color == _color)
                {
                    return;
                }

                _value = value;
                _color = color;
                MarkDirtyRepaint();
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
                p.lineWidth = LineWidth;

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
        // временная мера: снимок появляется не раньше ближайшей дневной
        // фотосессии, а до тех пор буква — единственное, что вообще есть.
        // Мёртвые лица тоже показываются: кэш переживает тело.
        private void ApplyRelationFace(VisualElement avatar, int otherId, Label initial)
        {
            if (_portraitCache == null || !_portraitCache.TryGet(otherId, out var face))
            {
                return;
            }

            avatar.style.backgroundImage = new StyleBackground(face);
            // §80: снимок теперь ПРОЗРАЧНЫЙ (вырезка без фона), поэтому под ним
            // нужна подложка — иначе лицо висит в дырке. Буква прячется: поверх
            // лица она нечитаема.
            avatar.style.backgroundColor = PortraitBackdrop;
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
