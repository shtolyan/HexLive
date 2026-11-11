#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
// Diagnostic partial only: compile beside the existing authoring partials.
// No assets, source mesh, materials or fit parameters are saved or modified.
public static partial class LegacyWebGLWardrobePreparation
{
    const int BootProbeSamples = 4096;
    sealed class BootProbeEquipped : IDisposable
    {
        public GameObject Body;
        public Wear Worn;
        public SkinnedMeshRenderer Skin;
        public void Dispose() { if (Body != null) Object.DestroyImmediate(Body); }
    }
    sealed class BootProbeGeometry
    {
        public Vector3[] Vertices, Normals, Samples;
        public int VertexSamples, CentroidSamples, Triangles;
        public Bounds Bounds;
    }

    static BootProbeEquipped BootProbeEquip(Wear source, string label, Material[] materials = null)
    {
        var result = new BootProbeEquipped();
        try
        {
            result.Body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Actors/Marta.prefab"));
            result.Body.hideFlags = HideFlags.HideAndDontSave;
            result.Body.GetComponent<Animator>().enabled = false;
            var body = result.Body.GetComponent<BodyBones>(); body.Construct(ActorName.Marta, 649);
            body.Equip("boot-probe-briefs", AssetDatabase.LoadAssetAtPath<GameObject>(
                PeopleRoot + "/Prefabs/Wear/Marta_PrimalBriefs.prefab").GetComponent<Wear>(), null);
            string key = "boot-probe-" + label;
            body.Equip(key, source, materials);
            result.Worn = body.GetWorn(key);
            if (result.Worn == null) throw new InvalidOperationException(label + ": real BodyBones.Equip did not equip");
            var skins = result.Worn.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skins.Length != 1) throw new InvalidOperationException(label + ": expected exactly one source garment renderer");
            result.Skin = skins[0];
            // The actual Equip refreshed _heel; do not fabricate or neutralize it.
            typeof(BodyBones).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(body, null);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    static GameObject BootProbeNativeTemplate(GameObject source, Mesh candidate)
    {
        var template = Object.Instantiate(source); template.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            StripPlayerIncompatibleComponents(template);
            var wear = template.GetComponent<Wear>();
            var data = new SerializedObject(wear); var configs = data.FindProperty("configs"); int changed = 0;
            for (int i = 0; i < configs.arraySize; i++)
            {
                var config = configs.GetArrayElementAtIndex(i);
                if (config.FindPropertyRelative("actorName").intValue != (int)ActorName.Marta) continue;
                config.FindPropertyRelative("mesh").objectReferenceValue = candidate; changed++;
            }
            if (changed != 1) throw new InvalidOperationException("Native Amy must have exactly one authored Marta fit");
            data.ApplyModifiedPropertiesWithoutUndo();
            template.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh = candidate;
            return template; // native skeleton, scale/offset/heel remain exactly authored
        }
        catch { Object.DestroyImmediate(template); throw; }
    }

    static BootProbeGeometry BootProbeBake(SkinnedMeshRenderer skin)
    {
        var mesh = new Mesh();
        try
        {
            skin.BakeMesh(mesh, true); // exactly the convention used by TriangleTree.ForRenderer / RenderWardrobe
            var matrix = skin.transform.localToWorldMatrix; var normal = matrix.inverse.transpose;
            var vertices = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var normals = mesh.normals.Select(n => normal.MultiplyVector(n).normalized).ToArray();
            var indices = mesh.triangles; int triangleCount = indices.Length / 3;
            var samples = new List<Vector3>(BootProbeSamples);
            int nv = Math.Min(vertices.Length, BootProbeSamples / 2);
            for (int i = 0; i < nv; i++) samples.Add(vertices[(int)((long)i * vertices.Length / nv)]);
            int nt = Math.Min(triangleCount, BootProbeSamples - samples.Count);
            for (int i = 0; i < nt; i++)
            {
                int at = (int)((long)i * triangleCount / nt) * 3;
                samples.Add((vertices[indices[at]] + vertices[indices[at+1]] + vertices[indices[at+2]]) / 3f);
            }
            if (samples.Count == 0) throw new InvalidOperationException("Empty diagnostic geometry");
            return new BootProbeGeometry { Vertices = vertices, Normals = normals, Samples = samples.ToArray(),
                VertexSamples = nv, CentroidSamples = nt, Triangles = triangleCount, Bounds = VertexBounds(vertices) };
        }
        finally { Object.DestroyImmediate(mesh); }
    }

    static JObject BootProbeDistances(BootProbeGeometry samples, TriangleTree target)
    {
        var errors = samples.Samples.Select(p => target.Nearest(p).Distance).OrderBy(v => v).ToArray();
        return new JObject { ["sampleCount"] = errors.Length, ["vertexSamples"] = samples.VertexSamples,
            ["triangleCentroidSamples"] = samples.CentroidSamples, ["max"] = errors[errors.Length-1],
            ["p95"] = errors[Math.Max(0,(int)Math.Ceiling(errors.Length*.95)-1)],
            ["median"] = errors[errors.Length/2], ["mean"] = errors.Average(), ["units"] = "world metres" };
    }
    static JArray BootProbeMatrix(Matrix4x4 value)
    {
        var rows = new JArray();
        for (int r = 0; r < 4; r++) rows.Add(new JArray(value[r,0],value[r,1],value[r,2],value[r,3]));
        return rows;
    }
    static JObject BootProbeBindMatrices(SkinnedMeshRenderer skin, Mesh mesh = null)
    {
        mesh = mesh != null ? mesh : skin.sharedMesh;
        var bones = skin.bones; var poses = mesh.bindposes; var weighted = new HashSet<int>();
        string weightReadError = null;
        try
        {
            foreach (var w in mesh.boneWeights)
            {
                if (w.weight0 > 1e-6f) weighted.Add(w.boneIndex0);
                if (w.weight1 > 1e-6f) weighted.Add(w.boneIndex1);
                if (w.weight2 > 1e-6f) weighted.Add(w.boneIndex2);
                if (w.weight3 > 1e-6f) weighted.Add(w.boneIndex3);
            }
        }
        catch (Exception e) { weightReadError = e.GetType().Name + ": " + e.Message; }
        float max = 0, weightedMax = 0; int worst = -1, worstElement = -1, weightedWorst = -1;
        var worstMatrix = Matrix4x4.identity; var missing = new JArray();
        for (int i = 0; i < Math.Min(bones.Length,poses.Length); i++)
        {
            if (bones[i] == null) { missing.Add(i); continue; }
            var matrix = skin.transform.worldToLocalMatrix * bones[i].localToWorldMatrix * poses[i];
            float delta = 0; int element = -1;
            for (int at = 0; at < 16; at++)
            {
                float error = Mathf.Abs(matrix[at] - Matrix4x4.identity[at]);
                if (error > delta) { delta = error; element = at; }
            }
            if (worst < 0 || delta > max) { max = delta; worst = i; worstElement = element; worstMatrix = matrix; }
            if (weighted.Contains(i) && (weightedWorst < 0 || delta > weightedMax)) { weightedMax = delta; weightedWorst = i; }
        }
        return new JObject { ["formula"] = "skin.worldToLocal * bone.localToWorld * bindpose - I",
            ["maxAbs"] = max, ["worstBoneIndex"] = worst, ["worstBone"] = worst < 0 ? null : bones[worst].name,
            ["worstElementColumnMajor"] = worstElement, ["worstMatrix"] = BootProbeMatrix(worstMatrix),
            ["worstBoneLossyScale"] = worst < 0 ? null : Vec(bones[worst].lossyScale),
            ["weightedMaxAbs"] = weightedMax, ["weightedWorstBone"] = weightedWorst < 0 ? null : bones[weightedWorst].name,
            ["weightedBoneCount"] = weighted.Count, ["weightReadError"] = weightReadError,
            ["boneCount"] = bones.Length, ["bindposeCount"] = poses.Length, ["missingBones"] = missing,
            ["rendererLossyScale"] = Vec(skin.transform.lossyScale),
            ["rendererLocalToWorld"] = BootProbeMatrix(skin.transform.localToWorldMatrix) };
    }

    static JObject BootProbeRecord(string label, BootProbeEquipped equipped, BootProbeGeometry baseline, TriangleTree sourceSurface)
    {
        var geometry = BootProbeBake(equipped.Skin); var surface = TriangleTree.ForRenderer(equipped.Skin);
        var row = new JObject { ["case"] = label, ["worldBounds"] = Box(geometry.Bounds),
            ["rendererBounds"] = Box(equipped.Skin.bounds), ["vertices"] = geometry.Vertices.Length,
            ["triangles"] = geometry.Triangles, ["sourceToCandidate"] = BootProbeDistances(baseline,surface),
            ["candidateToSource"] = BootProbeDistances(geometry,sourceSurface),
            ["bindMatrices"] = BootProbeBindMatrices(equipped.Skin), ["settings"] = Settings(equipped.Worn),
            ["materials"] = new JArray(equipped.Skin.sharedMaterials.Select(m => m == null ? "" : AssetDatabase.GetAssetPath(m))),
            ["front"] = OutputRoot+"/review/probe_amy_"+label+"_front.png",
            ["back"] = OutputRoot+"/review/probe_amy_"+label+"_back.png" };
        // Preserve numerical evidence if the existing renderer rejects grossly
        // displaced geometry; a diagnostic must not lose the offending row.
        try
        {
            RenderWardrobe(equipped.Body, "probe_amy_"+label+"_front", false);
            RenderWardrobe(equipped.Body, "probe_amy_"+label+"_back", true);
        }
        catch (Exception error) { row["renderError"] = error.ToString(); }
        return row;
    }

    public static void AuditBootsGeometry()
    {
        const string id = "clothing.ankleboots_amy";
        Directory.CreateDirectory(OutputRoot);
        var sourcePath = AssetDatabase.FindAssets("t:Prefab",new[]{SourceRoot+"/"+id}).Select(AssetDatabase.GUIDToAssetPath)
            .Where(p=>p.EndsWith(".prefab",StringComparison.Ordinal)).Single();
        string preparedPath = PreparedRoot+"/Prefabs/"+Key(id)+".prefab";
        var dependency = AssetDatabase.GetAssetDependencyHash(sourcePath);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        var sourceWear = prefab.GetComponent<Wear>(); string settings = Settings(sourceWear).ToString();
        var prepared = AssetDatabase.LoadAssetAtPath<GameObject>(preparedPath);
        if (prepared == null) throw new InvalidOperationException("Current prepared Amy is missing: "+preparedPath);
        var rows = new JArray(); Mesh rawSimplified = null, posed = null, posedSimplified = null;
        GameObject rawTemplate = null, posedTemplate = null;
        var report = new JObject { ["schemaVersion"] = 1, ["actor"] = "Marta", ["source"] = sourcePath,
            ["prepared"] = preparedPath, ["triangleBudget"] = TriangleBudget,
            ["maxSamplesPerDirection"] = BootProbeSamples, ["rows"] = rows,
            ["distanceMetric"] = "deterministic sampled vertices and triangle centroids to exact nearest triangles; not exhaustive Hausdorff",
            ["alphaAndNormalMapHypothesesAlreadyExcluded"] = true, ["assetsChanged"] = false, ["bundlesBuilt"] = false };
        try
        {
            using var native = BootProbeEquip(sourceWear,"native");
            var baseline = BootProbeBake(native.Skin); var sourceSurface = TriangleTree.ForRenderer(native.Skin);
            rows.Add(BootProbeRecord("native",native,baseline,sourceSurface));
            rawSimplified = Simplify(native.Skin.sharedMesh,"probe_amy_raw_8k");
            rawTemplate = BootProbeNativeTemplate(prefab,rawSimplified);
            try
            {
                using var raw = BootProbeEquip(rawTemplate.GetComponent<Wear>(),"raw_simplify_native");
                rows.Add(BootProbeRecord("raw_simplify_native",raw,baseline,sourceSurface));
            }
            catch (Exception error) { rows.Add(new JObject { ["case"]="raw_simplify_native", ["error"]=error.ToString() }); }
            var normalizing = new Fit { Skin = native.Skin,
                BoneWorld = native.Skin.bones.Select(b=>b.localToWorldMatrix).ToArray() };
            posed = BindMesh(native.Skin.sharedMesh,normalizing,baseline.Vertices,baseline.Normals,"probe_amy_posed_bind");
            posed.ClearBlendShapes();
            report["normalizedAtAuthoringBindMatrices"] = BootProbeBindMatrices(native.Skin,posed);
            posedSimplified = Simplify(posed,"probe_amy_posed_8k");
            report["normalizedSimplifiedAtAuthoringBindMatrices"] = BootProbeBindMatrices(native.Skin,posedSimplified);
            posedTemplate = BootProbeNativeTemplate(prefab,posedSimplified);
            try
            {
                using var normalized = BootProbeEquip(posedTemplate.GetComponent<Wear>(),"posed_simplify_native");
                rows.Add(BootProbeRecord("posed_simplify_native",normalized,baseline,sourceSurface));
            }
            catch (Exception error) { rows.Add(new JObject { ["case"]="posed_simplify_native", ["error"]=error.ToString() }); }
            var record = ((JArray)JObject.Parse(File.ReadAllText(PeopleRoot+"/catalog.json"))["records"])
                .Where(r=>(string)r["type"]=="wear"&&(string)r["main"]==preparedPath)
                .OrderBy(r=>(string)r["id"],StringComparer.Ordinal).First();
            var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)record["definition"]);
            try
            {
                using var current = BootProbeEquip(prepared.GetComponent<Wear>(),"prepared",definition.variantMaterials);
                rows.Add(BootProbeRecord("prepared",current,baseline,sourceSurface));
            }
            catch (Exception error) { rows.Add(new JObject { ["case"]="prepared", ["error"]=error.ToString() }); }
            if (Settings(sourceWear).ToString()!=settings || AssetDatabase.GetAssetDependencyHash(sourcePath)!=dependency)
                throw new InvalidOperationException("Diagnostic mutated native source");
            report["completed"] = true; report["sourceUnchanged"] = true;
            report["caseErrors"] = rows.Count(row => row["error"] != null || row["renderError"] != null);
        }
        catch(Exception error)
        {
            report["completed"] = false; report["error"] = error.ToString(); throw;
        }
        finally
        {
            if(rawTemplate!=null)Object.DestroyImmediate(rawTemplate);
            if(posedTemplate!=null)Object.DestroyImmediate(posedTemplate);
            if(rawSimplified!=null)Object.DestroyImmediate(rawSimplified);
            if(posedSimplified!=null)Object.DestroyImmediate(posedSimplified);
            if(posed!=null)Object.DestroyImmediate(posed);
            File.WriteAllText(OutputRoot+"/amy-geometry-probe.json",report.ToString());
        }
        Debug.Log("AMY_GEOMETRY_PROBE_COMPLETED: "+rows.Count+" real equipped cases, sampled bidirectional errors and runtime bind matrices");
    }
}
}
#endif
