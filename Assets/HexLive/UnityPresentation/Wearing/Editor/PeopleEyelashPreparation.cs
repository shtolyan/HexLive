#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// #482: separate, authored lash cards. Does not alter the paintable body FBX.
public static class PeopleEyelashPreparation
{
    private const string Root = PeopleAssetPreparation.Root;
    public static void Rebuild()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var path = Root + "/Source/Models/" + actor + "_Eyelashes.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            if (importer == null) throw new InvalidOperationException("Missing authored lash FBX: " + path);
            importer.importAnimation = false; importer.importBlendShapes = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.isReadable = true; importer.optimizeBones = true;
            importer.skinWeights = ModelImporterSkinWeights.Custom;
            importer.maxBonesPerVertex = 32; importer.minBoneWeight = 0;
            importer.importCameras = false; importer.importLights = false;
            importer.SaveAndReimport();
            PrepareMaterial(actor);
            var prefabPath = Root + "/Prefabs/Actors/" + actor + ".prefab";
            var prefab = PrefabUtility.LoadPrefabContents(prefabPath);
            try { Attach(prefab, actor); PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[PeopleEyelashes] Rebuilt two authored lash renderers; no bundles built.");
    }

    // Also used by the full actor preparation, so a later refresh keeps lashes.
    public static void Attach(GameObject actor, string id)
    {
        var old = actor.GetComponentsInChildren<Transform>(true).SingleOrDefault(t => t.name == "AuthoredEyelashes");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Source/Models/" + id + "_Eyelashes.fbx");
        var source = model != null ? model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single() : null;
        var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/" + id + "_Eyelashes.mat");
        if (source == null || material == null) throw new InvalidOperationException("Unprepared authored eyelashes: " + id);
        var bones = actor.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.name);
        var mapped = source.bones.Select(b => bones.TryGetValue(b.name, out var target) ? target :
            throw new InvalidOperationException("Missing eyelash bone: " + b.name)).ToArray();
        // The bind matrices define the source renderer basis. Match that basis
        // on the existing actor skeleton instead of adding a second skeleton.
        var matrices = mapped.Select((b, i) => actor.transform.worldToLocalMatrix * b.localToWorldMatrix * source.sharedMesh.bindposes[i]).ToArray();
        var basis = matrices[0];
        if (matrices.Any(m => Enumerable.Range(0, 16).Any(i => Mathf.Abs(m[i] - basis[i]) > .002f)))
            throw new InvalidOperationException("Lash and body rest skeletons differ: " + id);
        if (!ActorBodyResolver.TryResolve(actor, out var body, out var error)) throw new InvalidOperationException(error);
        var bodyBasis = actor.transform.worldToLocalMatrix * body.transform.localToWorldMatrix;
        if (Enumerable.Range(0, 16).Any(i => Mathf.Abs(bodyBasis[i] - basis[i]) > .002f))
            throw new InvalidOperationException("Lashes must share the body renderer basis: " + id);
        // Keep the same parent/local basis as the main skin. Flattening its
        // centimetre parent into a new .01 local scale breaks animated bounds.
        var go = new GameObject("AuthoredEyelashes"); go.transform.SetParent(body.transform.parent, false);
        go.transform.localPosition = body.transform.localPosition;
        go.transform.localRotation = body.transform.localRotation;
        go.transform.localScale = body.transform.localScale;
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = source.sharedMesh; renderer.sharedMaterial = material;
        renderer.bones = mapped; renderer.rootBone = body.rootBone;
        PeopleAssetPreparation.ConfigureActorBounds(go);
    }

    private static void PrepareMaterial(string actor)
    {
        var donorPath = actor == "Marta" ? "Assets/ImportedActors/Daz3D/Martanaked/Genesis3Female/Eyelashes.mat" :
            "Assets/ImportedActors/Daz3D/Kshishtof/Genesis3Male/Eyelashes.mat";
        var donor = AssetDatabase.LoadAssetAtPath<Material>(donorPath);
        // URP has no _CutoutOpacityMap shader property, but the source material
        // still serializes its authored DAZ mask there (male has no BaseMap).
        var envs = new SerializedObject(donor).FindProperty("m_SavedProperties.m_TexEnvs");
        Texture mask = null;
        var maskScale = Vector2.one; var maskOffset = Vector2.zero;
        foreach (var property in new[] { "_CutoutOpacityMap", "_BaseMap" })
        {
            for (var i = 0; i < envs.arraySize; i++)
            {
                var row = envs.GetArrayElementAtIndex(i);
                if (row.FindPropertyRelative("first").stringValue != property) continue;
                var value = row.FindPropertyRelative("second");
                mask = value.FindPropertyRelative("m_Texture").objectReferenceValue as Texture;
                maskScale = value.FindPropertyRelative("m_Scale").vector2Value;
                maskOffset = value.FindPropertyRelative("m_Offset").vector2Value;
                break;
            }
            if (mask != null) break;
        }
        if (mask == null) throw new InvalidOperationException("Missing authored lash opacity: " + donorPath);
        var sourcePath = AssetDatabase.GetAssetPath(mask);
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        var rgba = new Texture2D(1024, 1024, TextureFormat.RGBA32, false, true);
        try
        {
            if (!source.LoadImage(File.ReadAllBytes(sourcePath))) throw new InvalidOperationException("Cannot read lash mask " + sourcePath);
            source.wrapMode = TextureWrapMode.Repeat;
            var pixels = new Color[1024 * 1024];
            for (var y = 0; y < 1024; y++) for (var x = 0; x < 1024; x++)
                pixels[y * 1024 + x] = new Color(1, 1, 1, source.GetPixelBilinear(
                    (x + .5f) / 1024 * maskScale.x + maskOffset.x, (y + .5f) / 1024 * maskScale.y + maskOffset.y).r);
            rgba.SetPixels(pixels); rgba.Apply();
            var path = Root + "/Source/Textures/" + actor + "_Eyelashes_RGBA.png";
            File.WriteAllBytes(path, rgba.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 1024; importer.sRGBTexture = true; importer.isReadable = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true; importer.mipMapsPreserveCoverage = true;
            importer.alphaTestReferenceValue = donor.GetFloat("_Cutoff"); importer.SaveAndReimport();
            var matPath = Root + "/Materials/" + actor + "_Eyelashes.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material, matPath); }
            material.name = actor + "_Eyelashes";
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            material.SetColor("_BaseColor", donor.GetColor("_BaseColor"));
            material.SetFloat("_Surface", 0); material.SetFloat("_AlphaClip", 1);
            material.SetFloat("_Cutoff", donor.GetFloat("_Cutoff")); material.SetFloat("_Cull", 0);
            material.SetFloat("_ZWrite", 1); material.SetFloat("_Smoothness", 0);
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.EnableKeyword("_ALPHATEST_ON"); material.renderQueue = 2450;
            EditorUtility.SetDirty(material);
        }
        finally { Object.DestroyImmediate(source); Object.DestroyImmediate(rgba); }
    }
}

// Original DAZ and prepared FBX use different rest-bone bases. Rebind the
// unchanged authored lash vertices to the body's imported bind matrices,
// compacting to the bones actually used by the lashes. This is import data,
// not an actor-specific transform correction at runtime.
public sealed class PeopleEyelashModelPostprocessor : AssetPostprocessor
{
    private void OnPostprocessModel(GameObject model)
    {
        if (!assetPath.StartsWith(PeopleAssetPreparation.Root + "/Source/Models/", StringComparison.Ordinal) ||
            !assetPath.EndsWith("_Eyelashes.fbx", StringComparison.Ordinal)) return;
        var actor = Path.GetFileNameWithoutExtension(assetPath).Replace("_Eyelashes", "");
        var bodyPath = PeopleAssetPreparation.Root + "/Source/Models/" + actor + ".fbx";
        context.DependsOnSourceAsset(bodyPath);
        var body = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath).GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Single(r => r.sharedMesh.blendShapeCount == 109);
        var bodyIndices = body.bones.Select((b, i) => (b.name, i)).ToDictionary(p => p.name, p => p.i);
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = renderer.sharedMesh;
            // Mesh setters invalidate its borrowed NativeArray views.
            var counts = mesh.GetBonesPerVertex().ToArray(); var weights = mesh.GetAllBoneWeights().ToArray();
            var toBody = body.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Debug.Log($"[PeopleLashBasis] {actor}: source bounds={mesh.bounds}, source scale={renderer.transform.lossyScale}, body scale={body.transform.lossyScale}, basis scale={toBody.lossyScale}");
            NormalizeMeshBasis(mesh, toBody);
            var used = weights.Select(w => w.boneIndex).Distinct().OrderBy(i => i).ToArray();
            var remap = used.Select((old, index) => (old, index)).ToDictionary(p => p.old, p => p.index);
            foreach (var old in used)
                if (!bodyIndices.ContainsKey(renderer.bones[old].name)) throw new InvalidOperationException("Missing lash binding " + renderer.bones[old].name);
            var targetBindposes = body.sharedMesh.bindposes;
            mesh.bindposes = used.Select(i => targetBindposes[bodyIndices[renderer.bones[i].name]]).ToArray();
            for (var i = 0; i < weights.Length; i++) weights[i].boneIndex = remap[weights[i].boneIndex];
            using var compactCounts = new Unity.Collections.NativeArray<byte>(counts, Unity.Collections.Allocator.Temp);
            using var compactWeights = new Unity.Collections.NativeArray<BoneWeight1>(weights, Unity.Collections.Allocator.Temp);
            mesh.SetBoneWeights(compactCounts, compactWeights);
            renderer.bones = used.Select(i => renderer.bones[i]).ToArray();
        }
    }

    private static void NormalizeMeshBasis(Mesh mesh, Matrix4x4 basis)
    {
        var vertices = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents;
        var normalBasis = basis.inverse.transpose;
        var newNormals = normals.Select(n => normalBasis.MultiplyVector(n).normalized).ToArray();
        var newTangents = tangents.Select(t => {
            var direction = basis.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
            return new Vector4(direction.x, direction.y, direction.z, t.w * (basis.determinant < 0 ? -1 : 1));
        }).ToArray();
        var frames = new List<(string name, float weight, Vector3[] p, Vector3[] n, Vector3[] t)>();
        for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            for (var frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                var p = new Vector3[mesh.vertexCount]; var n = new Vector3[p.Length]; var t = new Vector3[p.Length];
                mesh.GetBlendShapeFrameVertices(shape, frame, p, n, t);
                for (var i = 0; i < p.Length; i++)
                {
                    p[i] = basis.MultiplyVector(p[i]);
                    if (normals.Length == p.Length) n[i] = normalBasis.MultiplyVector(normals[i] + n[i]).normalized - newNormals[i];
                    if (tangents.Length == p.Length)
                        t[i] = basis.MultiplyVector(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z) + t[i]).normalized -
                            new Vector3(newTangents[i].x, newTangents[i].y, newTangents[i].z);
                }
                frames.Add((mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, frame), p, n, t));
            }
        mesh.ClearBlendShapes();
        mesh.vertices = vertices.Select(basis.MultiplyPoint3x4).ToArray();
        mesh.normals = newNormals; if (newTangents.Length > 0) mesh.tangents = newTangents;
        foreach (var f in frames) mesh.AddBlendShapeFrame(f.name, f.weight, f.p, f.n, f.t);
        mesh.RecalculateBounds();
    }
}
}
#endif
