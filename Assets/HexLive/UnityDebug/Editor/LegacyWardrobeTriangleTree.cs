#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
/// <summary>Editor-only body-surface queries; no fitting work or tree is shipped to the Player.</summary>
sealed class TriangleTree
{
    public readonly struct Contact
    {
        public readonly Vector3 Point, Normal;
        public readonly float Distance;
        public Contact(Vector3 point, Vector3 normal, float distance)
        { Point = point; Normal = normal; Distance = distance; }
    }
    sealed class Node
    {
        public Bounds Bounds;
        public int Start, Count;
        public Node Left, Right;
    }
    readonly Vector3[] vertices, normals, centers;
    readonly int[] triangles, order;
    readonly Node root;
    public Bounds Bounds => root.Bounds;

    public static TriangleTree ForRenderer(SkinnedMeshRenderer renderer)
    {
        if (renderer == null) throw new InvalidOperationException("Missing canonical body surface");
        var baked = new Mesh();
        try
        {
            // Compensate the FBX renderer's .01 transform before converting
            // renderer-local points to world space. Otherwise it is scaled twice.
            renderer.BakeMesh(baked, true);
            return ForMesh(baked,renderer.transform.localToWorldMatrix);
        }
        finally { Object.DestroyImmediate(baked); }
    }

    public static TriangleTree ForMesh(Mesh mesh, Matrix4x4 transform)
    {
        var vertices=mesh.vertices;var normals=mesh.normals;var normalTransform=transform.inverse.transpose;
        for(int i=0;i<vertices.Length;i++)
        { vertices[i]=transform.MultiplyPoint3x4(vertices[i]);normals[i]=normalTransform.MultiplyVector(normals[i]).normalized; }
        return new TriangleTree(vertices,normals,mesh.triangles);
    }

    TriangleTree(Vector3[] vertices, Vector3[] normals, int[] triangles)
    {
        this.vertices = vertices; this.normals = normals; this.triangles = triangles;
        if (vertices.Length == 0 || normals.Length != vertices.Length || triangles.Length == 0)
            throw new InvalidOperationException("Incomplete body surface");
        order = new int[triangles.Length / 3];
        centers = new Vector3[order.Length];
        for (int i = 0; i < order.Length; i++)
        { order[i] = i; centers[i]=(vertices[triangles[i*3]]+vertices[triangles[i*3+1]]+vertices[triangles[i*3+2]])/3f; }
        root = Build(0, order.Length);
    }

    bool Less(int a,int b,int axis)
    {
        int c=centers[a][axis].CompareTo(centers[b][axis]);return c<0 || c==0 && a<b;
    }
    void Partition(int start,int count,int middle,int axis)
    {
        int low=start,high=start+count-1;
        while(low<high)
        {
            int pivot=order[(low+high)/2],i=low,j=high;
            while(i<=j)
            {
                while(Less(order[i],pivot,axis))i++;
                while(Less(pivot,order[j],axis))j--;
                if(i<=j){int t=order[i];order[i++]=order[j];order[j--]=t;}
            }
            if(middle<=j)high=j;else if(middle>=i)low=i;else return;
        }
    }
    Node Build(int start, int count)
    {
        var bounds = new Bounds(vertices[triangles[order[start] * 3]], Vector3.zero);
        for (int i = start; i < start + count; i++)
        for (int corner = 0; corner < 3; corner++) bounds.Encapsulate(vertices[triangles[order[i] * 3 + corner]]);
        var node = new Node { Bounds = bounds, Start = start, Count = count };
        if (count <= 8) return node;
        var size = bounds.size; int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        int half = count / 2;
        Partition(start,count,start+half,axis);
        node.Left = Build(start, half); node.Right = Build(start + half, count - half);
        return node;
    }

    public float RadialEnvelope(Vector3 point, Vector3 center)
    {
        var delta=new Vector2(point.x-center.x,point.z-center.z);if(delta.sqrMagnitude<1e-10f)return 0;
        float best=0;Radial(root,point.y-.025f,point.y+.025f,center,delta.normalized,ref best);return best;
    }
    void Radial(Node node,float lowY,float highY,Vector3 center,Vector2 direction,ref float best)
    {
        if(node.Bounds.max.y<lowY || node.Bounds.min.y>highY)return;
        var side=new Vector2(-direction.y,direction.x);var c=node.Bounds.center-center;var e=node.Bounds.extents;
        float forward=c.x*direction.x+c.z*direction.y;
        float forwardExtent=Mathf.Abs(direction.x)*e.x+Mathf.Abs(direction.y)*e.z;
        float lateral=c.x*side.x+c.z*side.y,sideExtent=Mathf.Abs(side.x)*e.x+Mathf.Abs(side.y)*e.z;
        if(forward+forwardExtent<=best || Mathf.Max(0,Mathf.Abs(lateral)-sideExtent)>(forward+forwardExtent)*.42f)return;
        if(node.Left!=null){Radial(node.Left,lowY,highY,center,direction,ref best);Radial(node.Right,lowY,highY,center,direction,ref best);return;}
        for(int at=node.Start;at<node.Start+node.Count;at++)for(int corner=0;corner<3;corner++)
        {
            var p=vertices[triangles[order[at]*3+corner]];if(p.y<lowY || p.y>highY)continue;
            var d=new Vector2(p.x-center.x,p.z-center.z);float radius=Vector2.Dot(d,direction);
            if(radius>best && Mathf.Abs(Vector2.Dot(d,side))<=radius*.42f)best=radius;
        }
    }

    public Contact Nearest(Vector3 point, Vector3 preferredNormal = default)
    {
        float distance = float.PositiveInfinity; Vector3 closest = default, normal = default;
        Search(root, point, preferredNormal, ref distance, ref closest, ref normal);
        if (float.IsPositiveInfinity(distance) && preferredNormal.sqrMagnitude > 0)
            return Nearest(point);
        if (float.IsPositiveInfinity(distance)) throw new InvalidOperationException("No body surface contact");
        return new Contact(closest, normal, Mathf.Sqrt(distance));
    }

    void Search(Node node, Vector3 point, Vector3 preferred, ref float best, ref Vector3 closest, ref Vector3 normal)
    {
        if (node.Bounds.SqrDistance(point) > best) return;
        if (node.Left != null)
        {
            var first = node.Left; var second = node.Right;
            if (first.Bounds.SqrDistance(point) > second.Bounds.SqrDistance(point)) { first = node.Right; second = node.Left; }
            Search(first, point, preferred, ref best, ref closest, ref normal);
            Search(second, point, preferred, ref best, ref closest, ref normal); return;
        }
        for (int at = node.Start; at < node.Start + node.Count; at++)
        {
            int offset = order[at] * 3, a = triangles[offset], b = triangles[offset + 1], c = triangles[offset + 2];
            var bary = ClosestBarycentric(point, vertices[a], vertices[b], vertices[c]);
            var position = vertices[a] * bary.x + vertices[b] * bary.y + vertices[c] * bary.z;
            float distance = (position - point).sqrMagnitude;
            if (distance >= best) continue;
            var n = (normals[a] * bary.x + normals[b] * bary.y + normals[c] * bary.z).normalized;
            if (preferred.sqrMagnitude > 0 && Vector3.Dot(n, preferred.normalized) < .2f) continue;
            closest = position; normal = n; best = distance;
        }
    }

    static Vector3 ClosestBarycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return new Vector3(1, 0, 0);
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return new Vector3(0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        { float v = d1 / (d1 - d3); return new Vector3(1 - v, v, 0); }
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return new Vector3(0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        { float w = d2 / (d2 - d6); return new Vector3(1 - w, 0, w); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        { float w = (d4 - d3) / (d4 - d3 + d5 - d6); return new Vector3(0, 1 - w, w); }
        float sum = va + vb + vc;
        if (Mathf.Abs(sum) < 1e-20f) return new Vector3(1, 0, 0);
        return new Vector3(va, vb, vc) / sum;
    }
}
}
#endif
