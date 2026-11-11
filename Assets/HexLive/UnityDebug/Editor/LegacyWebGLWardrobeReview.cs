#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    public static void AuditQualityAndSkirts()
    {
        AuditBootsQuality();
        AuditSkirtRetarget();
    }

    public static void AuditSkirtRetarget()
    {
        string path = PreparedRoot + "/Prefabs/" + Key("clothing.skirt_anarchy") + ".prefab";
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var source = SourceMesh(asset.GetComponent<Wear>(),ActorName.Marta,out float scale);
        using var female = ConstructFit(asset,source,source,ActorName.Marta);
        using var male = ConstructFit(asset,source,source,ActorName.Kshishtof);
        foreach (var mode in new[] { "surface", "clearance", "bones" })
        {
            var mesh = Retarget(source,female,male,out _,mode=="surface",mode!="bones");
            var template = Object.Instantiate(asset);
            var body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"));
            try
            {
                SetFits(template.GetComponent<Wear>(),source,mesh,scale,scale,false);
                body.GetComponent<Animator>().enabled=false;
                var bones=body.GetComponent<BodyBones>();bones.Construct(ActorName.Kshishtof,649);
                bones.Equip("baseline",AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Wear/Kshishtof_PrimalBriefs.prefab").GetComponent<Wear>(),null);
                bones.Equip("skirt",template.GetComponent<Wear>(),null);
                typeof(BodyBones).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bones,null);
                RenderWardrobe(body,"skirt_retarget_"+mode+"_front",false);
                RenderWardrobe(body,"skirt_retarget_"+mode+"_back",true);
            }
            finally { Object.DestroyImmediate(body);Object.DestroyImmediate(template);Object.DestroyImmediate(mesh); }
        }
    }

    public static void ReviewNativeBoots()
    {
        const string id = "clothing.ankleboots_amy";
        var path = AssetDatabase.FindAssets("t:Prefab", new[] { SourceRoot + "/" + id }).Select(AssetDatabase.GUIDToAssetPath).Single();
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Wear>();
        var body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Actors/Marta.prefab"));
        try
        {
            body.GetComponent<Animator>().enabled = false;
            var bones = body.GetComponent<BodyBones>(); bones.Construct(ActorName.Marta,649);
            bones.Equip("baseline649", AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Wear/Marta_PrimalBriefs.prefab").GetComponent<Wear>(), null);
            bones.Equip(id, source, null);
            typeof(BodyBones).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bones,null);
            RenderWardrobe(body,"native_amy_front",false); RenderWardrobe(body,"native_amy_back",true);
            var materials = new JArray();
            foreach (var material in source.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials)
            {
                var saved = new SerializedObject(material).FindProperty("m_SavedProperties.m_TexEnvs"); var textures = new JArray();
                for (int i=0;i<saved.arraySize;i++)
                {
                    var entry=saved.GetArrayElementAtIndex(i);var value=entry.FindPropertyRelative("second.m_Texture").objectReferenceValue;
                    if (value!=null) textures.Add(new JObject { ["property"]=entry.FindPropertyRelative("first").stringValue,["path"]=AssetDatabase.GetAssetPath(value) });
                }
                materials.Add(new JObject { ["name"]=material.name,["path"]=AssetDatabase.GetAssetPath(material),["textures"]=textures });
            }
            File.WriteAllText(OutputRoot+"/native-amy-materials.json",materials.ToString());
        }
        finally { Object.DestroyImmediate(body); }
    }

    public static void AuditPreparedTransparency()
    {
        string sourcePath = AssetDatabase.FindAssets("t:Prefab",new[]{SourceRoot+"/clothing.ankleboots_amy"}).Select(AssetDatabase.GUIDToAssetPath).Single();
        var rows = new JArray();
        foreach (var source in AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath).GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials)
        {
            var prepared = AssetDatabase.LoadAssetAtPath<Material>(PreparedRoot+"/Materials/"+AssetKey(source)+".mat");
            JObject Describe(Material material)
            {
                var texture = material.GetTexture("_BaseMap") as Texture2D;
                var importer = texture == null ? null : AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
                var data = new JObject { ["material"]=material.name,["clip"]=material.GetFloat("_AlphaClip"),["cutoff"]=material.GetFloat("_Cutoff"),["texture"]=texture==null?"":AssetDatabase.GetAssetPath(texture),
                    ["format"]=texture==null?"":texture.format.ToString(),["type"]=importer==null?"":importer.textureType.ToString(),["compression"]=importer==null?"":importer.textureCompression.ToString() };
                if(texture==null)return data;
                var rt=RenderTexture.GetTemporary(128,128,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear); var old=RenderTexture.active;
                var read=new Texture2D(128,128,TextureFormat.RGBA32,false,true);
                try { Graphics.Blit(texture,rt);RenderTexture.active=rt;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();var pixels=read.GetPixels32();data["alphaMin"]=pixels.Min(p=>(int)p.a);data["alphaMax"]=pixels.Max(p=>(int)p.a);data["transparentSamples"]=pixels.Count(p=>p.a<128); }
                finally { RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(read); }
                return data;
            }
            rows.Add(new JObject{["source"]=Describe(source),["prepared"]=Describe(prepared)});
        }
        File.WriteAllText(OutputRoot+"/transparency-audit.json",rows.ToString());
        RenderPrepared(PreparedRoot+"/Prefabs/"+Key("clothing.ankleboots_amy")+".prefab",true);
    }

    static void RenderPrepared(string prefabPath, bool diagnosticOpaque = false, JToken record = null)
    {
        record ??= ((JArray)JObject.Parse(File.ReadAllText(PreparedRoot + "/catalog.json"))["records"])
            .Where(r => (string)r["type"] == "wear" && (string)r["main"] == prefabPath)
            .OrderBy(r => (string)r["id"], StringComparer.Ordinal).First();
        if ((string)record["type"] != "wear" || (string)record["main"] != prefabPath)
            throw new InvalidOperationException("Prepared gallery record does not own " + prefabPath);
        var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)record["definition"]);
        if (definition == null)
            throw new InvalidOperationException("Missing prepared gallery definition for " + prefabPath);
        foreach (var actor in new[] { ActorName.Marta, ActorName.Kshishtof })
        {
            var body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Actors/" + actor + ".prefab"));
            var temporaryMaterials = new List<Material>();
            try
            {
                body.GetComponent<Animator>().enabled = false;
                var bones = body.GetComponent<BodyBones>(); bones.Construct(actor, 649);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<Wear>();
                if (!asset.SupportsActor(actor)) throw new InvalidOperationException("Missing prepared fit " + actor);
                if (!(asset.Layer == VisualWearLayer.Underwear && asset.Slots.Contains(VisualWearSlot.Pelvis)))
                    bones.Equip("baseline649", AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot + "/Prefabs/Wear/" + actor + "_PrimalBriefs.prefab").GetComponent<Wear>(), null);
                var materials = definition.variantMaterials;
                if (diagnosticOpaque)
                {
                    materials = asset.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials.Select(m => {
                        var copy = new Material(m);copy.SetFloat("_AlphaClip",0);copy.DisableKeyword("_ALPHATEST_ON");temporaryMaterials.Add(copy);return copy;
                    }).ToArray();
                }
                bones.Equip((string)record["id"], asset, materials);
                // EditMode previews have no frame loop: execute the production heel
                // correction once after equip, just as LateUpdate does in game.
                typeof(BodyBones).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bones, null);
                string key = Path.GetFileNameWithoutExtension(prefabPath) + "_" + actor + (diagnosticOpaque ? "_opaque" : "");
                RenderWardrobe(body, key + "_front", false);
                RenderWardrobe(body, key + "_back", true);
            }
            finally { Object.DestroyImmediate(body);foreach(var material in temporaryMaterials)Object.DestroyImmediate(material); }
        }
    }

    static void RenderWardrobe(GameObject body, string key, bool back)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new InvalidOperationException("Real graphics required for wardrobe review");
        bool async = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        var preview = new PreviewRenderUtility(); var meshes = new List<Mesh>();
        try
        {
            var parts = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials, MaterialPropertyBlock[] blocks)>();
            foreach (var renderer in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var mesh = new Mesh(); meshes.Add(mesh); renderer.BakeMesh(mesh, true);
                var vertices = mesh.vertices.Select(renderer.transform.TransformPoint).ToArray();
                var b = VertexBounds(vertices);
                if (b.size.magnitude > 4f || b.center.magnitude > 3f)
                    throw new InvalidOperationException(key + ": invalid runtime fitted bounds " + b);
                var materials = renderer.sharedMaterials;
                var blocks = new MaterialPropertyBlock[materials.Length];
                for (int i = 0; i < blocks.Length; i++)
                {
                    blocks[i] = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(blocks[i], i);
                    // Unity gives a per-material block priority over the renderer
                    // block. An empty per-material block uses the renderer fallback.
                    if (blocks[i].isEmpty) renderer.GetPropertyBlock(blocks[i]);
                }
                parts.Add((mesh, renderer.transform.localToWorldMatrix, materials, blocks));
            }
            var camera = preview.camera; camera.orthographic = true; camera.orthographicSize = 1.12f;
            camera.transform.position = new Vector3(back ? -.3f : .3f, 1.02f, back ? -4f : 4f);
            camera.transform.LookAt(new Vector3(0, .95f, 0)); camera.nearClipPlane = .01f; camera.farClipPlane = 10;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.13f,.15f,.17f);
            preview.lights[0].intensity = 1.15f; preview.lights[0].transform.rotation = Quaternion.Euler(35,back ? 0 : 180,0);
            preview.lights[1].intensity = .55f; preview.lights[1].transform.rotation = Quaternion.Euler(-15,back ? 160 : -30,0);
            Directory.CreateDirectory(OutputRoot + "/review");
            for (int pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0,0,768,768));
                foreach (var part in parts)
                for (int i = 0; i < part.mesh.subMeshCount; i++)
                {
                    int materialIndex = Mathf.Min(i, part.materials.Length-1);
                    preview.DrawMesh(part.mesh, part.matrix, part.materials[materialIndex], i, part.blocks[materialIndex]);
                }
                camera.Render(); var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(OutputRoot + "/review/" + key + ".png", image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
        }
        finally { foreach (var mesh in meshes) Object.DestroyImmediate(mesh); preview.Cleanup(); ShaderUtil.allowAsyncCompilation = async; }
    }
}
}
#endif
