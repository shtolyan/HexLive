using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class CharacterPanel
    {
        internal static CharacterPanel ItemInformationBuilderFor(ISimulationSource source)
        {
            foreach (var panel in FindObjectsByType<CharacterPanel>(FindObjectsSortMode.None))
                if (ReferenceEquals(panel._runner, source)) return panel;
            return null;
        }

        // §128.4: the exchange window uses the same description and stat builder
        // as the character inventory, without opening it or its action buttons.
        public VisualElement CreateReadOnlyItemInformation(string id, NpcSnapshot owner,
            bool worn, int sourceIndex, int count, InventorySlotSnapshot containerSlot = null)
        {
            var definition = ResolveDef(id);
            var info = ResolveItemInfo(id, definition);
            var content = new VisualElement { name = "item-information-content" };
            var header = new VisualElement(); header.AddToClassList("item-info-heading");
            var sprite = LoadItemIcon(id);
            VisualElement icon = sprite != null
                ? new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit }
                : new Label(info.Emoji);
            icon.AddToClassList("item-info-icon"); icon.pickingMode = PickingMode.Ignore;
            header.Add(icon);
            var titles = new VisualElement(); titles.AddToClassList("item-info-titles");
            var name = new Label(ItemName(definition, info)) { name = "item-information-name" };
            name.AddToClassList("item-info-name"); titles.Add(name);
            var category = new Label(ItemCategoryName(definition, info, IsWaterContainerId(id)));
            category.AddToClassList("item-info-category"); titles.Add(category);
            header.Add(titles); content.Add(header);
            var description = new Label(ItemDesc(definition, info));
            description.AddToClassList("item-info-description"); content.Add(description);
            var rows = new VisualElement { name = "item-information-stats" }; content.Add(rows);

            var durability = owner == null ? new Dictionary<string, float>() : ParseKv(worn ? owner.WornDurability : owner.InventoryDurability);
            var wetness = owner == null ? new Dictionary<string, float>() : ParseKv(worn ? owner.WornWetness : owner.InventoryWetness);
            var dirtiness = owner == null ? new Dictionary<string, float>() : ParseKv(worn ? owner.WornDirtiness : owner.InventoryDirtiness);
            if (owner != null) MergeContamination(dirtiness, ParseKv(worn ? owner.WornBloodiness : owner.InventoryBloodiness));
            var water = owner == null ? new Dictionary<int, WaterContainerState>() : ParseWaterKv(owner.InventoryWater);
            if (containerSlot != null && IsWaterContainerId(id))
                water[sourceIndex] = new WaterContainerState { ItemId = id, Amount = containerSlot.ResourceAmount,
                    Capacity = id == ContentIds.Bottle ? SimBalance.BottleCapacity : SimBalance.CoconutWaterCapacity,
                    Kind = containerSlot.WaterKind };
            var owners = owner == null ? null : worn ? owner.WornOwnerIds : owner.InventoryOwnerIds;
            var ownerId = owner == null ? int.MaxValue : owners != null && sourceIndex >= 0 && sourceIndex < owners.Count ? owners[sourceIndex] : 0;
            if (_runner != null && _runner.IsReady) RefreshInventoryOwnerNames(_runner.CreateSnapshot());
            BuildItemStats(definition, info, worn, durability, water,
                new Dictionary<string, int> { [id] = count }, wetness, dirtiness, sourceIndex, ownerId, rows);
            return content;
        }
    }
}
