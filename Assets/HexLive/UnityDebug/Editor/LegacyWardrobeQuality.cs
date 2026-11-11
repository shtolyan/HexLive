#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityMeshSimplifier;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const float QualityMaxError=.003f,QualityP95Error=.0005f;
    static readonly Dictionary<string,JObject> MeshQuality=new();
    static long TriangleCount(Mesh mesh)=>Enumerable.Range(0,mesh.subMeshCount).Sum(i=>(long)mesh.GetIndexCount(i))/3;
    static Vector3[] QualitySamples(Mesh mesh,Matrix4x4 world)
    {
        var vertices=mesh.vertices;var triangles=mesh.triangles;
        int vertexCount=Math.Min(2048,vertices.Length),triangleCount=Math.Min(2048,triangles.Length/3);
        var points=new Vector3[vertexCount+triangleCount];
        for(int i=0;i<vertexCount;i++)points[i]=world.MultiplyPoint3x4(vertices[(int)((long)i*vertices.Length/vertexCount)]);
        for(int i=0;i<triangleCount;i++)
        {
            int index=(int)((long)i*(triangles.Length/3)/triangleCount)*3;
            points[vertexCount+i]=world.MultiplyPoint3x4((vertices[triangles[index]]+vertices[triangles[index+1]]+vertices[triangles[index+2]])/3f);
        }
        return points;
    }
    static JObject QualityError(Vector3[] points,TriangleTree tree)
    {
        var values=points.Select(p=>tree.Nearest(p).Distance).ToArray();Array.Sort(values);
        if(values.Length==0 || values.Any(v=>!float.IsFinite(v)))throw new InvalidOperationException("Invalid geometry error samples");
        return new JObject{["sampleCount"]=values.Length,["max"]=values[values.Length-1],["p95"]=values[Mathf.CeilToInt(values.Length*.95f)-1]};
    }
    static bool WithinQuality(JObject error)=>(float)error["max"]<=QualityMaxError && (float)error["p95"]<=QualityP95Error;
    static long NextTriangleBudget(long requested, long actual)
    {
        if (requested <= 0 || actual < 0) throw new ArgumentOutOfRangeException();
        long next = checked(requested * 2);
        // Whinarn 3.1.1 uses the requested count only as an early-stop test.
        // An exhausted attempt cannot reach any higher budget <= its actual
        // result; those requests repeat the same edges and 100 iterations.
        while (next <= actual) next = checked(next * 2);
        return next;
    }
    static Mesh SimplifyWithErrorGate(Mesh source,string name,Matrix4x4 world)
    {
        long input=TriangleCount(source);var attempts=new JArray();
        var proof=new JObject{["inputTriangles"]=input,["maxErrorLimitMetres"]=QualityMaxError,["p95ErrorLimitMetres"]=QualityP95Error,["metric"]="bidirectional sampled vertex/triangle-centroid distance in fitted world metres",["attempts"]=attempts};
        if(input<=TriangleBudget)
        {var exact=Object.Instantiate(source);exact.name=name;proof["mode"]="unchanged";proof["actualTriangles"]=input;MeshQuality[name]=proof;return exact;}
        var reference=TriangleTree.ForMesh(source,world);var samples=QualitySamples(source,world);
        for(long budget=TriangleBudget;budget<input;)
        {
            var simplifier=new MeshSimplifier();var options=SimplificationOptions.Default;
            options.PreserveBorderEdges=true;options.PreserveUVSeamEdges=true;options.PreserveUVFoldoverEdges=true;options.PreserveSurfaceCurvature=true;
            simplifier.SimplificationOptions=options;simplifier.Initialize(source);simplifier.SimplifyMesh((float)budget/input);
            var mesh=simplifier.ToMesh();mesh.name=name;
            if(mesh.vertexCount==0 || mesh.subMeshCount!=source.subMeshCount || mesh.bindposes.Length!=source.bindposes.Length)
            {Object.DestroyImmediate(mesh);throw new InvalidOperationException(name+": simplification broke skin/material layout");}
            var forward=QualityError(samples,TriangleTree.ForMesh(mesh,world));var reverse=QualityError(QualitySamples(mesh,world),reference);
            bool passed=WithinQuality(forward) && WithinQuality(reverse);
            long actual=TriangleCount(mesh);
            attempts.Add(new JObject{["requestedTriangles"]=budget,["actualTriangles"]=actual,["sourceToCandidate"]=forward,["candidateToSource"]=reverse,["passed"]=passed});
            if(passed)
            {proof["mode"]="adaptive";proof["actualTriangles"]=TriangleCount(mesh);MeshQuality[name]=proof;return mesh;}
            Object.DestroyImmediate(mesh);
            budget=NextTriangleBudget(budget,actual);
        }
        // Preserve the source surface instead of silently keeping a damaged
        // reduction. The report makes every unsimplified outlier reviewable.
        var fallback=Object.Instantiate(source);fallback.name=name;proof["mode"]="source-preserved";proof["actualTriangles"]=input;MeshQuality[name]=proof;return fallback;
    }
}
}
#endif
