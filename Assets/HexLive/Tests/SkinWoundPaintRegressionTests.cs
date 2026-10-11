#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class SkinWoundPaintRegressionTests
    {
        const string Root="Assets/HexLiveContent/People";
        readonly List<(IDictionary cache, Dictionary<object,object> saved)> caches=new();
        bool oldAsync;
        GameObject actor;
        [SetUp] public void Setup()
        {
            oldAsync=ShaderUtil.allowAsyncCompilation;ShaderUtil.allowAsyncCompilation=false;
            foreach(var type in new[]{typeof(PaintPointMap),typeof(SkinPositionMapSet)})
            {
                var cache=(IDictionary)type.GetField("Cache",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
                var saved=new Dictionary<object,object>();foreach(DictionaryEntry entry in cache)saved.Add(entry.Key,entry.Value);
                caches.Add((cache,saved));cache.Clear();
                foreach(var guid in AssetDatabase.FindAssets("t:"+type.Name,new[]{Root+"/PaintMaps"}))
                {
                    var asset=AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid),type);cache[asset.name]=asset;
                }
            }
        }
        [TearDown] public void Cleanup()
        {
            if(actor!=null)Object.DestroyImmediate(actor);
            foreach(var pair in caches){pair.cache.Clear();foreach(var entry in pair.saved)pair.cache.Add(entry.Key,entry.Value);}
            caches.Clear();ShaderUtil.allowAsyncCompilation=oldAsync;
        }
        [Test] public void PositionMapsRetainSignedMeshCoordinates()
        {
            foreach(var name in new[]{"Marta","Kshishtof"})
            {
                var map=AssetDatabase.LoadAssetAtPath<SkinPositionMapSet>(Root+"/PaintMaps/skinpos_"+name+".asset");
                var texture=map.PositionMapOf(0);
                Assert.That(texture.format,Is.EqualTo(TextureFormat.RGBAHalf),name+" position data was clamped by colour import");
                var rt=RenderTexture.GetTemporary(texture.width,texture.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                Graphics.Blit(texture,rt);var old=RenderTexture.active;RenderTexture.active=rt;
                var read=new Texture2D(rt.width,rt.height,TextureFormat.RGBAFloat,false,true);
                read.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);read.Apply();
                var points=read.GetPixels().Where(p=>p.a>.99f).ToArray();
                Assert.That(points.Max(p=>p.g),Is.GreaterThan(100f),name+" lost height");
                Assert.That(points.Min(p=>p.r),Is.LessThan(-1f),name+" lost signed coordinates");
                Object.DestroyImmediate(read);RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);
            }
        }
        [Test] public void StampSizeIsInvariantBetweenMetreAndCentimetreMeshes()
        {
            actor=new GameObject("Sizing test");
            var painter=actor.AddComponent<SkinTexturePainter>();
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            typeof(SkinTexturePainter).GetField("_height",flags).SetValue(painter,1.7f);
            var size=typeof(SkinTexturePainter).GetMethod("SizeFromDensity",flags);
            foreach(var units in new[]{1f,100f})
            {
                var map=ScriptableObject.CreateInstance<PaintPointMap>();map.MeshHeight=1.7f*units;
                typeof(SkinTexturePainter).GetField("_map",flags).SetValue(painter,map);
                var args=new object[]{new PaintPointMap.Point{BindPerU=2f*units,BindPerV=4f*units},.08f,0f,0f,.004f};
                size.Invoke(painter,args);
                Assert.That((float)args[2],Is.EqualTo(.04f).Within(.00001f));
                Assert.That((float)args[3],Is.EqualTo(.02f).Within(.00001f));
                Object.DestroyImmediate(map);
            }
        }
        [UnityTest] public IEnumerator WoundsChangePixelsInEveryZoneAndHealWithoutTouchingEyes()
        {
            foreach(var name in new[]{"Marta","Kshishtof"})
            {
                actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Actors/"+name+".prefab"));
                actor.GetComponent<Animator>().enabled=false;
                var bones=actor.GetComponent<BodyBones>();bones.Construct((ActorName)System.Enum.Parse(typeof(ActorName),name),713);
                Assert.That(ActorBodyResolver.TryResolve(actor,out var body,out var error),Is.True,error);
                var original=body.sharedMaterials.Select(m=>m.GetTexture("_BaseMap")).ToArray();
                var baseline=Read(original[0]);
                var painter=actor.AddComponent<SkinTexturePainter>();painter.Construct(body,new[]{0},bones,actor.transform,713,name);
                foreach(var zone in new[]{"Head","Torso","Pelvis","ArmL","ArmR","LegL","LegR"})
                {
                    painter.Sync(new List<(string,int,float)>{(zone,48,0f),(zone,65,0f),(zone,82,0f)},new HashSet<string>());
                    var until=Time.realtimeSinceStartup+20;
                    // Use the live fresh lane, not PaintCycle: a packed atlas
                    // must not keep a clipped UV rectangle for an ambient cycle.
                    while(!painter.PresentationReady&&Time.realtimeSinceStartup<until)
                    {
                        if(painter.WantsFreshPass)painter.PaintFresh();
                        yield return null;
                    }
                    Assert.That(painter.PresentationReady,Is.True,name+zone);
                    var painted=Read(body.sharedMaterials[0].GetTexture("_BaseMap"));
                    var redPixels=painted.Zip(baseline,(p,b)=>p.r-p.g > b.r-b.g+.04f && p.g < b.g-.025f).Count(p=>p);
                    Assert.That(redPixels,Is.GreaterThan(40),name+" "+zone+" has no visible red wound pixels");
                    for(int i=1;i<original.Length;i++)Assert.That(body.sharedMaterials[i].GetTexture("_BaseMap"),Is.SameAs(original[i]));
                }
                painter.Sync(new List<(string,int,float)>(),new HashSet<string>());
                painter.TryPaintPresentation();
                var healed=Read(body.sharedMaterials[0].GetTexture("_BaseMap"));
                Assert.That(healed.Zip(baseline,(p,b)=>Mathf.Abs(p.r-b.r)+Mathf.Abs(p.g-b.g)+Mathf.Abs(p.b-b.b)).Average(),Is.LessThan(.002f),name+" healed wounds persist");
                Object.DestroyImmediate(actor);actor=null;
            }
        }
        [UnityTest] public IEnumerator BloodAndBruisesPaintAndWashOffPackedBodies()
        {
            foreach(var name in new[]{"Marta","Kshishtof"})
            {
                actor=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root+"/Prefabs/Actors/"+name+".prefab"));
                actor.GetComponent<Animator>().enabled=false;
                var bones=actor.GetComponent<BodyBones>();bones.Construct((ActorName)System.Enum.Parse(typeof(ActorName),name),713);
                Assert.That(ActorBodyResolver.TryResolve(actor,out var body,out var error),Is.True,error);
                var baseline=Read(body.sharedMaterials[0].GetTexture("_BaseMap"));
                var painter=actor.AddComponent<SkinTexturePainter>();painter.Construct(body,new[]{0},bones,actor.transform,713,name);
                foreach(var bruise in new[]{false,true})
                {
                    var damage=new List<(string,float)>{("Torso",.5f),("ArmL",.5f),("LegR",.5f)};
                    painter.Sync(new List<(string,int,float)>(),new HashSet<string>(),
                        zoneDamage:bruise?null:damage,zoneBruise:bruise?damage:null);
                    var until=Time.realtimeSinceStartup+20;
                    while(!painter.TryPaintPresentation()&&Time.realtimeSinceStartup<until)yield return null;
                    Assert.That(painter.PresentationReady,Is.True);
                    var painted=Read(body.sharedMaterials[0].GetTexture("_BaseMap"));
                    Assert.That(painted.Zip(baseline,(p,b)=>Mathf.Abs(p.r-b.r)+Mathf.Abs(p.g-b.g)+Mathf.Abs(p.b-b.b)>.05f).Count(p=>p),
                        Is.GreaterThan(100),name+" missing "+(bruise?"bruises":"blood"));
                    painter.Sync(new List<(string,int,float)>(),new HashSet<string>(),
                        zoneDamage:new List<(string,float)>(),zoneBruise:new List<(string,float)>());
                    until=Time.realtimeSinceStartup+20;
                    while(!painter.TryPaintPresentation()&&Time.realtimeSinceStartup<until)yield return null;
                    var clean=Read(body.sharedMaterials[0].GetTexture("_BaseMap"));
                    Assert.That(clean.Zip(baseline,(p,b)=>Mathf.Abs(p.r-b.r)+Mathf.Abs(p.g-b.g)+Mathf.Abs(p.b-b.b)).Average(),Is.LessThan(.002f));
                }
                Object.DestroyImmediate(actor);actor=null;
            }
        }
        static Color[] Read(Texture source)
        {
            var rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
            Graphics.Blit(source,rt);var old=RenderTexture.active;RenderTexture.active=rt;
            var read=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);read.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);read.Apply();
            var pixels=read.GetPixels();Object.DestroyImmediate(read);RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);return pixels;
        }
    }
}
#endif
