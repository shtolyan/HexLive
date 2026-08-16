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
    private VisualElement _leftContent = null!;
    private VisualElement _rightContent = null!;
    private Label _leftTitle = null!;
    private Label _rightTitle = null!;
    private Label _leftCapacity = null!;
    private Label _rightCapacity = null!;
    private Label _status = null!;

    private int _looterId = -1;
    private int _otherId = -1;
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
        _instance?.OpenInternal(looterId, otherId);
    }

    public static void Close() => _instance?.Hide();

    private void OpenInternal(int looterId, int otherId)
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
        if (looter == null || other == null || looter.Faction != Faction.Colony ||
            looter.Health <= 0f || !looter.IsManualControl || !IsLootable(other) ||
            (other.CarriedByNpcId is not null && other.CarriedByNpcId != looterId))
        {
            return;
        }

        _looterId = looterId;
        _otherId = otherId;
        _lastTick = -1;
        _signature = string.Empty;
        _pending = false;
        ClearDrag();
        _root.style.display = DisplayStyle.Flex;
        IsOpen = true;
        LayoutFrame();
        Refresh(force: true);
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
        _frame.RegisterCallback<PointerMoveEvent>(UpdateDrag);
        _frame.RegisterCallback<PointerUpEvent>(_ => ClearDrag());

        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 9f;
        var title = new Label(Loc.Get("loot.title"));
        title.style.color = Gold;
        title.style.fontSize = 17f;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.flexGrow = 1f;
        header.Add(title);
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
        _leftPane = BuildPersonPane(out _leftTitle, out _leftCapacity, out _leftContent);
        _rightPane = BuildPersonPane(out _rightTitle, out _rightCapacity, out _rightContent);
        _leftPane.name = "loot-own-window";
        _rightPane.name = "loot-target-window";
        _leftPane.RegisterCallback<PointerUpEvent>(evt => DropOn(_looterId, evt));
        _rightPane.RegisterCallback<PointerUpEvent>(evt => DropOn(_otherId, evt));
        panes.Add(_leftPane);
        var arrows = new Label("⇄");
        arrows.style.width = 34f;
        arrows.style.flexShrink = 0f;
        arrows.style.color = Gold;
        arrows.style.fontSize = 21f;
        arrows.style.unityTextAlign = TextAnchor.MiddleCenter;
        arrows.pickingMode = PickingMode.Ignore;
        panes.Add(arrows);
        panes.Add(_rightPane);
        _frame.Add(panes);

        _status = new Label(Loc.Get("loot.drag_hint"));
        _status.style.color = TextMute;
        _status.style.fontSize = 11f;
        _status.style.unityTextAlign = TextAnchor.MiddleCenter;
        _status.style.marginTop = 7f;
        _frame.Add(_status);
        _root.Add(_frame);
    }

    private static VisualElement BuildPersonPane(
        out Label title, out Label capacity, out VisualElement content)
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

        content = new VisualElement();
        content.style.flexDirection = FlexDirection.Row;
        content.style.flexWrap = Wrap.Wrap;
        content.style.alignContent = Align.FlexStart;
        content.style.flexGrow = 1f;
        content.style.minHeight = 0f;
        pane.Add(content);
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
        var other = FindNpc(snapshot, _otherId);
        if (looter == null || other == null || looter.Health <= 0f ||
            looter.Faction != Faction.Colony || !looter.IsManualControl ||
            !IsLootable(other) ||
            (other.CarriedByNpcId is not null && other.CarriedByNpcId != _looterId))
        {
            Hide();
            return;
        }

        var signature = PersonSignature(looter) + "<>" + PersonSignature(other);
        if (_pending)
        {
            if (signature != _pendingSignature)
            {
                _pending = false;
                _status.text = Loc.Get("loot.drag_hint");
            }
            else if (snapshot.Tick > _pendingTick &&
                     looter.CurrentGoal != "PlayerInventory" &&
                     looter.PlanStatus != "Active")
            {
                _pending = false;
                _status.text = Loc.Get("loot.transfer_failed");
            }
        }

        if (!force && signature == _signature) return;
        _signature = signature;
        Rebuild(looter, other);
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

    private VisualElement BuildSlot(int ownerId, InventorySlotSnapshot slot, string? badge)
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
            ownerId, InventoryItemSource.Carried, slot.SourceIndex,
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

        var glyph = new Label("◆");
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
            if (_pending || evt.button != 0) return;
            ClearDrag();
            _drag = item;
            _dragPrepared = true;
            _dragPointerId = evt.pointerId;
            _dragStart = new Vector2(evt.position.x, evt.position.y);
            _dragCell = element;
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
        if (!_dragPrepared || _dragMoved || evt.pointerId != _dragPointerId) return;
        var position = new Vector2(evt.position.x, evt.position.y);
        if ((position - _dragStart).sqrMagnitude < DragThreshold * DragThreshold) return;
        _dragMoved = true;
        if (_dragCell != null)
        {
            _dragCell.style.opacity = 0.55f;
            SetBorder(_dragCell, Gold, 2f);
        }
        _status.text = Loc.Get("loot.drop_hint");
    }

    private void DropOn(int destinationId, PointerUpEvent evt)
    {
        if (!_dragPrepared || !_dragMoved || evt.pointerId != _dragPointerId ||
            destinationId == _drag.OwnerId || _runner == null)
        {
            return;
        }

        var direction = _drag.OwnerId == _otherId && destinationId == _looterId
            ? InventoryTransferDirection.Take
            : InventoryTransferDirection.Give;
        _runner.EnqueueCommand(new TransferInventoryCommand(
            new HexLive.Simulation.Common.EntityId(_looterId),
            new HexLive.Simulation.Common.EntityId(_otherId),
            new InventoryItemRef(
                _drag.Source, _drag.Index, _drag.DefinitionId),
            _drag.Count,
            direction));
        _pending = true;
        _pendingTick = _lastTick;
        _pendingSignature = _signature;
        _status.text = Loc.Get("loot.transferring");
        ClearDrag(resetStatus: false);
        evt.StopPropagation();
    }

    private void ClearDrag(bool resetStatus = true)
    {
        if (_dragCell != null)
        {
            _dragCell.style.opacity = 1f;
            SetBorder(_dragCell, StrokeStrong, 1f);
        }
        _dragPrepared = false;
        _dragMoved = false;
        _dragPointerId = -1;
        _dragCell = null;
        if (resetStatus && !_pending && _status != null)
        {
            _status.text = Loc.Get("loot.drag_hint");
        }
    }

    private void FitDensity()
    {
        if (_densityTier >= CellSizes.Length - 1) return;
        var leftOverflow = RequiredHeight(_leftContent) > _leftContent.contentRect.height + 0.5f;
        var rightOverflow = RequiredHeight(_rightContent) > _rightContent.contentRect.height + 0.5f;
        if (!leftOverflow && !rightOverflow) return;
        _densityTier++;
        ApplyDensity();
        _frame.schedule.Execute(FitDensity);
    }

    private void ApplyDensity()
    {
        var tier = Mathf.Clamp(_densityTier, 0, CellSizes.Length - 1);
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

    private void Hide()
    {
        if (_root != null) _root.style.display = DisplayStyle.None;
        _looterId = -1;
        _otherId = -1;
        _pending = false;
        ClearDrag();
        IsOpen = false;
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
