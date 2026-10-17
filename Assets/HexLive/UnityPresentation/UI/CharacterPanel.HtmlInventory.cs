using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the backpack window (inventory, clothes, crafting) as the page's
    /// «inventory» screen. The UI Toolkit window keeps running hidden: it owns the
    /// open/close state, refreshes the cached maps and the craft options, and its
    /// methods carry every action — the page only picks the item and the verb.
    /// Item stats come out of the very <c>BuildItemStats</c> the card uses, built
    /// into a scratch element and read back, so no game rule is written twice.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private const string HtmlInventory = "inventory";
        private const int IconsPerFrame = 4;
        private string _lastInventoryJson;
        private Rect? _dollViewport;
        private bool _dollOnScreen;
        private Rect _dollShownAt;
        private bool _inventoryHtmlHidden;
        private readonly VisualElement _statScratch = new();

        private void UpdateHtmlInventory(WorldSnapshot snapshot)
        {
            if (!_inventoryOpen)
            {
                if (_lastInventoryJson != null) { HtmlUi.Hide(HtmlInventory); _lastInventoryJson = null; }
                _inventoryHtmlHidden = false;
                return;
            }

            if (!_inventoryHtmlHidden)
            {
                // The UI Toolkit window stays the owner of the state; it is just not drawn.
                _inventoryHtmlHidden = true;
                _inventoryWindow.style.visibility = Visibility.Hidden;
                if (_inventoryModal?.Backdrop != null) _inventoryModal.Backdrop.style.visibility = Visibility.Hidden;
            }

            var npc = _inventoryActorId >= 0 ? FindNpc(snapshot, _inventoryActorId) : null;
            if (npc == null) return;
            Push(HtmlInventory, InventoryModel(npc), ref _lastInventoryJson);
        }

        /// <summary>
        /// The doll belongs to whichever of the backpack or health window is open
        /// (they never are together); either page reports the same hole.
        /// </summary>
        private void UpdateHtmlDoll()
        {
            var open = _inventoryOpen || _healthOpen;
            if (!open) _dollViewport = null;
            SetDollOnScreen(open && _dollViewport.HasValue);
        }

        /// <summary>dollRect / dollRotate from either doll window.</summary>
        private bool OnHtmlDollCommand(string command, JObject args)
        {
            switch (command)
            {
                case "dollRotate":
                    _characterDollStage?.Rotate(((float?)args["dx"] ?? 0f) * -0.55f);
                    return true;
                case "dollRect":
                    var vw = (float?)args["vw"] ?? 0; var vh = (float?)args["vh"] ?? 0;
                    var w = (float?)args["w"] ?? 0; var h = (float?)args["h"] ?? 0;
                    if (vw < 1 || vh < 1 || w < 1 || h < 1) { _dollViewport = null; return true; } // the hole left the page
                    var x = (float?)args["x"] ?? 0; var y = (float?)args["y"] ?? 0;
                    _dollViewport = new Rect(x / vw, 1 - (y + h) / vh, w / vw, h / vh);
                    return true;
                default:
                    return false;
            }
        }

        private void SetDollOnScreen(bool onScreen)
        {
            if (_characterDollStage == null) return;
            if (onScreen)
            {
                if (_dollOnScreen && _dollShownAt == _dollViewport.Value) return;
                _dollShownAt = _dollViewport.Value;
                _characterDollStage.ShowOnScreen(_dollShownAt);
                _dollOnScreen = true;
            }
            else if (_dollOnScreen) { _characterDollStage.ShowInTexture(); _dollOnScreen = false; }
        }

        // ---- icons ------------------------------------------------------------

        private int _iconsEncodedThisFrame;
        private int _iconFrame = -1;

        /// <summary>A page asset for an item icon, or null while it loads (the page shows the emoji).</summary>
        private string IconKey(string defId)
        {
            var key = "icon:" + defId;
            if (HtmlUi.HasAsset(key)) return key;
            var sprite = LoadItemIcon(defId);
            if (sprite == null) return null;
            if (_iconFrame != Time.frameCount) { _iconFrame = Time.frameCount; _iconsEncodedThisFrame = 0; }
            if (_iconsEncodedThisFrame >= IconsPerFrame) return null; // the rest next frame
            _iconsEncodedThisFrame++;
            HtmlUi.Asset(key, SpriteDataUrl(sprite));
            return key;
        }

        /// <summary>
        /// Bundled icons are not CPU-readable (isReadable 0), so the sprite is
        /// blitted through the GPU into a small render texture and read back.
        /// </summary>
        private static string SpriteDataUrl(Sprite sprite)
        {
            var texture = sprite.texture;
            var rect = sprite.textureRect;
            var width = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, 128);
            var height = Mathf.Max(1, Mathf.RoundToInt(width * rect.height / Mathf.Max(1f, rect.width)));
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                Graphics.Blit(texture, target,
                    new Vector2(rect.width / texture.width, rect.height / texture.height),
                    new Vector2(rect.x / texture.width, rect.y / texture.height));
                RenderTexture.active = target;
                read = new Texture2D(width, height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                read.Apply();
                return "data:image/png;base64," + Convert.ToBase64String(read.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (read != null) Destroy(read);
            }
        }

        /// <summary>The card's stat rows as (label, value): first and last label of each row.</summary>
        private static JArray StatRows(VisualElement rows)
        {
            var stats = new JArray();
            foreach (var row in rows.Children())
            {
                var labels = new List<Label>();
                row.Query<Label>().ForEach(labels.Add);
                if (labels.Count >= 2) stats.Add(new JArray(labels[0].text, labels[labels.Count - 1].text));
            }
            return stats;
        }

        // ---- shared with other HTML windows (§174.7: the exchange window) -------

        /// <summary>An item as the page draws it: picture (icon asset or emoji) and name.</summary>
        internal JObject HtmlItem(string uid, string id, int count)
        {
            var def = ResolveDef(id);
            var info = ResolveItemInfo(id, def);
            return new JObject
            {
                ["uid"] = uid, ["def"] = id, ["icon"] = IconKey(id), ["emoji"] = info.Emoji,
                ["name"] = ItemName(def, info), ["count"] = count,
            };
        }

        /// <summary>An item or catalog card icon as a page asset; null without one (or while it loads).</summary>
        internal string HtmlIcon(string id) => IconKey(id);

        /// <summary>A colonist's face as a page asset, or null until the cache has it.</summary>
        internal string HtmlFace(int npcId) => FaceKey(npcId);

        /// <summary>
        /// The read-only item card (§128.4) as data: the very element the exchange
        /// window shows in UI Toolkit, read back, so both windows say the same.
        /// </summary>
        internal JObject HtmlItemInformation(string id, NpcSnapshot owner, bool worn, int sourceIndex,
            int count, InventorySlotSnapshot containerSlot)
        {
            var card = CreateReadOnlyItemInformation(id, owner, worn, sourceIndex, count, containerSlot);
            return new JObject
            {
                ["category"] = card.Q<Label>(className: "item-info-category")?.text,
                ["desc"] = card.Q<Label>(className: "item-info-description")?.text,
                ["stats"] = StatRows(card.Q("item-information-stats")),
            };
        }

        // ---- the model ----------------------------------------------------------

        private JObject InventoryModel(NpcSnapshot npc)
        {
            var carriedDurability = ParseKv(npc.InventoryDurability);
            var carriedWetness = ParseKv(npc.InventoryWetness);
            var carriedDirtiness = ParseKv(npc.InventoryDirtiness);
            MergeContamination(carriedDirtiness, ParseKv(npc.InventoryBloodiness));
            var water = ParseWaterKv(npc.InventoryWater);
            var stacks = ParseIntKv(npc.InventoryStacks);

            var items = new JArray();
            var slots = 0;
            var heldMarked = false;
            foreach (var container in npc.InventoryContainers)
            {
                foreach (var slot in container.Slots)
                {
                    slots++;
                    if (slot.SourceIndex < 0 || string.IsNullOrEmpty(slot.ItemDefinitionId)) continue;
                    var held = !heldMarked && slot.ItemDefinitionId == npc.HeldItemId;
                    heldMarked |= held;
                    items.Add(ItemModel(slot.ItemDefinitionId, false, slot.SourceIndex, Math.Max(1, slot.StackCount),
                        held, slot.ItemDefinitionId == npc.FavoriteWeaponId,
                        container.Kind == InventoryContainerKind.Overflow,
                        carriedDurability, water, stacks, carriedWetness, carriedDirtiness));
                }
            }

            for (var i = 0; i < npc.WornItems.Count; i++)
            {
                items.Add(ItemModel(npc.WornItems[i], true, i, 1, false, false, false,
                    _invWornDurability, water, stacks, _invWornWetness, _invWornDirtiness));
            }

            return new JObject
            {
                ["name"] = Loc.NpcName(npc.DisplayName),
                ["readonly"] = !_inventoryMutable,
                ["outfitLocked"] = _outfitLockedNow,
                ["capacity"] = new JObject { ["used"] = npc.InventoryUsedSlots, ["cap"] = Math.Max(0, npc.InventoryCapacity) },
                ["slots"] = slots,
                ["dollLayer"] = _invVisibleWearLayer.ToString().ToLowerInvariant(),
                ["items"] = items,
                ["craft"] = CraftModel(npc),
            };
        }

        private JObject ItemModel(string id, bool worn, int sourceIndex, int count, bool inUse, bool favorite, bool overflow,
            Dictionary<string, float> durability, Dictionary<int, WaterContainerState> water, Dictionary<string, int> stacks,
            Dictionary<string, float> wetness, Dictionary<string, float> dirtiness)
        {
            var def = ResolveDef(id);
            var info = ResolveItemInfo(id, def);
            var isWater = !worn && IsWaterContainerId(id);

            // The card's own rows, read back as (label, value).
            BuildItemStats(def, info, worn, durability, water, stacks, wetness, dirtiness,
                sourceIndex, ResolveInventoryOwnerId(id, worn, sourceIndex), _statScratch);
            var stats = StatRows(_statScratch);
            _statScratch.Clear();

            // Exactly the card's predicates (ShowItemDetail).
            var wearable = def != null && def.Layer.HasValue;
            var outfitBlocked = _outfitLockedNow && (worn || wearable);
            var actions = new JArray();
            if (_inventoryMutable && (worn || wearable) && !outfitBlocked) actions.Add(worn ? "stow" : "wear");
            if (_inventoryMutable && !worn && PlantGrowthCatalog.TryGetBySeed(id, out _)) actions.Add("plant");
            if (_inventoryMutable && CanFillVessel(id, worn, water, sourceIndex) && _inventoryHasTwoHands) actions.Add("fill");
            if (_inventoryMutable && !(_outfitLockedNow && worn)) actions.Add("drop");

            return new JObject
            {
                ["uid"] = $"{(worn ? 'w' : 'c')}:{sourceIndex}:{id}",
                ["def"] = id,
                ["icon"] = IconKey(id),
                ["emoji"] = info.Emoji,
                ["name"] = ItemName(def, info),
                ["count"] = count,
                ["inUse"] = inUse,
                ["favorite"] = favorite,
                ["worn"] = worn,
                ["overflow"] = overflow,
                ["detail"] = new JObject
                {
                    ["category"] = ItemCategoryName(def, info, isWater),
                    ["desc"] = ItemDesc(def, info),
                    ["stats"] = stats,
                    ["actions"] = actions,
                },
            };
        }

        private JObject CraftModel(NpcSnapshot npc)
        {
            if (!_craftAvailable)
                return new JObject { ["blocked"] = npc.IsManualControl ? "craft.block.Incapacitated" : "craft.block.NotManual" };

            var categories = new JArray();
            var recipes = new JArray();
            var detail = new JObject();
            foreach (var option in _craftOptions)
            {
                var id = option.OutputDefinitionId;
                var def = ResolveDef(id);
                var info = ResolveItemInfo(id, def);
                var category = CraftCategories.For(id).ToString().ToLowerInvariant();
                if (!categories.Contains(category)) categories.Add(category);
                var queued = _craftQueue.Remaining(_inventoryActorId, id);
                recipes.Add(new JObject
                {
                    ["id"] = id, ["icon"] = IconKey(id), ["emoji"] = info.Emoji, ["name"] = ItemName(def, info),
                    ["category"] = category, ["working"] = option.IsActive, ["queued"] = queued,
                });
                var ingredients = new JArray();
                foreach (var ingredient in option.Ingredients)
                {
                    var ingredientDef = ResolveDef(ingredient.DefinitionId);
                    var ingredientInfo = ResolveItemInfo(ingredient.DefinitionId, ingredientDef);
                    ingredients.Add(new JObject
                    {
                        ["icon"] = IconKey(ingredient.DefinitionId), ["emoji"] = ingredientInfo.Emoji,
                        ["name"] = ItemName(ingredientDef, ingredientInfo),
                        ["have"] = ingredient.Available, ["need"] = ingredient.Required,
                    });
                }
                detail[id] = new JObject
                {
                    ["ingredients"] = ingredients,
                    ["station"] = string.IsNullOrEmpty(option.StationTag) ? null : option.StationTag.ToLowerInvariant(),
                    ["progress"] = option.IsResume && option.WorkRequired > 0
                        ? Mathf.Clamp01(option.WorkDone / (float)option.WorkRequired) : 0f,
                    ["block"] = !option.CanCraft && !option.IsActive ? option.BlockReason.ToString() : null,
                    ["button"] = option.IsActive ? "working" : option.IsResume ? "continue" : "create",
                    ["queued"] = queued,
                    ["canQueue"] = option.CanCraft || option.IsActive,
                };
            }

            JObject stopped = null;
            if (_craftQueue.TryGetStop(_inventoryActorId, out var stoppedOutput, out var reason))
                stopped = new JObject { ["recipe"] = stoppedOutput, ["reason"] = reason.ToString() };
            return new JObject { ["blocked"] = null, ["categories"] = categories, ["recipes"] = recipes, ["detail"] = detail, ["stopped"] = stopped };
        }

        // ---- commands -----------------------------------------------------------

        private void OnHtmlInventoryCommand(string command, JObject args)
        {
            _htmlDirty = true;
            if (OnHtmlDollCommand(command, args)) return;
            switch (command)
            {
                case "close": CloseInventory(); return;
                case "outfitLock":
                    if (((bool?)args["value"] ?? false) != _outfitLockedNow) ToggleOutfitLock();
                    return;
                case "dollLayer":
                    if (Enum.TryParse<VisualWearLayer>((string)args["layer"], true, out var layer))
                    {
                        _invVisibleWearLayer = layer;
                        _characterDollStage?.SetVisibleWearLayer(layer);
                    }
                    return;
                case "craft":
                    _selectedCraftOutput = (string)args["recipe"] ?? _selectedCraftOutput;
                    EnqueueSelectedCraft();
                    return;
                case "craftCancel":
                    _craftQueue.Cancel(_inventoryActorId, (string)args["recipe"] ?? "");
                    return;
                case "action":
                    if (!SelectHtmlItem((string)args["uid"])) return;
                    var count = Math.Max(1, (int?)args["count"] ?? 1);
                    switch ((string)args["kind"])
                    {
                        case "wear": EnqueueInventoryAction(InventoryAction.Wear); break;
                        case "stow": EnqueueInventoryAction(InventoryAction.Stow); break;
                        case "drop": EnqueueInventoryAction(InventoryAction.Drop, count); break;
                        case "fill": EnqueueFillVessel(); break;
                        case "plant": BeginSelectedSeedPlacement(); break;
                    }
                    return;
            }
        }

        /// <summary>«w:index:def» / «c:sourceIndex:def» → the card's selection fields.</summary>
        private bool SelectHtmlItem(string uid)
        {
            var parts = (uid ?? "").Split(new[] { ':' }, 3);
            if (parts.Length != 3 || !int.TryParse(parts[1], out var index)) return false;
            _invSelectedWorn = parts[0] == "w";
            _invSelectedSourceIndex = index;
            _invSelectedId = parts[2];
            return true;
        }
    }
}
