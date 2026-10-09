#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
// Exercise production attachment entry points with the actual released sources.
// Run graphics PlayMode with -people-prop-inventory <package>/inventory.json.
public sealed class PeoplePropAttachmentRuntimeTests
{
    const string Root = "Assets/HexLiveContent/People";
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly List<GameObject> objects = new();
    readonly List<(IDictionary cache, Dictionary<object, object> saved)> caches = new();
    object oldService, oldSubscription;
    JArray records;
    bool oldAsync;
    IDictionary configs, handles, wardrobe, variants;

    IDictionary Isolate(Type type, string name)
    {
        var cache = (IDictionary)type.GetField(name, Static).GetValue(null);
        var saved = new Dictionary<object, object>();
        foreach (DictionaryEntry e in cache) saved[e.Key] = e.Value;
        caches.Add((cache, saved)); cache.Clear(); return cache;
    }
    static object Swap(Type type, string field, object value)
    {
        var f = type.GetField(field, Static); var old = f.GetValue(null); f.SetValue(null, value); return old;
    }
    static void Set(object target, string field, object value) => target.GetType().GetField(field, Instance).SetValue(target, value);
    static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Instance).GetValue(target);
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Instance).Invoke(target, args);
    static JArray Vec(Vector3 v) => new JArray(v.x, v.y, v.z);

    [SetUp]
    public void Setup()
    {
        Assert.That(Application.isPlaying, Is.True);
        Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
        oldAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        var service = FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        oldService = Swap(typeof(ContentAssetService), "_instance", service);
        oldSubscription = Swap(typeof(ContentPrefabCache), "_registrySource", service);
        configs = Isolate(typeof(GearLibrary), "Configs");
        handles = Isolate(typeof(ContentPrefabCache), "Handles");
        wardrobe = Isolate(typeof(ActorWardrobe), "Cache");
        variants = Isolate(typeof(GarmentVariants), "ById");
        var args = System.Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(args, "-people-prop-inventory");
        Assert.That(arg, Is.GreaterThanOrEqualTo(0), "Pass the validated package inventory");
        records = (JArray)JObject.Parse(File.ReadAllText(args[arg + 1]))["records"];
        foreach (var r in records.Where(r => (string)r["type"] == "object"))
        {
            var id = (string)r["id"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)r["main"]);
            Assert.That(prefab, Is.Not.Null, id);
            handles["object/" + id] = Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>), Instance, null, new object[] { prefab, null }, null);
            configs[id] = null; // Explicit absence for resources; never access the network.
        }
        foreach (var guid in AssetDatabase.FindAssets("t:GearConfig", new[] { "Assets/HexLiveContent/RuntimeSource/Gear" }))
            GearLibrary.Register(AssetDatabase.LoadAssetAtPath<GearConfig>(AssetDatabase.GUIDToAssetPath(guid)));
        foreach (var r in JObject.Parse(File.ReadAllText(Root + "/catalog.json"))["records"].Where(r => (string)r["type"] == "wear"))
        {
            wardrobe[(string)r["id"]] = new List<Wear> { AssetDatabase.LoadAssetAtPath<GameObject>((string)r["main"]).GetComponent<Wear>() };
            variants[(string)r["id"]] = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)r["definition"]);
        }
        Directory.CreateDirectory(Root + "/Validation/Review");
    }
    [TearDown]
    public void Cleanup()
    {
        foreach (var go in objects) if (go != null) Object.DestroyImmediate(go);
        objects.Clear();
        foreach (var (cache, saved) in caches) { cache.Clear(); foreach (var e in saved) cache[e.Key] = e.Value; }
        caches.Clear();
        Swap(typeof(ContentPrefabCache), "_registrySource", oldSubscription);
        Swap(typeof(ContentAssetService), "_instance", oldService);
        ShaderUtil.allowAsyncCompilation = oldAsync;
    }
    NpcActorView Actor(string name)
    {
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + name + ".prefab"));
        objects.Add(go); go.GetComponent<Animator>().enabled = false;
        go.transform.localScale = Vector3.one * .7764706f;
        var bones = go.GetComponent<BodyBones>(); bones.Construct((ActorName)Enum.Parse(typeof(ActorName), name), 169);
        var view = go.AddComponent<NpcActorView>(); view.enabled = false;
        Set(view, "_bodyBones", bones); Set(view, "_bodyRoot", go.transform);
        foreach (var garment in name == "Marta" ? new[] { "PrimalBriefs", "PrimalTop" } : new[] { "PrimalBriefs" })
            bones.Equip(garment, AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + name + "_" + garment + ".prefab").GetComponent<Wear>(), null);
        return view;
    }
    [Test]
    public void ReleasedPortablePropsKeepGripAndBackOffsetsOnBothAnimatedBodies()
    {
        var report = new JArray();
        var portable = records.Where(r => (string)r["type"] == "object" && new[] { "tool.", "resource.", "food.", "item.", "med." }.Any(p => ((string)r["id"]).StartsWith(p, StringComparison.Ordinal))).ToArray();
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var view = Actor(actor); var go = view.gameObject;
            var bones = go.GetComponent<BodyBones>();
            foreach (var r in portable)
            {
                var id = (string)r["id"];
                foreach (var left in new[] { false, true })
                {
                    Set(view, "_leftHanded", left);
                    Call(view, "SetHandProp", id); view.SetOffhandItem(id);
                    foreach (var field in new[] { "_handProp", "_offhandProp" })
                    {
                        var prop = Get<GameObject>(view, field); Assert.That(prop, Is.Not.Null, actor + "/" + id + "/" + field);
                        var anchor = prop.transform.parent;
                        Assert.That(Vector3.Distance(anchor.lossyScale, go.transform.lossyScale), Is.LessThan(.0001f), id);
                        Assert.That(ObjectFit.WorldBounds(prop, out var b), Is.True, id);
                        Assert.That(float.IsFinite(b.size.magnitude), Is.True, id);
                        var isLeft = field == "_handProp" ? left : !left;
                        if (GearLibrary.TryGetHandPose(id, isLeft, out var pos, out var rot, out var scale))
                            Assert.That(Vector3.Distance(prop.transform.position, anchor.TransformPoint(pos)), Is.LessThan(.0001f), id + " grip");
                        else if (id.StartsWith("resource.", StringComparison.Ordinal))
                        {
                            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)r["main"]);
                            Assert.That(prop.transform.localScale, Is.EqualTo(prefab.transform.localScale), id + " native resource scale");
                            var grip = new Vector3(isLeft ? -.069f : .069f, -.063f, .007f);
                            Assert.That(Vector3.Distance(prop.transform.position, anchor.TransformPoint(grip)), Is.LessThan(.0001f), id + " resource grip");
                        }
                        ObjectFit.TryMeasuredSize(prop, id, out var measured);
                        report.Add(new JObject { ["actor"] = actor, ["id"] = id, ["slot"] = isLeft ? "left" : "right", ["boundsSize"] = Vec(b.size), ["measuredSize"] = measured, ["groundTarget"] = ObjectFit.TargetWorldSize(id), ["gripOffsetWorld"] = Vec(prop.transform.position - anchor.position) });
                    }
                    Call(view, "SetHandProp", new object[] { null }); view.SetOffhandItem(null);
                }
                if (!id.StartsWith("tool.", StringComparison.Ordinal)) continue;
                view.SetBackWeapon(id);
                var backProp = Get<GameObject>(view, "_backProp"); Assert.That(backProp, Is.Not.Null, id);
                ObjectFit.WorldBounds(backProp, out var backBounds);
                var slot = new Vector3(.015f, -.05f, -.108f);
                var back = backProp.transform.parent;
                Assert.That(Vector3.Distance(backBounds.center, back.TransformPoint(slot)), Is.LessThan(.0001f), actor + "/" + id + " back centre");
                Assert.That(Vector3.Distance(backBounds.center, back.position), Is.GreaterThan(.08f), id + " must not collapse into the spine");
                ObjectFit.TryMeasuredSize(backProp, id, out var size);
                Assert.That(size, Is.EqualTo(ObjectFit.TargetWorldSize(id)).Within(.001f), id);
                // Move/rotate the skeleton as a running clip does. The attachment
                // must retain its authored frame and length, not a world position.
                var bone = back.parent; var old = bone.localRotation;
                var local = backProp.transform.localPosition;
                bone.localRotation *= Quaternion.Euler(15, -25, 12);
                Assert.That(backProp.transform.localPosition, Is.EqualTo(local));
                Assert.That(Vector3.Distance(backProp.transform.position, back.TransformPoint(local)), Is.LessThan(.0001f));
                ObjectFit.TryMeasuredSize(backProp, id, out var movingSize);
                Assert.That(movingSize, Is.EqualTo(size).Within(.001f), id);
                bone.localRotation = old;
                report.Add(new JObject { ["actor"] = actor, ["id"] = id, ["slot"] = "back", ["centreOffsetWorld"] = Vec(backBounds.center - back.position), ["measuredSize"] = size });
                view.SetBackWeapon(null);
            }
            Object.DestroyImmediate(go);
        }
        File.WriteAllText(Root + "/Validation/prop-attachment-audit.json", new JObject { ["portableIds"] = portable.Length, ["measurements"] = report }.ToString());
    }
    [Test]
    public void RenderProductionBackAndHandAttachmentsWithEveryPack()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        foreach (var pack in new[] { "None", "PrimalGathererPack", "PrimalHunterPack", "PrimalTrailPack" })
        {
            var view = Actor(actor); var go = view.gameObject;
            var bones = go.GetComponent<BodyBones>();
            if (pack != "None") bones.Equip(pack, AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + actor + "_" + pack + ".prefab").GetComponent<Wear>(), null);
            var animator = go.GetComponent<Animator>(); animator.enabled = true;
            animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind();
            var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx").OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
            var graph = PlayableGraph.Create("Prop attachment review");
            var play = AnimationClipPlayable.Create(graph, clip);
            AnimationPlayableOutput.Create(graph, "run", animator).SetSourcePlayable(play);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
            try
            {
                play.SetTime(clip.length * .23); graph.Evaluate(0);
                view.SetBackWeapon("tool.axe_stone");
                Call(view, "SetHandProp", "tool.spear"); view.SetOffhandItem("resource.stick");
                Render(go, actor + "-" + pack + "-fixed", new Vector3(2, 1.9f, -3));
                if (pack == "None")
                {
                    Render(go, actor + "-hands", new Vector3(-2, 1.9f, 3));
                    var prop = Get<GameObject>(view, "_backProp");
                    var anchor = prop.transform.parent;
                    ObjectFit.WorldBounds(prop, out var b);
                    // Reproduce the original rendered centre at the raw .01 bone.
                    prop.transform.position += anchor.parent.TransformPoint(new Vector3(.015f, -.05f, -.108f)) - b.center;
                    Render(go, actor + "-back-before", new Vector3(2, 1.9f, -3));
                }
            }
            finally { graph.Destroy(); Object.DestroyImmediate(go); }
        }
    }
    static void Render(GameObject go, string name, Vector3 eye)
    {
        var preview = new PreviewRenderUtility();
        var meshes = new List<Mesh>();
        try
        {
            var draws = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials)>();
            foreach (var skin in go.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                var mesh = new Mesh(); skin.BakeMesh(mesh, true); meshes.Add(mesh);
                draws.Add((mesh, skin.transform.localToWorldMatrix, skin.sharedMaterials));
            }
            foreach (var f in go.GetComponentsInChildren<MeshFilter>(false))
            {
                var r = f.GetComponent<MeshRenderer>(); if (r != null) draws.Add((f.sharedMesh, f.transform.localToWorldMatrix, r.sharedMaterials));
            }
            var camera = preview.camera; camera.orthographic = true; camera.orthographicSize = 1.0f;
            camera.transform.position = eye; camera.transform.LookAt(new Vector3(0, .85f, 0));
            camera.nearClipPlane = .01f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f,.14f,.16f);
            preview.lights[0].intensity = 1.2f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 180, 0);
            preview.lights[1].intensity = .7f; preview.lights[1].transform.rotation = Quaternion.Euler(-15, -30, 0);
            for (int pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0, 0, 768, 900));
                foreach (var d in draws) for (int m = 0; m < d.mesh.subMeshCount; m++) preview.DrawMesh(d.mesh, d.matrix, d.materials[m], m);
                camera.Render(); var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(Root + "/Validation/Review/attachment-" + name + ".png", image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
        }
        finally { foreach (var m in meshes) Object.DestroyImmediate(m); preview.Cleanup(); }
    }
    [Test]
    public void EquippingAndRemovingBagReseatsExistingWeaponOutsideItsMesh()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        foreach (var pack in new[] { "PrimalGathererPack", "PrimalHunterPack", "PrimalTrailPack" })
        {
            var view = Actor(actor); var bones = view.GetComponent<BodyBones>();
            view.SetBackWeapon("tool.axe_stone");
            var old = Get<GameObject>(view, "_backProp");
            var barePosition = old.transform.localPosition;
            bones.Equip(pack, AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + actor + "_" + pack + ".prefab").GetComponent<Wear>(), null);
            view.SetBackWeapon("tool.axe_stone");
            var prop = Get<GameObject>(view, "_backProp");
            Assert.That(prop, Is.Not.SameAs(old), "An arriving backpack must refresh the existing mount");
            var frame = prop.transform.parent;
            var rear = float.PositiveInfinity;
            var mesh = new Mesh();
            foreach (var skin in bones.TorsoBag.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.BakeMesh(mesh, true);
                foreach (var v in mesh.vertices) rear = Mathf.Min(rear, frame.InverseTransformPoint(skin.transform.TransformPoint(v)).z);
            }
            Object.DestroyImmediate(mesh);
            Assert.That(rear, Is.LessThan(-.1f), actor + "/" + pack + " bag must retain its imported scale");
            var front = float.NegativeInfinity;
            foreach (var f in prop.GetComponentsInChildren<MeshFilter>())
                foreach (var v in f.sharedMesh.vertices) front = Mathf.Max(front, frame.InverseTransformPoint(f.transform.TransformPoint(v)).z);
            Assert.That(front, Is.LessThanOrEqualTo(rear - .014f), actor + "/" + pack);
            var oldBagObject = bones.TorsoBag.gameObject;
            bones.TakeOff(pack); Object.DestroyImmediate(oldBagObject);
            view.SetBackWeapon("tool.axe_stone");
            Assert.That(Vector3.Distance(Get<GameObject>(view, "_backProp").transform.localPosition, barePosition), Is.LessThan(.0001f));
            Object.DestroyImmediate(view.gameObject);
        }
    }
    [Test]
    public void WeaponInEitherHandIsNotAlsoMountedOnBack()
    {
        var view = Actor("Kshishtof"); const string id = "tool.axe_stone";
        view.SetOffhandItem(id); view.SetBackWeapon(id);
        Assert.That(Get<GameObject>(view, "_backProp"), Is.Null);
        view.SetOffhandItem(null); view.SetBackWeapon(id);
        Assert.That(Get<GameObject>(view, "_backProp"), Is.Not.Null);
        Call(view, "SetHandProp", id); view.SetBackWeapon(id);
        Assert.That(Get<GameObject>(view, "_backProp"), Is.Null);
    }
    [Test]
    public void LateGarmentAndBackConfigRetryWithoutRespawningActor()
    {
        var view = Actor("Marta"); const string garment = "clothing.top_primal";
        var saved = wardrobe[garment]; wardrobe[garment] = new List<Wear>();
        Call(view, "SetHandGarment", garment);
        Assert.That(Get<GameObject>(view, "_handGarment"), Is.Null);
        wardrobe[garment] = saved;
        Call(view, "SetHandGarment", garment);
        Assert.That(Get<GameObject>(view, "_handGarment"), Is.Not.Null);
        const string id = "tool.axe_stone";
        var config = configs[id]; Assert.That(config, Is.Not.Null); configs[id] = null;
        view.SetBackWeapon(id); Assert.That(Get<GameObject>(view, "_backProp"), Is.Null);
        configs[id] = config; view.SetBackWeapon(id); Assert.That(Get<GameObject>(view, "_backProp"), Is.Not.Null);
    }
}
}
#endif
