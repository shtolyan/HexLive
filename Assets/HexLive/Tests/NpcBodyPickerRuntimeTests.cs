#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class NpcBodyPickerRuntimeTests
{
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
