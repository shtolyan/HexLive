using System;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public enum CraftCategory { All, Clothing, Prosthetics, Tools, Resources, Food, Other }

    public static class CraftCategories
    {
        public static CraftCategory For(string id)
        {
            if (id == ContentIds.WoodenArm || id == ContentIds.WoodenLeg || id.StartsWith("prosthetic.", StringComparison.Ordinal))
                return CraftCategory.Prosthetics;
            foreach (var garment in GarmentLibrary.Active)
                if (garment.Id == id) return CraftCategory.Clothing;
            if (id.StartsWith("clothing.", StringComparison.Ordinal) || id.StartsWith("underwear.", StringComparison.Ordinal) || id.StartsWith("gear.backpack", StringComparison.Ordinal)) return CraftCategory.Clothing;
            if (id.StartsWith("food.", StringComparison.Ordinal)) return CraftCategory.Food;
            if (id.StartsWith("resource.", StringComparison.Ordinal)) return CraftCategory.Resources;
            if (id.StartsWith("tool.", StringComparison.Ordinal) || id.StartsWith("weapon.", StringComparison.Ordinal) || id.StartsWith("item.", StringComparison.Ordinal)) return CraftCategory.Tools;
            return CraftCategory.Other;
        }
    }

    public sealed partial class CharacterPanel
    {
        private VisualElement _craftCategories;
        private ScrollView _craftRecipeScroll;
        private CraftCategory _craftCategory;
        private VisualElement _inventoryBackdrop;
        private static int _inventoryClosedFrame = -1;
        public static bool BlocksWorldPointer => _inventoryClosedFrame == Time.frameCount;

        private void BuildCraftCategories(VisualElement parent)
        {
            _craftCategories = new VisualElement { name = "craft-categories" };
            _craftCategories.style.flexDirection = FlexDirection.Row;
            _craftCategories.style.flexWrap = Wrap.Wrap;
            _craftCategories.style.flexShrink = 0;
            parent.Add(_craftCategories);
            RefreshCraftCategories();
        }

        private bool InCraftCategory(string id) => _craftCategory == CraftCategory.All || CraftCategories.For(id) == _craftCategory;

        private void RefreshCraftCategories()
        {
            if (_craftCategories == null) return;
            _craftCategories.Clear();
            foreach (CraftCategory category in Enum.GetValues(typeof(CraftCategory)))
            {
                var button = new Button(() => {
                    _craftCategory = category;
                    EnsureCraftCategorySelection();
                    _craftSig = CraftingSignature();
                    RefreshCraftCategories(); RebuildCraftRecipeGrid(); RefreshCraftDetail();
                    _craftRecipeScroll.scrollOffset = Vector2.zero;
                }) { name = "craft-category-" + category, text = Loc.Get("craft.category." + category.ToString().ToLowerInvariant()) };
                button.style.backgroundColor = category == _craftCategory ? GoldDim : Track;
                button.style.color = category == _craftCategory ? Text : TextDim;
                button.style.marginBottom = 6; button.style.marginRight = 4;
                SetBorder(button, category == _craftCategory ? GoldDim : StrokeStrong, 1f);
                SetRadius(button, 7f);
                _craftCategories.Add(button);
            }
        }

        private void EnsureCraftCategorySelection()
        {
            if (FindSelectedCraft() != null && InCraftCategory(_selectedCraftOutput)) return;
            _selectedCraftOutput = null;
            foreach (var option in _craftOptions)
                if (InCraftCategory(option.OutputDefinitionId)) { _selectedCraftOutput = option.OutputDefinitionId; break; }
            if (_inventoryActorId >= 0) _selectedCraftOutputByNpc[_inventoryActorId] = _selectedCraftOutput;
        }

        private void BuildInventoryBackdrop()
        {
            _inventoryBackdrop = new VisualElement { name = "inventory-backdrop", pickingMode = PickingMode.Position };
            _inventoryBackdrop.style.position = Position.Absolute;
            _inventoryBackdrop.style.left = 0; _inventoryBackdrop.style.right = 0;
            _inventoryBackdrop.style.top = 0; _inventoryBackdrop.style.bottom = 0;
            _inventoryBackdrop.style.display = DisplayStyle.None;
            _inventoryBackdrop.RegisterCallback<PointerDownEvent>(evt => {
                if (evt.button != 0 || evt.target != _inventoryBackdrop) return;
                CloseInventory(); evt.StopPropagation();
            });
            _root.Add(_inventoryBackdrop);
        }
    }
}
