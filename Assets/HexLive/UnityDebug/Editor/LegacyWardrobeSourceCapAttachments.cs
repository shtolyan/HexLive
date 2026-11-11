#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string SourceCapManifestPath=PreparedRoot+"/cap-attachments.json";
    static bool SourceCapTopologyProofAccepted(JToken token)
    {
        if(!(token is JObject topology))return false;
        if(topology["sourceCapAnnulus"]==null)return true;
        if(!(topology["sourceCapAnnulus"] is JObject cap))return false;
        var geometry=cap["geometry"];var attributes=cap["attributes"];
        if(!(geometry is JObject)||!(attributes is JObject))return false;
        int? original=(int?)cap["originalSourceTriangles"],clipped=(int?)cap["clippedTriangles"],output=(int?)topology["outputTriangles"];
        if(!(original>0)||!(clipped>0)||!(output>=clipped))return false;
        int budget=Math.Max((int)Math.Ceiling(original.Value*.10),2048);
        return (bool?)cap["applicable"]==true&&(bool?)cap["automaticSemanticDetection"]==false&&(bool?)cap["acceptanceOverride"]==false&&
            (string)cap["sourceEvidenceManifest"]==SourceCapManifestPath&&((string)cap["sourceEvidenceManifestSha"])?.Length==64&&((string)cap["sourceRecordSha"])?.Length==64&&
            !string.IsNullOrEmpty((string)cap["sourceNative"])&&((string)cap["sourceNativeSha"])?.Length==64&&((string)cap["sourceTopologySha"])?.Length==64&&
            (bool?)cap["discardedMarginMayHaveBeenVisible"]==true&&(bool?)cap["requiresActualPostBindAllSampleAndReferenceAndVisualAcceptance"]==true&&
            (int?)topology["inputTriangles"]==clipped&&(int?)topology["inputVertices"]==(int?)cap["clippedVertices"]&&
            (int?)geometry["originalTriangles"]==original&&(int?)geometry["outputTriangles"]==clipped&&
            (bool?)geometry["retainedFanVerticesAnd13SamplesPassed"]==true&&(bool?)geometry["outerRingOriginalPositionsUnchanged"]==true&&
            (bool?)geometry["otherOriginalFacesUnchanged"]==true&&(int?)geometry["originalRetainedPositionsChanged"]==0&&(bool?)geometry["common12cmCapsPassed"]==true&&
            (bool?)attributes["retainedFullSkinFloatBitsExact"]==true&&(bool?)attributes["channelFormatsAndDimensionsExact"]==true&&
            (float?)attributes["maximumDroppedWeightFraction"]>=0&&(float?)attributes["maximumDroppedWeightFraction"]<=.05f&&
            (int?)cap["canonicalOriginalAdditionalTriangleBudget"]==budget&&(int?)topology["aggregateSourceAdditionalTriangleBudget"]==budget&&
            (int?)topology["aggregateSourceAdditionalTriangles"]==output-original&&output-original<=budget;
    }
    static bool SourceCapTransferAccepted(JObject cap,string targetActor,string sourceActor)
    {
        if(cap["recipientTransfer"]==null)return true;
        if(!(cap["recipientTransfer"] is JObject transfer))return false;
        bool original=(string)transfer["ownReferencePolicy"]=="actual cut-retarget target seed before underlayers"&&transfer["targetClearanceSeed"]==null;
        bool clearance=(string)transfer["ownReferencePolicy"]=="actual cut-retarget-clearance target seed before underlayers"&&
            (string)transfer["seedOperator"]=="CreateSourceClearanceSeed on SOURCE-evidenced clipped topology"&&
            transfer["targetClearanceSeed"] is JObject&&(bool?)transfer["targetClearanceSeed"]["passed"]==true;
        return (original||clearance)&&(sourceActor=="Marta"||sourceActor=="Kshishtof")&&
            (targetActor=="Marta"||targetActor=="Kshishtof")&&sourceActor!=targetActor&&
            (string)transfer["sourceActor"]==sourceActor&&(string)transfer["targetActor"]==targetActor&&
            (bool?)transfer["targetClipWasRecomputed"]==false&&
            (string)transfer["sourceClipScope"]=="SOURCE-evidenced donor geometry before target bone retarget and strict repair"&&
            (string)transfer["commonReferencePolicy"]=="source lineage interpolated ORIGINAL UNCUT fresh-bone target reference";
    }
    static void ValidateCurrentSourceCapProof(JObject cap,string targetActor)
    {
        if(FinalFileSHA(SourceCapManifestPath)!=(string)cap["sourceEvidenceManifestSha"])throw new InvalidDataException("Source cap evidence manifest changed");
        var document=JObject.Parse(File.ReadAllText(SourceCapManifestPath));
        var record=document["records"].OfType<JObject>().Single(r=>(string)r["nativeSource"]==(string)cap["sourceNative"]);
        if(FinalJsonSHA(record)!=(string)cap["sourceRecordSha"])throw new InvalidDataException("Source cap authoring record changed");
        ValidateSourceCapRecord(record,true);
        if(!SourceCapTransferAccepted(cap,targetActor,(string)record["actor"]))throw new InvalidDataException("Invalid donor-cap target transfer provenance");
    }
    static string[] SourceCapInputPaths()
    {
        var document=JObject.Parse(File.ReadAllText(SourceCapManifestPath));
        return SourceCapInputPathsFromDocument(document);
    }
    static string[] SourceCapInputPathsFromDocument(JObject document)
    {
        var paths=((JArray)document["records"]).SelectMany(r=>r["evidenceFiles"].Select(f=>(string)f["path"])).ToArray();
        foreach(string path in paths)
            if(string.IsNullOrEmpty(path)||!path.StartsWith(PreparedRoot+"/Evidence/",StringComparison.Ordinal)||path.Contains("..")||path.Contains("\\"))
                throw new InvalidDataException("Source cap evidence input path is outside its owned directory");
        return new[]{SourceCapManifestPath}.Concat(paths)
            .Distinct(StringComparer.Ordinal).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
    }
    static string SourceCapTopologySHA(Mesh mesh)
    {
        using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
        {writer.Write(mesh.subMeshCount);for(int sm=0;sm<mesh.subMeshCount;sm++){int[] indices=mesh.GetTriangles(sm);writer.Write(indices.Length);foreach(int index in indices)writer.Write(index);}}
        return FinalSHA(stream.ToArray());
    }
    static string SourceCapSkinSHA(Mesh mesh)
    {
        using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
        {foreach(byte count in mesh.GetBonesPerVertex().ToArray())writer.Write(count);foreach(var influence in mesh.GetAllBoneWeights().ToArray()){writer.Write(influence.boneIndex);writer.Write(influence.weight);}}
        return FinalSHA(stream.ToArray());
    }
    static string SourceCapPositionsSHA(Vector3[] points)
    {var values=points.SelectMany(p=>new[]{p.x,p.y,p.z}).ToArray();var bytes=new byte[values.Length*4];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);return FinalSHA(bytes);}
    static void ValidateSourceCapRecord(JObject record,bool requireCaptured)
    {
        if(!BitConverter.IsLittleEndian)throw new InvalidDataException("Source cap binary evidence requires little-endian float32");
        foreach(var pair in new[]{("nativeSource","nativeSha"),("nativeMesh","nativeMeshSha"),("materialPath","materialSha")})
            if(FinalFileSHA((string)record[pair.Item1])!=(string)record[pair.Item2])throw new InvalidDataException("Source cap authoring input changed: "+pair.Item1);
        foreach(var file in record["evidenceFiles"])
        {
            string path=(string)file["path"];
            if(path==null||!path.StartsWith(PreparedRoot+"/Evidence/",StringComparison.Ordinal)||path.Contains("..")||FinalFileSHA(path)!=(string)file["sha256"])
                throw new InvalidDataException("Owned source cap evidence changed");
        }
        if(requireCaptured)
        {
            if((bool?)record["requiresFreshUnityDependencyCapture"]!=false||string.IsNullOrEmpty((string)record["nativeDependency"])||
                AssetDatabase.GetAssetDependencyHash((string)record["nativeSource"]).ToString()!=(string)record["nativeDependency"])
                throw new InvalidDataException("Explicit source cap dependency capture is absent or stale");
            string[] expectedBodies={PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"};
            if(!(record["bodyAuthoringDependencies"] is JArray bodies)||bodies.Count!=2||
                !bodies.Select(b=>(string)b["path"]).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(expectedBodies.OrderBy(p=>p,StringComparer.Ordinal)))
                throw new InvalidDataException("Source cap requires exactly the two reviewed body authoring dependencies");
            foreach(var dependency in bodies)
                if(string.IsNullOrEmpty((string)dependency["dependency"])||((string)dependency["sha256"])?.Length!=64||
                    FinalFileSHA((string)dependency["path"])!=(string)dependency["sha256"]||
                    AssetDatabase.GetAssetDependencyHash((string)dependency["path"]).ToString()!=(string)dependency["dependency"])
                    throw new InvalidDataException("Reviewed source-cap body authoring dependency changed");
        }
        var native=AssetDatabase.LoadAssetAtPath<GameObject>((string)record["nativeSource"]);
        var actor=(ActorName)Enum.Parse(typeof(ActorName),(string)record["actor"]);
        var mesh=SourceMesh(native.GetComponent<Wear>(),actor,out _);
        var materials=native.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials;int materialSlot=(int)record["materialSlot"];
        if(materialSlot<0||materialSlot>=materials.Length||AssetDatabase.GetAssetPath(materials[materialSlot])!=(string)record["materialPath"])
            throw new InvalidDataException("Source cap Native material owner changed");
        var actualNative=new JObject{["nativeMesh"]=AssetDatabase.GetAssetPath(mesh),["nativeMeshGuid"]=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)),
            ["vertices"]=mesh.vertexCount,["triangles"]=mesh.triangles.Length/3,["sourceTopologySha256"]=SourceCapTopologySHA(mesh),
            ["fullWeightsSha256"]=SourceCapSkinSHA(mesh),["localPositionsFloat32Sha256"]=SourceCapPositionsSHA(mesh.vertices)};
        var nativeMismatch=new JObject();foreach(var field in actualNative.Properties())if(!JToken.DeepEquals(field.Value,record[field.Name]))
            nativeMismatch[field.Name]=new JObject{["expected"]=record[field.Name]?.DeepClone(),["actual"]=field.Value.DeepClone()};
        if(nativeMismatch.Count!=0)throw new InvalidDataException("Source cap Native topology/skin/positions do not match reviewed evidence: "+nativeMismatch.ToString(Newtonsoft.Json.Formatting.None));
        int[] centers=record["centerIndices"].ToObject<int[]>(),faces=record["capFaceIndices"].ToObject<int[]>();var selected=new HashSet<int>(centers);var triangles=mesh.triangles;
        int[] actual=Enumerable.Range(0,triangles.Length/3).Where(f=>selected.Contains(triangles[f*3])||selected.Contains(triangles[f*3+1])||selected.Contains(triangles[f*3+2])).ToArray();
        if(!actual.OrderBy(x=>x).SequenceEqual(faces.OrderBy(x=>x)))throw new InvalidDataException("Source cap evidence face membership changed");
        var counts=mesh.GetBonesPerVertex().ToArray();var influences=mesh.GetAllBoneWeights().ToArray();var offsets=new int[counts.Length];int cursor=0;
        for(int i=0;i<counts.Length;i++){offsets[i]=cursor;cursor+=counts[i];}
        foreach(var group in record["groups"])
        {
            string canonical=null;
            foreach(int index in group["centerIndices"].Values<int>())
            {
                using var bytes=new MemoryStream();using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
                {foreach(var w in influences.Skip(offsets[index]).Take(counts[index]).OrderBy(w=>w.boneIndex)){writer.Write(w.boneIndex);writer.Write(w.weight);}}
                string hash=FinalSHA(bytes.ToArray());if(hash!=(string)group["canonicalInfluenceBytesSha256"]||canonical!=null&&hash!=canonical)
                    throw new InvalidDataException("Source cap attribute copies have different full influences");canonical=hash;
            }
        }

    }
    // Explicit authoring capture only. Prepare/validation never repairs or
    // silently refreshes a stale source admission record.
    public static void CaptureSourceCapEvidenceDependencies()
    {
        var document=JObject.Parse(File.ReadAllText(SourceCapManifestPath));bool changed=false;
        if((int?)document["schemaVersion"]!=1||(bool?)document["automaticSemanticDetection"]!=false)throw new InvalidDataException("Invalid source cap evidence schema");
        foreach(JObject record in document["records"])
        {
            ValidateSourceCapRecord(record,false);
            if((bool?)record["requiresFreshUnityDependencyCapture"]!=true){ValidateSourceCapRecord(record,true);continue;}
            record["nativeDependency"]=AssetDatabase.GetAssetDependencyHash((string)record["nativeSource"]).ToString();
            record["bodyAuthoringDependencies"]=new JArray(new[]{"Marta","Kshishtof"}.Select(actor=>
            {string path=PeopleRoot+"/Prefabs/Actors/"+actor+".prefab";return new JObject{["path"]=path,["dependency"]=AssetDatabase.GetAssetDependencyHash(path).ToString(),["sha256"]=FinalFileSHA(path)};}));
            record["requiresFreshUnityDependencyCapture"]=false;changed=true;
        }
        if(changed){document["stage"]="source-evidenced-attachment-fans";AtomicFinalText(SourceCapManifestPath,document.ToString());AssetDatabase.ImportAsset(SourceCapManifestPath,ImportAssetOptions.ForceSynchronousImport);}
    }
    static bool HasSourceCapAdmission(FinalFitInput input)
        =>JObject.Parse(File.ReadAllText(SourceCapManifestPath))["records"].OfType<JObject>()
            .Any(record=>(string)record["nativeSource"]==input.NativeSource);
    static Mesh TrySourceCapAnnulus(FinalFitInput input,Mesh source,Fit fit,Vector3[] seed,Vector3[] normals,Vector3[][] references,
        out Vector3[] world,out Vector3[] outputNormals,out Vector3[][] outputReferences,out JObject proof)
        =>TrySourceCapAnnulusWithLineage(input,source,fit,seed,normals,references,out world,out outputNormals,out outputReferences,out proof,out _);
    static Mesh TrySourceCapAnnulusWithLineage(FinalFitInput input,Mesh source,Fit fit,Vector3[] seed,Vector3[] normals,Vector3[][] references,
        out Vector3[] world,out Vector3[] outputNormals,out Vector3[][] outputReferences,out JObject proof,out SourceCapAnnulusLineage[] capLineage)
    {
        capLineage=Array.Empty<SourceCapAnnulusLineage>();
        world=seed;outputNormals=normals;outputReferences=references;proof=new JObject{["applicable"]=false,["automaticSemanticDetection"]=false,["acceptanceOverride"]=false};
        var document=JObject.Parse(File.ReadAllText(SourceCapManifestPath));
        if((int?)document["schemaVersion"]!=1||(string)document["stage"]!="source-evidenced-attachment-fans"||(bool?)document["automaticSemanticDetection"]!=false)
            throw new InvalidDataException("Source cap evidence must be explicitly captured before preparation");
        var matches=((JArray)document["records"]).OfType<JObject>().Where(r=>(string)r["nativeSource"]==input.NativeSource).ToArray();
        if(matches.Length==0)return null;if(matches.Length!=1)throw new InvalidDataException("Ambiguous source cap authoring record");
        var record=matches[0];
        if(input.Donor!=(string)record["actor"])throw new InvalidDataException("Source cap authoring actor does not match immutable base donor");
        ValidateSourceCapRecord(record,true);
        if((float?)record["clipPolicy"]?["minimumAcceptedMetres"]!=.002f||(bool?)record["clipPolicy"]?["acceptanceOverride"]!=false)
            throw new InvalidDataException("Source cap evidence does not authorize a physical acceptance change");
        if(source.vertexCount!=(int)record["vertices"]||SourceCapTopologySHA(source)!=(string)record["sourceTopologySha256"]||SourceCapSkinSHA(source)!=(string)record["fullWeightsSha256"])
            throw new InvalidDataException("Candidate no longer has the reviewed Native cap topology/skin correspondence");
        if(references.Length!=2||references.Any(r=>r.Length!=seed.Length)||seed.Length!=source.vertexCount||normals.Length!=seed.Length||
            seed.Concat(normals).Concat(references.SelectMany(r=>r)).Any(p=>!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z)))
            throw new InvalidDataException("Source cap requires complete finite seed/normals/own/common references");
        int[] centers=record["centerIndices"].ToObject<int[]>();var volume=WardrobeBodyVolume.For(fit.BodySurface);
        if(centers.Any(i=>{var q=volume.Query(seed[i]);return !q.Inside||q.Ambiguous||q.OnSurface;}))
        {proof["notApplicableReason"]="Target fan center is not proven inside; no automatic alternative cap semantics";return null;}
        int originalTriangles=source.triangles.Length/3,budget=Math.Max((int)Math.Ceiling(originalTriangles*.10),2048);
        SourceCapAnnulusResult geometry;
        try{geometry=BuildEvidencedCapAnnulus(seed,references[0],references[1],Enumerable.Range(0,source.subMeshCount).Select(sm=>source.GetTriangles(sm)).ToArray(),
            centers,volume,(float)record["clipPolicy"]["candidateContourMetres"],budget,"Explicit SOURCE evidence "+FinalJsonSHA(record));}
        catch(SourceCapGeometryRejectedException error){proof["notApplicableReason"]=error.Message;return null;}
        var lineage=new JArray(geometry.Lineage.Select(v=>new JObject{["original"]=v.OriginalIndex,["indices"]=new JArray(v.Indices),["barycentric"]=new JArray(v.Barycentric.x,v.Barycentric.y,v.Barycentric.z)}));
        var payload=new JObject{["lineage"]=lineage,["submeshes"]=new JArray(geometry.Submeshes.Select(s=>new JArray(s))),["triangles"]=new JArray(geometry.Submeshes.SelectMany(s=>s))};
        var mesh=SourceCapLineageMesh(source,payload,out var attributes);
        capLineage=geometry.Lineage;
        world=geometry.World;outputReferences=new[]{geometry.OwnReference,geometry.BoneReference};
        outputNormals=geometry.Lineage.Select(v=>v.OriginalIndex>=0?normals[v.OriginalIndex]:(normals[v.Indices[0]]*v.Barycentric.x+normals[v.Indices[1]]*v.Barycentric.y+normals[v.Indices[2]]*v.Barycentric.z).normalized).ToArray();
        proof=new JObject{["applicable"]=true,["sourceEvidenceManifest"]=SourceCapManifestPath,["sourceEvidenceManifestSha"]=FinalFileSHA(SourceCapManifestPath),
            ["sourceRecordSha"]=FinalJsonSHA(record),["sourceNative"]=input.NativeSource,["sourceNativeSha"]=record["nativeSha"].DeepClone(),
            ["sourceTopologySha"]=record["sourceTopologySha256"].DeepClone(),["originalSourceVertices"]=source.vertexCount,["originalSourceTriangles"]=originalTriangles,
            ["clippedVertices"]=mesh.vertexCount,["clippedTriangles"]=mesh.triangles.Length/3,["canonicalOriginalAdditionalTriangleBudget"]=budget,
            ["attributes"]=attributes,["geometry"]=geometry.Evidence,["automaticSemanticDetection"]=false,["acceptanceOverride"]=false,
            ["discardedMarginMayHaveBeenVisible"]=true,["requiresActualPostBindAllSampleAndReferenceAndVisualAcceptance"]=true};
        return mesh;
    }
    static Mesh SourceCapLineageMesh(Mesh source,JObject candidate,out JObject proof)
    {
        var lineage=(JArray)candidate["lineage"];int n=lineage.Count;
        int[][] ids=lineage.Select(v=>v["indices"].ToObject<int[]>()).ToArray();float[][] blend=lineage.Select(v=>v["barycentric"].ToObject<float[]>()).ToArray();
        int[] originals=lineage.Select(v=>(int)v["original"]).ToArray();
        for(int i=0;i<n;i++)
        {
            if(ids[i].Length!=3||blend[i].Length!=3||ids[i].Any(v=>v<0||v>=source.vertexCount)||blend[i].Any(w=>!float.IsFinite(w)||w<0)||Mathf.Abs(blend[i].Sum()-1)>2e-6f)
                throw new InvalidOperationException("Invalid annulus attribute lineage "+i);
            if(originals[i]>=0&&(originals[i]>=source.vertexCount||ids[i][0]!=originals[i]||blend[i][0]!=1||blend[i][1]!=0||blend[i][2]!=0))
                throw new InvalidOperationException("Retained original lineage mismatch");
        }
        Vector3[] V3(Vector3[] values,bool normalizeNew=false)=>Enumerable.Range(0,n).Select(i=>originals[i]>=0?values[originals[i]]:
            normalizeNew?(values[ids[i][0]]*blend[i][0]+values[ids[i][1]]*blend[i][1]+values[ids[i][2]]*blend[i][2]).normalized:
            values[ids[i][0]]*blend[i][0]+values[ids[i][1]]*blend[i][1]+values[ids[i][2]]*blend[i][2]).ToArray();
        Vector4[] V4(Vector4[] values)=>Enumerable.Range(0,n).Select(i=>originals[i]>=0?values[originals[i]]:values[ids[i][0]]*blend[i][0]+values[ids[i][1]]*blend[i][1]+values[ids[i][2]]*blend[i][2]).ToArray();
        var mesh=new Mesh{name="Source_evidenced_cap_annulus",indexFormat=n>65535?IndexFormat.UInt32:source.indexFormat};
        try
        {
            mesh.vertices=V3(source.vertices);
            if(source.HasVertexAttribute(VertexAttribute.Normal))mesh.normals=V3(source.normals,true);
            if(source.HasVertexAttribute(VertexAttribute.Tangent))
            {var t=V4(source.tangents);for(int i=0;i<n;i++)if(originals[i]<0){var unit=new Vector3(t[i].x,t[i].y,t[i].z).normalized;t[i]=new Vector4(unit.x,unit.y,unit.z,t[i].w<0?-1:1);}mesh.tangents=t;}
            if(source.HasVertexAttribute(VertexAttribute.Color))
            {
                var c=source.colors;var result=Enumerable.Range(0,n).Select(i=>originals[i]>=0?c[originals[i]]:c[ids[i][0]]*blend[i][0]+c[ids[i][1]]*blend[i][1]+c[ids[i][2]]*blend[i][2]).ToArray();
                if(source.GetVertexAttributeFormat(VertexAttribute.Color)==VertexAttributeFormat.UNorm8)mesh.colors32=result.Select(v=>(Color32)v).ToArray();else mesh.colors=result;
            }
            var dimensions=new JArray();
            for(int ch=0;ch<8;ch++)
            {
                var attr=(VertexAttribute)((int)VertexAttribute.TexCoord0+ch);if(!source.HasVertexAttribute(attr)){dimensions.Add(0);continue;}
                int dim=source.GetVertexAttributeDimension(attr);dimensions.Add(dim);var original=new List<Vector4>();source.GetUVs(ch,original);
                var values=V4(original.ToArray());
                if(dim==1){using var a=new NativeArray<float>(values.Select(v=>v.x).ToArray(),Allocator.Temp);mesh.SetUVs(ch,a);}
                else if(dim==2)mesh.SetUVs(ch,values.Select(v=>new Vector2(v.x,v.y)).ToArray());
                else if(dim==3)mesh.SetUVs(ch,values.Select(v=>new Vector3(v.x,v.y,v.z)).ToArray());else if(dim==4)mesh.SetUVs(ch,values);
                else throw new InvalidOperationException("Unsupported UV dimension");
            }
            var counts=source.GetBonesPerVertex().ToArray();var full=source.GetAllBoneWeights().ToArray();var legacy=source.boneWeights;
            if(counts.Length!=source.vertexCount||counts.Any(c=>c<1||c>4)||legacy.Length!=source.vertexCount)throw new InvalidOperationException("This diagnostic requires existing <=4 nonzero full influences");
            var sourceWeights=new SortedDictionary<int,float>[counts.Length];int cursor=0;
            for(int i=0;i<counts.Length;i++){sourceWeights[i]=new SortedDictionary<int,float>();for(int k=0;k<counts[i];k++){var w=full[cursor++];sourceWeights[i][w.boneIndex]=sourceWeights[i].TryGetValue(w.boneIndex,out float old)?old+w.weight:w.weight;}}
            var outputWeights=new BoneWeight[n];float maximumDrop=0;
            for(int i=0;i<n;i++)
            {
                if(originals[i]>=0){outputWeights[i]=legacy[originals[i]];continue;}
                var mixed=new SortedDictionary<int,float>();for(int k=0;k<3;k++)foreach(var w in sourceWeights[ids[i][k]])mixed[w.Key]=mixed.TryGetValue(w.Key,out float old)?old+w.Value*blend[i][k]:w.Value*blend[i][k];
                var sorted=mixed.Where(v=>v.Value>0).OrderByDescending(v=>v.Value).ThenBy(v=>v.Key).ToArray();float all=sorted.Sum(v=>v.Value),kept=sorted.Take(4).Sum(v=>v.Value);
                if(!(kept>0))throw new InvalidOperationException("Lost all annulus influences");maximumDrop=Mathf.Max(maximumDrop,1-kept/all);
                var wgt=new BoneWeight();if(sorted.Length>0){wgt.boneIndex0=sorted[0].Key;wgt.weight0=sorted[0].Value/kept;}if(sorted.Length>1){wgt.boneIndex1=sorted[1].Key;wgt.weight1=sorted[1].Value/kept;}
                if(sorted.Length>2){wgt.boneIndex2=sorted[2].Key;wgt.weight2=sorted[2].Value/kept;}if(sorted.Length>3){wgt.boneIndex3=sorted[3].Key;wgt.weight3=sorted[3].Value/kept;}outputWeights[i]=wgt;
            }
            if(maximumDrop>.05f)throw new InvalidOperationException("Annulus influence loss exceeds existing5%gate");
            mesh.boneWeights=outputWeights;mesh.bindposes=source.bindposes;
            // Legacy writer preserves the real nontrivial source float weights.
            var outCounts=mesh.GetBonesPerVertex().ToArray();var outFull=mesh.GetAllBoneWeights().ToArray();var offsets=new int[counts.Length];cursor=0;
            for(int i=0;i<counts.Length;i++){offsets[i]=cursor;cursor+=counts[i];}cursor=0;
            for(int i=0;i<n;i++)
            {
                if(originals[i]>=0){int old=originals[i];if(outCounts[i]!=counts[old])throw new InvalidOperationException("Retained full influence count changed");
                    for(int k=0;k<counts[old];k++){var a=full[offsets[old]+k];var b=outFull[cursor+k];if(a.boneIndex!=b.boneIndex||BitConverter.SingleToInt32Bits(a.weight)!=BitConverter.SingleToInt32Bits(b.weight))throw new InvalidOperationException("Retained full influence bytes changed");}}
                cursor+=outCounts[i];
            }
            var subs=(JArray)candidate["submeshes"];if(subs.Count!=source.subMeshCount)throw new InvalidOperationException("Annulus material slots changed");mesh.subMeshCount=subs.Count;
            for(int sm=0;sm<subs.Count;sm++)mesh.SetTriangles(subs[sm].ToObject<int[]>(),sm);
            if(!mesh.triangles.SequenceEqual(candidate["triangles"].ToObject<int[]>()))throw new InvalidOperationException("Annulus flattened submesh order mismatch");
            mesh.RecalculateBounds();proof=new JObject{["vertices"]=n,["triangles"]=mesh.triangles.Length/3,["materialSlots"]=mesh.subMeshCount,["uvDimensions"]=dimensions,
                ["retainedFullSkinFloatBitsExact"]=true,["maximumDroppedWeightFraction"]=maximumDrop,["sourceUnchanged"]=true,["lineageInterpolation"]=true};
            var channelProof=new JArray();
            foreach(var descriptor in source.GetVertexAttributes())
            {
                var attribute=descriptor.attribute;
                bool exists=mesh.HasVertexAttribute(attribute);
                var channel=new JObject{["attribute"]=attribute.ToString(),["sourceFormat"]=descriptor.format.ToString(),["sourceDimension"]=descriptor.dimension,
                    ["outputExists"]=exists,["outputFormat"]=exists?mesh.GetVertexAttributeFormat(attribute).ToString():null,
                    ["outputDimension"]=exists?mesh.GetVertexAttributeDimension(attribute):0};channelProof.Add(channel);
                // This source cap writer must not silently widen a half UV
                // channel. The generic writer has no raw-layout preservation claim.
                if(!exists||mesh.GetVertexAttributeFormat(attribute)!=descriptor.format||mesh.GetVertexAttributeDimension(attribute)!=descriptor.dimension)
                    throw new InvalidOperationException("Source cap output changed channel format/dimension: "+attribute);
            }
            proof["actualChannelFormatsAndDimensions"]=channelProof;proof["channelFormatsAndDimensionsExact"]=true;
            return mesh;
        }
        catch{Object.DestroyImmediate(mesh);throw;}
    }

}
}
#endif
