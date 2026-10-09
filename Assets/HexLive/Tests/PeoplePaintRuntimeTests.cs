#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using Newtonsoft.Json.Linq;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
// Local preparation fixture: real imported prefabs/maps and runtime painters,
// loaded directly without remote wardrobe dependencies or bundle builds. Run in graphics PlayMode.
public sealed class PeoplePaintRuntimeTests
{
    private const string Root = "Assets/HexLiveContent/People";
    private readonly List<GameObject> _objects = new();
    private bool _oldAsyncShaders;
    private readonly List<(IDictionary cache, Dictionary<object, object> saved)> _caches = new();

    [SetUp]
    public void LoadPreparedMaps()
    {
        Assert.That(Application.isPlaying, Is.True, "Run this fixture in PlayMode");
        _oldAsyncShaders = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
        foreach (var type in new[] { typeof(PaintPointMap), typeof(SkinPositionMapSet) })
        {
            var cache = (IDictionary)type.GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var saved = new Dictionary<object, object>();
            foreach (DictionaryEntry entry in cache) saved.Add(entry.Key, entry.Value);
            _caches.Add((cache, saved));
            cache.Clear();
            foreach (var guid in AssetDatabase.FindAssets("t:" + type.Name, new[] { Root + "/PaintMaps" }))
            {
                var asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), type);
                cache[asset.name] = asset;
            }
        }
    }

    [TearDown]
    public void Restore()
    {
        ShaderUtil.allowAsyncCompilation = _oldAsyncShaders;
        foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
        _objects.Clear();
        foreach (var (cache, saved) in _caches)
        {
            cache.Clear();
            foreach (var entry in saved) cache.Add(entry.Key, entry.Value);
        }
        _caches.Clear();
    }

    private GameObject Actor(string actor)
    {
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
        _objects.Add(go);
        go.GetComponent<Animator>().enabled = false;
        go.GetComponent<BodyBones>().Construct((ActorName)Enum.Parse(typeof(ActorName), actor), 169);
        return go;
    }

    [Test]
    public void SelectedSkinTintSurvivesWeatheringUpdates()
    {
        var go = Actor("Marta");
        var view = go.AddComponent<NpcActorView>(); view.enabled = false;
        typeof(NpcActorView).GetField("_bodySkins", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(view, go.GetComponentsInChildren<SkinnedMeshRenderer>());
        var apply = typeof(NpcActorView).GetMethod("ApplySkinSet", BindingFlags.NonPublic | BindingFlags.Instance);
        apply.Invoke(view, new object[] { "Jana", false });
        view.SetSkinWeathering(0f, 0f);
        var expected = go.GetComponent<PeopleAppearance>().SkinMaterials("Jana").Values.First().GetColor("_BaseColor");
        Assert.That(Vector4.Distance(view.SkinTint, expected), Is.LessThan(.001f));
        apply.Invoke(view, new object[] { "Marta", false });
        view.SetSkinWeathering(0f, 0f);
        Assert.That(Vector4.Distance(view.SkinTint, Color.white), Is.LessThan(.001f));
    }

    [UnityTest]
    public IEnumerator SkinToneAndBloodReachNewAtlasWithoutPaintingEyesOrTeeth()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = Actor(actor);
            Assert.That(ActorBodyResolver.TryResolve(go, out var body, out var error), Is.True, error);
            var cleanSkin = body.sharedMaterials[0].GetTexture("_BaseMap");
            var eyes = body.sharedMaterials[1].GetTexture("_BaseMap");
            var teeth = body.sharedMaterials[2].GetTexture("_BaseMap");
            var paint = go.AddComponent<SkinTexturePainter>();
            paint.Construct(body, new[] { 0 }, go.GetComponent<BodyBones>(), go.transform, 169, actor);
            var wounds = new List<(string zone, int seed, float heal)>();
            var blood = new List<(string zone, float damage01)> { ("Torso", .7f) };
            paint.SetSkinTone(new Color(.6f, .5f, .45f));
            paint.Sync(wounds, new HashSet<string>(), zoneDamage: blood);
            float until = Time.realtimeSinceStartup + 20f;
            while (!paint.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(paint.PresentationReady, Is.True, actor);
            Assert.That(paint.SlotHasAlbedoPaint(0), Is.True, actor + " skin RT missing");
            var dark = Mean(body.sharedMaterials[0].GetTexture("_BaseMap"));
            Assert.That(body.sharedMaterials[1].GetTexture("_BaseMap"), Is.SameAs(eyes));
            Assert.That(body.sharedMaterials[2].GetTexture("_BaseMap"), Is.SameAs(teeth));
            Assert.That(paint.SlotHasAlbedoPaint(1) || paint.SlotHasAlbedoPaint(2), Is.False);
            paint.SetSkinTone(Color.white);
            paint.Sync(wounds, new HashSet<string>(), zoneDamage: blood);
            until = Time.realtimeSinceStartup + 20f;
            while (!paint.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
            var light = Mean(body.sharedMaterials[0].GetTexture("_BaseMap"));
            Assert.That(Difference(body.sharedMaterials[0].GetTexture("_BaseMap"), cleanSkin), Is.GreaterThan(.0001f), actor + " blood did not change atlas pixels");
            Assert.That(light, Is.GreaterThan(dark * 1.15f), actor + " tan did not change atlas pixels");
            Object.DestroyImmediate(go);
        }
    }

    [UnityTest]
    public IEnumerator EveryGarmentVariantPaintsDirtAndTears()
    {
        int tested = 0;
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = Actor(actor);
            var bones = go.GetComponent<BodyBones>();
            int originalBoneCount = bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length;
            var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
            foreach (var row in catalog["records"].Where(r => (string)r["type"] == "wear"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
                if (!prefab.name.StartsWith(actor + "_", StringComparison.Ordinal)) continue;
                string id = (string)row["id"];
                var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
                bones.Equip(id, prefab.GetComponent<Wear>(), definition.variantMaterials);
                var wear = bones.WearTransform.GetComponentsInChildren<Wear>(true).Single(w => w.name == prefab.name + "(Clone)");
                var renderer = wear.GetComponentInChildren<SkinnedMeshRenderer>();
                var originalTextures = renderer.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
                var dryColour = renderer.sharedMaterial.GetColor("_BaseColor");
                float drySmoothness = renderer.sharedMaterial.GetFloat("_Smoothness");
                wear.SetErosion(.3f);
                wear.SetGrime(.8f, new[] { "Torso", "Pelvis", "LegL", "ArmL" }, new[] { .7f, .7f, .7f, .7f }, 4, .6f);
                wear.SetWetness(.5f);
                var paint = wear.GetComponent<GarmentWearPainter>();
                if (paint == null) paint = wear.GetComponentInChildren<GarmentWearPainter>();
                Assert.That(paint, Is.Not.Null, prefab.name);
                float until = Time.realtimeSinceStartup + 15f;
                while (!paint.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
                Assert.That(paint.PresentationReady, Is.True, prefab.name);
                var mats = renderer.sharedMaterials;
                Assert.That(mats.Select((m, i) => (texture: m.GetTexture("_BaseMap"), original: originalTextures[i]))
                    .Any(p => p.texture is RenderTexture && p.original != null && Difference(p.texture, p.original) > .001f),
                    Is.True, id + " dirt did not change atlas pixels");
                var masks = mats.Where(m => m.HasProperty("_TearMaskTex")).Select(m => m.GetTexture("_TearMaskTex")).OfType<RenderTexture>().ToArray();
                Assert.That(masks, Is.Not.Empty, prefab.name + " tear RT");
                var originalMask = Resources.Load<Texture2D>("HexLive/Decals/tear_mask");
                Assert.That(originalMask, Is.Not.Null);
                Assert.That(masks.Any(mask => HasAdditionalHole(mask, originalMask)), Is.True, id + " tear mask has no new holes");
                wear.SetWetness(0);
                var properties = new MaterialPropertyBlock(); renderer.GetPropertyBlock(properties, 0);
                Assert.That(properties.GetFloat("_Smoothness"), Is.EqualTo(drySmoothness).Within(.0001f), id);
                Assert.That(Vector4.Distance(properties.GetColor("_BaseColor"), dryColour), Is.LessThan(.0001f), id);
                tested++;
                bones.TakeOff(id);
                yield return null;
                Assert.That(bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(originalBoneCount), id + " left garment bones after removal");
            }
            Object.DestroyImmediate(go);
        }
        Assert.That(tested, Is.EqualTo(78), "All catalog variants must be exercised");
    }

    private static Color[] Pixels(Texture texture)
    {
        Assert.That(texture, Is.Not.Null);
        var old = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(128, 128, 0, RenderTextureFormat.ARGB32);
        var image = new Texture2D(128, 128, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(texture, rt); RenderTexture.active = rt;
            image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); image.Apply();
            return image.GetPixels();
        }
        finally { RenderTexture.active = old; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(image); }
    }
    private static float Mean(Texture texture) => Pixels(texture).Average(c => (c.r + c.g + c.b) / 3f);
    private static float Difference(Texture left, Texture right)
    {
        var a = Pixels(left); var b = Pixels(right);
        return a.Select((c, i) => Mathf.Abs(c.r - b[i].r) + Mathf.Abs(c.g - b[i].g) + Mathf.Abs(c.b - b[i].b)).Average();
    }
    private static bool HasAdditionalHole(Texture texture, Texture original)
    {
        var pixels = Pixels(texture); var baseline = Pixels(original);
        return pixels.Where((c, i) => c.r < baseline[i].r - .05f).Count() >= 2;
    }
}
}
#endif
