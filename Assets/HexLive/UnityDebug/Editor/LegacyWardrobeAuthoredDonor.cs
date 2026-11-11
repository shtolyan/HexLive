#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string AuthoredDonorManifest=PreparedRoot+"/authored-donor-admissions.json";
    const string AuthoredDonorMode="authored-donor-preserved";
    static JObject AuthoredDonorDocument()
    {
        var document=JObject.Parse(File.ReadAllText(AuthoredDonorManifest));
        if((int?)document["schemaVersion"]!=1||(bool?)document["automaticFallback"]!=false||!(document["records"] is JArray))
            throw new InvalidDataException("Invalid explicit authored-donor admission schema");
        var context=document["validationContext"];
        if((string)context?["unityVersion"]!=Application.unityVersion||
            (string)context?["activeBuildTarget"]!=EditorUserBuildSettings.activeBuildTarget.ToString())
            throw new InvalidDataException("Authored-donor evidence is scoped to its captured Unity version/build target; explicit new platform validation required");
        return document;
    }
    public static void CaptureInitialAuthoredDonorValidationContext()
    {
        var document=JObject.Parse(File.ReadAllText(AuthoredDonorManifest));
        if(document["validationContext"]!=null){AuthoredDonorDocument();return;}
        if((int?)document["schemaVersion"]!=1||(bool?)document["automaticFallback"]!=false)throw new InvalidDataException("Invalid authored-donor capture header");
        foreach(JObject record in document["records"])ValidateAuthoredDonorRecord(record);
        document["validationContext"]=new JObject{["unityVersion"]=Application.unityVersion,["activeBuildTarget"]=EditorUserBuildSettings.activeBuildTarget.ToString(),
            ["sameTargetDependencyValidation"]=true,["futureWebGLRequiresExplicitPlatformScopedValidation"]=true};
        AtomicFinalText(AuthoredDonorManifest,document.ToString());AssetDatabase.ImportAsset(AuthoredDonorManifest,ImportAssetOptions.ForceSynchronousImport);
    }
    static string[] AuthoredDonorInputPaths()
    {
        var paths=AuthoredDonorDocument()["records"].SelectMany(r=>r["evidenceFiles"].Select(e=>(string)e["path"])).ToArray();
        foreach(string path in paths)
            if(string.IsNullOrEmpty(path)||!path.StartsWith(PreparedRoot+"/Evidence/",StringComparison.Ordinal)||path.Contains("..")||path.Contains("\\"))
                throw new InvalidDataException("Authored-donor evidence path outside owned Evidence directory");
        return new[]{AuthoredDonorManifest}.Concat(paths).Distinct(StringComparer.Ordinal).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
    }
    static bool AuthoredDonorIdentityAccepted(JObject record,string source,string actor,string donor)
        =>record!=null&&(actor=="Marta"||actor=="Kshishtof")&&actor==donor&&
          (string)record["nativeSource"]==source&&(string)record["actor"]==actor&&
          (string)record["review"]?["geometryMode"]==AuthoredDonorMode&&
          (bool?)record["review"]?["sourceOnly"]==true&&(bool?)record["review"]?["recipientStrict"]==true&&
          (bool?)record["review"]?["bodyContactAcceptance"]==false;
    static void ValidateAuthoredDonorRecord(JObject record)
    {
        foreach(string path in new[]{(string)record["nativeSource"],(string)record["nativeMesh"]}.Concat(record["materialPaths"]?.Select(m=>(string)m["path"])??Enumerable.Empty<string>()))
            if(string.IsNullOrEmpty(path)||!path.StartsWith("Assets/",StringComparison.Ordinal)||path.Contains("..")||path.Contains("\\"))
                throw new InvalidDataException("Authored-donor source path outside Assets");
        foreach(var pair in new[]{("nativeSource","nativeSha"),("nativeMesh","nativeMeshSha")})
            if(FinalFileSHA((string)record[pair.Item1])!=(string)record[pair.Item2])throw new InvalidDataException("Authored-donor Native bytes changed");
        string source=(string)record["nativeSource"];
        if(string.IsNullOrEmpty((string)record["nativeDependency"])||AssetDatabase.GetAssetDependencyHash(source).ToString()!=(string)record["nativeDependency"])
            throw new InvalidDataException("Authored-donor source dependency changed");
        if(AssetDatabase.AssetPathToGUID(source)!=(string)record["nativeGuid"])throw new InvalidDataException("Authored-donor Native prefab GUID changed");
        string[] expectedBodies={PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"};
        if(!(record["bodyAuthoringDependencies"] is JArray bodies)||bodies.Count!=2||
            !bodies.Select(v=>(string)v["path"]).OrderBy(v=>v,StringComparer.Ordinal).SequenceEqual(expectedBodies.OrderBy(v=>v,StringComparer.Ordinal)))
            throw new InvalidDataException("Authored-donor body authoring pair missing");
        foreach(var body in bodies)if(FinalFileSHA((string)body["path"])!=(string)body["sha256"]||
            string.IsNullOrEmpty((string)body["dependency"])||AssetDatabase.GetAssetDependencyHash((string)body["path"]).ToString()!=(string)body["dependency"])
            throw new InvalidDataException("Authored-donor body authoring bytes/dependency changed");
        if(AssetDatabase.AssetPathToGUID((string)record["nativeMesh"])!=(string)record["nativeMeshGuid"])
            throw new InvalidDataException("Authored-donor mesh GUID changed");
        if(!(record["evidenceFiles"] is JArray files)||files.Count<3)throw new InvalidDataException("Authored-donor actual pose/raw/visual evidence missing");
        foreach(var file in files)
        {
            string path=(string)file["path"];
            if(string.IsNullOrEmpty(path)||!path.StartsWith(PreparedRoot+"/Evidence/",StringComparison.Ordinal)||path.Contains("..")||path.Contains("\\")||FinalFileSHA(path)!=(string)file["sha256"])
                throw new InvalidDataException("Authored-donor owned evidence changed");
        }
        if(!(record["materialPaths"] is JArray materials)||materials.Count==0)throw new InvalidDataException("Authored-donor Native materials missing");
        foreach(var material in materials)if(FinalFileSHA((string)material["path"])!=(string)material["sha256"]||
            AssetDatabase.AssetPathToGUID((string)material["path"])!=(string)material["guid"])
            throw new InvalidDataException("Authored-donor Native material bytes/GUID changed");
        var native=AssetDatabase.LoadAssetAtPath<GameObject>(source);var wear=native.GetComponent<Wear>();
        if(!JToken.DeepEquals(JObject.Parse(Settings(wear).ToString(Newtonsoft.Json.Formatting.None)),record["nativeSettings"]))
            throw new InvalidDataException("Authored-donor Native authoring settings changed");
        if(!native.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials.Select(AssetDatabase.GetAssetPath)
            .SequenceEqual(materials.Select(m=>(string)m["path"])))throw new InvalidDataException("Authored-donor Native material order changed");
        ValidateAuthoredDonorEvidence(record);
    }
    static JObject NativeCopyBufferProof(Mesh source,Mesh copy)
    {
        var proof=new JObject{["vertexBuffers"]=new JArray()};
        if(source.vertexCount!=copy.vertexCount||source.subMeshCount!=copy.subMeshCount||source.vertexBufferCount!=copy.vertexBufferCount||!source.GetVertexAttributes().SequenceEqual(copy.GetVertexAttributes()))
            throw new InvalidDataException("Authored-donor derived layout differs from Native");
        using(var data=Mesh.AcquireReadOnlyMeshData(new[]{source,copy}))
        {
            for(int stream=0;stream<source.vertexBufferCount;stream++)
            {
                var a=data[0].GetVertexData<byte>(stream).ToArray();var b=data[1].GetVertexData<byte>(stream).ToArray();
                if(!a.SequenceEqual(b))throw new InvalidDataException("Authored-donor derived raw vertex bytes changed");
                ((JArray)proof["vertexBuffers"]).Add(new JObject{["stream"]=stream,["bytes"]=a.Length,["sha256"]=FinalSHA(a)});
            }
            var aIndex=data[0].GetIndexData<byte>().ToArray();var bIndex=data[1].GetIndexData<byte>().ToArray();
            if(!aIndex.SequenceEqual(bIndex))throw new InvalidDataException("Authored-donor derived raw index bytes changed");
            proof["indexBufferSha256"]=FinalSHA(aIndex);
        }
        if(!source.GetBonesPerVertex().ToArray().SequenceEqual(copy.GetBonesPerVertex().ToArray()))throw new InvalidDataException("Authored-donor full influence counts changed");
        var weights=source.GetAllBoneWeights().ToArray();var copied=copy.GetAllBoneWeights().ToArray();
        if(weights.Length!=copied.Length||weights.Where((w,i)=>w.boneIndex!=copied[i].boneIndex||BitConverter.SingleToInt32Bits(w.weight)!=BitConverter.SingleToInt32Bits(copied[i].weight)).Any())
            throw new InvalidDataException("Authored-donor full influence float bits changed");
        var binds=source.bindposes;var copyBinds=copy.bindposes;
        if(binds.Length!=copyBinds.Length||binds.Where((m,i)=>Enumerable.Range(0,16).Any(k=>BitConverter.SingleToInt32Bits(m[k])!=BitConverter.SingleToInt32Bits(copyBinds[i][k]))).Any())
            throw new InvalidDataException("Authored-donor bindpose float bits changed");
        for(int sm=0;sm<source.subMeshCount;sm++)if(!source.GetSubMesh(sm).Equals(copy.GetSubMesh(sm)))throw new InvalidDataException("Authored-donor submesh/material descriptors changed");
        proof["allRawVertexAndIndexBytesExact"]=true;proof["fullSkinFloatBitsExact"]=true;
        proof["bindposeFloatBitsExact"]=true;proof["submeshDescriptorsExact"]=true;proof["vertices"]=copy.vertexCount;proof["triangles"]=TriangleCount(copy);
        return proof;
    }
    static Mesh CloneReadableAuthoredMesh(Mesh source,out JObject proof)
    {
        bool sourceReadable=source.isReadable;var copy=Object.Instantiate(source);
        try
        {
            if(!copy.isReadable)
            {
                var serialized=new SerializedObject(copy);var readable=serialized.FindProperty("m_IsReadable");
                if(readable==null)throw new InvalidDataException("Owned Native copy has no serialized CPU-readable property");
                readable.boolValue=true;serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            if(!copy.isReadable)throw new InvalidDataException("Owned Native copy remains unreadable");
            proof=NativeCopyBufferProof(source,copy);proof["ownedDerivedReadable"]=true;proof["nativeReadableFlag"]=sourceReadable;proof["nativeReadableFlagUnchanged"]=source.isReadable==sourceReadable;
            if(source.isReadable!=sourceReadable)throw new InvalidDataException("Native readable flag changed");
            return copy;
        }
        catch{Object.DestroyImmediate(copy);throw;}
    }
    static bool TryAuthoredDonorProduct(FinalFitInput input,ActorName actor,out Mesh result,out JObject evidence)
    {
        result=null;evidence=null;
        if(actor.ToString()!=input.Donor)return false;
        var records=AuthoredDonorDocument()["records"].OfType<JObject>().Where(r=>(string)r["nativeSource"]==input.NativeSource).ToArray();
        if(records.Length==0)return false;if(records.Length!=1)throw new InvalidDataException("Ambiguous authored-donor admission");
        var record=records[0];if(!AuthoredDonorIdentityAccepted(record,input.NativeSource,actor.ToString(),input.Donor))throw new InvalidDataException("Authored-donor admission does not permit this body/source");
        ValidateAuthoredDonorRecord(record);
        var native=AssetDatabase.LoadAssetAtPath<GameObject>(input.NativeSource);var source=SourceMesh(native.GetComponent<Wear>(),actor,out _);
        if(AssetDatabase.GetAssetPath(source)!=(string)record["nativeMesh"]||source.vertexCount!=(int?)record["vertices"]||TriangleCount(source)!=(long?)record["triangles"])
            throw new InvalidDataException("Authored-donor selected Native actor mesh changed");
        result=CloneReadableAuthoredMesh(source,out var raw);
        try
        {
            using var nativeFit=ConstructFinalRuntimeFit(native,source,source,actor);
            using var finalFit=ConstructFinalRuntimeFit(input.PrivateTemplate,actor==ActorName.Marta?result:input.MartaMesh,
                actor==ActorName.Kshishtof?result:input.KshishtofMesh,actor);
            var reconstruction=RetargetProbeError(nativeFit.WorldVertices,finalFit.WorldVertices);
            if((float)reconstruction["maxMetres"]>.0002f)throw new InvalidDataException("Authored-donor actual equipment reconstruction changed source geometry");
            var sdf=EnvelopeProbeResidualDispatchedDraft(finalFit.WorldVertices,result.triangles,WardrobeBodyVolume.For(finalFit.BodySurface),.002f);
            evidence=new JObject{["geometryMode"]=AuthoredDonorMode,["runtimeConstruction"]="BodyBones.Equip",["reconstruction"]=reconstruction,
                ["sourceAdmission"]=new JObject{["manifest"]=AuthoredDonorManifest,["manifestSha256"]=FinalFileSHA(AuthoredDonorManifest),
                    ["recordSha256"]=FinalJsonSHA(record),["nativeSource"]=input.NativeSource,["nativeMesh"]=record["nativeMesh"].DeepClone(),
                    ["nativeMeshSha256"]=record["nativeMeshSha"].DeepClone(),["actor"]=actor.ToString(),["donor"]=input.Donor,
                    ["recipientStrict"]=true,["rawBuffers"]=raw,["sourceSettingsChanged"]=false,["shapeDeformationAllowed"]=false},
                ["bodyContactDiagnostic"]=new JObject{["acceptance"]=false,["diagnosticOnly"]=true,["strictPhysical2mmResult"]=sdf},
                ["worldBounds"]=Box(VertexBounds(finalFit.WorldVertices))};
            return true;
        }
        catch{Object.DestroyImmediate(result);result=null;throw;}
    }
    static bool AuthoredDonorReceiptAccepted(JToken body,string actor,string source,string donor)
    {
        if(!(body is JObject value)||(string)value["geometryMode"]!=AuthoredDonorMode||!(value["sourceAdmission"] is JObject admission))return false;
        if((string)admission["manifest"]!=AuthoredDonorManifest||(string)admission["actor"]!=actor||(string)admission["donor"]!=donor||actor!=donor||
            (string)admission["nativeSource"]!=source||(bool?)admission["recipientStrict"]!=true||(bool?)admission["sourceSettingsChanged"]!=false||
            (bool?)admission["shapeDeformationAllowed"]!=false||value["surfaceEnvelope"]!=null||value["postBindSurfaceEnvelope"]!=null||value["topologyRefinement"]!=null||
            (bool?)value["bodyContactDiagnostic"]?["acceptance"]!=false||(bool?)value["bodyContactDiagnostic"]?["diagnosticOnly"]!=true)return false;
        var raw=admission["rawBuffers"];
        return (bool?)raw?["ownedDerivedReadable"]==true&&(bool?)raw?["nativeReadableFlagUnchanged"]==true&&
            (bool?)raw?["allRawVertexAndIndexBytesExact"]==true&&(bool?)raw?["fullSkinFloatBitsExact"]==true&&
            (bool?)raw?["bindposeFloatBitsExact"]==true&&(bool?)raw?["submeshDescriptorsExact"]==true&&
            (string)value["runtimeConstruction"]=="BodyBones.Equip"&&(float?)value["reconstruction"]?["maxMetres"]>=0&&(float?)value["reconstruction"]?["maxMetres"]<=.0002f;
    }
    static void ValidateCurrentAuthoredDonorProof(JToken body,string actor,string source,string donor)
    {
        if(!AuthoredDonorReceiptAccepted(body,actor,source,donor))throw new InvalidDataException("Invalid authored-donor receipt/body/source");
        var admission=body["sourceAdmission"];
        if((string)admission["manifestSha256"]!=FinalFileSHA(AuthoredDonorManifest))throw new InvalidDataException("Authored-donor manifest changed");
        var record=AuthoredDonorDocument()["records"].OfType<JObject>().Single(r=>(string)r["nativeSource"]==source);
        if(!AuthoredDonorIdentityAccepted(record,source,actor,donor)||FinalJsonSHA(record)!=(string)admission["recordSha256"]||
            (string)admission["nativeMesh"]!=(string)record["nativeMesh"]||(string)admission["nativeMeshSha256"]!=(string)record["nativeMeshSha"])
            throw new InvalidDataException("Authored-donor source admission changed");
        ValidateAuthoredDonorRecord(record);
    }
    public static JObject AuditAuthoredDonorFinalRuntime(GameObject finalTemplate,string nativePath,string actorName,string donorName)
    {
        if(!Application.isPlaying||Time.unscaledDeltaTime<=0)throw new InvalidDataException("Authored-donor six-pose proof requires real PlayMode frames");
        var record=AuthoredDonorDocument()["records"].OfType<JObject>().Single(r=>(string)r["nativeSource"]==nativePath);
        if(!AuthoredDonorIdentityAccepted(record,nativePath,actorName,donorName))throw new InvalidDataException("Runtime authored-donor body/source is not admitted");
        ValidateAuthoredDonorRecord(record);
        var actor=(ActorName)Enum.Parse(typeof(ActorName),actorName);var native=AssetDatabase.LoadAssetAtPath<GameObject>(nativePath);
        using var source=new FcoDonorRig(native,actor);using var candidate=new FcoDonorRig(finalTemplate,actor);
        var idle=FcoDonorClip("Male@Idle.fbx");var run=FcoDonorClip("Male@Run.fbx");
        var poses=new[]{("standing",idle,.4f,false),("run_early",run,.1f,false),("run_mid",run,.45f,false),("run_late",run,.8f,false),
            ("sleep",FcoDonorClip("Sleep.fbx"),.5f,true),("belly",FcoDonorClip("LayingBelly.anim"),.5f,true)};
        var rows=new JArray();
        JObject Compare(string label)
        {
            var a=FcoDonorCapture(source.Skin);var b=FcoDonorCapture(candidate.Skin);
            try
            {
                var error=RetargetProbeError(a.vertices,b.vertices);
                if((float)error["maxMetres"]>.0002f||!a.triangles.SequenceEqual(b.triangles))throw new InvalidDataException("Authored-donor posed geometry changed: "+label);
                return new JObject{["passed"]=true,["allVertices"]=a.vertexCount,["maximumAllowedMetres"]=.0002f,["actualVertexError"]=error,["allTriangleIndicesExact"]=true};
            }
            finally{Object.DestroyImmediate(a);Object.DestroyImmediate(b);}
        }
        foreach(var pose in poses)
        {
            foreach(var rig in new[]{source,candidate}){rig.Body.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);rig.Pose(pose.Item2,pose.Item3,pose.Item4);}
            var row=new JObject{["pose"]=pose.Item1,["posed"]=Compare(pose.Item1)};
            foreach(var rig in new[]{source,candidate})rig.Body.transform.SetPositionAndRotation(new Vector3(13.25f,2.1f,-8.75f),Quaternion.Euler(0,137,0));
            row["movedRoot"]=Compare(pose.Item1+"/moved");rows.Add(row);
        }
        foreach(var rig in new[]{source,candidate}){rig.Body.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);rig.Bones.TakeOff("fco-evidence");rig.Equip();rig.Pose(idle,.4f,false);}
        return new JObject{["passed"]=true,["sixActualPoses"]=rows,["actualReequip"]=Compare("actual-reequip"),
            ["allVerticesAndIndicesCompared"]=true,["bodyContactAcceptance"]=false,["source"]=nativePath,["actor"]=actorName,["donor"]=donorName};
    }

}
}
#endif
