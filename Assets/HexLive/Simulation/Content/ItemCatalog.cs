using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    // Spec §51: how the inventory UI classifies one item for its icon, tint and
    // "what is this" copy. Kept Unity-free (no Color/Sprite) — the presentation
    // layer maps the category to a colour and the Emoji to a placeholder glyph,
    // exactly like EffectCatalog does for status effects (§48). Real icons can
    // replace the emoji per id later without touching this file.
    public enum ItemCategory
    {
        Weapon,
        Tool,
        Clothing,
        Armor,
        Food,
        Water,
        Medicine,
        Resource,
        Misc
    }

    // The static "what is this item" descriptor the inventory panel reads. Name
    // and description resolve through Loc: "item.<slug>.name" / ".desc" when a
    // hand-authored string exists, otherwise the panel falls back to the
    // definition's DisplayName and a generic per-category blurb
    // ("itemcat.<category>.desc"). Nothing here is mutable per-NPC — the live
    // wetness/durability of a specific instance stays on ItemInstance.
    public sealed class ItemInfo
    {
        public string DefinitionId { get; }

        public ItemCategory Category { get; }

        // Placeholder glyph shown in the slot (emoji for v1, spec §51.2).
        public string Emoji { get; }

        // Per-item localization keys (may be absent → panel uses fallbacks).
        public string NameKey { get; }

        public string DescKey { get; }

        // Per-category localization keys (always present in Loc).
        public string CategoryNameKey { get; }

        public string CategoryDescKey { get; }

        public ItemInfo(string definitionId, ItemCategory category, string emoji)
        {
            DefinitionId = definitionId;
            Category = category;
            Emoji = emoji;

            var slug = Slug(definitionId);
            NameKey = $"item.{slug}.name";
            DescKey = $"item.{slug}.desc";

            var cat = category.ToString().ToLowerInvariant();
            CategoryNameKey = $"itemcat.{cat}.name";
            CategoryDescKey = $"itemcat.{cat}.desc";
        }

        // Loc-key friendly form of an id: lowercase, every non-alphanumeric
        // character collapsed to '_' ("Shirt G3F_31977" → "shirt_g3f_31977").
        public static string Slug(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return string.Empty;
            }

            var chars = id.ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }

    // Spec §51: single source of truth for how an inventory item is presented.
    // Classification is derived from the ObjectDefinition's tags/layer so newly
    // added content (e.g. imported garments) is covered automatically; a small
    // per-id emoji table sharpens the glyph where the category default is too
    // generic.
    public static class ItemCatalog
    {
        // A nicer glyph than the category default for well-known items.
        private static readonly Dictionary<string, string> EmojiById = new()
        {
            ["food.coconut"] = "🥥",
            ["food.coconut_pierced"] = "🥥",
            ["food.coconut_open"] = "🥥",
            ["food.meat_cooked"] = "🍖",
            ["food.meat_raw"] = "🥩",
            ["tool.bottle"] = "🧴",
            ["tool.pot"] = "🍲",
            ["tool.lighter"] = "🔥",
            ["tool.axe_stone"] = "🪓",
            ["tool.pickaxe_stone"] = "⛏️",
            ["tool.saw"] = "🪚",
            ["tool.bow"] = "🏹",
            ["tool.spear"] = "🔱",
            ["resource.arrow"] = "🎯",
            ["resource.stone"] = "🪨",
            ["resource.log"] = "🪵",
            ["resource.stick"] = "🥢",
            ["resource.fiber"] = "🌾",
            ["resource.rope"] = "🪢",
            ["resource.cloth"] = "🧵",
            ["tool.knife"] = "🔪",
            ["resource.hide"] = "🟫",
            ["resource.palm_leaf"] = "🍃",
            ["resource.herb_leaf"] = "🌿",
            ["item.bandage"] = "🩹",
        };

        // Spec §52: how badly the NPC wants to keep this in a full pack. Water
        // and food outrank everything — you drop a stone before your dinner. The
        // haul-to-fire / spill-overflow logic drops the LOWEST importance first,
        // and the keep-a-stash logic fetches the HIGHEST it lacks.
        // Spec §54: water is paramount; food and DEFENCE (a weapon) rank next and
        // roughly equal — a survivor guards her blade almost as fiercely as her
        // dinner and never sets it down to free a pocket.
        public static int Importance(ItemCategory category) => category switch
        {
            ItemCategory.Water => 100,
            ItemCategory.Food => 95,
            ItemCategory.Weapon => 93,
            ItemCategory.Medicine => 80,
            ItemCategory.Tool => 60,
            ItemCategory.Armor => 45,
            ItemCategory.Clothing => 40,
            ItemCategory.Resource => 20,
            _ => 10
        };

        // Importance of a specific item, category resolved from its definition
        // (falls back to the id-prefix heuristic when the def is absent).
        public static int Importance(ObjectDefinition def) =>
            Importance(def != null && IsWaterSourceId(def.Id)
                ? ItemCategory.Water
                : def != null ? Classify(def) : ItemCategory.Misc);

        public static int ImportanceById(string definitionId) =>
            Importance(IsWaterSourceId(definitionId)
                ? ItemCategory.Water
                : ClassifyById(definitionId));

        public static bool IsWaterContainerId(string definitionId) =>
            definitionId == "tool.bottle" ||
            definitionId == "food.coconut_pierced";

        public static bool IsWaterSourceId(string definitionId) =>
            IsWaterContainerId(definitionId) ||
            definitionId == "food.coconut";

        // Resolve from a full definition (preferred — tags/layer drive category).
        public static ItemInfo Resolve(ObjectDefinition def)
        {
            if (def == null)
            {
                return new ItemInfo(string.Empty, ItemCategory.Misc, CategoryEmoji(ItemCategory.Misc));
            }

            var category = Classify(def);
            var emoji = EmojiById.TryGetValue(def.Id, out var e) ? e : CategoryEmoji(category);
            return new ItemInfo(def.Id, category, emoji);
        }

        // Fallback when only the id is known (no ObjectDefinition on hand).
        public static ItemInfo Resolve(string definitionId)
        {
            var category = ClassifyById(definitionId);
            var emoji = EmojiById.TryGetValue(definitionId, out var e) ? e : CategoryEmoji(category);
            return new ItemInfo(definitionId, category, emoji);
        }

        public static ItemCategory Classify(ObjectDefinition def)
        {
            if (def == null)
            {
                return ItemCategory.Misc;
            }

            var tags = def.Tags;

            // Water containers are strategic inventory items regardless of their
            // static authoring tags: bottle is technically a tool, pierced coconut
            // is technically a coconut, but both are carried water.
            if (IsWaterContainerId(def.Id) || tags.Contains("CoconutWater"))
            {
                return ItemCategory.Water;
            }

            // Wearables first: a Layer (or the Clothing tag) makes it apparel;
            // the Armor tag splits protective gear from plain clothing.
            if (def.Layer.HasValue || tags.Contains("Clothing"))
            {
                return tags.Contains("Armor") ? ItemCategory.Armor : ItemCategory.Clothing;
            }

            // Spec §54: anything that can DEFEND is a Weapon — the spear/bow, but
            // also the axe, pickaxe and knife (all real strike-back weapons now).
            // Weapons rank near food in importance so a survivor never drops her
            // means of fighting off a dog.
            if (tags.Contains("Weapon") || tags.Contains("Axe") ||
                tags.Contains("Pickaxe") || tags.Contains("Knife"))
            {
                return ItemCategory.Weapon;
            }

            if (tags.Contains("Medicine") || tags.Contains("Herb"))
            {
                return ItemCategory.Medicine;
            }

            if (tags.Contains("Food") || tags.Contains("RawMeat"))
            {
                return ItemCategory.Food;
            }

            if (tags.Contains("RawWater") || tags.Contains("Water"))
            {
                return ItemCategory.Water;
            }

            // Non-combat tools (pot, lighter, bottle, hammer, saw) — the axe /
            // pickaxe / knife are classified as Weapon above.
            if (tags.Contains("Tool") || tags.Contains("Saw"))
            {
                return ItemCategory.Tool;
            }

            if (tags.Contains("Wood") || tags.Contains("Stone") || tags.Contains("Hide") ||
                tags.Contains("PalmLeaf") || tags.Contains("Resource"))
            {
                return ItemCategory.Resource;
            }

            return ItemCategory.Misc;
        }

        // Weak id-prefix inference for the id-only fallback path.
        private static ItemCategory ClassifyById(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return ItemCategory.Misc;
            }

            if (IsWaterContainerId(id)) return ItemCategory.Water;
            if (id.StartsWith("armor.")) return ItemCategory.Armor;
            if (id.StartsWith("clothing.") || id.StartsWith("underwear.")) return ItemCategory.Clothing;
            if (id.StartsWith("food.")) return ItemCategory.Food;
            if (id.StartsWith("water.")) return ItemCategory.Water;
            if (id == "item.bandage") return ItemCategory.Medicine;
            if (id.StartsWith("tool.")) return ItemCategory.Tool;
            if (id.StartsWith("resource.")) return ItemCategory.Resource;

            // Imported garments carry no dotted prefix — treat as clothing.
            return ItemCategory.Clothing;
        }

        public static string CategoryEmoji(ItemCategory category)
        {
            return category switch
            {
                ItemCategory.Weapon => "⚔️",
                ItemCategory.Tool => "🛠️",
                ItemCategory.Clothing => "👕",
                ItemCategory.Armor => "🛡️",
                ItemCategory.Food => "🍎",
                ItemCategory.Water => "💧",
                ItemCategory.Medicine => "💊",
                ItemCategory.Resource => "📦",
                _ => "❔"
            };
        }
    }
}
