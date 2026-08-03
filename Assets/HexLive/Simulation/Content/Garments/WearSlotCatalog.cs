using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// Where a garment physically SITS on the body — the fine-grained wear slot
    /// model, mirrored from the prefab (`Wear.slots`, the presentation's
    /// authority for what displaces what: `BodyBones.Equip` keeps one garment
    /// per (layer, slot)).
    ///
    /// <b>Why this exists.</b> The sim's <see cref="ObjectDefinition.Covers"/> is
    /// a list of 7 coarse <see cref="BodyPart"/> zones and means "what this
    /// garment PROTECTS / covers" — armor absorption, warmth, sun, wound stains.
    /// It was long doubled up as an occupancy test ("same layer + overlapping
    /// part ⇒ take the old piece off"), which is far too blunt: a thigh holster,
    /// stockings and boots all report `LegL+LegR`, so donning boots stripped the
    /// holster, panties stripped tights and a necklace stripped a bra — 61 such
    /// false pairs across the wardrobe. Slots tell them apart (`ThighR` vs
    /// `ShinR/L` vs `FootR/L`).
    ///
    /// <b>Direction of truth (was written backwards in the spec).</b> The PREFAB
    /// is authoritative; this table mirrors it. Never "fix" a prefab by
    /// coarsening its slots to match `Covers` — that destroys the finer data.
    /// After editing a prefab's slots, update the matching row here.
    ///
    /// A garment with no row falls back to the old `Covers` test, so
    /// unauthored art keeps its previous behaviour instead of stacking freely.
    /// </summary>
    public static class WearSlotCatalog
    {
        // Mirrors HexLive.UnityPresentation.Wearing.VisualWearSlot 1:1 (order
        // matters only for readability; matching NAMES are what the sweep uses).
        private static readonly Dictionary<string, WearSlot[]> Slots = new()
        {
            // §72: снаряжение чужака. Слоты взяты ИЗ ПРЕФАБОВ (§52.9 —
            // вытеснение решают слоты, а не Covers), поэтому портупея и
            // поддоспешник оба Chest и вытесняют друг друга по слоям, а
            // набедренники и наколенники живут раздельно.
            ["TonnyFlash"] = new[] { WearSlot.Chest },
            ["FAO Harness Male"] = new[] { WearSlot.Chest },
            ["FCO Belt Male"] = new[] { WearSlot.Belly },
            ["FCO Boots Male"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["FCO Gloves Male"] = new[] { WearSlot.HandR, WearSlot.HandL },
            ["FCO Knee Straps Male"] = new[] { WearSlot.ShinR, WearSlot.ShinL },
            ["FCO Legs Straps Male"] = new[] { WearSlot.ThighR, WearSlot.ThighL },
            ["FCO Pants Male"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["FCO Waist Strappy Male"] = new[] { WearSlot.Pelvis },
            // ["Boots 20496"] — prefab authors NO slots; falls back to Covers.
            ["clothing.ankleboots_amy"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.armwraps_primal"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.armwraps_primal_sleeve1"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.armwraps_primal_sleeve2"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.armwraps_primal_sleeve3"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.babydoll_tod"] = new[] { WearSlot.Chest },
            ["clothing.belt_anarchy"] = new[] { WearSlot.Pelvis },
            ["clothing.blouse_anarchy"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.blouse_nerd"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.blouse_riot"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.blouse_riot_denim"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.blouse_riot_green"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.blouse_riot_squaresclear"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.blouse_waist_riot"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.blouse_waist_riot_denim"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.blouse_waist_riot_green"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.blouse_waist_riot_squaresclear"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.boots_anarchy"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_anarchy_purple"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_anarchy_red"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_ranger"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_riot"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.bowtie_nerd"] = new[] { WearSlot.Neck },
            ["clothing.cap_anarchy"] = new[] { WearSlot.Head },
            ["clothing.cap_anarchy_black"] = new[] { WearSlot.Head },
            ["clothing.cap_anarchy_brown"] = new[] { WearSlot.Head },
            ["clothing.cap_anarchy_purple"] = new[] { WearSlot.Head },
            ["clothing.cap_riot"] = new[] { WearSlot.Head },
            ["clothing.cap_riot_camel"] = new[] { WearSlot.Head },
            ["clothing.cap_riot_denim"] = new[] { WearSlot.Head },
            ["clothing.cap_riot_red"] = new[] { WearSlot.Head },
            ["clothing.collar_anarchy"] = new[] { WearSlot.Neck },
            ["clothing.collar_anarchy_brn"] = new[] { WearSlot.Neck },
            ["clothing.collar_studded_anarchy"] = new[] { WearSlot.Neck },
            ["clothing.collar_studded_anarchy_black"] = new[] { WearSlot.Neck },
            ["clothing.corset_anarchy"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.corset_anarchy_red"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.croptop_fit"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_blackblue"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_blacklime"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_blackpink"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_blackwhite"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_blue"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_lime"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_pink"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_1_white"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_2_black"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_black"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_black2"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_blackgreen"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_blackmagenta"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_blackorange"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_magenta"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_orange"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_turquoise"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_3_white"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_4_batik1"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_4_batik2"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_4_batik3"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_4_batik4"] = new[] { WearSlot.Chest },
            ["clothing.croptop_fit_4_batik5"] = new[] { WearSlot.Chest },
            ["clothing.cuffs_anarchy"] = new[] { WearSlot.WristR, WearSlot.WristL },
            ["clothing.glasses_nerd"] = new[] { WearSlot.Head },
            ["clothing.gloves_fit"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.gloves_fit_black2"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.gloves_fit_blue"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.gloves_fit_blue2"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.gloves_lace_tod"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.gloves_long_anarchy"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_strap_anarchy"] = new[] { WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_strap_anarchy_brn"] = new[] { WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.greaves_tod"] = new[] { WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.headband_primal"] = new[] { WearSlot.Head },
            ["clothing.headband_primal_2"] = new[] { WearSlot.Head },
            ["clothing.headband_primal_3"] = new[] { WearSlot.Head },
            ["clothing.headband_primal_4"] = new[] { WearSlot.Head },
            ["clothing.hipbelt_anarchy"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.jacket_ranger"] = new[] { WearSlot.Neck, WearSlot.Chest, WearSlot.Belly },
            ["clothing.jacket_tek"] = new[] { WearSlot.Neck, WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.jackettied_tek"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.pants_anarchy"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.pants_anarchy_black"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.pants_ranger"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.pendant_amy"] = new[] { WearSlot.Neck },
            ["clothing.pumps_flair"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.shirt_amy"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.shirt_amy_07"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.shirt_amy_08"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.shirt_riot"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.shirt_riot_greydark"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.shirt_riot_pink"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.shirt_riot_wine"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.shoes_tod"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.shorts_fit"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_1_blackblue"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_1_blacklime"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_1_blackpink"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_1_blackwhite"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_2_black"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_black"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_blackgreen"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_blackmagenta"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_blackorange"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_magenta"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_orange"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_turquoise"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_3_white"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_4_batik1"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_4_batik2"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_4_batik3"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_4_batik4"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_4_batik5"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_fit_4_camouflage"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_riot"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.shorts_riot_brown"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.shorts_riot_denimgrey"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.shorts_riot_green"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_amy"] = new[] { WearSlot.Belly, WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_amy_04"] = new[] { WearSlot.Belly, WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_amy_05"] = new[] { WearSlot.Belly, WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_anarchy"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_anarchy_red"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_flair"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_flair_skirt_02"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_flair_skirt_03"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_primal"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.skirt_primal_1"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.skirt_primal_2"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.skirt_primal_3"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.sneakers_nerd"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.sneakers_nerd_nc_sneakers_pink"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.sneakers_nerd_nc_sneakers_purple"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.suspenders_nerd"] = new[] { WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["clothing.suspenders_nerd_nc_suspenders_checks"] = new[] { WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["clothing.sweater_flair"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["clothing.sweater_flair_sweater_02"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["clothing.sweater_flair_sweater_03"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["clothing.thighboots_amy"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.top_anarchy"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_bra_purple"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_bra_red"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_red"] = new[] { WearSlot.Chest },
            ["clothing.top_primal"] = new[] { WearSlot.Chest },
            ["clothing.top_primal_1"] = new[] { WearSlot.Chest },
            ["clothing.top_primal_2"] = new[] { WearSlot.Chest },
            ["clothing.top_primal_3"] = new[] { WearSlot.Chest },
            ["clothing.top_tod"] = new[] { WearSlot.Neck, WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.tutu_nerd"] = new[] { WearSlot.Belly, WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.tutu_nerd_nc_skirt_def"] = new[] { WearSlot.Belly, WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.vest_ranger"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.vest_ranger_green"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.vest_ranger_packs"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.wrapboots_primal"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.wrapboots_primal_1"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.wrapboots_primal_2"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.wrapboots_primal_3"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.yogapants_tek"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_01_black_mesh"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_02_black"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_03_black_red"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_04_black_orange"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_05_black_pink"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_06_black_blue"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_07_black_plain"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.yogapants_tek_yoga_08_white_plain"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["gear.backpack_riot"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["gear.backpack_riot_denim"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["gear.backpack_riot_grey"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["gear.backpack_riot_leather"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["gear.beltpouch_ranger"] = new[] { WearSlot.Pelvis },
            ["underwear.bra_riot"] = new[] { WearSlot.Chest },
            ["underwear.bra_riot_blue"] = new[] { WearSlot.Chest },
            ["underwear.bra_riot_pink"] = new[] { WearSlot.Chest },
            ["underwear.bra_riot_red"] = new[] { WearSlot.Chest },
            ["underwear.briefs_flair"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.briefs_flair_panty_02"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.briefs_flair_panty_03"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.briefs_primal"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.briefs_primal_panty1"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.briefs_primal_panty2"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.briefs_primal_panty3"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.kneesocks_nerd"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.kneesocks_nerd_nc_sock_left_argyle"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.kneesocks_nerd_nc_sock_left_polka_dots"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.kneesocks_nerd_nc_sock_right_hearts"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.kneesocks_nerd_nc_sock_right_polka_dots"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.panty_tod"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.socks_fit"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.socks_fit_black2"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.socks_fit_blue"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.socks_fit_blue2"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.sportsbra_tek"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_01_apply_black_bottom_trim"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_01_apply_black_trim"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_01_apply_white_bottom_trim"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_01_apply_white_trim"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_02_black"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_03_orange_black"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_04_red_black"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_05_black_pink"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_06_blue_black"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_07_blue_black_orange"] = new[] { WearSlot.Chest },
            ["underwear.sportsbra_tek_bra_08_white"] = new[] { WearSlot.Chest },
            ["underwear.stockings_riot"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.stockings_riot_clear"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["underwear.stockings_tod"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["underwear.thong_anarchy"] = new[] { WearSlot.Pelvis },
            ["underwear.thong_anarchy_purple"] = new[] { WearSlot.Pelvis },
        };

        /// <summary>
        /// Kill-switch (§52.9). False reverts every displacement site to the old
        /// coarse `Covers` test — the pre-slot behaviour — without touching the
        /// call sites. Kept for A/B soaks and as a one-flip escape hatch if the
        /// finer model ever misbehaves in play.
        /// </summary>
        public static bool Enabled = true;

        public static bool Has(string definitionId) =>
            Enabled && definitionId != null && Slots.ContainsKey(definitionId);

        public static IReadOnlyList<WearSlot> For(string definitionId) =>
            definitionId != null && Slots.TryGetValue(definitionId, out var s)
                ? s
                : System.Array.Empty<WearSlot>();

        /// <summary>
        /// Do these two garments occupy an overlapping spot on the body? This is
        /// the ONE occupancy predicate every displacement site must use (it does
        /// NOT test the wear layer — callers own that). Prefers the fine slot
        /// data; falls back to the coarse protection zones only when either side
        /// has no authored slots.
        /// </summary>
        public static bool SameSpot(ObjectDefinition a, ObjectDefinition b)
        {
            if (a is null || b is null)
            {
                return false;
            }

            if (Has(a.Id) && Has(b.Id))
            {
                foreach (var slot in Slots[a.Id])
                {
                    foreach (var other in Slots[b.Id])
                    {
                        if (slot == other)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            foreach (var part in a.Covers)
            {
                if (b.Covers.Contains(part))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // The body spots a garment can occupy — mirrors the prefab-side
    // VisualWearSlot enum name-for-name (spec §52.9).
    public enum WearSlot
    {
        Head,
        EarR,
        EarL,
        Neck,
        Chest,
        ShoulderR,
        ShoulderL,
        ForearmR,
        ForearmL,
        Belly,
        WristR,
        WristL,
        Pelvis,
        HandR,
        HandL,
        ThighR,
        ThighL,
        ShinR,
        ShinL,
        FootR,
        FootL
    }
}
