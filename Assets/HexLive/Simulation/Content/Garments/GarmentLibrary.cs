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
                new("underwear.thong_anarchy_red", "Thong (Red)", WearLayer.Underwear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis) { PrototypeId = "underwear.thong_anarchy" },
                new("underwear.bra_riot", "Plain Bra", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso),
                new("underwear.stockings_riot", "Long Stockings", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("underwear.bra_riot_blue", "Plain Bra (Blue)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_riot" },
                new("underwear.bra_riot_pink", "Plain Bra (Pink)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_riot" },
                new("underwear.bra_riot_red", "Plain Bra (Red)", WearLayer.Underwear, 0.02f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "underwear.bra_riot" },
                new("underwear.stockings_riot_blackcat", "Long Stockings (BlackCat)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.stockings_riot" },
                new("underwear.stockings_riot_clear", "Long Stockings (Clear)", WearLayer.Underwear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "underwear.stockings_riot" },

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
                new("clothing.top_anarchy_bra_redpurp", "Anarchy Top (Bra RedPurp)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_anarchy" },
                new("clothing.top_anarchy_red", "Anarchy Top (Red)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.top_anarchy" },
                new("clothing.corset_anarchy_red", "Laced Corset (Red)", WearLayer.Wear, 0.10f, 0.08f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.corset_anarchy" },
                new("clothing.gloves_strap_anarchy_brn", "Long Gloves (Brn)", WearLayer.Wear, 0.02f, 0.02f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.gloves_strap_anarchy" },
                new("clothing.gloves_strap_anarchy_redpurp", "Long Gloves (RedPurp)", WearLayer.Wear, 0.02f, 0.02f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.gloves_strap_anarchy" },
                new("clothing.pants_anarchy_black", "Torn Trousers (Black)", WearLayer.Wear, 0.15f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.pants_anarchy" },
                new("clothing.skirt_anarchy_red", "Pleated Skirt (Red)", WearLayer.Wear, 0.06f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.skirt_anarchy" },
                new("clothing.collar_studded_anarchy_black", "Strap Collar (Black)", WearLayer.Wear, 0.01f, 0.06f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.collar_studded_anarchy" },
                new("clothing.gloves_long_anarchy_black", "Strap Gloves (Black)", WearLayer.Wear, 0.05f, 0.03f, 0.00f, dress, 0, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.gloves_long_anarchy" },
                new("clothing.collar_anarchy_brn", "Studded Collar (Brn)", WearLayer.Wear, 0.01f, 0.00f, 0.00f, dress, 0, BodyPart.Torso) { PrototypeId = "clothing.collar_anarchy" },
                new("clothing.blouse_waist_riot", "Blouse Tied at the Waist", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.blouse_riot", "Work Blouse", WearLayer.Wear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR),
                new("clothing.cap_riot", "Pinned Cap", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head),
                new("clothing.shorts_riot", "Cut-off Shorts", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.shirt_riot", "Buttoned Shirt", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso),
                new("clothing.pants_ranger", "Ranger Trousers", WearLayer.Wear, 0.17f, 0.04f, 0.00f, dress, 3, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.vest_ranger", "Padded Vest", WearLayer.Wear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso),
                new("clothing.blouse_waist_riot_denim", "Blouse Tied at the Waist (Denim)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.blouse_waist_riot" },
                new("clothing.blouse_waist_riot_green", "Blouse Tied at the Waist (Green)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.blouse_waist_riot" },
                new("clothing.blouse_waist_riot_squaresclear", "Blouse Tied at the Waist (SquaresClear)", WearLayer.Wear, 0.04f, 0.00f, 0.00f, dress, 0, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.blouse_waist_riot" },
                new("clothing.blouse_riot_denim", "Work Blouse (Denim)", WearLayer.Wear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.blouse_riot" },
                new("clothing.blouse_riot_green", "Work Blouse (Green)", WearLayer.Wear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.blouse_riot" },
                new("clothing.blouse_riot_squaresclear", "Work Blouse (SquaresClear)", WearLayer.Wear, 0.10f, 0.00f, 0.00f, dress, 1, BodyPart.Torso, BodyPart.ArmL, BodyPart.ArmR) { PrototypeId = "clothing.blouse_riot" },
                new("clothing.cap_riot_camel", "Pinned Cap (Camel)", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_riot" },
                new("clothing.cap_riot_denim", "Pinned Cap (Denim)", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_riot" },
                new("clothing.cap_riot_red", "Pinned Cap (Red)", WearLayer.Wear, 0.05f, 0.01f, 0.00f, dress, 0, BodyPart.Head) { PrototypeId = "clothing.cap_riot" },
                new("clothing.shorts_riot_brown", "Cut-off Shorts (Brown)", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.shorts_riot" },
                new("clothing.shorts_riot_denimgrey", "Cut-off Shorts (DenimGrey)", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.shorts_riot" },
                new("clothing.shorts_riot_green", "Cut-off Shorts (Green)", WearLayer.Wear, 0.06f, 0.01f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.shorts_riot" },
                new("clothing.shirt_riot_greydark", "Buttoned Shirt (GreyDark)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.shirt_riot" },
                new("clothing.shirt_riot_pink", "Buttoned Shirt (Pink)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.shirt_riot" },
                new("clothing.shirt_riot_wine", "Buttoned Shirt (Wine)", WearLayer.Wear, 0.09f, 0.00f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.shirt_riot" },
                new("clothing.pants_ranger_default", "Ranger Trousers (Default)", WearLayer.Wear, 0.17f, 0.04f, 0.00f, dress, 3, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.pants_ranger" },
                new("clothing.vest_ranger_camo", "Padded Vest (Camo)", WearLayer.Wear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.vest_ranger" },
                new("clothing.vest_ranger_green", "Padded Vest (Green)", WearLayer.Wear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.vest_ranger" },
                new("clothing.vest_ranger_packs", "Padded Vest (Packs)", WearLayer.Wear, 0.14f, 0.11f, 0.00f, dress, 1, BodyPart.Torso) { PrototypeId = "clothing.vest_ranger" },

                // --- Outerwear: the top layer — jackets, boots, armor. ----------
                // §52.8 tool holster: GEAR, not clothing — no warmth, no thermal
                // pull, 0 pockets. Its 3 TYPED weapon slots (HolsterCatalog) are
                // the whole point; the token 0.05 armor is the buckled leather
                // strap itself, and gives her a reason to want it on.
                new("clothing.hipbelt_anarchy", "Hip Straps", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("clothing.belt_anarchy", "Studded Belt", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis),
                new("clothing.boots_anarchy", "Buckled Thigh Boots", WearLayer.Outerwear, 0.14f, 0.13f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.hipbelt_anarchy_black", "Hip Straps (Black)", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 1, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.hipbelt_anarchy" },
                new("clothing.belt_anarchy_brn", "Studded Belt (Brn)", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis) { PrototypeId = "clothing.belt_anarchy" },
                new("clothing.boots_anarchy_purple", "Buckled Boots (Purple)", WearLayer.Outerwear, 0.14f, 0.13f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_anarchy" },
                new("clothing.boots_anarchy_red", "Buckled Boots (Red)", WearLayer.Outerwear, 0.14f, 0.13f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_anarchy" },
                new("gear.legrig_ranger", "Thigh Rig", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR),
                new("gear.chestpack_ranger", "Chest Pack", WearLayer.Outerwear, 0.00f, 0.01f, 0.00f, dress, 2, BodyPart.Torso),
                new("gear.backpack_riot", "Canvas Backpack", WearLayer.Outerwear, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso),
                new("clothing.boots_riot", "Laced Work Boots", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("clothing.boots_ranger", "Ranger Boots", WearLayer.Outerwear, 0.15f, 0.14f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR),
                new("gear.beltpouch_ranger", "Belt Pouches", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 3, BodyPart.Pelvis),
                new("clothing.jacket_ranger", "Ranger Jacket", WearLayer.Outerwear, 0.30f, 0.06f, 0.00f, dress, 3, BodyPart.Torso),
                new("gear.legrig_ranger_default", "Thigh Rig (Default)", WearLayer.Outerwear, 0.00f, 0.02f, 0.00f, dress, 2, BodyPart.Pelvis, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "gear.legrig_ranger" },
                new("gear.backpack_riot_denim", "Canvas Backpack (Denim)", WearLayer.Outerwear, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso) { PrototypeId = "gear.backpack_riot" },
                new("gear.backpack_riot_grey", "Canvas Backpack (Grey)", WearLayer.Outerwear, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso) { PrototypeId = "gear.backpack_riot" },
                new("gear.backpack_riot_leather", "Canvas Backpack (Leather)", WearLayer.Outerwear, 0.02f, 0.00f, 0.00f, dress, 8, BodyPart.Torso) { PrototypeId = "gear.backpack_riot" },
                new("clothing.boots_riot_clear", "Laced Work Boots (Clear)", WearLayer.Outerwear, 0.13f, 0.11f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_riot" },
                new("clothing.boots_ranger_default", "Ranger Boots (Default)", WearLayer.Outerwear, 0.15f, 0.14f, 0.00f, dress, 0, BodyPart.LegL, BodyPart.LegR) { PrototypeId = "clothing.boots_ranger" },
                new("clothing.jacket_ranger_green", "Ranger Jacket (Green)", WearLayer.Outerwear, 0.30f, 0.06f, 0.00f, dress, 3, BodyPart.Torso) { PrototypeId = "clothing.jacket_ranger" },

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
