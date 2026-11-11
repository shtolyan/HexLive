#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
namespace HexLive.Tests
{
public sealed class FinalUnmodifiedRevalidationTests
{
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation",false)).First(t=>t!=null);
    static object Call(string name,params object[] args)=>Preparation.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
    static JArray Sources()=>new JArray(new JObject{["path"]="original.cs",["sha"]=new string('a',64)});
    [Test] public void OriginalInputRequiresCompleteOriginalGraphAndExactHash()
    {
        var old=JObject.Parse("{'version':'v1','nativeSourceDependency':'native-A','basePrefabDependency':'base-A','settings':{'scale':1.04},'bodyDependencies':['body-A']}");old["sources"]=Sources();
        string hash=(string)Call("FinalJsonSHA",old);var current=(JObject)old.DeepClone();current["sources"]=new JArray(new JObject{["path"]="new.cs",["sha"]="new"});
        Assert.That((bool)Call("OriginalFinalInputMatches",current,Sources(),hash),Is.True);
        Assert.That((bool)Call("OriginalFinalInputMatches",current,Sources(),new string('0',64)),Is.False);
        foreach(string field in new[]{"nativeSourceDependency","basePrefabDependency","bodyDependencies","settings"})
        {var changed=(JObject)current.DeepClone();changed[field]="changed";Assert.That((bool)Call("OriginalFinalInputMatches",changed,Sources(),hash),Is.False,field);}
    }
    [Test] public void OriginalProducerSourceBytesCannotBeSubstituted()
    {
        var input=new JObject{["id"]="real",["sources"]=Sources()};string hash=(string)Call("FinalJsonSHA",input);
        var changed=Sources();changed[0]["sha"]=new string('b',64);
        Assert.That((bool)Call("OriginalFinalInputMatches",input,changed,hash),Is.False);
        Assert.That((bool)Call("OriginalFinalInputMatches",input,new JArray(),hash),Is.False);
    }
    [Test] public void OriginalRowDigestAndOriginalInputIdentityAreVerifiable()
    {
        var input=new JObject{["id"]="real",["sources"]=Sources()};string hash=(string)Call("FinalJsonSHA",input);
        var row=new JObject{["final"]=new JObject{["inputHash"]=hash,["processorEvidence"]=new JObject{["bodies"]=new JObject{["Marta"]=new JObject{["seed"]="original"}}}}};
        var p=new JObject{["mode"]="revalidated-unmodified-topology",["originalInputReconstructedExactly"]=true,["originalOutputArtifactsVerified"]=true,
            ["originalInput"]=input,["priorOriginalInputHash"]=hash,["producerSources"]=Sources(),["originalRow"]=row,["oldRowSHA"]=(string)Call("FinalJsonSHA",row)};
        Assert.That((bool)Call("FinalRevalidationProvenanceAccepted",p),Is.True);
        row["final"]["processorEvidence"]["bodies"]["Marta"]["seed"]="tampered";
        Assert.That((bool)Call("FinalRevalidationProvenanceAccepted",p),Is.False);
        p["oldRowSHA"]=(string)Call("FinalJsonSHA",row);row["final"]["inputHash"]="another-input";p["oldRowSHA"]=(string)Call("FinalJsonSHA",row);
        Assert.That((bool)Call("FinalRevalidationProvenanceAccepted",p),Is.False);
    }
    Mesh source,output;
    [TearDown] public void Cleanup(){if(source!=null)UnityEngine.Object.DestroyImmediate(source);if(output!=null)UnityEngine.Object.DestroyImmediate(output);}
    void Meshes()
    {
        source=new Mesh();source.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up};source.triangles=new[]{0,1,2};source.normals=Enumerable.Repeat(Vector3.forward,3).ToArray();
        source.tangents=Enumerable.Repeat(new Vector4(1,0,0,1),3).ToArray();source.uv=new[]{Vector2.zero,Vector2.right,Vector2.up};
        source.bindposes=new[]{Matrix4x4.identity,Matrix4x4.Translate(Vector3.right)};source.boneWeights=Enumerable.Repeat(new BoneWeight{boneIndex0=0,weight0=1},3).ToArray();output=UnityEngine.Object.Instantiate(source);
    }
    bool Attributes()=>(bool)Call("UnmodifiedFinalAttributes",source,output,source.bindposes);
    [Test] public void ExactTopologyAndSkinAttributesAreRequired()
    {Meshes();Assert.That(Attributes(),Is.True);output.triangles=new[]{0,2,1};Assert.That(Attributes(),Is.False);}
    [Test] public void ChangedUvCannotReuseAnOriginalGeometryReceipt()
    {Meshes();output.uv=new[]{Vector2.one,Vector2.right,Vector2.up};Assert.That(Attributes(),Is.False);}
    [Test] public void ChangedSkinWeightsCannotReuseAnOriginalGeometryReceipt()
    {Meshes();var w=output.boneWeights;w[1].boneIndex0=1;output.boneWeights=w;Assert.That(Attributes(),Is.False);}
    [Test] public void ChangedBindposesCannotReuseAnOriginalGeometryReceipt()
    {Meshes();output.bindposes=new[]{Matrix4x4.Translate(Vector3.right)};Assert.That(Attributes(),Is.False);}
    static JObject Current()
    {
        var p=JObject.Parse(@"{'geometryMode':'revalidated-unmodified-topology','seed':'immutable-base-fit','runtimeConstruction':'BodyBones.Equip','reconstruction':{'maxMetres':0},'historicalSolverEvidence':{'seed':'immutable-base-fit'},'currentGeometryRevalidation':{'passed':true,'unchangedAttributesExact':true,'originalOutputAttributesPinned':true,'vertices':3,'triangles':1},'postBindSurfaceEnvelope':{'passedSampledEnvelope':true,'sampleCount':16,'targetClearanceMetres':0.002,'minimumSignedDistanceMetres':0.0021,'samplesBelowMinimum':0,'insideSamples':0,'ambiguousSamples':0,'onSurfaceSamples':0}}");
        foreach(string field in new[]{"baseToFinalDisplacement","commonBoneReferenceDisplacement"})p[field]=JObject.Parse(@"{'measurementScope':'all-output-vertices','positionScope':'actual-post-bind-world-vertices','measuredVertices':3,'maxMetres':0.01}");
        return p;
    }
    static bool Accepted(JObject p)=>(bool)Call("CurrentFinalRevalidationAccepted",p,3,1L);
    [Test] public void CurrentPhysicalMeasurementsCanPassWithoutPretendingToRunSolverAgain()
    {var p=Current();Assert.That(Accepted(p),Is.True);Assert.That(p["surfaceEnvelope"],Is.Null);Assert.That(p["topologyRefinement"],Is.Null);}
    [Test] public void PhysicallyFailingSavedOutputCannotBeRevalidated()
    {var p=Current();p["postBindSurfaceEnvelope"]["minimumSignedDistanceMetres"]=.0019f;Assert.That(Accepted(p),Is.False);p=Current();p["postBindSurfaceEnvelope"]["insideSamples"]=1;Assert.That(Accepted(p),Is.False);}
    [Test] public void AllOutputActualDisplacementLimitsStillApply()
    {foreach(string field in new[]{"baseToFinalDisplacement","commonBoneReferenceDisplacement"}){var p=Current();p[field]["maxMetres"]=.12001f;Assert.That(Accepted(p),Is.False);p=Current();p[field]["positionScope"]="planned";Assert.That(Accepted(p),Is.False);}}
}
}
#endif
