#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

public static class MsiHairSourceUvAudit
{
    private static bool cleanMaterials;
    public static void CaptureCapCandidates()
    {
        var path="Build/MSIVisualAudit/HairSourceUV/candidate.txt";
        var previous=File.ReadAllText(path);
        try
        {
            foreach(var candidate in File.ReadAllLines("Build/MSIVisualAudit/HairSourceUV/cap-candidate-list.txt"))
            {
                File.WriteAllText(path,candidate);Capture();
            }
        }
        finally { File.WriteAllText(path,previous); }
    }
    public static void CaptureCleanMaterials()
    {
        var path="Build/MSIVisualAudit/HairSourceUV/candidate.txt";
        var previous=File.ReadAllText(path);cleanMaterials=true;
        try
        {
            foreach(var style in new[]{"EilisHair","Hair07"})
            {
                File.WriteAllText(path,style+"AllLayersLOD0");Capture();
            }
        }
        finally { cleanMaterials=false;File.WriteAllText(path,previous); }
    }
    public static void CaptureAllLayers()
    {
        var path="Build/MSIVisualAudit/HairSourceUV/candidate.txt";
        var previous=File.ReadAllText(path);
        try
        {
            foreach(var candidate in File.ReadAllLines("Build/MSIVisualAudit/HairSourceUV/all-layer-candidate-list.txt"))
            {
                if(string.IsNullOrWhiteSpace(candidate))continue;
                if(new[]{0,90,180,270}.All(angle=>File.Exists("Build/MSIVisualAudit/HairSourceUV/"+candidate+"-SingleLOD-source-uv-"+angle+".png")))continue;
                File.WriteAllText(path,candidate);Capture();
                GC.Collect();
            }
        }
        finally { File.WriteAllText(path,previous); }
    }
    public static void CaptureFarCandidates()
    {
        CapturePair("FarCandidate");
    }
    public static void CaptureFullReferences()
    {
        CapturePair("Reference");
    }
    public static void CaptureBetterFar()
    {
        var path="Build/MSIVisualAudit/HairSourceUV/candidate.txt";
        var previous=File.ReadAllText(path);
        try { File.WriteAllText(path,"TootsieRollHairFar1800Candidate");Capture(); }
        finally { File.WriteAllText(path,previous); }
    }
    public static void CaptureFullBudgets()
    {
        var path="Build/MSIVisualAudit/HairSourceUV/candidate.txt";
        var previous=File.ReadAllText(path);
        try
        {
            var budgets=File.Exists("Build/MSIVisualAudit/HairSourceUV/budgets.txt")
                ?File.ReadAllText("Build/MSIVisualAudit/HairSourceUV/budgets.txt").Split(',').Select(int.Parse).ToArray()
                :new[]{8000,4000};
            foreach(var budget in budgets)
            {
                File.WriteAllText(path,"TootsieRollHairFull"+budget);Capture();
            }
        }
        finally { File.WriteAllText(path,previous); }
    }
    private static void CapturePair(string suffix)
    {
        var path="Build/MSIVisualAudit/HairSourceUV/candidate.txt";
        var previous=File.Exists(path)?File.ReadAllText(path):null;
        try
        {
            var styles=File.Exists("Build/MSIVisualAudit/HairSourceUV/reference-styles.txt")
                ?File.ReadAllLines("Build/MSIVisualAudit/HairSourceUV/reference-styles.txt"):new[]{"Hair07","TootsieRollHair"};
            foreach(var style in styles)
            {
                File.WriteAllText(path,style+suffix);Capture();
            }
        }
        finally { if(previous!=null)File.WriteAllText(path,previous); }
    }
    public static void Capture()
    {
        const string output="Build/MSIVisualAudit/HairSourceUV";
        const string root="Assets/HexLiveContent/People";
        var candidate=File.Exists(output+"/candidate.txt")?File.ReadAllText(output+"/candidate.txt").Trim():"AdellHair";
        var data=JObject.Parse(File.ReadAllText(output+"/"+candidate+".json"));
        var style=(string)data["name"];
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(root+"/Prefabs/Actors/Marta.prefab"));
        var created=new List<Object>();
        try
        {
            actor.GetComponent<Animator>().enabled=false;
            var bones=actor.GetComponent<BodyBones>();bones.Construct(ActorName.Marta,123);
            bones.SetHair(AssetDatabase.LoadAssetAtPath<GameObject>(root+"/Prefabs/Hair/"+style+".prefab").GetComponent<Wear>());
            foreach(var lod in actor.GetComponentsInChildren<LODGroup>(true))lod.enabled=false;
            var hair=actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r=>r.name==style+"_LOD0");
            foreach(var renderer in bones.HairInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                renderer.enabled=renderer==hair;
            hair.enabled=true;
            if(bones.HairInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(r=>r.enabled)!=1)
                throw new InvalidOperationException("Preview must render exactly one hair LOD");
            var targetBones=hair.bones.ToList();
            var bindposes=hair.sharedMesh.bindposes.ToList();
            var lookup=targetBones.Select((b,i)=>(b.name,i)).ToDictionary(x=>x.name,x=>x.i);
            var bodyRenderer=actor.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .First(r=>r.sharedMesh!=null&&r.sharedMesh.blendShapeCount>=109);
            var actorBones=bodyRenderer.bones.ToDictionary(b=>b.name,b=>b);
            var required=data["weights"].SelectMany(w=>w).Select(w=>(string)w[0]).Distinct();
            foreach(var name in required)
            {
                if(lookup.ContainsKey(name))continue;
                if(!actorBones.TryGetValue(name,out var bone))throw new InvalidOperationException("Source bone missing from actor: "+name);
                lookup.Add(name,targetBones.Count);targetBones.Add(bone);
                bindposes.Add(bone.worldToLocalMatrix*hair.transform.localToWorldMatrix);
                Debug.Log("[MSIHairUV] retained additional actor bone "+name+" for "+candidate);
            }
            var vertices=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var weights=new List<BoneWeight>();
            var triangles=((JArray)data["materials"]).Select(_=>new List<int>()).ToArray();
            foreach(var face in data["faces"])
            foreach(var corner in new[]{2,1,0})
            {
                var vi=(int)face[0][corner];var p=data["vertices"][vi];var n=face[2][corner];var t=face[1][corner];
                vertices.Add(new Vector3(-(float)p[0],(float)p[1],(float)p[2]));
                normals.Add(new Vector3(-(float)n[0],(float)n[1],(float)n[2]));uv.Add(new Vector2((float)t[0],(float)t[1]));
                var entries=data["weights"][vi].ToArray();var indices=new int[4];var values=new float[4];
                if(entries.Length>4)throw new InvalidOperationException("Too many weights");
                for(var i=0;i<entries.Length;i++){indices[i]=lookup[(string)entries[i][0]];values[i]=(float)entries[i][1];}
                weights.Add(new BoneWeight{boneIndex0=indices[0],boneIndex1=indices[1],boneIndex2=indices[2],boneIndex3=indices[3],weight0=values[0],weight1=values[1],weight2=values[2],weight3=values[3]});
                triangles[(int)face[3]].Add(vertices.Count-1);
            }
            var mesh=new Mesh{name=style+"OriginalUV",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};created.Add(mesh);mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetUVs(0,uv);
            mesh.boneWeights=weights.ToArray();mesh.bindposes=bindposes.ToArray();mesh.subMeshCount=triangles.Length;
            for(var i=0;i<triangles.Length;i++)mesh.SetTriangles(triangles[i],i);
            mesh.RecalculateTangents();mesh.RecalculateBounds();hair.sharedMesh=mesh;hair.bones=targetBones.ToArray();
            var materials=new List<Material>();
            foreach(var row in data["materials"])
            {
                var mat=cleanMaterials?new Material(Shader.Find("Universal Render Pipeline/Lit")):new Material(hair.sharedMaterial);
                if(cleanMaterials)mat.SetFloat("_Smoothness",.08f);
                created.Add(mat);var color=row["color"];
                mat.name=(string)row["name"];
                mat.SetColor("_BaseColor",new Color((float)color["r"],(float)color["g"],(float)color["b"],1));
                var texturePath=(string)row["texture"];
                var texture=string.IsNullOrEmpty(texturePath)?Texture2D.whiteTexture:AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if(texture==null)throw new InvalidOperationException("Missing source texture: "+texturePath);
                mat.SetTexture("_BaseMap",texture);
                mat.SetTextureScale("_BaseMap",new Vector2((float)row["uv_scale"]["x"],(float)row["uv_scale"]["y"]));
                mat.SetTextureOffset("_BaseMap",new Vector2((float)row["uv_offset"]["x"],(float)row["uv_offset"]["y"]));
                var cutoff=(float?)row["alphaCutoff"]??(style=="BendineHair"?.12f:style=="ChunkyHair"?.08f:.42f);
                mat.SetFloat("_Cull",0);mat.SetFloat("_AlphaClip",1);mat.SetFloat("_Cutoff",cutoff);mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetOverrideTag("RenderType","TransparentCutout");mat.renderQueue=2450;materials.Add(mat);
            }
            hair.sharedMaterials=materials.ToArray();
            if(cleanMaterials)
            {
                hair.SetPropertyBlock(null);
                for(var slot=0;slot<materials.Count;slot++)hair.SetPropertyBlock(null,slot);
            }
            var head=actor.GetComponentsInChildren<Transform>(true).First(t=>t.name=="head");
            var bounds=new Bounds(head.position+Vector3.up*.06f,new Vector3(.5f,.65f,.5f));
            var render=typeof(MsiPeopleVisualAudit).GetMethod("Render",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
            foreach(var angle in new[]{0f,90f,180f,270f})render.Invoke(null,new object[]{actor,bounds,angle,output+"/"+candidate+(cleanMaterials?"-CleanMaterial":"")+"-SingleLOD-source-uv-"+angle+".png"});
            Debug.Log("[MSIHairUV] candidate="+candidate+" vertices="+vertices.Count+" triangles="+vertices.Count/3+" materialCount="+materials.Count);
        }
        finally{Object.DestroyImmediate(actor);foreach(var item in created)Object.DestroyImmediate(item);}
    }
}
#endif
