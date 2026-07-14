using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{
    /// <summary>
    /// Spec §42: the wardrobe database — one asset that collects every
    /// <see cref="GarmentDefinition"/>. This is the single object the game loads
    /// (from Resources/HexLive) and hands to GarmentTuning, which flattens it
    /// into the engine-free GarmentLibrary before the world is built.
    ///
    /// The editor tool "HexLive → Garments → Rebuild Catalog From Defaults"
    /// creates the per-garment assets and fills this list; after that you tune
    /// each item in the inspector and the game picks it up on the next run.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Garment Catalog", fileName = "GarmentCatalog")]
    public sealed class GarmentCatalog : ScriptableObject
    {
        // Under a Resources folder so the shipping game loads it with no scene
        // reference (mirrors HexTuning.ResourcePath).
        public const string ResourcePath = "HexLive/GarmentCatalog";

        [Tooltip("Все вещи гардероба — по одному GarmentDefinition-ассету на айтем. Заполняется генератором, дальше правится вручную.")]
        public List<GarmentDefinition> garments = new();

        // Flatten the assigned assets into engine-free params (skips empty slots
        // and duplicate ids — last one wins, with a warning).
        public List<GarmentParams> ToParams()
        {
            var result = new List<GarmentParams>();
            var seen = new HashSet<string>();
            foreach (var g in garments)
            {
                if (g == null || string.IsNullOrEmpty(g.id))
                {
                    continue;
                }

                if (!seen.Add(g.id))
                {
                    Debug.LogWarning($"GarmentCatalog: duplicate garment id '{g.id}' — later entry overrides.", this);
                }

                result.Add(g.ToParams());
            }

            return result;
        }
    }
}
