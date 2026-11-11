#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMeshSimplifier;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string PeopleRoot = "Assets/HexLiveContent/People";
    const int TriangleBudget = 8000;
    const float ContactRange = .022f;
    const float Clearance = .003f;

    static JArray Vec(Vector3 v) => new JArray(v.x, v.y, v.z);
    static JObject Box(Bounds b) => new JObject { ["center"] = Vec(b.center), ["size"] = Vec(b.size) };
    static Bounds VertexBounds(Vector3[] vertices)
    {
        if (vertices.Length == 0) throw new InvalidOperationException("Empty fitted geometry");
        var bounds = new Bounds(vertices[0], Vector3.zero);
        foreach (var point in vertices)
        {
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
                throw new InvalidOperationException("Nonfinite fitted geometry");
            bounds.Encapsulate(point);
        }
        return bounds;
    }

    sealed class Fit : IDisposable
    {
        public GameObject Body, Garment;
        public SkinnedMeshRenderer Skin;
        public Transform[] Bones;
        public Matrix4x4[] BoneWorld;
        public string[] Paths;
        public Dictionary<string, Matrix4x4> RestWorld;
        public Mesh Baked;
        public Vector3[] WorldVertices, WorldNormals;
        public TriangleTree BodySurface;
        public TriangleTree[] Underlayers = Array.Empty<TriangleTree>();
        public Vector3 RadialCentre;
        public void Dispose()
        {
            if (Baked != null) Object.DestroyImmediate(Baked);
            // Construct scatters clothing bones under the private body.
            if (Body != null) Object.DestroyImmediate(Body);
            if (Garment != null) Object.DestroyImmediate(Garment);
        }
    }

    static string RelativePath(Transform root, Transform child)
    {
        var parts = new List<string>();
        for (var t = child; t != root; t = t.parent)
        {
            if (t == null) throw new InvalidOperationException("Bone escaped authoring hierarchy");
            parts.Add(t.name);
        }
        parts.Reverse(); return string.Join("/", parts);
    }

    static Mesh SourceMesh(Wear wear, ActorName actor, out float authoredScale)
    {
        var serialized = new SerializedObject(wear);
        var configs = serialized.FindProperty("configs");
        for (var i = 0; i < configs.arraySize; i++)
        {
            var config = configs.GetArrayElementAtIndex(i);
            if (config.FindPropertyRelative("actorName").intValue != (int)actor) continue;
            authoredScale = config.FindPropertyRelative("scale").floatValue;
            return config.FindPropertyRelative("mesh").objectReferenceValue as Mesh ??
                   wear.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
        }
        authoredScale = 1f;
        return wear.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
    }

    static void SetFits(Wear wear, Mesh female, Mesh male, float femaleScale, float maleScale, bool neutral)
    {
        // Capture before resizing/reordering configs, including previous-stage
        // hand tuning copied into the template. ConstructFit and output use the
        // same path, so neither rebinding nor a second pass erases either height.
        float femaleHeight = wear.GetConfigHeight(ActorName.Marta);
        float maleHeight = wear.GetConfigHeight(ActorName.Kshishtof);
        var serialized = new SerializedObject(wear);
        var configs = serialized.FindProperty("configs");
        configs.arraySize = 2;
        for (int i = 0; i < 2; i++)
        {
            var item = configs.GetArrayElementAtIndex(i);
            item.FindPropertyRelative("actorName").intValue = (int)(i == 0 ? ActorName.Marta : ActorName.Kshishtof);
            item.FindPropertyRelative("mesh").objectReferenceValue = i == 0 ? female : male;
            item.FindPropertyRelative("scale").floatValue = neutral ? 1f : i == 0 ? femaleScale : maleScale;
            item.FindPropertyRelative("heightOffset").floatValue = i == 0 ? femaleHeight : maleHeight;
            item.FindPropertyRelative("hairLodMeshes").arraySize = 0;
        }
        if (neutral)
        {
            // These authored corrections are restored on the output prefab.
            // Neutral binding prevents baking a correction and applying it twice.
            serialized.FindProperty("heel").boxedValue = default(HeelPose);
            serialized.FindProperty("headwearFit").boxedValue = default(HeadwearFit);
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void StripPlayerIncompatibleComponents(GameObject go)
    {
        foreach (var component in go.GetComponentsInChildren<Component>(true).Reverse())
        {
            if (component == null) throw new InvalidOperationException(go.name + ": missing source script");
            if (component is Transform || component is Wear || component is SkinnedMeshRenderer ||
                component is MeshFilter || component is MeshRenderer) continue;
            Object.DestroyImmediate(component);
        }
    }

    static Fit ConstructFit(GameObject template, Mesh female, Mesh male, ActorName actor)
    {
        var result = new Fit();
        try
        {
            result.Body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Actors/" + actor + ".prefab"));
            result.Body.hideFlags = HideFlags.HideAndDontSave;
            var animator = result.Body.GetComponent<Animator>(); if (animator != null) animator.enabled = false;
            var bodyBones = result.Body.GetComponent<BodyBones>(); bodyBones.Construct(actor, 649);
            result.RadialCentre = bodyBones.GetBone("pelvis").position;
            var authoredWear = template.GetComponent<Wear>();
            var underlayers = new List<SkinnedMeshRenderer>();
            foreach (var pair in new[] { (VisualWearSlot.Pelvis, "PrimalBriefs"), (VisualWearSlot.Chest, "PrimalTop") })
            {
                if (!authoredWear.Slots.Contains(pair.Item1) || authoredWear.HeedHideUnderwearSlot(pair.Item1)) continue;
                string key = "fitting-baseline-" + pair.Item2;
                var baseline = AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Wear/" + actor + "_" + pair.Item2 + ".prefab").GetComponent<Wear>();
                bodyBones.Equip(key, baseline, null);
                underlayers.AddRange(bodyBones.GetWorn(key).GetComponentsInChildren<SkinnedMeshRenderer>(true));
            }
            result.Garment = Object.Instantiate(template);
            result.Garment.hideFlags = HideFlags.HideAndDontSave;
            StripPlayerIncompatibleComponents(result.Garment);
            var wear = result.Garment.GetComponent<Wear>();
            // Bind against the actual authored scale. Baking at 1 then restoring
            // e.g. 1.04 changes each joint-relative offset after rebinding and
            // opens holes in otherwise correctly fitted boots.
            var configData = new SerializedObject(authoredWear).FindProperty("configs");
            bool HasConfig(ActorName who) => Enumerable.Range(0, configData.arraySize).Any(i => configData.GetArrayElementAtIndex(i).FindPropertyRelative("actorName").intValue == (int)who);
            var donor = authoredWear.Gender == VisualGender.Male ? ActorName.Kshishtof : ActorName.Marta;
            float femaleScale = authoredWear.GetConfigScale(HasConfig(ActorName.Marta) ? ActorName.Marta : donor);
            float maleScale = authoredWear.GetConfigScale(HasConfig(ActorName.Kshishtof) ? ActorName.Kshishtof : donor);
            SetFits(wear, female, male, femaleScale, maleScale, false);
            result.Skin = result.Garment.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var transforms = result.Garment.GetComponentsInChildren<Transform>(true)
                .ToDictionary(t => RelativePath(result.Garment.transform, t), t => t);
            result.Bones = result.Skin.bones;
            result.Paths = result.Bones.Select(b => RelativePath(result.Garment.transform, b)).ToArray();
            wear.Construct(actor, bodyBones, "legacy-preparation");
            // Fit collisions in the same heel pose used by the live actor. The
            // resulting bind matrices keep this correction from being baked twice.
            typeof(BodyBones).GetField("_heel", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(bodyBones, template.GetComponent<Wear>().Heel);
            typeof(BodyBones).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bodyBones, null);
            result.BodySurface = TriangleTree.ForRenderer(ActorBodyResolver.ResolveOrNull(result.Body.transform));
            result.Underlayers = underlayers.Select(TriangleTree.ForRenderer).ToArray();
            if (result.BodySurface.Bounds.size.y < 1.5f || result.BodySurface.Bounds.size.y > 2.2f)
                throw new InvalidOperationException(actor + ": body surface is not in world metres: " + result.BodySurface.Bounds);
            result.RestWorld = transforms.ToDictionary(p => p.Key, p => p.Value.localToWorldMatrix);
            result.BoneWorld = result.Bones.Select(b => b.localToWorldMatrix).ToArray();
            result.Baked = new Mesh(); result.Skin.BakeMesh(result.Baked, true);
            var matrix = result.Skin.transform.localToWorldMatrix;
            result.WorldVertices = result.Baked.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var normalMatrix = matrix.inverse.transpose;
            result.WorldNormals = result.Baked.normals.Select(n => normalMatrix.MultiplyVector(n).normalized).ToArray();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    static Mesh Simplify(Mesh source, string name, Matrix4x4? world = null) =>
        SimplifyWithErrorGate(source,name,world ?? Matrix4x4.identity);

    static Mesh BindMesh(Mesh source, Fit target, Vector3[] worldVertices, Vector3[] worldNormals, string name)
    {
        var mesh = Object.Instantiate(source); mesh.name = name;
        var inverse = target.Skin.transform.worldToLocalMatrix;
        mesh.vertices = worldVertices.Select(inverse.MultiplyPoint3x4).ToArray();
        var normalMatrix = target.Skin.transform.localToWorldMatrix.transpose;
        mesh.normals = worldNormals.Select(n => normalMatrix.MultiplyVector(n).normalized).ToArray();
        mesh.bindposes = target.BoneWorld.Select(m => m.inverse * target.Skin.transform.localToWorldMatrix).ToArray();
        mesh.RecalculateBounds(); mesh.RecalculateTangents();
        return mesh;
    }

    static void AddWeighted(ref Vector3 position, ref Vector3 normal, Vector3 point, Vector3 sourceNormal,
        int bone, float weight, Matrix4x4[] remap)
    {
        if (weight <= 0) return;
        if (bone < 0 || bone >= remap.Length) throw new InvalidOperationException("Invalid source bone weight");
        var transform = remap[bone];
        position += transform.MultiplyPoint3x4(point) * weight;
        normal += transform.inverse.transpose.MultiplyVector(sourceNormal) * weight;
    }

    static Mesh Retarget(Mesh source, Fit female, Fit male, out int conformed, bool matchSurface = false, bool fitToBody = true)
    {
        if (female.BoneWorld.Length != male.BoneWorld.Length || !female.Paths.SequenceEqual(male.Paths))
            throw new InvalidOperationException("Body-specific source skeletons differ");
        var remap = male.BoneWorld.Select((m, i) => m * female.BoneWorld[i].inverse).ToArray();
        var positions = new Vector3[source.vertexCount]; var normals = new Vector3[source.vertexCount];
        var weights = source.boneWeights; conformed = 0;
        if (weights.Length != source.vertexCount) throw new InvalidOperationException("Missing source skinning");
        for (int i = 0; i < positions.Length; i++)
        {
            var w = weights[i]; var original = female.WorldVertices[i]; var originalNormal = female.WorldNormals[i];
            var p = Vector3.zero; var n = Vector3.zero;
            AddWeighted(ref p, ref n, original, originalNormal, w.boneIndex0, w.weight0, remap);
            AddWeighted(ref p, ref n, original, originalNormal, w.boneIndex1, w.weight1, remap);
            AddWeighted(ref p, ref n, original, originalNormal, w.boneIndex2, w.weight2, remap);
            AddWeighted(ref p, ref n, original, originalNormal, w.boneIndex3, w.weight3, remap);
            var mapped = p;
            if (!fitToBody) { positions[i] = p; normals[i] = n.normalized; continue; }
            var from = female.BodySurface.Nearest(original);
            if (matchSurface && from.Distance <= ContactRange)
            {
                var anchor = Vector3.zero; var mappedNormal = Vector3.zero;
                AddWeighted(ref anchor, ref mappedNormal, from.Point, from.Normal, w.boneIndex0, w.weight0, remap);
                AddWeighted(ref anchor, ref mappedNormal, from.Point, from.Normal, w.boneIndex1, w.weight1, remap);
                AddWeighted(ref anchor, ref mappedNormal, from.Point, from.Normal, w.boneIndex2, w.weight2, remap);
                AddWeighted(ref anchor, ref mappedNormal, from.Point, from.Normal, w.boneIndex3, w.weight3, remap);
                var contact = male.BodySurface.Nearest(anchor, mappedNormal);
                p = contact.Point + contact.Normal * Mathf.Max(Clearance, from.Distance);
                n = Quaternion.FromToRotation(mappedNormal.normalized, contact.Normal) * n;
            }
            else
            {
                var contact = male.BodySurface.Nearest(p, n);
                float signed = Vector3.Dot(p - contact.Point, contact.Normal);
                if (contact.Distance < .04f && signed < Clearance) p += contact.Normal * (Clearance - signed);
            }
            // Open outer layers must fit around visible underwear too. Query
            // each closed underlayer independently rather than the nearest
            // triangle of a union, which can choose an inner body surface.
            foreach (var layer in male.Underlayers)
            {
                var box = layer.Bounds;
                if (p.x < box.min.x - .008f || p.x > box.max.x + .008f || p.y < box.min.y - .008f || p.y > box.max.y + .008f) continue;
                var radial = new Vector2(p.x - male.RadialCentre.x,p.z - male.RadialCentre.z);
                float radius = radial.magnitude;
                if (radius < .00001f) continue;
                float envelope = layer.RadialEnvelope(p,male.RadialCentre);
                if (envelope > 0 && radius < envelope + Clearance)
                {
                    radial *= (envelope + Clearance) / radius;
                    p.x = male.RadialCentre.x + radial.x; p.z = male.RadialCentre.z + radial.y;
                }
            }
            if ((p - mapped).sqrMagnitude > 1e-12f) conformed++;
            positions[i] = p; normals[i] = n.normalized;
        }
        return BindMesh(source, male, positions, normals, male.Body.name + "_" + source.name);
    }

    public static void Preflight()
    {
        Audit();
        var rows = new JArray();
        foreach (var id in new[] { "clothing.ankleboots_amy", "clothing.skirt_anarchy", "underwear.top_vapor" })
        {
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { SourceRoot + "/" + id })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".prefab", StringComparison.Ordinal)).ToArray();
            if (paths.Length != 1) throw new InvalidOperationException(id + ": ambiguous authoring input");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[0]);
            var wear = prefab.GetComponent<Wear>();
            var authored = Settings(wear); var input = SourceMesh(wear, ActorName.Marta, out var scale);
            Mesh simplified = null, femaleMesh = null, maleMesh = null;
            try
            {
                simplified = Simplify(input, id);
                using var female = ConstructFit(prefab, simplified, simplified, ActorName.Marta);
                using var male = ConstructFit(prefab, simplified, simplified, ActorName.Kshishtof);
                femaleMesh = BindMesh(simplified, female, female.WorldVertices, female.WorldNormals, "Marta_" + id);
                maleMesh = Retarget(simplified, female, male, out var conformed);
                if (Settings(wear).ToString() != authored.ToString()) throw new InvalidOperationException("Source settings changed");
                rows.Add(new JObject { ["id"] = id, ["sourceVertices"] = input.vertexCount,
                    ["preparedVertices"] = simplified.vertexCount, ["femaleVertices"] = femaleMesh.vertexCount,
                    ["maleVertices"] = maleMesh.vertexCount, ["conformedVertices"] = conformed,
                    ["authoredScale"] = scale, ["authoredSettingsPreserved"] = true,
                    ["femaleBounds"] = Box(femaleMesh.bounds), ["maleBounds"] = Box(maleMesh.bounds),
                    ["femaleWorldBounds"] = Box(VertexBounds(female.WorldVertices)),
                    ["maleWorldBounds"] = Box(VertexBounds(male.WorldVertices)),
                    ["femaleBodyBounds"] = Box(female.BodySurface.Bounds), ["maleBodyBounds"] = Box(male.BodySurface.Bounds),
                    ["femaleRendererScale"] = Vec(female.Skin.transform.lossyScale),
                    ["maleRendererScale"] = Vec(male.Skin.transform.lossyScale) });
            }
            finally
            {
                if (simplified != null) Object.DestroyImmediate(simplified);
                if (femaleMesh != null) Object.DestroyImmediate(femaleMesh);
                if (maleMesh != null) Object.DestroyImmediate(maleMesh);
            }
        }
        File.WriteAllText(OutputRoot + "/geometry-preflight.json", new JObject { ["passed"] = true,
            ["rows"] = rows, ["assetsChanged"] = false, ["bundlesBuilt"] = false }.ToString());
        Debug.Log("LEGACY_WARDROBE_GEOMETRY_PREFLIGHT: " + rows.Count + " unique source models, both bodies; no assets changed");
    }
}
}
#endif
