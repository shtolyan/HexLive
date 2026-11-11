#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class WardrobeCandidateRejectionTests
{
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
    static Mesh Collinear(bool weighted)
    {
        var mesh=new Mesh{name="private-candidate-rejection-control"};mesh.vertices=new[]{Vector3.zero,Vector3.right,Vector3.right*2};mesh.normals=Enumerable.Repeat(Vector3.forward,3).ToArray();mesh.triangles=new[]{0,1,2};
        mesh.boneWeights=Enumerable.Repeat(weighted?new BoneWeight{boneIndex0=0,weight0=1}:default,3).ToArray();mesh.bindposes=new[]{Matrix4x4.identity};return mesh;
    }
    static Exception Failure(Mesh mesh)
    {
        var method=Preparation.GetMethod("SubdivideEnvelopeFaces",BindingFlags.Static|BindingFlags.NonPublic);
        return Assert.Throws<TargetInvocationException>(()=>method.Invoke(null,new object[]{mesh,mesh.vertices,mesh.normals,new[]{0},null,null,null})).InnerException;
    }
    [Test]
    public void OnlyDegeneratePrivateSeedGetsTypedRejectionWithExactFaceContext()
    {
        var mesh=Collinear(true);try
        {
            var before=mesh.vertices;var error=Failure(mesh);Assert.That(error.GetType().Name,Is.EqualTo("EnvelopeCandidateDegenerateFaceException"));
            var evidence=(JObject)error.GetType().GetField("Evidence",BindingFlags.Public|BindingFlags.Instance).GetValue(error);
            Assert.That((string)evidence["mesh"],Is.EqualTo(mesh.name));Assert.That((int)evidence["globalFace"],Is.Zero);Assert.That((int)evidence["submesh"],Is.Zero);
            Assert.That((bool)evidence["directlyMarked"],Is.True);Assert.That((bool)evidence["conformingNeighbourOnly"],Is.False);
            Assert.That((float)evidence["squaredCross"],Is.Zero);Assert.That(mesh.vertices,Is.EqualTo(before));Assert.That(mesh.triangles,Is.EqualTo(new[]{0,1,2}));
        }finally{Object.DestroyImmediate(mesh);}
    }
    [Test]
    public void MalformedSkinIsFatalAndCannotBeMisclassifiedAsExpectedCandidateRejection()
    {
        var mesh=Collinear(false);try
        {
            var error=Failure(mesh);Assert.That(error,Is.InstanceOf<InvalidOperationException>());
            Assert.That(error.GetType().Name,Is.Not.EqualTo("EnvelopeCandidateDegenerateFaceException"));Assert.That(error.Message,Does.Contain("Unweighted"));
        }finally{Object.DestroyImmediate(mesh);}
    }
}
}
#endif
