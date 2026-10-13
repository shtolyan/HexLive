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

public static class MsiSkinNormalSourceAudit
{
    public static void CaptureSkinPrototype()
    {
        var candidateFile="Build/MSIVisualAudit/SkinNormals/candidate.txt";
        var candidate=File.Exists(candidateFile)?File.ReadAllText(candidateFile).Trim():"Prototype";
        var output="Build/MSIVisualAudit/SkinNormals/Preview-"+candidate;
        Directory.CreateDirectory(output);
        var render=typeof(MsiPeopleVisualAudit).GetMethod("Render",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
        foreach(var name in new[]{ActorName.Marta,ActorName.Kshishtof})
        foreach(var legacy in new[]{false,true})
        {
            var path=legacy?(name==ActorName.Marta?"Assets/ImportedActors/Daz3D/Martanaked/Martanaked.fbx":"Assets/ImportedActors/Daz3D/Kshishtof/Kshishtof.fbx"):
                "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab";
            var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            var materials=new List<Material>(); Texture2D normal=null;
            try
            {
                foreach(var animator in actor.GetComponentsInChildren<Animator>())animator.enabled=false;
                SkinnedMeshRenderer body;
                if(legacy)body=actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderByDescending(r=>r.sharedMesh.vertexCount).First();
                else
                {
                    actor.GetComponent<BodyBones>().Construct(name,432);
                    if(!ActorBodyResolver.TryResolve(actor,out body,out var error))throw new InvalidOperationException(error);
                    var mesh=body.sharedMesh;
                    File.WriteAllText(output+"/"+name+"-mesh.json",new JObject{
                        ["vertices"]=new JArray(mesh.vertices.Select(v=>new JArray(v.x,v.y,v.z))),
                        ["normals"]=new JArray(mesh.normals.Select(v=>new JArray(v.x,v.y,v.z))),
                        ["tangents"]=new JArray(mesh.tangents.Select(v=>new JArray(v.x,v.y,v.z,v.w))),
                        ["uv"]=new JArray(mesh.uv.Select(v=>new JArray(v.x,v.y))),
                        ["triangles"]=new JArray(mesh.triangles)}.ToString(Newtonsoft.Json.Formatting.None));
                    if(candidate=="Installed")
                    {
                        normal=body.sharedMaterials[0].GetTexture("_BumpMap") as Texture2D;
                        if(normal==null)throw new InvalidOperationException("Installed skin has no normal map");
                    }
                    else
                    {
                    const string normalRoot="Assets/Editor/MSIVisualAuditNormals";
                    Directory.CreateDirectory(normalRoot);
                    var normalPath=normalRoot+"/"+name+"_skin_n.png";
                    File.Copy("Build/MSIVisualAudit/SkinNormals/"+candidate+"/"+name+"_skin_n.png",normalPath,true);
                    AssetDatabase.ImportAsset(normalPath,ImportAssetOptions.ForceSynchronousImport);
                    var importer=(TextureImporter)AssetImporter.GetAtPath(normalPath);
                    importer.textureType=TextureImporterType.NormalMap;importer.sRGBTexture=false;
                    importer.mipmapEnabled=true;importer.maxTextureSize=1024;importer.wrapMode=TextureWrapMode.Clamp;
                    importer.textureCompression=TextureImporterCompression.Compressed;importer.SaveAndReimport();
                    normal=AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                    }
                }
                Debug.Log("[MSISkinPrototype] "+name+" legacy="+legacy+" tangents="+body.sharedMesh.tangents.Length+" bounds="+body.bounds);
                var original=body.sharedMaterials;
                if(legacy)
                {
                    var materialRoot=name==ActorName.Marta?"Assets/ImportedActors/Daz3D/Martanaked/Genesis3Female":"Assets/ImportedActors/Daz3D/Kshishtof/Genesis3Male";
                    original=original.Select(m=>AssetDatabase.LoadAssetAtPath<Material>(materialRoot+"/"+m.name+".mat")??throw new InvalidOperationException("Missing canonical reference material "+m.name)).ToArray();
                }
                var clones=original.Select(m=>new Material(m)).ToArray();materials.AddRange(clones);body.sharedMaterials=clones;
                foreach(var m in clones){m.SetTexture("_BaseMap",Texture2D.whiteTexture);m.SetColor("_BaseColor",new Color(.6f,.6f,.6f));m.SetFloat("_Smoothness",.2f);m.SetFloat("_Metallic",0);}
                if(!legacy){clones[0].SetTexture("_BumpMap",normal);clones[0].SetFloat("_BumpScale",1);}
                foreach(var enabled in new[]{false,true})
                {
                    for(var i=0;i<clones.Length;i++)
                    {
                        if(enabled && (legacy?original[i].IsKeywordEnabled("_NORMALMAP"):i==0))clones[i].EnableKeyword("_NORMALMAP");
                        else clones[i].DisableKeyword("_NORMALMAP");
                    }
                    // Runtime bounds include the entire locomotion envelope.
                    // Frame the actual baked geometry, not that padded cube.
                    var baked=new Mesh();body.BakeMesh(baked,true);
                    var points=baked.vertices.Select(body.transform.TransformPoint).ToArray();
                    var b=new Bounds(points[0],Vector3.zero);foreach(var point in points)b.Encapsulate(point);
                    Object.DestroyImmediate(baked);var height=b.size.y;
                    var regions=new[]{
                        ("body",new Bounds(b.center,new Vector3(1.4f,height,.6f))),
                        ("head",new Bounds(new Vector3(b.center.x,b.max.y-.16f,b.center.z),new Vector3(.4f,.4f,.35f))),
                        ("torso",new Bounds(new Vector3(b.center.x,b.min.y+height*.69f,b.center.z),new Vector3(.65f,.65f,.4f))),
                        ("hand",new Bounds(new Vector3(b.max.x-.1f,b.min.y+height*.8f,b.center.z),new Vector3(.3f,.3f,.3f)))};
                    foreach(var (region,bounds) in regions)
                    foreach(var angle in new[]{90f,180f,270f})
                        render.Invoke(null,new object[]{actor,bounds,angle,output+"/"+name+"-"+(legacy?"source":"target")+"-"+(enabled?"normal":"flat")+"-"+region+"-"+angle+".png"});
                }
            }
            finally{Object.DestroyImmediate(actor);foreach(var m in materials)Object.DestroyImmediate(m);if(normal!=null && !EditorUtility.IsPersistent(normal))Object.DestroyImmediate(normal);}
        }
    }

    public static void RepairTangentsAndCompare()
    {
        const string output="Build/MSIVisualAudit/SkinNormals/TangentAudit";
        Directory.CreateDirectory(output);
        var rows=new JArray();
        foreach(var name in new[]{ActorName.Marta,ActorName.Kshishtof})
        {
            
            var path="Assets/HexLiveContent/People/Source/Models/"+name+".fbx";
            var importer=(ModelImporter)AssetImporter.GetAtPath(path);
            importer.importTangents=ModelImporterTangents.None; importer.SaveAndReimport();
            var before=CaptureResponse(name,"before");
            var after=CaptureResponse(name,"after");
            if((int)before["vertices"]!=(int)after["vertices"] || (string)before["geometryHash"]!=(string)after["geometryHash"])
                throw new InvalidOperationException("Tangent import altered paint-map geometry "+name);
            if((int)after["tangents"]!=(int)after["vertices"])
                throw new InvalidOperationException("Missing generated tangents "+name);
            rows.Add(new JObject{["actor"]=name.ToString(),["before"]=before,["after"]=after});
        }
        File.WriteAllText(output+"/mesh-check.json",rows.ToString());
    }

    private static JObject CaptureResponse(ActorName name,string phase)
    {
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        Material mat=null; Texture2D normal=null;
        try
        {
            actor.GetComponent<BodyBones>().Construct(name,432);
            if(!ActorBodyResolver.TryResolve(actor,out var body,out var error))throw new InvalidOperationException(error);
            var mesh=Object.Instantiate(body.sharedMesh); body.sharedMesh=mesh;
            if(phase=="before") mesh.tangents=Array.Empty<Vector4>();
            using var bytes=new MemoryStream(); using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
            {
                foreach(var v in mesh.vertices){writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);}
                foreach(var uv in mesh.uv){writer.Write(uv.x);writer.Write(uv.y);}
                foreach(var index in mesh.triangles)writer.Write(index);
            }
            var hash=BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(bytes.ToArray())).Replace("-","");
            mat=new Material(body.sharedMaterial); body.sharedMaterial=mat;
            mat.SetTexture("_BaseMap",Texture2D.whiteTexture);mat.SetColor("_BaseColor",new Color(.6f,.6f,.6f));
            mat.SetFloat("_BumpScale",1);mat.SetFloat("_Smoothness",.2f);mat.SetFloat("_Metallic",0);mat.EnableKeyword("_NORMALMAP");
            normal=new Texture2D(1,1,TextureFormat.RGBA32,false,true);mat.SetTexture("_BumpMap",normal);
            var render=typeof(MsiPeopleVisualAudit).GetMethod("Render",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
            foreach(var tilted in new[]{false,true})
            {
                // Opposite X, identical Z: isolates the tangent response from
                // a normal-length change in the broken zero-tangent baseline.
                normal.SetPixel(0,0,tilted?new Color(.8f,.5f,.9f,1):new Color(.2f,.5f,.9f,1));normal.Apply();
                render.Invoke(null,new object[]{actor,new Bounds(actor.transform.position+Vector3.up*.95f,new Vector3(1.4f,1.9f,.6f)),0f,"Build/MSIVisualAudit/SkinNormals/TangentAudit/"+name+"-"+phase+"-"+(tilted?"tilted":"flat")+".png"});
            }
            return new JObject{["vertices"]=mesh.vertexCount,["tangents"]=mesh.tangents.Length,["geometryHash"]=hash};
        }
        finally {Object.DestroyImmediate(actor);if(mat!=null)Object.DestroyImmediate(mat);if(normal!=null)Object.DestroyImmediate(normal);}
    }

    public static void Export()
    {
        const string output="Build/MSIVisualAudit/SkinNormals/Source";
        Directory.CreateDirectory(output);
        var report=new JArray();
        var decoder=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/MsiSourceNormalDecode.shader"));
        var oldAsync=ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation=false;
        try
        {
            foreach(var actor in new[]{"Marta","Kshishtof"})
            {
                var root=actor=="Marta"?"Assets/ImportedActors/Daz3D/Martanaked/Genesis3Female":"Assets/ImportedActors/Daz3D/Kshishtof/Genesis3Male";
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/"+actor+".prefab");
                if(!ActorBodyResolver.TryResolve(prefab,out var body,out var error))throw new InvalidOperationException(error);
                var mesh=body.sharedMesh;
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
                        row["convertHeight"]=importer.convertToNormalmap; row["heightScale"]=importer.heightmapScale;
                        row["uvScale"]=new JArray(mat.GetTextureScale("_BumpMap").x,mat.GetTextureScale("_BumpMap").y);
                        row["uvOffset"]=new JArray(mat.GetTextureOffset("_BumpMap").x,mat.GetTextureOffset("_BumpMap").y);
                    }
                    rows.Add(row);
                }
                report.Add(new JObject { ["actor"]=actor,["mesh"]=mesh.name,["vertices"]=mesh.vertexCount,["tangents"]=mesh.tangents.Length,["normals"]=mesh.normals.Length,["materials"]=rows });
                Debug.Log("[MSISkinNormals] "+actor+" vertices="+mesh.vertexCount+" tangents="+mesh.tangents.Length+" exported="+files.Count);
            }
            File.WriteAllText(output+"/source.json",report.ToString());
        }
        finally { ShaderUtil.allowAsyncCompilation=oldAsync; Object.DestroyImmediate(decoder); }
    }
}
#endif
