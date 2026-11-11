#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class AdaptiveEnvelopeMeshTests
{
    readonly List<Mesh> meshes=new();
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
    [TearDown] public void Cleanup(){foreach(var mesh in meshes)if(mesh!=null)Object.DestroyImmediate(mesh);meshes.Clear();}
    Mesh Own(Mesh mesh){meshes.Add(mesh);return mesh;}
    static void Equal(Vector3 a,Vector3 b){Assert.That(a.x,Is.EqualTo(b.x).Within(1e-6));Assert.That(a.y,Is.EqualTo(b.y).Within(1e-6));Assert.That(a.z,Is.EqualTo(b.z).Within(1e-6));}
    static int[] Indices(Mesh mesh)=>Enumerable.Range(0,mesh.subMeshCount).SelectMany(mesh.GetTriangles).ToArray();
    static BoneWeight Weight(int start)=>new(){boneIndex0=start,boneIndex1=start+1,boneIndex2=start+2,boneIndex3=start+3,weight0=.4f,weight1=.3f,weight2=.2f,weight3=.1f};
    Mesh Fixture(bool seam=false,bool colors32=false,bool wideWeights=false)
    {
        var mesh=Own(new Mesh{name="AdaptiveFixture",indexFormat=IndexFormat.UInt32});
        mesh.vertices=seam?new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(0,0,0),new Vector3(1,1,0),new Vector3(0,1,0)}:
            new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(0,1,0),new Vector3(3,0,0),new Vector3(4,0,0),new Vector3(3,1,0)};
        int n=mesh.vertexCount;mesh.normals=Enumerable.Range(0,n).Select(i=>seam&&i>=3?new Vector3(0,.8f,.6f):Vector3.forward).ToArray();
        mesh.tangents=Enumerable.Range(0,n).Select(i=>new Vector4(1,0,0,seam&&i>=3?-1:1)).ToArray();
        mesh.boneWeights=Enumerable.Range(0,n).Select(i=>Weight(!seam&&wideWeights&&i==1?4:0)).ToArray();
        mesh.bindposes=Enumerable.Range(0,8).Select(i=>Matrix4x4.Translate(new Vector3(i*.01f,0,0))).ToArray();
        for(int channel=0;channel<8;channel++)
        {
            var uv=Enumerable.Range(0,n).Select(i=>new Vector4(i*.1f+channel,seam&&i>=3?i*.2f+.3f:i*.2f,channel*.25f,.75f)).ToList();
            if(channel%3==0)mesh.SetUVs(channel,uv.Select(v=>new Vector2(v.x,v.y)).ToList());
            else if(channel%3==1)mesh.SetUVs(channel,uv.Select(v=>new Vector3(v.x,v.y,v.z)).ToList());else mesh.SetUVs(channel,uv);
        }
        if(colors32)mesh.colors32=Enumerable.Range(0,n).Select(i=>new Color32((byte)(i*17),30,60,255)).ToArray();
        else mesh.colors=Enumerable.Range(0,n).Select(i=>new Color(i*.07f,.12345f,.34567f,1)).ToArray();
        mesh.subMeshCount=2;mesh.SetTriangles(seam?new[]{0,1,2}:new[]{0,1,2,4,5,6},0);mesh.SetTriangles(seam?new[]{3,4,5}:new[]{0,2,3},1);
        return mesh;
    }
    (Mesh mesh,Vector3[] seed,Vector3[] normals,JObject audit) Split(Mesh source,int[] marked)
    {
        var seed=source.vertices.Select(v=>v+new Vector3(5,2,-3)).ToArray();var normals=source.normals;
        var parameters=new object[]{source,seed,normals,marked,null,null,null};
        var method=Preparation.GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Single(m=>m.Name=="SubdivideEnvelopeFaces"&&m.GetParameters().Length==7);
        var result=Own((Mesh)method.Invoke(null,parameters));
        return(result,(Vector3[])parameters[4],(Vector3[])parameters[5],(JObject)parameters[6]);
    }
    static double Area(Mesh mesh)=>Indices(mesh).Select((_,i)=>i).Where(i=>i%3==0).Sum(i=>{
        var t=Indices(mesh);var p=mesh.vertices;return .5d*Vector3.Cross(p[t[i+1]]-p[t[i]],p[t[i+2]]-p[t[i]]).magnitude;});
    static int EdgeUse(Mesh mesh,int a,int b)
    {
        int count=0;var t=Indices(mesh);for(int i=0;i<t.Length;i+=3)for(int j=0;j<3;j++){int x=t[i+j],y=t[i+(j+1)%3];if(x==a&&y==b||x==b&&y==a)count++;}return count;
    }
    [Test] public void SharedEdgeConformsUnselectedNeighborAndPreservesFarFaceMaterialsAreaWinding()
    {
        var source=Fixture();var vertices=source.vertices;var original=Indices(source);double area=Area(source);
        var result=Split(source,new[]{0});Assert.That((bool)result.audit["passed"],Is.True);Assert.That(result.mesh==source,Is.False);Assert.That(result.mesh.subMeshCount,Is.EqualTo(2));
        Assert.That(result.mesh.GetTriangles(0).Length/3,Is.EqualTo(7));Assert.That(result.mesh.GetTriangles(1).Length/3,Is.EqualTo(4));
        Assert.That(Area(result.mesh),Is.EqualTo(area).Within(1e-6));
        var p=result.mesh.vertices;var t=Indices(result.mesh);
        for(int i=0;i<t.Length;i+=3)Assert.That(Vector3.Cross(p[t[i+1]]-p[t[i]],p[t[i+2]]-p[t[i]]).z,Is.GreaterThan(0));
        int middle=Enumerable.Range(vertices.Length,p.Length-vertices.Length).Single(i=>(p[i]-new Vector3(.5f,.5f,0)).sqrMagnitude<1e-12f);
        Assert.That(EdgeUse(result.mesh,0,2),Is.Zero);Assert.That(EdgeUse(result.mesh,0,middle),Is.EqualTo(2));Assert.That(EdgeUse(result.mesh,middle,2),Is.EqualTo(2));
        Assert.That(Enumerable.Range(0,result.mesh.GetTriangles(0).Length/3).Any(i=>result.mesh.GetTriangles(0).Skip(i*3).Take(3).SequenceEqual(new[]{4,5,6})),Is.True);
        Assert.That(source.vertices,Is.EqualTo(vertices));Assert.That(Indices(source),Is.EqualTo(original));
    }
    [Test] public void SplitIndexUvSeamGetsConformingSeparateMidpointsWithoutWeldingAttributes()
    {
        var source=Fixture(true);var result=Split(source,new[]{0});var p=result.mesh.vertices;
        var mids=Enumerable.Range(source.vertexCount,p.Length-source.vertexCount).Where(i=>(p[i]-new Vector3(.5f,.5f,0)).sqrMagnitude<1e-12f).ToArray();
        Assert.That(mids.Length,Is.EqualTo(2));
        var uv=new List<Vector4>();result.mesh.GetUVs(0,uv);Assert.That(uv[mids[0]],Is.Not.EqualTo(uv[mids[1]]));
        Assert.That(result.mesh.normals[mids[0]],Is.Not.EqualTo(result.mesh.normals[mids[1]]));
        Assert.That(Mathf.Abs(result.mesh.tangents[mids[0]].w),Is.EqualTo(1));Assert.That(Mathf.Abs(result.mesh.tangents[mids[1]].w),Is.EqualTo(1));
        Equal(result.seed[mids[0]],result.seed[mids[1]]);
        for(int i=0;i<Indices(result.mesh).Length;i+=3)
        {
            var t=Indices(result.mesh);for(int j=0;j<3;j++)
            {
                var a=p[t[i+j]];var b=p[t[i+(j+1)%3]];
                Assert.That((a==Vector3.zero&&b==new Vector3(1,1,0))||(b==Vector3.zero&&a==new Vector3(1,1,0)),Is.False,"Unsplit geometric seam edge creates T-junction");
            }
        }
    }
    [TestCase(false)] [TestCase(true)]
    public void AllUvDimensionsOriginalAttributesAndColorFormatSurvive(bool colors32)
    {
        var source=Fixture(false,colors32);var oldJson=EditorJsonUtility.ToJson(source);var result=Split(source,new[]{0});var mesh=result.mesh;
        for(int i=0;i<source.vertexCount;i++)
        {Equal(mesh.vertices[i],source.vertices[i]);Equal(mesh.normals[i],source.normals[i]);Assert.That(mesh.tangents[i],Is.EqualTo(source.tangents[i]));Assert.That(mesh.boneWeights[i],Is.EqualTo(source.boneWeights[i]));}
        for(int channel=0;channel<8;channel++)
        {
            var attribute=(VertexAttribute)((int)VertexAttribute.TexCoord0+channel);
            Assert.That(mesh.GetVertexAttributeDimension(attribute),Is.EqualTo(source.GetVertexAttributeDimension(attribute)));
            var before=new List<Vector4>();var after=new List<Vector4>();source.GetUVs(channel,before);mesh.GetUVs(channel,after);
            Assert.That(after.Take(source.vertexCount),Is.EqualTo(before));
        }
        Assert.That(mesh.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(source.GetVertexAttributeFormat(VertexAttribute.Color)));
        if(colors32)Assert.That(mesh.colors32.Take(source.vertexCount),Is.EqualTo(source.colors32));else Assert.That(mesh.colors.Take(source.vertexCount),Is.EqualTo(source.colors));
        Assert.That(mesh.bindposes,Is.EqualTo(source.bindposes));Assert.That(EditorJsonUtility.ToJson(source),Is.EqualTo(oldJson));
    }
    [Test] public void InterpolatedSkinWeightsAreFiniteNormalizedAndKeepStrongestFour()
    {
        var source=Fixture(false,false,true);var result=Split(source,new[]{0});var p=result.mesh.vertices;
        Assert.That((float)result.audit["attributes"]["maximumDroppedWeightFraction"],Is.EqualTo(.3f).Within(1e-6));
        Assert.That((bool)result.audit["passed"],Is.False,"The5%dropcapmustfailclosed");
        int midpoint=Enumerable.Range(source.vertexCount,p.Length-source.vertexCount).Single(i=>(p[i]-new Vector3(.5f,0,0)).sqrMagnitude<1e-12f);
        var w=result.mesh.boneWeights[midpoint];var actual=new Dictionary<int,float>{{w.boneIndex0,w.weight0},{w.boneIndex1,w.weight1},{w.boneIndex2,w.weight2},{w.boneIndex3,w.weight3}};
        Assert.That(actual.Keys,Is.EquivalentTo(new[]{0,4,1,5}));Assert.That(actual[0],Is.EqualTo(2f/7).Within(1e-6));Assert.That(actual[4],Is.EqualTo(2f/7).Within(1e-6));
        Assert.That(actual[1],Is.EqualTo(3f/14).Within(1e-6));Assert.That(actual[5],Is.EqualTo(3f/14).Within(1e-6));
        foreach(var weight in result.mesh.boneWeights)
        {var values=new[]{weight.weight0,weight.weight1,weight.weight2,weight.weight3};Assert.That(values.All(v=>!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=0),Is.True);Assert.That(values.Sum(),Is.EqualTo(1).Within(2e-6));}
    }
    Mesh SphereBody()
    {
        var vertices=new[]{Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
        int[] indices={0,2,4,2,1,4,1,3,4,3,0,4,2,0,5,1,2,5,3,1,5,0,3,5};
        var faces=new List<(Vector3 a,Vector3 b,Vector3 c)>();for(int i=0;i<indices.Length;i+=3)faces.Add((vertices[indices[i]],vertices[indices[i+1]],vertices[indices[i+2]]));
        for(int round=0;round<3;round++)
        {
            var next=new List<(Vector3,Vector3,Vector3)>();
            foreach(var(a,b,c)in faces){var ab=(a+b).normalized;var bc=(b+c).normalized;var ca=(c+a).normalized;next.Add((a,ab,ca));next.Add((ab,b,bc));next.Add((ca,bc,c));next.Add((ab,bc,ca));}faces=next;
        }
        var mesh=Own(new Mesh());mesh.vertices=faces.SelectMany(f=>new[]{f.a,f.b,f.c}).ToArray();mesh.triangles=Enumerable.Range(0,mesh.vertexCount).ToArray();mesh.RecalculateNormals();return mesh;
    }
    (Mesh mesh,Vector3[] world,JObject proof) Refine(Mesh source,Mesh body,int passes=16)
    {
        var treeType=Preparation.Assembly.GetType("HexLive.UnityDebug.Editor.TriangleTree",true);
        var tree=treeType.GetMethod("ForMesh").Invoke(null,new object[]{body,Matrix4x4.identity});
        var parameters=new object[]{source,source.vertices,source.normals,tree,.006f,.002f,passes,.1f,null,null,null};
        var method=Preparation.GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Single(m=>m.Name=="RefineEnvelopeMesh"&&m.GetParameters().Length==11);
        var mesh=Own((Mesh)method.Invoke(null,parameters));return(mesh,(Vector3[])parameters[8],(JObject)parameters[10]);
    }
    [Test] public void AlreadyClearInputReturnsPrivateTopologyWithZeroRefinement()
    {
        var source=Own(new Mesh());source.vertices=new[]{new Vector3(1.2f,0,0),new Vector3(1.2f,.1f,0),new Vector3(1.2f,0,.1f)};
        source.triangles=new[]{0,1,2};source.RecalculateNormals();var before=source.vertices;
        var result=Refine(source,SphereBody());Assert.That((bool)result.proof["passed"],Is.True);Assert.That((int)result.proof["refinementRounds"],Is.Zero);
        Assert.That(result.mesh==source,Is.False);Assert.That(result.world,Is.EqualTo(before));Assert.That(source.vertices,Is.EqualTo(before));
    }
    [Test] public void DuplicatedUvSeamRemainsCoincidentAfterActualCurvedBodyDeformation()
    {
        var source=Fixture(true);source.vertices=new[]{new Vector3(.99f,-.2f,-.2f),new Vector3(.99f,.2f,-.2f),new Vector3(.99f,.2f,.2f),
            new Vector3(.99f,-.2f,-.2f),new Vector3(.99f,.2f,.2f),new Vector3(.99f,-.2f,.2f)};
        var original=source.vertices;var result=Refine(source,SphereBody());Assert.That((bool)result.proof["passed"],Is.True);
        foreach(var pair in new[]{(0,3),(2,4)})for(int component=0;component<3;component++)
            Assert.That(BitConverter.SingleToInt32Bits(result.world[pair.Item1][component]),Is.EqualTo(BitConverter.SingleToInt32Bits(result.world[pair.Item2][component])));
        var uv=new List<Vector4>();result.mesh.GetUVs(0,uv);Assert.That(uv[0],Is.Not.EqualTo(uv[3]));Assert.That(uv[2],Is.Not.EqualTo(uv[4]));
        Assert.That(source.vertices,Is.EqualTo(original));Assert.That((bool)result.proof["sourceUnchanged"],Is.True);
    }
    Mesh ImpossibleTriangle(bool wideWeights=false)
    {
        var mesh=Own(new Mesh());mesh.vertices=new[]{new Vector3(1.3f,0,0),new Vector3(-.65f,0,1.125833f),new Vector3(-.65f,0,-1.125833f)};
        mesh.triangles=new[]{0,1,2};mesh.RecalculateNormals();
        if(wideWeights){mesh.bindposes=Enumerable.Repeat(Matrix4x4.identity,8).ToArray();mesh.boneWeights=new[]{Weight(0),Weight(4),Weight(0)};}
        return mesh;
    }
    [Test] public void ImpossibleWithinDisplacementCapFailsRatherThanRelaxingPhysicalFloorOrBudgets()
    {
        var source=ImpossibleTriangle();var result=Refine(source,SphereBody(),1);
        Assert.That((bool)result.proof["passed"],Is.False);Assert.That((int)result.proof["refinementRounds"],Is.LessThanOrEqualTo(3));
        Assert.That((int)result.proof["outputTriangles"]-(int)result.proof["inputTriangles"],Is.LessThanOrEqualTo((int)result.proof["additionalTriangleBudget"]));
        Assert.That((float)result.proof["surfaceEnvelope"]["maximumAllowedVertexDisplacementMetres"],Is.EqualTo(.1f));
        Assert.That((float)result.proof["surfaceEnvelope"]["minimumAcceptedClearanceMetres"],Is.EqualTo(.002f));
    }
    [Test] public void AdaptiveSkinWeightLossFailsClosedAtApprovedFivePercentCap()
    {
        var result=Refine(ImpossibleTriangle(true),SphereBody(),1);
        Assert.That((bool)result.proof["passed"],Is.False);Assert.That((string)result.proof["stoppedReason"],Is.EqualTo("bone-weight-drop-cap"));
        Assert.That((float)result.proof["attributes"]["maximumDroppedWeightFraction"],Is.GreaterThan(.05f));
        Assert.That((bool)result.proof["attributes"]["boneWeightDropPassed"],Is.False);
    }
    [Test] public void AddedVerticesUpgradeIndexFormatBeforeCrossingUInt16Limit()
    {
        var source=Own(new Mesh{indexFormat=IndexFormat.UInt16});var points=new Vector3[65534];points[1]=Vector3.right;points[2]=Vector3.up;
        source.vertices=points;source.normals=points.Select(_=>Vector3.forward).ToArray();source.triangles=new[]{0,1,2};
        var result=Split(source,new[]{0});Assert.That(result.mesh.vertexCount,Is.GreaterThan(65535));Assert.That(result.mesh.indexFormat,Is.EqualTo(IndexFormat.UInt32));
        Assert.That(Indices(result.mesh).Max(),Is.LessThan(result.mesh.vertexCount));Assert.That(source.indexFormat,Is.EqualTo(IndexFormat.UInt16));
    }
}
}
#endif
