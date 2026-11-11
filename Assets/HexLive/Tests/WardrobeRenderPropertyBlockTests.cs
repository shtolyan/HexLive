#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class WardrobeRenderPropertyBlockTests
{
    [Test]
    public void ActualGalleryRendererPreservesRendererFallbackAndPerMaterialPriority()
    {
        Type type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
        var method=type.GetMethod("RenderWardrobe",BindingFlags.NonPublic|BindingFlags.Static);
        string output=(string)type.GetField("OutputRoot",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).GetRawConstantValue();
        string key="mpb-regression-"+Guid.NewGuid().ToString("N"),first=output+"/review/"+key+"-renderer.png",second=output+"/review/"+key+"-slot.png";
        var body=new GameObject("private-property-block-fixture");var mesh=new Mesh();Material material=null;Texture2D image=null;
        try
        {
            var shader=Shader.Find("Universal Render Pipeline/Unlit");Assert.That(shader,Is.Not.Null);
            material=new Material(shader);material.SetColor("_BaseColor",Color.red);
            mesh.vertices=new[]{new Vector3(-.3f,.8f,0),new Vector3(.3f,.8f,0),new Vector3(.3f,1.2f,0),new Vector3(-.3f,1.2f,0)};
            mesh.triangles=new[]{0,1,2,0,2,3};mesh.normals=Enumerable.Repeat(Vector3.forward,4).ToArray();
            mesh.boneWeights=Enumerable.Repeat(new BoneWeight{boneIndex0=0,weight0=1},4).ToArray();mesh.bindposes=new[]{Matrix4x4.identity};mesh.RecalculateBounds();
            var bone=new GameObject("fixture-bone");bone.transform.SetParent(body.transform,false);
            var skin=body.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.bones=new[]{bone.transform};skin.rootBone=bone.transform;skin.sharedMaterial=material;
            var rendererBlock=new MaterialPropertyBlock();rendererBlock.SetColor("_BaseColor",Color.blue);skin.SetPropertyBlock(rendererBlock);
            method.Invoke(null,new object[]{body,key+"-renderer",false});
            image=new Texture2D(2,2);Assert.That(image.LoadImage(File.ReadAllBytes(first)),Is.True);
            Assert.That(image.GetPixels32().Count(c=>c.b>180&&c.r<70&&c.g<70),Is.GreaterThan(500),"Actual preview must render the renderer MPB instead of red shared material");
            Object.DestroyImmediate(image);image=null;
            var slotBlock=new MaterialPropertyBlock();slotBlock.SetColor("_BaseColor",Color.green);skin.SetPropertyBlock(slotBlock,0);
            method.Invoke(null,new object[]{body,key+"-slot",false});
            image=new Texture2D(2,2);Assert.That(image.LoadImage(File.ReadAllBytes(second)),Is.True);
            Assert.That(image.GetPixels32().Count(c=>c.g>180&&c.r<70&&c.b<70),Is.GreaterThan(500),"Per-material block must retain Unity priority over renderer fallback");
            Assert.That(material.GetColor("_BaseColor"),Is.EqualTo(Color.red),"Source material must remain unchanged");
            var actualRenderer=new MaterialPropertyBlock();skin.GetPropertyBlock(actualRenderer);Assert.That(actualRenderer.GetColor("_BaseColor"),Is.EqualTo(Color.blue));
            var actualSlot=new MaterialPropertyBlock();skin.GetPropertyBlock(actualSlot,0);Assert.That(actualSlot.GetColor("_BaseColor"),Is.EqualTo(Color.green));
        }
        finally
        {
            if(image!=null)Object.DestroyImmediate(image);Object.DestroyImmediate(body);Object.DestroyImmediate(mesh);if(material!=null)Object.DestroyImmediate(material);
            foreach(string path in new[]{first,second})if(File.Exists(path))File.Delete(path);
        }
    }
}
}
#endif
