#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HexLive.UnityPresentation.Environment;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HexLive.Editor
{
    /// <summary>§120: exercise every paid unit through the real reveal code.</summary>
    public static class ConstructionStageBuildGate
    {
        public const string ManifestPath = "Tools/construction_stage_manifest.json";

        public static string ValidateContent(string type, string id)
        {
            if (type != "object" && type != "building") return null;
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            var definition = manifest.definitions.FirstOrDefault(item => item.id == id);
            if (definition == null) return null;
            var issues = new SortedSet<string>(StringComparer.Ordinal);
            ValidateDefinition(definition, issues);
            if (issues.Count != 0)
                throw new UnityEditor.Build.BuildFailedException(string.Join("\n", issues));
            return Path.ChangeExtension(definition.export, ".construction.json").Replace('\\', '/');
        }

        public static HashSet<string> Validate(ISet<string> violations)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            foreach (var definition in manifest.definitions)
            {
                ids.Add(definition.id);
                try { ValidateDefinition(definition, violations); }
                catch (Exception error) { violations.Add(definition.id + ": " + error.Message); }
            }
            return ids;
        }

        [MenuItem("HexLive/Content/Validate Construction Units")]
        public static void ValidateAll()
        {
            var issues = new SortedSet<string>(StringComparer.Ordinal);
            var ids = Validate(issues);
            if (issues.Count > 0)
                throw new UnityEditor.Build.BuildFailedException(string.Join("\n", issues));
            Debug.Log($"[ConstructionStageBuildGate] PASS: {ids.Count} models; every resource increment, sockets and receipts.");
        }

        private static void ValidateDefinition(Definition definition, ISet<string> issues)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(definition.export);
            if (prefab == null) throw new InvalidOperationException("Missing native FBX " + definition.export);
            var receiptPath = Path.ChangeExtension(definition.export, ".construction.json");
            if (!File.Exists(receiptPath)) throw new InvalidOperationException("Missing export receipt " + receiptPath);
            var receipt = JsonUtility.FromJson<Receipt>(File.ReadAllText(receiptPath));
            if (receipt.version != 1 || receipt.definitionId != definition.id)
                throw new InvalidOperationException("Wrong construction receipt identity/version");
            using (var sha = SHA256.Create())
            using (var source = File.OpenRead(definition.export))
            {
                var digest = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").ToLowerInvariant();
                if (digest != receipt.modelSha256)
                    throw new InvalidOperationException("FBX changed outside the approved stage export; receipt is stale");
            }
            var architecture = definition.id.StartsWith("architecture.", StringComparison.Ordinal);
            var previewScene = EditorSceneManager.NewPreviewScene();
            var wrapper = new GameObject("Construction gate " + definition.id)
                { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(wrapper, previewScene);
            try
            {
                var model = Object.Instantiate(prefab, wrapper.transform, false);
                var nodes = model.GetComponentsInChildren<Transform>(true);
                // Unity may replace the single FBX root's name with its file
                // basename (bed_basic_final_native rather than bed.basic).
                var owner = nodes.FirstOrDefault(t => t.name == definition.id) ?? model.transform;
                foreach (var required in definition.requiredNodes ?? Array.Empty<string>())
                    if (!nodes.Any(t => t.name == required)) issues.Add(definition.id + ": missing " + required);
                foreach (var template in nodes.Where(t => t.name == "HangerTemplate"))
                    template.gameObject.SetActive(false);
                var assembly = architecture ? null : wrapper.AddComponent<BedAssembly>();
                var units = new List<(Transform Node, string Resource)>();
                foreach (var stage in definition.stages)
                {
                    var root = nodes.Single(t => t.name.StartsWith($"BuildStage_{stage.index}__", StringComparison.Ordinal));
                    var children = new List<Transform>();
                    foreach (Transform child in root)
                    {
                        if (child.name.StartsWith("HL_Door_Pivot", StringComparison.Ordinal))
                        {
                            foreach (Transform part in child)
                                if (part.name.StartsWith("unit_", StringComparison.Ordinal)) children.Add(part);
                        }
                        else if (child.name.StartsWith("unit_", StringComparison.Ordinal)) children.Add(child);
                    }
                    children.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                    if (children.Count != stage.units)
                        issues.Add($"{definition.id}: stage {stage.index}: {children.Count} units != bill {stage.units}");
                    units.AddRange(children.Select(t => (t, stage.material)));
                }
                if (receipt.actions.Length != units.Count)
                    throw new InvalidOperationException("Receipt count differs from FBX units");
                for (var i = 0; i < units.Count; i++)
                {
                    var action = receipt.actions[i];
                    var relative = action.nodePath.Substring(definition.id.Length+1);
                    if (action.index != i+1 || action.count != 1 || action.resource != units[i].Resource ||
                        owner.Find(relative) != units[i].Node)
                        issues.Add($"{definition.id}: receipt action {i+1} does not match actual FBX unit");
                }
                var delivered = new Dictionary<string, int>();
                int Count(string material) => delivered.TryGetValue(material, out var value) ? value : 0;
                for (var step = 0; step <= units.Count; step++)
                {
                    if (step > 0)
                    {
                        var material = units[step-1].Resource;
                        delivered[material] = Count(material)+1;
                    }
                    if (architecture)
                        BlueprintArchitectureFactory.ApplyStageProgress(wrapper, Count("resource.stick"),
                            Count("resource.board")+Count("resource.palm_leaf"), Count("resource.rope"));
                    else assembly.Apply(Count("resource.log"), Count("resource.stick"), Count("resource.rope"),
                        Count("resource.palm_leaf"), Count("resource.stone"), Count("resource.board"));
                    for (var i = 0; i < units.Count; i++)
                    {
                        var unit = units[i].Node;
                        if (unit.gameObject.activeInHierarchy != (i < step))
                            issues.Add($"{definition.id}: step {step}: wrong visibility of {unit.name}");
                        if (unit.GetComponentsInChildren<Renderer>(true).Length == 0)
                            issues.Add($"{definition.id}: resource unit {unit.name} has no geometry");
                    }
                    if (step == 0 && wrapper.GetComponentsInChildren<Renderer>(false).Any(t => t.enabled))
                        issues.Add(definition.id + ": empty site contains floating/free renderers");
                }
                // Six legal yaws must not change an attachment's wrapper-local pose.
                var attachments = nodes.Where(t => t.name.StartsWith("ClothingSlot_", StringComparison.Ordinal) ||
                    t.name.StartsWith("DryingSlot_", StringComparison.Ordinal)).ToArray();
                var offsets = attachments.Select(t => wrapper.transform.InverseTransformPoint(t.position)).ToArray();
                for (var yaw = 0; yaw < 6; yaw++)
                {
                    wrapper.transform.rotation = Quaternion.Euler(0, yaw*60, 0);
                    for (var i = 0; i < attachments.Length; i++)
                        if (Vector3.Distance(wrapper.transform.InverseTransformPoint(attachments[i].position), offsets[i]) > .00001f)
                            issues.Add(definition.id + ": attachment drift at yaw " + yaw);
                }
            }
            finally
            {
                Object.DestroyImmediate(wrapper);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        [Serializable] private sealed class Manifest { public Definition[] definitions; }
        [Serializable] private sealed class Definition
        {
            public string id;
            public string export;
            public string[] requiredNodes;
            public Stage[] stages;
        }
        [Serializable] private sealed class Stage { public int index; public string material; public int units; }
        [Serializable] private sealed class Receipt
        {
            public int version;
            public string definitionId;
            public string modelSha256;
            public Action[] actions;
        }
        [Serializable] private sealed class Action
        {
            public int index;
            public int count;
            public string resource;
            public string nodePath;
        }
    }
}
#endif
