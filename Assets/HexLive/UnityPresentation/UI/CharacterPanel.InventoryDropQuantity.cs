#nullable enable
using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{

/// <summary>§123.5: quantity choice for dropping one carried stack.</summary>
public sealed partial class CharacterPanel
{
    private VisualElement _inventoryDropQuantityOverlay = null!;
    private Label _inventoryDropQuantityTitle = null!;
    private SliderInt _inventoryDropQuantitySlider = null!;
    private IntegerField _inventoryDropQuantityInput = null!;
    private Button _inventoryDropQuantityConfirm = null!;
    private int _inventoryDropQuantityMax;
    private bool _inventoryDropQuantitySync;
    private int _inventoryDropQuantityActorId = -1;
    private string _inventoryDropQuantityDefinitionId = string.Empty;
    private int _inventoryDropQuantitySourceIndex = -1;
    private bool _inventoryDropQuantityWorn;

    private void BuildInventoryDropQuantityPicker()
    {
        _inventoryDropQuantityOverlay = new VisualElement
            { name = "inventory-drop-quantity-overlay", focusable = true };
        _inventoryDropQuantityOverlay.style.position = Position.Absolute;
        _inventoryDropQuantityOverlay.style.left = 0f;
        _inventoryDropQuantityOverlay.style.right = 0f;
        _inventoryDropQuantityOverlay.style.top = 0f;
        _inventoryDropQuantityOverlay.style.bottom = 0f;
        _inventoryDropQuantityOverlay.style.alignItems = Align.Center;
        _inventoryDropQuantityOverlay.style.justifyContent = Justify.Center;
        _inventoryDropQuantityOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.68f);
        _inventoryDropQuantityOverlay.pickingMode = PickingMode.Position;

        var card = new VisualElement { name = "inventory-drop-quantity-card" };
        card.style.width = Length.Percent(90f);
        card.style.maxWidth = 430f;
        card.style.paddingLeft = 18f;
        card.style.paddingRight = 18f;
        card.style.paddingTop = 16f;
        card.style.paddingBottom = 16f;
        card.style.backgroundColor = Raised;
        SetBorder(card, GoldDim, 1f);
        SetRadius(card, 12f);

        _inventoryDropQuantityTitle = new Label();
        _inventoryDropQuantityTitle.style.color = Text;
        _inventoryDropQuantityTitle.style.fontSize = 15f;
        _inventoryDropQuantityTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        _inventoryDropQuantityTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
        _inventoryDropQuantityTitle.style.whiteSpace = WhiteSpace.Normal;
        _inventoryDropQuantityTitle.style.marginBottom = 12f;
        card.Add(_inventoryDropQuantityTitle);

        var chooser = new VisualElement();
        chooser.style.flexDirection = FlexDirection.Row;
        chooser.style.alignItems = Align.Center;
        _inventoryDropQuantitySlider = new SliderInt { name = "inventory-drop-quantity-slider" };
        _inventoryDropQuantitySlider.style.flexGrow = 1f;
        _inventoryDropQuantitySlider.style.marginRight = 12f;
        _inventoryDropQuantityInput = new IntegerField { name = "inventory-drop-quantity-input" };
        _inventoryDropQuantityInput.style.width = 76f;
        _inventoryDropQuantityInput.isDelayed = false;
        chooser.Add(_inventoryDropQuantitySlider);
        chooser.Add(_inventoryDropQuantityInput);
        card.Add(chooser);

        var buttons = new VisualElement();
        buttons.style.flexDirection = FlexDirection.Row;
        buttons.style.justifyContent = Justify.FlexEnd;
        buttons.style.marginTop = 14f;
        var cancel = InventoryDropQuantityButton(
            Loc.Get("loot.quantity_cancel"), HideInventoryDropQuantityPicker);
        cancel.style.marginRight = 8f;
        _inventoryDropQuantityConfirm = InventoryDropQuantityButton(
            Loc.Get("inv.drop_quantity_confirm"), ConfirmInventoryDropQuantity);
        buttons.Add(cancel);
        buttons.Add(_inventoryDropQuantityConfirm);
        card.Add(buttons);

        _inventoryDropQuantitySlider.RegisterValueChangedCallback(evt =>
        {
            if (_inventoryDropQuantitySync) return;
            _inventoryDropQuantitySync = true;
            _inventoryDropQuantityInput.SetValueWithoutNotify(evt.newValue);
            _inventoryDropQuantitySync = false;
        });
        _inventoryDropQuantityInput.RegisterValueChangedCallback(evt =>
        {
            if (_inventoryDropQuantitySync) return;
            _inventoryDropQuantitySync = true;
            var value = Mathf.Clamp(
                evt.newValue, _inventoryDropQuantitySlider.lowValue,
                _inventoryDropQuantitySlider.highValue);
            _inventoryDropQuantitySlider.SetValueWithoutNotify(value);
            _inventoryDropQuantityInput.SetValueWithoutNotify(value);
            _inventoryDropQuantitySync = false;
        });
        _inventoryDropQuantityOverlay.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                ConfirmInventoryDropQuantity();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                HideInventoryDropQuantityPicker();
                evt.StopPropagation();
            }
        });
        _inventoryDropQuantityOverlay.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (ReferenceEquals(evt.target, _inventoryDropQuantityOverlay))
            {
                HideInventoryDropQuantityPicker();
                evt.StopPropagation();
            }
        });

        _inventoryDropQuantityOverlay.Add(card);
        _root.Add(_inventoryDropQuantityOverlay);
        _inventoryDropQuantityOverlay.style.display = DisplayStyle.None;
    }

    private static Button InventoryDropQuantityButton(string text, Action clicked)
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

    private void RequestInventoryDrop()
    {
        if (!_inventoryMutable || _runner == null || _invSelectedId == null ||
            _inventoryActorId < 0) return;
        var snapshot = _runner.IsReady ? _runner.CreateSnapshot() : null;
        var npc = snapshot != null ? FindNpc(snapshot, _inventoryActorId) : null;
        if (npc == null) return;
        var count = SelectedInventoryStackCount(npc, _invSelectedId, _invSelectedSourceIndex);
        if (_invSelectedWorn || !InventoryState.IsStackable(_invSelectedId) || count <= 1)
        {
            EnqueueInventoryAction(InventoryAction.Drop, 1);
            return;
        }

        _inventoryDropQuantityMax = count;
        _inventoryDropQuantityActorId = _inventoryActorId;
        _inventoryDropQuantityDefinitionId = _invSelectedId;
        _inventoryDropQuantitySourceIndex = _invSelectedSourceIndex;
        _inventoryDropQuantityWorn = _invSelectedWorn;
        _inventoryDropQuantityTitle.text = string.Format(
            Loc.Get("inv.drop_quantity_title"), _invDetailName.text);
        _inventoryDropQuantitySlider.lowValue = 1;
        _inventoryDropQuantitySlider.highValue = count;
        _inventoryDropQuantitySlider.SetValueWithoutNotify(count);
        _inventoryDropQuantityInput.SetValueWithoutNotify(count);
        _inventoryDropQuantityConfirm.text = Loc.Get("inv.drop_quantity_confirm");
        _inventoryDropQuantityOverlay.style.display = DisplayStyle.Flex;
        _inventoryDropQuantityOverlay.BringToFront();
        _inventoryDropQuantityInput.Focus();
    }

    private static int SelectedInventoryStackCount(
        NpcSnapshot npc, string definitionId, int sourceIndex)
    {
        foreach (var container in npc.InventoryContainers)
        foreach (var slot in container.Slots)
        {
            if (slot.SourceIndex == sourceIndex && slot.ItemDefinitionId == definitionId)
                return Mathf.Max(1, slot.StackCount);
        }

        return 1;
    }

    private void ConfirmInventoryDropQuantity()
    {
        if (_inventoryDropQuantityOverlay.style.display.value == DisplayStyle.None) return;
        if (_inventoryActorId != _inventoryDropQuantityActorId ||
            _invSelectedId != _inventoryDropQuantityDefinitionId ||
            _invSelectedSourceIndex != _inventoryDropQuantitySourceIndex ||
            _invSelectedWorn != _inventoryDropQuantityWorn)
        {
            HideInventoryDropQuantityPicker();
            return;
        }

        var count = Mathf.Clamp(_inventoryDropQuantityInput.value, 1, _inventoryDropQuantityMax);
        HideInventoryDropQuantityPicker();
        EnqueueInventoryAction(InventoryAction.Drop, count);
    }

    private void HideInventoryDropQuantityPicker()
    {
        _inventoryDropQuantityMax = 0;
        _inventoryDropQuantityActorId = -1;
        _inventoryDropQuantityDefinitionId = string.Empty;
        _inventoryDropQuantitySourceIndex = -1;
        _inventoryDropQuantityWorn = false;
        if (_inventoryDropQuantityOverlay != null)
            _inventoryDropQuantityOverlay.style.display = DisplayStyle.None;
    }
}

}
