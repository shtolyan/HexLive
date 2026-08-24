#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HexLive.Editor
{
    public static class MedkitNativeSetup
    {
        private const string MeshPath =
            "Assets/HexLiveContent/RuntimeSource/Objects/item_medkit_native_mesh.fbx";
        private const string TexturePath =
            "Assets/HexLiveContent/RuntimeSource/Objects/item_medkit_albedo.png";
        private const string MaterialPath =
            "Assets/HexLiveContent/RuntimeSource/Objects/MedkitTextured.mat";
        private const string PrefabPath =
            "Assets/HexLiveContent/RuntimeSource/Objects/item.medkit.prefab";

        public static void Configure()
        {
            AssetDatabase.ImportAsset(MeshPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceSynchronousImport);

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (source == null || texture == null || shader == null)
                throw new InvalidOperationException(
                    $"Medkit native inputs are incomplete: mesh={source != null}, " +
                    $"texture={texture != null}, shader={shader != null}");

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "MedkitTextured" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            else
            {
                material.shader = shader;
            }
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(material);

            var instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
            if (instance == null)
                throw new InvalidOperationException("Could not instantiate medkit FBX");
            try
            {
                instance.name = "item.medkit";
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                    throw new InvalidOperationException("Medkit FBX has no renderers");
                foreach (var renderer in renderers)
                    renderer.sharedMaterials = Enumerable.Repeat(
                        material, Math.Max(1, renderer.sharedMaterials.Length)).ToArray();
                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[MedkitNativeSetup] Player-safe prefab ready: {PrefabPath}");
        }
    }
}
#endif
