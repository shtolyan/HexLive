#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
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
public sealed class LeatherFlaskRuntimeTests : BugWaveArtRuntimeFixture
{
    const string Id=ContentIds.LeatherFlask;
    const string Output="Build/Diagnostics/bug-wave2-actions/flask-runtime";
    private GameObject RegisterFlask()
    {
        var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/"+Id+".fbx");
        Assert.That(source,Is.Not.Null);
        var handles=(IDictionary)typeof(ContentPrefabCache).GetField("Handles",Static).GetValue(null);
        handles["object/"+Id]=Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>),
            BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{source,null},null);
        var config=AssetDatabase.LoadAssetAtPath<GearConfig>("Assets/HexLiveContent/RuntimeSource/Gear/flask_leather.asset");
        Assert.That(config,Is.Not.Null);GearLibrary.Register(config);
        Assert.That(config.gearId,Is.EqualTo(Id));
        Assert.That(config.craftStation,Is.EqualTo(CraftPlace.Workbench));
        Assert.That(config.craftIngredients.Select(i=>(i.ResolveId(),i.count)),
            Is.EquivalentTo(new[]{(ContentIds.Hide,1),(ContentIds.Rope,1)}));
        return source;
    }
    [Test]
    public void ActualOwnerDiscoverySpriteLocalizationAndGroundFactoryAreComplete()
    {
        RegisterFlask();
        var atomic=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("AtomicContentBatchBuild")).First(t=>t!=null);
        var main=(string)atomic.GetMethod("FindObjectMain",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Id});
        Assert.That(main,Is.EqualTo("Assets/HexLiveContent/RuntimeSource/Objects/"+Id+".fbx"));
        var sprite=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/"+Id+".png");
        Assert.That(sprite,Is.Not.Null);Assert.That(sprite.rect.width,Is.EqualTo(512));
        Assert.That(ItemIcons.Address(Id),Is.EqualTo("object/"+Id+"#icon"));
        var previous=Loc.Current;
        try
        {
            foreach(var language in new[]{Language.Russian,Language.English})
            {
                Loc.Current=language;
                foreach(var key in new[]{"item.tool_flask_leather.name","item.tool_flask_leather.desc"})
                {Assert.That(Loc.Has(key),Is.True);Assert.That(Loc.Get(key),Is.Not.EqualTo(key).And.Not.Empty);}
            }
        }
        finally {Loc.Current=previous;}
        var drop=Keep(WorldPropResources.Build(Id));
        Assert.That(drop.GetComponentInChildren<MeshRenderer>(),Is.Not.Null);
        foreach(var material in drop.GetComponentsInChildren<Renderer>().SelectMany(r=>r.sharedMaterials))
        {
            Assert.That(material,Is.Not.Null);
            Assert.That(material.shader.name,Does.StartWith("Universal Render Pipeline/"),"Use the actual imported URP material, not a preview override.");
        }
        var renderer=Keep(new GameObject("Flask production fit")).AddComponent<HexWorldRenderer>();renderer.enabled=false;
        typeof(HexWorldRenderer).GetMethod("FitObjectPrefab",BindingFlags.NonPublic|BindingFlags.Instance)
            .Invoke(renderer,new object[]{drop,Id,650,false,true});
        Assert.That(ObjectFit.TryMeasuredSize(drop,Id,out var size),Is.True);
        Assert.That(size,Is.EqualTo(.24f).Within(.0001));
        Assert.That(ObjectFit.WorldBounds(drop,out var bounds),Is.True);
        Assert.That(bounds.min.y,Is.EqualTo(0f).Within(.00005));
        Assert.That(drop.GetComponentInChildren<Light>(),Is.Null);
        Assert.That(drop.GetComponentInChildren<BottleLiquidVisual>(),Is.Null,"Opaque hide has no plastic liquid inset.");
    }
    [UnityTest]
    public IEnumerator GroundSizeComparisonUsesTheActualPlasticBottleAndFlaskFactories()
    {
        RegisterFlask();
        var original=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/tool_bottle_native.prefab");
        Assert.That(original,Is.Not.Null);
        var handles=(IDictionary)typeof(ContentPrefabCache).GetField("Handles",Static).GetValue(null);
        handles["object/"+ContentIds.Bottle]=Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>),
            BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{original,null},null);
        var renderer=Keep(new GameObject("Vessel size comparison production fit")).AddComponent<HexWorldRenderer>();renderer.enabled=false;
        var fit=typeof(HexWorldRenderer).GetMethod("FitObjectPrefab",BindingFlags.NonPublic|BindingFlags.Instance);
        foreach(var id in new[]{ContentIds.Bottle,Id})
        {
            var prop=Keep(WorldPropResources.Build(id));
            fit.Invoke(renderer,new object[]{prop,id,650,false,true});
            Assert.That(ObjectFit.TryMeasuredSize(prop,id,out var size),Is.True);
            Assert.That(size,Is.EqualTo(id == Id ? .24f : .27f).Within(.0001));
            Assert.That(ObjectFit.WorldBounds(prop,out var bounds),Is.True);
            Assert.That(bounds.min.y,Is.EqualTo(0f).Within(.00005));
            prop.transform.position+=new Vector3(1000+(id == Id ? .18f : -.18f),0,1000);
        }
        var light=Keep(new GameObject("Vessel comparison light")).AddComponent<Light>();light.type=LightType.Directional;
        light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(40,-25,0);
        yield return null;yield return null;
        Capture(new Vector3(1000,.12f,1000),"flask-vs-plastic-ground",.3f);
    }
    [UnityTest] public IEnumerator MartaRight()=>Review("Marta",false);
    [UnityTest] public IEnumerator MartaLeft()=>Review("Marta",true);
    [UnityTest] public IEnumerator KshishtofRight()=>Review("Kshishtof",false);
    [UnityTest] public IEnumerator KshishtofLeft()=>Review("Kshishtof",true);
    IEnumerator Review(string name,bool left)
    {
        RegisterFlask();
        var actor=Keep(Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/"+name+".prefab")));
        actor.transform.position=new Vector3(1000,0,1000);actor.transform.localScale=Vector3.one*.7764706f;
        var bones=actor.GetComponent<BodyBones>();bones.Construct((ActorName)Enum.Parse(typeof(ActorName),name),650);
        var animator=actor.GetComponent<Animator>();animator.runtimeAnimatorController=null;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();
        var clip=GearLibrary.ConfigFor(Id).armedIdle;Assert.That(clip,Is.Not.Null);
        var graph=PlayableGraph.Create("Leather flask real hand verification");
        var play=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"idle",animator).SetSourcePlayable(play);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);graph.Play();play.SetTime(clip.length*.4);graph.Evaluate(0);
        try
        {
            yield return null;yield return null;
            var handBone=bones.GetBone(left?"lHand":"rHand");
            Assert.That(ActorBodyResolver.TryResolve(actor,out var skin,out var error),Is.True,error);
            Assert.That(skin.bones.Contains(handBone),Is.True);
            var appearance=bones.GetComponent<PeopleAppearance>();var hand=appearance!=null?appearance.PropAnchor(handBone):handBone;
            var type=typeof(NpcActorView).Assembly.GetType("HexLive.UnityPresentation.Wearing.HandPropVisual");
            var prop=Keep((GameObject)type.GetMethod("Create",BindingFlags.Static|BindingFlags.Public)
                .Invoke(null,new object[]{Id,hand,left}));
            Assert.That(ObjectFit.TryMeasuredSize(prop,Id,out var size),Is.True);
            Assert.That(size,Is.EqualTo(.24f).Within(.001));
            var palm=hand.TransformPoint(new Vector3(left?-.0543f:.0543f,-.0236f,-.051f));
            var errorDistance=Vector3.Distance(prop.transform.position,palm);
            Assert.That(errorDistance,Is.LessThan(.005f),"The model origin is its neck grip.");
            var light=Keep(new GameObject("Flask acceptance light")).AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(40,-25,0);
            yield return null;yield return null;
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output+"/"+name+(left?"-left":"-right")+".json",new JObject{
                ["size"]=size,["gripError"]=errorDistance,["handBone"]=handBone.name,["iconOwner"]=ItemIcons.Address(Id)}.ToString());
            // Real PlayerLoop poses have completed before capture, as in TorchGripRuntimeTests.
            Capture(actor.transform.position+new Vector3(0,.65f,0),"flask-"+name+(left?"-left":"-right"));
            Capture(prop.transform.position,"flask-close-"+name+(left?"-left":"-right"),.35f);
        }
        finally {if(graph.IsValid()) graph.Destroy();}
    }
}
}
#endif
