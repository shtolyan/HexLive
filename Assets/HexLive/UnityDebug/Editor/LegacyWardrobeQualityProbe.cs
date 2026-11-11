#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMeshSimplifier;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
// Diagnostic only. Compile alongside LegacyWardrobeProbe.cs and the authoring partials.
// No persistent mesh, prefab, material, fit setting or production budget is changed.
public static partial class LegacyWebGLWardrobePreparation
{
    const float BootQualityContactRange = .05f;
    const float BootQualityPenetrationThreshold = -.0005f;

    static Mesh BootQualitySimplify(Mesh posed, int budget, out JObject algorithm)
    {
        long triangles = Enumerable.Range(0, posed.subMeshCount).Sum(s => (long)posed.GetIndexCount(s)) / 3;
        var options = SimplificationOptions.Default;
        options.PreserveSurfaceCurvature = true;
        options.PreserveBorderEdges = true;
        options.PreserveUVSeamEdges = true;
        options.PreserveUVFoldoverEdges = true;
        float quality = Mathf.Min(1f, (float)budget / triangles);
        algorithm = new JObject { ["requestedTriangleBudget"] = budget, ["inputTriangles"] = triangles,
            ["requestedQualityRatio"] = quality, ["preserveSurfaceCurvature"] = options.PreserveSurfaceCurvature,
            ["preserveBorderEdges"] = options.PreserveBorderEdges, ["preserveUVSeamEdges"] = options.PreserveUVSeamEdges,
            ["preserveUVFoldoverEdges"] = options.PreserveUVFoldoverEdges,
            ["enableSmartLink"] = options.EnableSmartLink, ["maxIterationCount"] = options.MaxIterationCount,
            ["agressiveness"] = options.Agressiveness };
        Mesh mesh;
        if (triangles <= budget) mesh = Object.Instantiate(posed);
        else
        {
            var simplifier = new MeshSimplifier { SimplificationOptions = options };
            simplifier.Initialize(posed);
            simplifier.SimplifyMesh(quality);
            mesh = simplifier.ToMesh();
        }
        mesh.name = "probe_amy_quality_" + budget;
        if (mesh.vertexCount == 0 || mesh.subMeshCount != posed.subMeshCount || mesh.bindposes.Length != posed.bindposes.Length)
        {
            Object.DestroyImmediate(mesh);
            throw new InvalidOperationException("Quality simplification broke skin/material layout at " + budget);
        }
        long actual = Enumerable.Range(0, mesh.subMeshCount).Sum(s => (long)mesh.GetIndexCount(s)) / 3;
        algorithm["actualTriangles"] = actual;
        algorithm["actualToRequestedRatio"] = (double)actual / budget;
        algorithm["actualToSourceRatio"] = (double)actual / triangles;
        return mesh;
    }

    static JObject BootQualityContacts(Vector3[] points, int start, int count, TriangleTree canonicalBody)
    {
        int nearCount = 0, penetrations = 0, worstIndex = -1;
        float maxDepth = 0, minSigned = float.PositiveInfinity;
        var depths = new System.Collections.Generic.List<float>();
        Vector3 worstPoint = default, worstBodyPoint = default, worstNormal = default;
        for (int i = start; i < start + count; i++)
        {
            var contact = canonicalBody.Nearest(points[i]);
            if (!(contact.Distance < BootQualityContactRange)) continue;
            nearCount++;
            float signed = Vector3.Dot(points[i] - contact.Point, contact.Normal);
            minSigned = Mathf.Min(minSigned, signed);
            if (!(signed < BootQualityPenetrationThreshold)) continue;
            penetrations++;
            float depth = -signed; depths.Add(depth);
            if (depth <= maxDepth) continue;
            maxDepth = depth; worstIndex = i;
            worstPoint = points[i]; worstBodyPoint = contact.Point; worstNormal = contact.Normal;
        }
        depths.Sort();
        return new JObject { ["sampleCount"] = count, ["nearBodySampleCount"] = nearCount,
            ["penetratingSampleCount"] = penetrations, ["penetrationFractionOfAllSamples"] = count == 0 ? 0 : (double)penetrations / count,
            ["penetrationFractionOfNearSamples"] = nearCount == 0 ? 0 : (double)penetrations / nearCount,
            ["maxPenetrationDepth"] = maxDepth,
            ["p95PenetratingDepth"] = depths.Count == 0 ? 0 : depths[Math.Max(0, (int)Math.Ceiling(depths.Count * .95) - 1)],
            ["minNearSignedDistance"] = nearCount == 0 ? JValue.CreateNull() : new JValue(minSigned),
            ["worstSampleIndex"] = worstIndex,
            ["worstSamplePoint"] = worstIndex < 0 ? null : Vec(worstPoint),
            ["worstBodyPoint"] = worstIndex < 0 ? null : Vec(worstBodyPoint),
            ["worstBodyNormal"] = worstIndex < 0 ? null : Vec(worstNormal) };
    }

    static JObject BootQualityPenetration(BootProbeGeometry geometry, BootProbeEquipped equipped)
    {
        // Resolve the canonical body even where the equipped boots hide its render slots.
        // Re-bake each case's real body AFTER Equip/LateUpdate; no unposed neutral-body proxy.
        var bodySkin = ActorBodyResolver.ResolveOrNull(equipped.Body.transform);
        var body = TriangleTree.ForRenderer(bodySkin);
        if (body.Bounds.size.y < 1.5f || body.Bounds.size.y > 2.2f)
            throw new InvalidOperationException("Canonical posed body surface is not in world metres: " + body.Bounds);
        return new JObject { ["canonicalBodyRenderer"] = bodySkin.name, ["bodyBounds"] = Box(body.Bounds),
            ["units"] = "world metres", ["contactDistanceLessThan"] = BootQualityContactRange,
            ["signedDistanceLessThan"] = BootQualityPenetrationThreshold,
            ["signedFormula"] = "dot(sample - nearestBodyPoint, interpolatedBodyNormal)",
            ["limitation"] = "sampled local signed contact using nearest triangle normal, not a watertight inside/outside or exhaustive intersection test",
            ["all"] = BootQualityContacts(geometry.Samples, 0, geometry.Samples.Length, body),
            ["vertices"] = BootQualityContacts(geometry.Samples, 0, geometry.VertexSamples, body),
            ["triangleCentroids"] = BootQualityContacts(geometry.Samples, geometry.VertexSamples, geometry.CentroidSamples, body) };
    }

    static JObject BootQualityRecord(string label, BootProbeEquipped equipped, BootProbeGeometry baseline,
        TriangleTree sourceSurface, JObject nativePenetration = null)
    {
        var row = BootProbeRecord("quality_" + label, equipped, baseline, sourceSurface);
        var geometry = BootProbeBake(equipped.Skin);
        var penetration = BootQualityPenetration(geometry, equipped); row["bodyContact"] = penetration;
        if (nativePenetration != null)
        {
            var current = penetration["all"]; var native = nativePenetration["all"];
            row["deltaFromNativeBodyContact"] = new JObject {
                ["maxPenetrationDepth"] = (double)current["maxPenetrationDepth"] - (double)native["maxPenetrationDepth"],
                ["penetratingSampleCount"] = (int)current["penetratingSampleCount"] - (int)native["penetratingSampleCount"],
                ["penetrationFractionOfAllSamples"] = (double)current["penetrationFractionOfAllSamples"] - (double)native["penetrationFractionOfAllSamples"],
                ["sampleLocationsDifferByTopology"] = true };
        }
        return row;
    }

    public static void AuditBootsQuality()
    {
        const string id = "clothing.ankleboots_amy";
        Directory.CreateDirectory(OutputRoot);
        string path = AssetDatabase.FindAssets("t:Prefab", new[] { SourceRoot + "/" + id })
            .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".prefab", StringComparison.Ordinal)).Single();
        var dependency = AssetDatabase.GetAssetDependencyHash(path);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var wear = prefab.GetComponent<Wear>(); string settings = Settings(wear).ToString();
        var rows = new JArray(); Mesh posed = null;
        var report = new JObject { ["schemaVersion"] = 1, ["actor"] = "Marta", ["source"] = path,
            ["requestedTriangleBudgets"] = new JArray(8000, 16000, 32000, 64000),
            ["maxSamplesPerDirection"] = BootProbeSamples, ["rows"] = rows,
            ["distanceMetric"] = "deterministic sampled vertices and triangle centroids to exact nearest triangles; not exhaustive Hausdorff",
            ["purpose"] = "quality-vs-actual-triangle evidence; no production threshold or budget selected by this diagnostic",
            ["alphaAndNormalMapHypothesesAlreadyExcluded"] = true, ["assetsChanged"] = false, ["bundlesBuilt"] = false };
        try
        {
            using var native = BootProbeEquip(wear, "quality_native");
            var baseline = BootProbeBake(native.Skin); var surface = TriangleTree.ForRenderer(native.Skin);
            var nativeRow = BootQualityRecord("native", native, baseline, surface); rows.Add(nativeRow);
            var nativePenetration = (JObject)nativeRow["bodyContact"];
            var normalizing = new Fit { Skin = native.Skin,
                BoneWorld = native.Skin.bones.Select(b => b.localToWorldMatrix).ToArray() };
            posed = BindMesh(native.Skin.sharedMesh, normalizing, baseline.Vertices, baseline.Normals, "probe_amy_quality_posed");
            posed.ClearBlendShapes();
            report["normalizedAtAuthoringBindMatrices"] = BootProbeBindMatrices(native.Skin, posed);

            // Full-resolution normalized control separates normalization/re-equip from decimation.
            var controlTemplate = BootProbeNativeTemplate(prefab, posed);
            try
            {
                using var control = BootProbeEquip(controlTemplate.GetComponent<Wear>(), "quality_posed_full_resolution");
                rows.Add(BootQualityRecord("posed_full_resolution", control, baseline, surface, nativePenetration));
            }
            finally { Object.DestroyImmediate(controlTemplate); }

            foreach (int budget in new[] { 8000, 16000, 32000, 64000 })
            {
                Mesh candidate = null; GameObject template = null;
                try
                {
                    candidate = BootQualitySimplify(posed, budget, out var algorithm);
                    var authoringBind = BootProbeBindMatrices(native.Skin, candidate);
                    template = BootProbeNativeTemplate(prefab, candidate);
                    using var equipped = BootProbeEquip(template.GetComponent<Wear>(), "quality_curvature_" + budget);
                    var row = BootQualityRecord("curvature_" + budget, equipped, baseline, surface, nativePenetration);
                    row["simplification"] = algorithm; row["normalizedAtAuthoringBindMatrices"] = authoringBind;
                    rows.Add(row);
                    // Preserve completed budgets if a later decimation/render fails or is interrupted.
                    File.WriteAllText(OutputRoot + "/amy-quality-probe.json", report.ToString());
                }
                catch (Exception error)
                {
                    rows.Add(new JObject { ["case"] = "quality_curvature_" + budget, ["requestedTriangleBudget"] = budget,
                        ["error"] = error.ToString() });
                }
                finally
                {
                    if (template != null) Object.DestroyImmediate(template);
                    if (candidate != null) Object.DestroyImmediate(candidate);
                }
            }
            if (Settings(wear).ToString() != settings || AssetDatabase.GetAssetDependencyHash(path) != dependency)
                throw new InvalidOperationException("Quality diagnostic mutated native source");
            report["completed"] = true; report["sourceUnchanged"] = true;
            report["caseErrors"] = rows.Count(row => row["error"] != null || row["renderError"] != null);
        }
        catch (Exception error) { report["completed"] = false; report["error"] = error.ToString(); throw; }
        finally
        {
            if (posed != null) Object.DestroyImmediate(posed);
            File.WriteAllText(OutputRoot + "/amy-quality-probe.json", report.ToString());
        }
        Debug.Log("AMY_QUALITY_PROBE_COMPLETED: " + rows.Count + " actual equipped controls/budgets; see amy-quality-probe.json");
    }
}
}
#endif
