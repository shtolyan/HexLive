#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
// Offline actual Unity Mesh controls. No imported model, service or registry.
public sealed class AdaptiveEnvelopeReferenceTests
{
    readonly List<Mesh> _owned=new();
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
    [TearDown] public void Cleanup(){foreach(var mesh in _owned)if(mesh!=null)Object.DestroyImmediate(mesh);_owned.Clear();}
    Mesh Own(Mesh mesh){_owned.Add(mesh);return mesh;}
    static MethodInfo Method(string name,int count)=>Preparation.GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==count);
    static Matrix4x4 ReferenceA=>Matrix4x4.TRS(new Vector3(3,-2,5),Quaternion.Euler(17,35,11),new Vector3(2,3,.5f));
    static Matrix4x4 ReferenceB=>Matrix4x4.TRS(new Vector3(-7,4,2),Quaternion.Euler(-9,77,13),new Vector3(.4f,1.5f,2));
    Mesh Triangle()
    {
        var mesh=Own(new Mesh{name="CompanionReferenceFixture"});mesh.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up};
        mesh.triangles=new[]{0,1,2};mesh.normals=Enumerable.Repeat(Vector3.forward,3).ToArray();mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.up};return mesh;
    }
    static Vector3[] Transform(Matrix4x4 matrix,Vector3[] points)=>points.Select(matrix.MultiplyPoint3x4).ToArray();
    static Vector3[][] Copy(Vector3[][] arrays)=>arrays.Select(a=>(Vector3[])a.Clone()).ToArray();
    static void Equal(Vector3[] actual,Vector3[] expected,float tolerance=1e-5f)
    {
        Assert.That(actual.Length,Is.EqualTo(expected.Length));
        for(int i=0;i<actual.Length;i++)Assert.That(Vector3.Distance(actual[i],expected[i]),Is.LessThanOrEqualTo(tolerance),"vertex "+i);
    }
    static void Unchanged(Vector3[][] actual,Vector3[][] expected)
    {Assert.That(actual.Length,Is.EqualTo(expected.Length));for(int i=0;i<actual.Length;i++)Assert.That(actual[i],Is.EqualTo(expected[i]),"input companion "+i);}
    (Mesh mesh,Vector3[] seed,Vector3[] normals,JObject proof,Vector3[][] references) Split(Mesh mesh,Vector3[] seed,Vector3[] normals,int[] faces,Vector3[][] references)
    {
        var args=new object[]{mesh,seed,normals,faces,references,null,null,null,null};
        var result=Own((Mesh)Method("SubdivideEnvelopeFacesWithReferences",9).Invoke(null,args));
        return(result,(Vector3[])args[5],(Vector3[])args[6],(JObject)args[7],(Vector3[][])args[8]);
    }
    object Tree(Mesh body)
    {
        var type=Preparation.Assembly.GetType("HexLive.UnityDebug.Editor.TriangleTree",true);
        return type.GetMethod("ForMesh").Invoke(null,new object[]{body,Matrix4x4.identity});
    }
    Mesh Body()
    {
        var mesh=Own(new Mesh{name="ClosedReferenceBody"});
        mesh.vertices=new[]{Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
        mesh.triangles=new[]{0,2,4,2,1,4,1,3,4,3,0,4,2,0,5,1,2,5,3,1,5,0,3,5};mesh.RecalculateNormals();return mesh;
    }

    [Test]
    public void CompanionArraysFollowMidpointsAndCentroidsThroughTwoSubdivisionRounds()
    {
        var source=Triangle();string before=EditorJsonUtility.ToJson(source);var local=source.vertices;
        var seed=Transform(Matrix4x4.Translate(new Vector3(5,2,-3)),local);
        var companions=new[]{Transform(ReferenceA,local),Transform(ReferenceB,local)};var original=Copy(companions);var seedBefore=(Vector3[])seed.Clone();
        var first=Split(source,seed,source.normals,new[]{0},companions);
        Assert.That(first.mesh.vertexCount,Is.EqualTo(7)); // Three corners, three edge mids, one centroid.
        Equal(first.references[0],Transform(ReferenceA,first.mesh.vertices));Equal(first.references[1],Transform(ReferenceB,first.mesh.vertices));
        Assert.That(ReferenceEquals(first.references[0],companions[0]),Is.False);
        var firstBefore=Copy(first.references);string firstMeshBefore=EditorJsonUtility.ToJson(first.mesh);
        var second=Split(first.mesh,first.seed,first.normals,new[]{0,3},first.references);
        Assert.That(second.mesh.vertexCount,Is.GreaterThan(first.mesh.vertexCount));
        Equal(second.references[0],Transform(ReferenceA,second.mesh.vertices));Equal(second.references[1],Transform(ReferenceB,second.mesh.vertices));
        Equal(second.seed,Transform(Matrix4x4.Translate(new Vector3(5,2,-3)),second.mesh.vertices));
        Unchanged(companions,original);Unchanged(first.references,firstBefore);Assert.That(seed,Is.EqualTo(seedBefore));
        Assert.That(EditorJsonUtility.ToJson(source),Is.EqualTo(before));Assert.That(EditorJsonUtility.ToJson(first.mesh),Is.EqualTo(firstMeshBefore));
    }

    [TestCase(0)] [TestCase(2)] [TestCase(4)]
    public void CompanionLengthMismatchFailsWithoutChangingTheMeshOrOtherCompanions(int wrongLength)
    {
        var mesh=Triangle();var before=EditorJsonUtility.ToJson(mesh);var valid=Transform(ReferenceA,mesh.vertices);var validBefore=(Vector3[])valid.Clone();
        var error=Assert.Throws<TargetInvocationException>(()=>Split(mesh,mesh.vertices,mesh.normals,new[]{0},new[]{valid,new Vector3[wrongLength]}));
        Assert.That(error.InnerException,Is.InstanceOf<ArgumentException>());Assert.That(valid,Is.EqualTo(validBefore));Assert.That(EditorJsonUtility.ToJson(mesh),Is.EqualTo(before));
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
    public void NonfiniteCompanionFailsWithoutChangingPersistentOrValidInputData(float invalid)
    {
        var mesh=Triangle();var before=EditorJsonUtility.ToJson(mesh);var invalidReference=mesh.vertices;invalidReference[1].x=invalid;
        var valid=Transform(ReferenceA,mesh.vertices);var validBefore=(Vector3[])valid.Clone();
        var error=Assert.Throws<TargetInvocationException>(()=>Split(mesh,mesh.vertices,mesh.normals,new[]{0},new[]{valid,invalidReference}));
        Assert.That(error.InnerException,Is.InstanceOf<InvalidOperationException>());Assert.That(valid,Is.EqualTo(validBefore));Assert.That(EditorJsonUtility.ToJson(mesh),Is.EqualTo(before));
    }

    [Test]
    public void NullCompanionIsRejectedInsteadOfBeingSilentlyDropped()
    {
        var mesh=Triangle();var error=Assert.Throws<TargetInvocationException>(()=>Split(mesh,mesh.vertices,mesh.normals,new[]{0},new Vector3[][]{null}));
        Assert.That(error.InnerException,Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void EmptyCompanionsKeepTheExistingSubdivisionApiGeometryAndAttributes()
    {
        var source=Triangle();var oldArgs=new object[]{source,source.vertices,source.normals,new[]{0},null,null,null};
        var old=Own((Mesh)Method("SubdivideEnvelopeFaces",7).Invoke(null,oldArgs));
        var next=Split(source,source.vertices,source.normals,new[]{0},Array.Empty<Vector3[]>());
        Assert.That(next.references,Is.Empty);Assert.That(next.mesh.vertices,Is.EqualTo(old.vertices));Assert.That(next.mesh.triangles,Is.EqualTo(old.triangles));
        Assert.That(next.mesh.normals,Is.EqualTo(old.normals));Assert.That(next.mesh.uv,Is.EqualTo(old.uv));Assert.That(next.seed,Is.EqualTo((Vector3[])oldArgs[4]));
        Assert.That(next.normals,Is.EqualTo((Vector3[])oldArgs[5]));Assert.That(JToken.DeepEquals(next.proof,(JObject)oldArgs[6]),Is.True);
    }

    [Test]
    public void ZeroCompanionRefinerPreservesTheExistingElevenParameterApi()
    {
        var source=Triangle();source.vertices=new[]{new Vector3(2,0,0),new Vector3(2,.1f,0),new Vector3(2,0,.1f)};var tree=Tree(Body());
        var oldArgs=new object[]{source,source.vertices,source.normals,tree,.006f,.002f,16,.1f,null,null,null};
        var old=Own((Mesh)Method("RefineEnvelopeMesh",11).Invoke(null,oldArgs));
        var args=new object[]{source,source.vertices,source.normals,tree,.006f,.002f,16,.1f,Array.Empty<Vector3[]>(),null,null,null,null};
        var next=Own((Mesh)Method("RefineEnvelopeMeshWithReferences",13).Invoke(null,args));
        Assert.That(next.vertices,Is.EqualTo(old.vertices));Assert.That(next.triangles,Is.EqualTo(old.triangles));Assert.That((Vector3[])args[9],Is.EqualTo((Vector3[])oldArgs[8]));
        Assert.That((Vector3[])args[10],Is.EqualTo((Vector3[])oldArgs[9]));Assert.That((Vector3[][])args[12],Is.Empty);
        Assert.That(JToken.DeepEquals((JObject)args[11],(JObject)oldArgs[10]),Is.True);
    }

    [Test]
    public void NewMidpointAndCentroidCanViolateCommonReferenceWhileEveryOldVertexPasses()
    {
        var mesh=Triangle();var common=mesh.vertices;var own=common.Select(v=>v+Vector3.forward*.1f).ToArray();
        var result=Split(mesh,own,mesh.normals,new[]{0},new[]{own,common});var fitted=(Vector3[])result.seed.Clone();
        for(int i=0;i<fitted.Length;i++)fitted[i]+=Vector3.forward*(i<mesh.vertexCount?-.1f:.1f);
        Assert.That(fitted.Take(mesh.vertexCount).Select((p,i)=>Vector3.Distance(p,common[i])).Max(),Is.LessThan(1e-6f),"The old first-N check incorrectly accepts this case");
        Assert.That(fitted.Select((p,i)=>Vector3.Distance(p,result.references[0][i])).Max(),Is.LessThanOrEqualTo(.100001f),"Every point still respects its own10cm displacement cap");
        var violations=Enumerable.Range(mesh.vertexCount,fitted.Length-mesh.vertexCount).Where(i=>Vector3.Distance(fitted[i],result.references[1][i])>.12f).ToArray();
        Assert.That(violations.Length,Is.EqualTo(4),"ALLthree new edge midpoints and the centroid violate the common12cm limit");
        foreach(int i in violations)Assert.That(Vector3.Distance(fitted[i],result.references[1][i]),Is.EqualTo(.2f).Within(1e-6f));
    }

    [Test]
    public void ActualAdaptiveRetriesReturnAllCompanionsFromOriginalUnfittedSurface()
    {
        var mesh=Triangle();mesh.vertices=new[]{new Vector3(1.3f,0,0),new Vector3(-.65f,0,1.125833f),new Vector3(-.65f,0,-1.125833f)};mesh.RecalculateNormals();
        var before=EditorJsonUtility.ToJson(mesh);var original=mesh.vertices;var companions=new[]{Transform(ReferenceA,original),Transform(ReferenceB,original)};var copy=Copy(companions);
        var args=new object[]{mesh,original,mesh.normals,Tree(Body()),.006f,.002f,1,.1f,companions,null,null,null,null};
        var result=Own((Mesh)Method("RefineEnvelopeMeshWithReferences",13).Invoke(null,args));var proof=(JObject)args[11];var references=(Vector3[][])args[12];
        Assert.That((int)proof["refinementRounds"],Is.GreaterThan(0).And.LessThanOrEqualTo(3));Assert.That((int)proof["companionReferenceCount"],Is.EqualTo(2));
        Assert.That((int)proof["companionReferenceInputVertices"],Is.EqualTo(original.Length));Assert.That((int)proof["companionReferenceOutputVertices"],Is.EqualTo(result.vertexCount));
        Assert.That(result.vertexCount,Is.GreaterThan(original.Length));Assert.That(references.All(r=>r.Length==result.vertexCount),Is.True);
        Equal(references[0],Transform(ReferenceA,result.vertices));Equal(references[1],Transform(ReferenceB,result.vertices));
        Unchanged(companions,copy);Assert.That(mesh.vertices,Is.EqualTo(original));Assert.That(EditorJsonUtility.ToJson(mesh),Is.EqualTo(before));
    }
}
}
#endif
