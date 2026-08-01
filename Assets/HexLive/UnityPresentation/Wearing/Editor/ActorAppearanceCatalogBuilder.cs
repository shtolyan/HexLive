#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// §74: (re)builds <see cref="ActorAppearanceCatalog"/> — the one asset that
    /// makes the hairstyle library reachable at runtime and in a build.
    ///
    /// The discriminator is the same one WardrobeTest has used since the 13-hair
    /// drop: everything under <c>Assets/ImportedActors/Wear</c> that carries a
    /// <see cref="Wear"/> component with NO slots is a hairstyle (garments live
    /// in <c>Resources/HexLive/Wear</c> and always claim slots). Anything else in
    /// that tree — the male genital prop, stray material prefabs — has no Wear
    /// component and is skipped.
    ///
    /// Safe to re-run: the list is rebuilt from scratch every time, and the
    /// hair prefabs themselves (including hand-tuned WearConfig fit rows and
    /// materials) are only referenced, never written.
    /// </summary>
    public static class ActorAppearanceCatalogBuilder
    {
        private const string HairRoot = "Assets/ImportedActors/Wear";
        private const string CatalogPath =
            "Assets/Resources/HexLive/ActorAppearanceCatalog.asset";

        [MenuItem("HexLive/Actors/Rebuild Appearance Catalog")]
        public static void Rebuild()
        {
            var found = new List<Wear>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { HairRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var wear = prefab != null ? prefab.GetComponent<Wear>() : null;
                if (wear == null || wear.Slots.Count > 0)
                {
                    continue;
                }

                found.Add(wear);
            }

            found.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));

            var directory = Path.GetDirectoryName(CatalogPath);
            if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            var catalog = AssetDatabase.LoadAssetAtPath<ActorAppearanceCatalog>(CatalogPath);
            var created = catalog == null;
            if (created)
            {
                catalog = ScriptableObject.CreateInstance<ActorAppearanceCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            catalog.hairstyles = found;
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var names = new List<string>(found.Count);
            foreach (var hair in found)
            {
                names.Add(hair.name);
            }

            Debug.Log(
                $"[§74] ActorAppearanceCatalog {(created ? "created" : "rebuilt")} " +
                $"with {found.Count} hairstyles: {string.Join(", ", names)}\n" +
                "Keep ColonistAppearance.Hairstyles (simulation side) in sync with this list — " +
                "an id the sim rolls but the catalog lacks silently leaves the girl with her prefab hair.",
                catalog);
        }
    }
}
#endif
