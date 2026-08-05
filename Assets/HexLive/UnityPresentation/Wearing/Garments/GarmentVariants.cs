using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{
    /// <summary>
    /// Spec §31B.4E: which art an item wears, and how it is painted.
    /// </summary>
    /// <remarks>
    /// A garment used to be one item, one geometry, one prefab. Polka-dot and
    /// starred knickers were therefore two of everything — two prefabs, two art
    /// folders, two icons, two library rows, two slot rows, four I2 terms —
    /// over meshes that were already the same asset. Measured on the shipped
    /// wardrobe: 6 prototypes carrying 22 items between them.
    ///
    /// A VARIANT keeps the prototype's geometry and changes its materials. This
    /// class is the whole of the lookup: item id -> art id, item id -> the
    /// materials to paint it with. Nothing else in the wearing code needs to
    /// know that variants exist.
    ///
    /// Materials are a PRESENTATION concern and stay on this side of the fence:
    /// the simulation carries only `GarmentParams.PrototypeId`, because whether
    /// the cloth is spotted or starred cannot change what the cold does.
    /// </remarks>
    public static class GarmentVariants
    {
        private static Dictionary<string, GarmentDefinition> _byId;
        private static Dictionary<string, List<GarmentDefinition>> _byArt;

        private static Dictionary<string, GarmentDefinition> Index
        {
            get
            {
                Build();
                return _byId;
            }
        }

        // Both indexes are filled in ONE pass so Forget() can never leave half
        // the lookup warm and half of it stale.
        private static void Build()
        {
            if (_byId != null)
            {
                return;
            }

            _byId = new Dictionary<string, GarmentDefinition>();
            _byArt = new Dictionary<string, List<GarmentDefinition>>();
            var catalog = Resources.Load<GarmentCatalog>(GarmentCatalog.ResourcePath);
            if (catalog == null || catalog.garments == null)
            {
                return;
            }

            foreach (var g in catalog.garments)
            {
                if (g == null || string.IsNullOrEmpty(g.id))
                {
                    continue;
                }

                _byId[g.id] = g;

                if (!_byArt.TryGetValue(g.ArtId, out var family))
                {
                    family = new List<GarmentDefinition>();
                    _byArt[g.ArtId] = family;
                }

                // The prototype leads its own family: a chooser that opens on
                // the original reads as "this is the default colour", and the
                // catalog's order between variants is otherwise arbitrary.
                if (g.id == g.ArtId)
                {
                    family.Insert(0, g);
                }
                else
                {
                    family.Add(g);
                }
            }
        }

        /// <summary>Art folder this item loads from — its prototype, or itself.</summary>
        public static string ArtIdOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return itemId;
            }

            // An unknown id is its own art: the wardrobe must keep working for
            // anything the catalog has not been told about yet (dev scenes load
            // prefabs by path), rather than resolving to nothing.
            return Index.TryGetValue(itemId, out var def) ? def.ArtId : itemId;
        }

        /// <summary>Materials for this item, or null to keep the prototype's.</summary>
        public static Material[] MaterialsOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || !Index.TryGetValue(itemId, out var def))
            {
                return null;
            }

            return def.variantMaterials != null && def.variantMaterials.Length > 0
                ? def.variantMaterials
                : null;
        }

        /// <summary>
        /// Every item painted on one prototype's art, the prototype first.
        /// </summary>
        /// <remarks>
        /// The inverse of <see cref="ArtIdOf"/> — the wardrobe browser needs it
        /// to offer "the same knickers, other colours" without walking the
        /// catalog itself. An art id nobody claims answers with an empty list,
        /// which is the honest answer for the 88 garments that have no variants
        /// and the shape a caller must handle anyway.
        /// </remarks>
        public static IReadOnlyList<GarmentDefinition> VariantsOf(string artId)
        {
            if (string.IsNullOrEmpty(artId))
            {
                return System.Array.Empty<GarmentDefinition>();
            }

            Build();
            return _byArt.TryGetValue(artId, out var family)
                ? family
                : System.Array.Empty<GarmentDefinition>();
        }

        /// <summary>Drop the cache — the catalog changed under us (editor only).</summary>
        public static void Forget()
        {
            _byId = null;
            _byArt = null;
        }
    }
}
