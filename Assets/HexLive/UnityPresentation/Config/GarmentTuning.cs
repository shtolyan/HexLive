using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Spec §42: bridges the saved <see cref="GarmentCatalog"/> asset and the
    /// engine-free <see cref="GarmentLibrary"/> the simulation reads — the exact
    /// mirror of <see cref="HexTuning"/> for wearables. Called once at startup
    /// (PrototypeRuntimeBootstrap) BEFORE the world is built, so the content
    /// catalog materializes the tuned garment table.
    ///
    /// If the asset is missing or empty, nothing is applied and the game runs on
    /// GarmentLibrary's built-in defaults — the wardrobe can never go blank.
    /// </summary>
    public static class GarmentTuning
    {
        public static void LoadAndApply()
        {
            var catalog = Resources.Load<GarmentCatalog>(GarmentCatalog.ResourcePath);
            if (catalog == null)
            {
                // No asset yet (e.g. before the editor tool has been run) —
                // keep the built-in defaults.
                return;
            }

            GarmentLibrary.Override(BackfillCapacity(catalog.ToParams()));
        }

        // Spec §52: the slot-capacity field is newer than the saved assets, so a
        // catalog rebuilt before this change serializes capacity = 0 for every
        // garment — which would leave the shipped game with hand-slots only.
        // When an asset reports 0, restore the code default by id. This is
        // lossless: real accessories default to 0 too, so nothing that should
        // carry pockets is silently zeroed. (Re-run "HexLive → Garments → Reset
        // Values From Defaults" to bake the capacities into the assets for good.)
        private static System.Collections.Generic.List<GarmentParams> BackfillCapacity(
            System.Collections.Generic.List<GarmentParams> fromAsset)
        {
            var defaults = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var d in GarmentLibrary.Defaults)
            {
                defaults[d.Id] = d.Capacity;
            }

            var result = new System.Collections.Generic.List<GarmentParams>(fromAsset.Count);
            foreach (var g in fromAsset)
            {
                var cap = g.Capacity;
                if (cap == 0 && defaults.TryGetValue(g.Id, out var def))
                {
                    cap = def;
                }

                result.Add(new GarmentParams(
                    g.Id, g.DisplayName, g.Layer, g.Warmth, g.Armor, g.ThermalDelta,
                    g.DressDurationTicks, cap, g.Covers.ToArray()));
            }

            return result;
        }
    }
}
