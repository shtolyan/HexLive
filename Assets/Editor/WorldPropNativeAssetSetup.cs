#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.Editor
{
    public static class WorldPropNativeAssetSetup
    {
        [MenuItem("HexLive/Content/Create Native Bottle Prefab")]
        public static void CreateNativeBottlePrefab()
        {
            const string meshPath = "Assets/HexLiveContent/RuntimeSource/Objects/tool_bottle_native_mesh.fbx";
            const string texturePath = "Assets/HexLiveContent/RuntimeSource/Objects/tool_bottle_albedo.png";
            const string materialPath = "Assets/HexLiveContent/RuntimeSource/Objects/BottleTransparent.mat";
            const string prefabPath = "Assets/HexLiveContent/RuntimeSource/Objects/tool_bottle_native.prefab";

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (source == null || texture == null || shader == null)
                throw new FileNotFoundException("Native bottle mesh, albedo or URP/Lit is missing.");

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "BottleTransparent" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.shader = shader;
            material.mainTexture = texture;
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);

            var instance = Object.Instantiate(source);
            instance.name = "tool_bottle_native";
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[WorldProp] Native bottle prefab created with transparent textured URP material.");
        }

        [MenuItem("HexLive/Content/Create Native Machete Prefab")]
        public static void CreateNativeMachetePrefab()
        {
            const string meshPath = "Assets/HexLiveContent/RuntimeSource/Objects/tool_machete_native_mesh.fbx";
            const string texturePath = "Assets/HexLiveContent/RuntimeSource/Objects/tool_machete_albedo.png";
            const string materialPath = "Assets/HexLiveContent/RuntimeSource/Objects/MacheteTextured.mat";
            const string prefabPath = "Assets/HexLiveContent/RuntimeSource/Objects/tool_machete_native.prefab";

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (source == null || texture == null || shader == null)
                throw new FileNotFoundException("Native machete mesh, albedo or URP/Lit is missing.");

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "MacheteTextured" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.shader = shader;
            material.mainTexture = texture;
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 1f);
            material.SetOverrideTag("RenderType", "Opaque");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Geometry;
            EditorUtility.SetDirty(material);

            var instance = Object.Instantiate(source);
            instance.name = "tool_machete_native";
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[WorldProp] Native machete prefab created with an opaque textured URP material.");
        }
    }
}
#endif
