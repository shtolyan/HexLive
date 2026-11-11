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
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Optional, one-run R31 recovery. A missing/rejected receipt always falls back
    // to the ordinary producer. Completed final manifests embed provenance and
    // never depend on this ignored diagnostic input for future cache hits.
    sealed class FinalRevalidationContext
    {
        internal JObject Provenance,Rows;
    }
    static FinalRevalidationContext LoadFinalRevalidationContext()
    {
        string path=Environment.GetEnvironmentVariable("HEXLIVE_FINAL_REVALIDATE_R31");
        if(string.IsNullOrEmpty(path))return null;
        var proof=JObject.Parse(File.ReadAllText(path));string cache=(string)proof["cachePath"];
        if((string)proof["stage"]!="R31-immutable-original-provenance"||FinalFileSHA(cache)!=(string)proof["cacheSHA"])
            throw new InvalidDataException("Frozen original R31 recovery receipt changed");
        return new FinalRevalidationContext{Provenance=proof,Rows=(JObject)JObject.Parse(File.ReadAllText(cache))["rows"]};
    }
    static JObject OriginalFinalInput(JObject current,JArray sources)
    {var original=(JObject)current.DeepClone();original["sources"]=sources.DeepClone();return original;}
    static bool OriginalFinalInputMatches(JObject current,JArray sources,string originalHash)
    {
        if(current==null||sources==null||string.IsNullOrEmpty(originalHash))return false;
        return FinalJsonSHA(OriginalFinalInput(current,sources))==originalHash;
    }
    static bool ReviewedR31SourceAllowed(string path,string originalSha,string currentSha)
    {
        if(string.IsNullOrEmpty(originalSha)||string.IsNullOrEmpty(currentSha))return false;
        if(originalSha==currentSha)return true;
        string name=Path.GetFileName(path);
        // Root reviewed this exact exception/context-only Adaptive revision.
        // No numeric/topology change, and eligible historical rows used zero
        // subdivision rounds. Current actual replay remains mandatory.
        if(name=="LegacyWardrobeAdaptiveEnvelope.cs")
            return currentSha=="c847fbfa817de9d2faa9b99848630e7d937a274d39d6859a64776acfd8f656d3";
        return new[]{"LegacyWardrobeFinalFitMath.cs","LegacyWebGLWardrobeFinalFitStorage.cs","LegacyWardrobeBodyVolume.cs","LegacyWardrobeEnvelopeProbe.cs"}.Contains(name,StringComparer.Ordinal);
    }
    static JObject ClaimFinalRevalidation(FinalRevalidationContext context,JObject current,JObject metric,JObject settings,out JObject provenance)
    {
        provenance=null;if(context==null)return null;
        string id=(string)metric["id"];var row=context.Rows[id] as JObject;
        if(row==null||(string)row["final"]?["processorEvidence"]?["fitKind"]!="deformable-garment")return null;
        var oldSources=(JArray)context.Provenance["sources"];
        foreach(var source in oldSources)
        {
            string path=(string)source["path"];
            if(!ReviewedR31SourceAllowed(path,(string)source["sha"],FinalFileSHA(path)))return null;
        }
        if(!OriginalFinalInputMatches(current,oldSources,(string)row["final"]["inputHash"])||
            !FinalRowMatches(row,(string)row["final"]["inputHash"],settings))return null;
        foreach(string actor in new[]{"Marta","Kshishtof"})
        {
            var body=row["final"]["processorEvidence"]["bodies"][actor];var topology=body?["topologyRefinement"];
            if((int?)topology?["refinementRounds"]!=0||(bool?)topology?["topologyUnchanged"]!=true||
                (int?)topology?["inputVertices"]!=(int?)topology?["outputVertices"]||
                (long?)topology?["inputTriangles"]!=(long?)topology?["outputTriangles"])return null;
        }
        provenance=new JObject{["mode"]="revalidated-unmodified-topology",["priorOriginalInputHash"]=row["final"]["inputHash"].DeepClone(),
            ["oldRowSHA"]=FinalJsonSHA(row),["originalRow"]=row.DeepClone(),["originalInputReconstructedExactly"]=true,["originalOutputArtifactsVerified"]=true,
            ["producerSources"]=oldSources.DeepClone(),["originalInput"]=OriginalFinalInput(current,oldSources),["originalCacheSHA"]=context.Provenance["cacheSHA"].DeepClone()};
        return (JObject)row.DeepClone();
    }
    public static void AuditFinalUnmodifiedRevalidation()
    {
        const string id="clothing.skirt_alloy";string path=OutputRoot+"/final-unmodified-revalidation-control.json";
        var report=new JObject{["passed"]=false,["assetsModified"]=false};FinalFitProduct product=null;
        try
        {
            var context=LoadFinalRevalidationContext();if(context==null)throw new InvalidDataException("Explicit original R31 provenance required");
            var metric=(JObject)JObject.Parse(File.ReadAllText(PreparedRoot+"/preparation.json"))[id];
            var settings=FinalSettings(AssetDatabase.LoadAssetAtPath<GameObject>((string)metric["prepared"]).GetComponent<Wear>());
            var deps=new Dictionary<string,string>();foreach(string p in new[]{(string)metric["source"],(string)metric["prepared"]}.Concat(FittingUnderlayerPaths)
                .Concat(new[]{PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"}))deps[p]=AssetDatabase.GetAssetDependencyHash(p).ToString();
            var sources=new JArray(FinalFitSourcePaths().Concat(BaseGeneratorSources.Select(n=>"Assets/HexLive/UnityDebug/Editor/"+n)).Append(FinalStorageSource)
                .Distinct(StringComparer.Ordinal).OrderBy(p=>p,StringComparer.Ordinal).Select(p=>new JObject{["path"]=p,["sha"]=FinalFileSHA(p)}));
            var input=FinalProcessorInput(FinalFitVersion,metric,settings,sources,deps);
            var original=ClaimFinalRevalidation(context,input,metric,settings,out JObject provenance);
            if(original==null)throw new InvalidDataException("Actual complete original input/artifacts did not match; no reuse authorized");
            using var work=new FinalFitInput(id,metric,FinalJsonSHA(input),original,provenance);product=MakeFinalFitProduct(work);
            if(product.Mode!="revalidated-unmodified-topology")throw new InvalidDataException("Actual control did not revalidate BOTH actor outputs");
            report["evidence"]=product.Evidence.DeepClone();report["passed"]=true;
        }
        catch(Exception e){report["error"]=e.ToString();throw;}
        finally{if(product!=null)DestroyFinalPrivate(product.Template,product.MartaMesh,product.KshishtofMesh);File.WriteAllText(path,report.ToString());}
        Debug.Log("LEGACY_FINAL_REVALIDATION_CONTROL_PASS "+path);
    }
    // Source attributes that fitting is required to retain, compared in exact
    // index order. Fitted positions/normals/tangents are separately protected by
    // the original complete output+meta SHA and independently measured below.
    static bool UnmodifiedFinalAttributes(Mesh source,Mesh result,Matrix4x4[] expectedBindposes)
    {
        if(source.vertexCount!=result.vertexCount||source.subMeshCount!=result.subMeshCount||
            !result.bindposes.SequenceEqual(expectedBindposes)||
            !source.GetBonesPerVertex().ToArray().SequenceEqual(result.GetBonesPerVertex().ToArray())||
            !source.GetAllBoneWeights().ToArray().SequenceEqual(result.GetAllBoneWeights().ToArray())||
            !source.colors.SequenceEqual(result.colors)||
            source.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.Color)!=result.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.Color)||
            (source.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.Color)>0&&source.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Color)!=result.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Color)))return false;
        for(int sm=0;sm<source.subMeshCount;sm++)
            if(source.GetTopology(sm)!=result.GetTopology(sm)||!source.GetIndices(sm).SequenceEqual(result.GetIndices(sm)))return false;
        for(int channel=0;channel<8;channel++)
        {
            var attr=(UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0+channel);
            if(source.GetVertexAttributeDimension(attr)!=result.GetVertexAttributeDimension(attr)||
                (source.GetVertexAttributeDimension(attr)>0&&source.GetVertexAttributeFormat(attr)!=result.GetVertexAttributeFormat(attr)))return false;
            var a=new List<Vector4>();var b=new List<Vector4>();source.GetUVs(channel,a);result.GetUVs(channel,b);
            if(!a.SequenceEqual(b))return false;
        }
        return result.normals.Length==result.vertexCount&&result.tangents.Length==result.vertexCount;
    }
    static bool FinalRevalidationProvenanceAccepted(JToken p)
    {
        return p is JObject&&(string)p["mode"]=="revalidated-unmodified-topology"&&
            (bool?)p["originalInputReconstructedExactly"]==true&&(bool?)p["originalOutputArtifactsVerified"]==true&&
            p["originalRow"] is JObject row&&FinalJsonSHA(row)==(string)p["oldRowSHA"]&&
            (string)row["final"]?["inputHash"]==(string)p["priorOriginalInputHash"]&&
            p["producerSources"] is JArray sources&&sources.Count>0&&
            p["originalInput"] is JObject original&&FinalJsonSHA(original)==(string)p["priorOriginalInputHash"]&&
            JToken.DeepEquals(original["sources"],sources)&&sources.All(x=>((string)x["sha"])?.Length==64&&!string.IsNullOrEmpty((string)x["path"]));
    }
    static bool CurrentFinalRevalidationAccepted(JToken body,int vertices,long triangles)
    {
        if(!(body is JObject))return false;
        var r=body["currentGeometryRevalidation"];var p=body["postBindSurfaceEnvelope"];
        if((string)body["geometryMode"]!="revalidated-unmodified-topology"||(bool?)r?["passed"]!=true||
            (bool?)r?["unchangedAttributesExact"]!=true||(bool?)r?["originalOutputAttributesPinned"]!=true||
            (int?)r?["vertices"]!=vertices||(long?)r?["triangles"]!=triangles||
            (bool?)p?["passedSampledEnvelope"]!=true||(long?)p?["sampleCount"]!=vertices+13L*triangles||
            (float?)p?["targetClearanceMetres"]!=.002f||!((float?)p?["minimumSignedDistanceMetres"]>=.002f))return false;
        foreach(string f in new[]{"samplesBelowMinimum","insideSamples","ambiguousSamples","onSurfaceSamples"})if((int?)p?[f]!=0)return false;
        foreach(string field in new[]{"baseToFinalDisplacement","commonBoneReferenceDisplacement"})
            if((string)body[field]?["measurementScope"]!="all-output-vertices"||
                (string)body[field]?["positionScope"]!="actual-post-bind-world-vertices"||
                (int?)body[field]?["measuredVertices"]!=vertices||!((float?)body[field]?["maxMetres"]>=0&& (float?)body[field]?["maxMetres"]<=.12f))return false;
        return (string)body["runtimeConstruction"]=="BodyBones.Equip"&&(float?)body["reconstruction"]?["maxMetres"]>=0&&(float?)body["reconstruction"]?["maxMetres"]<=.0002f&&
            (string)body["historicalSolverEvidence"]?["seed"]==(string)body["seed"];
    }
    static bool TryRevalidateFinalBody(FinalFitInput input,ActorName actor,GameObject template,Fit reference,
        IEnumerable<(string kind,Mesh m,Mesh k)> candidates,out Mesh result,out JObject proof)
    {
        result=null;proof=null;var prior=input.PriorRevalidationRow;
        if(prior==null)return false;
        var historical=prior["final"]["processorEvidence"]["bodies"][actor.ToString()];string kind=(string)historical["seed"];
        if(!new[]{"immutable-base-fit","uniform-foot-profile","source-clearance-retarget","fresh-bone-retarget"}.Contains(kind))return false;
        var selected=candidates.Where(c=>c.kind==kind).ToArray();if(selected.Length!=1)return false;
        var c=selected[0];var seed=actor==ActorName.Marta?c.m:c.k;
        var saved=AssetDatabase.LoadAssetAtPath<Mesh>((string)prior["final"]["meshes"][actor.ToString()]["path"]);
        try
        {
            using var fit=ConstructFit(template,c.m,c.k,actor);
            var binds=fit.BoneWorld.Select(m=>m.inverse*fit.Skin.transform.localToWorldMatrix).ToArray();
            if(saved==null||!UnmodifiedFinalAttributes(seed,saved,binds))return false;
            result=Object.Instantiate(saved);
            using var planned=ConstructFit(template,actor==ActorName.Marta?result:c.m,actor==ActorName.Kshishtof?result:c.k,actor);
            using var actual=ConstructFinalRuntimeFit(template,actor==ActorName.Marta?result:c.m,actor==ActorName.Kshishtof?result:c.k,actor);
            var own=RetargetProbeError(fit.WorldVertices,actual.WorldVertices);var common=RetargetProbeError(reference.WorldVertices,actual.WorldVertices);
            foreach(var m in new[]{own,common})
            {m["measurementScope"]="all-output-vertices";m["positionScope"]="actual-post-bind-world-vertices";m["originalReferenceVertices"]=seed.vertexCount;m["measuredVertices"]=actual.WorldVertices.Length;}
            var surface=EnvelopeProbeResidualDispatchedDraft(actual.WorldVertices,result.triangles,WardrobeBodyVolume.For(actual.BodySurface),.002f);
            proof=new JObject{["geometryMode"]="revalidated-unmodified-topology",["seed"]=kind,["runtimeConstruction"]="BodyBones.Equip",
                ["postBindSurfaceEnvelope"]=surface,["baseToFinalDisplacement"]=own,["commonBoneReferenceDisplacement"]=common,
                ["reconstruction"]=RetargetProbeError(planned.WorldVertices,actual.WorldVertices),["historicalSolverEvidence"]=historical.DeepClone(),
                ["currentGeometryRevalidation"]=new JObject{["passed"]=true,["unchangedAttributesExact"]=true,["originalOutputAttributesPinned"]=true,
                    ["vertices"]=result.vertexCount,["triangles"]=TriangleCount(result),["priorOriginalInputHash"]=prior["final"]["inputHash"].DeepClone()}};
            if(CurrentFinalRevalidationAccepted(proof,result.vertexCount,TriangleCount(result)))return true;
        }
        catch(Exception error){Debug.LogWarning("LEGACY_FINAL_REVALIDATION_FALLBACK "+input.Id+"/"+actor+": "+error.Message);}
        if(result!=null)Object.DestroyImmediate(result);result=null;proof=null;return false;
    }
}
}
#endif
