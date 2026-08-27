using System;
using System.Collections.Generic;
using System.Linq;
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
        private static readonly Dictionary<string, GarmentDefinition> ById = new();
        private static readonly Dictionary<string, List<GarmentDefinition>> ByArt = new();
        private static readonly Dictionary<string,
            HexLive.UnityPresentation.Content.ContentAssetHandle<GarmentDefinition>> Handles = new();
        private static readonly Dictionary<string, List<Action>> Waiters = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Forget();

        /// <summary>
        /// Loads this item's own metadata entry. The GarmentDefinition and its
        /// variant materials live in the same owner bundle as the prefab/icon;
        /// there is no shared GarmentCatalog payload.
        /// </summary>
        public static void PrewarmAsync(string itemId, Action completed = null)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                completed?.Invoke();
                return;
            }
            if (ById.ContainsKey(itemId))
            {
                completed?.Invoke();
                return;
            }
            if (Waiters.TryGetValue(itemId, out var pending))
            {
                if (completed != null)
                {
                    pending.Add(completed);
                }
                return;
            }

            pending = new List<Action>();
            if (completed != null)
            {
                pending.Add(completed);
            }
            Waiters[itemId] = pending;
            HexLive.UnityPresentation.Content.ContentAssetService.Instance
                .LoadAsset<GarmentDefinition>("wear", itemId, "metadata", loaded =>
                {
                    if (loaded?.Asset != null)
                    {
                        Handles[itemId] = loaded;
                        Add(loaded.Asset);
                    }
                    else
                    {
                        loaded?.Dispose();
                    }

                    var callbacks = Waiters[itemId].ToArray();
                    Waiters.Remove(itemId);
                    foreach (var callback in callbacks)
                    {
                        callback();
                    }
                });
        }

        private static void Add(GarmentDefinition garment)
        {
            if (garment == null || string.IsNullOrEmpty(garment.id))
            {
                return;
            }
            ById[garment.id] = garment;
            if (!ByArt.TryGetValue(garment.ArtId, out var family))
            {
                family = new List<GarmentDefinition>();
                ByArt[garment.ArtId] = family;
            }
            family.RemoveAll(value => value == null || value.id == garment.id);
            family.Add(garment);
            family.Sort((left, right) =>
            {
                var leftPrototype = left.id == left.ArtId;
                var rightPrototype = right.id == right.ArtId;
                if (leftPrototype != rightPrototype)
                {
                    return leftPrototype ? -1 : 1;
                }
                return string.CompareOrdinal(left.id, right.id);
            });
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
            if (ById.TryGetValue(itemId, out var definition))
            {
                return definition.ArtId;
            }

            PrewarmAsync(itemId);
            return HexLive.UnityPresentation.Content.ContentAssetService.Instance.TryGetRecord(
                       "wear", itemId, out var record)
                ? record.metadata?.Value<string>("artId") ?? itemId
                : itemId;
        }

        /// <summary>Materials for this item, or null to keep the prototype's.</summary>
        public static Material[] MaterialsOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || !ById.TryGetValue(itemId, out var def))
            {
                PrewarmAsync(itemId);
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

            foreach (var record in HexLive.UnityPresentation.Content.ContentAssetService.Instance
                         .Records("wear")
                         .Where(record => string.Equals(
                             record.metadata?.Value<string>("artId") ?? record.id,
                             artId, StringComparison.Ordinal)))
            {
                PrewarmAsync(record.id);
            }
            return ByArt.TryGetValue(artId, out var family)
                ? family
                : System.Array.Empty<GarmentDefinition>();
        }

        /// <summary>Резидентность (ContentResidency): отпустить метаданные
        /// одной вещи — их хэндл держит ТОТ ЖЕ wear-бандл, что и префаб, и без
        /// этого выгрузка гардероба не освобождает ни байта. False — загрузка
        /// в полёте, вытеснение повторят позже.</summary>
        public static bool Evict(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return true;
            }
            if (Waiters.ContainsKey(itemId))
            {
                return false;
            }

            if (Handles.TryGetValue(itemId, out var handle))
            {
                handle?.Dispose();
                Handles.Remove(itemId);
            }

            if (ById.TryGetValue(itemId, out var garment))
            {
                ById.Remove(itemId);
                if (garment != null && ByArt.TryGetValue(garment.ArtId, out var family))
                {
                    family.RemoveAll(value => value == null || value.id == itemId);
                    if (family.Count == 0)
                    {
                        ByArt.Remove(garment.ArtId);
                    }
                }
            }
            return true;
        }

        /// <summary>Drop the cache — the catalog changed under us (editor only).</summary>
        public static void Forget()
        {
            foreach (var handle in Handles.Values)
            {
                handle?.Dispose();
            }
            Handles.Clear();
            ById.Clear();
            ByArt.Clear();
            Waiters.Clear();
        }
    }
}
