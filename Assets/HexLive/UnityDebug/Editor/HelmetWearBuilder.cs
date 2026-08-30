#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{

/// <summary>
/// Bug #329: превращает нормализованные Blender'ом шлемы
/// (Assets/HexLiveContent/RuntimeSource/Helmets/*.fbx — меш уже в «пространстве
/// актрисы»: метры, центр в нуле, перед по -Y блендера → +Z юнити) в wear-префабы.
///
/// Шлем — жёсткая вещь, а не DAZ-одежда, поэтому он строится как причёска:
/// цепочка костей hip→…→head с rest-позой актрисы (снята с AnarchyCap.prefab),
/// весь меш прискинен к head 1:1. Wear.Construct пришивает кости по имени, и
/// шлем ездит на голове без единой строчки рантайма.
///
/// Логирует, никаких диалогов (мост живёт на главном потоке редактора).
/// </summary>
public static class HelmetWearBuilder
{
    private const string SourceFolder = "Assets/HexLiveContent/RuntimeSource/Helmets";
    private const string WearFolder = "Assets/HexLiveContent/Wear";

    // Rest-поза DAZ-цепочки до головы — локальные позиции из AnarchyCap.prefab.
    private static readonly (string Name, Vector3 Local)[] Chain =
    {
        ("hip", new Vector3(0f, 1.03925f, 0.0203f)),
        ("abdomenLower", new Vector3(0f, 0.01767f, -0.01482f)),
        ("abdomenUpper", new Vector3(0f, 0.0815f, 0.01193f)),
        ("chestLower", new Vector3(0f, 0.07905f, -0.00397f)),
        ("chestUpper", new Vector3(0f, 0.136f, -0.03763f)),
        ("neckLower", new Vector3(0f, 0.18986f, -0.01559f)),
        ("neckUpper", new Vector3(0f, 0.03282f, 0.01979f)),
        ("head", new Vector3(0f, 0.04936f, 0.00159f)),
    };

    // Центр шлема относительно кости head: чуть выше и вперёд основания черепа.
    private static readonly Vector3 HeadCenterOffset = new(0f, 0.075f, 0.02f);

    [MenuItem("HexLive/Build Helmet Wear Prefabs")]
    public static void BuildAll()
    {
        var built = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { SourceFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            try
            {
                BuildOne(path);
                built++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"HelmetWearBuilder: {path}: {e}");
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"HelmetWearBuilder: built {built} helmet prefabs");
    }

    private static void BuildOne(string fbxPath)
    {
        var hid = Path.GetFileNameWithoutExtension(fbxPath); // helmet_space
        var itemId = "clothing." + hid;

        var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
        if (!importer.isReadable || importer.animationType != ModelImporterAnimationType.None)
        {
            importer.isReadable = true;
            importer.animationType = ModelImporterAnimationType.None;
            importer.SaveAndReimport();
        }

        // Unity не умеет читать текстуры, вшитые Blender'ом внутрь FBX, поэтому
        // пайплайн печёт каждому шлему единый albedo-PNG рядом с FBX, а материал
        // собирается здесь — плоский URP/Lit в стиле игры.
        var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
            $"{SourceFolder}/{hid}_albedo.png");

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null)
        {
            throw new System.Exception("model failed to load");
        }

        // Мировая (по префабу модели) геометрия — так импортные повороты
        // экспорта Blender→FBX запекаются и перестают существовать.
        var instance = Object.Instantiate(model);
        try
        {
            var bakedVertices = new List<Vector3>();
            var bakedNormals = new List<Vector3>();
            var bakedUvs = new List<Vector2>();
            var subTriangles = new List<List<int>>();
            var materials = new List<Material>();
            foreach (var mf in instance.GetComponentsInChildren<MeshFilter>())
            {
                BakeMesh(mf.sharedMesh, mf.transform.localToWorldMatrix,
                    mf.GetComponent<MeshRenderer>()?.sharedMaterials,
                    bakedVertices, bakedNormals, bakedUvs, subTriangles, materials);
            }

            if (bakedVertices.Count == 0)
            {
                throw new System.Exception("no mesh filters");
            }

            // Центрируем и сажаем на голову.
            var min = bakedVertices[0];
            var max = bakedVertices[0];
            foreach (var v in bakedVertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            var headWorld = Vector3.zero;
            foreach (var (_, local) in Chain)
            {
                headWorld += local;
            }

            var target = headWorld + HeadCenterOffset;
            var shift = target - (min + max) * 0.5f;
            for (var i = 0; i < bakedVertices.Count; i++)
            {
                bakedVertices[i] += shift;
            }

            // Скелет.
            var root = new GameObject(hid);
            var bones = new Transform[Chain.Length];
            Transform parent = root.transform;
            for (var i = 0; i < Chain.Length; i++)
            {
                var bone = new GameObject(Chain[i].Name).transform;
                bone.SetParent(parent, false);
                bone.localPosition = Chain[i].Local;
                bones[i] = bone;
                parent = bone;
            }

            var headIndex = Chain.Length - 1;

            var mesh = new Mesh { name = hid };
            mesh.SetVertices(bakedVertices);
            mesh.SetNormals(bakedNormals);
            mesh.SetUVs(0, bakedUvs);
            mesh.subMeshCount = subTriangles.Count;
            for (var s = 0; s < subTriangles.Count; s++)
            {
                mesh.SetTriangles(subTriangles[s], s);
            }

            var weights = new BoneWeight[bakedVertices.Count];
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = new BoneWeight { boneIndex0 = headIndex, weight0 = 1f };
            }

            mesh.boneWeights = weights;
            var bindposes = new Matrix4x4[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                bindposes[i] = bones[i].worldToLocalMatrix;
            }

            mesh.bindposes = bindposes;
            mesh.RecalculateBounds();

            var itemFolder = $"{WearFolder}/{itemId}";
            if (!AssetDatabase.IsValidFolder(itemFolder))
            {
                AssetDatabase.CreateFolder(WearFolder, itemId);
            }

            var meshPath = $"{itemFolder}/{hid}.mesh.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

            Material material;
            var matPath = $"{itemFolder}/{hid}.mat";
            if (albedo != null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                {
                    name = hid
                };
                material.SetTexture("_BaseMap", albedo);
                material.SetFloat("_Smoothness", 0.2f);
                AssetDatabase.DeleteAsset(matPath);
                AssetDatabase.CreateAsset(material, matPath);
                material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            }
            else
            {
                material = materials.Count > 0 ? materials[0] : null;
            }

            var sharedMaterials = new Material[subTriangles.Count];
            for (var s = 0; s < sharedMaterials.Length; s++)
            {
                sharedMaterials[s] = material != null ? material
                    : (s < materials.Count ? materials[s] : null);
            }

            var meshGo = new GameObject(hid + "_mesh");
            meshGo.transform.SetParent(root.transform, false);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[0];
            smr.sharedMaterials = sharedMaterials;
            smr.updateWhenOffscreen = true;

            var wear = root.AddComponent<Wear>();
            var so = new SerializedObject(wear);
            so.FindProperty("layer").enumValueIndex = (int)VisualWearLayer.Outerwear;
            so.FindProperty("hidesHair").boolValue = true;
            so.FindProperty("gender").enumValueIndex = (int)VisualGender.Female;
            var slots = so.FindProperty("slots");
            slots.arraySize = 1;
            slots.GetArrayElementAtIndex(0).enumValueIndex = (int)VisualWearSlot.Head;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, $"{itemFolder}/{hid}.prefab");
            Object.DestroyImmediate(root);
            Debug.Log($"HelmetWearBuilder: {itemId} → {itemFolder}/{hid}.prefab " +
                $"({bakedVertices.Count} verts, {subTriangles.Count} submeshes, " +
                $"albedo={(albedo != null ? albedo.name : "none")})");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static void BakeMesh(
        Mesh source, Matrix4x4 toWorld, Material[]? sourceMaterials,
        List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
        List<List<int>> subTriangles, List<Material> materials)
    {
        if (source == null)
        {
            return;
        }

        var baseIndex = vertices.Count;
        var normalMatrix = toWorld.inverse.transpose;
        var srcVerts = source.vertices;
        var srcNormals = source.normals;
        var srcUvs = source.uv;
        var flip = toWorld.determinant < 0f;
        for (var i = 0; i < srcVerts.Length; i++)
        {
            vertices.Add(toWorld.MultiplyPoint3x4(srcVerts[i]));
            normals.Add(i < srcNormals.Length
                ? normalMatrix.MultiplyVector(srcNormals[i]).normalized
                : Vector3.up);
            uvs.Add(i < srcUvs.Length ? srcUvs[i] : Vector2.zero);
        }

        for (var s = 0; s < source.subMeshCount; s++)
        {
            var tris = source.GetTriangles(s).Select(t => t + baseIndex).ToList();
            if (flip)
            {
                for (var t = 0; t < tris.Count; t += 3)
                {
                    (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
                }
            }

            subTriangles.Add(tris);
            materials.Add(sourceMaterials != null && s < sourceMaterials.Length
                ? sourceMaterials[s]
                : null!);
        }
    }
}

}
