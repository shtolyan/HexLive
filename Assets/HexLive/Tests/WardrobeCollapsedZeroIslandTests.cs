#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class WardrobeCollapsedZeroIslandTests
{
    readonly List<Object> owned=new List<Object>();
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation",false)).Single(t=>t!=null);
    static MethodInfo Method(string name)=>Preparation.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic);
    T Own<T>(T value)where T:Object{owned.Add(value);return value;}
    [TearDown]public void Cleanup(){foreach(var value in owned.AsEnumerable().Reverse())if(value!=null)Object.DestroyImmediate(value);owned.Clear();}
    Mesh Native()
    {
        var m=Own(new Mesh{name="NativeZeroIslandControl",indexFormat=IndexFormat.UInt32});
        m.vertices=new[]{new Vector3(-.5f,1,0),new Vector3(.5f,1,0),new Vector3(0,2,0),
            new Vector3(.1f,1.4f,.1f),new Vector3(.2f,1.4f,.1f),new Vector3(.1f,1.5f,.1f)};
        m.normals=Enumerable.Repeat(Vector3.forward,6).ToArray();m.tangents=Enumerable.Repeat(new Vector4(1,0,0,-1),6).ToArray();
        m.colors32=Enumerable.Range(0,6).Select(i=>new Color32((byte)(11+i),(byte)(22+i),(byte)(33+i),(byte)(244-i))).ToArray();
        m.SetUVs(0,Enumerable.Range(0,6).Select(i=>new Vector2(i*.1f,i*.2f)).ToList());
        m.SetUVs(1,Enumerable.Range(0,6).Select(i=>new Vector3(i*.3f,i*.4f,i*.5f)).ToList());
        m.SetUVs(2,Enumerable.Range(0,6).Select(i=>new Vector4(i*.6f,i*.7f,i*.8f,i*.9f)).ToList());
        using(var uv=new NativeArray<float>(Enumerable.Range(0,6).Select(i=>i*.25f).ToArray(),Allocator.Temp))m.SetUVs(3,uv);
        m.bindposes=Enumerable.Repeat(Matrix4x4.identity,4).ToArray();
        // Imported legacy meshes can contain all-zero BoneWeight entries.
        // The modern SetBoneWeights API rejects a zero influence COUNT, so it
        // cannot construct the real imported representation being exercised.
        m.boneWeights=Enumerable.Range(0,6).Select(i=>i<3?new BoneWeight{boneIndex0=0,weight0=.40150553f,boneIndex1=1,weight1=.331295937f,boneIndex2=2,weight2=.143652186f,boneIndex3=3,weight3=.123546347f}:default).ToArray();
        m.subMeshCount=2;m.SetTriangles(new[]{0,1,2},0);m.SetTriangles(new[]{3,4,5},1);m.RecalculateBounds();return m;
    }
    JObject NativeProof(Mesh native)
    {
        var root=Own(new GameObject("ActualNativeZeroIslandSkin"));var bones=Enumerable.Range(0,4).Select(i=>{var bone=new GameObject("bone"+i);bone.transform.SetParent(root.transform,false);return bone.transform;}).ToArray();
        var skin=root.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=native;skin.bones=bones;skin.rootBone=bones[0];
        var proof=(JObject)Method("CaptureBakedCollapsedZeroEvidence").Invoke(null,new object[]{skin});
        Assert.That((bool)proof["passed"],Is.True,"Actual Unity BakeMesh must collapse unweighted native triangles");
        Assert.That((int)proof["rawNondegenerateFaces"],Is.EqualTo(1));Assert.That((int)proof["actualBakedNondegenerateFaces"],Is.Zero);return proof;
    }
    Mesh Derived(Mesh native)
    {var m=Own(Object.Instantiate(native));var v=m.vertices;v[3]=v[4]=v[5]=Vector3.zero;m.vertices=v;m.RecalculateBounds();return m;}
    Mesh Clean(Mesh source,JObject proof,out object[] args)
    {
        var p=source.vertices;var r0=p.ToArray();var r1=p.Select((v,i)=>i<3?v+new Vector3(.01f,.02f,.03f):Vector3.zero).ToArray();
        args=new object[]{source,p,source.normals,new[]{r0,r1},proof,null,null,null,null};
        return (Mesh)Method("CleanCollapsedUnweightedDerived").Invoke(null,args);
    }
    static void SameRawKeptVertices(Mesh source,Mesh result,int[] kept)
    {
        // Assert authored channels rather than Unity's internal skin layout.
        // Full influence count/value equality is asserted separately below.
        var expected=new[]{VertexAttribute.Position,VertexAttribute.Normal,VertexAttribute.Tangent,VertexAttribute.Color,
            VertexAttribute.TexCoord0,VertexAttribute.TexCoord1,VertexAttribute.TexCoord2,VertexAttribute.TexCoord3};
        Assert.That(result.GetVertexAttributes().Select(d=>d.attribute).Where(a=>a!=VertexAttribute.BlendWeight&&a!=VertexAttribute.BlendIndices),Is.EquivalentTo(expected));
        Assert.That(result.vertices,Is.EqualTo(kept.Select(i=>source.vertices[i]).ToArray()));
        Assert.That(result.normals,Is.EqualTo(kept.Select(i=>source.normals[i]).ToArray()));
        Assert.That(result.tangents,Is.EqualTo(kept.Select(i=>source.tangents[i]).ToArray()));
        Assert.That(result.colors32,Is.EqualTo(kept.Select(i=>source.colors32[i]).ToArray()));
        Assert.That(result.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(VertexAttributeFormat.UNorm8));
        foreach(var attribute in expected.Where(a=>a!=VertexAttribute.Color))
            Assert.That(result.GetVertexAttributeFormat(attribute),Is.EqualTo(VertexAttributeFormat.Float32));
        for(int channel=0;channel<4;channel++)
        {
            var attribute=(VertexAttribute)((int)VertexAttribute.TexCoord0+channel);
            Assert.That(result.GetVertexAttributeDimension(attribute),Is.EqualTo(new[]{2,3,4,1}[channel]));
            var original=new List<Vector4>();var actual=new List<Vector4>();source.GetUVs(channel,original);result.GetUVs(channel,actual);
            Assert.That(actual.Count,Is.EqualTo(kept.Length));
            for(int i=0;i<kept.Length;i++)for(int component=0;component<4;component++)
                Assert.That(BitConverter.SingleToInt32Bits(actual[i][component]),Is.EqualTo(BitConverter.SingleToInt32Bits(original[kept[i]][component])),"Exact UV bits channel="+channel+" component="+component);
        }
    }
    [Test]public void ActualBakeEvidenceAllowsOnlyInvisibleDerivedIslandAndPreservesAllAttributes()
    {
        var native=Native();var proof=NativeProof(native);var source=Derived(native);var before=source.vertices;var beforeTriangles=source.triangles;
        var result=Own(Clean(source,proof,out var args));Assert.That(result,Is.Not.Null);Assert.That(result.vertexCount,Is.EqualTo(3));
        Assert.That(result.subMeshCount,Is.EqualTo(2));Assert.That(result.GetTriangles(0),Is.EqualTo(new[]{0,1,2}));Assert.That(result.GetTriangles(1),Is.Empty);
        var bounds=result.GetSubMesh(0).bounds;foreach(var vertex in result.vertices)
            Assert.That(bounds.SqrDistance(vertex),Is.LessThan(1e-12f),"Retained submesh bounds cover its referenced vertices");
        SameRawKeptVertices(source,result,new[]{0,1,2});Assert.That(result.bindposes,Is.EqualTo(source.bindposes));
        var sourceCounts=source.GetBonesPerVertex().ToArray();var sourceWeights=source.GetAllBoneWeights().ToArray();
        Assert.That(result.GetBonesPerVertex().ToArray(),Is.EqualTo(sourceCounts.Take(3).ToArray()));
        Assert.That(result.GetAllBoneWeights().ToArray(),Is.EqualTo(sourceWeights.Take(sourceCounts.Take(3).Sum(x=>(int)x)).ToArray()));
        Assert.That((Vector3[])args[5],Is.EqualTo(before.Take(3).ToArray()));Assert.That((Vector3[])args[6],Is.EqualTo(source.normals.Take(3).ToArray()));
        var refs=(Vector3[][])args[7];Assert.That(refs.Length,Is.EqualTo(2));Assert.That(refs[1],Is.EqualTo(before.Take(3).Select(v=>v+new Vector3(.01f,.02f,.03f)).ToArray()));
        Assert.That((bool)((JObject)args[8])["passed"],Is.True);Assert.That((int)((JObject)args[8])["removedVertices"],Is.EqualTo(3));
        Assert.That(source.vertices,Is.EqualTo(before));Assert.That(source.triangles,Is.EqualTo(beforeTriangles));
    }
    [Test]public void NonzeroRawNativeIslandIsNeverRemovedDirectly()
    {
        var native=Native();var proof=NativeProof(native);
        var error=Assert.Throws<TargetInvocationException>(()=>Clean(native,proof,out _));Assert.That(error.InnerException,Is.TypeOf<InvalidOperationException>());
    }
    [Test]public void MixedWeightedAndUnweightedFaceIsRejected()
    {
        var native=Native();var proof=NativeProof(native);var source=Derived(native);source.SetTriangles(new[]{0,3,4},1);
        var error=Assert.Throws<TargetInvocationException>(()=>Clean(source,proof,out _));Assert.That(error.InnerException,Is.TypeOf<InvalidOperationException>());
    }
    [Test]public void MissingActualNativeEvidenceIsRejected()
    {
        var source=Derived(Native());var error=Assert.Throws<TargetInvocationException>(()=>Clean(source,new JObject{["passed"]=true},out _));
        Assert.That(error.InnerException,Is.TypeOf<InvalidOperationException>());
    }
    [Test]public void NonzeroCompanionReferenceIsNotSilentlyDropped()
    {
        var native=Native();var proof=NativeProof(native);var source=Derived(native);var p=source.vertices;var reference=p.ToArray();reference[4]=Vector3.up;
        var args=new object[]{source,p,source.normals,new[]{reference},proof,null,null,null,null};
        var error=Assert.Throws<TargetInvocationException>(()=>Method("CleanCollapsedUnweightedDerived").Invoke(null,args));Assert.That(error.InnerException,Is.TypeOf<InvalidOperationException>());
    }
    [Test]public void FullyWeightedMeshIsUnchangedWithoutCleanupEvidence()
    {
        var source=Native();source.boneWeights=Enumerable.Repeat(new BoneWeight{boneIndex0=0,weight0=1},6).ToArray();
        var result=Clean(source,null,out var args);Assert.That(result,Is.Null);Assert.That((bool)((JObject)args[8])["applicable"],Is.False);
        Assert.That((Vector3[])args[5],Is.EqualTo(source.vertices));
    }
    [Test]public void NonfiniteRetainedCompanionReferenceFailsClosed()
    {
        var native=Native();var proof=NativeProof(native);var source=Derived(native);var p=source.vertices;var reference=p.ToArray();reference[1]=new Vector3(float.NaN,0,0);
        var args=new object[]{source,p,source.normals,new[]{reference},proof,null,null,null,null};
        var error=Assert.Throws<TargetInvocationException>(()=>Method("CleanCollapsedUnweightedDerived").Invoke(null,args));Assert.That(error.InnerException,Is.TypeOf<InvalidOperationException>());
    }
}
}
#endif
