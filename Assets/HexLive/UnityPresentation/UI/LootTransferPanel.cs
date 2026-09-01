#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{

/// <summary>
/// §128 Kenshi-style two-person inventory exchange. Both panes are projections
/// of the ordinary NPC snapshots; every drop becomes one authoritative
/// <see cref="TransferInventoryCommand"/>.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public sealed class LootTransferPanel : MonoBehaviour
{
    [SerializeField] private float _uiScale = 1f;

    private ISimulationSource? _runner;
    private UIDocument _document = null!;
    private VisualElement _root = null!;
    private VisualElement _frame = null!;
    private VisualElement _leftPane = null!;
    private VisualElement _rightPane = null!;
    private ScrollView _leftScroll = null!;
    private ScrollView _rightScroll = null!;
    private VisualElement _leftContent = null!;
    private VisualElement _rightContent = null!;
    private Label _leftTitle = null!;
    private Label _rightTitle = null!;
    private Label _leftCapacity = null!;
    private Label _rightCapacity = null!;
    private Label _status = null!;
    private Label _title = null!;
    private Label _arrows = null!;
    private VisualElement _quantityOverlay = null!;
    private Label _quantityTitle = null!;
    private SliderInt _quantitySlider = null!;
    private IntegerField _quantityInput = null!;
    private Button _quantityConfirm = null!;
    private Button _quantityCancel = null!;

    // §153.1: то же окно, но отдачей в одну сторону. Отдельного интерфейса
    // передачи нет по замыслу — это была бы вторая версия §128 со своими
    // багами; вместо неё окно знает про РЕЖИМ и гасит обратное направление.
    private bool _gift;

    // Отказ обязан пережить конец жеста: ClearDrag возвращает подсказку по
    // умолчанию, и без этого флага «забрать нельзя» гасло бы в том же кадре,
    // в котором появилось, — то есть его бы никто не прочитал.
    private bool _statusHeld;

    private int _looterId = -1;

    // §128: id второй стороны для перетаскивания. Для человека это его EntityId,
    // для вещи (§128.5) — МИНУС id объекта: жесту нужен один int, по которому он
    // отличает «своя панель» от «чужой», а пространства id людей и объектов
    // независимы и оба начинаются с единицы. Минус разводит их без второго поля
    // в каждом DragItem. Настоящий id вещи лежит в _otherObjectId.
    private int _otherId = -1;

    private int _otherObjectId = -1;
    private int _lastTick = -1;
    private string _signature = string.Empty;
    private bool _pending;
    private int _pendingTick;
    private string _pendingSignature = string.Empty;

    private DragItem _drag;
    private bool _dragPrepared;
    private bool _dragMoved;
    private int _dragPointerId = -1;
    private Vector2 _dragStart;
    private VisualElement? _dragCell;
    private VisualElement? _dragGhost;
    private bool _dragDoubleClick;
    private readonly DoubleClickWatch _doubleClick = new();

    // Bug #344: every cross-inventory move of a visible stack pauses here so
    // the player chooses an exact count. One state serves loot, gifts and
    // world containers because all three already converge on DropOn.
    private bool _quantityOpen;
    private bool _quantitySync;
    private int _quantityDestinationId;
    private DragItem _quantityItem;

    // §128.1a: снапшот приходит 4 раза в секунду и может пересобрать раскладку
    // прямо посреди жеста. Читать его продолжаем, дерево — не трогаем.
    private bool _rebuildDeferred;

    // §128.4: единственное отложенное действие панели — довести забранную
    // носимую вещь до надетой, когда она доедет в переноску получателя.
    private string _autoWearDefinitionId = string.Empty;
    private int _autoWearDeadlineTick = -1;
    private int _autoWearBaseline;

    private readonly List<VisualElement> _slotCells = new();
    private readonly List<VisualElement> _slotIcons = new();
    private readonly List<Label> _slotBadges = new();
    private readonly List<Label> _slotGlyphs = new();
    private readonly List<VisualElement> _containerCards = new();
    private int _densityTier;

    private static LootTransferPanel? _instance;

    public static bool IsOpen { get; private set; }
    public static bool PointerOverPanel { get; private set; }

    private static readonly float[] CellSizes = { 42f, 36f, 32f, 28f };
    private static readonly float[] Gaps = { 5f, 4f, 3f, 2f };
    private const float DragThreshold = 6f;

    // §128.1: со скроллом мельчить бессмысленно — две ступени экономят место,
    // дальше панель скроллится. Хвост лестницы (32/28) оставлен в таблицах как
    // след прежнего поведения и намеренно не используется.
    private const int MaxDensityTier = 1;
    private const float GhostSize = 40f;

    // §128.4: окно, в течение которого «забрал носимое» доводится до «надел».
    // 40 тиков ≈ 10 секунд — этого хватает на подход и передачу.
    private const int AutoWearWindowTicks = 40;

    private static readonly Color Text = new(0.906f, 0.925f, 0.937f);
    private static readonly Color TextDim = new(0.655f, 0.702f, 0.733f);
    private static readonly Color TextMute = new(0.400f, 0.447f, 0.478f);
    private static readonly Color Panel = new(0.075f, 0.094f, 0.110f, 0.985f);
    private static readonly Color Raised = new(0.133f, 0.165f, 0.192f);
    private static readonly Color Track = new(0.047f, 0.063f, 0.075f);
    private static readonly Color Stroke = new(1f, 1f, 1f, 0.14f);
    private static readonly Color StrokeStrong = new(1f, 1f, 1f, 0.24f);
    private static readonly Color Gold = new(0.941f, 0.706f, 0.361f);
    private static readonly Color GoldDim = new(0.941f, 0.706f, 0.361f, 0.55f);

    private readonly struct DragItem
    {
        public DragItem(
            int ownerId, InventoryItemSource source, int index, int count, string definitionId)
        {
            OwnerId = ownerId;
            Source = source;
            Index = index;
            Count = count;
            DefinitionId = definitionId;
        }

        public int OwnerId { get; }
        public InventoryItemSource Source { get; }
        public int Index { get; }
        public int Count { get; }
        public string DefinitionId { get; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
        IsOpen = false;
        PointerOverPanel = false;
    }

    public void SetRunner(ISimulationSource runner) => _runner = runner;

    private void Awake()
    {
        _instance = this;
        _document = GetComponent<UIDocument>();
        var reference = new Vector2Int(
            Mathf.RoundToInt(1920f / Mathf.Max(0.25f, _uiScale)),
            Mathf.RoundToInt(1080f / Mathf.Max(0.25f, _uiScale)));
        var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (baseSettings != null)
        {
            var settings = Instantiate(baseSettings);
            settings.name = "LootTransferPanelSettings";
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.referenceResolution = reference;
            settings.match = 1f;
            settings.sortingOrder = 166;
            _document.panelSettings = settings;
        }

        BuildUi();
        Hide();
    }

    private void OnDestroy()
    {
        if (!ReferenceEquals(_instance, this)) return;
        _instance = null;
        IsOpen = false;
        PointerOverPanel = false;
    }

    public static void Open(int looterId, int otherId)
    {
        _instance?.OpenInternal(looterId, otherId, gift: false);
    }

    /// <summary>
    /// §153.1: «Подарить» — то же окно обмена, но цель может быть на ногах и в
    /// сознании, а вещи ходят только НАЛЕВО→НАПРАВО. Забрать что-нибудь у
    /// живого человека, пока он смотрит, подарок не разрешает: это уже §111,
    /// и оно должно называться своим именем.
    /// </summary>
    public static void OpenGift(int looterId, int otherId)
    {
        _instance?.OpenInternal(looterId, otherId, gift: true);
    }

    /// <summary>
    /// §128.5: то же окно, но справа ВЕЩЬ — истлевшее тело, снятый рюкзак,
    /// аптечка. Вторая сторона называется id объекта, а не человека.
    /// </summary>
    public static void OpenContainer(int looterId, int objectId)
    {
        _instance?.OpenContainerInternal(looterId, objectId);
    }

    public static void Close() => _instance?.Hide();

    private void OpenInternal(int looterId, int otherId, bool gift)
    {
        if (_runner == null || !_runner.IsReady || !_runner.SupportsNpcCommands ||
            looterId < 0 || otherId < 0 || looterId == otherId)
        {
            return;
        }

        var snapshot = _runner.CreateSnapshot();
        var looter = FindNpc(snapshot, looterId);
        var other = FindNpc(snapshot, otherId);
        // §128: несомый — цель, только если он на руках у САМОГО обыскивающего.
        // §153.1: подарить можно и стоящей в сознании — зеркало сим-предиката
        // PlayerLootTargets.TryResolve с направлением Give.
        if (looter == null || other == null || !_runner.CanControlNpc(looter.Id) ||
            looter.Health <= 0f ||
            !(gift ? IsGiftable(other) : IsLootable(other)) ||
            (other.CarriedByNpcId is not null && other.CarriedByNpcId != looterId))
        {
            return;
        }

        _gift = gift;
        _title.text = Loc.Get(gift ? "gift.title" : "loot.title");
        _arrows.text = gift ? "➜" : "⇄";
        _looterId = looterId;
        _otherId = otherId;
        _otherObjectId = -1;
        _lastTick = -1;
        _signature = string.Empty;
        _pending = false;
        _statusHeld = false;
        _rebuildDeferred = false;
        ClearAutoWear();
        _doubleClick.Reset();
        ClearDrag();
        _root.style.display = DisplayStyle.Flex;
        IsOpen = true;
        LayoutFrame();
        Refresh(force: true);
    }

    private void OpenContainerInternal(int looterId, int objectId)
    {
        if (_runner == null || !_runner.IsReady || !_runner.SupportsNpcCommands ||
            looterId < 0 || objectId < 0)
        {
            return;
        }

        var snapshot = _runner.CreateSnapshot();
        var looter = FindNpc(snapshot, looterId);
        var container = FindContainer(snapshot, objectId);
        if (looter == null || container == null || !_runner.CanControlNpc(looter.Id) ||
            looter.Health <= 0f)
        {
            return;
        }

        _gift = false;
        _title.text = Loc.Get("loot.title");
        _arrows.text = "⇄";
        _looterId = looterId;
        _otherObjectId = objectId;
        _otherId = -objectId;
        _lastTick = -1;
        _signature = string.Empty;
        _pending = false;
        _statusHeld = false;
        _rebuildDeferred = false;
        ClearAutoWear();
        _doubleClick.Reset();
        ClearDrag();
        _root.style.display = DisplayStyle.Flex;
        IsOpen = true;
        LayoutFrame();
        Refresh(force: true);
    }

    /// <summary>§128.5: вещь, у которой есть что показать в окне обыска.</summary>
    private static ObjectSnapshot? FindContainer(WorldSnapshot snapshot, int objectId)
    {
        foreach (var obj in snapshot.Objects)
        {
            if (obj.Id.Value == objectId) return obj;
        }

        return null;
    }

    private void Update()
    {
        if (IsOpen)
        {
            Refresh(force: false);
        }
    }

    private void BuildUi()
    {
        _root = _document.rootVisualElement;
        _root.pickingMode = PickingMode.Ignore;
        _root.RegisterCallback<GeometryChangedEvent>(_ => LayoutFrame());

        _frame = new VisualElement();
        _frame.name = "loot-transfer-frame";
        _frame.style.position = Position.Absolute;
        _frame.style.width = 1080f;
        _frame.style.height = 650f;
        _frame.style.backgroundColor = Panel;
        _frame.style.paddingLeft = 14f;
        _frame.style.paddingRight = 14f;
        _frame.style.paddingTop = 12f;
        _frame.style.paddingBottom = 12f;
        _frame.style.overflow = Overflow.Hidden;
        _frame.pickingMode = PickingMode.Position;
        SetBorder(_frame, StrokeStrong, 1f);
        SetRadius(_frame, 15f);
        _frame.RegisterCallback<PointerEnterEvent>(_ => PointerOverPanel = true);
        _frame.RegisterCallback<PointerLeaveEvent>(_ => PointerOverPanel = false);
        // §128.1a: жест ведёт рамка, потому что она переживает пересборку
        // содержимого. Отпускание разбирается ЗДЕСЬ, а не на панели-приёмнике:
        // с захваченным указателем цель события — всегда рамка.
        _frame.RegisterCallback<PointerMoveEvent>(UpdateDrag);
        _frame.RegisterCallback<PointerUpEvent>(OnFramePointerUp);
        _frame.RegisterCallback<PointerCaptureOutEvent>(_ => ClearDrag());

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 9f;
        _title = new Label(Loc.Get("loot.title"));
        _title.style.color = Gold;
        _title.style.fontSize = 17f;
        _title.style.unityFontStyleAndWeight = FontStyle.Bold;
        _title.style.flexGrow = 1f;
        header.Add(_title);
        var close = new Label("✕");
        close.style.color = TextDim;
        close.style.fontSize = 15f;
        close.style.width = 26f;
        close.style.height = 24f;
        close.style.unityTextAlign = TextAnchor.MiddleCenter;
        close.RegisterCallback<PointerEnterEvent>(_ => close.style.color = Text);
        close.RegisterCallback<PointerLeaveEvent>(_ => close.style.color = TextDim);
        close.RegisterCallback<PointerDownEvent>(evt =>
        {
            Hide();
            evt.StopPropagation();
        });
        header.Add(close);
        _frame.Add(header);

        var panes = new VisualElement();
        panes.name = "loot-transfer-two-windows";
        panes.style.flexDirection = FlexDirection.Row;
        panes.style.flexGrow = 1f;
        panes.style.minHeight = 0f;
        _leftPane = BuildPersonPane(out _leftTitle, out _leftCapacity, out _leftScroll);
        _rightPane = BuildPersonPane(out _rightTitle, out _rightCapacity, out _rightScroll);
        _leftContent = _leftScroll.contentContainer;
        _rightContent = _rightScroll.contentContainer;
        _leftPane.name = "loot-own-window";
        _rightPane.name = "loot-target-window";
        panes.Add(_leftPane);
        _arrows = new Label("⇄");
        _arrows.style.width = 34f;
        _arrows.style.flexShrink = 0f;
        _arrows.style.color = Gold;
        _arrows.style.fontSize = 21f;
        _arrows.style.unityTextAlign = TextAnchor.MiddleCenter;
        _arrows.pickingMode = PickingMode.Ignore;
        panes.Add(_arrows);
        panes.Add(_rightPane);
        _frame.Add(panes);

        _status = new Label(Loc.Get("loot.drag_hint"));
        _status.style.color = TextMute;
        _status.style.fontSize = 11f;
        _status.style.unityTextAlign = TextAnchor.MiddleCenter;
        _status.style.marginTop = 7f;
        _frame.Add(_status);
        BuildQuantityPicker();
        _root.Add(_frame);
    }

    private void BuildQuantityPicker()
    {
        _quantityOverlay = new VisualElement { name = "loot-quantity-overlay" };
        _quantityOverlay.style.position = Position.Absolute;
        _quantityOverlay.style.left = 0f;
        _quantityOverlay.style.right = 0f;
        _quantityOverlay.style.top = 0f;
        _quantityOverlay.style.bottom = 0f;
        _quantityOverlay.style.alignItems = Align.Center;
        _quantityOverlay.style.justifyContent = Justify.Center;
        _quantityOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.68f);
        _quantityOverlay.pickingMode = PickingMode.Position;

        var card = new VisualElement { name = "loot-quantity-card" };
        card.style.width = 430f;
        card.style.paddingLeft = 18f;
        card.style.paddingRight = 18f;
        card.style.paddingTop = 16f;
        card.style.paddingBottom = 16f;
        card.style.backgroundColor = Raised;
        SetBorder(card, GoldDim, 1f);
        SetRadius(card, 12f);

        _quantityTitle = new Label();
        _quantityTitle.style.color = Text;
        _quantityTitle.style.fontSize = 15f;
        _quantityTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        _quantityTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
        _quantityTitle.style.marginBottom = 12f;
        card.Add(_quantityTitle);

        var chooser = new VisualElement();
        chooser.style.flexDirection = FlexDirection.Row;
        chooser.style.alignItems = Align.Center;
        _quantitySlider = new SliderInt { name = "loot-quantity-slider" };
        _quantitySlider.style.flexGrow = 1f;
        _quantitySlider.style.marginRight = 12f;
        _quantityInput = new IntegerField { name = "loot-quantity-input" };
        _quantityInput.style.width = 76f;
        _quantityInput.isDelayed = false;
        chooser.Add(_quantitySlider);
        chooser.Add(_quantityInput);
        card.Add(chooser);

        var buttons = new VisualElement();
        buttons.style.flexDirection = FlexDirection.Row;
        buttons.style.justifyContent = Justify.FlexEnd;
        buttons.style.marginTop = 14f;
        _quantityCancel = QuantityButton(Loc.Get("loot.quantity_cancel"), CancelQuantity);
        _quantityCancel.style.marginRight = 8f;
        _quantityConfirm = QuantityButton(Loc.Get("loot.quantity_confirm"), ConfirmQuantity);
        buttons.Add(_quantityCancel);
        buttons.Add(_quantityConfirm);
        card.Add(buttons);

        _quantitySlider.RegisterValueChangedCallback(evt =>
        {
            if (_quantitySync) return;
            _quantitySync = true;
            _quantityInput.SetValueWithoutNotify(evt.newValue);
            _quantitySync = false;
        });
        _quantityInput.RegisterValueChangedCallback(evt =>
        {
            if (_quantitySync) return;
            _quantitySync = true;
            _quantitySlider.SetValueWithoutNotify(Mathf.Clamp(
                evt.newValue, _quantitySlider.lowValue, _quantitySlider.highValue));
            _quantitySync = false;
        });
        _quantityOverlay.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                ConfirmQuantity();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                CancelQuantity();
                evt.StopPropagation();
            }
        });

        _quantityOverlay.Add(card);
        _frame.Add(_quantityOverlay);
        _quantityOverlay.style.display = DisplayStyle.None;
    }

    private static Button QuantityButton(string text, Action clicked)
    {
        var button = new Button(clicked) { text = text };
        button.style.minWidth = 116f;
        button.style.height = 34f;
        button.style.color = Text;
        button.style.backgroundColor = Track;
        SetBorder(button, StrokeStrong, 1f);
        SetRadius(button, 7f);
        return button;
    }

    private static VisualElement BuildPersonPane(
        out Label title, out Label capacity, out ScrollView scroll)
    {
        var pane = new VisualElement();
        pane.style.width = Length.Percent(50f);
        pane.style.flexShrink = 1f;
        pane.style.backgroundColor = Track;
        pane.style.paddingLeft = 9f;
        pane.style.paddingRight = 9f;
        pane.style.paddingTop = 8f;
        pane.style.paddingBottom = 8f;
        SetBorder(pane, Stroke, 1f);
        SetRadius(pane, 11f);

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 7f;
        title = new Label();
        title.style.color = Text;
        title.style.fontSize = 14f;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.flexGrow = 1f;
        capacity = new Label();
        capacity.style.color = TextMute;
        capacity.style.fontSize = 11f;
        header.Add(title);
        header.Add(capacity);
        pane.Add(header);

        // §128.1: панель скроллится своим скроллбаром. До этого раскладка,
        // не влезшая после усадки ячеек, просто срезалась рамкой — добраться
        // до остатка было нечем.
        scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.style.flexGrow = 1f;
        scroll.style.minHeight = 0f;
        scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
        scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;

        var content = scroll.contentContainer;
        content.style.flexDirection = FlexDirection.Row;
        content.style.flexWrap = Wrap.Wrap;
        content.style.alignContent = Align.FlexStart;
        content.style.width = Length.Percent(100f);
        pane.Add(scroll);
        return pane;
    }

    private void LayoutFrame()
    {
        if (_root == null || _frame == null || _root.layout.width < 1f) return;
        var width = Mathf.Min(1080f, Mathf.Max(620f, _root.layout.width - 28f));
        var height = Mathf.Min(650f, Mathf.Max(380f, _root.layout.height - 28f));
        _frame.style.width = width;
        _frame.style.height = height;
        _frame.style.left = Mathf.Max(8f, (_root.layout.width - width) * 0.5f);
        _frame.style.top = Mathf.Max(8f, (_root.layout.height - height) * 0.5f);
    }

    private void Refresh(bool force)
    {
        if (_runner == null || !_runner.IsReady)
        {
            Hide();
            return;
        }

        if (!force && _lastTick == _runner.CurrentTick) return;
        var snapshot = _runner.CreateSnapshot();
        _lastTick = snapshot.Tick;
        var looter = FindNpc(snapshot, _looterId);

        // §128.5: справа вещь — своя, куда более короткая проверка живости:
        // у мешка нет ни сознания, ни фракции, ни чужих рук.
        if (_otherObjectId >= 0)
        {
            var container = FindContainer(snapshot, _otherObjectId);
            if (looter == null || container == null || looter.Health <= 0f ||
                !_runner.CanControlNpc(looter.Id))
            {
                Hide();
                return;
            }

            var containerSignature =
                PersonSignature(looter) + "<>" + ContainerSignature(container);
            UpdatePendingStatus(snapshot, looter, containerSignature);
            TryFinishAutoWear(looter, containerSignature);
            if (!force && containerSignature == _signature) return;
            if (_dragPrepared)
            {
                _rebuildDeferred = true;
                return;
            }

            _signature = containerSignature;
            RebuildWithContainer(looter, container);
            return;
        }

        var other = FindNpc(snapshot, _otherId);
        // §153.1: окно закрывается по СВОЕМУ условию. В подарке цель на ногах и
        // в сознании — проверять её тем же IsLootable значило бы закрывать окно
        // в тот же кадр, в который его открыли.
        if (looter == null || other == null || looter.Health <= 0f ||
            !_runner.CanControlNpc(looter.Id) ||
            !(_gift ? IsGiftable(other) : IsLootable(other)) ||
            (other.CarriedByNpcId is not null && other.CarriedByNpcId != _looterId))
        {
            Hide();
            return;
        }

        var signature = PersonSignature(looter) + "<>" + PersonSignature(other);
        UpdatePendingStatus(snapshot, looter, signature);
        TryFinishAutoWear(looter, signature);

        if (!force && signature == _signature) return;
        // §128.1a: раскладку во время жеста не трогаем — иначе нажатая ячейка
        // исчезнет из дерева прямо под пальцем.
        if (_dragPrepared)
        {
            _rebuildDeferred = true;
            return;
        }

        _signature = signature;
        Rebuild(looter, other);
    }

    // Один и тот же ответ на «дошёл ли приказ» для человека и для вещи: раскладка
    // изменилась — дошёл; не изменилась, а исполнитель уже не занят — отказ.
    private void UpdatePendingStatus(
        WorldSnapshot snapshot, NpcSnapshot looter, string signature)
    {
        if (!_pending) return;
        if (signature != _pendingSignature)
        {
            _pending = false;
            _status.text = DragHint();
        }
        else if (snapshot.Tick > _pendingTick &&
                 looter.CurrentGoal != "PlayerInventory" &&
                 looter.PlanStatus != "Active")
        {
            _pending = false;
            _status.text = Loc.Get("loot.transfer_failed");
        }
    }

    private static string ContainerSignature(ObjectSnapshot container)
    {
        var result = new System.Text.StringBuilder();
        result.Append(container.Id.Value).Append(':').Append(container.DefinitionId);
        foreach (var slot in container.Contents)
        {
            result.Append('|').Append(slot.Index).Append(':')
                .Append(slot.ItemDefinitionId).Append('×').Append(slot.StackCount);
        }

        return result.ToString();
    }

    private void RebuildWithContainer(NpcSnapshot looter, ObjectSnapshot container)
    {
        _leftContent.Clear();
        _rightContent.Clear();
        _slotCells.Clear();
        _slotIcons.Clear();
        _slotBadges.Clear();
        _slotGlyphs.Clear();
        _containerCards.Clear();
        _leftTitle.text = Loc.NpcName(looter.DisplayName);
        _rightTitle.text = ItemName(container.DefinitionId);
        _leftCapacity.text = $"{looter.InventoryUsedSlots}/{looter.InventoryCapacity}";
        var stored = container.Contents.Count;
        _rightCapacity.text = container.ContainerCapacity > 0
            ? $"{stored}/{container.ContainerCapacity}"
            : stored.ToString();
        BuildInventoryProjection(_leftContent, looter);
        BuildContainerProjection(_rightContent, container);
        _densityTier = 0;
        ApplyDensity();
        _frame.schedule.Execute(FitDensity);
    }

    /// <summary>
    /// §128.5: содержимое вещи — ОДНА плоская карточка. У мешка нет ни рук, ни
    /// слоёв одежды, ни кобуры, и рисовать ему человеческую анатомию значило бы
    /// показать игроку то, чего в модели нет.
    /// </summary>
    private void BuildContainerProjection(VisualElement parent, ObjectSnapshot container)
    {
        if (container.Contents.Count == 0 && container.ContainerCapacity <= 0)
        {
            var empty = new Label(Loc.Get("loot.empty"));
            empty.style.color = TextMute;
            empty.style.fontSize = 12f;
            empty.style.marginTop = 16f;
            empty.style.width = Length.Percent(100f);
            empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            parent.Add(empty);
            return;
        }

        var card = new VisualElement();
        card.name = "loot-container-card";
        card.style.width = Length.Percent(100f);
        card.style.flexShrink = 0f;
        card.style.backgroundColor = Raised;
        card.style.paddingLeft = 7f;
        card.style.paddingRight = 7f;
        card.style.paddingTop = 6f;
        card.style.paddingBottom = 7f;
        card.style.marginBottom = 6f;
        SetBorder(card, Stroke, 1f);
        SetRadius(card, 8f);
        _containerCards.Add(card);

        var slots = new VisualElement();
        slots.style.flexDirection = FlexDirection.Row;
        slots.style.flexWrap = Wrap.Wrap;
        foreach (var slot in container.Contents)
        {
            // Ячейка называется своим ИНДЕКСОМ В РАСКЛАДКЕ, а не физическим
            // местом в Contents: симуляция разрешает её тем же перечислением,
            // что построило эту сетку, и сверяет ожидаемый id.
            slots.Add(BuildSlot(-container.Id.Value, slot, null, useCellIndex: true));
        }
        for (var index = container.Contents.Count;
             index < container.ContainerCapacity; index++)
        {
            slots.Add(BuildSlot(-container.Id.Value, new InventorySlotSnapshot
            {
                Index = index,
                SourceIndex = -1
            }, null, useCellIndex: true));
        }

        card.Add(slots);
        parent.Add(card);
    }

    private void Rebuild(NpcSnapshot looter, NpcSnapshot other)
    {
        _leftContent.Clear();
        _rightContent.Clear();
        _slotCells.Clear();
        _slotIcons.Clear();
        _slotBadges.Clear();
        _slotGlyphs.Clear();
        _containerCards.Clear();
        _leftTitle.text = Loc.NpcName(looter.DisplayName);
        _rightTitle.text = Loc.NpcName(other.DisplayName);
        _leftCapacity.text = $"{looter.InventoryUsedSlots}/{looter.InventoryCapacity}";
        _rightCapacity.text = $"{other.InventoryUsedSlots}/{other.InventoryCapacity}";
        BuildInventoryProjection(_leftContent, looter);
        BuildInventoryProjection(_rightContent, other);
        _densityTier = 0;
        ApplyDensity();
        _frame.schedule.Execute(FitDensity);
    }

    private void BuildInventoryProjection(VisualElement parent, NpcSnapshot npc)
    {
        var hands = new List<InventoryContainerSnapshot>();
        InventoryContainerSnapshot? carry = null;
        var regular = new List<InventoryContainerSnapshot>();
        foreach (var container in npc.InventoryContainers)
        {
            if (container.Kind is InventoryContainerKind.HandLeft or
                InventoryContainerKind.HandRight)
            {
                hands.Add(container);
            }
            else if (container.Kind == InventoryContainerKind.Carry)
            {
                carry = container;
            }
            else
            {
                regular.Add(container);
            }
        }

        var representedWorn = new HashSet<int>();
        if (carry != null)
        {
            parent.Add(BuildContainer(npc, carry, hands, representedWorn));
        }
        else if (hands.Count > 0)
        {
            parent.Add(BuildContainer(npc, new InventoryContainerSnapshot
            {
                Id = "carry:loot-ui",
                Kind = InventoryContainerKind.Carry,
                BodyAnchor = InventoryBodyAnchor.Pelvis
            }, hands, representedWorn));
        }
        foreach (var container in regular)
        {
            parent.Add(BuildContainer(npc, container, null, representedWorn));
        }

        for (var wornIndex = 0; wornIndex < npc.WornItems.Count; wornIndex++)
        {
            if (representedWorn.Contains(wornIndex)) continue;
            parent.Add(BuildWornChip(npc.Id.Value, wornIndex, npc.WornItems[wornIndex]));
        }

        if (parent.childCount == 0)
        {
            var empty = new Label(Loc.Get("loot.empty"));
            empty.style.color = TextMute;
            empty.style.fontSize = 12f;
            empty.style.marginTop = 16f;
            empty.style.width = Length.Percent(100f);
            empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            parent.Add(empty);
        }
    }

    private VisualElement BuildContainer(
        NpcSnapshot npc,
        InventoryContainerSnapshot container,
        IReadOnlyList<InventoryContainerSnapshot>? hands,
        HashSet<int> representedWorn)
    {
        var card = new VisualElement();
        card.name = "loot-container-card";
        card.style.width = Length.Percent(100f);
        card.style.flexShrink = 0f;
        card.style.backgroundColor = Raised;
        card.style.paddingLeft = 7f;
        card.style.paddingRight = 7f;
        card.style.paddingTop = 6f;
        card.style.paddingBottom = 7f;
        card.style.marginBottom = 6f;
        SetBorder(card, container.Kind == InventoryContainerKind.Overflow ? GoldDim : Stroke, 1f);
        SetRadius(card, 8f);
        _containerCards.Add(card);

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 5f;
        var title = new Label(ContainerTitle(container));
        title.style.color = Text;
        title.style.fontSize = 11.5f;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.flexGrow = 1f;
        title.style.whiteSpace = WhiteSpace.NoWrap;
        title.style.overflow = Overflow.Hidden;
        header.Add(title);

        var filled = 0;
        foreach (var slot in container.Slots)
            if (!string.IsNullOrEmpty(slot.ItemDefinitionId)) filled++;
        var handCapacity = 0;
        if (hands != null)
        {
            foreach (var hand in hands)
            {
                handCapacity += hand.Capacity;
                foreach (var slot in hand.Slots)
                    if (!string.IsNullOrEmpty(slot.ItemDefinitionId)) filled++;
            }
        }
        var count = new Label($"{filled}/{container.Capacity + handCapacity}");
        count.style.color = TextMute;
        count.style.fontSize = 9.5f;
        header.Add(count);
        card.Add(header);

        if (!string.IsNullOrEmpty(container.OwnerItemDefinitionId))
        {
            representedWorn.Add(container.OwnerSourceIndex);
            RegisterWornDrag(header, npc.Id.Value, container.OwnerSourceIndex,
                container.OwnerItemDefinitionId);
            header.RegisterCallback<PointerEnterEvent>(_ => SetBorder(card, GoldDim, 1f));
            header.RegisterCallback<PointerLeaveEvent>(_ => SetBorder(card, Stroke, 1f));
        }

        var slots = new VisualElement();
        slots.style.flexDirection = FlexDirection.Row;
        slots.style.flexWrap = Wrap.Wrap;
        foreach (var slot in container.Slots)
        {
            slots.Add(BuildSlot(npc.Id.Value, slot, null));
        }
        if (hands != null)
        {
            foreach (var hand in hands)
            {
                var badge = hand.Kind == InventoryContainerKind.HandLeft
                    ? Loc.Get("inv.hand_left")
                    : Loc.Get("inv.hand_right");
                foreach (var slot in hand.Slots)
                {
                    slots.Add(BuildSlot(npc.Id.Value, slot, badge));
                }
            }
        }
        card.Add(slots);
        return card;
    }

    private VisualElement BuildWornChip(int ownerId, int wornIndex, string itemId)
    {
        var chip = new VisualElement();
        chip.name = "loot-worn-chip";
        chip.style.width = Length.Percent(49f);
        chip.style.minHeight = 34f;
        chip.style.flexDirection = FlexDirection.Row;
        chip.style.alignItems = Align.Center;
        chip.style.backgroundColor = Raised;
        chip.style.paddingLeft = 5f;
        chip.style.paddingRight = 5f;
        chip.style.marginRight = Length.Percent(1f);
        chip.style.marginBottom = 5f;
        SetBorder(chip, Stroke, 1f);
        SetRadius(chip, 7f);
        AddItemVisual(chip, itemId, 26f, trackDensity: false);
        var label = new Label(ItemName(itemId));
        label.style.color = TextDim;
        label.style.fontSize = 10f;
        label.style.whiteSpace = WhiteSpace.NoWrap;
        label.style.overflow = Overflow.Hidden;
        label.style.flexGrow = 1f;
        label.pickingMode = PickingMode.Ignore;
        chip.Add(label);
        RegisterWornDrag(chip, ownerId, wornIndex, itemId);
        return chip;
    }

    // §128.5: useCellIndex — ячейка ВЕЩИ. У человека жест несёт физический
    // индекс экземпляра (SourceIndex), у мешка — номер ячейки в его раскладке:
    // именно им симуляция разрешает содержимое обратно.
    private VisualElement BuildSlot(
        int ownerId, InventorySlotSnapshot slot, string? badge, bool useCellIndex = false)
    {
        var cell = new VisualElement();
        cell.name = "loot-item-slot";
        cell.style.width = CellSizes[0];
        cell.style.height = CellSizes[0];
        cell.style.marginRight = Gaps[0];
        cell.style.marginBottom = Gaps[0];
        cell.style.position = Position.Relative;
        cell.style.alignItems = Align.Center;
        cell.style.justifyContent = Justify.Center;
        cell.style.backgroundColor = Track;
        SetBorder(cell, StrokeStrong, 1f);
        SetRadius(cell, 7f);
        _slotCells.Add(cell);

        if (!string.IsNullOrEmpty(badge))
        {
            var hand = new Label(badge);
            hand.style.position = Position.Absolute;
            hand.style.left = 2f;
            hand.style.top = 1f;
            hand.style.maxWidth = 34f;
            hand.style.color = TextMute;
            hand.style.fontSize = 6f;
            hand.style.whiteSpace = WhiteSpace.NoWrap;
            hand.style.overflow = Overflow.Hidden;
            hand.pickingMode = PickingMode.Ignore;
            cell.Add(hand);
            _slotBadges.Add(hand);
        }

        if (string.IsNullOrEmpty(slot.ItemDefinitionId))
        {
            if (!string.IsNullOrEmpty(slot.AcceptedItemDefinitionId))
            {
                cell.tooltip = Loc.Get("inv.typed_slot") + ": " +
                               ItemName(slot.AcceptedItemDefinitionId);
            }
            return cell;
        }

        AddItemVisual(cell, slot.ItemDefinitionId, 34f, trackDensity: true);
        if (slot.StackCount > 1)
        {
            var count = new Label("×" + slot.StackCount);
            count.style.position = Position.Absolute;
            count.style.right = 2f;
            count.style.bottom = 1f;
            count.style.color = Text;
            count.style.fontSize = 8.5f;
            count.style.unityFontStyleAndWeight = FontStyle.Bold;
            count.pickingMode = PickingMode.Ignore;
            cell.Add(count);
            _slotBadges.Add(count);
        }
        cell.tooltip = ItemName(slot.ItemDefinitionId);
        RegisterDrag(cell, new DragItem(
            ownerId, InventoryItemSource.Carried,
            useCellIndex ? slot.Index : slot.SourceIndex,
            Mathf.Max(1, slot.StackCount), slot.ItemDefinitionId));
        return cell;
    }

    private void AddItemVisual(
        VisualElement parent, string itemId, float size, bool trackDensity)
    {
        var sprite = ItemIcons.Load(itemId);
        if (sprite != null)
        {
            var image = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit };
            image.style.width = size;
            image.style.height = size;
            image.pickingMode = PickingMode.Ignore;
            parent.Add(image);
            if (trackDensity) _slotIcons.Add(image);
            return;
        }

        var glyph = new Label(ItemIcons.FallbackGlyph(itemId));
        glyph.style.color = Gold;
        glyph.style.fontSize = size * 0.55f;
        glyph.style.unityTextAlign = TextAnchor.MiddleCenter;
        glyph.pickingMode = PickingMode.Ignore;
        parent.Add(glyph);
        if (trackDensity) _slotGlyphs.Add(glyph);
    }

    private void RegisterWornDrag(
        VisualElement element, int ownerId, int wornIndex, string itemId)
    {
        if (wornIndex < 0) return;
        RegisterDrag(element, new DragItem(
            ownerId, InventoryItemSource.Worn, wornIndex, 1, itemId));
        element.tooltip = ItemName(itemId);
    }

    private void RegisterDrag(VisualElement element, DragItem item)
    {
        if (item.Index < 0) return;
        element.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (_pending || _quantityOpen || evt.button != 0) return;
            // Новый жест снимает удержанный отказ: игрок уже прочитал его.
            _statusHeld = false;
            ClearDrag();
            _drag = item;
            _dragPrepared = true;
            _dragPointerId = evt.pointerId;
            _dragStart = new Vector2(evt.position.x, evt.position.y);
            _dragCell = element;
            // §128.4: пару засекаем на нажатии, исполняем на отпускании — иначе
            // второй клик успел бы стать началом перетаскивания.
            _dragDoubleClick = _doubleClick.Accept(
                InventoryQuickActions.CellKey(
                    item.OwnerId, item.Source, item.Index, item.DefinitionId),
                evt.clickCount);
            // §128.1a: указатель забирает РАМКА. Ячейка под курсором может
            // исчезнуть на ближайшей пересборке, рамка — нет.
            _frame.CapturePointer(evt.pointerId);
        });
        element.RegisterCallback<PointerEnterEvent>(_ => SetBorder(element, GoldDim, 1f));
        element.RegisterCallback<PointerLeaveEvent>(_ =>
        {
            if (!ReferenceEquals(_dragCell, element) || !_dragMoved)
                SetBorder(element, StrokeStrong, 1f);
        });
    }

    private void UpdateDrag(PointerMoveEvent evt)
    {
        if (!_dragPrepared || evt.pointerId != _dragPointerId) return;
        var position = new Vector2(evt.position.x, evt.position.y);
        if (!_dragMoved)
        {
            if ((position - _dragStart).sqrMagnitude < DragThreshold * DragThreshold) return;
            _dragMoved = true;
            if (_dragCell != null)
            {
                _dragCell.style.opacity = 0.55f;
                SetBorder(_dragCell, Gold, 2f);
            }
            ShowGhost(_drag.DefinitionId);
            _status.text = Loc.Get("loot.drop_hint");
        }

        MoveGhost(position);
    }

    /// <summary>§128.1a: приёмник — тот, ЧЬЯ ПАНЕЛЬ под точкой отпускания, а не
    /// то, что оказалось под курсором.</summary>
    private void OnFramePointerUp(PointerUpEvent evt)
    {
        if (!_dragPrepared || evt.pointerId != _dragPointerId)
        {
            ClearDrag();
            return;
        }

        var position = new Vector2(evt.position.x, evt.position.y);
        var item = _drag;
        var doubleClick = _dragDoubleClick;
        var moved = _dragMoved;
        if (_frame.HasPointerCapture(evt.pointerId)) _frame.ReleasePointer(evt.pointerId);

        if (moved)
        {
            if (_leftPane.worldBound.Contains(position)) DropOn(_looterId, item);
            else if (_rightPane.worldBound.Contains(position)) DropOn(_otherId, item);
        }
        else if (doubleClick)
        {
            QuickAction(item);
        }

        // Двойной клик сам оставляет сообщение (в том числе «так нельзя»), а
        // сорвавшееся перетаскивание обязано вернуть подсказку. Успешный приказ
        // защищён внутри ClearDrag проверкой _pending.
        ClearDrag(resetStatus: !doubleClick);
        evt.StopPropagation();
    }

    private void DropOn(int destinationId, DragItem drag)
    {
        _drag = drag;
        if (destinationId == _drag.OwnerId || _runner == null) return;

        var direction = _drag.OwnerId == _otherId && destinationId == _looterId
            ? InventoryTransferDirection.Take
            : InventoryTransferDirection.Give;

        // §153.1: в режиме подарка обратный жест не отправляется вовсе. Слать
        // приказ, который симуляция отклонит по PersonNotAvailable, и было бы
        // «маркер есть, никто не идёт» из §121.1 — игрок читает это как поломку.
        if (_gift && direction == InventoryTransferDirection.Take)
        {
            _statusHeld = true;
            _status.text = Loc.Get("gift.take_forbidden");
            return;
        }

        if (InventoryState.IsStackable(_drag.DefinitionId) && _drag.Count > 1)
        {
            ShowQuantityPicker(destinationId, _drag);
            return;
        }

        ExecuteTransfer(destinationId, _drag, _drag.Count);
    }

    private void ShowQuantityPicker(int destinationId, DragItem item)
    {
        _quantityOpen = true;
        _quantityDestinationId = destinationId;
        _quantityItem = item;
        _quantityTitle.text = string.Format(
            Loc.Get("loot.quantity_title"), ItemName(item.DefinitionId));
        _quantitySlider.lowValue = 1;
        _quantitySlider.highValue = item.Count;
        _quantitySlider.SetValueWithoutNotify(item.Count);
        _quantityInput.SetValueWithoutNotify(item.Count);
        _quantityConfirm.text = Loc.Get("loot.quantity_confirm");
        _quantityCancel.text = Loc.Get("loot.quantity_cancel");
        _quantityOverlay.style.display = DisplayStyle.Flex;
        _quantityOverlay.BringToFront();
        _quantityOverlay.Focus();
        _quantityInput.Focus();
    }

    private void ConfirmQuantity()
    {
        if (!_quantityOpen) return;
        var destinationId = _quantityDestinationId;
        var item = _quantityItem;
        var count = Mathf.Clamp(_quantityInput.value, 1, item.Count);
        HideQuantityPicker();
        ExecuteTransfer(destinationId, item, count);
    }

    private void CancelQuantity()
    {
        if (!_quantityOpen) return;
        HideQuantityPicker();
        _status.text = DragHint();
    }

    private void HideQuantityPicker()
    {
        _quantityOpen = false;
        _quantityDestinationId = 0;
        _quantityItem = default;
        if (_quantityOverlay != null)
            _quantityOverlay.style.display = DisplayStyle.None;
    }

    private void ExecuteTransfer(int destinationId, DragItem item, int count)
    {
        _drag = item;
        if (destinationId == item.OwnerId || _runner == null) return;

        var direction = item.OwnerId == _otherId && destinationId == _looterId
            ? InventoryTransferDirection.Take
            : InventoryTransferDirection.Give;

        // §128.5: справа вещь — другой приказ. Сторону жест уже определил выше:
        // из мешка к себе — Take, из своих карманов в мешок — Give.
        if (_otherObjectId >= 0)
        {
            _runner.EnqueueCommand(new TransferContainerCommand(
                new HexLive.Simulation.Common.EntityId(_looterId),
                new HexLive.Simulation.Common.ObjectId(_otherObjectId),
                item.Index,
                item.DefinitionId,
                count,
                direction));
            MarkPending();
            return;
        }

        _runner.EnqueueCommand(new TransferInventoryCommand(
            new HexLive.Simulation.Common.EntityId(_looterId),
            new HexLive.Simulation.Common.EntityId(_otherId),
            new InventoryItemRef(
                item.Source, item.Index, item.DefinitionId),
            count,
            direction));
        MarkPending();
    }

    /// <summary>§128.4 быстрое действие двойным кликом. Сторону выбирает
    /// <see cref="InventoryQuickActions"/>; приказ по-прежнему шлётся отсюда и
    /// проверяется симуляцией заново.</summary>
    private void QuickAction(DragItem item)
    {
        if (_runner == null || _pending) return;
        var quick = InventoryQuickActions.Resolve(
            ownSide: item.OwnerId == _looterId,
            worn: item.Source == InventoryItemSource.Worn,
            wearable: InventoryQuickActions.IsWearable(_runner, item.DefinitionId));
        switch (quick)
        {
            case InventoryQuickAction.TakeFromOther:
                // §153.1: в подарке двойной клик по чужой панели ничего не
                // забирает — и не заводит отложенное «надеть» на вещь, которая
                // никуда не поедет.
                if (_gift)
                {
                    _statusHeld = true;
                    _status.text = Loc.Get("gift.take_forbidden");
                    break;
                }

                DropOn(_looterId, item);
                ArmAutoWear(item);
                break;
            case InventoryQuickAction.Wear:
                EnqueueManage(item, InventoryAction.Wear, "loot.equipping");
                break;
            case InventoryQuickAction.TakeOff:
                EnqueueManage(item, InventoryAction.Stow, "loot.removing");
                break;
            default:
                _status.text = Loc.Get("loot.no_quick_action");
                break;
        }
    }

    private void EnqueueManage(DragItem item, InventoryAction action, string statusTerm)
    {
        _runner!.EnqueueCommand(new ManageInventoryCommand(
            new HexLive.Simulation.Common.EntityId(_looterId),
            new InventoryItemRef(item.Source, item.Index, item.DefinitionId),
            action));
        ClearAutoWear();
        MarkPending();
        _status.text = Loc.Get(statusTerm);
    }

    private void MarkPending()
    {
        _pending = true;
        _pendingTick = _lastTick;
        _pendingSignature = _signature;
        _status.text = Loc.Get("loot.transferring");
    }

    /// <summary>§128.4: надетая вещь приезжает уже надетой (§128.2), доводить
    /// нечего. Носимая из чужих карманов приезжает в переноску — её и надеваем,
    /// как только она там появится.</summary>
    private void ArmAutoWear(DragItem item)
    {
        ClearAutoWear();
        if (item.Source != InventoryItemSource.Carried || _runner == null ||
            !InventoryQuickActions.IsWearable(_runner, item.DefinitionId))
        {
            return;
        }

        var snapshot = _runner.CreateSnapshot();
        var looter = snapshot == null ? null : FindNpc(snapshot, _looterId);
        if (looter == null) return;
        _autoWearDefinitionId = item.DefinitionId;
        _autoWearBaseline = CountCarried(looter, item.DefinitionId, out _);
        _autoWearDeadlineTick = _lastTick + AutoWearWindowTicks;
    }

    private void TryFinishAutoWear(NpcSnapshot looter, string signature)
    {
        if (_autoWearDefinitionId.Length == 0 || _runner == null) return;
        if (_lastTick > _autoWearDeadlineTick)
        {
            ClearAutoWear();
            return;
        }

        var count = CountCarried(looter, _autoWearDefinitionId, out var index);
        if (count <= _autoWearBaseline || index < 0) return;

        _runner.EnqueueCommand(new ManageInventoryCommand(
            new HexLive.Simulation.Common.EntityId(_looterId),
            new InventoryItemRef(InventoryItemSource.Carried, index, _autoWearDefinitionId),
            InventoryAction.Wear));
        ClearAutoWear();
        _pending = true;
        _pendingTick = _lastTick;
        _pendingSignature = signature;
        _status.text = Loc.Get("loot.equipping");
    }

    private static int CountCarried(
        NpcSnapshot npc, string definitionId, out int firstSourceIndex)
    {
        firstSourceIndex = -1;
        var count = 0;
        foreach (var container in npc.InventoryContainers)
        {
            foreach (var slot in container.Slots)
            {
                if (slot.ItemDefinitionId != definitionId || slot.SourceIndex < 0) continue;
                count++;
                if (firstSourceIndex < 0) firstSourceIndex = slot.SourceIndex;
            }
        }

        return count;
    }

    private void ClearAutoWear()
    {
        _autoWearDefinitionId = string.Empty;
        _autoWearDeadlineTick = -1;
        _autoWearBaseline = 0;
    }

    private void ShowGhost(string definitionId)
    {
        if (_dragGhost == null)
        {
            _dragGhost = new VisualElement { name = "loot-drag-ghost" };
            _dragGhost.style.position = Position.Absolute;
            _dragGhost.style.width = GhostSize;
            _dragGhost.style.height = GhostSize;
            _dragGhost.style.alignItems = Align.Center;
            _dragGhost.style.justifyContent = Justify.Center;
            _dragGhost.style.backgroundColor = new Color(Panel.r, Panel.g, Panel.b, 0.85f);
            _dragGhost.pickingMode = PickingMode.Ignore;
            SetBorder(_dragGhost, Gold, 2f);
            SetRadius(_dragGhost, 7f);
            _root.Add(_dragGhost);
        }

        _dragGhost.Clear();
        AddItemVisual(_dragGhost, definitionId, GhostSize - 10f, trackDensity: false);
        _dragGhost.style.display = DisplayStyle.Flex;
        _dragGhost.BringToFront();
    }

    private void MoveGhost(Vector2 position)
    {
        if (_dragGhost == null) return;
        _dragGhost.style.left = position.x - GhostSize * 0.5f;
        _dragGhost.style.top = position.y - GhostSize * 0.5f;
    }

    private void ClearDrag(bool resetStatus = true)
    {
        if (_dragCell != null)
        {
            _dragCell.style.opacity = 1f;
            SetBorder(_dragCell, StrokeStrong, 1f);
        }
        if (_dragGhost != null) _dragGhost.style.display = DisplayStyle.None;
        if (_dragPointerId >= 0 && _frame != null &&
            _frame.HasPointerCapture(_dragPointerId))
        {
            _frame.ReleasePointer(_dragPointerId);
        }
        _dragPrepared = false;
        _dragMoved = false;
        _dragDoubleClick = false;
        _dragPointerId = -1;
        _dragCell = null;
        if (resetStatus && !_pending && !_statusHeld && _status != null)
        {
            _status.text = DragHint();
        }

        // §128.1a: пересборку, которую жест придержал, отпускаем сразу — иначе
        // окно осталось бы показывать раскладку с прошлого тика.
        if (_rebuildDeferred && IsOpen)
        {
            _rebuildDeferred = false;
            Refresh(force: true);
        }
    }

    private void FitDensity()
    {
        if (_densityTier >= MaxDensityTier) return;
        var leftHeight = ViewportHeight(_leftScroll);
        var rightHeight = ViewportHeight(_rightScroll);
        if (leftHeight < 1f && rightHeight < 1f) return;
        var leftOverflow = RequiredHeight(_leftContent) > leftHeight + 0.5f;
        var rightOverflow = RequiredHeight(_rightContent) > rightHeight + 0.5f;
        if (!leftOverflow && !rightOverflow) return;
        _densityTier++;
        ApplyDensity();
        _frame.schedule.Execute(FitDensity);
    }

    private static float ViewportHeight(ScrollView scroll) =>
        scroll.contentViewport.contentRect.height;

    private void ApplyDensity()
    {
        var tier = Mathf.Clamp(_densityTier, 0, MaxDensityTier);
        var size = CellSizes[tier];
        var gap = Gaps[tier];
        foreach (var cell in _slotCells)
        {
            cell.style.width = size;
            cell.style.height = size;
            cell.style.marginRight = gap;
            cell.style.marginBottom = gap;
        }
        foreach (var icon in _slotIcons)
        {
            var iconSize = Mathf.Max(20f, size - 7f);
            icon.style.width = iconSize;
            icon.style.height = iconSize;
        }
        foreach (var label in _slotBadges)
        {
            label.style.fontSize = Mathf.Max(5.5f, 8.5f - tier * 0.7f);
        }
        foreach (var glyph in _slotGlyphs)
        {
            glyph.style.fontSize = Mathf.Max(12f, size * 0.52f);
        }
        foreach (var card in _containerCards)
        {
            card.style.paddingLeft = Mathf.Max(4f, 7f - tier);
            card.style.paddingRight = Mathf.Max(4f, 7f - tier);
            card.style.paddingTop = Mathf.Max(3f, 6f - tier);
            card.style.paddingBottom = Mathf.Max(4f, 7f - tier);
            card.style.marginBottom = gap;
        }
    }

    private static float RequiredHeight(VisualElement content)
    {
        var bottom = 0f;
        foreach (var child in content.Children())
        {
            bottom = Mathf.Max(bottom, child.layout.yMax);
        }
        return bottom;
    }

    private string ContainerTitle(InventoryContainerSnapshot container)
    {
        if (!string.IsNullOrEmpty(container.OwnerItemDefinitionId))
            return ItemName(container.OwnerItemDefinitionId);
        return Loc.Get(container.Kind switch
        {
            InventoryContainerKind.Carry => "inv.carry",
            InventoryContainerKind.Holster => "inv.holster",
            InventoryContainerKind.Overflow => "inv.overflow",
            _ => "inv.carried"
        });
    }

    private string ItemName(string definitionId)
    {
        var key = $"item.{ItemInfo.Slug(definitionId)}.name";
        if (Loc.Has(key)) return Loc.Get(key);
        return _runner != null &&
               _runner.TryGetObjectDefinition(definitionId, out var definition) &&
               definition != null
            ? definition.DisplayName
            : definitionId;
    }

    private static string PersonSignature(NpcSnapshot npc)
    {
        var result = new System.Text.StringBuilder();
        result.Append(npc.Id.Value).Append(':').Append(npc.InventoryUsedSlots)
            .Append('/').Append(npc.InventoryCapacity).Append('|');
        foreach (var worn in npc.WornItems) result.Append('w').Append(worn).Append('|');
        foreach (var container in npc.InventoryContainers)
        {
            result.Append(container.Id).Append(':').Append(container.OwnerSourceIndex)
                .Append(':').Append(container.OwnerItemDefinitionId);
            foreach (var slot in container.Slots)
            {
                result.Append('[').Append(slot.SourceIndex).Append(':')
                    .Append(slot.ItemDefinitionId).Append(':').Append(slot.StackCount).Append(']');
            }
        }
        return result.ToString();
    }

    // §128 r2 (#164): тело лежит в ОТДЕЛЬНОМ списке снапшота, и без этой ветки
    // панель молча не открывалась над мёртвой — «обыскать» в меню было, а окна
    // не появлялось.
    private static NpcSnapshot? FindNpc(WorldSnapshot snapshot, int id)
    {
        foreach (var npc in snapshot.Npcs)
            if (npc.Id.Value == id) return npc;
        foreach (var corpse in snapshot.Corpses)
            if (corpse.Id.Value == id) return corpse;
        return null;
    }

    /// <summary>§128 r2 (#164): лежит и не ответит — мёртвая, спящая, без
    /// сознания. Зеркало сим-предиката PlayerLootTargets.IsLyingHelpless.</summary>
    private static bool IsLootable(NpcSnapshot person) =>
        person.Health <= 0f || person.IsUnconscious || person.IsDying ||
        person.IsFainted || person.CurrentInteraction == "Sleep";

    /// <summary>§153.1: кому можно подарить — живому, не при смерти и не в
    /// отключке. Зеркало сим-предиката PlayerLootTargets.TryResolveGiftRecipient;
    /// правду на месте всё равно проверяет симуляция.</summary>
    private static bool IsGiftable(NpcSnapshot person) =>
        person.Health > 0f && !person.IsUnconscious && !person.IsDying &&
        !person.IsFainted;

    /// <summary>Подсказка внизу окна зависит от РЕЖИМА: в подарке вещи ходят
    /// только в одну сторону, и обещать обмен было бы враньём.</summary>
    private string DragHint() =>
        Loc.Get(_gift ? "gift.drag_hint" : "loot.drag_hint");

    private void Hide()
    {
        if (_root != null) _root.style.display = DisplayStyle.None;
        _looterId = -1;
        _otherId = -1;
        _otherObjectId = -1;
        _pending = false;
        _statusHeld = false;
        _gift = false;
        IsOpen = false;
        _rebuildDeferred = false;
        HideQuantityPicker();
        ClearAutoWear();
        _doubleClick.Reset();
        ClearDrag();
        PointerOverPanel = false;
    }

    private static void SetRadius(VisualElement element, float radius)
    {
        element.style.borderTopLeftRadius = radius;
        element.style.borderTopRightRadius = radius;
        element.style.borderBottomLeftRadius = radius;
        element.style.borderBottomRightRadius = radius;
    }

    private static void SetBorder(VisualElement element, Color color, float width)
    {
        element.style.borderLeftColor = color;
        element.style.borderRightColor = color;
        element.style.borderTopColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftWidth = width;
        element.style.borderRightWidth = width;
        element.style.borderTopWidth = width;
        element.style.borderBottomWidth = width;
    }
}

}
