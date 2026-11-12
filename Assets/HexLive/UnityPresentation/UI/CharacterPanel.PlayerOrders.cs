using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class CharacterPanel
    {
        private ScrollView _playerOrdersRow;

        private VisualElement BuildPlayerOrdersRow()
        {
            _playerOrdersRow = new ScrollView(ScrollViewMode.Horizontal) { name = "player-orders" };
            _playerOrdersRow.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _playerOrdersRow.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _playerOrdersRow.contentContainer.style.flexDirection = FlexDirection.Row;
            _playerOrdersRow.style.marginBottom = 6;
            _playerOrdersRow.style.display = DisplayStyle.None;
            return _playerOrdersRow;
        }

        private void RefreshPlayerOrders(NpcSnapshot npc)
        {
            if (_playerOrdersRow == null)
                return;

            _playerOrdersRow.Clear();
            bool show = _controlAvailable && npc.PlayerOrders.Count > 0;
            _playerOrdersRow.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
                return;

            foreach (var order in npc.PlayerOrders)
            {
                var item = new VisualElement { tooltip = PlayerOrderLabel(order) };
                item.style.width = 32;
                item.style.height = 32;
                item.style.marginRight = 4;
                item.style.flexShrink = 0;
                item.style.alignItems = Align.Center;
                item.style.justifyContent = Justify.Center;
                item.style.backgroundColor = order.Active ? new Color(.27f, .38f, .26f) : Panel;
                SetRadius(item, 16f);
                var icon = new VectorIcon(NativeOrderIcon(PlayerOrderIcon(order)), order.Active ? Good : TextMute);
                icon.style.width = 19;
                icon.style.height = 19;
                item.Add(icon);
                _playerOrdersRow.Add(item);
            }
        }

        private JArray HtmlPlayerOrders(NpcSnapshot npc)
        {
            var result = new JArray();
            if (!_controlAvailable)
                return result;
            foreach (var order in npc.PlayerOrders)
            {
                result.Add(new JObject
                {
                    ["id"] = order.Id,
                    ["icon"] = PlayerOrderIcon(order),
                    ["label"] = PlayerOrderLabel(order),
                    ["active"] = order.Active,
                });
            }
            return result;
        }

        private static string PlayerOrderLabel(PlayerOrderView order)
        {
            if (order.Action == "Interact")
            {
                var parts = order.Detail.Split('|');
                if (parts.Length == 2 && parts[1].Length > 0)
                {
                    var key = "interaction." + parts[1] + ".verb";
                    var label = Loc.Get(key);
                    if (label != key)
                        return label;
                }
                return Loc.Get("interaction." + parts[0] + ".verb");
            }

            return Loc.Get(order.Action switch
            {
                "MoveTo" => "menu.walk_here",
                "PlantSeed" => "inv.action.plant",
                "PlaceInventoryFurniture" => "interaction.Build.verb",
                "GatherAll" => "interaction.PickUp.verb",
                "AttackNpc" or "AttackMob" => "menu.attack",
                "CarryPerson" => "menu.carry_person",
                "PutDownPerson" => "menu.put_down_person",
                "PutPersonInBed" => "menu.self.sleep",
                "Craft" => "interaction.Craft.verb",
                "TalkTo" or "RequestItem" => "menu.talk_to",
                "Romance" or "ForceRomance" => "menu.romance",
                "Aid" => "menu.aid",
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
                    _ => "panel.orders.action",
                },
                _ => "panel.orders.action",
            });
        }

        private static string PlayerOrderIcon(PlayerOrderView order) => order.Action switch
        {
            "MoveTo" => "walk",
            "TalkTo" or "RequestItem" or "KnockDoor" => "social",
            "Craft" or "PlantSeed" or "PlaceInventoryFurniture" or "BreakDoor" => "hammer",
            "GatherAll" or "CarryPerson" or "PutDownPerson" => "bag",
            "Aid" or "TreatLimbs" or "MedicalAid" => "health",
            "Romance" or "ForceRomance" => "heartFill",
            "AttackNpc" or "AttackMob" or "Prey" => "alert",
            "SelfAction" when order.Detail is "Bathe" or "WashClothes" or "DrinkFromPack" => "thirst",
            "SelfAction" when order.Detail == "GroundSleep" => "comfort",
            "SelfAction" when order.Detail == "EatFromPack" => "hunger",
            _ => "play",
        };

        private static VectorIcon.Kind NativeOrderIcon(string icon) => icon switch
        {
            "social" => VectorIcon.Kind.Social,
            "bag" => VectorIcon.Kind.Bag,
            "health" => VectorIcon.Kind.Health,
            "heartFill" => VectorIcon.Kind.HeartFill,
            "alert" => VectorIcon.Kind.Shield,
            "thirst" => VectorIcon.Kind.Thirst,
            "comfort" => VectorIcon.Kind.Comfort,
            "hunger" => VectorIcon.Kind.Hunger,
            _ => VectorIcon.Kind.Play,
        };
    }
}
