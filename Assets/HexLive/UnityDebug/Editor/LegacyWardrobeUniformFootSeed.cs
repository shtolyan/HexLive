#if UNITY_EDITOR
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Candidate seed only. Final all-sample physical/post-bind/reference gates remain mandatory.
    // Uniform expansion preserves each pure foot region's relative geometry; source-gap
    // geometry remains outside that region and skin influences blend the ankle boundary.
    static Mesh ApplyUniformFootProfileSeed(Mesh source,Fit recipient,Vector3[] boneMapped,Vector3[] sourceGap,Vector3[] boneMappedWorldNormals,out JObject proof)
    {
        if(source==null||boneMapped==null||sourceGap==null||boneMapped.Length!=source.vertexCount||sourceGap.Length!=source.vertexCount||boneMappedWorldNormals==null||boneMappedWorldNormals.Length!=source.vertexCount)
            throw new ArgumentException("Uniform foot seed requires matching complete topology");
        int count=source.vertexCount,cursor=0;var left=new float[count];var right=new float[count];
        var bones=recipient.Skin.bones;
        var counts=source.GetBonesPerVertex().ToArray();
        var influences=source.GetAllBoneWeights().ToArray();
        {
            if(counts.Length!=count)throw new InvalidOperationException("Uniform foot seed requires complete influences");
            for(int i=0;i<count;i++)for(int j=0;j<counts[i];j++)
            {
                var w=influences[cursor++];
                if(w.boneIndex<0||w.boneIndex>=bones.Length||bones[w.boneIndex]==null||!float.IsFinite(w.weight)||w.weight<0)
                    throw new InvalidOperationException("Invalid uniform foot skin influence");
                string name=bones[w.boneIndex].name;
                if(!(name.Contains("Foot")||name.Contains("Heel")||name.Contains("Metatarsals")||name.Contains("Toe")))continue;
                if(name.StartsWith("l",StringComparison.Ordinal))left[i]+=w.weight;
                else if(name.StartsWith("r",StringComparison.Ordinal))right[i]+=w.weight;
            }
            if(cursor!=influences.Length)throw new InvalidOperationException("Incomplete uniform foot influence traversal");
        }
        Vector3 Pivot(float[] weights,out int pure,out int active)
        {
            double x=0,y=0,z=0;pure=0;active=0;
            for(int i=0;i<count;i++)
            {
                if(!float.IsFinite(boneMapped[i].x)||!float.IsFinite(boneMapped[i].y)||!float.IsFinite(boneMapped[i].z)||
                   !float.IsFinite(sourceGap[i].x)||!float.IsFinite(sourceGap[i].y)||!float.IsFinite(sourceGap[i].z))
                    throw new ArgumentException("Nonfinite uniform foot seed");
                if(weights[i]>0)active++;
                if(weights[i]>=.99999f){pure++;x+=boneMapped[i].x;y+=boneMapped[i].y;z+=boneMapped[i].z;}
            }
            return pure==0?Vector3.zero:new Vector3((float)(x/pure),(float)(y/pure),(float)(z/pure));
        }
        var lp=Pivot(left,out int pureLeft,out int activeLeft);var rp=Pivot(right,out int pureRight,out int activeRight);
        var profiles=new JArray();float selectedScale=1;int best=int.MaxValue;Vector3[] selected=(Vector3[])sourceGap.Clone();
        bool applicable=(activeLeft+activeRight)>0&&(activeLeft==0||pureLeft>0)&&(activeRight==0||pureRight>0);
        if(applicable)
        {
            var volume=WardrobeBodyVolume.For(recipient.BodySurface);
            foreach(float scale in new[]{1f,1.08f,1.16f,1.24f,1.32f})
            {
                var points=new Vector3[count];int violations=0,inside=0,ambiguous=0,surface=0;float minimum=float.PositiveInfinity;
                for(int i=0;i<count;i++)
                {
                    float l=left[i],r=right[i],total=l+r;
                    if(total<=0)points[i]=sourceGap[i];
                    else
                    {
                        if(total>1){l/=total;r/=total;total=1;}
                        points[i]=sourceGap[i]*(1-total)+(lp+(boneMapped[i]-lp)*scale)*l+(rp+(boneMapped[i]-rp)*scale)*r;
                    }
                    var contact=volume.Query(points[i]);minimum=Mathf.Min(minimum,contact.SignedDistance);
                    if(contact.Inside)inside++;if(contact.Ambiguous)ambiguous++;if(contact.OnSurface)surface++;
                    if(contact.Ambiguous||contact.OnSurface||contact.Inside||contact.SignedDistance<.002f)violations++;
                }
                profiles.Add(new JObject{["scale"]=scale,["vertexViolations"]=violations,["insideVertices"]=inside,
                    ["ambiguousVertices"]=ambiguous,["onSurfaceVertices"]=surface,["minimumSignedDistanceMetres"]=minimum});
                // Ascending profiles and strict improvement make ties choose the smallest expansion.
                if(violations<best){best=violations;selectedScale=scale;selected=points;}
            }
        }
        proof=new JObject{["passed"]=true,["applicable"]=applicable,["kind"]="uniform-foot-centroid-profile-seed",
            ["selectedScale"]=selectedScale,["selectedVertexViolations"]=applicable?best:0,["profiles"]=profiles,
            ["selection"]="minimum vertex violations; ties choose smallest scale",
            ["pivotPolicy"]="XYZ centroid of pure foot-subtree vertices",["leftPureVertices"]=pureLeft,["rightPureVertices"]=pureRight,
            ["leftPivot"]=new JArray(lp.x,lp.y,lp.z),["rightPivot"]=new JArray(rp.x,rp.y,rp.z),
            ["allSkinInfluencesUsed"]=true,["topologyUnchanged"]=true,["sourceUnchanged"]=true,
            ["acceptanceOverride"]=false,["requiresFinalPhysicalAndPostBindAcceptance"]=true};
        var normalMatrix=recipient.Skin.transform.worldToLocalMatrix.transpose;var localNormals=source.normals;
        if(localNormals.Length!=count)throw new InvalidOperationException("Uniform foot seed requires complete source normals");
        var normals=new Vector3[count];
        for(int i=0;i<count;i++)
        {
            float footWeight=applicable?Mathf.Clamp01(left[i]+right[i]):0;
            var gapNormal=normalMatrix.MultiplyVector(localNormals[i]).normalized;
            var boneNormal=boneMappedWorldNormals[i];
            if(!float.IsFinite(boneNormal.x)||!float.IsFinite(boneNormal.y)||!float.IsFinite(boneNormal.z))
                throw new ArgumentException("Nonfinite bone-mapped uniform foot normal");
            // Positive uniform scale preserves the bone-mapped foot normal; retain
            // transported source-gap normals elsewhere and blend at the ankle.
            normals[i]=(gapNormal*(1-footWeight)+boneNormal.normalized*footWeight).normalized;
        }
        return BindMesh(source,recipient,selected,normals,"UniformFoot_"+source.name);
    }
}
}
#endif
