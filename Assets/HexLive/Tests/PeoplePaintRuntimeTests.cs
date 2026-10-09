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
using UnityEngine.Playables;
using HexLive.UnityPresentation;
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

    [Test]
    public void LiveActorPainterSelectsBodyEvenWhenGenitalsAreFirst()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = Actor(actor);
            var view = go.AddComponent<NpcActorView>(); view.enabled = false;
            SetPrivate(view, "_bodyRoot", go.transform);
            SetPrivate(view, "_bodyBones", go.GetComponent<BodyBones>());
            SetPrivate(view, "_bodySkins", go.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            SetPrivate(view, "_actorMesh", (ActorName)Enum.Parse(typeof(ActorName), actor));
            InvokePrivate(view, "BuildSkinTintTargets");
            InvokePrivate(view, "ConstructSkinPainter");
            var painter = go.GetComponent<SkinTexturePainter>();
            Assert.That(painter, Is.Not.Null);
            Assert.That(GetPrivate(painter, "_body"), Is.SameAs(ActorBodyResolver.ResolveOrNull(go.transform)));
            Assert.That(GetPrivate(painter, "_map"), Is.Not.Null, actor);
            Assert.That(GetPrivate(painter, "_mapVertexCount"), Is.EqualTo(actor == "Marta" ? 4410 : 8957));
        }
    }

    [UnityTest]
    public IEnumerator DelayedGarmentMapPaintsBloodOnTheReadyTransition()
    {
        int tested = 0;
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = Actor(actor);
            var bones = go.GetComponent<BodyBones>();
            var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
            foreach (var row in catalog["records"].Where(r => (string)r["type"] == "wear"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
                if (!prefab.name.StartsWith(actor + "_", StringComparison.Ordinal)) continue;
                var id = (string)row["id"];
                var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
                bones.Equip(id, prefab.GetComponent<Wear>(), def.variantMaterials);
                var wear = bones.WearTransform.GetComponentsInChildren<Wear>(true).Single(w => w.name == prefab.name + "(Clone)");
                wear.SetErosion(.001f); // creates painter; below the first natural-hole threshold
                var paint = wear.GetComponent<GarmentWearPainter>();
                var renderer = wear.GetComponentInChildren<SkinnedMeshRenderer>();
                var original = renderer.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
                // The first asynchronous call left no map on the painter. On
                // this call Request returns Ready. Previously it still warned
                // Missing and cached the input as handled without painting it.
                SetPrivate(paint, "_map", null);
                wear.SetGrime(0f, new[] { "Torso", "Pelvis", "LegL", "LegR", "ArmL", "ArmR", "Head" },
                    new[] { .9f, .9f, .9f, .9f, .9f, .9f, .9f }, 7, .9f);
                Assert.That(GetPrivate(paint, "_map"), Is.Not.Null, id);
                Assert.That(((ICollection)GetPrivate(paint, "_bloodStains")).Count, Is.GreaterThan(0), id);
                float until = Time.realtimeSinceStartup + 15f;
                while (!paint.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
                Assert.That(renderer.sharedMaterials.Select((m, i) => (texture: m.GetTexture("_BaseMap"), clean: original[i]))
                    .Any(v => v.texture is RenderTexture && v.clean != null && Difference(v.texture, v.clean) > .00001f),
                    Is.True, id + " blood-only input did not change pixels");
                bones.TakeOff(id); tested++; yield return null;
            }
        }
        Assert.That(tested, Is.EqualTo(78));
    }

    [Test]
    public void AllGarmentDropsPreserveMetreScaleAndVariants()
    {
        var cache = (IDictionary)typeof(ActorWardrobe).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var variants = (IDictionary)typeof(GarmentVariants).GetField("ById", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
        int count = 0;
        foreach (var row in catalog["records"].Where(r => (string)r["type"] == "wear"))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
            var source = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
            var id = (string)row["id"];
            var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
            var saved = cache[id]; var savedDef = variants[id];
            cache[id] = new List<Wear> { prefab.GetComponent<Wear>() }; variants[id] = def;
            try
            {
                var sourceSize = Vector3.Scale(source.sharedMesh.bounds.size, source.transform.lossyScale);
                var expected = Mathf.Max(sourceSize.x, sourceSize.y, sourceSize.z);
                foreach (var hanging in new[] { false, true })
                {
                    var drop = hanging ? GarmentDropFactory.BuildHanging(id) : GarmentDropFactory.Build(id);
                    Assert.That(drop, Is.Not.Null, id); _objects.Add(drop);
                    Assert.That(ObjectFit.WorldBounds(drop, out var box), Is.True);
                    var size = Mathf.Max(box.size.x, box.size.y, box.size.z);
                    Assert.That(size, Is.LessThanOrEqualTo(expected * 1.15f), id + " grew when removed");
                    Assert.That(size, Is.GreaterThan(expected * .20f), id + " shrank when removed");
                    Assert.That(box.center.magnitude, Is.LessThan(.16f), id + " lost its drop pivot");
                    var mats = drop.GetComponentInChildren<MeshRenderer>().sharedMaterials;
                    for (int m = 0; m < def.variantMaterials.Length; m++)
                        if (def.variantMaterials[m] != null) Assert.That(mats[m], Is.SameAs(def.variantMaterials[m]), id);
                }
                count++;
            }
            finally
            {
                if (saved == null) cache.Remove(id); else cache[id] = saved;
                if (savedDef == null) variants.Remove(id); else variants[id] = savedDef;
            }
        }
        Assert.That(count, Is.EqualTo(78));
    }

    [UnityTest]
    public IEnumerator BodyBoundsFollowMovingActorsAndSurvivePoseModeChanges()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = Actor(actor);
            var view = go.AddComponent<NpcActorView>(); view.enabled = false;
            var skins = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SetPrivate(view, "_bodySkins", skins);
            var animator = go.GetComponent<Animator>(); animator.enabled = true;
            animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx")
                .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
            var graph = UnityEngine.Playables.PlayableGraph.Create("PeopleMovingBounds");
            var play = UnityEngine.Animations.AnimationClipPlayable.Create(graph, clip);
            UnityEngine.Animations.AnimationPlayableOutput.Create(graph, "run", animator).SetSourcePlayable(play);
            graph.SetTimeUpdateMode(UnityEngine.Playables.DirectorUpdateMode.Manual); graph.Play();
            try
            {
                for (int frame = 0; frame < 19; frame++)
                {
                    go.transform.SetPositionAndRotation(new Vector3(11 + frame, 2, -7 + frame), Quaternion.Euler(0, frame * 31, 0));
                    go.transform.localScale = Vector3.one * .7764706f;
                    play.SetTime(clip.length * frame / 18d); graph.Evaluate(0);
                    SetPrivate(view, "_laying", frame % 3 == 0);
                    InvokePrivate(view, "RefreshSkinBounds");
                    // Unity updates skinned culling boxes during rendering, after
                    // coroutine Update. Observe after a complete render frame.
                    yield return null;
                    yield return null;
                    foreach (var skin in skins.Where(r => r.gameObject.activeInHierarchy))
                    {
                        var mesh = new Mesh(); skin.BakeMesh(mesh, true);
                        var bounds = skin.bounds; bounds.Expand(.01f);
                        var outside = mesh.vertices.Select(skin.transform.TransformPoint).Count(p => !bounds.Contains(p));
                        Object.DestroyImmediate(mesh);
                        Assert.That(outside, Is.EqualTo(0), actor + "/" + skin.name + " frame " + frame + " bounds=" + bounds);
                    }
                }
            }
            finally { graph.Destroy(); }
        }
    }

    [Test]
    public void BothHandAnchorsUseActorMetresForAuthoredGripOffsets()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = Actor(actor); go.transform.localScale = Vector3.one * .7764706f;
            var view = go.AddComponent<NpcActorView>(); view.enabled = false;
            SetPrivate(view, "_bodyBones", go.GetComponent<BodyBones>());
            foreach (var left in new[] { false, true })
            {
                var anchor = (Transform)typeof(NpcActorView).GetMethod("HandPropAnchor", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(view, new object[] { left });
                var bone = go.GetComponent<BodyBones>().GetBone(left ? "lHand" : "rHand");
                Assert.That(Vector3.Distance(anchor.position, bone.position), Is.LessThan(.0001f));
                Assert.That(Vector3.Distance(anchor.lossyScale, go.transform.lossyScale), Is.LessThan(.0001f));
                Assert.That(Vector3.Distance(anchor.TransformPoint(Vector3.right * .1f), anchor.position),
                    Is.EqualTo(.07764706f).Within(.0001f));
            }
        }
    }

    private static void SetPrivate(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static object GetPrivate(object target, string field) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void InvokePrivate(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

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
