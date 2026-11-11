#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
// Editor-only immutable view over the canonical TriangleTree. Sign never uses its
// smoothed normals. Parity presumes a closed surface: 3-ray agreement is a useful
// diagnostic, NOT certification that an imported body (mouth/eyes/etc.) is watertight.
internal sealed class WardrobeBodyVolume
{
    public const float BoundaryEpsilon = 1e-7f; // World metres, not a mesh-size quantization.
    public readonly struct Contact
    {
        public readonly Vector3 Point, OutwardDirection;
        public readonly float Distance, SignedDistance;
        public readonly bool Inside, Ambiguous, OnSurface, NonUnanimous;
        public readonly int InsideVotes, ValidRays;
        internal Contact(Vector3 point, Vector3 direction, float distance, bool inside, int votes, int valid, bool ambiguous, bool surface)
        { Point=point; OutwardDirection=direction; Distance=distance; SignedDistance=surface?0:inside?-distance:distance;
          Inside=inside; InsideVotes=votes; ValidRays=valid; Ambiguous=ambiguous; OnSurface=surface; NonUnanimous=votes>0&&votes<valid; }
    }
    internal readonly struct RayResult
    {
        public readonly int Crossings, RawHits;
        public readonly bool Ambiguous;
        internal RayResult(int crossings,int raw,bool ambiguous) { Crossings=crossings;RawHits=raw;Ambiguous=ambiguous; }
    }
    readonly struct Node
    {
        public readonly Vector3 Min,Max;
        public readonly int Start,Count,Left,Right;
        public Node(Bounds bounds,int start,int count,int left,int right)
        { Min=bounds.min;Max=bounds.max;Start=start;Count=count;Left=left;Right=right; }
    }
    readonly struct D3
    {
        public readonly double X,Y,Z;
        public D3(double x,double y,double z) { X=x;Y=y;Z=z; }
        public D3(Vector3 p) { X=p.x;Y=p.y;Z=p.z; }
        public static D3 operator -(D3 a,D3 b)=>new D3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static D3 Cross(D3 a,D3 b)=>new D3(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
        public static double Dot(D3 a,D3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        public D3 Unit() { double m=Math.Sqrt(Dot(this,this));if(!(m>0)||double.IsInfinity(m))throw new ArgumentException("Invalid ray direction");return new D3(X/m,Y/m,Z/m); }
    }
    readonly struct TriangleData
    {
        public readonly D3 A,E1,E2;
        public readonly double ParallelThreshold;
        public readonly bool HasScale;
        public TriangleData(Vector3 a,Vector3 b,Vector3 c)
        {
            A=new D3(a);E1=new D3(b)-A;E2=new D3(c)-A;
            // Identical operations/rounding to Collect, moved out of each ray.
            double scale=Math.Sqrt(D3.Dot(E1,E1)*D3.Dot(E2,E2));
            HasScale=scale>0;ParallelThreshold=scale*1e-12;
        }
    }
    readonly struct ExactPoint : IEquatable<ExactPoint>
    {
        readonly int x,y,z;
        public ExactPoint(Vector3 point)
        {x=BitConverter.SingleToInt32Bits(point.x);y=BitConverter.SingleToInt32Bits(point.y);z=BitConverter.SingleToInt32Bits(point.z);}
        public bool Equals(ExactPoint other)=>x==other.x&&y==other.y&&z==other.z;
        public override bool Equals(object other)=>other is ExactPoint point&&Equals(point);
        public override int GetHashCode(){unchecked{return ((x*397)^y)*397^z;}}
    }
    readonly struct Hit
    {
        public readonly double T;
        public readonly int Triangle;
        public readonly bool Boundary;
        public Hit(double t,int triangle,bool boundary) { T=t;Triangle=triangle;Boundary=boundary; }
    }
    sealed class HitComparer : IComparer<Hit>
    {
        public static readonly HitComparer Instance=new HitComparer();
        public int Compare(Hit a,Hit b) { int c=a.T.CompareTo(b.T);return c!=0?c:a.Triangle.CompareTo(b.Triangle); }
    }
    static readonly ConditionalWeakTable<TriangleTree,WardrobeBodyVolume> Cache=new ConditionalWeakTable<TriangleTree,WardrobeBodyVolume>();
    static readonly D3[] Directions={new D3(1,.4142135623730951,.7320508075688772).Unit(),
        new D3(-.2718281828459045,1,.6180339887498948).Unit(),new D3(.529177210903,-.317837245195,1).Unit()};
    [ThreadStatic] static List<Hit> ScratchHits;
    readonly TriangleTree tree;
    // Source arrays are private readonly inputs to canonical TriangleTree and never
    // exposed or changed here. BVH topology is flattened ONCE; no reflection per query.
    readonly Vector3[] vertices;
    readonly int[] triangles,order;
    readonly Node[] nodes;
    readonly TriangleData[] triangleData;
    internal const int MaxCachedContacts=1048576;
    readonly int contactCacheLimit;
    readonly Dictionary<ExactPoint,Contact> contacts=new Dictionary<ExactPoint,Contact>();

    public static WardrobeBodyVolume For(TriangleTree tree)
    {
        if(tree==null)throw new ArgumentNullException(nameof(tree));
        return Cache.GetValue(tree,t=>new WardrobeBodyVolume(t));
    }
    static FieldInfo Field(Type type,string name)
    {
        var f=type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if(f==null)throw new InvalidOperationException("Canonical TriangleTree schema changed: "+type.FullName+"."+name);
        return f;
    }
    WardrobeBodyVolume(TriangleTree source):this(source,MaxCachedContacts){}
    internal WardrobeBodyVolume(TriangleTree source,int contactCacheLimit)
    {
        if(contactCacheLimit<0||contactCacheLimit>MaxCachedContacts)throw new ArgumentOutOfRangeException(nameof(contactCacheLimit));
        this.contactCacheLimit=contactCacheLimit;
        tree=source;var type=typeof(TriangleTree);
        vertices=(Vector3[])Field(type,"vertices").GetValue(source);
        var normals=(Vector3[])Field(type,"normals").GetValue(source);
        triangles=(int[])Field(type,"triangles").GetValue(source);
        order=(int[])Field(type,"order").GetValue(source);
        var root=Field(type,"root").GetValue(source);
        if(vertices==null||normals==null||vertices.Length==0||normals.Length!=vertices.Length||
            triangles==null||triangles.Length==0||triangles.Length%3!=0||order==null||order.Length!=triangles.Length/3||root==null)
            throw new InvalidOperationException("Incomplete canonical TriangleTree");
        foreach(var p in vertices)if(!Finite(p))throw new InvalidOperationException("Nonfinite body vertex");
        foreach(int i in triangles)if(i<0||i>=vertices.Length)throw new InvalidOperationException("Invalid body triangle index");
        triangleData=new TriangleData[triangles.Length/3];
        for(int i=0;i<triangleData.Length;i++)triangleData[i]=new TriangleData(vertices[triangles[i*3]],vertices[triangles[i*3+1]],vertices[triangles[i*3+2]]);
        var seen=new bool[order.Length];foreach(int i in order)
        { if(i<0||i>=seen.Length||seen[i])throw new InvalidOperationException("Invalid BVH triangle permutation");seen[i]=true; }
        var nt=root.GetType();var bounds=Field(nt,"Bounds");var start=Field(nt,"Start");var count=Field(nt,"Count");
        var left=Field(nt,"Left");var right=Field(nt,"Right");var flat=new List<Node>();
        int Capture(object n)
        {
            int index=flat.Count;flat.Add(default);
            var b=(Bounds)bounds.GetValue(n);int s=(int)start.GetValue(n),c=(int)count.GetValue(n);
            if(!Finite(b.min)||!Finite(b.max)||s<0||c<=0||s+c>order.Length)throw new InvalidOperationException("Invalid canonical BVH node");
            var l=left.GetValue(n);var r=right.GetValue(n);
            if((l==null)!=(r==null))throw new InvalidOperationException("Incomplete canonical BVH branch");
            int li=l==null?-1:Capture(l),ri=r==null?-1:Capture(r);
            flat[index]=new Node(b,s,c,li,ri);return index;
        }
        Capture(root);nodes=flat.ToArray();
    }
    static bool Finite(Vector3 p)=>!float.IsNaN(p.x)&&!float.IsInfinity(p.x)&&!float.IsNaN(p.y)&&!float.IsInfinity(p.y)&&!float.IsNaN(p.z)&&!float.IsInfinity(p.z);
    // Draft scheduling observation only; does not alter Query/cache behavior.
    internal bool IsContactCacheEmpty { get { lock(contacts) return contacts.Count==0; } }
    public Contact Query(Vector3 point)
    {
        if(!Finite(point))throw new ArgumentException("Nonfinite body-volume query",nameof(point));
        if(contactCacheLimit==0)return QueryUncached(point);
        var key=new ExactPoint(point);
        lock(contacts){if(contacts.TryGetValue(key,out var retained))return retained;}
        var result=QueryUncached(point);
        // Keep admission bounded per immutable volume. Reaching the limit affects
        // hit rate only: no quantization, eviction approximation or ray shortcut.
        lock(contacts){if(contacts.Count<contactCacheLimit&&!contacts.ContainsKey(key))contacts.Add(key,result);}
        return result;
    }
    Contact QueryUncached(Vector3 point)
    {
        var nearest=tree.Nearest(point);
        if(float.IsNaN(nearest.Distance)||float.IsInfinity(nearest.Distance)||nearest.Distance<0||!Finite(nearest.Point))
            throw new InvalidOperationException("Invalid canonical nearest-surface result");
        if(nearest.Distance<=BoundaryEpsilon)
            return new Contact(nearest.Point,Vector3.zero,nearest.Distance,false,0,0,true,true);
        int votes=0,valid=0;var origin=new D3(point);
        foreach(var direction in Directions)
        {
            var ray=Cast(origin,direction);if(ray.Ambiguous)continue;valid++;if((ray.Crossings&1)!=0)votes++;
        }
        bool inside=votes>=2;bool ambiguous=!inside&&valid-votes<2;
        var delta=inside?nearest.Point-point:point-nearest.Point;
        // Unity Vector3.normalized deliberately returns zero below 1e-5 metres.
        // Our explicit surface boundary is smaller; retain geometric directions
        // throughout the valid sub-10-micrometre range without changing facts.
        double length=Math.Sqrt((double)delta.x*delta.x+(double)delta.y*delta.y+(double)delta.z*delta.z);
        var outward=length>0?new Vector3((float)(delta.x/length),(float)(delta.y/length),(float)(delta.z/length)):Vector3.zero;
        return new Contact(nearest.Point,outward,nearest.Distance,inside,votes,valid,ambiguous,false);
    }
    // Diagnostic geometry tests can force a shared-edge/vertex ray. Runtime fitting
    // uses the same private Cast through Query's three fixed non-axis directions.
    internal RayResult InspectRay(Vector3 origin,Vector3 direction)
    { if(!Finite(origin)||!Finite(direction))throw new ArgumentException("Nonfinite diagnostic ray");return Cast(new D3(origin),new D3(direction).Unit()); }
    RayResult Cast(D3 origin,D3 direction)
    {
        var hits=ScratchHits??(ScratchHits=new List<Hit>(16));hits.Clear();
        Collect(0,origin,direction,hits);hits.Sort(HitComparer.Instance);
        int crossings=0;bool ambiguous=false;Hit previous=default;
        foreach(var hit in hits)
        {
            bool sameDistance=crossings>0&&Math.Abs(hit.T-previous.T)<=Math.Max(1e-10,Math.Max(Math.Abs(hit.T),Math.Abs(previous.T))*1e-10);
            if(sameDistance&&SharedVertex(hit.Triangle,previous.Triangle))
            {
                // Closed triangulated faces hit at their shared edge/vertex count once.
                // Coincident interiors are not a certified boundary crossing.
                if(!hit.Boundary||!previous.Boundary)ambiguous=true;
                continue;
            }
            crossings++;previous=hit;
        }
        return new RayResult(crossings,hits.Count,ambiguous);
    }
    bool SharedVertex(int a,int b)
    {
        for(int i=0;i<3;i++)for(int j=0;j<3;j++)
        {
            var x=vertices[triangles[a*3+i]];var y=vertices[triangles[b*3+j]];
            // Exact coordinate equality also welds duplicated UV-seam vertices;
            // Unity Vector3.operator== uses a tolerance, so do not use it here.
            if(x.x==y.x&&x.y==y.y&&x.z==y.z)return true;
        }
        return false;
    }
    void Collect(int index,D3 origin,D3 direction,List<Hit> hits)
    {
        var n=nodes[index];if(!RayBounds(origin,direction,n.Min,n.Max))return;
        if(n.Left>=0){Collect(n.Left,origin,direction,hits);Collect(n.Right,origin,direction,hits);return;}
        for(int at=n.Start;at<n.Start+n.Count;at++)
        {
            int triangle=order[at];ref readonly var data=ref triangleData[triangle];
            var a=data.A;var e1=data.E1;var e2=data.E2;var h=D3.Cross(direction,e2);double det=D3.Dot(e1,h);
            if(!data.HasScale||Math.Abs(det)<=data.ParallelThreshold)continue; // Same degenerate/parallel decision, double sided.
            double inv=1/det;var s=origin-a;double u=D3.Dot(s,h)*inv;
            const double baryEpsilon=1e-10;
            if(u< -baryEpsilon||u>1+baryEpsilon)continue;
            var q=D3.Cross(s,e1);double v=D3.Dot(direction,q)*inv;
            if(v< -baryEpsilon||u+v>1+baryEpsilon)continue;
            double t=D3.Dot(e2,q)*inv;if(!(t>1e-10)||double.IsInfinity(t)||double.IsNaN(t))continue;
            hits.Add(new Hit(t,triangle,u<=baryEpsilon||v<=baryEpsilon||1-u-v<=baryEpsilon));
        }
    }
    static bool RayBounds(D3 o,D3 d,Vector3 min,Vector3 max)
    {
        double low=0,high=double.PositiveInfinity;
        return Slab(o.X,d.X,min.x,max.x,ref low,ref high)&&Slab(o.Y,d.Y,min.y,max.y,ref low,ref high)&&Slab(o.Z,d.Z,min.z,max.z,ref low,ref high)&&high>1e-10;
    }
    static bool Slab(double origin,double direction,double min,double max,ref double low,ref double high)
    {
        if(Math.Abs(direction)<1e-30)return origin>=min&&origin<=max;
        double a=(min-origin)/direction,b=(max-origin)/direction;if(a>b){double t=a;a=b;b=t;}
        low=Math.Max(low,a);high=Math.Min(high,b);return high>=low;
    }
}
}
#endif
