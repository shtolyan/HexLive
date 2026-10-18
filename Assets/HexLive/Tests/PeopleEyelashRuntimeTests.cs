#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
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
public sealed class PeopleEyelashRuntimeTests
{
    private const string Root = "Assets/HexLiveContent/People";
    [TestCase("Marta", "Marta")]
    [TestCase("Marta", "Molly")]
    [TestCase("Marta", "Jana")]
    [TestCase("Marta", "Jolly")]
    [TestCase("Marta", "Masha")]
    [TestCase("Marta", "Rita")]
    [TestCase("Kshishtof", "Kshishtof")]
    [TestCase("Kshishtof", "Tonny")]
    public void EverySelectableSkinCanTanAndBurnWithoutTintingOpticalSurfaces(string actor, string id)
    {
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
        try
        {
            var view = go.AddComponent<NpcActorView>(); view.enabled = false;
            typeof(NpcActorView).GetField("_bodySkins", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(view, go.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            typeof(NpcActorView).GetMethod("ApplySkinSet", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { id, false });
            typeof(NpcActorView).GetMethod("BuildSkinTintTargets", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
            Assert.That(ActorBodyResolver.TryResolve(go, out var body, out var error), Is.True, error);
            var lashes = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "AuthoredEyelashes");
            var optical = new[] { body.sharedMaterials[1], body.sharedMaterials[2], lashes.sharedMaterial };
            var colours = optical.Select(m => m.GetColor("_BaseColor")).ToArray();
            var textures = optical.Select(m => m.GetTexture("_BaseMap")).ToArray();
            view.SetSkinWeathering(0, 0); var clean = body.sharedMaterial.GetColor("_BaseColor");
            view.SetSkinWeathering(1, 0); var tan = body.sharedMaterial.GetColor("_BaseColor");
            Assert.That(Vector4.Distance(clean, tan), Is.GreaterThan(.02f), id + " tan must remain visible");
            view.SetSkinWeathering(1, 1); var burn = body.sharedMaterial.GetColor("_BaseColor");
            Assert.That(burn.r, Is.GreaterThan(tan.r), id + " burn red");
            Assert.That(burn.g, Is.LessThan(tan.g), id + " burn green");
            for (var i = 0; i < optical.Length; i++)
            {
                Assert.That(optical[i].GetColor("_BaseColor"), Is.EqualTo(colours[i]));
                Assert.That(optical[i].GetTexture("_BaseMap"), Is.SameAs(textures[i]));
            }
            Assert.That(lashes.sharedMaterial.IsKeywordEnabled("_ALPHATEST_ON"), Is.True);
        }
        finally { Object.DestroyImmediate(go); }
    }
    [TestCase("Marta")]
    [TestCase("Kshishtof")]
    public void AuthoredLashesKeepSeparateCutoutAndTheExistingSkeleton(string actor)
    {
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
        try
        {
            Assert.That(ActorBodyResolver.TryResolve(go, out var body, out var error), Is.True, error);
            Assert.That(body.sharedMesh.blendShapeCount, Is.EqualTo(109));
            Assert.That(AssetDatabase.GetAssetPath(body.sharedMesh), Is.EqualTo(Root + "/Source/Models/" + actor + ".fbx"));
            var lashes = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).SingleOrDefault(r => r.name == "AuthoredEyelashes");
            Assert.That(lashes, Is.Not.Null, "Simplified body must not replace authored lash cards");
            Assert.That(AssetDatabase.GetAssetPath(lashes.sharedMesh), Does.EndWith(actor + "_Eyelashes.fbx"));
            Assert.That(lashes.sharedMesh.triangles.Length / 3, Is.EqualTo(504));
            Assert.That(lashes.sharedMesh.blendShapeCount, Is.EqualTo(16));
            var world = SkinWorld(lashes); var region = new Bounds(world[0], Vector3.zero);
            foreach (var point in world) region.Encapsulate(point);
            var head = body.bones.Single(b => b.name == "head").position;
            Assert.That(region.size.x, Is.InRange(.05f, .20f), "Lash world width: " + region);
            Assert.That(Vector3.Distance(region.center, head), Is.LessThan(.25f), "Lashes must be at the head, not scaled down at the feet: " + region);

            Assert.That(lashes.sharedMesh.vertexCount, Is.EqualTo(360), "Authored corner normals avoid flat-face vertex splits");
            Assert.That(lashes.bones.All(b => body.bones.Contains(b)), Is.True, "No duplicate lash skeleton");
            var mat = lashes.sharedMaterial;
            Assert.That(mat.GetFloat("_AlphaClip"), Is.EqualTo(1));
            Assert.That(mat.IsKeywordEnabled("_ALPHATEST_ON"), Is.True);
            Assert.That(mat.GetFloat("_Surface"), Is.EqualTo(0));
            Assert.That(mat.GetFloat("_Cull"), Is.EqualTo(0));
            Assert.That(mat.renderQueue, Is.EqualTo(2450));
            var tint = mat.GetColor("_BaseColor");
            Assert.That(Mathf.Max(tint.r, tint.g, tint.b), Is.LessThan(.15f), "Lash cards must not become white");
            var path = AssetDatabase.GetAssetPath(mat.GetTexture("_BaseMap"));
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.That(importer.alphaSource, Is.EqualTo(TextureImporterAlphaSource.FromInput));
            Assert.That(importer.alphaIsTransparency && importer.mipmapEnabled && importer.mipMapsPreserveCoverage, Is.True);
            Assert.That(importer.maxTextureSize, Is.EqualTo(1024));
            Assert.That(NpcActorView.IsSkinMaterialName(mat.name), Is.False, "Weathering must not replace lash cutout");
        }
        finally { Object.DestroyImmediate(go); }
    }

    [TestCase("Marta")]
    [TestCase("Kshishtof")]
    public void LashOpacityHasRealHolesAndFaceRigClosesBothMeshes(string actor)
    {
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
        var pixels = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        var rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            go.GetComponent<Animator>().enabled = false;
            var skins = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var lashes = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "AuthoredEyelashes");
            Graphics.Blit(lashes.sharedMaterial.GetTexture("_BaseMap"), rt);
            RenderTexture.active = rt; pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); pixels.Apply();
            var alpha = pixels.GetPixels32().Select(p => p.a).ToArray();
            Assert.That(alpha.Count(a => a < 8), Is.GreaterThan(1000), "JPEG alpha=1 is not a lash mask");
            Assert.That(alpha.Count(a => a > 240), Is.GreaterThan(100), "Mask must retain the authored hairs");
            var catalog = new FaceExpressionCatalog();
            catalog.AddCustom("closed", "closed", new[] { ("eCTRLEyesClosedL", 100f), ("eCTRLEyesClosedR", 100f) });
            var rig = new FaceExpressionRig(skins, catalog);
            rig.Apply(0, 1);
            foreach (var skin in skins.Where(s => s == lashes || s.sharedMesh.blendShapeCount == 109))
                foreach (var suffix in new[] { "eCTRLEyesClosedL", "eCTRLEyesClosedR" })
                {
                    var index = Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Single(i => skin.sharedMesh.GetBlendShapeName(i).EndsWith(suffix));
                    Assert.That(skin.GetBlendShapeWeight(index), Is.EqualTo(100), skin.name + " " + suffix);
                }
            rig.Clear();
            Assert.That(Enumerable.Range(0, lashes.sharedMesh.blendShapeCount).All(i => lashes.GetBlendShapeWeight(i) == 0), Is.True);
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(pixels); Object.DestroyImmediate(go); }
    }
    [UnityTest]
    public IEnumerator RealRendererMakesLashesAndClosedEyesVisibleOnActualFrames()
    {
        var audit = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("PeopleEyeSurfaceAudit")).First(t => t != null);
        yield return (IEnumerator)audit.GetMethod("CaptureRuntime").Invoke(null, null);
        var args = Environment.GetCommandLineArgs(); var option = Array.IndexOf(args, "-eye-audit-output");
        var output = option >= 0 ? args[option + 1] : "Build/PeopleEyeReview";
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            foreach (var pose in new[] { "open", "closed" })
                Assert.That(DifferentPixels(Path.Combine(output, actor + "-lash-" + pose + "-ON.png"),
                    Path.Combine(output, actor + "-lash-" + pose + "-OFF.png")), Is.GreaterThan(100), actor + " " + pose + " lash renderer must affect actual pixels");
            Assert.That(DifferentPixels(Path.Combine(output, actor + "-lash-open-ON.png"),
                Path.Combine(output, actor + "-lash-closed-ON.png")), Is.GreaterThan(1000), actor + " native eye closure must reach the rendered frame");
        }
    }

    private static int DifferentPixels(string first, string second)
    {
        var a = new Texture2D(2, 2, TextureFormat.RGBA32, false); var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            Assert.That(a.LoadImage(File.ReadAllBytes(first)) && b.LoadImage(File.ReadAllBytes(second)), Is.True);
            var p = a.GetPixels32(); var q = b.GetPixels32(); Assert.That(p.Length, Is.EqualTo(q.Length));
            return p.Where((v, i) => Math.Max(Math.Abs(v.r - q[i].r), Math.Max(Math.Abs(v.g - q[i].g), Math.Abs(v.b - q[i].b))) > 3).Count();
        }
        finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
    }

    private static Vector3[] SkinWorld(SkinnedMeshRenderer skin)
    {
        var mesh = skin.sharedMesh; var vertices = mesh.vertices;
        var counts = mesh.GetBonesPerVertex().ToArray(); var weights = mesh.GetAllBoneWeights().ToArray();
        var bindposes = mesh.bindposes;
        var matrices = skin.bones.Select((b, i) => b.localToWorldMatrix * bindposes[i]).ToArray();
        var result = new Vector3[vertices.Length]; var cursor = 0;
        for (var v = 0; v < vertices.Length; v++) for (var w = 0; w < counts[v]; w++)
        {
            var weight = weights[cursor++];
            result[v] += matrices[weight.boneIndex].MultiplyPoint3x4(vertices[v]) * weight.weight;
        }
        return result;
    }

}
}
#endif
