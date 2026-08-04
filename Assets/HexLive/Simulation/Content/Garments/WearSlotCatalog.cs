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
            ["Bikini Bottom"] = new[] { WearSlot.Pelvis },
            ["Bikini top"] = new[] { WearSlot.Chest },
            ["Boots"] = new[] { WearSlot.FootR, WearSlot.FootL },
            // §52.9 r2: the prefab authored NO slots, so this pair rode the coarse
            // Covers fallback and stripped stockings/tights/socks for nothing (7
            // false pairs). FootR/FootL was authored ON THE PREFAB to match its
            // two siblings above; this row mirrors it.
            ["Boots 20496"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["Boots_155064"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["CityDress"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.Pelvis },
            ["CowTop"] = new[] { WearSlot.Chest },
            ["Glove_2245"] = new[] { WearSlot.WristL, WearSlot.HandL },
            ["Gloves_17510"] = new[] { WearSlot.HandR, WearSlot.HandL },
            ["Gloves_5480"] = new[] { WearSlot.HandR, WearSlot.HandL },
            ["Gloves_8128"] = new[] { WearSlot.HandR, WearSlot.HandL },
            ["Got stock"] = new[] { WearSlot.ThighL, WearSlot.ShinL, WearSlot.FootL },
            ["NeckWarmer_1259"] = new[] { WearSlot.Neck },
            ["Necklace_2228"] = new[] { WearSlot.Neck },
            ["Over Knee G3F_18296"] = new[] { WearSlot.ShinR, WearSlot.ShinL },
            ["Pants_24055"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["Panty_11571"] = new[] { WearSlot.Pelvis },
            ["Shirt G3F_31977"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["Shorts 1389"] = new[] { WearSlot.Pelvis },
            ["Shorts Green"] = new[] { WearSlot.Pelvis },
            ["Shorts short"] = new[] { WearSlot.Pelvis },
            ["Shorts_10_14636"] = new[] { WearSlot.Pelvis },
            ["Skirt 29046"] = new[] { WearSlot.Pelvis },
            ["Skirt G3F_27980"] = new[] { WearSlot.Pelvis },
            ["Skirt_2799"] = new[] { WearSlot.Pelvis },
            ["Sleeve_19793"] = new[] { WearSlot.WristL, WearSlot.HandL },
            ["Stockings_8731"] = new[] { WearSlot.ShinR, WearSlot.ShinL },
            ["TankTop9_20034"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["Tights Old"] = new[] { WearSlot.ShinR, WearSlot.ShinL },
            ["Tights_1818"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["Top_11927"] = new[] { WearSlot.Neck, WearSlot.ShoulderL },
            ["Top_2300"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["armor.heavy"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["armor.leather"] = new[] { WearSlot.Chest },
            ["clothing.belt_stars"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.blouse_nerd"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.boots_cammy"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_classic"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.bowtie_nerd"] = new[] { WearSlot.Neck },
            ["clothing.cap_stars"] = new[] { WearSlot.Head },
            ["clothing.coat"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.dress_fur"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.dress_night"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.Pelvis },
            ["clothing.glasses_nerd"] = new[] { WearSlot.Head },
            ["clothing.gloves_classic"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_stars"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL },
            ["clothing.headdress_jaguar"] = new[] { WearSlot.Head },
            ["clothing.leather_pants"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.leggings_fitness"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.pants_stars"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.scarf_classic"] = new[] { WearSlot.Neck, WearSlot.Chest },
            ["clothing.shorts_cherry"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_classic"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_critters"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_hearts"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_olive"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_red"] = new[] { WearSlot.Pelvis },
            ["clothing.shorts_white"] = new[] { WearSlot.Pelvis },
            ["clothing.skirt_crux"] = new[] { WearSlot.Pelvis },
            ["clothing.skirt_sweetjane"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.skirt_tutu"] = new[] { WearSlot.Belly, WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.sleeves_fitness"] = new[] { WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.slipons_fads"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.sneakers_nerd"] = new[] { WearSlot.FootR, WearSlot.FootL },
            ["clothing.socks_nerd"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.stockings_spooky"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            // §52.9 r2: NerdSuspenders.prefab claims Chest+Belly, not the shoulders
            // this row guessed — so the sim let them coexist with every top while
            // the body evicted one of the two (11 of the 30 clashing pairs). The
            // prefab is the authority: it is a bib, and it replaces a top.
            ["clothing.suspenders_nerd"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.sweater_flair"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.tank_sweetjane"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.top_classic"] = new[] { WearSlot.Chest },
            ["clothing.top_fitness"] = new[] { WearSlot.Chest },
            ["clothing.top_stars"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL },
            ["clothing.top_tiedye"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.top_tropic"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.vest_stars"] = new[] { WearSlot.Neck, WearSlot.Chest },
            ["legHolster_2204"] = new[] { WearSlot.ThighR },
            ["pants_21038"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["underwear.babydoll_sweety"] = new[] { WearSlot.Chest, WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.bra_basic"] = new[] { WearSlot.Chest },
            ["underwear.bra_cherry"] = new[] { WearSlot.Chest },
            ["underwear.bra_crux"] = new[] { WearSlot.Chest },
            ["underwear.bra_dots"] = new[] { WearSlot.Chest },
            ["underwear.bra_stripe"] = new[] { WearSlot.Chest },
            ["underwear.cloth"] = new[] { WearSlot.Chest, WearSlot.Pelvis },
            ["underwear.panty_basic"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_cherry"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_crux"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_dots"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_flair"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_leo"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_stars"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_stripe"] = new[] { WearSlot.Pelvis },
            ["underwear.panty_sweety"] = new[] { WearSlot.Belly, WearSlot.Pelvis },
            ["underwear.swim_bottom"] = new[] { WearSlot.Pelvis },
            ["underwear.swim_top"] = new[] { WearSlot.Chest },
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
        /// <b>THE</b> occupancy predicate (§52.9 r2): would wearing <paramref name="a"/>
        /// take <paramref name="b"/> off? Layer AND slot, in one place, mirroring
        /// <c>BodyBones.Equip</c> ("one garment per (layer, slot)") exactly.
        /// <para>
        /// Every displacement site calls this — <c>ResolveWearConflicts</c>,
        /// <c>HasWearConflict</c>, <c>WarmthGainFromWearing</c>. The layer test used
        /// to be re-typed at each of them, which is how <c>WarmthGainFromWearing</c>
        /// drifted out of sync once already; with one predicate the sim's answer and
        /// the prefab's answer can only disagree through DATA, and that is what the
        /// headless wear-slot gate proves they never do.
        /// </para>
        /// <para>
        /// A non-wearable (no <see cref="ObjectDefinition.Layer"/>) occupies nothing.
        /// </para>
        /// </summary>
        public static bool Occupies(ObjectDefinition a, ObjectDefinition b) =>
            a is not null && b is not null &&
            a.Layer is not null && a.Layer == b.Layer &&
            SameSpot(a, b);

        /// <summary>
        /// Do these two garments overlap on the BODY, ignoring the layer? Prefers
        /// the fine slot data; falls back to the coarse protection zones only when
        /// either side has no authored slots. Callers want
        /// <see cref="Occupies"/> — this is its slot half, exposed for the gate.
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
