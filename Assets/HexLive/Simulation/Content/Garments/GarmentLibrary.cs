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

        // §84: можно ли ЭТОМУ телу надеть ЭТУ вещь. Женская вещь на мужском
        // теле рисуется вывернутым мешем — поэтому запрет стоит в симуляции, а
        // не в виде: чужую одежду не надо отрисовывать правильнее, её не надо
        // даже рассматривать как одежду.
        //
        // Вещь без пола (Any) носят все; неизвестный id пропускаем — не наше
        // дело запрещать то, чего мы не знаем.
        public static bool FitsSex(GarmentSex wearer, string definitionId)
        {
            foreach (var g in Active)
            {
                if (g.Id == definitionId)
                {
                    return g.Sex == GarmentSex.Any || g.Sex == wearer;
                }
            }

            return true;
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
                // New-wear drop (2026-07, Temp FBX extraction — spec §31B.4).
                // AI-print skins of the basic panty/bra (2026-07, fal.ai prints — spec §31B.4).
                // A dense leather bra (was Outerwear armor): worn against the skin,
                // the one underwear piece that still dampens a torso bite (§29C.4).
                new("underwear.thong_anarchy", "Thong", WearLayer.Underwear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis),
                new("underwear.thong_anarchy_purple", "Thong (Purple)", WearLayer.Underwear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis) { PrototypeId = "underwear.thong_anarchy" },
                new("underwear.bra_riot", "Plain Bra", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("underwear.stockings_riot", "Long Stockings", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("underwear.bra_riot_blue", "Plain Bra (Blue)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_riot" },
                new("underwear.bra_riot_pink", "Plain Bra (Pink)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_riot" },
                new("underwear.bra_riot_red", "Plain Bra (Red)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_riot" },
                new("underwear.stockings_riot_clear", "Long Stockings (Clear)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.stockings_riot" },
                new("underwear.panty_tod", "Lace Briefs", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis),
                new("underwear.stockings_tod", "Lace Stockings", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("underwear.briefs_primal", "Hide Briefs", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis),
                new("underwear.briefs_primal_panty1", "Hide Briefs (Panty1)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_primal" },
                new("underwear.briefs_primal_panty2", "Hide Briefs (Panty2)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_primal" },
                new("underwear.briefs_primal_panty3", "Hide Briefs (Panty3)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_primal" },
                new("underwear.kneesocks_nerd", "Knee Socks", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("underwear.sportsbra_tek", "Sports Bra", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("underwear.briefs_flair", "Silk Briefs", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis),
                new("underwear.socks_fit", "Ankle Socks", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("underwear.kneesocks_nerd_nc_sock_left_argyle", "Knee Socks (NC Sock Left Argyle)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.kneesocks_nerd" },
                new("underwear.kneesocks_nerd_nc_sock_left_polka_dots", "Knee Socks (NC Sock Left Polka Dots)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.kneesocks_nerd" },
                new("underwear.kneesocks_nerd_nc_sock_right_hearts", "Knee Socks (NC Sock Right Hearts)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.kneesocks_nerd" },
                new("underwear.kneesocks_nerd_nc_sock_right_polka_dots", "Knee Socks (NC Sock Right Polka Dots)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.kneesocks_nerd" },
                new("underwear.sportsbra_tek_bra_01_apply_black_bottom_trim", "Sports Bra (Bra 01 Apply Black Bottom Trim)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_01_apply_black_trim", "Sports Bra (Bra 01 Apply Black Trim)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_01_apply_white_bottom_trim", "Sports Bra (Bra 01 Apply White Bottom Trim)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_01_apply_white_trim", "Sports Bra (Bra 01 Apply White Trim)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_02_black", "Sports Bra (Bra 02 Black)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_03_orange_black", "Sports Bra (Bra 03 Orange Black)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_04_red_black", "Sports Bra (Bra 04 Red Black)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_05_black_pink", "Sports Bra (Bra 05 Black Pink)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_06_blue_black", "Sports Bra (Bra 06 Blue Black)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_07_blue_black_orange", "Sports Bra (Bra 07 Blue Black Orange)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.sportsbra_tek_bra_08_white", "Sports Bra (Bra 08 White)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.sportsbra_tek" },
                new("underwear.briefs_flair_panty_02", "Silk Briefs (Panty-02)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_flair" },
                new("underwear.briefs_flair_panty_03", "Silk Briefs (Panty-03)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_flair" },
                new("underwear.socks_fit_blue", "Ankle Socks (Blue)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.socks_fit" },
                new("underwear.socks_fit_blue2", "Ankle Socks (Blue2)", WearLayer.Underwear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.socks_fit" },
                new("underwear.bra_lace", "Lace Bra", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("underwear.briefs_lace", "Lace Briefs", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis),
                new("underwear.bra_lace_dream_lace_3_bra_black", "Lace Bra (Dream Lace 3 Bra Black)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_lace" },
                new("underwear.briefs_lace_dream_lace_3_panty_black", "Lace Briefs (Dream Lace 3 Panty Black)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_lace" },
                new("underwear.briefs_lace_dream_lace_3_panty_blue1", "Lace Briefs (Dream Lace 3 Panty Blue1)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_lace" },
                new("underwear.briefs_lace_dream_lace_3_panty_blue2", "Lace Briefs (Dream Lace 3 Panty Blue2)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis) { PrototypeId = "underwear.briefs_lace" },

                // --- Wear: the main clothing layer, carries the warmth budget. --
                // New-wear drop (2026-07, Temp FBX extraction — spec §31B.4).
                // Detail-preserving retextures of the denim shorts (2026-07,
                // PIL recolor — seams/pockets/zipper kept, fabric re-dyed).
                // Pattern retextures of the Bottom_1389 shorts (2026-08, PIL —
                // original fold shading kept, fabric re-dyed in UV space).
                // Sweet Jane + Fitness Idol drop (2026-08, Temp FBX extraction —
                // spec §31B.4). The first garments fitted to all FOUR girls.
                new("clothing.cap_anarchy", "Studded Cap", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Head),
                new("clothing.top_anarchy", "Anarchy Top", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.corset_anarchy", "Laced Corset", WearLayer.Wear, 0.10f, 0.08f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.gloves_strap_anarchy", "Long Gloves", WearLayer.Wear, 0.02f, 0.02f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.pants_anarchy", "Torn Trousers", WearLayer.Wear, 0.15f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.skirt_anarchy", "Pleated Skirt", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.collar_studded_anarchy", "Strap Collar", WearLayer.Wear, 0.01f, 0.06f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.gloves_long_anarchy", "Strap Gloves", WearLayer.Wear, 0.05f, 0.03f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.cuffs_anarchy", "Studded Cuffs", WearLayer.Wear, 0.01f, 0.04f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.collar_anarchy", "Studded Collar", WearLayer.Wear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.blouse_anarchy", "Torn Blouse", WearLayer.Wear, 0.08f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.cap_anarchy_black", "Studded Cap (Black)", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_anarchy" },
                new("clothing.cap_anarchy_brown", "Studded Cap (Brown)", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_anarchy" },
                new("clothing.cap_anarchy_purple", "Studded Cap (Purple)", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_anarchy" },
                new("clothing.top_anarchy_bra_purple", "Anarchy Top (Bra Purple)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_anarchy" },
                new("clothing.top_anarchy_bra_red", "Anarchy Top (Bra Red)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_anarchy" },
                new("clothing.top_anarchy_red", "Anarchy Top (Red)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_anarchy" },
                new("clothing.corset_anarchy_red", "Laced Corset (Red)", WearLayer.Wear, 0.10f, 0.08f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.corset_anarchy" },
                new("clothing.gloves_strap_anarchy_brn", "Long Gloves (Brn)", WearLayer.Wear, 0.02f, 0.02f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.gloves_strap_anarchy" },
                new("clothing.pants_anarchy_black", "Torn Trousers (Black)", WearLayer.Wear, 0.15f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.pants_anarchy" },
                new("clothing.skirt_anarchy_red", "Pleated Skirt (Red)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.skirt_anarchy" },
                new("clothing.collar_studded_anarchy_black", "Strap Collar (Black)", WearLayer.Wear, 0.01f, 0.06f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.collar_studded_anarchy" },
                new("clothing.collar_anarchy_brn", "Studded Collar (Brn)", WearLayer.Wear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.collar_anarchy" },
                new("clothing.blouse_waist_riot", "Blouse Tied at the Waist", WearLayer.Outerwear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.blouse_riot", "Work Blouse", WearLayer.Outerwear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.cap_riot", "Pinned Cap", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head),
                new("clothing.shorts_riot", "Cut-off Shorts", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.shirt_riot", "Buttoned Shirt", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso),
                new("clothing.pants_ranger", "Ranger Trousers", WearLayer.Wear, 0.17f, 0.04f, 0.00f, dress, 3, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.vest_ranger", "Padded Vest", WearLayer.Outerwear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso),
                new("clothing.blouse_waist_riot_denim", "Blouse Tied at the Waist (Denim)", WearLayer.Outerwear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.blouse_waist_riot" },
                new("clothing.blouse_waist_riot_green", "Blouse Tied at the Waist (Green)", WearLayer.Outerwear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.blouse_waist_riot" },
                new("clothing.blouse_waist_riot_squaresclear", "Blouse Tied at the Waist (SquaresClear)", WearLayer.Outerwear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.blouse_waist_riot" },
                new("clothing.blouse_riot_denim", "Work Blouse (Denim)", WearLayer.Outerwear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.blouse_riot" },
                new("clothing.blouse_riot_green", "Work Blouse (Green)", WearLayer.Outerwear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.blouse_riot" },
                new("clothing.blouse_riot_squaresclear", "Work Blouse (SquaresClear)", WearLayer.Outerwear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.blouse_riot" },
                new("clothing.cap_riot_camel", "Pinned Cap (Camel)", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_riot" },
                new("clothing.cap_riot_denim", "Pinned Cap (Denim)", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_riot" },
                new("clothing.cap_riot_red", "Pinned Cap (Red)", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_riot" },
                new("clothing.shorts_riot_brown", "Cut-off Shorts (Brown)", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.shorts_riot" },
                new("clothing.shorts_riot_denimgrey", "Cut-off Shorts (DenimGrey)", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.shorts_riot" },
                new("clothing.shorts_riot_green", "Cut-off Shorts (Green)", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.shorts_riot" },
                new("clothing.shirt_riot_greydark", "Buttoned Shirt (GreyDark)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.shirt_riot" },
                new("clothing.shirt_riot_pink", "Buttoned Shirt (Pink)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.shirt_riot" },
                new("clothing.shirt_riot_wine", "Buttoned Shirt (Wine)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.shirt_riot" },
                new("clothing.vest_ranger_green", "Padded Vest (Green)", WearLayer.Outerwear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.vest_ranger" },
                new("clothing.vest_ranger_packs", "Padded Vest (Packs)", WearLayer.Outerwear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.vest_ranger" },
                new("clothing.babydoll_tod", "Lace Babydoll", WearLayer.Wear, 0.03f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.gloves_lace_tod", "Lace Gloves", WearLayer.Wear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.top_tod", "High-collar Blouse", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.armwraps_primal", "Hide Arm Wraps", WearLayer.Wear, 0.06f, 0.05f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.headband_primal", "Hide Headband", WearLayer.Wear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Head),
                new("clothing.skirt_primal", "Hide Skirt", WearLayer.Wear, 0.07f, 0.02f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.Pelvis),
                new("clothing.top_primal", "Hide Wrap", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.pendant_amy", "Pendant", WearLayer.Wear, 0.00f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.shirt_amy", "Street Shirt", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.skirt_amy", "Pleated Skirt", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.armwraps_primal_sleeve1", "Hide Arm Wraps (Sleeve1)", WearLayer.Wear, 0.06f, 0.05f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.armwraps_primal" },
                new("clothing.armwraps_primal_sleeve2", "Hide Arm Wraps (Sleeve2)", WearLayer.Wear, 0.06f, 0.05f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.armwraps_primal" },
                new("clothing.armwraps_primal_sleeve3", "Hide Arm Wraps (Sleeve3)", WearLayer.Wear, 0.06f, 0.05f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.armwraps_primal" },
                new("clothing.headband_primal_2", "Hide Headband (2)", WearLayer.Wear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.headband_primal" },
                new("clothing.headband_primal_3", "Hide Headband (3)", WearLayer.Wear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.headband_primal" },
                new("clothing.headband_primal_4", "Hide Headband (4)", WearLayer.Wear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.headband_primal" },
                new("clothing.skirt_primal_1", "Hide Skirt (1)", WearLayer.Wear, 0.07f, 0.02f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "clothing.skirt_primal" },
                new("clothing.skirt_primal_2", "Hide Skirt (2)", WearLayer.Wear, 0.07f, 0.02f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "clothing.skirt_primal" },
                new("clothing.skirt_primal_3", "Hide Skirt (3)", WearLayer.Wear, 0.07f, 0.02f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.Pelvis) { PrototypeId = "clothing.skirt_primal" },
                new("clothing.top_primal_1", "Hide Wrap (1)", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_primal" },
                new("clothing.top_primal_2", "Hide Wrap (2)", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_primal" },
                new("clothing.top_primal_3", "Hide Wrap (3)", WearLayer.Wear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_primal" },
                new("clothing.shirt_amy_07", "Street Shirt (07)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.shirt_amy" },
                new("clothing.shirt_amy_08", "Street Shirt (08)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.shirt_amy" },
                new("clothing.skirt_amy_04", "Pleated Skirt (04)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.skirt_amy" },
                new("clothing.skirt_amy_05", "Pleated Skirt (05)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.skirt_amy" },
                new("clothing.blouse_nerd", "School Blouse", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.bowtie_nerd", "Bow Tie", WearLayer.Wear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.tutu_nerd", "Tutu Skirt", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.jackettied_tek", "Jacket Tied at the Waist", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.yogapants_tek", "Yoga Pants", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.skirt_flair", "Pencil Skirt", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.sweater_flair", "Knit Sweater", WearLayer.Wear, 0.19f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.gloves_fit", "Training Gloves", WearLayer.Wear, 0.03f, 0.04f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.croptop_fit", "Crop Top", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.shorts_fit", "Running Shorts", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis),
                new("clothing.yogapants_tek_yoga_01_black_mesh", "Yoga Pants (Yoga 01 Black Mesh)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_02_black", "Yoga Pants (Yoga 02 Black)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_03_black_red", "Yoga Pants (Yoga 03 Black Red)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_04_black_orange", "Yoga Pants (Yoga 04 Black Orange)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_05_black_pink", "Yoga Pants (Yoga 05 Black Pink)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_06_black_blue", "Yoga Pants (Yoga 06 Black Blue)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_07_black_plain", "Yoga Pants (Yoga 07 Black Plain)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.yogapants_tek_yoga_08_white_plain", "Yoga Pants (Yoga 08 White Plain)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.yogapants_tek" },
                new("clothing.skirt_flair_skirt_02", "Pencil Skirt (Skirt-02)", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.skirt_flair" },
                new("clothing.skirt_flair_skirt_03", "Pencil Skirt (Skirt-03)", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.skirt_flair" },
                new("clothing.sweater_flair_sweater_02", "Knit Sweater (Sweater-02)", WearLayer.Wear, 0.19f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.sweater_flair" },
                new("clothing.sweater_flair_sweater_03", "Knit Sweater (Sweater-03)", WearLayer.Wear, 0.19f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.sweater_flair" },
                new("clothing.gloves_fit_blue", "Training Gloves (Blue)", WearLayer.Wear, 0.03f, 0.04f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.gloves_fit" },
                new("clothing.gloves_fit_blue2", "Training Gloves (Blue2)", WearLayer.Wear, 0.03f, 0.04f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.gloves_fit" },
                new("clothing.croptop_fit_1_blackblue", "Crop Top (1 BlackBlue)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_blacklime", "Crop Top (1 BlackLime)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_blackpink", "Crop Top (1 BlackPink)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_blackwhite", "Crop Top (1 BlackWhite)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_blue", "Crop Top (1 Blue)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_lime", "Crop Top (1 Lime)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_pink", "Crop Top (1 Pink)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_1_white", "Crop Top (1 White)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_2_black", "Crop Top (2 Black)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_black", "Crop Top (3 Black)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_black2", "Crop Top (3 Black2)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_blackgreen", "Crop Top (3 BlackGreen)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_blackmagenta", "Crop Top (3 BlackMagenta)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_blackorange", "Crop Top (3 BlackOrange)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_magenta", "Crop Top (3 Magenta)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_orange", "Crop Top (3 Orange)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_turquoise", "Crop Top (3 Turquoise)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_3_white", "Crop Top (3 White)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_4_batik1", "Crop Top (4 Batik1)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_4_batik2", "Crop Top (4 Batik2)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_4_batik3", "Crop Top (4 Batik3)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_4_batik4", "Crop Top (4 Batik4)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.croptop_fit_4_batik5", "Crop Top (4 Batik5)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_fit" },
                new("clothing.shorts_fit_1_blackblue", "Running Shorts (1 BlackBlue)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_1_blacklime", "Running Shorts (1 BlackLime)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_1_blackpink", "Running Shorts (1 BlackPink)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_1_blackwhite", "Running Shorts (1 BlackWhite)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_2_black", "Running Shorts (2 Black)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_black", "Running Shorts (3 Black)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_blackgreen", "Running Shorts (3 BlackGreen)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_blackmagenta", "Running Shorts (3 BlackMagenta)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_blackorange", "Running Shorts (3 BlackOrange)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_magenta", "Running Shorts (3 Magenta)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_orange", "Running Shorts (3 Orange)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_turquoise", "Running Shorts (3 Turquoise)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_3_white", "Running Shorts (3 White)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_4_batik1", "Running Shorts (4 Batik1)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_4_batik2", "Running Shorts (4 Batik2)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_4_batik3", "Running Shorts (4 Batik3)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_4_batik4", "Running Shorts (4 Batik4)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_4_batik5", "Running Shorts (4 Batik5)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_fit_4_camouflage", "Running Shorts (4 Camouflage)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 1, BodyPart.Pelvis) { PrototypeId = "clothing.shorts_fit" },
                new("clothing.shorts_summer", "Denim Shorts", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis),
                new("clothing.tshirt_summer", "T-Shirt", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.tanktop_summer", "Tank Top", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.croptop_idol", "Cropped Top", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.leggings_idol", "Leggings", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.sleeves_idol", "Detached Sleeves", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.gloves_biker", "Biker Gloves", WearLayer.Wear, 0.06f, 0.06f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.pants_biker", "Biker Trousers", WearLayer.Wear, 0.18f, 0.09f, 0.00f, dress, 3, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.top_folk", "Folk Blouse", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.armguards_fighter", "Arm Guards", WearLayer.Wear, 0.04f, 0.15f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.keikogi_fighter", "Keikogi", WearLayer.Wear, 0.13f, 0.03f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.pants_fighter", "Training Trousers", WearLayer.Wear, 0.10f, 0.01f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.top_fighter", "Wrapped Top", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("clothing.tshirt_summer_i13sw_tshirt_02", "T-Shirt (i13SW TShirt 02)", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.tshirt_summer" },
                new("clothing.tshirt_summer_i13sw_tshirt_03", "T-Shirt (i13SW TShirt 03)", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.tshirt_summer" },
                new("clothing.tshirt_summer_i13sw_tshirt_04", "T-Shirt (i13SW TShirt 04)", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.tshirt_summer" },
                new("clothing.tshirt_summer_i13sw_tshirt_05", "T-Shirt (i13SW TShirt 05)", WearLayer.Wear, 0.07f, 0.00f, 0.00f, dress, 0, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.tshirt_summer" },
                new("clothing.tanktop_summer_i13sw_tank_02", "Tank Top (i13SW Tank 02)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.tanktop_summer" },
                new("clothing.tanktop_summer_i13sw_tank_03", "Tank Top (i13SW Tank 03)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.tanktop_summer" },
                new("clothing.tanktop_summer_i13sw_tank_04", "Tank Top (i13SW Tank 04)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.tanktop_summer" },
                new("clothing.tanktop_summer_i13sw_tank_05", "Tank Top (i13SW Tank 05)", WearLayer.Wear, 0.05f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.tanktop_summer" },
                new("clothing.croptop_idol_blue", "Cropped Top (Blue)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.croptop_idol" },
                new("clothing.leggings_idol_blue", "Leggings (Blue)", WearLayer.Wear, 0.12f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.leggings_idol" },

                // --- Outerwear: the top layer — jackets, boots, armor. ----------
                // §52.8 tool holster: GEAR, not clothing — no warmth, no thermal
                // pull, 0 pockets. Its 3 TYPED weapon slots (HolsterCatalog) are
                // the whole point; the token 0.05 armor is the buckled leather
                // strap itself, and gives her a reason to want it on.
                new("clothing.hipbelt_anarchy", "Hip Straps", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.belt_anarchy", "Studded Belt", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis),
                new("clothing.boots_anarchy", "Buckled Thigh Boots", WearLayer.Outerwear, 0.14f, 0.13f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.boots_anarchy_purple", "Buckled Boots (Purple)", WearLayer.Outerwear, 0.14f, 0.13f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_anarchy" },
                new("clothing.boots_anarchy_red", "Buckled Boots (Red)", WearLayer.Outerwear, 0.14f, 0.13f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_anarchy" },
                new("gear.backpack_riot", "Canvas Backpack", WearLayer.Bags, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso),
                new("clothing.boots_riot", "Laced Work Boots", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.boots_ranger", "Ranger Boots", WearLayer.Outerwear, 0.15f, 0.14f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("gear.beltpouch_ranger", "Belt Pouches", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 3, BodyPart.Pelvis),
                new("clothing.jacket_ranger", "Ranger Jacket", WearLayer.Outerwear, 0.30f, 0.06f, 0.00f, dress, 3, BodyPart.Torso),
                new("gear.backpack_riot_denim", "Canvas Backpack (Denim)", WearLayer.Bags, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso) { PrototypeId = "gear.backpack_riot" },
                new("gear.backpack_riot_grey", "Canvas Backpack (Grey)", WearLayer.Bags, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso) { PrototypeId = "gear.backpack_riot" },
                new("gear.backpack_riot_leather", "Canvas Backpack (Leather)", WearLayer.Bags, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso) { PrototypeId = "gear.backpack_riot" },
                new("clothing.greaves_tod", "Brass Greaves", WearLayer.Outerwear, 0.04f, 0.18f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.shoes_tod", "Buckled Shoes", WearLayer.Outerwear, 0.05f, 0.02f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.wrapboots_primal", "Wrapped Boots", WearLayer.Outerwear, 0.11f, 0.07f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.thighboots_amy", "Thigh Boots", WearLayer.Outerwear, 0.16f, 0.09f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.ankleboots_amy", "Ankle Boots", WearLayer.Outerwear, 0.10f, 0.08f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.wrapboots_primal_1", "Wrapped Boots (1)", WearLayer.Outerwear, 0.11f, 0.07f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.wrapboots_primal" },
                new("clothing.wrapboots_primal_2", "Wrapped Boots (2)", WearLayer.Outerwear, 0.11f, 0.07f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.wrapboots_primal" },
                new("clothing.wrapboots_primal_3", "Wrapped Boots (3)", WearLayer.Outerwear, 0.11f, 0.07f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.wrapboots_primal" },
                new("clothing.glasses_nerd", "Round Glasses", WearLayer.Outerwear, 0.00f, 0.00f, 0.00f, dress, 0, BodyPart.Head),
                new("clothing.sneakers_nerd", "Canvas Sneakers", WearLayer.Outerwear, 0.07f, 0.03f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.jacket_tek", "Training Jacket", WearLayer.Outerwear, 0.22f, 0.02f, 0.00f, dress, 2, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.pumps_flair", "Pumps", WearLayer.Outerwear, 0.04f, 0.01f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.sneakers_nerd_nc_sneakers_pink", "Canvas Sneakers (NC Sneakers Pink)", WearLayer.Outerwear, 0.07f, 0.03f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.sneakers_nerd" },
                new("clothing.sneakers_nerd_nc_sneakers_purple", "Canvas Sneakers (NC Sneakers Purple)", WearLayer.Outerwear, 0.07f, 0.03f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.sneakers_nerd" },
                new("clothing.jacket_biker", "Biker Jacket", WearLayer.Outerwear, 0.32f, 0.14f, 0.00f, dress, 3, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.boots_biker", "Biker Boots", WearLayer.Outerwear, 0.15f, 0.14f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.boots_leather", "Leather Boots", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.belt_fighter", "Wide Belt", WearLayer.Outerwear, 0.01f, 0.02f, 0.00f, dress, 2, BodyPart.Torso),
                new("clothing.footwear_fighter", "Wrapped Sandals", WearLayer.Outerwear, 0.07f, 0.05f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.boots_leather_lb_black_red_leather", "Leather Boots (LB Black Red Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_black_white_leather", "Leather Boots (LB Black White Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_black_yellow_leather", "Leather Boots (LB Black Yellow Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_red_black_leather", "Leather Boots (LB Red Black Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_red_leather", "Leather Boots (LB Red Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_red_white_leather", "Leather Boots (LB Red White Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_red_yellow_leather", "Leather Boots (LB Red Yellow Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_white_black_leather", "Leather Boots (LB White Black Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_white_leather", "Leather Boots (LB White Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_white_red_leather", "Leather Boots (LB White Red Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_white_yellow_leather", "Leather Boots (LB White Yellow Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_yellow_black_leather", "Leather Boots (LB Yellow Black Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_yellow_leather", "Leather Boots (LB Yellow Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_yellow_red_leather", "Leather Boots (LB Yellow Red Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },
                new("clothing.boots_leather_lb_yellow_white_leather", "Leather Boots (LB Yellow White Leather)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_leather" },

                // --- §72: снаряжение чужака ------------------------------------
                // Он не выживальщик в трусах, а боец: тактический комплект с
                // настоящей защитой. Ids равны именам папок в
                // Resources/HexLive/Wear — арт грузится по id.
                //
                // Броня берётся МАКСИМУМОМ по части тела (EquipmentMath), а не
                // суммой, поэтому важна лучшая вещь на каждую зону, а не число
                // слоёв. Итог по нему: торс 0.40, таз 0.30, ноги 0.30, руки 0.20,
                // голова 0 — шлема нет, и это его слабое место.
                new("TonnyFlash",            "Poddospeshnik",    WearLayer.Underwear, 0.12f, 0.10f, -0.02f, dress, 0, GarmentSex.Male, BodyPart.Torso),
                new("FCO Pants Male",        "Shtany boitsa",    WearLayer.Wear,      0.20f, 0.25f, -0.04f, dress, 2, GarmentSex.Male, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("FCO Belt Male",         "Poyas boitsa",     WearLayer.Wear,      0.04f, 0.15f,  0.00f, dress, 2, GarmentSex.Male, BodyPart.Torso),
                new("FCO Gloves Male",       "Perchatki",        WearLayer.Wear,      0.06f, 0.20f,  0.00f, dress, 0, GarmentSex.Male, BodyPart.ArmL, BodyPart.ArmR),
                new("FAO Harness Male",      "Portupeya",        WearLayer.Outerwear, 0.10f, 0.40f, -0.06f, dress, 3, GarmentSex.Male, BodyPart.Torso),
                new("FCO Boots Male",        "Sapogi boitsa",    WearLayer.Outerwear, 0.16f, 0.20f,  0.00f, dress, 0, GarmentSex.Male, BodyPart.LegL, BodyPart.LegR),
                new("FCO Legs Straps Male",  "Nabedrenniki",     WearLayer.Outerwear, 0.05f, 0.30f, -0.02f, dress, 1, GarmentSex.Male, BodyPart.LegL, BodyPart.LegR),
                new("FCO Knee Straps Male",  "Nakolenniki",      WearLayer.Outerwear, 0.04f, 0.30f, -0.02f, dress, 0, GarmentSex.Male, BodyPart.LegL, BodyPart.LegR),
                new("FCO Waist Strappy Male","Nabedrennyy remen",WearLayer.Outerwear, 0.04f, 0.30f, -0.02f, dress, 1, GarmentSex.Male, BodyPart.Pelvis),
            };
        }
    }
}
