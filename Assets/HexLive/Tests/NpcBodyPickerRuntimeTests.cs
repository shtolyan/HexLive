#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class NpcBodyPickerRuntimeTests
{
    public static string[] ArmCases => new[]{"Marta|l|upright","Marta|r|upright","Marta|l|prone","Marta|r|prone",
        "Kshishtof|l|upright","Kshishtof|r|upright","Kshishtof|l|prone","Kshishtof|r|prone"};
    public static string[] Actors => new[]{"Marta","Kshishtof"};

    [UnityTest]
    public IEnumerator VisibleOutstretchedForearmAndPalmRemainPickableOutsideTheOldCoreVolumes(
        [ValueSource(nameof(ArmCases))] string value)
    {
        Assert.That(Application.isPlaying,Is.True,"Run the actual imported-body fixture in PlayMode");
        var parts=value.Split('|');var name=parts[0];var side=parts[1];var prone=parts[2]=="prone";
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        try
        {
            var animator=actor.GetComponent<Animator>();animator.enabled=false;
            actor.GetComponent<BodyBones>().Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            // This fixture has no camera and deliberately moves far offscreen.
            // Evaluate the real imported rig and let Unity update its skinned
            // bounds before asking the old exact-mesh picker (whose first gate
            // is Renderer.bounds). Bone positions alone do not refresh bounds.
            animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController=null;animator.enabled=true;
            animator.Rebind();animator.Update(0);animator.enabled=false;
            actor.transform.localScale=Vector3.one*.7764706f;actor.transform.position=new Vector3(1000,2,1000);
            actor.transform.rotation=Quaternion.Euler(prone?90:0,32,0);
            var view=actor.GetComponent<NpcActorView>()??actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);skin.enabled=true;
            skin.updateWhenOffscreen=true;
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&string.Equals(b.name,id,StringComparison.OrdinalIgnoreCase));
            var elbow=Bone(side+"ForearmBend");var hand=Bone(side+"Hand");var palm=Bone(side+"Mid2");
            yield return null;yield return null;
            var rows=new JArray();
            foreach(var point in new[]{(elbow.position+hand.position)*.5f,(hand.position+palm.position)*.5f})
            {
                var ray=new Ray(point-actor.transform.forward*3,actor.transform.forward);
                Assert.That(OldCoreHit(skin,ray,10),Is.False,value+" is a concrete miss of the old torso/legs-only picker");
                var exactMeshHit=HexLive.UnityPresentation.Views.MeshRayPicker.TryIntersect(skin,ray,out var exactMeshDistance);
                Assert.That(view.TryRaycastVisibleGeometry(ray,10,out var realMeshHit),Is.True,
                    value+" must be genuinely visible imported skin, not an arbitrary point outside the body; "+
                    "point="+point+" bounds="+skin.bounds+" directMeshHit="+exactMeshHit+" directMeshDistance="+exactMeshDistance);
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hit),Is.True,value);
                Assert.That(hit,Is.InRange(2.6f,3.1f));
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,hit*.5f,out _),Is.False,"Respect a nearer target's maximum distance");
                skin.enabled=false;
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hiddenHit),Is.True,value+" clothing-hidden body");
                Assert.That(hiddenHit,Is.EqualTo(hit).Within(.00001f));skin.enabled=true;
                rows.Add(new JObject{["point"]=V(point),["realMeshDistance"]=realMeshHit,["bodyDistance"]=hit,["oldCoreMiss"]=true});
            }
            var empty=(hand.position+palm.position)*.5f+actor.transform.right*(side=="l"?-.4f:.4f);
            Assert.That(NpcBodyPicker.TryIntersect(view,new Ray(empty-actor.transform.forward*3,actor.transform.forward),10,out _),Is.False,
                "Do not replace precise limb volumes with one wide body AABB");
            var directory="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(directory);
            File.WriteAllText(directory+"/arms-"+value.Replace('|','-')+".json",new JObject{["sourcePose"]="Actual authored prefab; no mesh/pose edits",
                ["actor"]=name,["side"]=side,["prone"]=prone,["hits"]=rows}.ToString());
        }
        finally{Object.DestroyImmediate(actor);}
    }

    [UnityTest]
    public IEnumerator CachedArmReferencesFollowTheActualImportedPunchClip([ValueSource(nameof(Actors))] string name)
    {
        Assert.That(Application.isPlaying,Is.True);
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        var graph=PlayableGraph.Create("NpcBodyPicker actual arm motion");
        try
        {
            var animator=actor.GetComponent<Animator>();animator.enabled=false;
            var bones=actor.GetComponent<BodyBones>();bones.Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            var view=actor.GetComponent<NpcActorView>()??actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&b.name==id);
            // Warm the real picker in the authored pose, before bones animate.
            var warm=(Bone("rHand").position+Bone("rMid2").position)*.5f;
            Assert.That(NpcBodyPicker.TryIntersect(view,new Ray(warm-Vector3.forward*3,Vector3.forward),10,out _),Is.True);
            var before=actor.transform.InverseTransformPoint(Bone("rHand").position);
            var clip=AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Punch A_once_to65.fbx")
                .OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview__",StringComparison.Ordinal));
            animator.enabled=true;animator.runtimeAnimatorController=null;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();
            var play=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"real punch",animator).SetSourcePlayable(play);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);graph.Play();float moved=0;var rows=new JArray();
            foreach(var time in new[]{.2f,.5f,.8f})
            {
                play.SetTime(clip.length*time);graph.Evaluate(0);
                yield return null;yield return null;
                moved=Mathf.Max(moved,Vector3.Distance(before,actor.transform.InverseTransformPoint(Bone("rHand").position)));
                foreach(var side in new[]{"l","r"})
                {
                    var point=(Bone(side+"Hand").position+Bone(side+"Mid2").position)*.5f;
                    var ray=new Ray(point-actor.transform.forward*3,actor.transform.forward);
                    Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hit),Is.True,name+"/"+side+" at "+time);
                    rows.Add(new JObject{["time"]=time,["side"]=side,["palm"]=V(point),["distance"]=hit});
                }
            }
            Assert.That(moved,Is.GreaterThan(.05f),"The real imported clip must actually move the arm");
            var directory="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(directory);
            File.WriteAllText(directory+"/arms-punch-"+name+".json",new JObject{["clip"]=AssetDatabase.GetAssetPath(clip),
                ["handMotionMetres"]=moved,["hits"]=rows,["cacheWarmedBeforeAnimation"]=true}.ToString());
        }
        finally{if(graph.IsValid())graph.Destroy();Object.DestroyImmediate(actor);}
    }

    static JArray V(Vector3 point)=>new JArray(point.x,point.y,point.z);
    // Differential control retained for this regression: a ray crossing actual
    // visible forearm/palm skin was completely outside all five old volumes.
    static bool OldCoreHit(SkinnedMeshRenderer skin,Ray ray,float maximum)
    {
        Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&b.name==id);
        var p=Bone("pelvis").position;var h=Bone("head").position;
        var l=Bone("lFoot").position;var r=Bone("rFoot").position;var lk=Bone("lShin").position;var rk=Bone("rShin").position;
        var height=Vector3.Distance(p,h)+(Vector3.Distance(p,lk)+Vector3.Distance(lk,l)+Vector3.Distance(p,rk)+Vector3.Distance(rk,r))*.5f;
        NpcPickMath.Point P(Vector3 v)=>new(v.x,v.y,v.z);
        bool Span(Vector3 a,Vector3 b,float radius)=>NpcPickMath.Capsule(P(ray.origin),P(ray.direction),P(a),P(b),radius,maximum,out _);
        return Span(p,h,height*.16f+.03f)||Span(p,lk,height*.09f+.03f)||Span(lk,l,height*.09f+.03f)||
            Span(p,rk,height*.09f+.03f)||Span(rk,r,height*.09f+.03f);
    }

    [TestCase("Marta",false)] [TestCase("Marta",true)]
    [TestCase("Kshishtof",false)] [TestCase("Kshishtof",true)]
    public void RealBodyCanBePickedAtTorsoAndLegsWhenUprightOrLyingWithBodyLayerHidden(string name,bool prone)
    {
        var actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab"));
        try
        {
            actor.GetComponent<Animator>().enabled=false;
            actor.GetComponent<BodyBones>().Construct((ActorName)Enum.Parse(typeof(ActorName),name),646);
            actor.transform.localScale=Vector3.one*.7764706f;
            actor.transform.position=new Vector3(1000,2,1000);
            actor.transform.rotation=Quaternion.Euler(prone?90:0,32,0);
            var view=actor.AddComponent<NpcActorView>();view.enabled=false;
            var skin=view.PrimaryBodySkin;Assert.That(skin,Is.Not.Null);
            Transform Bone(string id)=>skin.bones.Single(b=>b!=null&&string.Equals(b.name,id,StringComparison.OrdinalIgnoreCase));
            var pelvis=Bone("pelvis");var head=Bone("head");
            var points=new[]{(pelvis.position+head.position)*.5f,
                (pelvis.position+Bone("lFoot").position)*.5f,
                (pelvis.position+Bone("rFoot").position)*.5f};
            var results=new JArray();
            foreach(var point in points)
            {
                var direction=new Vector3(.3f,-.6f,1).normalized;
                var ray=new Ray(point-direction*3,direction);
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var distance),Is.True);
                Assert.That(distance,Is.InRange(2f,3f));
                // Clothing visibility may hide skin; device picking is a body
                // volume, not a test of the skin renderer's current draw bit.
                skin.enabled=false;
                Assert.That(NpcBodyPicker.TryIntersect(view,ray,10,out var hiddenHit),Is.True);
                Assert.That(hiddenHit,Is.EqualTo(distance).Within(.00001f));
                results.Add(new JObject{["point"]=new JArray(point.x,point.y,point.z),["distance"]=distance});
            }
            var output="Build/Diagnostics/bug-wave2-actions/picking";Directory.CreateDirectory(output);
            File.WriteAllText(output+"/"+name+(prone?"-prone":"-upright")+".json",results.ToString());
        }
        finally {Object.DestroyImmediate(actor);}
    }
}
}
#endif
