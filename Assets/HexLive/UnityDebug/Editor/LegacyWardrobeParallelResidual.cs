using System;using Newtonsoft.Json.Linq;using UnityEngine;
namespace HexLive.UnityDebug.Editor {public static partial class LegacyWebGLWardrobePreparation {
    internal static JObject EnvelopeProbeResidualDispatchedDraft(Vector3[] points,int[] triangles,WardrobeBodyVolume body,float target,int degree=8)
    {
        // One scheduling decision only: never parallelize a populated cache.
        // Query, cache retention/admission and numerical solver remain unchanged.
        if(!body.IsContactCacheEmpty)return EnvelopeProbeResidual(points,triangles,body,target);
        return EnvelopeProbeResidualParallelDraft(points,triangles,body,target,Math.Min(8,Math.Max(1,degree)));
    }
    internal static JObject EnvelopeProbeResidualParallelDraft(Vector3[] points,int[] triangles,WardrobeBodyVolume body,float target,int degree)
    {
        // Draft only: immutable numerical queries are parallel; all aggregation below
        // is verbatim canonical order, including strict worst/first-ambiguous ties.
        int count=checked(points.Length+(triangles.Length/3)*13);
        var contacts=new WardrobeBodyVolume.Contact[count];
        Vector3 PointAt(int index)
        {
            if(index<points.Length)return points[index];
            int flat=index-points.Length, at=(flat/13)*3;var w=EnvelopeProbeBarycentrics[flat%13];
            return points[triangles[at]]*w.x+points[triangles[at+1]]*w.y+points[triangles[at+2]]*w.z;
        }
        const int chunk=2048;
        System.Threading.Tasks.Parallel.For(0,(count+chunk-1)/chunk,
            new System.Threading.Tasks.ParallelOptions{MaxDegreeOfParallelism=degree},part=>
            {int end=Math.Min(count,(part+1)*chunk);for(int i=part*chunk;i<end;i++)contacts[i]=body.Query(PointAt(i));});
        int nextContact=0;

        int samples=0,belowTarget=0,belowMinimum=0,penetrating1=0,penetrating3=0,inside=0,ambiguous=0,onSurface=0,nonUnanimous=0,minValidRays=3;
        float maxViolation=0,maxDepth=0,minimumSigned=float.PositiveInfinity;
        Vector3 worst=default,worstBody=default,firstAmbiguous=default;int worstTriangle=-2;
        void Sample(Vector3 point,int triangle)
        {
            var c=contacts[nextContact++];samples++;
            if(c.NonUnanimous)nonUnanimous++;minValidRays=Math.Min(minValidRays,c.ValidRays);
            if(c.Ambiguous){if(ambiguous==0)firstAmbiguous=point;ambiguous++;}
            if(c.OnSurface)onSurface++;
            if(c.Inside)inside++;
            // Physical floor is exact: the separate 0.5 mm diagnostic deficit
            // counter below never grants a 1.5 mm point acceptance at 2 mm.
            float signed=c.SignedDistance;
            minimumSigned=Mathf.Min(minimumSigned,signed);
            if(signed<target)belowMinimum++;
            if(c.Ambiguous&&!c.OnSurface)return; // An unknown sign is never accepted as outside.
            if(signed<target-.0005f)belowTarget++;
            if(signed<-.001f)penetrating1++;if(signed<-.003f)penetrating3++;
            maxDepth=Mathf.Max(maxDepth,-signed);
            if(target-signed<=maxViolation)return;
            maxViolation=target-signed;worst=point;worstBody=c.Point;worstTriangle=triangle;
        }
        foreach(var p in points)Sample(p,-1);
        for(int at=0;at<triangles.Length;at+=3)
        foreach(var w in EnvelopeProbeBarycentrics)
            Sample(points[triangles[at]]*w.x+points[triangles[at+1]]*w.y+points[triangles[at+2]]*w.z,at/3);
        return new JObject{["allVerticesAnd13BarycentricSamplesPerTriangle"]=true,["sampleCount"]=samples,["targetClearanceMetres"]=target,
            ["minimumSignedDistanceMetres"]=minimumSigned,["samplesBelowMinimum"]=belowMinimum,["physicalMinimumIsStrict"]=true,
            ["distanceMethod"]="unsigned exact-nearest distance with three deduplicated BVH ray parity inside votes",
            ["insidePolicy"]="At least two agreeing valid rays; non-unanimous majority is diagnostic, not failure.",
            ["nonUnanimousSamples"]=nonUnanimous,["minimumValidRays"]=minValidRays,
            ["insideSamples"]=inside,["ambiguousSamples"]=ambiguous,["onSurfaceSamples"]=onSurface,["firstAmbiguousPoint"]=ambiguous==0?null:Vec(firstAmbiguous),
            ["toleranceMetres"]=.0005f,["toleranceIsDiagnosticOnly"]=true,["samplesBelowTargetMinusTolerance"]=belowTarget,["samplesBelowMinus1mm"]=penetrating1,
            ["samplesBelowMinus3mm"]=penetrating3,["maximumClearanceDeficitMetres"]=maxViolation,["maximumPenetrationDepthMetres"]=maxDepth,
            ["worstTriangleIndex"]=worstTriangle,["worstPoint"]=Vec(worst),["worstBodyPoint"]=Vec(worstBody),
            ["passedSampledEnvelope"]=minimumSigned>=target&&belowMinimum==0&&inside==0&&ambiguous==0&&onSurface==0};
    }

}}
