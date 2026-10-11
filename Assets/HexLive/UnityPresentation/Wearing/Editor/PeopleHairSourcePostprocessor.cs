#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Editor
{
    // §169: restore source influences that the native FBX importer truncates.
    // Only explicitly exported hair with a matching, hash-pinned sidecar opts in.
    public sealed class PeopleHairSourcePostprocessor : AssetPostprocessor
    {
        public override uint GetVersion() => 1;
        private void OnPostprocessModel(GameObject model)
        {
            if (!assetPath.StartsWith(PeopleAssetPreparation.Root + "/Source/Models/", StringComparison.Ordinal)) return;
            string bindingsPath = Path.ChangeExtension(assetPath, ".hair-bindings.json");
            if (!File.Exists(bindingsPath)) return;
            context.DependsOnSourceAsset(bindingsPath);
            var source = JObject.Parse(File.ReadAllText(bindingsPath));
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(assetPath))
                if (!string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""),
                    (string)source["fbxSha256"], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(assetPath + ": stale hair binding sidecar");
            Restore(model, source);
        }

        public static void Restore(GameObject model, JObject source)
        {
            if ((int?)source["schemaVersion"] != 1) throw new InvalidOperationException("Unknown hair binding schema");
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 1 || renderers[0].name != (string)source["model"])
                throw new InvalidOperationException("Hair binding model mismatch");
            var renderer = renderers[0]; var mesh = renderer.sharedMesh;
            var positions = source["positions"].Select(p => new Vector3((float)p[0], (float)p[1], (float)p[2])).ToArray();
            var uvs = source["uv"].Select(v => v.Select(p => new Vector2((float)p[0], (float)p[1])).ToArray()).ToArray();
            var authored = source["weights"].Select(v => v.ToDictionary(p => (string)p[0], p => (float)p[1])).ToArray();
            if (positions.Length != uvs.Length || positions.Length != authored.Length)
                throw new InvalidOperationException("Hair binding arrays differ in length");
            foreach (var weights in authored)
                if (weights.Count == 0 || weights.Count > 4 || weights.Values.Any(w => !float.IsFinite(w) || w < 0) ||
                    Mathf.Abs(weights.Values.Sum() - 1) > .0001f)
                    throw new InvalidOperationException("Invalid authored hair weights");
            var cells = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < positions.Length; i++)
            {
                var key = Key(positions[i]);
                if (!cells.TryGetValue(key, out var indices)) cells[key] = indices = new List<int>();
                indices.Add(i);
            }
            var bones = renderer.bones.ToList(); var bindposes = mesh.bindposes.ToList();
            var hierarchy = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            foreach (string name in authored.SelectMany(w => w.Keys).Distinct())
                if (!bones.Any(b => b.name == name))
                {
                    if (!hierarchy.TryGetValue(name, out var bone)) throw new InvalidOperationException("Missing hair bone: " + name);
                    bones.Add(bone); bindposes.Add(bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix);
                }
            var boneIndices = bones.Select((b, i) => (b.name, i)).ToDictionary(p => p.name, p => p.i);
            var vertices = mesh.vertices; var importedUv = mesh.uv;
            var oldCounts = mesh.GetBonesPerVertex(); var oldWeights = mesh.GetAllBoneWeights();
            var counts = new byte[vertices.Length]; var result = new List<BoneWeight1>(); int offset = 0;
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                var position = renderer.transform.TransformPoint(vertices[vertex]); var cell = Key(position);
                var native = new Dictionary<string, float>();
                for (int w = 0; w < oldCounts[vertex]; w++)
                { var value = oldWeights[offset++]; native[renderer.bones[value.boneIndex].name] = value.weight; }
                int match = -1; float best = float.PositiveInfinity;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    if (!cells.TryGetValue(cell + new Vector3Int(x, y, z), out var candidates)) continue;
                    foreach (int index in candidates)
                    {
                        float distance = Vector3.Distance(position, positions[index]);
                        if (distance > .00002f || !uvs[index].Any(uv => Vector2.Distance(uv, importedUv[vertex]) < .00002f)) continue;
                        // Duplicate seam vertices can share position/UV but have
                        // different bindings. Prefer the native weight signature.
                        float error = native.Keys.Union(authored[index].Keys).Sum(b =>
                            Mathf.Abs((native.TryGetValue(b, out var a) ? a : 0) - (authored[index].TryGetValue(b, out var e) ? e : 0)));
                        float score = error + distance;
                        if (score < best) { best = score; match = index; }
                    }
                }
                if (match < 0) throw new InvalidOperationException(mesh.name + ": no authored binding for vertex " + vertex);
                var weights = authored[match].OrderByDescending(p => p.Value).ToArray();
                counts[vertex] = (byte)weights.Length;
                result.AddRange(weights.Select(p => new BoneWeight1 { boneIndex = boneIndices[p.Key], weight = p.Value }));
            }
            renderer.bones = bones.ToArray(); mesh.bindposes = bindposes.ToArray();
            using (var c = new NativeArray<byte>(counts, Allocator.Temp))
            using (var w = new NativeArray<BoneWeight1>(result.ToArray(), Allocator.Temp)) mesh.SetBoneWeights(c, w);
        }
        private static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / .0001f), Mathf.FloorToInt(p.y / .0001f), Mathf.FloorToInt(p.z / .0001f));
    }
}
#endif
