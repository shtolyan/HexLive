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
// Actual Unity Mesh/math controls; no portable arithmetic, graphics, remote requests
// or authored asset writes. Internal Editor classes are resolved by reflection.
public sealed class WardrobeBodyVolumeTests
{
    readonly List<Mesh> _meshes = new List<Mesh>();
    static Type TreeType => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HexLive.UnityDebug.Editor.TriangleTree", false)).Single(t => t != null);
    static Type VolumeType => TreeType.Assembly.GetType("HexLive.UnityDebug.Editor.WardrobeBodyVolume", true);
    static void Check(bool condition, string message) => Assert.That(condition, Is.True, message);
    object Tree(Vector3[] points, int[] triangles)
    {
        var mesh = new Mesh { name = "WardrobeBodyVolumeFixture", vertices = points,
            normals = points.Select(_ => new Vector3(-1, 0, 0)).ToArray(), triangles = triangles };
        _meshes.Add(mesh);
        return TreeType.GetMethod("ForMesh").Invoke(null, new object[] { mesh, Matrix4x4.identity });
    }
    [TearDown] public void DisposeActualMeshes()
    { foreach (var mesh in _meshes) if (mesh != null) Object.DestroyImmediate(mesh); _meshes.Clear(); }
    static VolumeProxy For(object tree) => new VolumeProxy(VolumeType.GetMethod("For").Invoke(null, new[] { tree }));
    sealed class Sample
    {
        readonly object _value;
        T Read<T>(string field) => (T)_value.GetType().GetField(field).GetValue(_value);
        public Sample(object value) { _value = value; }
        public Vector3 Point => Read<Vector3>("Point");
        public Vector3 OutwardDirection => Read<Vector3>("OutwardDirection");
        public float Distance => Read<float>("Distance");
        public float SignedDistance => Read<float>("SignedDistance");
        public bool Inside => Read<bool>("Inside");
        public bool Ambiguous => Read<bool>("Ambiguous");
        public bool OnSurface => Read<bool>("OnSurface");
        public bool NonUnanimous => Read<bool>("NonUnanimous");
        public int InsideVotes => Read<int>("InsideVotes");
        public int ValidRays => Read<int>("ValidRays");
    }
    sealed class RaySample
    {
        readonly object _value;
        T Read<T>(string field) => (T)_value.GetType().GetField(field).GetValue(_value);
        public RaySample(object value) { _value = value; }
        public int Crossings => Read<int>("Crossings");
        public int RawHits => Read<int>("RawHits");
        public bool Ambiguous => Read<bool>("Ambiguous");
    }
    sealed class VolumeProxy
    {
        public readonly object Raw;
        public VolumeProxy(object value) { Raw = value; }
        public Sample Query(Vector3 point) => new Sample(VolumeType.GetMethod("Query").Invoke(Raw, new object[] { point }));
        public RaySample InspectRay(Vector3 origin, Vector3 direction) => new RaySample(VolumeType.GetMethod("InspectRay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(Raw, new object[] { origin, direction }));
    }
    static (Vector3[],int[]) Cube(float half=1)
    {
        var p=new[]{new Vector3(-half,-1,-1),new Vector3(half,-1,-1),new Vector3(half,1,-1),new Vector3(-half,1,-1),
            new Vector3(-half,-1,1),new Vector3(half,-1,1),new Vector3(half,1,1),new Vector3(-half,1,1)};
        return(p,new[]{0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5});
    }
    static (Vector3[],int[]) Concave()
    {
        var polygon=new[]{(0f,0f),(2f,0f),(2f,1f),(1f,1f),(1f,2f),(0f,2f)};
        var p=polygon.Select(v=>new Vector3(v.Item1,v.Item2,-.5f)).Concat(polygon.Select(v=>new Vector3(v.Item1,v.Item2,.5f))).ToArray();
        var t=new List<int>();var face=new[]{0,1,3,1,2,3,0,3,5,3,4,5};
        for(int i=0;i<face.Length;i+=3){t.AddRange(new[]{face[i],face[i+2],face[i+1],face[i]+6,face[i+1]+6,face[i+2]+6});}
        for(int a=0;a<6;a++){int b=(a+1)%6;t.AddRange(new[]{a,b,b+6,a,b+6,a+6});}
        return(p,t.ToArray());
    }
    static (Vector3[],int[]) Sphere()
    {
        var v=new[]{new Vector3(1,0,0),new Vector3(-1,0,0),new Vector3(0,1,0),new Vector3(0,-1,0),new Vector3(0,0,1),new Vector3(0,0,-1)};
        int[] f={0,2,4,2,1,4,1,3,4,3,0,4,2,0,5,1,2,5,3,1,5,0,3,5};
        var faces=new List<(Vector3,Vector3,Vector3)>();for(int i=0;i<f.Length;i+=3)faces.Add((v[f[i]],v[f[i+1]],v[f[i+2]]));
        for(int iteration=0;iteration<3;iteration++)
        {
            var next=new List<(Vector3,Vector3,Vector3)>();
            foreach(var(a,b,c)in faces){var ab=(a+b).normalized;var bc=(b+c).normalized;var ca=(c+a).normalized;
                next.Add((a,ab,ca));next.Add((ab,b,bc));next.Add((ca,bc,c));next.Add((ab,bc,ca));}
            faces=next;
        }
        var p=faces.SelectMany(x=>new[]{x.Item1,x.Item2,x.Item3}).ToArray();return(p,Enumerable.Range(0,p.Length).ToArray());
    }
    static void Classified(VolumeProxy volume,Vector3 p,bool inside)
    {
        var q=volume.Query(p);Check(q.Inside==inside,"Wrong inside at "+p.x+","+p.y+","+p.z);
        Check(!q.Ambiguous&&!q.NonUnanimous&&q.ValidRays==3&&!q.OnSurface,"Closed generic query did not agree 3/3");
        Check(inside?q.SignedDistance<0:q.SignedDistance>0,"Wrong distance sign");
        var d=inside?q.Point-p:p-q.Point;
        double length=Math.Sqrt((double)d.x*d.x+(double)d.y*d.y+(double)d.z*d.z);
        var exactDirection=new Vector3((float)(d.x/length),(float)(d.y/length),(float)(d.z/length));
        Check(Vector3.Dot(exactDirection,q.OutwardDirection)>.9999f,"Direction uses anything except geometric closest-point delta");
    }
    [Test] public void SameTreeCache()
    { var(p,t)=Cube();var tree=Tree(p,t);Assert.That(For(tree).Raw,Is.SameAs(For(tree).Raw)); }

    [Test]
    public void BoxInsideOutsideWrongNormals()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        Classified(volume,new Vector3(.2f,.3f,.1f),true);Classified(volume,new Vector3(1.2f,.3f,.1f),false);
                Check(Math.Abs(volume.Query(new Vector3(1.2f,.3f,.1f)).Distance-.2f)<1e-6,"Unsigned distance changed");
    }

    [Test]
    public void BoxNearSurfaceSign()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        Classified(volume,new Vector3(.9999f,.19f,.31f),true);Classified(volume,new Vector3(1.0001f,.19f,.31f),false);
    }

    [Test]
    public void Sub10micrometreDirectionSurvivesUnityNormalizationThreshold()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        Assert.That(new Vector3(.000001f,0,0).normalized, Is.EqualTo(Vector3.zero), "Actual Unity normalized deadzone must be exercised");
        Classified(volume,new Vector3(.999999f,.19f,.31f),true);Classified(volume,new Vector3(1.000001f,.19f,.31f),false);
    }

    [Test]
    public void SurfaceExplicitAmbiguousZeroDirection()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var q=volume.Query(new Vector3(1,.2f,.3f));Check(q.OnSurface&&q.Ambiguous&&q.SignedDistance==0&&q.OutwardDirection.sqrMagnitude==0,"Surface ambiguity hidden");
    }

    [Test]
    public void SharedTriangleEdgeDedup()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var r=volume.InspectRay(Vector3.zero,new Vector3(1,0,0));Check(r.Crossings==1&&r.RawHits==2&&!r.Ambiguous,"Face diagonal counted twice");
    }

    [Test]
    public void SharedVertexDedup()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var r=volume.InspectRay(Vector3.zero,new Vector3(1,1,1));Check(r.Crossings==1&&r.RawHits>=3&&!r.Ambiguous,"Shared vertex counted multiple times");
    }

    [Test]
    public void OutsideTwoCrossings()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var r=volume.InspectRay(new Vector3(-2,.2f,.3f),new Vector3(1,0,0));Check(r.Crossings==2,"Outside parity not even");
    }

    [Test]
    public void DoubleSidedReversedWinding()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var reversed=t.ToArray();for(int i=0;i<reversed.Length;i+=3)(reversed[i],reversed[i+1])=(reversed[i+1],reversed[i]);
                var v=For(Tree(p,reversed));Classified(v,new Vector3(.2f,.3f,.1f),true);Classified(v,new Vector3(1.2f,.3f,.1f),false);
    }

    [Test]
    public void ConcaveLPrismNotchAndBranches()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var(a,b)=Concave();var v=For(Tree(a,b));
                Classified(v,new Vector3(.5f,1.5f,0),true);Classified(v,new Vector3(1.5f,.5f,0),true);Classified(v,new Vector3(1.5f,1.5f,0),false);
                Classified(v,new Vector3(1.001f,1.2f,0),false);
    }

    [Test]
    public void SphereBvhInteriorExterior()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var(a,b)=Sphere();var v=For(Tree(a,b));
                Classified(v,new Vector3(.13f,.21f,.09f),true);Classified(v,new Vector3(1.5f,.01f,.02f),false);
    }

    [Test]
    public void ThinVolumeCrossingsNotQuantized()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var(a,b)=Cube(.000001f);var v=For(Tree(a,b));
                Classified(v,new Vector3(0,.13f,.21f),true);var r=v.InspectRay(new Vector3(-.000003f,.13f,.21f),new Vector3(1,0,0));Check(r.Crossings==2,"Thin distinct faces merged");
    }

    [Test]
    public void OpenMeshMajorityNotCertified()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var open=Enumerable.Range(0,t.Length/3).Where(i=>!Enumerable.Range(0,3).All(j=>p[t[i*3+j]].x==1)).SelectMany(i=>t.Skip(i*3).Take(3)).ToArray();
                var v=For(Tree(p,open));var q=v.Query(Vector3.zero);
                Check(q.ValidRays==3&&q.NonUnanimous&&!q.Ambiguous&&q.InsideVotes==2&&q.Inside,"Valid 2:1 majority was rejected or undocumented");
    }

    [Test]
    public void CoincidentInteriorsRejectUnknownSign()
    {
        var(p,t)=Cube(); var tree=Tree(p,t); var volume=For(tree);
        var v=For(Tree(p,t.Concat(t).ToArray()));var q=v.Query(new Vector3(.12f,.23f,.34f));
                Check(q.Ambiguous&&q.ValidRays==0,"Coincident interior hits were silently certified");
    }
}
}
#endif
