#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Content;
using UnityEditor;
using UnityEngine;
// UnityEditor also declares a BodyPart enum (avatar mapping) — pin ours.
using BodyPart = HexLive.Simulation.Content.BodyPart;

namespace HexLive.UnityPresentation.Wearing.Garments
{
    /// <summary>
    /// Spec §42: materializes the wardrobe. Reads the built-in table from
    /// <see cref="GarmentLibrary.Defaults"/>, writes one <see cref="GarmentDefinition"/>
    /// asset per garment (organized into Underwear/Wear/Outerwear folders), and
    /// (re)builds the <see cref="GarmentCatalog"/> in Resources that collects
    /// them all. Unity assigns the GUIDs/.meta, so this is the correct way to
    /// spawn the assets (they can't be hand-authored ahead of import).
    ///
    /// "Rebuild" is safe to re-run: it creates any missing assets and refreshes
    /// the catalog list WITHOUT touching values you've already tuned. Use "Reset
    /// Values" only when you want to stamp the code defaults back over the assets.
    /// </summary>
    public static class GarmentCatalogBuilder
    {
        private const string GarmentsRoot =
            "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";
        private const string CatalogPath =
            "Assets/HexLiveContent/RuntimeSource/GarmentCatalog.asset";

        [MenuItem("HexLive/Garments/Rebuild Catalog From Defaults")]
        public static void Rebuild()
        {
            BuildInternal(overwriteExisting: false);
        }

        [MenuItem("HexLive/Garments/Reset Values From Defaults")]
        public static void ResetValues()
        {
            if (!EditorUtility.DisplayDialog(
                    "Reset garment values",
                    "Перезаписать параметры ВСЕХ garment-ассетов значениями из кода " +
                    "(GarmentLibrary.Defaults)? Ручная правка будет потеряна.",
                    "Reset", "Cancel"))
            {
                return;
            }

            BuildInternal(overwriteExisting: true);
        }

        [MenuItem("HexLive/Garments/Classify Storage Categories")]
        public static void ClassifyStorageCategories()
        {
            var changed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:GarmentDefinition", new[] { GarmentsRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
                if (definition == null) continue;

                var category = GarmentCategoryRules.Classify(
                    definition.id, definition.displayName, definition.layer,
                    definition.covers, definition.capacity);
                if (definition.category == category) continue;
                definition.category = category;
                EditorUtility.SetDirty(definition);
                changed++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Garment storage categories classified: {changed} assets updated.");
        }

        private static void BuildInternal(bool overwriteExisting)
        {
            EnsureFolder(GarmentsRoot);

            var all = new List<GarmentDefinition>();
            var created = 0;
            var updated = 0;

            foreach (var p in GarmentLibrary.Defaults)
            {
                var layerFolder = GarmentsRoot + "/" + p.Layer;
                EnsureFolder(layerFolder);

                var assetPath = layerFolder + "/" + ItemInfo.Slug(p.Id) + ".asset";
                var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(assetPath);

                if (def == null)
                {
                    def = ScriptableObject.CreateInstance<GarmentDefinition>();
                    Apply(def, p);
                    AssetDatabase.CreateAsset(def, assetPath);
                    created++;
                }
                else if (overwriteExisting)
                {
                    Apply(def, p);
                    EditorUtility.SetDirty(def);
                    updated++;
                }

                all.Add(def);
            }

            EnsureFolder("Assets/Resources");
            EnsureFolder("Assets/HexLiveContent/RuntimeSource");

            var catalog = AssetDatabase.LoadAssetAtPath<GarmentCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<GarmentCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            catalog.garments = all;
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"GarmentCatalog rebuilt: {all.Count} garments " +
                $"({created} created, {updated} overwritten). Asset: {CatalogPath}");
        }

        private static void Apply(GarmentDefinition def, GarmentParams p)
        {
            def.id = p.Id;
            def.displayName = p.DisplayName;
            def.layer = p.Layer;
            def.category = p.Category;
            def.covers = new List<BodyPart>(p.Covers);
            def.warmth = p.Warmth;
            def.armor = p.Armor;
            def.thermalDelta = p.ThermalDelta;
            def.dressDurationTicks = p.DressDurationTicks;
        }

        // Create every missing folder along an "Assets/..." path.
        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path).Replace("\\", "/");
            var leaf = Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
