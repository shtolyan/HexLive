using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// Spec §42: the single home for every wearable's parameters. Two layers:
    ///
    ///   • <see cref="Defaults"/> — the built-in table (below). Engine-free, so
    ///     the headless soak harness and any non-Unity host run without an asset.
    ///   • <see cref="Active"/> — what the content catalog actually reads. Starts
    ///     as a copy of the defaults; the Unity presentation layer overwrites it
    ///     at startup from the GarmentCatalog ScriptableObject (via GarmentTuning,
    ///     mirroring how HexTuningConfig feeds the hop/swim statics).
    ///
    /// <see cref="PrototypeContentCatalog"/> calls <see cref="AppendDefinitions"/>
    /// to turn the active table into runtime ObjectDefinitions, so removing the
    /// old hard-coded garment blocks changes nothing downstream.
    ///
    /// Warmth budget (spec §42): EquippedWarmth x10 = °C added. A summer set of
    /// top + pants + boots lands near +0.5 (~+5 °C); a coat is worth ~+0.4 on its
    /// own. Underwear is decorative warmth (0.01-0.06). Armor lives on the parts a
    /// garment covers — only leather / boots / heavy gear carry it.
    /// </summary>
    public static class GarmentLibrary
    {
        private static List<GarmentParams> _active;

        // The active table the catalog reads. Lazily seeded from the defaults so
        // a host that never applies an asset still gets sane values.
        public static IReadOnlyList<GarmentParams> Active => _active ??= BuildDefaults();

        // A fresh copy of the built-in table (never the live list).
        public static IReadOnlyList<GarmentParams> Defaults => BuildDefaults();

        // Presentation-side override: replace the active table with the catalog
        // asset's contents. Empty / null is ignored so a missing asset can't wipe
        // the wardrobe (the game falls back to the built-in defaults).
        public static void Override(IEnumerable<GarmentParams> garments)
        {
            if (garments == null)
            {
                return;
            }

            var list = new List<GarmentParams>(garments);
            if (list.Count == 0)
            {
                return;
            }

            _active = list;
        }

        public static void ResetToDefaults()
        {
            _active = BuildDefaults();
        }

        // Materialize the active table into the shared definition dictionary.
        // Each garment becomes a wearable ObjectDefinition with a Dress
        // interaction carrying warmth / armor / thermal — identical in shape to
        // the blocks this replaced.
        public static void AppendDefinitions(Dictionary<string, ObjectDefinition> defs)
        {
            if (defs == null)
            {
                return;
            }

            foreach (var g in Active)
            {
                var def = new ObjectDefinition
                {
                    Id = g.Id,
                    DisplayName = g.DisplayName,
                    Layer = g.Layer,
                    InventoryCapacity = g.Capacity
                };
                def.Covers.AddRange(g.Covers);
                def.Tags.Add("Clothing");
                // Armor tag drives inventory classification (§51) and only
                // belongs on gear that actually stops a bite.
                if (g.Armor > 0f)
                {
                    def.Tags.Add("Armor");
                }

                var dress = new InteractionDefinition
                {
                    Id = "dress." + g.Id,
                    Type = InteractionType.Dress,
                    DurationTicks = g.DressDurationTicks
                };
                dress.Effects.WarmthDelta = g.Warmth;
                dress.Effects.ArmorDelta = g.Armor;
                dress.Effects.ThermalDelta = g.ThermalDelta;
                def.Interactions.Add(dress);

                // Spec §52: a dropped garment can be "picked at" without
                // dressing — GatherTools rifles its pockets for stashed tools
                // (the undress overflow may have carried the knife down with
                // the jacket). Execution never pockets the garment itself.
                def.Interactions.Add(new InteractionDefinition
                {
                    Id = "pickup." + g.Id,
                    Type = InteractionType.PickUp,
                    DurationTicks = 4
                });

                defs[g.Id] = def;
            }
        }

        // ----- the built-in wardrobe -------------------------------------------
        // Args: (id, displayName, layer, warmth, armor, thermalDelta, dressTicks,
        //        ...covered body parts). Ids are frozen — the art loads by id.
        private static List<GarmentParams> BuildDefaults()
        {
            const int dress = 8; // §Wardrobe-anim: 2.0s dress window.

            // Spec §52 slot budget (the arg after `dress`): panties/bra 1,
            // top 2, pants 4, jacket/coat & heavy vest 6, dress 4, skirt/shorts
            // 2, leather armor 4. Pure accessories (jewelry, gloves, stockings,
            // boots, holster) carry nothing → 0.
            return new List<GarmentParams>
            {
                // --- Underwear: worn against the skin, decorative warmth. -------
                new("underwear.cloth",       "Cloth Underwear",  WearLayer.Underwear, 0.02f, 0.00f,  0.00f, dress, 1, BodyPart.Torso, BodyPart.Pelvis),
                new("underwear.panty_leo",   "Leopard Panties",  WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("underwear.panty_stars", "Star Panties",     WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("Bikini Bottom",         "Bikini nizkii",    WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("Bikini top",            "Bikini verx",      WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("Panty_11571",           "Trusiki kruzhevnye", WearLayer.Underwear, 0.01f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis),
                // New-wear drop (2026-07, Temp FBX extraction — spec §31B.4).
                new("underwear.panty_flair", "Flair Panties",    WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("underwear.panty_basic", "Cotton Panties",   WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("underwear.bra_basic",   "Cotton Bra",       WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("underwear.swim_top",    "Swimsuit Top",     WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("underwear.swim_bottom", "Swimsuit Bottom",  WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                // AI-print skins of the basic panty/bra (2026-07, fal.ai prints — spec §31B.4).
                new("underwear.panty_dots",  "Polka-Dot Panties", WearLayer.Underwear, 0.01f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis),
                new("underwear.panty_stripe","Striped Panties",  WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("underwear.panty_cherry","Cherry Panties",   WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Pelvis),
                new("underwear.bra_dots",    "Polka-Dot Bra",    WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("underwear.bra_stripe",  "Striped Bra",      WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("underwear.bra_cherry",  "Cherry Bra",       WearLayer.Underwear, 0.01f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("CowTop",                "Korotkij top",     WearLayer.Underwear, 0.03f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("Top_11927",             "Top",              WearLayer.Underwear, 0.03f, 0.00f,  0.00f, dress, 1, BodyPart.Torso),
                new("NeckWarmer_1259",       "Sharf",            WearLayer.Underwear, 0.06f, 0.00f,  0.00f, dress, 0, BodyPart.Torso),
                new("Necklace_2228",         "Kolie",            WearLayer.Underwear, 0.00f, 0.00f,  0.00f, dress, 0, BodyPart.Torso),
                new("Got stock",             "Chulki",           WearLayer.Underwear, 0.04f, 0.00f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("Stockings_8731",        "Chulki",           WearLayer.Underwear, 0.04f, 0.00f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("Over Knee G3F_18296",   "Chulki za koleno", WearLayer.Underwear, 0.04f, 0.00f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("Tights Old",            "Kolgotki starye",  WearLayer.Underwear, 0.05f, 0.00f,  0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("Tights_1818",           "Kolgotki",         WearLayer.Underwear, 0.05f, 0.00f,  0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("Boots 20496",           "Sapozhki",         WearLayer.Underwear, 0.12f, 0.05f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                // A dense leather bra (was Outerwear armor): worn against the skin,
                // the one underwear piece that still dampens a torso bite (§29C.4).
                new("armor.leather",         "Leather Armor",    WearLayer.Underwear, 0.15f, 0.15f, -0.10f, dress, 4, BodyPart.Torso),

                // --- Wear: the main clothing layer, carries the warmth budget. --
                new("clothing.coat",         "Coat",             WearLayer.Wear, 0.40f, 0.00f, -0.30f, dress, 6, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.leather_pants","Leather Pants",    WearLayer.Wear, 0.25f, 0.20f, -0.10f, dress, 4, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("Pants_24055",           "Bryuki",           WearLayer.Wear, 0.25f, 0.05f, -0.10f, dress, 4, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("pants_21038",           "Shtany",           WearLayer.Wear, 0.25f, 0.05f, -0.10f, dress, 4, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("CityDress",             "Plate",            WearLayer.Wear, 0.20f, 0.00f, -0.05f, dress, 4, BodyPart.Torso, BodyPart.Pelvis),
                new("Shirt G3F_31977",       "Rubashka",         WearLayer.Wear, 0.15f, 0.00f, -0.05f, dress, 2, BodyPart.Torso),
                new("clothing.top_tropic",   "Tropic Top",       WearLayer.Wear, 0.12f, 0.00f,  0.00f, dress, 2, BodyPart.Torso),
                new("clothing.top_tiedye",   "Tie-Dye Top",      WearLayer.Wear, 0.12f, 0.00f,  0.00f, dress, 2, BodyPart.Torso),
                new("Top_2300",              "Sportivnyj top",   WearLayer.Wear, 0.12f, 0.00f,  0.00f, dress, 2, BodyPart.Torso),
                new("TankTop9_20034",        "Majka",            WearLayer.Wear, 0.10f, 0.00f,  0.00f, dress, 2, BodyPart.Torso),
                new("Skirt 29046",           "Yubka",            WearLayer.Wear, 0.10f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("Skirt G3F_27980",       "Yubka",            WearLayer.Wear, 0.10f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                // New-wear drop (2026-07, Temp FBX extraction — spec §31B.4).
                new("clothing.sweater_flair","Flair Sweater",    WearLayer.Wear, 0.30f, 0.00f, -0.15f, dress, 4, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.dress_night",  "Night Dress",      WearLayer.Wear, 0.10f, 0.00f,  0.00f, dress, 4, BodyPart.Torso, BodyPart.Pelvis),
                new("clothing.dress_fur",    "Fur Dress",        WearLayer.Wear, 0.35f, 0.05f, -0.15f, dress, 4, BodyPart.Torso, BodyPart.Pelvis),
                new("Skirt_2799",            "Yubka mini",       WearLayer.Wear, 0.06f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("Shorts 1389",           "Shorty",           WearLayer.Wear, 0.08f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("Shorts Green",          "Shorty zelyonye",  WearLayer.Wear, 0.08f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("Shorts_10_14636",       "Shorty",           WearLayer.Wear, 0.08f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                // Detail-preserving retextures of the denim shorts (2026-07,
                // PIL recolor — seams/pockets/zipper kept, fabric re-dyed).
                new("clothing.shorts_red",   "Red Shorts",       WearLayer.Wear, 0.08f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("clothing.shorts_olive", "Olive Shorts",     WearLayer.Wear, 0.08f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("clothing.shorts_cherry","Cherry Shorts",    WearLayer.Wear, 0.08f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("Shorts short",          "Mini-shorty",      WearLayer.Wear, 0.06f, 0.00f,  0.00f, dress, 2, BodyPart.Pelvis),
                new("Glove_2245",            "Perchatki",        WearLayer.Wear, 0.04f, 0.00f,  0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("Gloves_17510",          "Perchatki",        WearLayer.Wear, 0.04f, 0.00f,  0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("Gloves_5480",           "Perchatki korotkie", WearLayer.Wear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("Gloves_8128",           "Perchatki kozhanye", WearLayer.Wear, 0.05f, 0.05f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("Sleeve_19793",          "Narukavniki",      WearLayer.Wear, 0.03f, 0.00f,  0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),

                // --- Outerwear: the top layer — jackets, boots, armor. ----------
                new("armor.heavy",           "Heavy Armor",      WearLayer.Outerwear, 0.25f, 0.50f, -0.10f, dress, 6, BodyPart.Torso, BodyPart.Pelvis),
                new("Boots",                 "Sapogi",           WearLayer.Outerwear, 0.15f, 0.10f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("Boots_155064",          "Botinki",          WearLayer.Outerwear, 0.14f, 0.10f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("legHolster_2204",       "Kabura na nogu",   WearLayer.Outerwear, 0.00f, 0.00f,  0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
            };
        }
    }
}
