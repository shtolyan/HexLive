#nullable enable
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class LootTransferPanel
    {
        private RuntimeWindow? _informationModal;
        private VisualElement? _informationCard;

        private void HideItemInformation()
        {
            _informationModal?.Show(false);
            _informationModal?.Backdrop.RemoveFromHierarchy();
            _informationModal = null;
            _informationCard?.RemoveFromHierarchy();
            _informationCard = null;
        }

        private void ShowItemInformation(DragItem item)
        {
            if (_runner == null || !_runner.IsReady || _quantityOpen || _pending) return;
            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null) return;
            var builder = CharacterPanel.ItemInformationBuilderFor(_runner);
            if (builder == null) return;
            NpcSnapshot? owner = null; InventorySlotSnapshot? slot = null;
            var worn = item.Source == InventoryItemSource.Worn;
            if (item.OwnerId >= 0)
            {
                owner = FindNpc(snapshot, item.OwnerId);
                if (owner == null) return;
                if (worn)
                {
                    if (item.Index < 0 || item.Index >= owner.WornItems.Count || owner.WornItems[item.Index] != item.DefinitionId) return;
                }
                else
                {
                    foreach (var container in owner.InventoryContainers)
                        foreach (var candidate in container.Slots)
                            if (candidate.SourceIndex == item.Index && candidate.ItemDefinitionId == item.DefinitionId) slot = candidate;
                    if (slot == null) return;
                }
            }
            else
            {
                ObjectSnapshot? container = null;
                foreach (var candidate in snapshot.Objects)
                    if (candidate.Id.Value == -item.OwnerId) { container = candidate; break; }
                if (container == null) return;
                foreach (var candidate in container.Contents)
                    if (candidate.Index == item.Index && candidate.ItemDefinitionId == item.DefinitionId) slot = candidate;
                if (slot == null) return;
            }
            HideItemInformation();
            _informationCard = new VisualElement { name = "loot-item-information-card" };
            _informationCard.AddToClassList("item-info-card");
            _informationCard.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape) return;
                HideItemInformation(); evt.StopPropagation();
            });
            var close = RuntimeWindow.CloseButton(HideItemInformation, Text, "loot-item-information-close");
            close.AddToClassList("item-info-close"); _informationCard.Add(close);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("item-info-scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.Add(builder.CreateReadOnlyItemInformation(item.DefinitionId, owner!, worn, item.Index,
                slot?.StackCount ?? 1, item.OwnerId < 0 ? slot : null));
            _informationCard.Add(scroll);
            var overlay = new VisualElement { name = "loot-item-information-backdrop" };
            overlay.AddToClassList("item-info-overlay");
            overlay.Add(_informationCard); _root.Add(overlay);
            _informationModal = new RuntimeWindow(_root, _informationCard, HideItemInformation, "loot-item-information", overlay);
            _informationModal.Show(true);
            _informationModal.Backdrop.BringToFront(); _informationCard.BringToFront(); close.Focus();
        }
    }
}
