using System;
using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>Semantic wardrobe category, independent of layer and body slots.</summary>
    public enum GarmentCategory
    {
        Unclassified = 0,
        Underwear, Top, Bottom, Dress, Outerwear, Footwear, Gloves,
        Headwear, Neckwear, Jewellery, Armwear, Legwear, Belt, Bag, Outfit, Accessory
    }

    /// <summary>Single categoriser used to stamp editable garment metadata.</summary>
    public static class GarmentCategoryRules
    {
        public static GarmentCategory Classify(string id, string displayName, WearLayer layer,
            IReadOnlyList<BodyPart> covers, int capacity)
        {
            // Content id is the durable authored vocabulary. Display text is
            // intentionally not a primary source: "Ring Top" must not become
            // jewellery and "High-collar Blouse" must not become neckwear.
            var text = (id ?? string.Empty).ToLowerInvariant();
            if (HasToken(text, "underwear")) return GarmentCategory.Underwear;
            // Footwear source ids also use compound family names such as
            // "wrapboots" and "slipons".  These are deliberately recognised
            // as authored vocabulary before the generic Outerwear fallback.
            if (HasFragment(text, "boot", "shoe", "sandal", "sneaker", "heel", "pump", "slipon", "footwear")) return GarmentCategory.Footwear;
            if (HasToken(text, "glove", "gloves", "mitten", "mittens")) return GarmentCategory.Gloves;
            if (HasToken(text, "necklace", "pendant", "bracelet", "earring")) return GarmentCategory.Jewellery;
            if (HasToken(text, "scarf", "bowtie", "choker", "collar", "necktie")) return GarmentCategory.Neckwear;
            if (HasToken(text, "cap", "hat", "headband", "glasses", "sunglasses", "goggles")) return GarmentCategory.Headwear;
            if (HasToken(text, "backpack", "bag", "purse", "pouch", "holster")) return GarmentCategory.Bag;
            if (HasToken(text, "belt")) return GarmentCategory.Belt;
            if (HasToken(text, "dress", "babydoll")) return GarmentCategory.Dress;
            if (HasToken(text, "outfit", "keikogi", "bodysuit", "jumpsuit", "suit")) return GarmentCategory.Outfit;
            if (HasToken(text, "stocking", "stockings", "sock", "socks", "tight", "tights", "greave", "greaves")) return GarmentCategory.Legwear;
            if (HasToken(text, "sleeve", "sleeves", "armwrap", "armguard", "armguards", "bracer", "bracers", "cuffs")) return GarmentCategory.Armwear;
            if (HasToken(text, "pants", "trouser", "trousers", "short", "shorts", "skirt", "leggings", "yogapants")) return GarmentCategory.Bottom;
            if (HasToken(text, "top", "shirt", "tshirt", "tank", "tanktop", "blouse", "sweater", "halter", "croptop", "corset")) return GarmentCategory.Top;
            if (HasToken(text, "jacket", "coat", "hoodie", "vest", "cape", "poncho")) return GarmentCategory.Outerwear;
            if (layer == WearLayer.Underwear) return GarmentCategory.Underwear;
            if (layer == WearLayer.Outerwear) return GarmentCategory.Outerwear;
            if (capacity >= 2 && Covers(covers, BodyPart.Torso)) return GarmentCategory.Top;
            if (capacity >= 2 && Covers(covers, BodyPart.Pelvis)) return GarmentCategory.Bottom;
            return GarmentCategory.Accessory;
        }

        private static bool HasToken(string value, params string[] tokens)
        {
            var start = 0;
            while (start < value.Length)
            {
                while (start < value.Length && !char.IsLetterOrDigit(value[start])) start++;
                var end = start;
                while (end < value.Length && char.IsLetterOrDigit(value[end])) end++;
                if (end == start) break;

                foreach (var token in tokens)
                {
                    if (token.Length == end - start &&
                        string.Compare(value, start, token, 0, token.Length, StringComparison.Ordinal) == 0) return true;
                }

                start = end;
            }
            return false;
        }

        private static bool HasFragment(string value, params string[] fragments)
        {
            foreach (var fragment in fragments)
            {
                if (value.IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool Covers(IReadOnlyList<BodyPart> covers, BodyPart part)
        {
            if (covers == null) return false;
            for (var index = 0; index < covers.Count; index++) if (covers[index] == part) return true;
            return false;
        }
    }
}
