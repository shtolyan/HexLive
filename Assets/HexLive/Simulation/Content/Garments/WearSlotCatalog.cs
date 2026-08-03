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
            ["clothing.belt_anarchy"] = new[] { WearSlot.Pelvis },
            ["clothing.belt_anarchy_brn"] = new[] { WearSlot.Pelvis },
            ["clothing.blouse_anarchy"] = new[] { WearSlot.Chest, WearSlot.ShoulderR, WearSlot.ShoulderL, WearSlot.ForearmR, WearSlot.ForearmL },
            ["clothing.boots_anarchy"] = new[] { WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_anarchy_purple"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.boots_anarchy_red"] = new[] { WearSlot.ShinR, WearSlot.ShinL, WearSlot.FootR, WearSlot.FootL },
            ["clothing.cap_anarchy"] = new[] { WearSlot.Head },
            ["clothing.cap_anarchy_black"] = new[] { WearSlot.Head },
            ["clothing.cap_anarchy_brown"] = new[] { WearSlot.Head },
            ["clothing.cap_anarchy_purple"] = new[] { WearSlot.Head },
            ["clothing.collar_anarchy"] = new[] { WearSlot.Neck },
            ["clothing.collar_anarchy_brn"] = new[] { WearSlot.Neck },
            ["clothing.collar_studded_anarchy"] = new[] { WearSlot.Neck },
            ["clothing.collar_studded_anarchy_black"] = new[] { WearSlot.Neck },
            ["clothing.corset_anarchy"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.corset_anarchy_red"] = new[] { WearSlot.Chest, WearSlot.Belly },
            ["clothing.cuffs_anarchy"] = new[] { WearSlot.WristR, WearSlot.WristL },
            ["clothing.gloves_long_anarchy"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_long_anarchy_black"] = new[] { WearSlot.ForearmR, WearSlot.ForearmL, WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_strap_anarchy"] = new[] { WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_strap_anarchy_brn"] = new[] { WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.gloves_strap_anarchy_redpurp"] = new[] { WearSlot.WristR, WearSlot.WristL, WearSlot.HandR, WearSlot.HandL },
            ["clothing.hipbelt_anarchy"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.hipbelt_anarchy_black"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.pants_anarchy"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.pants_anarchy_black"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL, WearSlot.ShinR, WearSlot.ShinL },
            ["clothing.skirt_anarchy"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.skirt_anarchy_red"] = new[] { WearSlot.Pelvis, WearSlot.ThighR, WearSlot.ThighL },
            ["clothing.top_anarchy"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_bra_purple"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_bra_red"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_bra_redpurp"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_red"] = new[] { WearSlot.Chest },
            ["clothing.top_anarchy_white"] = new[] { WearSlot.Chest },
            ["underwear.thong_anarchy"] = new[] { WearSlot.Pelvis },
            ["underwear.thong_anarchy_purple"] = new[] { WearSlot.Pelvis },
            ["underwear.thong_anarchy_red"] = new[] { WearSlot.Pelvis },
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
