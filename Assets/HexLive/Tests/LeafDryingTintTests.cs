using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class LeafDryingTintTests
{
#if UNITY_EDITOR
    [TestCase("resource.palm_leaf")]
    [TestCase("resource.palm_crown")]
    [TestCase("architecture.roof.palm")]
    [TestCase("bed.basic")]
    [TestCase("station.water_collector")]
    public void RealLeafSlotsTurnYellowWhileNonLeafSlotsAndSourceMaterialsStayUntouched(string id)
    {
        GameObject root=null; RenderTexture target=null; Texture2D pixels=null;
        var cameraRoot=new GameObject("leaf aging proof camera");
        var lightRoot=new GameObject("leaf aging proof light");
        var oldResolver=BedAssembly.EditorObjectPrefabResolver;
        try
        {
            GameObject Resolve(string key)=>UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/RuntimeSource/Objects/"+WorldPropResources.NativeName(key)+".fbx");
            var source=Resolve(id); Assert.That(source,Is.Not.Null,id);
            BedAssembly.EditorObjectPrefabResolver=Resolve;
            root=BedAssembly.IsAssembled(id) ? BedAssembly.BuildFinished(id) : Object.Instantiate(source);
            Assert.That(root,Is.Not.Null); root.transform.position=Vector3.zero;
            foreach(var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer=31;
            var renderers=root.GetComponentsInChildren<Renderer>(true);
            var original=renderers.SelectMany(r=>r.sharedMaterials).Distinct().ToDictionary(m=>m,m=>m.color);
            var tint=root.AddComponent<LeafDryingTint>(); tint.Initialize();
            TestContext.WriteLine(id+": "+string.Join(";",renderers.Select(r=>r.name+"="+string.Join(",",r.sharedMaterials.Select(m=>m.name)))));
            Assert.That(tint.LeafSlotCount,Is.GreaterThan(0),"Real leaf material/name contract must match");
            tint.Sync(.5f);
            var blocks=new System.Collections.Generic.List<Color>();
            var block=new MaterialPropertyBlock();
            var colorId=Shader.PropertyToID("_BaseColor");
            foreach(var renderer in renderers)
                for(var slot=0;slot<renderer.sharedMaterials.Length;slot++)
                {
                    renderer.GetPropertyBlock(block,slot);
                    if(!block.isEmpty) blocks.Add(block.GetColor(colorId));
                }
            Assert.That(blocks.Count,Is.EqualTo(tint.LeafSlotCount));
            tint.Sync(1f);
            foreach(var renderer in renderers)
                for(var slot=0;slot<renderer.sharedMaterials.Length;slot++)
                {
                    renderer.GetPropertyBlock(block,slot);
                    if(block.isEmpty) continue;
                    var dry=block.GetColor(colorId);
                    Assert.That(dry.r,Is.GreaterThan(dry.g));
                    Assert.That(dry.g,Is.GreaterThan(dry.b));
                }
            foreach(var pair in original) Assert.That(pair.Key.color,Is.EqualTo(pair.Value),"Shared material was mutated");
            // A real GPU proof, same native model and assembly path, not a colour swatch.
            var bounds=renderers[0].bounds;
            foreach(var renderer in renderers) if(renderer.gameObject.activeInHierarchy) bounds.Encapsulate(renderer.bounds);
            var camera=cameraRoot.AddComponent<Camera>(); camera.cullingMask=1<<31;
            var light=lightRoot.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1f;
            light.cullingMask=1<<31;lightRoot.transform.rotation=Quaternion.Euler(40,25,0);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.14f,.18f);
            camera.orthographic=true;camera.orthographicSize=Mathf.Max(bounds.size.x,bounds.size.z)*.7f+.2f;
            camera.transform.position=bounds.center+new Vector3(0,5,-4);camera.transform.LookAt(bounds.center);
            target=new RenderTexture(640,480,24);target.Create();camera.targetTexture=target;
            var path=Path.Combine(Application.dataPath,"../Build/MSIRelease/20261014-leaf-drying");Directory.CreateDirectory(path);
            foreach(var age in new[]{0f,.5f,1f})
            {
                tint.Sync(age);camera.Render();var previous=RenderTexture.active;RenderTexture.active=target;
                pixels=new Texture2D(640,480,TextureFormat.RGB24,false);
                pixels.ReadPixels(new Rect(0,0,640,480),0,0);pixels.Apply();RenderTexture.active=previous;
                File.WriteAllBytes(Path.Combine(path,id+"-"+age.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+".png"),pixels.EncodeToPNG());
                Object.DestroyImmediate(pixels);pixels=null;
            }
        }
        finally
        {
            BedAssembly.EditorObjectPrefabResolver=oldResolver;
            if(pixels!=null) Object.DestroyImmediate(pixels);
            var camera=cameraRoot.GetComponent<Camera>();
            if(camera!=null) camera.targetTexture=null;
            if(target!=null) { target.Release();Object.DestroyImmediate(target); }
            if(root!=null) Object.DestroyImmediate(root);
            Object.DestroyImmediate(cameraRoot);
            Object.DestroyImmediate(lightRoot);
        }
    }
#endif
}
}
