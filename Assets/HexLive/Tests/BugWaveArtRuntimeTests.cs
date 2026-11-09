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
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public class BugWaveArtRuntimeFixture
{
    protected const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    readonly List<(IDictionary cache, Hashtable saved)> caches = new();
    readonly List<GameObject> roots = new();
    IDictionary Isolate(Type type, string field)
    {
        var d = (IDictionary)type.GetField(field, Static).GetValue(null);
        var saved = new Hashtable(); foreach (DictionaryEntry e in d) saved[e.Key] = e.Value;
        caches.Add((d, saved)); d.Clear(); return d;
    }
    [SetUp] public void Setup()
    {
        var handles = Isolate(typeof(ContentPrefabCache), "Handles");
        Isolate(typeof(GearLibrary), "Configs");
        foreach (var id in new[] { ContentIds.DryingRack, ContentIds.Torch })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
            Assert.That(prefab, Is.Not.Null);
            handles["object/" + id] = Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { prefab, null }, null);
        }
        GearLibrary.Register(AssetDatabase.LoadAssetAtPath<GearConfig>("Assets/HexLiveContent/RuntimeSource/Gear/torch.asset"));
        var wear = Isolate(typeof(ActorWardrobe), "Cache");
        var variants = Isolate(typeof(GarmentVariants), "ById");
        foreach (var r in JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"].Where(r => (string)r["type"] == "wear"))
        {
            var id = (string)r["id"];
            wear[id] = new List<Wear> { AssetDatabase.LoadAssetAtPath<GameObject>((string)r["main"]).GetComponent<Wear>() };
            variants[id] = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)r["definition"]);
        }
    }
    [TearDown] public void Cleanup()
    {
        foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
        roots.Clear(); foreach (var (d, old) in caches) { d.Clear(); foreach (DictionaryEntry e in old) d[e.Key] = e.Value; }
        caches.Clear();
    }
    protected GameObject Keep(GameObject o) { Assert.That(o, Is.Not.Null); roots.Add(o); return o; }
    protected void Capture(Vector3 look, string file, float viewScale = 1f)
    {
        var c = Keep(new GameObject("Art acceptance camera")).AddComponent<Camera>();
        c.transform.position = look + new Vector3(2.1f, 1.1f, -2.4f) * viewScale; c.transform.LookAt(look);
        c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(.16f,.20f,.24f);
        c.nearClipPlane = .01f; c.farClipPlane = 20f; c.fieldOfView = 36;
        var rt = new RenderTexture(1200, 1000, 24); var old = RenderTexture.active;
        var t = new Texture2D(1200,1000,TextureFormat.RGB24,false);
        try { c.targetTexture=rt; c.Render(); RenderTexture.active=rt; t.ReadPixels(new Rect(0,0,1200,1000),0,0);t.Apply();
            Directory.CreateDirectory("Build/Diagnostics/bug-wave-20261109/visual");
            File.WriteAllBytes("Build/Diagnostics/bug-wave-20261109/visual/"+file+".png",t.EncodeToPNG()); }
        finally { c.targetTexture=null; RenderTexture.active=old; Object.DestroyImmediate(t); Object.DestroyImmediate(rt); }
    }
}
public sealed class DryingRackHangerRuntimeTests : BugWaveArtRuntimeFixture
{
    [UnityTest] public IEnumerator DryingRackHasEightShoulderHangersAtEveryFootprintYaw()
    {
        var rack = Keep(BedAssembly.BuildFinished(ContentIds.DryingRack));
        Assert.That(rack.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="HangerTemplate").gameObject.activeSelf, Is.False);
        var prefab = WorldPropResources.Load(ContentIds.DryingRack);
        var ids = ((IDictionary)typeof(ActorWardrobe).GetField("Cache",Static).GetValue(null)).Keys.Cast<string>()
            .Where(x=>x.StartsWith("clothing.dress_primal") && !x.Contains("male")).Take(7).Concat(new[]{"clothing.top_primal"}).ToArray();
        Assert.That(ids.Length, Is.EqualTo(8));
        for (var i=0;i<8;i++)
        {
            var socket=DryingRackHangers.Slot(i);
            Assert.That(socket.y,Is.EqualTo(1.32f).Within(.0001f));
            if(i>0) Assert.That(Vector3.Distance(socket,DryingRackHangers.Slot(i-1)),Is.EqualTo(.1f).Within(.0001f));
            var hanger=Keep(WardrobeHangerFactory.Build(prefab)); hanger.transform.SetParent(rack.transform,false); hanger.transform.localPosition=socket;
            Assert.That(ObjectFit.HasRenderableGeometry(hanger),Is.True);
            var garment=Keep(GarmentDropFactory.BuildHanging(ids[i])); garment.transform.SetParent(rack.transform,false);
            garment.transform.localScale=Vector3.one*(1.5f*(11f/30f)*2.4f/1.7f);
            garment.transform.localPosition=(Vector3)typeof(HexWorldRenderer).GetMethod("HangSlotOffset",Static)
                .Invoke(null,new object[]{garment.transform,i,false,socket});
        }
        var light=Keep(new GameObject("Acceptance sun")).AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.4f;light.transform.rotation=Quaternion.Euler(40,-25,0);
        for(var yaw=0;yaw<6;yaw++)
        {
            rack.transform.rotation=Quaternion.Euler(0,yaw*60,0); yield return null;
            foreach(var t in rack.GetComponentsInChildren<Transform>().Where(t=>t.name.StartsWith("GarmentDrop ")))
            { ObjectFit.WorldBounds(t.gameObject,out var b); Assert.That(b.min.y,Is.GreaterThanOrEqualTo(.039f)); }
            if(yaw==0 || yaw==1) Capture(new Vector3(0,.70f,0),"rack-yaw"+yaw);
        }
    }
}
}
#endif
