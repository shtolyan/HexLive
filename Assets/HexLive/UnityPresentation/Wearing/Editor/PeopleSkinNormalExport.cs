#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// Exports existing Unity normal textures and their authored strength; no asset mutation.
public static class PeopleSkinNormalExport
{
    public static void Install()
    {
        const string source="Build/PeopleSkinNormals";
        foreach(var actor in new[]{"Marta","Kshishtof"})
            if(!File.Exists(source+"/"+actor+"_skin_n.png"))throw new InvalidOperationException("Missing remapped normal: "+actor);
        var manifestPath=PeopleAssetPreparation.Root+"/Source/source-manifest.json";
        var manifest=JObject.Parse(File.ReadAllText(manifestPath));
        foreach(var actor in new[]{"Marta","Kshishtof"})
        {
            var relative="Textures/"+actor+"_SkinNormal.png";
            var path=PeopleAssetPreparation.Root+"/Source/"+relative;
            File.Copy(source+"/"+actor+"_skin_n.png",path,true);
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer=(TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType=TextureImporterType.NormalMap;importer.sRGBTexture=false;
            importer.mipmapEnabled=true;importer.maxTextureSize=1024;importer.isReadable=false;
            importer.wrapMode=TextureWrapMode.Clamp;importer.textureCompression=TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
            var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var row=manifest["materials"][actor+"_Atlas"];
            var images=(JArray)row["images"];
            foreach(var old in images.Where(i=>(string)i["node"]=="SkinNormal").ToArray())old.Remove();
            images.Add(new JObject { ["node"]="SkinNormal",["image"]=relative,
                ["links"]=new JArray(new JObject{["output"]="Color",["toNode"]="Normal Map",["toSocket"]="Color"}) });
            row["normalScale"]=1f; // Existing source material strengths are already in the remapped vectors.
            var paths=new[]{PeopleAssetPreparation.Root+"/Materials/"+actor+"_Atlas__Base.mat"}
                .Concat(Directory.GetFiles(PeopleAssetPreparation.Root+"/Materials","Skin_"+actor+"_*.mat"));
            foreach(var materialPath in paths)
            {
                var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath.Replace('\\','/'));
                if(material==null)throw new InvalidOperationException("Missing skin material "+materialPath);
                material.SetTexture("_BumpMap",texture);material.SetFloat("_BumpScale",1);material.EnableKeyword("_NORMALMAP");
                EditorUtility.SetDirty(material);
            }
        }
        File.WriteAllText(manifestPath,manifest.ToString(Newtonsoft.Json.Formatting.Indented)+System.Environment.NewLine);
        AssetDatabase.SaveAssets();
    }

    public static void Export()
    {
        const string output="Build/PeopleSkinNormalSource";
        Directory.CreateDirectory(output);
        var report=new JArray();
        var decoder=new Material(Shader.Find("Hidden/HexLive/SourceNormalDecode"));
        var oldAsync=ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation=false;
        try
        {
            foreach(var actor in new[]{"Marta","Kshishtof"})
            {
                var root=actor=="Marta"?"Assets/ImportedActors/Daz3D/Martanaked/Genesis3Female":"Assets/ImportedActors/Daz3D/Kshishtof/Genesis3Male";
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/"+actor+".prefab");
                if(!ActorBodyResolver.TryResolve(prefab,out var body,out var error))throw new InvalidOperationException(error);
                var mesh=body.sharedMesh;
                if(mesh.tangents.Length!=mesh.vertexCount)throw new InvalidOperationException("Prepare body tangents first: "+actor);
                File.WriteAllText(output+"/"+actor+"-mesh.json",new JObject {
                    ["vertices"]=new JArray(mesh.vertices.Select(v=>new JArray(v.x,v.y,v.z))),
                    ["normals"]=new JArray(mesh.normals.Select(v=>new JArray(v.x,v.y,v.z))),
                    ["tangents"]=new JArray(mesh.tangents.Select(v=>new JArray(v.x,v.y,v.z,v.w))),
                    ["uv"]=new JArray(mesh.uv.Select(v=>new JArray(v.x,v.y))),
                    ["triangles"]=new JArray(mesh.triangles)
                }.ToString(Newtonsoft.Json.Formatting.None));
                var rows=new JArray(); var files=new Dictionary<string,string>();
                foreach(var path in Directory.GetFiles(root,"*.mat").OrderBy(p=>p))
                {
                    var mat=AssetDatabase.LoadAssetAtPath<Material>(path.Replace('\\','/'));
                    var tex=mat.HasProperty("_BumpMap")?mat.GetTexture("_BumpMap") as Texture2D:null;
                    var enabled=mat.IsKeywordEnabled("_NORMALMAP");
                    var scale=mat.HasProperty("_BumpScale")?mat.GetFloat("_BumpScale"):1f;
                    var row=new JObject { ["slot"]=Path.GetFileNameWithoutExtension(path),["material"]=path.Replace('\\','/'),["enabled"]=enabled,["scale"]=scale };
                    if(tex!=null && enabled)
                    {
                        var texturePath=AssetDatabase.GetAssetPath(tex); var key=texturePath+"|"+scale.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
                        if(!files.TryGetValue(key,out var file))
                        {
                            file=actor+"_"+Path.GetFileNameWithoutExtension(path)+".exr";
                            var rt=RenderTexture.GetTemporary(tex.width,tex.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                            var previous=RenderTexture.active; var pixels=new Texture2D(tex.width,tex.height,TextureFormat.RGBAFloat,false,true);
                            try
                            {
                                decoder.SetFloat("_Scale",scale); Graphics.Blit(tex,rt,decoder); RenderTexture.active=rt;
                                pixels.ReadPixels(new Rect(0,0,tex.width,tex.height),0,0); pixels.Apply();
                                File.WriteAllBytes(output+"/"+file,pixels.EncodeToEXR(Texture2D.EXRFlags.CompressZIP));
                            }
                            finally { RenderTexture.active=previous; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(pixels); }
                            files.Add(key,file);
                        }
                        var importer=(TextureImporter)AssetImporter.GetAtPath(texturePath);
                        row["image"]=file; row["texture"]=texturePath; row["width"]=tex.width; row["height"]=tex.height;
                        row["materialSha256"]=HashFile(path); row["textureSha256"]=HashFile(texturePath); row["textureMetaSha256"]=HashFile(texturePath+".meta");
                        row["convertHeight"]=importer.convertToNormalmap; row["heightScale"]=importer.heightmapScale;
                        row["uvScale"]=new JArray(mat.GetTextureScale("_BumpMap").x,mat.GetTextureScale("_BumpMap").y);
                        row["uvOffset"]=new JArray(mat.GetTextureOffset("_BumpMap").x,mat.GetTextureOffset("_BumpMap").y);
                    }
                    rows.Add(row);
                }
                report.Add(new JObject { ["actor"]=actor,["mesh"]=mesh.name,["vertices"]=mesh.vertexCount,["tangents"]=mesh.tangents.Length,["normals"]=mesh.normals.Length,["materials"]=rows });
                Debug.Log("[PeopleSkinNormals] "+actor+" vertices="+mesh.vertexCount+" tangents="+mesh.tangents.Length+" exported="+files.Count);
            }
            File.WriteAllText(output+"/source.json",report.ToString());
        }
        finally { ShaderUtil.allowAsyncCompilation=oldAsync; Object.DestroyImmediate(decoder); }
    }
    private static string HashFile(string path)
    {
        using var sha=System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
    }
}
}
#endif
