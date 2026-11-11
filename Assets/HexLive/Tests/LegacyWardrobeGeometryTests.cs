#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class LegacyWardrobeGeometryTests
{
    static Type Tree => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("HexLive.UnityDebug.Editor.TriangleTree", false)).Single(t => t != null);
    static object Build(Mesh mesh, Matrix4x4 world) => Tree.GetMethod("ForMesh").Invoke(null, new object[] { mesh, world });
    static (Vector3 point, Vector3 normal, float distance) Nearest(object tree, Vector3 query, Vector3 preferred = default)
    {
        object contact = Tree.GetMethod("Nearest").Invoke(tree, new object[] { query, preferred });
        Type type = contact.GetType();
        return ((Vector3)type.GetField("Point").GetValue(contact), (Vector3)type.GetField("Normal").GetValue(contact),
            (float)type.GetField("Distance").GetValue(contact));
    }

    [TestCase(0f, .4f, 0f)]
    [TestCase(-2f, -.7f, .3f)]
    [TestCase(2f, .5f, 2f)]
    [TestCase(1f, 0f, -1f)]
    public void DeepPartitionedGridMatchesAnalyticRectangleInWorldMetres(float x, float y, float z)
    {
        const int side = 17;
        var vertices = new List<Vector3>(); var indices = new List<int>();
        for (int row = 0; row < side; row++)
        for (int col = 0; col < side; col++) vertices.Add(new Vector3(-1f + 2f * col / (side - 1), 0, -1f + 2f * row / (side - 1)));
        for (int row = 0; row < side - 1; row++)
        for (int col = 0; col < side - 1; col++)
        {
            int i = row * side + col;
            indices.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 });
        }
        var mesh = new Mesh { vertices = vertices.ToArray(), triangles = indices.ToArray(), normals = Enumerable.Repeat(Vector3.up, vertices.Count).ToArray() };
        try
        {
            var world = Matrix4x4.TRS(new Vector3(13, -4, 7), Quaternion.Euler(23, 40, 5), new Vector3(1.7f, .8f, .6f));
            var query = world.MultiplyPoint3x4(new Vector3(x, y, z));
            var expected = world.MultiplyPoint3x4(new Vector3(Mathf.Clamp(x, -1, 1), 0, Mathf.Clamp(z, -1, 1)));
            var normal = world.inverse.transpose.MultiplyVector(Vector3.up).normalized;
            var tree = Build(mesh, world);
            var hit = Nearest(tree, query);
            Assert.That(Vector3.Distance(hit.point, expected), Is.LessThan(.00002f));
            Assert.That(hit.distance, Is.EqualTo(Vector3.Distance(expected, query)).Within(.00002f));
            Assert.That(Vector3.Dot(hit.normal, normal), Is.GreaterThan(.99999f));
            var fallback = Nearest(tree, query, -normal);
            Assert.That(Vector3.Distance(fallback.point, expected), Is.LessThan(.00002f), "No compatible face must fall back to the real nearest surface.");
        }
        finally { Object.DestroyImmediate(mesh); }
    }

    [Test]
    public void PreferredFacingSkipsTheCloserOppositeSurface()
    {
        var mesh = new Mesh
        {
            vertices = new[] { new Vector3(-1,0,-1), new Vector3(1,0,-1), new Vector3(0,0,1), new Vector3(-1,1,-1), new Vector3(1,1,-1), new Vector3(0,1,1) },
            triangles = new[] { 0,1,2,3,4,5 },
            normals = new[] { Vector3.down,Vector3.down,Vector3.down,Vector3.up,Vector3.up,Vector3.up }
        };
        try
        {
            var tree = Build(mesh, Matrix4x4.identity); var query = new Vector3(0,.2f,0);
            Assert.That(Nearest(tree, query).distance, Is.EqualTo(.2f).Within(.00001f));
            var facing = Nearest(tree, query, Vector3.up);
            Assert.That(facing.point.y, Is.EqualTo(1f).Within(.00001f));
            Assert.That(facing.distance, Is.EqualTo(.8f).Within(.00001f));
        }
        finally { Object.DestroyImmediate(mesh); }
    }

    [Test]
    public void RadialEnvelopeDoesNotPullAThroughSkirtVertexOntoTheOppositeSide()
    {
        const int count = 64; const float radius = .3f;
        var vertices = new List<Vector3>(); var indices = new List<int>();
        for (int row = 0; row < 3; row++)
        for (int i = 0; i < count; i++)
        {
            float angle = i * Mathf.PI * 2 / count;
            vertices.Add(new Vector3(Mathf.Cos(angle)*radius,row,Mathf.Sin(angle)*radius));
        }
        for (int row = 0; row < 2; row++)
        for (int i = 0; i < count; i++)
        {
            int a = row*count+i, b = row*count+(i+1)%count;
            indices.AddRange(new[] { a,b,a+count,b,b+count,a+count });
        }
        var mesh = new Mesh { vertices=vertices.ToArray(),triangles=indices.ToArray(),normals=vertices.Select(v=>new Vector3(v.x,0,v.z).normalized).ToArray() };
        try
        {
            var center = new Vector3(4,0,-3); var tree = Build(mesh,Matrix4x4.Translate(center));
            for (int i=0;i<count;i++)
            {
                float angle=i*Mathf.PI*2/count;
                var direction=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                foreach(float distance in new[]{.1f,1.5f})
                {
                    float actual=(float)Tree.GetMethod("RadialEnvelope").Invoke(tree,new object[]{center+Vector3.up+direction*distance,center});
                    Assert.That(actual,Is.EqualTo(radius).Within(.00002f),"Ray "+i+", query radius "+distance);
                }
            }
            Assert.That((float)Tree.GetMethod("RadialEnvelope").Invoke(tree,new object[]{center+Vector3.up,center}),Is.Zero);
        }
        finally { Object.DestroyImmediate(mesh); }
    }
}
}
#endif
