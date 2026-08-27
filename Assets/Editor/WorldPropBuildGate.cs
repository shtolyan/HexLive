#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using HexLive.UnityPresentation.Environment;

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
                var path = "Assets/HexLiveContent/RuntimeSource/Objects/" + entry.native;
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
                if (entry.requireTextures && !renderers
                        .SelectMany(renderer => renderer.sharedMaterials)
                        .Where(material => material != null)
                        .Any(material => material.mainTexture != null))
                    violations.Add($"{entry.id}: native materials have no texture");
            }

            // Bug #60: mesh/material presence is not enough for staged props.
            // Exercise the runtime assembly itself: an empty site must render
            // nothing, every delivered resource must reveal more geometry, and
            // the complete bill must reveal every renderer. This catches FBX
            // hierarchy drift that the former dependency-only gate missed.
            foreach (var entry in manifest.entries ?? Array.Empty<Entry>())
            {
                // JsonUtility materialises a default nested object even when
                // stagedBill is absent from JSON. Only a positive bill marks
                // an object as a staged assembly; otherwise ordinary props
                // were incorrectly exercised through the bed fallback.
                if (HasStagedBill(entry.stagedBill))
                    ValidateStagedAssembly(entry, violations);
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
                Debug.Log("[WorldPropBuildGate] Resources are Player-safe and staged assemblies grow piece by piece.");
                return;
            }

            throw new BuildFailedException(
                "World props failed Player-safety or staged-assembly validation. " +
                "Bake native FBX mirrors with Tools/bake_world_prop_fbx.py and fix the runtime mapping:\n" +
                string.Join("\n", violations.Select(v => "  - " + v)));
        }

        private static void ValidateStagedAssembly(Entry entry, ISet<string> violations)
        {
            BedAssembly.EditorAssemblyPrefabResolver = LoadAssemblySource;
            BedAssembly.EditorObjectPrefabResolver = LoadObjectSource;
            try
            {
                ValidateStagedAssemblyWithAuthoringAssets(entry, violations);
            }
            finally
            {
                BedAssembly.EditorAssemblyPrefabResolver = null;
                BedAssembly.EditorObjectPrefabResolver = null;
            }
        }

        private static void ValidateStagedAssemblyWithAuthoringAssets(
            Entry entry, ISet<string> violations)
        {
            var bill = entry.stagedBill;
            GameObject empty = null;
            try
            {
                empty = BedAssembly.BuildPartial(entry.id, 0, 0, 0, 0, 0);
                if (empty == null)
                {
                    violations.Add($"{entry.id}: runtime staged assembly could not be created");
                    return;
                }

                var emptyActive = ActiveRendererCount(empty);
                if (emptyActive != 0)
                    violations.Add($"{entry.id}: empty staged assembly renders {emptyActive} piece(s)");
            }
            finally
            {
                if (empty != null) UnityEngine.Object.DestroyImmediate(empty);
            }

            ValidateStageChannel(entry.id, "logs", bill.logs,
                count => BedAssembly.BuildPartial(entry.id, count, 0, 0, 0, 0), violations);
            ValidateStageChannel(entry.id, "sticks", bill.sticks,
                count => BedAssembly.BuildPartial(entry.id, bill.logs, count, 0, 0, 0), violations);
            ValidateStageChannel(entry.id, "rope", bill.rope,
                count => BedAssembly.BuildPartial(
                    entry.id, bill.logs, bill.sticks, count, 0, 0), violations);
            ValidateStageChannel(entry.id, "leaves", bill.leaves,
                count => BedAssembly.BuildPartial(
                    entry.id, bill.logs, bill.sticks, bill.rope, count, 0), violations);
            ValidateStageChannel(entry.id, "stones", bill.stones,
                count => BedAssembly.BuildPartial(
                    entry.id, bill.logs, bill.sticks, bill.rope, bill.leaves, count), violations);

            GameObject complete = null;
            try
            {
                complete = BedAssembly.BuildPartial(entry.id, bill.logs, bill.sticks,
                    bill.rope, bill.leaves, bill.stones);
                if (complete == null)
                {
                    violations.Add($"{entry.id}: complete staged assembly could not be created");
                    return;
                }

                var active = ActiveRendererCount(complete);
                var total = complete.GetComponentsInChildren<Renderer>(true).Length;
                if (active != total || total < entry.minRenderers)
                    violations.Add($"{entry.id}: complete staged assembly has {active}/{total} active " +
                                   $"renderers (minimum {entry.minRenderers})");
            }
            finally
            {
                if (complete != null) UnityEngine.Object.DestroyImmediate(complete);
            }
        }

        private static GameObject LoadAssemblySource(string formerResourcePath)
        {
            const string prefix = "HexLive/Objects/";
            var name = formerResourcePath.StartsWith(prefix, StringComparison.Ordinal)
                ? formerResourcePath.Substring(prefix.Length)
                : formerResourcePath;
            return AssetDatabase.LoadAssetAtPath<GameObject>(
                $"Assets/HexLiveContent/RuntimeSource/Objects/{name}.fbx");
        }

        private static GameObject LoadObjectSource(string id)
        {
            var name = WorldPropResources.NativeName(id);
            return AssetDatabase.LoadAssetAtPath<GameObject>(
                $"Assets/HexLiveContent/RuntimeSource/Objects/{name}.fbx");
        }

        private static void ValidateStageChannel(
            string id, string material, int expected,
            Func<int, GameObject> build, ISet<string> violations)
        {
            var previous = 0;
            GameObject baseline = null;
            try
            {
                baseline = build(0);
                if (baseline == null)
                {
                    violations.Add($"{id}: {material} baseline could not be created");
                    return;
                }

                // A sequential stage starts on top of the fully completed
                // prerequisite stages. Compare against that prefix, not zero.
                previous = ActiveRendererCount(baseline);
            }
            finally
            {
                if (baseline != null) UnityEngine.Object.DestroyImmediate(baseline);
            }

            for (var delivered = 1; delivered <= expected; delivered++)
            {
                GameObject assembly = null;
                try
                {
                    assembly = build(delivered);
                    if (assembly == null)
                    {
                        violations.Add($"{id}: {material} step {delivered} could not be created");
                        return;
                    }

                    var current = ActiveRendererCount(assembly);
                    if (current <= previous)
                    {
                        violations.Add($"{id}: delivered {material} {delivered}/{expected} " +
                                       $"does not reveal a new piece ({previous} -> {current})");
                        return;
                    }
                    previous = current;
                }
                finally
                {
                    if (assembly != null) UnityEngine.Object.DestroyImmediate(assembly);
                }
            }
        }

        private static int ActiveRendererCount(GameObject root) =>
            root.GetComponentsInChildren<Renderer>(false).Count(renderer => renderer.enabled);

        private static bool HasStagedBill(StageBill bill) =>
            bill != null && (bill.logs > 0 || bill.sticks > 0 || bill.rope > 0 ||
                             bill.leaves > 0 || bill.stones > 0);

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
            public bool requireTextures;
            public string alpha;
            public StageBill stagedBill;
        }
        [Serializable] private sealed class StageBill
        {
            public int logs;
            public int sticks;
            public int rope;
            public int leaves;
            public int stones;
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
