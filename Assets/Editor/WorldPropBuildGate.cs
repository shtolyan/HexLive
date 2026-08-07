#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HexLive.Editor
{
    /// <summary>Prevents Resources from silently pulling glTF importer sub-assets into Player.</summary>
    public sealed class WorldPropBuildGate : IPreprocessBuildWithReport
    {
        public int callbackOrder => -900;

        public void OnPreprocessBuild(BuildReport report) => Validate();

        [MenuItem("HexLive/Content/Validate Player-Safe Resources")]
        public static void Validate()
        {
            var violations = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { "Assets/Resources" }))
            {
                var root = AssetDatabase.GUIDToAssetPath(guid);
                if (IsGltf(root)) violations.Add(root);
                foreach (var dependency in AssetDatabase.GetDependencies(root, true))
                {
                    if (IsGltf(dependency)) violations.Add($"{root} -> {dependency}");
                }
            }

            var manifest = JsonUtility.FromJson<Manifest>(
                File.ReadAllText("Tools/world_prop_manifest.json"));
            foreach (var entry in manifest.entries ?? Array.Empty<Entry>())
            {
                if (entry.mode != "native" && entry.mode != "existing-native") continue;
                var path = "Assets/Resources/HexLive/Objects/" + entry.native;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    violations.Add($"{entry.id}: missing native GameObject {path}");
                    continue;
                }
                var renderers = prefab.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length < entry.minRenderers)
                    violations.Add($"{entry.id}: renderers {renderers.Length} < {entry.minRenderers}");
                var materialNames = new HashSet<string>(renderers
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null)
                    .Select(material => material.name), StringComparer.OrdinalIgnoreCase);
                foreach (var required in entry.materialSlots ?? Array.Empty<string>())
                    if (!materialNames.Any(name => name.Contains(required, StringComparison.OrdinalIgnoreCase)))
                        violations.Add($"{entry.id}: missing material slot {required}");

                foreach (var material in renderers.SelectMany(renderer => renderer.sharedMaterials)
                             .Where(material => material != null).Distinct())
                {
                    var transparent = material.renderQueue >= 3000 ||
                        (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f);
                    var alphaClip = material.IsKeywordEnabled("_ALPHATEST_ON") ||
                        (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f);
                    if ((entry.alpha == "opaque" || entry.alpha == "opaque-geometry") && transparent)
                        violations.Add($"{entry.id}: material {material.name} violates {entry.alpha}");
                    if (entry.alpha == "clip" && !alphaClip)
                        violations.Add($"{entry.id}: material {material.name} must enable alpha clip");
                    if (entry.alpha == "blend" && !transparent)
                        violations.Add($"{entry.id}: material {material.name} must be transparent");
                    if ((entry.alpha == "clip" || entry.alpha == "blend") && material.mainTexture == null)
                        violations.Add($"{entry.id}: alpha material {material.name} has no texture");
                }
            }

            foreach (var group in manifest.addressableNativeGroups ?? Array.Empty<NativeGroup>())
            {
                var files = Directory.Exists(group.root)
                    ? Directory.GetFiles(group.root, group.pattern, SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();
                if (files.Length != group.expectedCount)
                    violations.Add($"{group.id}: expected {group.expectedCount} native assets, got {files.Length}");
                foreach (var file in files)
                    foreach (var dependency in AssetDatabase.GetDependencies(file, true))
                        if (IsGltf(dependency)) violations.Add($"{file} -> {dependency}");
            }

            if (violations.Count == 0)
            {
                Debug.Log("[WorldPropBuildGate] Resources are free of glTF ScriptedImporter dependencies.");
                return;
            }

            throw new BuildFailedException(
                "Resources contain Player-unsafe glTF dependencies. Bake native FBX mirrors with " +
                "Tools/bake_world_prop_fbx.py and repoint runtime assets:\n" +
                string.Join("\n", violations.Select(v => "  - " + v)));
        }

        private static bool IsGltf(string path) =>
            path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        [Serializable] private sealed class Manifest
        {
            public Entry[] entries;
            public NativeGroup[] addressableNativeGroups;
        }
        [Serializable] private sealed class Entry
        {
            public string id;
            public string native;
            public string mode;
            public int minRenderers;
            public string[] materialSlots;
            public string alpha;
        }
        [Serializable] private sealed class NativeGroup
        {
            public string id;
            public string root;
            public string pattern;
            public int expectedCount;
        }
    }
}
#endif
