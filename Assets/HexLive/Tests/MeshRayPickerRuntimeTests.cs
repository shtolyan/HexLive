#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Views;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class MeshRayPickerRuntimeTests
{
    public static string[] ScaledBodies=>new[]{"Marta|unit","Marta|small","Marta|wide","Kshishtof|unit","Kshishtof|small","Kshishtof|wide"};

    [UnityTest]
    public IEnumerator RealImportedBodyUsesItsFullRendererMatrixAndReturnsWorldDistance([ValueSource(nameof(ScaledBodies))] string value)
    {
        Assert.That(Application.isPlaying,Is.True);
        var parts=value.Split('|');var name=parts[0];
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        try
        {
            var animator=actor.GetComponent<Animator>();animator.enabled=false;
            actor.GetComponent<BodyBones>().Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.runtimeAnimatorController=null;
            animator.enabled=true;animator.Rebind();animator.Update(0);animator.enabled=false;
            actor.transform.localScale=parts[1]=="small"?new Vector3(.35f,.65f,.9f):parts[1]=="wide"?new Vector3(1.4f,.8f,1.1f):Vector3.one;
            var view=actor.GetComponent<NpcActorView>()??actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);skin.enabled=true;skin.updateWhenOffscreen=true;
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&b.name==id);
            var rows=new JArray();float previous=-1;
            for(var phase=0;phase<2;phase++)
            {
                actor.transform.position=phase==0?new Vector3(1000,2,1000):new Vector3(-17,3,-11);
                actor.transform.rotation=Quaternion.Euler(phase==0?0:17,phase==0?32:113,phase==0?0:11);
                yield return null;yield return null;
                var point=(Bone("rForearmBend").position+Bone("rHand").position)*.5f;
                var ray=new Ray(point-actor.transform.forward*3.5f,actor.transform.forward);
                Assert.That(skin.bounds.Contains(point),Is.True,"The ray target is in the actual rendered arm bounds");
                Assert.That(MeshRayPicker.TryIntersect(skin,ray,out var distance),Is.True,value+" phase="+phase);
                Assert.That(distance,Is.InRange(3.2f,3.6f),"Triangle distance must be world metres even with a .01 FBX renderer and nonuniform actor scale");
                Assert.That(view.TryRaycastVisibleGeometry(ray,4,out var visible),Is.True);
                Assert.That(visible,Is.EqualTo(distance).Within(.001f));
                Assert.That(view.TryRaycastVisibleGeometry(ray,distance*.5f,out _),Is.False,"Respect the caller's nearer world-distance limit");
                if(previous>0)Assert.That(distance,Is.EqualTo(previous).Within(.003f),"Moving/yawing the actor must preserve the relative surface distance");
                previous=distance;
                rows.Add(new JObject{["phase"]=phase,["distanceWorld"]=distance,["skinScale"]=new JArray(skin.transform.lossyScale.x,skin.transform.lossyScale.y,skin.transform.lossyScale.z)});
            }
            var output="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(output);
            File.WriteAllText(output+"/scaled-mesh-"+value.Replace('|','-')+".json",rows.ToString());
        }
        finally{Object.DestroyImmediate(actor);}
    }

    [Test]
    public void TinyStaticMeshKeepsScaledRotatedWorldDistanceAndSubmeshSelection()
    {
        var root=new GameObject("Tiny authored mesh #299 control");var mesh=new Mesh();
        try
        {
            mesh.vertices=new[]{new Vector3(-.005f,-.005f,0),new Vector3(.005f,-.005f,0),new Vector3(0,.005f,0),
                new Vector3(.025f,-.005f,0),new Vector3(.035f,-.005f,0),new Vector3(.03f,.005f,0)};
            mesh.subMeshCount=2;mesh.SetTriangles(new[]{0,1,2},0);mesh.SetTriangles(new[]{3,4,5},1);mesh.RecalculateBounds();
            root.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=root.AddComponent<MeshRenderer>();
            root.transform.position=new Vector3(83,7,-41);root.transform.rotation=Quaternion.Euler(23,71,17);root.transform.localScale=new Vector3(77,31,49);
            var point=root.transform.TransformPoint(new Vector3(0,-.001f,0));
            var normal=Vector3.Cross(root.transform.TransformVector(Vector3.right),root.transform.TransformVector(Vector3.up)).normalized;
            var ray=new Ray(point-normal*3.5f,normal);
            Assert.That(MeshRayPicker.TryIntersect(renderer,ray,out var all),Is.True);Assert.That(all,Is.EqualTo(3.5f).Within(.001f));
            Assert.That(MeshRayPicker.TryIntersect(renderer,0,ray,out var first),Is.True);Assert.That(first,Is.EqualTo(all).Within(.001f));
            Assert.That(MeshRayPicker.TryIntersect(renderer,1,ray,out _),Is.False);
        }
        finally{Object.DestroyImmediate(root);Object.DestroyImmediate(mesh);}
    }
}
}
#endif
