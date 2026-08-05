#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Sway for the hairstyles that ship with NO strand bones.
//
//  BodyHairPhysicsSetup springs the five hairstyles whose DAZ product came with
//  its own bones (Tail/Bangs/Ponytail…). The other eight are skinned to
//  head/neck/chest/shoulder bones alone — dForce hair, meant to be simulated in
//  DAZ — so BoneSpring has nothing to move.
//
//  Four of those eight are skull caps and stay untouched on purpose: measured
//  from the head bone, AdellHair/Bob3Hair/Neu09Hair/TootsieRollHair hang only
//  5-8 cm, i.e. they end around the ears. Nothing dangles, nothing can sway.
//
//  The four LONG ones (17-26 cm below the head bone, down onto the shoulders)
//  get the bones they were missing: a short chain is added under the hair's own
//  `head`, the lower part of the mesh is re-skinned onto it with a smooth
//  top-to-bottom blend, and then it is the ordinary BoneSpring recipe — the
//  same one Marta's ponytail uses. No MeshCloth: the meshes run 33-120 K verts
//  and MagicaCloth would have to build a proxy for every one of them at
//  DRESSING time, where a bone chain costs nothing.
//
//  Re-skinning REWRITES the .mesh asset (bones, bindposes, weights). It is
//  idempotent — a hairstyle that already carries a sway bone is skipped — but
//  re-running HairExtractor rebuilds the mesh from the FBX and drops the bones,
//  so run this again after a re-extract.
//
//  Menu: HexLive/Physics/Setup Hair Sway (boneless hairstyles)
// ---------------------------------------------------------------------------
public static class HairSwaySetup
{
    const string HairRoot = "Assets/ImportedActors/Hair";
    const string BonePrefix = "HairSway";

    // Where the moving zone starts, measured DOWN from the hair's head bone.
    // 6 cm is roughly ear level: above it the hair is skull cap and must not
    // move, below it it hangs. The blend is smoothstepped from here, so the
    // first centimetres barely move whatever the number.
    const float SwayTopBelowHead = 0.06f;

    // Three joints down the length. Same reasoning as OnyxHair's Tail1-3: the
    // deflection budget is split across joints, so the tip travels a natural
    // arc instead of the whole fall pivoting as one slab.
    const int SwayJoints = 3;

    struct LongHair
    {
        public string Hair;
        public float Strength;   // how much of the tip's weight the chain takes
    }

    static readonly LongHair[] LongHairs =
    {
        new LongHair { Hair = "Hair07",           Strength = 1.0f },
        new LongHair { Hair = "JelikaHair_32434", Strength = 1.0f },
        new LongHair { Hair = "JenniferHair",     Strength = 1.0f },
        new LongHair { Hair = "LoonaHair",        Strength = 1.0f },
    };

    [MenuItem("HexLive/Physics/Setup Hair Sway (boneless hairstyles)")]
    static void SetupAll()
    {
        int done = 0, skipped = 0;
        foreach (var entry in LongHairs)
        {
            var path = $"{HairRoot}/{entry.Hair}/{entry.Hair}.prefab";
            var result = AddSwayChain(path, entry.Strength);
            if (result == null) { skipped++; continue; }
            done++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // The springs go on in a second pass: BodyHairPhysicsSetup.Setup
        // instantiates the prefab, so the new bones have to be saved first.
        foreach (var entry in LongHairs)
        {
            var path = $"{HairRoot}/{entry.Hair}/{entry.Hair}.prefab";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || asset.GetComponentInChildren<MagicaCloth2.MagicaCloth>(true) != null) continue;
            if (FindDeep(asset.transform, BonePrefix + "1") == null) continue;

            BodyHairPhysicsSetup.Setup(path, new[] { BonePrefix + "1" }, breast: false,
                                       limitAngle: 14f, restoreStiffness: 0.30f, worldInertia: 0.80f);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[HairSway] {done} причёск(и) переcкинуты на цепочку, {skipped} пропущено.");
    }

    // Re-skinning rewrites a mesh ASSET, and Unity rewrites one whether or not
    // anything changed — so the only honest check is what is inside it: the
    // bindpose count has to match the bone array, and the chain has to actually
    // carry weight. A mismatch renders as a hairstyle exploded across the map.
    [MenuItem("HexLive/Physics/Validate Hair Sway")]
    static void Validate()
    {
        foreach (var entry in LongHairs)
        {
            var path = $"{HairRoot}/{entry.Hair}/{entry.Hair}.prefab";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) { Debug.LogError($"[HairSway] нет префаба: {path}"); continue; }

            var smr = asset.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var mesh = smr != null ? smr.sharedMesh : null;
            if (mesh == null) { Debug.LogError($"[HairSway] {entry.Hair}: нет скина"); continue; }

            var chain = new HashSet<int>();
            for (var i = 0; i < smr.bones.Length; i++)
                if (smr.bones[i] != null && smr.bones[i].name.StartsWith(BonePrefix)) chain.Add(i);

            float mass = 0f;
            int touched = 0;
            foreach (var w in mesh.boneWeights)
            {
                float v = 0f;
                if (chain.Contains(w.boneIndex0)) v += w.weight0;
                if (chain.Contains(w.boneIndex1)) v += w.weight1;
                if (chain.Contains(w.boneIndex2)) v += w.weight2;
                if (chain.Contains(w.boneIndex3)) v += w.weight3;
                if (v <= 0f) continue;
                mass += v;
                touched++;
            }

            bool ok = smr.bones.Length == mesh.bindposes.Length && chain.Count == SwayJoints && touched > 0;
            var line = $"[HairSway] {entry.Hair}: кости {smr.bones.Length} / байндпозы {mesh.bindposes.Length}, " +
                       $"цепочка {chain.Count} костей, под ней {touched} вершин " +
                       $"(масса {mass:F0} = {mass / mesh.vertexCount:P1} меша), " +
                       $"пружина {(asset.GetComponentInChildren<MagicaCloth2.MagicaCloth>(true) != null ? "есть" : "НЕТ")}";
            if (ok) Debug.Log(line); else Debug.LogError(line);
        }
    }

    // Returns null when nothing was done (missing prefab, or already has bones).
    static string AddSwayChain(string path, float strength)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null) { Debug.LogError($"[HairSway] нет префаба: {path}"); return null; }
        if (FindDeep(asset.transform, BonePrefix + "1") != null)
        {
            Debug.Log($"[HairSway] {asset.name}: цепочка уже есть — пропущено (удали кости, чтобы переделать).");
            return null;
        }

        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var smr = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var head = FindDeep(root.transform, "head");
            if (smr == null || smr.sharedMesh == null || head == null)
            {
                Debug.LogError($"[HairSway] {root.name}: нет скина или головной кости");
                return null;
            }

            var mesh = smr.sharedMesh;
            if (mesh.isReadable == false)
            {
                Debug.LogError($"[HairSway] {root.name}: меш не читается");
                return null;
            }

            var toWorld = smr.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            var world = new Vector3[verts.Length];
            for (var i = 0; i < verts.Length; i++) world[i] = toWorld.MultiplyPoint3x4(verts[i]);

            float swayTop = head.position.y - SwayTopBelowHead;
            float bottom = world.Min(v => v.y);
            float span = swayTop - bottom;
            if (span < 0.05f)
            {
                Debug.LogWarning($"[HairSway] {root.name}: свисает всего {span:F3} м — качать нечего.");
                return null;
            }

            // Each joint sits in the middle of the band it drives, and at the
            // CENTRE OF MASS of that band rather than on the head axis: a
            // ponytail hangs behind the skull, and a pivot in front of the hair
            // would swing it sideways instead of along its own length.
            var joints = new Transform[SwayJoints];
            var previous = head;
            for (var j = 0; j < SwayJoints; j++)
            {
                float top = swayTop - span * j / SwayJoints;
                float low = swayTop - span * (j + 1) / SwayJoints;
                var band = world.Where(v => v.y <= top && v.y >= low).ToList();
                var centre = band.Count > 0
                    ? new Vector3(band.Average(v => v.x), (top + low) * 0.5f, band.Average(v => v.z))
                    : new Vector3(head.position.x, (top + low) * 0.5f, head.position.z);

                var go = new GameObject($"{BonePrefix}{j + 1}");
                go.transform.SetParent(previous, false);
                go.transform.position = centre;
                go.transform.rotation = head.rotation;
                joints[j] = go.transform;
                previous = go.transform;
            }

            // --- re-skin ------------------------------------------------------
            var bones = smr.bones.ToList();
            var binds = mesh.bindposes.ToList();
            int firstJoint = bones.Count;
            foreach (var joint in joints)
            {
                bones.Add(joint);
                binds.Add(joint.worldToLocalMatrix * smr.transform.localToWorldMatrix);
            }

            var weights = mesh.boneWeights;
            int touched = 0;
            for (var i = 0; i < weights.Length; i++)
            {
                float u = Mathf.Clamp01((swayTop - world[i].y) / span);
                if (u <= 0f) continue;

                float s = Mathf.SmoothStep(0f, 1f, u) * strength;
                if (s <= 0.0001f) continue;

                // Which joint drives this height, blended across the boundary.
                float p = Mathf.Clamp(u * SwayJoints, 0f, SwayJoints - 0.0001f);
                int a = Mathf.Clamp((int)p, 0, SwayJoints - 1);
                float f = p - a;

                var mix = new List<(int bone, float weight)>(6);
                var w = weights[i];
                AddInfluence(mix, w.boneIndex0, w.weight0 * (1f - s));
                AddInfluence(mix, w.boneIndex1, w.weight1 * (1f - s));
                AddInfluence(mix, w.boneIndex2, w.weight2 * (1f - s));
                AddInfluence(mix, w.boneIndex3, w.weight3 * (1f - s));
                if (a + 1 < SwayJoints)
                {
                    AddInfluence(mix, firstJoint + a, s * (1f - f));
                    AddInfluence(mix, firstJoint + a + 1, s * f);
                }
                else
                {
                    AddInfluence(mix, firstJoint + a, s);
                }

                weights[i] = TopFour(mix);
                touched++;
            }

            mesh.bindposes = binds.ToArray();
            mesh.boneWeights = weights;
            EditorUtility.SetDirty(mesh);

            smr.bones = bones.ToArray();
            smr.rootBone = smr.rootBone != null ? smr.rootBone : head;
            // Simulated vertices leave the skinned silhouette, so the renderer's
            // own bounds stop describing it — the trap OnyxHair hit first.
            smr.updateWhenOffscreen = true;

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[HairSway] {root.name}: цепочка из {SwayJoints} костей, " +
                      $"свес {span:F3} м, переcкинуто {touched} из {weights.Length} вершин.");
            return path;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static void AddInfluence(List<(int bone, float weight)> mix, int bone, float weight)
    {
        if (weight <= 0.0001f) return;
        for (var i = 0; i < mix.Count; i++)
        {
            if (mix[i].bone != bone) continue;
            mix[i] = (bone, mix[i].weight + weight);
            return;
        }
        mix.Add((bone, weight));
    }

    // Unity skins with at most four influences (and the quality setting may cut
    // it further), so the mix is trimmed HERE and renormalised — leaving five
    // would let the runtime drop one silently and unbalance the vertex.
    static BoneWeight TopFour(List<(int bone, float weight)> mix)
    {
        var top = mix.OrderByDescending(m => m.weight).Take(4).ToList();
        float total = top.Sum(m => m.weight);
        if (total <= 0f) total = 1f;

        var bw = new BoneWeight();
        for (var i = 0; i < top.Count; i++)
        {
            float w = top[i].weight / total;
            switch (i)
            {
                case 0: bw.boneIndex0 = top[i].bone; bw.weight0 = w; break;
                case 1: bw.boneIndex1 = top[i].bone; bw.weight1 = w; break;
                case 2: bw.boneIndex2 = top[i].bone; bw.weight2 = w; break;
                case 3: bw.boneIndex3 = top[i].bone; bw.weight3 = w; break;
            }
        }
        return bw;
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform c in root)
        {
            var r = FindDeep(c, name);
            if (r) return r;
        }
        return null;
    }
}
#endif
