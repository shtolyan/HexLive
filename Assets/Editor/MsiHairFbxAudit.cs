#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class MsiHairFbxAudit
{
    const string Scratch = "Assets/Editor/MSIHairImportAudit";
    const string Stage = "Build/MSIVisualAudit/HairExport";
    const string Inputs = "Build/MSIVisualAudit/HairSourceUV";
    const float Cell = .0001f;
    static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x/Cell), Mathf.FloorToInt(p.y/Cell), Mathf.FloorToInt(p.z/Cell));
    public static void Run() => Run(false);
    public static void RunProduction() => Run(true);
    private static void Run(bool production)
    {
        Directory.CreateDirectory(Scratch);
        var selected = JObject.Parse(File.ReadAllText(Inputs+"/selected-integration-inputs.json"));
        var manifest = JObject.Parse(File.ReadAllText(Stage+"/hair-source-manifest.json"));
        var results = new JArray();
        foreach(var row in manifest["models"])
        {
            string id=(string)row["id"], path=(production?"Assets/HexLiveContent/People/Source/Models":Scratch)+"/"+id+".fbx";
            if(!production)
            {
            File.Copy(Stage+"/"+(string)row["path"],path,true);
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer=(ModelImporter)AssetImporter.GetAtPath(path);
            importer.importAnimation=false; importer.importCameras=false; importer.importLights=false;
            importer.materialImportMode=ModelImporterMaterialImportMode.None;
            importer.meshCompression=ModelImporterMeshCompression.Off; importer.isReadable=true;
            importer.optimizeGameObjects=false; importer.optimizeBones=true;
            importer.skinWeights=ModelImporterSkinWeights.Custom; importer.maxBonesPerVertex=32; importer.minBoneWeight=.000001f;
            importer.importNormals=ModelImporterNormals.Import; importer.importTangents=ModelImporterTangents.None;
            importer.animationType=ModelImporterAnimationType.Generic; importer.SaveAndReimport();
            }
            var input=selected["models"].Single(r=>((string)r["style"]+"_LOD"+(int)r["lod"])==id);
            var source=JObject.Parse(File.ReadAllText(Inputs+"/"+(string)input["candidate"]+".json"));
            var vertices=source["vertices"].Select(p=>new Vector3(-(float)p[0],(float)p[1],(float)p[2])*.01f).ToArray();
            var cells=new Dictionary<Vector3Int,List<int>>();
            for(int i=0;i<vertices.Length;i++)
            { var k=Key(vertices[i]); if(!cells.TryGetValue(k,out var list))cells[k]=list=new List<int>(); list.Add(i); }
            var uvs=Enumerable.Range(0,vertices.Length).Select(_=>new List<Vector2>()).ToArray();
            foreach(var face in source["faces"])
                for(int c=0;c<3;c++)uvs[(int)face[0][c]].Add(new Vector2((float)face[1][c][0],(float)face[1][c][1]));
            var expectedWeights=source["weights"].Select(w=>w.ToDictionary(x=>(string)x[0],x=>(float)x[1])).ToArray();
            var go=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            try
            {
                var renderers=go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if(renderers.Length!=1)throw new Exception(id+": renderer count "+renderers.Length);
                var renderer=renderers[0]; var mesh=renderer.sharedMesh;
                if(mesh.triangles.Length/3!=(int)input["triangles"])throw new Exception(id+": triangle count changed");
                var usedSlots=source["faces"].Select(f=>(int)f[3]).Distinct().OrderBy(i=>i).ToArray();
                if(mesh.subMeshCount!=usedSlots.Length)throw new Exception(id+": material slots changed");
                for(int slot=0;slot<usedSlots.Length;slot++)
                    if(mesh.GetTriangles(slot).Length/3!=source["faces"].Count(f=>(int)f[3]==usedSlots[slot]))
                        throw new Exception(id+": material triangle allocation changed "+slot);
                var actualVertices=mesh.vertices; var actualUvs=mesh.uv;
                // Diagnostic restoration from the pinned authored source. This
                // affects this instantiated audit mesh only, not the FBX asset.
                if(!production && File.Exists(Stage+"/restore-authored-weights.txt"))
                {
                    mesh=UnityEngine.Object.Instantiate(mesh); renderer.sharedMesh=mesh;
                    var boneList=renderer.bones.ToList(); var bindposes=mesh.bindposes.ToList();
                    var hierarchy=go.GetComponentsInChildren<Transform>(true).GroupBy(t=>t.name).ToDictionary(g=>g.Key,g=>g.First());
                    foreach(string bone in expectedWeights.SelectMany(w=>w.Keys).Distinct())
                        if(!boneList.Any(b=>b.name==bone))
                        {
                            if(!hierarchy.TryGetValue(bone,out var t))throw new Exception(id+": missing authored bone "+bone);
                            boneList.Add(t);bindposes.Add(t.worldToLocalMatrix*renderer.transform.localToWorldMatrix);
                        }
                    var boneIndices=boneList.Select((b,i)=>(b.name,i)).ToDictionary(p=>p.name,p=>p.i);
                    var restoredCounts=new byte[mesh.vertexCount]; var restoredWeights=new List<BoneWeight1>();
                    for(int i=0;i<mesh.vertexCount;i++)
                    {
                        var p=renderer.transform.TransformPoint(actualVertices[i]);var k=Key(p);int match=-1;
                        for(int x=-1;x<=1&&match<0;x++)for(int y=-1;y<=1&&match<0;y++)for(int z=-1;z<=1&&match<0;z++)
                        {
                            if(!cells.TryGetValue(k+new Vector3Int(x,y,z),out var candidates))continue;
                            foreach(int v in candidates)
                                if(Vector3.Distance(p,vertices[v])<.00002f&&uvs[v].Any(uv=>Vector2.Distance(uv,actualUvs[i])<.00002f))
                                {match=v;break;}
                        }
                        if(match<0)throw new Exception(id+": cannot restore source vertex "+i);
                        var sourceWeights=expectedWeights[match].OrderByDescending(w=>w.Value).ToArray();
                        restoredCounts[i]=(byte)sourceWeights.Length;
                        restoredWeights.AddRange(sourceWeights.Select(w=>new BoneWeight1{boneIndex=boneIndices[w.Key],weight=w.Value}));
                    }
                    renderer.bones=boneList.ToArray();mesh.bindposes=bindposes.ToArray();
                    using(var c=new Unity.Collections.NativeArray<byte>(restoredCounts,Unity.Collections.Allocator.Temp))
                    using(var w=new Unity.Collections.NativeArray<BoneWeight1>(restoredWeights.ToArray(),Unity.Collections.Allocator.Temp))mesh.SetBoneWeights(c,w);
                }
                var counts=mesh.GetBonesPerVertex(); var weights=mesh.GetAllBoneWeights(); int offset=0;
                float maxPosition=0,maxWeight=0; int unmatched=0;
                var failures=new JArray();
                for(int i=0;i<actualVertices.Length;i++)
                {
                    var p=renderer.transform.TransformPoint(actualVertices[i]); var key=Key(p);
                    var actual=new Dictionary<string,float>();
                    for(int j=0;j<counts[i];j++)
                    { var w=weights[offset++]; string b=renderer.bones[w.boneIndex].name; actual[b]=actual.TryGetValue(b,out var n)?n+w.weight:w.weight; }
                    bool found=false;
                    for(int x=-1;x<=1&&!found;x++)for(int y=-1;y<=1&&!found;y++)for(int z=-1;z<=1&&!found;z++)
                    {
                        if(!cells.TryGetValue(key+new Vector3Int(x,y,z),out var candidates))continue;
                        foreach(int v in candidates)
                        {
                            float distance=Vector3.Distance(p,vertices[v]);
                            if(distance>.00002f || !uvs[v].Any(uv=>Vector2.Distance(uv,actualUvs[i])<.00002f))continue;
                            var expected=expectedWeights[v];
                            float difference=actual.Keys.Union(expected.Keys).Max(b=>Mathf.Abs((actual.TryGetValue(b,out var a)?a:0)-(expected.TryGetValue(b,out var e)?e:0)));
                            if(difference>.0001f)continue;
                            maxPosition=Mathf.Max(maxPosition,distance);maxWeight=Mathf.Max(maxWeight,difference);found=true;break;
                        }
                    }
                    if(!found)
                    {
                        unmatched++;
                        if(failures.Count<8)
                        {
                            int near=Enumerable.Range(0,vertices.Length).OrderBy(v=>(p-vertices[v]).sqrMagnitude).First();
                            failures.Add(new JObject{["vertex"]=i,["position"]=p.ToString("F7"),
                                ["nearestDistance"]=Vector3.Distance(p,vertices[near]),
                                ["uv"]=actualUvs[i].ToString("F7"),["sourceUv"]=new JArray(uvs[near].Select(v=>v.ToString("F7"))),
                                ["weights"]=JObject.FromObject(actual),["sourceWeights"]=JObject.FromObject(expectedWeights[near])});
                        }
                    }
                }
                var entry=new JObject{["id"]=id,["vertices"]=mesh.vertexCount,["triangles"]=mesh.triangles.Length/3,
                    ["unmatchedVertices"]=unmatched,["maxPositionErrorMetres"]=maxPosition,["maxWeightError"]=maxWeight,
                    ["bones"]=new JArray(renderer.bones.Select(b=>b.name)),["bounds"]=renderer.bounds.ToString(),["failures"]=failures,
                    ["authoredWeightsRestored"]=!production && File.Exists(Stage+"/restore-authored-weights.txt"),["productionImport"]=production};
                results.Add(entry);
                File.WriteAllText(Stage+(production?"/unity-production-fbx-audit.json":"/unity-fbx-audit.json"),new JObject{["passed"]=false,["models"]=results}.ToString());
                if(unmatched!=0)throw new Exception(id+": "+unmatched+" vertices fail position/UV/weight parity");
                Debug.Log("HAIR_FBX_PARITY "+id+" vertices="+mesh.vertexCount+" maxPosition="+maxPosition);
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        File.WriteAllText(Stage+(production?"/unity-production-fbx-audit.json":"/unity-fbx-audit.json"),new JObject{["passed"]=results.Count==32,["models"]=results}.ToString());
    }
}
#endif
