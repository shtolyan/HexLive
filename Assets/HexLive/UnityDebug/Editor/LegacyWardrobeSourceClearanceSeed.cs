#if UNITY_EDITOR
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Private PRE-FIT candidate, never an acceptance override. Transfer the
    // donor's actual body-to-cloth standoff instead of flattening nearby layers
    // to one body envelope. Existing final physical/bind/reference gates remain.
    static Mesh CreateSourceClearanceSeed(Mesh source,Fit donor,Fit recipient,out JObject proof)
    {
        if(source==null||source.vertexCount!=donor.WorldVertices.Length||source.vertexCount!=recipient.WorldVertices.Length||
           donor.BoneWorld.Length!=recipient.BoneWorld.Length||!donor.Paths.SequenceEqual(recipient.Paths))
            throw new ArgumentException("Source-clearance transfer requires matching topology and canonical bone order");
        var weights=source.boneWeights;
        if(weights.Length!=source.vertexCount)throw new InvalidOperationException("Source-clearance transfer requires complete skin influences");
        var remap=recipient.BoneWorld.Select((m,i)=>m*donor.BoneWorld[i].inverse).ToArray();
        var volume=WardrobeBodyVolume.For(donor.BodySurface);
        var positions=new Vector3[source.vertexCount];var normals=new Vector3[source.vertexCount];
        int transferred=0,outsideRange=0,ambiguous=0,incompatible=0,onSurface=0,inside=0;
        float minimumGap=float.PositiveInfinity,maximumGap=0,maximumMove=0;
        for(int i=0;i<source.vertexCount;i++)
        {
            var w=weights[i];var original=donor.WorldVertices[i];var originalNormal=donor.WorldNormals[i];
            Vector3 Map(Vector3 p,Vector3 n,out Vector3 mappedNormal)
            {
                Vector3 mapped=default; mappedNormal=default;
                AddWeighted(ref mapped,ref mappedNormal,p,n,w.boneIndex0,w.weight0,remap);
                AddWeighted(ref mapped,ref mappedNormal,p,n,w.boneIndex1,w.weight1,remap);
                AddWeighted(ref mapped,ref mappedNormal,p,n,w.boneIndex2,w.weight2,remap);
                AddWeighted(ref mapped,ref mappedNormal,p,n,w.boneIndex3,w.weight3,remap);
                return mapped;
            }
            var mapped=Map(original,originalNormal,out var normal);positions[i]=mapped;normals[i]=normal.normalized;
            var contact=volume.Query(original);
            if(contact.OnSurface)onSurface++;if(contact.Inside)inside++;
            if(contact.Ambiguous&&!contact.OnSurface){ambiguous++;continue;}
            if(contact.Distance>ContactRange){outsideRange++;continue;}
            // Smoothed normals are used only for correspondence/orientation,
            // NEVER as an inside/outside sign. Exact surface has known zero gap.
            float sourceGap=Mathf.Max(0,contact.SignedDistance);
            var orientation=donor.BodySurface.Nearest(original).Normal;
            var anchor=Map(contact.Point,orientation,out var mappedBodyNormal);
            var target=recipient.BodySurface.Nearest(anchor,mappedBodyNormal);
            if(mappedBodyNormal.sqrMagnitude<1e-10f||Vector3.Dot(target.Normal,mappedBodyNormal.normalized)<.2f)
            {incompatible++;continue;} // Never silently accept Nearest's unfiltered fallback.
            float desiredGap=.006f+sourceGap;
            var fitted=target.Point+target.Normal*desiredGap;
            positions[i]=fitted;
            if(mappedBodyNormal.sqrMagnitude>1e-10f&&target.Normal.sqrMagnitude>1e-10f)
                normals[i]=(Quaternion.FromToRotation(mappedBodyNormal.normalized,target.Normal)*normal).normalized;
            minimumGap=Mathf.Min(minimumGap,sourceGap);maximumGap=Mathf.Max(maximumGap,sourceGap);
            maximumMove=Mathf.Max(maximumMove,Vector3.Distance(mapped,fitted));transferred++;
        }
        proof=new JObject{["passed"]=ambiguous==0&&incompatible==0,["kind"]="source-body-clearance-transfer-seed",
            ["acceptanceOverride"]=false,["sourceVertices"]=source.vertexCount,["transferredVertices"]=transferred,
            ["boneOnlyOutsideContactRange"]=outsideRange,["unresolvedSourceSignVertices"]=ambiguous,["sourceOnSurfaceVertices"]=onSurface,
            ["incompatibleTargetNormalVertices"]=incompatible,
            ["sourceInsideVertices"]=inside,["sourceContactRangeMetres"]=ContactRange,["addedClearanceBiasMetres"]=.006f,
            ["minimumTransferredSourceGapMetres"]=transferred==0?0:minimumGap,["maximumTransferredSourceGapMetres"]=maximumGap,
            ["maximumMoveFromBoneMappedPointMetres"]=maximumMove,
            ["sourceGapPolicy"]="nonnegative parity-signed donor gap plus6mm; retain original bone mapping beyond contact range",
            ["sourceUnchanged"]=true,["topologyUnchanged"]=true,["requiresFinalPhysicalAndPostBindAcceptance"]=true};
        return BindMesh(source,recipient,positions,normals,"SourceClearance_"+source.name);
    }
}
}
#endif
