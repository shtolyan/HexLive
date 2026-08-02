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

        private static Dictionary<string, GarmentDefinition> Index
        {
            get
            {
                if (_byId != null)
                {
                    return _byId;
                }

                _byId = new Dictionary<string, GarmentDefinition>();
                var catalog = Resources.Load<GarmentCatalog>(GarmentCatalog.ResourcePath);
                if (catalog != null && catalog.garments != null)
                {
                    foreach (var g in catalog.garments)
                    {
                        if (g != null && !string.IsNullOrEmpty(g.id))
                        {
                            _byId[g.id] = g;
                        }
                    }
                }

                return _byId;
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

        /// <summary>Drop the cache — the catalog changed under us (editor only).</summary>
        public static void Forget()
        {
            _byId = null;
        }
    }
}
