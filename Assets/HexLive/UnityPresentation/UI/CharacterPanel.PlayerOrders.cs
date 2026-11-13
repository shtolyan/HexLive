using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §121.13: the selected character's action list («как в Sims»). Each entry
    /// shows its own flat glyph (Player Resources <c>HexLive/UI/ActionIcons</c>,
    /// drawn by <c>Tools/gen_action_icons.py</c>), a hover card with the
    /// localized action and its target, a click cancels it, and a waiting entry
    /// can be dragged to a new place. The simulation queue stays the only
    /// source of truth: the reorder travels as <see cref="MovePlayerOrderCommand"/>.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private const int PlayerOrderChipSize = 36;
        private const int PlayerOrderChipGap = 4;
        private const float PlayerOrderDragThreshold = 6f;
        internal const string PlayerOrderIconFolder = "HexLive/UI/ActionIcons/";
        private ScrollView _playerOrdersRow;
        private readonly List<PlayerOrderView> _drawnPlayerOrders = new();
        private readonly List<VisualElement> _playerOrderChips = new();
        private static readonly Dictionary<string, Texture2D> PlayerOrderTextures = new();
        private int _drawnOrderActor = -1;
        private string _drawnOrderLanguage;
        private int _orderPressIndex = -1;
        private Vector2 _orderPressPosition;
        private bool _orderDragging;
        private int _orderDropSlot = -1;

        private VisualElement BuildPlayerOrdersRow()
        {
            _playerOrdersRow = new ScrollView(ScrollViewMode.Horizontal) { name = "player-orders" };
            _playerOrdersRow.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _playerOrdersRow.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _playerOrdersRow.contentContainer.style.flexDirection = FlexDirection.Row;
            _playerOrdersRow.style.height = 38;
            _playerOrdersRow.style.maxWidth = Length.Percent(50);
            _playerOrdersRow.style.flexGrow = 0;
            _playerOrdersRow.style.flexShrink = 0;
            _playerOrdersRow.style.marginLeft = StyleKeyword.Auto;
            _playerOrdersRow.style.marginRight = 20;
            _playerOrdersRow.style.marginBottom = 6;
            _playerOrdersRow.style.display = DisplayStyle.None;
            return _playerOrdersRow;
        }

        private void RefreshPlayerOrders(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            if (_playerOrdersRow == null)
                return;

            bool show = _controlAvailable && npc.PlayerOrders.Count > 0;
            _playerOrdersRow.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            // A drag in progress keeps its chips; the next snapshot redraws after the drop.
            if (!show || _orderPressIndex >= 0 ||
                (_drawnOrderActor == npc.Id.Value && _drawnOrderLanguage == Loc.Code &&
                _drawnPlayerOrders.SequenceEqual(npc.PlayerOrders)))
                return;

            _drawnOrderActor = npc.Id.Value;
            _drawnOrderLanguage = Loc.Code;
            _drawnPlayerOrders.Clear();
            _drawnPlayerOrders.AddRange(npc.PlayerOrders);
            _playerOrdersRow.style.width = npc.PlayerOrders.Count * (PlayerOrderChipSize + PlayerOrderChipGap);
            _playerOrdersRow.Clear();
            _playerOrderChips.Clear();
            HideEffectTooltip();
            for (var index = 0; index < npc.PlayerOrders.Count; index++)
            {
                var order = npc.PlayerOrders[index];
                var chipIndex = index;
                var actorId = npc.Id.Value;
                var caption = PlayerOrderCaption(snapshot, order);
                var item = new VisualElement { name = "player-order-" + order.Id };
                item.style.width = PlayerOrderChipSize;
                item.style.height = PlayerOrderChipSize;
                item.style.marginRight = PlayerOrderChipGap;
                item.style.flexShrink = 0;
                item.style.alignItems = Align.Center;
                item.style.justifyContent = Justify.Center;
                item.style.backgroundColor = order.Active ? new Color(.27f, .38f, .26f) : Panel;
                SetBorder(item, order.Active ? Good : Stroke, 1f);
                SetRadius(item, 18f);
                item.pickingMode = PickingMode.Position;

                var tint = order.Active ? Good : TextMute;
                var texture = PlayerOrderTexture(PlayerOrderIcon(order));
                if (texture != null)
                {
                    var glyph = new VisualElement { pickingMode = PickingMode.Ignore };
                    glyph.style.width = 22;
                    glyph.style.height = 22;
                    glyph.style.backgroundImage = new StyleBackground(texture);
                    glyph.style.unityBackgroundImageTintColor = tint;
                    glyph.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    item.Add(glyph);
                }
                else
                {
                    var icon = new VectorIcon(VectorIcon.Kind.Play, tint) { pickingMode = PickingMode.Ignore };
                    icon.style.width = 19;
                    icon.style.height = 19;
                    item.Add(icon);
                }

                item.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    if (!_orderDragging)
                        ShowPlayerOrderTooltip(caption, order, texture, item);
                });
                item.RegisterCallback<PointerLeaveEvent>(_ => HideEffectTooltip());
                item.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    _orderPressIndex = chipIndex;
                    _orderPressPosition = evt.position;
                    _orderDragging = false;
                    _orderDropSlot = -1;
                    item.CapturePointer(evt.pointerId);
                    evt.StopPropagation();
                });
                item.RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (_orderPressIndex != chipIndex || !item.HasPointerCapture(evt.pointerId)) return;
                    // The running order is not part of the reorderable tail (§121.13).
                    if (order.Active) return;
                    if (!_orderDragging &&
                        Vector2.Distance(_orderPressPosition, evt.position) < PlayerOrderDragThreshold)
                        return;
                    if (!_orderDragging)
                    {
                        _orderDragging = true;
                        HideEffectTooltip();
                        item.style.opacity = 0.55f;
                    }
                    UpdatePlayerOrderDropSlot(evt.position);
                });
                item.RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (_orderPressIndex != chipIndex) return;
                    if (item.HasPointerCapture(evt.pointerId)) item.ReleasePointer(evt.pointerId);
                    var dragged = _orderDragging;
                    var slot = _orderDropSlot;
                    EndPlayerOrderDrag();
                    if (dragged)
                        MovePlayerOrder(actorId, chipIndex, slot);
                    else
                        CancelPlayerOrder(actorId, order.Id);
                    evt.StopPropagation();
                });
                item.RegisterCallback<PointerCaptureOutEvent>(_ =>
                {
                    if (_orderPressIndex == chipIndex) EndPlayerOrderDrag();
                });
                _playerOrderChips.Add(item);
                _playerOrdersRow.Add(item);
            }
        }

        /// <summary>
        /// The insertion gap under the pointer: slot <c>k</c> means «before chip k»,
        /// <c>Count</c> means «after the last». Slots in front of the running
        /// order are clamped away — dragging in front of it is not supported.
        /// </summary>
        private void UpdatePlayerOrderDropSlot(Vector2 pointer)
        {
            var count = _drawnPlayerOrders.Count;
            var first = count > 0 && _drawnPlayerOrders[0].Active ? 1 : 0;
            var slot = count;
            for (var i = 0; i < _playerOrderChips.Count; i++)
            {
                var bound = _playerOrderChips[i].worldBound;
                if (pointer.x < bound.center.x)
                {
                    slot = i;
                    break;
                }
            }

            _orderDropSlot = Mathf.Clamp(slot, first, count);
            for (var i = 0; i < _playerOrderChips.Count; i++)
            {
                var marked = i == _orderDropSlot || (_orderDropSlot == count && i == count - 1);
                var color = marked ? Good : (_drawnPlayerOrders[i].Active ? Good : Stroke);
                SetBorder(_playerOrderChips[i], color, marked ? 2f : 1f);
            }
        }

        private void EndPlayerOrderDrag()
        {
            _orderPressIndex = -1;
            _orderDragging = false;
            _orderDropSlot = -1;
            _drawnOrderActor = -1; // redraw from the next snapshot
        }

        /// <summary>
        /// Converts «drop chip <paramref name="from"/> into gap <paramref name="slot"/>»
        /// into the stable-id command: the order goes in front of the chip that
        /// now follows the gap, or to the end (0).
        /// </summary>
        internal static bool TryPlayerOrderMove(
            IReadOnlyList<PlayerOrderView> orders, int from, int slot, out int orderId, out int beforeOrderId)
        {
            orderId = 0;
            beforeOrderId = 0;
            if (from < 0 || from >= orders.Count || orders[from].Active)
                return false;
            var first = orders.Count > 0 && orders[0].Active ? 1 : 0;
            if (slot < first || slot > orders.Count || slot == from || slot == from + 1)
                return false;
            orderId = orders[from].Id;
            beforeOrderId = slot < orders.Count ? orders[slot].Id : 0;
            return true;
        }

        private void MovePlayerOrder(int actorId, int from, int slot)
        {
            if (!TryPlayerOrderMove(_drawnPlayerOrders, from, slot, out var orderId, out var beforeOrderId))
                return;
            EnqueueMovePlayerOrder(actorId, orderId, beforeOrderId);
        }

        private void EnqueueMovePlayerOrder(int actorId, int orderId, int beforeOrderId)
        {
            if (_runner == null || !NpcSelection.Contains(actorId) || orderId <= 0)
                return;
            var actor = new EntityId(actorId);
            if (_runner.CanControlNpc(actor))
                _runner.EnqueueCommand(new MovePlayerOrderCommand(actor, orderId, beforeOrderId));
        }

        private void CancelPlayerOrder(int actorId, int orderId)
        {
            if (_runner == null || !NpcSelection.Contains(actorId) || orderId <= 0)
                return;
            var actor = new EntityId(actorId);
            if (_runner.CanControlNpc(actor))
                _runner.EnqueueCommand(new CancelPlayerOrderCommand(actor, orderId));
        }

        private void ShowPlayerOrderTooltip(string caption, PlayerOrderView order, Texture2D texture, VisualElement anchor)
        {
            if (_effectTooltip == null)
                return;
            _effectTooltipIcon.text = string.Empty;
            _effectTooltipIcon.style.backgroundImage = texture != null ? new StyleBackground(texture) : StyleKeyword.Null;
            _effectTooltipIcon.style.unityBackgroundImageTintColor = order.Active ? Good : Text;
            _effectTooltipIcon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _effectTooltipTitle.text = caption;
            _effectTooltipTitle.style.color = order.Active ? Good : Text;
            _effectTooltipDesc.text = Loc.Get(order.Active ? "panel.orders.hint_active" : "panel.orders.hint_pending");
            _effectTooltipDesc.style.display = DisplayStyle.Flex;
            _effectTooltipImpacts.style.display = DisplayStyle.None;
            PopTooltipAbove(anchor);
        }

        private static Texture2D PlayerOrderTexture(string slug)
        {
            if (!PlayerOrderTextures.TryGetValue(slug, out var texture))
            {
                texture = Resources.Load<Texture2D>(PlayerOrderIconFolder + slug);
                PlayerOrderTextures[slug] = texture;
            }
            return texture;
        }

        private JArray HtmlPlayerOrders(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            var result = new JArray();
            if (!_controlAvailable)
                return result;
            foreach (var order in npc.PlayerOrders)
            {
                result.Add(new JObject
                {
                    ["id"] = order.Id,
                    ["icon"] = HtmlPlayerOrderIcon(PlayerOrderIcon(order)),
                    ["label"] = PlayerOrderCaption(snapshot, order),
                    ["hint"] = Loc.Get(order.Active ? "panel.orders.hint_active" : "panel.orders.hint_pending"),
                    ["active"] = order.Active,
                });
            }
            return result;
        }

        /// <summary>The page asset key of an action glyph, sent once as a data URL.</summary>
        private static string HtmlPlayerOrderIcon(string slug)
        {
            var key = "action:" + slug;
            if (HexLive.UnityPresentation.Platform.HtmlUi.HasAsset(key))
                return key;
            var texture = PlayerOrderTexture(slug);
            if (texture == null)
                return null;
            HexLive.UnityPresentation.Platform.HtmlUi.Asset(key,
                TextureDataUrl(texture, new Rect(0f, 0f, texture.width, texture.height)));
            return key;
        }

        /// <summary>«Обнять: Марта» — the action and, when the order names one, its target.</summary>
        private string PlayerOrderCaption(WorldSnapshot snapshot, PlayerOrderView order)
        {
            var label = PlayerOrderLabel(order);
            var target = PlayerOrderTargetName(snapshot, order);
            return string.IsNullOrEmpty(target)
                ? label
                : string.Format(Loc.Get("panel.orders.with_target"), label, target);
        }

        private string PlayerOrderTargetName(WorldSnapshot snapshot, PlayerOrderView order)
        {
            if (snapshot == null)
                return null;
            if (order.TargetNpcId is { } npcId)
            {
                var target = FindNpc(snapshot, npcId);
                return target == null || string.IsNullOrEmpty(target.DisplayName)
                    ? null : Loc.NpcName(target.DisplayName);
            }
            if (order.TargetObjectId is { } objectId && _runner != null)
            {
                foreach (var obj in snapshot.Objects)
                    if (obj.Id.Value == objectId)
                        return CraftProjectDetailsPanel.ItemName(_runner, obj.DefinitionId);
            }
            return null;
        }

        private static string PlayerOrderLabel(PlayerOrderView order)
        {
            if (order.Action == "Interact")
            {
                var parts = order.Detail.Split('|');
                if (parts.Length == 2 && parts[1].Length > 0)
                {
                    var key = "interaction." + parts[1] + ".verb";
                    if (Loc.Has(key))
                        return Loc.Get(key);
                }
                var typeKey = "interaction." + parts[0] + ".verb";
                return Loc.Get(Loc.Has(typeKey) ? typeKey : "panel.orders.action");
            }

            var label = order.Action switch
            {
                "MoveTo" => "menu.walk_here",
                "PlantSeed" => "inv.action.plant",
                "PlaceInventoryFurniture" => "interaction.Build.verb",
                "GatherAll" => "menu.gather_all",
                "AttackNpc" or "AttackMob" => "menu.attack",
                "CarryPerson" => "menu.carry_person",
                "PutDownPerson" => "menu.put_down_person",
                "PutPersonInBed" => "menu.self.sleep",
                "Craft" => "interaction.Craft.verb",
                "Inventory" when order.Detail == "Eat" => "inv.action.eat",
                "Inventory" when order.Detail == "Cook" => "menu.cook_meat",
                "Inventory" when order.Detail == "Serve" => "inv.action.serve",
                "FillVessel" => "inv.action.fill",
                "TalkTo" => order.Detail switch
                {
                    "Hug" => "menu.hug",
                    "Kiss" => "menu.kiss",
                    "AskBathe" => "menu.ask_bathe",
                    "AskWashClothes" => "menu.ask_wash_clothes",
                    _ => "menu.talk_to",
                },
                "RequestItem" => "panel.orders.request_item",
                "Romance" or "ForceRomance" => "menu.romance",
                "Aid" => order.Detail.Length > 0 ? "menu.aid." + order.Detail.ToLowerInvariant() : "menu.aid",
                "TreatLimbs" => "menu.treat_limbs",
                "MedicalAid" => "menu.medical_aid",
                "KnockDoor" => "menu.knock_door",
                "BreakDoor" => "menu.break_door",
                "ButcherPerson" => "menu.butcher_person",
                "Prey" => "menu.dark.prey",
                "Abuse" => "menu.dark.abuse",
                "SelfAction" => order.Detail switch
                {
                    "CallForHelp" => "menu.self.call_help",
                    "TreatSelf" => "menu.self.treat",
                    "GroundSit" => "menu.self.sit",
                    "GroundSleep" => "menu.self.sleep",
                    "Bathe" => "menu.self.bathe",
                    "WashClothes" => "menu.self.wash",
                    "EatFromPack" => "menu.self.eat",
                    "DrinkFromPack" => "menu.self.drink",
                    "GoHome" => "menu.self.go_home",
                    "Explore" => "panel.orders.explore",
                    _ => "panel.orders.action",
                },
                _ => "panel.orders.action",
            };
            return Loc.Get(Loc.Has(label) ? label : "panel.orders.action");
        }

        /// <summary>
        /// §121.13: the glyph of every kind of order the queue can hold
        /// (<see cref="QueuePlayerOrderCommand.Supports"/>). Each slug is a PNG
        /// in <see cref="PlayerOrderIconFolder"/>; «action» is the fallback.
        /// </summary>
        internal static string PlayerOrderIcon(PlayerOrderView order) => order.Action switch
        {
            "MoveTo" => "walk",
            "Interact" => InteractionIcon(order.Detail.Split('|')[0]),
            "PlantSeed" => "plant",
            "PlaceInventoryFurniture" => "build",
            "GatherAll" => "gather_all",
            "AttackNpc" or "AttackMob" or "Prey" or "Abuse" => "attack",
            "CarryPerson" => "carry",
            "PutDownPerson" => "put_down",
            "PutPersonInBed" => "sleep",
            "Craft" => "craft",
            "Inventory" when order.Detail == "Eat" => "eat",
            "Inventory" when order.Detail == "Cook" => "cook",
            "FillVessel" => "fill",
            "TalkTo" => order.Detail switch
            {
                "Hug" => "hug",
                "Kiss" => "kiss",
                _ => "talk",
            },
            "RequestItem" => "give",
            "Romance" or "ForceRomance" => "romance",
            "Aid" => "aid",
            "TreatLimbs" or "MedicalAid" => "medical",
            "KnockDoor" or "BreakDoor" => "door",
            "ButcherPerson" => "butcher",
            "SelfAction" => order.Detail switch
            {
                "CallForHelp" => "shout",
                "TreatSelf" => "medical",
                "GroundSit" => "sit",
                "GroundSleep" => "sleep",
                "Bathe" => "bathe",
                "WashClothes" => "wash",
                "EatFromPack" => "eat",
                "DrinkFromPack" => "drink",
                "GoHome" => "home",
                "Explore" => "explore",
                _ => "action",
            },
            _ => "action",
        };

        private static string InteractionIcon(string interaction) => interaction switch
        {
            "PickUp" => "pickup",
            "Build" or "BuildRaft" => "build",
            "Harvest" => "harvest",
            "Process" => "chop",
            "Butcher" => "butcher",
            "Eat" => "eat",
            "Drink" => "drink",
            "FillBottle" or "PlaceVessel" or "TakeVessel" or "FillVessel" => "fill",
            "Sleep" or "PutInBed" => "sleep",
            "Sit" or "Rest" or "CoolOff" => "sit",
            "Craft" => "craft",
            "Fuel" or "Ignite" => "fire",
            "Loot" => "loot",
            "Dress" or "Undress" or "Hang" => "wear",
            "WashClothes" => "wash",
            "Plant" => "plant",
            "TravelPortal" => "portal",
            "Talk" => "talk",
            "Romance" => "romance",
            "Abuse" => "attack",
            "PickUpPerson" => "carry",
            "FeedOther" or "HydrateOther" or "ConsoleOther" => "aid",
            "TreatOther" or "MedicateOther" or "Splint" or "FitProsthetic" or "MedicalAid" or "TreatSelf" => "medical",
            _ => "use",
        };
    }
}
