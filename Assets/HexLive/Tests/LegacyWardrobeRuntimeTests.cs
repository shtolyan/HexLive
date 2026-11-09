#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
// §169.10. Offline graphics PlayMode acceptance of the STAGED collection.
// Loading imported fixture assets does not opt the collection into the live
// catalog, contact the registry, generate content, or publish anything.
public sealed class LegacyWardrobeRuntimeTests
{
    const string Root = "Assets/HexLiveContent/People";
    const string Staged = Root + "/Wardrobe";
    const string NativeDefinitions = "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";
    const string Output = "Build/Diagnostics/bug-wave2-actions/legacy-runtime";
    const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags HiddenInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    readonly List<(IDictionary cache, Hashtable previous)> _caches = new();
    readonly List<GameObject> _objects = new();
    readonly List<(FieldInfo field, object previous)> _fields = new();
    JToken[] _live, _legacy, _combined;
    Dictionary<string, GarmentDefinition> _definitions;
    Dictionary<string, Wear> _prefabs;
    bool _async;

    static JToken[] Records(string path) => ((JArray)JObject.Parse(File.ReadAllText(path))["records"])
        .Where(r => (string)r["type"] == "wear").ToArray();
    // Unity's coroutine parameterization supports ValueSource, not TestCaseSource.
    public static IEnumerable<string> GeometryCases => Records(Staged + "/catalog.json")
        .Where(r => (bool?)r["metadata"]?["legacyWardrobe"] == true)
        .Select(r => (string)r["main"]).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)
        .SelectMany(path => new[] { path + "|Marta", path + "|Kshishtof" });

    IDictionary Isolate(Type type, string name)
    {
        var field = type.GetField(name, HiddenStatic); Assert.That(field, Is.Not.Null, type.Name + "." + name);
        var cache = (IDictionary)field.GetValue(null); var saved = new Hashtable();
        foreach (DictionaryEntry entry in cache) saved.Add(entry.Key, entry.Value);
        _caches.Add((cache, saved)); cache.Clear(); return cache;
    }
    void SaveField(Type type, string name)
    {
        var field = type.GetField(name, HiddenStatic); Assert.That(field, Is.Not.Null, name);
        _fields.Add((field, field.GetValue(null)));
    }
    [SetUp] public void InjectOnlyImportedAssets()
    {
        Assert.That(Application.isPlaying, Is.True, "Run this fixture in graphics PlayMode");
        Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
        _async = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        _live = Records(Root + "/catalog.json"); _legacy = Records(Staged + "/catalog.json");
        Assert.That(_legacy, Is.Not.Empty, "Prepare the full staged wardrobe first; do not silently skip missing content");
        Assert.That(_legacy.All(r => (bool?)r["metadata"]?["legacyWardrobe"] == true), Is.True);
        Assert.That(_live.All(r => (bool?)r["metadata"]?["legacyWardrobe"] != true), Is.True,
            "Preparation must not add the staged collection to the current live wardrobe");
        _combined = _live.Concat(_legacy).ToArray();
        Assert.That(_combined.Select(r => (string)r["id"]).Distinct().Count(), Is.EqualTo(_combined.Length));
        _definitions = _combined.ToDictionary(r => (string)r["id"], r =>
            AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)r["definition"]), StringComparer.Ordinal);
        _prefabs = _combined.ToDictionary(r => (string)r["id"], r =>
            AssetDatabase.LoadAssetAtPath<GameObject>((string)r["main"])?.GetComponent<Wear>(), StringComparer.Ordinal);
        foreach (var row in _combined)
        {
            var id = (string)row["id"]; Assert.That(_definitions[id], Is.Not.Null, id + " definition");
            Assert.That(_prefabs[id], Is.Not.Null, id + " prefab");
        }
        SaveField(typeof(GarmentLibrary), "_active"); SaveField(typeof(GarmentLibrary), "_spawnable"); SaveField(typeof(GarmentLibrary), "_byId");
        GarmentLibrary.Override(_definitions.Values.Select(d => d.ToParams()));
        var cache = Isolate(typeof(ActorWardrobe), "Cache"); var variants = Isolate(typeof(GarmentVariants), "ById");
        foreach (var row in _combined)
        {
            var id = (string)row["id"]; cache[id] = new List<Wear> { _prefabs[id] }; variants[id] = _definitions[id];
        }
        var maps = Isolate(typeof(PaintPointMap), "Cache");
        foreach (var path in AssetDatabase.FindAssets("t:PaintPointMap", new[] { Root + "/PaintMaps" }).Select(AssetDatabase.GUIDToAssetPath))
        {
            var map = AssetDatabase.LoadAssetAtPath<PaintPointMap>(path);
            Assert.That(map.name, Is.EqualTo(Path.GetFileNameWithoutExtension(path)), path + " runtime resource key");
            maps[map.name] = map;
        }
        Directory.CreateDirectory(Output);
    }
    [TearDown] public void RestoreGlobalCaches()
    {
        foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
        _objects.Clear();
        foreach (var (cache, previous) in _caches)
        { cache.Clear(); foreach (DictionaryEntry entry in previous) cache.Add(entry.Key, entry.Value); }
        _caches.Clear();
        foreach (var (field, previous) in _fields) field.SetValue(null, previous);
        _fields.Clear(); ShaderUtil.allowAsyncCompilation = _async;
    }

    GameObject Actor(ActorName actor)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab");
        Assert.That(prefab, Is.Not.Null);
        var go = Object.Instantiate(prefab); _objects.Add(go);
        go.GetComponent<Animator>().enabled = false;
        go.GetComponent<BodyBones>().Construct(actor, 649);
        var view = go.GetComponent<NpcActorView>() ?? go.AddComponent<NpcActorView>(); view.enabled = false;
        Set(view, "_bodyBones", go.GetComponent<BodyBones>()); Set(view, "_actorMesh", actor);
        Set(view, "_bodyRoot", go.transform);
        Set(view, "_bodySkins", go.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        return go;
    }
    static void Set(object instance, string name, object value) => instance.GetType().GetField(name, HiddenInstance).SetValue(instance, value);
    static void Call(object instance, string name) => instance.GetType().GetMethod(name, HiddenInstance).Invoke(instance, null);
    static Mesh FittedMesh(Wear wear, ActorName actor)
    {
        var configs = new SerializedObject(wear).FindProperty("configs");
        for (int i = 0; i < configs.arraySize; i++)
        {
            var config = configs.GetArrayElementAtIndex(i);
            if (config.FindPropertyRelative("actorName").intValue == (int)actor)
                return config.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
        }
        Assert.Fail(wear.name + ": missing canonical body fit " + actor); return null;
    }
    static PaintPointMap ImportedMap(Mesh mesh)
    {
        string key = "garment_" + mesh.name + "_" + mesh.vertexCount;
        var cache = (IDictionary)typeof(PaintPointMap).GetField("Cache", HiddenStatic).GetValue(null);
        Assert.That(cache.Contains(key), Is.True, "Missing imported map " + key + "; never start a remote fallback in an offline test");
        var map = PaintPointMap.Load(key, mesh.vertexCount);
        Assert.That(map, Is.Not.Null, key); Assert.That(map.VertexCount, Is.EqualTo(mesh.vertexCount), key);
        return map;
    }
    static AnimationClip Clip(string file)
    {
        var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/" + file).OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
        Assert.That(clips, Is.Not.Empty, file + " must be a real imported animation");
        return clips[0];
    }
    static Vector3[] Capture(SkinnedMeshRenderer skin)
    {
        var mesh = new Mesh();
        try { skin.BakeMesh(mesh, true); return mesh.vertices.Select(skin.transform.TransformPoint).ToArray(); }
        finally { Object.DestroyImmediate(mesh); }
    }
    static Bounds BoundsOf(Vector3[] vertices)
    {
        Assert.That(vertices, Is.Not.Empty); var bounds = new Bounds(vertices[0], Vector3.zero);
        foreach (var p in vertices)
        {
            if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) Assert.Fail("Nonfinite skinned vertex");
            bounds.Encapsulate(p);
        }
        return bounds;
    }
    static JArray Vec(Vector3 p) => new JArray(p.x, p.y, p.z);
    static JObject Box(Bounds b) => new JObject { ["center"] = Vec(b.center), ["size"] = Vec(b.size) };
    static void FiniteAndNearBody(SkinnedMeshRenderer garment, Vector3[] points, SkinnedMeshRenderer body, float scale, string label)
    {
        Assert.That(garment.enabled && garment.gameObject.activeInHierarchy, Is.True, label + " visible garment");
        var bounds = BoundsOf(points); var bodyBounds = BoundsOf(Capture(body));
        Assert.That(bounds.size.magnitude, Is.InRange(.001f * scale, 4f * scale), label + " world size");
        Assert.That(Vector3.Distance(bounds.center, bodyBounds.ClosestPoint(bounds.center)), Is.LessThan(.75f * scale), label + " detached from body");
        var culling = garment.bounds; culling.Expand(.01f * scale);
        Assert.That(points.Count(p => !culling.Contains(p)), Is.Zero, label + " skinned vertices outside actual culling bounds");
    }

    [UnityTest]
    public IEnumerator EveryUniqueLegacyPrototypeSyncsBothCanonicalBodiesAndFollowsRealPoses(
        [ValueSource(nameof(GeometryCases))] string key)
    {
        var parts = key.Split('|'); var path = parts[0]; var actor = (ActorName)Enum.Parse(typeof(ActorName), parts[1]);
        var row = _legacy.Where(r => (string)r["main"] == path).OrderBy(r => (string)r["id"], StringComparer.Ordinal).First();
        string id = (string)row["id"], equipKey = id + "#0";
        var prefab = _prefabs[id]; Assert.That(prefab.SupportsActor(actor), Is.True, key);
        var expected = FittedMesh(prefab, actor); Assert.That(expected, Is.Not.Null);
        var go = Actor(actor); var bones = go.GetComponent<BodyBones>(); var view = go.GetComponent<NpcActorView>();
        int originalBones = bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length;
        var originalTransforms = new HashSet<Transform>(go.GetComponentsInChildren<Transform>(true));
        view.SyncWorn(new[] { id }); var wear = bones.GetWorn(equipKey); Assert.That(wear, Is.Not.Null, key);
        var skin = wear.GetComponentInChildren<SkinnedMeshRenderer>(true); Assert.That(skin, Is.Not.Null);
        Assert.That(skin.sharedMesh, Is.SameAs(expected), key + " canonical actor selected the wrong body mesh");
        Assert.That(wear.DefinitionId, Is.EqualTo(id), "Prepared fitting must retain the simulation identity");
        int equippedBones = bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length;
        for (int i = 0; i < 3; i++) view.SyncWorn(new[] { id });
        Assert.That(bones.GetWorn(equipKey), Is.SameAs(wear), key + " identical snapshot rebuilt clothing");
        Assert.That(bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(equippedBones));
        var body = ActorBodyResolver.ResolveOrNull(go.transform); Assert.That(body, Is.Not.Null);
        var animator = go.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
        animator.enabled = true; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind();
        var poses = new[] { ("standing", Clip("Male@Idle.fbx"), .4f, false),
            ("run_early", Clip("Male@Run.fbx"), .1f, false), ("run_mid", Clip("Male@Run.fbx"), .45f, false),
            ("run_late", Clip("Male@Run.fbx"), .8f, false), ("sleep", Clip("Sleep.fbx"), .5f, true),
            ("belly", Clip("LayingBelly.anim"), .5f, true) };
        var audit = new JArray(); var graph = PlayableGraph.Create("LegacyWardrobeActualRuntime"); Vector3[] standingBody = null;
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "real humanoid clips", animator);
            AnimationClipPlayable previous = default;
            foreach (var (pose, clip, time, laying) in poses)
            {
                if (previous.IsValid()) graph.DestroyPlayable(previous);
                var play = AnimationClipPlayable.Create(graph, clip); previous = play; output.SetSourcePlayable(play);
                graph.Play(); play.SetTime(clip.length * time); graph.Evaluate(0);
                bones.SetHeelPoseSuppressed(laying); Set(view, "_laying", laying);
                // Let the real heel transition finish. Re-evaluate the same manual
                // clip, so geometry comparison never conflates motion with movement.
                float until = Time.realtimeSinceStartup + 1f;
                while (Mathf.Abs(bones.HeelPoseWeight - (laying ? 0 : 1)) > .001f && Time.realtimeSinceStartup < until)
                { graph.Evaluate(0); yield return null; }
                Assert.That(bones.HeelPoseWeight, Is.EqualTo(laying ? 0 : 1).Within(.001f));
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); go.transform.localScale = Vector3.one * .7764706f;
                graph.Evaluate(0); Call(bones, "LateUpdate"); Call(view, "RefreshSkinBounds");
                yield return null; yield return null;
                var before = Capture(skin);
                var local = before.Select(go.transform.InverseTransformPoint).ToArray();
                var posedBody = Capture(body).Select(go.transform.InverseTransformPoint).ToArray();
                if (pose == "standing") standingBody = posedBody;
                float bodyPoseChange = 0;
                if (pose.StartsWith("run_", StringComparison.Ordinal))
                {
                    Assert.That(posedBody.Length, Is.EqualTo(standingBody.Length));
                    for (int i = 0; i < posedBody.Length; i++) bodyPoseChange = Mathf.Max(bodyPoseChange, Vector3.Distance(posedBody[i], standingBody[i]));
                    Assert.That(bodyPoseChange, Is.GreaterThan(.01f), key + "/" + pose + " animation graph did not pose the real body");
                }
                FiniteAndNearBody(skin, before, body, .7764706f, key + "/" + pose);
                go.transform.SetPositionAndRotation(new Vector3(13.25f, 2.1f, -8.75f), Quaternion.Euler(0, 137f, 0));
                Call(view, "RefreshSkinBounds"); yield return null; yield return null;
                var moved = Capture(skin);
                Assert.That(moved.Length, Is.EqualTo(before.Length));
                float followError = 0;
                for (int i = 0; i < moved.Length; i++) followError = Mathf.Max(followError, Vector3.Distance(moved[i], go.transform.TransformPoint(local[i])));
                Assert.That(followError, Is.LessThan(.0003f), key + "/" + pose + " garment failed translation/yaw equivariance");
                FiniteAndNearBody(skin, moved, body, .7764706f, key + "/" + pose + "/moved");
                Assert.That(bones.GetWorn(equipKey), Is.SameAs(wear));
                audit.Add(new JObject { ["pose"] = pose, ["clip"] = AssetDatabase.GetAssetPath(clip), ["clipTime"] = clip.length * time,
                    ["heelPoseWeight"] = bones.HeelPoseWeight, ["boundsBefore"] = Box(BoundsOf(before)),
                    ["boundsMoved"] = Box(BoundsOf(moved)), ["translationYawMaxError"] = followError,
                    ["bodyPoseChangeFromStanding"] = bodyPoseChange });
            }
        }
        finally { if (graph.IsValid()) graph.Destroy(); }
        animator.enabled = false;
        var attached = go.GetComponentsInChildren<Transform>(true).Where(t => !originalTransforms.Contains(t)).ToArray();
        view.SyncWorn(Array.Empty<string>()); Assert.That(bones.GetWorn(equipKey), Is.Null);
        yield return null; yield return null; // BodyBones.TakeOff uses runtime Destroy, not DestroyImmediate.
        Assert.That(bones.SkeletonRoot.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(originalBones), key + " leaked garment skeleton");
        Assert.That(attached.All(t => t == null), Is.True, key + " left detached garment objects/bones");
        File.WriteAllText(Output + "/" + Path.GetFileNameWithoutExtension(path) + "_" + actor + ".json", new JObject {
            ["requestedId"] = id, ["main"] = path, ["actor"] = actor.ToString(), ["mesh"] = AssetDatabase.GetAssetPath(expected),
            ["vertices"] = expected.vertexCount, ["poses"] = audit, ["repeatSyncSameInstance"] = true,
            ["takeOffRestoredBoneCount"] = true, ["clippingVisuallyProven"] = false, ["remoteRequests"] = false }.ToString());
    }

    [Test]
    public void StagedVariantsCoverRetainedDefaultsAndPreserveCanonicalStatsAndOwnedDependencies()
    {
        var native = AssetDatabase.FindAssets("t:GarmentDefinition", new[] { NativeDefinitions })
            .Select(g => AssetDatabase.LoadAssetAtPath<GarmentDefinition>(AssetDatabase.GUIDToAssetPath(g)))
            .ToDictionary(d => d.id, StringComparer.Ordinal);
        var defaults = GarmentLibrary.Defaults.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var liveIds = new HashSet<string>(_live.Select(r => (string)r["id"]), StringComparer.Ordinal);
        var expected = new HashSet<string>(defaults.Keys.Where(id => !liveIds.Contains(id)), StringComparer.Ordinal);
        var actual = new HashSet<string>(_legacy.Select(r => (string)r["id"]), StringComparer.Ordinal);
        Assert.That(actual, Is.EquivalentTo(expected), "Cover every retained default, including retired save IDs, without activating source-only orphans");
        var seen = new HashSet<string>(); var report = new JArray();
        foreach (var row in _legacy)
        {
            string id = (string)row["id"], path = (string)row["main"];
            Assert.That(native.TryGetValue(id, out var source), Is.True, id + " canonical native definition");
            var target = _definitions[id]; var a = source.ToParams(); var b = target.ToParams(); var baseline = defaults[id];
            {
                Assert.That(b.Id, Is.EqualTo(a.Id), id); Assert.That(b.DisplayName, Is.EqualTo(a.DisplayName), id);
                Assert.That(b.PrototypeId, Is.EqualTo(a.PrototypeId), id); Assert.That(b.Warmth, Is.EqualTo(a.Warmth), id);
                Assert.That(b.Armor, Is.EqualTo(a.Armor), id); Assert.That(b.ThermalDelta, Is.EqualTo(a.ThermalDelta), id);
                Assert.That(b.Capacity, Is.EqualTo(a.Capacity), id); Assert.That(b.DressDurationTicks, Is.EqualTo(a.DressDurationTicks), id);
                Assert.That(b.Covers, Is.EqualTo(a.Covers), id); Assert.That(target.category, Is.EqualTo(source.category), id);
                Assert.That(b.Retired, Is.EqualTo(a.Retired || baseline.Retired), id + " retirement must not revive saved definitions");
                Assert.That(b.Sex, Is.EqualTo(GarmentSex.Any), id + " both prepared body fits");
                Assert.That(b.AlternateFitId, Is.Empty, id + " one item with two fits");
                Assert.That(target.layer.ToString(), Is.EqualTo(_prefabs[id].Layer.ToString()), id + " actual authored visual layer");
            }
            Assert.That(path, Does.StartWith(Staged + "/Prefabs/"));
            Assert.That((string)row["definition"], Does.StartWith(Staged + "/Definitions/"));
            var metadata = row["metadata"]; var sim = metadata["simulation"];
            Assert.That((string)metadata["artId"], Is.EqualTo(target.ArtId), id);
            Assert.That((string)metadata["sex"], Is.EqualTo("Any"), id);
            Assert.That(metadata["slots"].Values<string>(), Is.EqualTo(_prefabs[id].Slots.Select(s => s.ToString())), id);
            Assert.That((float)sim["warmth"], Is.EqualTo(b.Warmth), id); Assert.That((float)sim["armor"], Is.EqualTo(b.Armor), id);
            Assert.That((float)sim["thermalDelta"], Is.EqualTo(b.ThermalDelta), id);
            Assert.That((string)sim["displayName"], Is.EqualTo(b.DisplayName), id);
            Assert.That((string)sim["prototypeId"], Is.EqualTo(b.PrototypeId), id);
            Assert.That((string)sim["sex"], Is.EqualTo(b.Sex.ToString()), id);
            Assert.That((int)sim["capacity"], Is.EqualTo(b.Capacity), id);
            Assert.That((int)sim["dressDurationTicks"], Is.EqualTo(b.DressDurationTicks), id);
            Assert.That((bool)sim["retired"], Is.EqualTo(b.Retired), id);
            Assert.That((string)sim["layer"], Is.EqualTo(b.Layer.ToString()), id);
            Assert.That(sim["covers"].Values<string>(), Is.EqualTo(b.Covers.Select(p => p.ToString())), id);
            foreach (var dependency in AssetDatabase.GetDependencies(new[] { path, (string)row["definition"] }, true))
            {
                Assert.That(dependency, Does.Not.StartWith("Assets/HexLiveContent/Wear/").And.Not.StartWith("Assets/HexLiveContent/RuntimeSource/Wear/").And.Not.StartWith("Assets/ImportedActors/"), id + " old high-poly dependency");
                var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                if (type != null && (typeof(Texture).IsAssignableFrom(type) || typeof(Material).IsAssignableFrom(type) || typeof(Mesh).IsAssignableFrom(type)))
                    Assert.That(dependency, Does.StartWith(Staged + "/"), id + " prepared asset ownership");
            }
            if (seen.Add(path))
            {
                var wear = _prefabs[id]; var data = new SerializedObject(wear).FindProperty("configs");
                Assert.That(data.arraySize, Is.EqualTo(2), path + " canonical two-fit contract");
                var sources = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/HexLiveContent/Wear/" + source.ArtId })
                    .Select(AssetDatabase.GUIDToAssetPath).ToArray();
                Assert.That(sources.Length, Is.EqualTo(1), source.ArtId + " unambiguous canonical native prototype");
                var nativeWear = AssetDatabase.LoadAssetAtPath<GameObject>(sources[0]).GetComponent<Wear>();
                Assert.That(wear.Layer, Is.EqualTo(nativeWear.Layer), path + " preserved physical layer");
                Assert.That(wear.Slots, Is.EqualTo(nativeWear.Slots), path + " preserved visual coverage");
                Assert.That(wear.NoHideUnderwearSlots, Is.EqualTo(nativeWear.NoHideUnderwearSlots), path);
                Assert.That(wear.HideWearSlots, Is.EqualTo(nativeWear.HideWearSlots), path);
                Assert.That(wear.HidesHair, Is.EqualTo(nativeWear.HidesHair), path);
                Assert.That(wear.Heel, Is.EqualTo(nativeWear.Heel), path);
                Assert.That(wear.GetHeadwearFit(), Is.EqualTo(nativeWear.GetHeadwearFit()), path);
                var female = FittedMesh(wear, ActorName.Marta); var male = FittedMesh(wear, ActorName.Kshishtof);
                Assert.That(female, Is.Not.SameAs(male), path + " male geometry must have its own fitted mesh");
                foreach (var actor in new[] { ActorName.Marta, ActorName.Kshishtof })
                {
                    Assert.That(wear.SupportsActor(actor), Is.True); var mesh = FittedMesh(wear, actor);
                    Assert.That(mesh, Is.Not.Null); Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                    Assert.That(mesh.isReadable, Is.True, path + " CPU topology is needed by the actual wear painter");
                    Assert.That(AssetDatabase.GetAssetPath(mesh), Does.StartWith(Staged + "/Meshes/"));
                    ImportedMap(mesh);
                }
                Assert.That(wear.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length, Is.EqualTo(1));
                Assert.That(wear.GetComponentsInChildren<MonoBehaviour>(true).All(c => c is Wear), Is.True,
                    path + " no runtime cloth/refitting components are allowed in prepared art");
            }
            report.Add(new JObject { ["id"] = id, ["source"] = AssetDatabase.GetAssetPath(source), ["definition"] = (string)row["definition"],
                ["retired"] = b.Retired, ["statsMatchCanonicalNative"] = true, ["main"] = path });
        }
        File.WriteAllText(Output + "/metadata-coverage.json", new JObject { ["variants"] = report, ["uniqueLegacyModels"] = seen.Count,
            ["retainedDefaultsOutsideLiveCatalog"] = expected.Count,
            ["sourceOnlyOrphansExcluded"] = new JArray(native.Keys.Where(id => !defaults.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal)),
            ["liveCatalogChanged"] = false, ["published"] = false }.ToString());
    }

    [Test]
    public void EveryStagedVariantHasItsImportedOwnerIconAndActualRussianEnglishNamesAndDescriptions()
    {
        var previous = Loc.Current;
        try
        {
            var imported = AssetDatabase.LoadMainAssetAtPath("Assets/Resources/I2Languages.asset");
            Assert.That(imported, Is.Not.Null);
            var terms = new SerializedObject(imported).FindProperty("mSource.mTerms");
            Assert.That(terms, Is.Not.Null, "Terms must be inside the actual imported I2 source");
            var authored = new Dictionary<string, string[]>();
            for (int i = 0; i < terms.arraySize; i++)
            {
                var term = terms.GetArrayElementAtIndex(i); var translations = term.FindPropertyRelative("Languages");
                authored[term.FindPropertyRelative("Term").stringValue] = Enumerable.Range(0, translations.arraySize)
                    .Select(index => translations.GetArrayElementAtIndex(index).stringValue).ToArray();
            }
            foreach (var row in _legacy)
            {
                var id = (string)row["id"];
                // Same concrete per-owner icon path as canonical ResolvePeople;
                // no ItemIcons.Load/Address call that would initialize a remote service.
                var icon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + id + ".png");
                Assert.That(icon, Is.Not.Null, id + " owner icon must import as Sprite");
                Assert.That(icon.rect.width, Is.GreaterThanOrEqualTo(128)); Assert.That(icon.rect.height, Is.GreaterThanOrEqualTo(128));
                var item = ItemCatalog.Resolve(id);
                foreach (var language in new[] { Language.Russian, Language.English })
                {
                    Loc.Current = language;
                    foreach (var term in new[] { item.NameKey, item.DescKey })
                    {
                        Assert.That(authored.TryGetValue(term, out var values), Is.True, term + " imported I2 term");
                        Assert.That(values.Length, Is.GreaterThan((int)language));
                        Assert.That(values[(int)language], Is.Not.Empty.And.Not.EqualTo(term), "English fallback must not mask missing Russian authoring");
                        Assert.That(Loc.Has(term), Is.True, language + "/" + term); Assert.That(Loc.Get(term), Is.Not.Empty.And.Not.EqualTo(term));
                    }
                }
            }
        }
        finally { Loc.Current = previous; }
    }

    [UnityTest]
    public IEnumerator FourRepresentativeLegacyModelsPaintFromActualMapsWithoutAllocatingTheWholeCollection()
    {
        foreach (var id in new[] { "clothing.ankleboots_amy", "clothing.skirt_anarchy", "underwear.top_vapor", "FCO Pants Male" })
        foreach (var actor in new[] { ActorName.Marta, ActorName.Kshishtof })
        {
            Assert.That(_legacy.Any(r => (string)r["id"] == id), Is.True, "Missing representative " + id);
            var go = Actor(actor); var bones = go.GetComponent<BodyBones>(); var view = go.GetComponent<NpcActorView>();
            view.SyncWorn(new[] { id }); var wear = bones.GetWorn(id + "#0"); Assert.That(wear, Is.Not.Null);
            var skin = wear.GetComponentInChildren<SkinnedMeshRenderer>(); var mesh = skin.sharedMesh;
            var map = ImportedMap(mesh);
            var zones = map.Zones.Where(z => z.Points.Any(p => p.Valid)).Select(z => z.Zone).ToArray(); Assert.That(zones, Is.Not.Empty);
            var pristine = skin.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
            var dryColor = skin.sharedMaterial.GetColor("_BaseColor"); float drySmoothness = skin.sharedMaterial.GetFloat("_Smoothness");
            wear.SetErosion(.3f); wear.SetGrime(.8f, zones, zones.Select(_ => .8f).ToArray(), zones.Length, .6f); wear.SetWetness(.65f);
            var paint = wear.GetComponent<GarmentWearPainter>() ?? wear.GetComponentInChildren<GarmentWearPainter>(); Assert.That(paint, Is.Not.Null);
            Assert.That(typeof(GarmentWearPainter).GetField("_map", HiddenInstance).GetValue(paint), Is.SameAs(map), "Painter must use the injected imported map, not a network/runtime fallback");
            float until = Time.realtimeSinceStartup + 20f;
            while (!paint.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(paint.PresentationReady, Is.True, actor + "/" + id);
            var painted = skin.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
            Assert.That(painted.Select((t, i) => t is RenderTexture && pristine[i] != null && PixelDifference(t, pristine[i]) > .0001f).Any(x => x), Is.True,
                id + " GPU paint did not change albedo");
            var masks = skin.sharedMaterials.Where(m => m.HasProperty("_TearMaskTex")).Select(m => m.GetTexture("_TearMaskTex")).OfType<RenderTexture>().ToArray();
            Assert.That(masks, Is.Not.Empty, id + " actual tear mask");
            var block = new MaterialPropertyBlock(); skin.GetPropertyBlock(block, 0);
            Assert.That(block.GetFloat("_Smoothness"), Is.EqualTo(Mathf.Lerp(drySmoothness, .72f, .65f)).Within(.0001f), id + " wetness must reach the real renderer");
            Assert.That(Vector4.Distance(block.GetColor("_BaseColor"), dryColor), Is.GreaterThan(.001f));
            wear.SetWetness(0); skin.GetPropertyBlock(block, 0);
            Assert.That(block.GetFloat("_Smoothness"), Is.EqualTo(drySmoothness).Within(.0001f));
            Assert.That(Vector4.Distance(block.GetColor("_BaseColor"), dryColor), Is.LessThan(.0001f));
            view.SyncWorn(Array.Empty<string>()); yield return null; yield return null;
            Assert.That(painted.OfType<RenderTexture>().All(t => t == null || !t.IsCreated()), Is.True, id + " leaked paint RenderTexture");
            Object.DestroyImmediate(go); // Dispose each painter before allocating the next representative.
        }
    }
    static Color[] Pixels(Texture texture)
    {
        var old = RenderTexture.active; var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32);
        var image = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        try { Graphics.Blit(texture, rt); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); image.Apply(); return image.GetPixels(); }
        finally { RenderTexture.active = old; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(image); }
    }
    static float PixelDifference(Texture left, Texture right)
    { var a = Pixels(left); var b = Pixels(right); return a.Select((c, i) => Mathf.Abs(c.r - b[i].r) + Mathf.Abs(c.g - b[i].g) + Mathf.Abs(c.b - b[i].b)).Average(); }
}
}
#endif
