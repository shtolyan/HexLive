#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Final acceptance uses the ACTUAL equipment factory, not an approximation
    // of its root parenting, layer state or heel lifecycle. Base generation is
    // deliberately unchanged; only the derived final stage calls this helper.
    static Fit ConstructFinalRuntimeFit(GameObject template,Mesh female,Mesh male,ActorName actor)
    {
        var result=new Fit();GameObject privateTemplate=null;
        try
        {
            privateTemplate=Object.Instantiate(template);privateTemplate.hideFlags=HideFlags.HideAndDontSave;
            AssignFinalMeshes(privateTemplate,female,male);
            var authored=privateTemplate.GetComponent<Wear>();var sourceSkin=privateTemplate.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var sourcePaths=sourceSkin.bones.Select(b=>RelativePath(privateTemplate.transform,b)).ToArray();
            result.Body=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Actors/"+actor+".prefab"));
            result.Body.hideFlags=HideFlags.HideAndDontSave;result.Body.GetComponent<Animator>().enabled=false;
            var bones=result.Body.GetComponent<BodyBones>();bones.Construct(actor,649);
            var underlayers=new List<SkinnedMeshRenderer>();
            if(!(authored.Layer==VisualWearLayer.Underwear&&authored.Slots.Contains(VisualWearSlot.Pelvis)))
            {
                bones.Equip("final-proof-briefs",AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Wear/"+actor+"_PrimalBriefs.prefab").GetComponent<Wear>(),null);
                underlayers.AddRange(bones.GetWorn("final-proof-briefs").GetComponentsInChildren<SkinnedMeshRenderer>(true));
            }
            if(authored.Slots.Contains(VisualWearSlot.Chest)&&!authored.HeedHideUnderwearSlot(VisualWearSlot.Chest))
            {
                bones.Equip("final-proof-top",AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Wear/"+actor+"_PrimalTop.prefab").GetComponent<Wear>(),null);
                underlayers.AddRange(bones.GetWorn("final-proof-top").GetComponentsInChildren<SkinnedMeshRenderer>(true));
            }
            bones.Equip("final-proof-item",authored,null);
            result.Garment=bones.GetWorn("final-proof-item").gameObject;
            result.Skin=result.Garment.GetComponentInChildren<SkinnedMeshRenderer>(true);
            typeof(BodyBones).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bones,null);
            result.Bones=result.Skin.bones;result.Paths=sourcePaths;
            if(result.Bones.Length!=sourceSkin.bones.Length||!result.Bones.Select(b=>b.name).SequenceEqual(sourceSkin.bones.Select(b=>b.name)))
                throw new InvalidOperationException("Final actual factory changed skeleton order");
            result.BoneWorld=result.Bones.Select(b=>b.localToWorldMatrix).ToArray();
            result.BodySurface=TriangleTree.ForRenderer(ActorBodyResolver.ResolveOrNull(result.Body.transform));
            result.Underlayers=underlayers.Select(TriangleTree.ForRenderer).ToArray();result.RadialCentre=bones.GetBone("pelvis").position;
            result.Baked=new Mesh();result.Skin.BakeMesh(result.Baked,true);
            var matrix=result.Skin.transform.localToWorldMatrix;var normalMatrix=matrix.inverse.transpose;
            result.WorldVertices=result.Baked.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            result.WorldNormals=result.Baked.normals.Select(n=>normalMatrix.MultiplyVector(n).normalized).ToArray();
            return result;
        }
        catch{result.Dispose();throw;}
        finally{if(privateTemplate!=null)Object.DestroyImmediate(privateTemplate);}
    }
}
}
#endif
