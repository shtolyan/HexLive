#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
// Run graphics PlayMode with -wardrobe-bundle <the released UnityFS payload>.
// The actual serialized owner is loaded, unloaded and loaded again: this must
// not be replaced by a scene-only fixture that misses bundle/import ancestors.
public sealed class WardrobeBundlePlacementRuntimeTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const string Key = "object/furniture.wardrobe#main";
    readonly List<GameObject> objects = new();
    object oldService, oldSubscription;
    readonly List<(FieldInfo field, object value)> fields = new();
    readonly List<(IDictionary cache, Dictionary<object, object> entries)> caches = new();
    IDictionary handles, prefabHandles, wardrobe, variants;
    Vector3[] savedDryingSlots;
    AssetBundle bundle;

    object Swap(Type type, string field, object value)
    {
        var f = type.GetField(field, Static); var old = f.GetValue(null);
        fields.Add((f, old)); f.SetValue(null, value); return old;
    }
    IDictionary Cache(Type type, string name)
    {
        var cache = (IDictionary)type.GetField(name, Static).GetValue(null);
        var entries = new Dictionary<object, object>();
        foreach (DictionaryEntry e in cache) entries[e.Key] = e.Value;
        caches.Add((cache, entries)); cache.Clear(); return cache;
    }
    [SetUp]
    public void Setup()
    {
        var service = FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        typeof(ContentAssetService).GetField("_pinned", Instance).SetValue(service, new Dictionary<string, ContentRecord>());
        oldService = Swap(typeof(ContentAssetService), "_instance", service);
        oldSubscription = Swap(typeof(AtomicResources), "_registrySource", service);
        Swap(typeof(ContentPrefabCache), "_registrySource", service);
        handles = Cache(typeof(AtomicResources), "Handles");
        Cache(typeof(AtomicResources), "Terminal");
        Cache(typeof(BlueprintArchitectureFactory), "ModelPrefabs");
        prefabHandles = Cache(typeof(ContentPrefabCache), "Handles");
        Swap(typeof(DryingRackHangers), "_resolvedPrefab", null);
        Swap(typeof(DryingRackHangers), "_hasAuthoredSlots", false);
        savedDryingSlots = (Vector3[])((Vector3[])typeof(DryingRackHangers)
            .GetField("AuthoredSlots", Static).GetValue(null)).Clone();
        wardrobe = Cache(typeof(ActorWardrobe), "Cache");
        variants = Cache(typeof(GarmentVariants), "ById");
        Cache(typeof(WardrobeAssembly), "ClothingSlots");
        Swap(typeof(WardrobeAssembly), "_clothingSlotsResolved", false);
        Swap(typeof(WardrobeAssembly), "_shoeShelfResolved", false);
        Swap(typeof(WardrobeAssembly), "_shoeShelfSurface", 0f);
    }
    [TearDown]
    public void Cleanup()
    {
        DestroyInstances();
        if (bundle != null) bundle.Unload(true);
        savedDryingSlots.CopyTo((Vector3[])typeof(DryingRackHangers)
            .GetField("AuthoredSlots", Static).GetValue(null), 0);
        foreach (var (cache, entries) in caches) { cache.Clear(); foreach (var e in entries) cache[e.Key] = e.Value; }
        caches.Clear();
        for (int i = fields.Count - 1; i >= 0; i--) fields[i].field.SetValue(null, fields[i].value);
        fields.Clear();
    }
    void DestroyInstances()
    {
        foreach (var go in objects) if (go != null) Object.DestroyImmediate(go);
        objects.Clear();
    }
    void Publish(GameObject prefab)
    {
        handles[Key] = Activator.CreateInstance(typeof(ContentAssetHandle<Object>), Instance, null, new object[] { prefab, null }, null);
        typeof(WardrobeAssembly).GetField("_clothingSlotsResolved", Static).SetValue(null, false);
        typeof(WardrobeAssembly).GetField("_shoeShelfResolved", Static).SetValue(null, false);
    }
    static Bounds Geometry(GameObject go, Transform frame)
    {
        var bounds = new Bounds(); bool first = true;
        foreach (var f in go.GetComponentsInChildren<MeshFilter>(true))
        {
            var b = f.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                p = frame.InverseTransformPoint(f.transform.TransformPoint(p));
                if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p);
            }
        }
        Assert.That(first, Is.False); return bounds;
    }
    [Test]
    public void SerializedWebGlOwnerPreservesHangerAxesAndShelfAcrossReloadAndSixYaws()
    {
        var args = System.Environment.GetCommandLineArgs(); var at = Array.IndexOf(args, "-wardrobe-bundle");
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "Pass the exact published bundle");
        for (int load = 0; load < 2; load++)
        {
            bundle = AssetBundle.LoadFromFile(args[at + 1]); Assert.That(bundle, Is.Not.Null);
            var prefab = bundle.LoadAsset<GameObject>("main"); Assert.That(prefab, Is.Not.Null); Publish(prefab);
            Verify(prefab);
            DestroyInstances(); handles.Remove(Key); bundle.Unload(true); bundle = null;
        }
    }
    [Test]
    public void ImportedDryingRackUsesEightDistinctAuthoredSlotsAcrossReloadAndSixYaws()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/RuntimeSource/Objects/station.drying_rack.fbx");
        Assert.That(source, Is.Not.Null);
        const string garmentId = "clothing.wrapboots_primal";
        wardrobe[garmentId] = new List<Wear> { AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Wear/Marta_PrimalWrapBoots.prefab").GetComponent<Wear>() };
        variants[garmentId] = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(
            "Assets/HexLiveContent/People/Definitions/clothing.wrapboots_primal.asset");
        var hang = typeof(HexWorldRenderer).GetMethod("HangSlotOffset", Static);
        Assert.That(hang, Is.Not.Null);
        for (int reload = 0; reload < 2; reload++)
        {
            var prefab = Object.Instantiate(source); objects.Add(prefab);
            prefabHandles["object/station.drying_rack"] = Activator.CreateInstance(
                typeof(ContentAssetHandle<GameObject>), Instance, null, new object[] { prefab, null }, null);
            var slots = Enumerable.Range(0, 8).Select(DryingRackHangers.Slot).ToArray();
            for (int i = 0; i < slots.Length; i++)
            {
                Assert.That(slots[i].y, Is.EqualTo(i < 4 ? 1.14f : .73f).Within(.0001f));
                for (int j = 0; j < i; j++)
                    Assert.That(Vector3.Distance(slots[i], slots[j]), Is.GreaterThan(.15f),
                        $"Drying slots {i}/{j} collapsed");
            }
            for (int yaw = 0; yaw < 360; yaw += 60)
            {
                var frame = new GameObject("Drying production frame " + yaw); objects.Add(frame);
                frame.transform.SetPositionAndRotation(new Vector3(12, 3, -7), Quaternion.Euler(0, yaw, 0));
                var model = Object.Instantiate(prefab, frame.transform, false);
                for (int i = 0; i < 8; i++)
                {
                    var socket = model.GetComponentsInChildren<Transform>(true).Single(t => t.name == $"DryingSlot_{i:D2}");
                    Assert.That(Vector3.Distance(frame.transform.TransformPoint(slots[i]), socket.position), Is.LessThan(.0001f));
                    var garment = GarmentDropFactory.BuildHanging(garmentId); Assert.That(garment, Is.Not.Null);
                    garment.transform.SetParent(frame.transform, false);
                    garment.transform.localScale = Vector3.one * .7764706f;
                    var position = (Vector3)hang.Invoke(null, new object[] { garment.transform, i, false, Vector3.zero });
                    garment.transform.localPosition = position;
                    Assert.That(position.x, Is.EqualTo(slots[i].x).Within(.0001f));
                    Assert.That(position.z, Is.EqualTo(slots[i].z).Within(.0001f));
                    Assert.That(Geometry(garment, frame.transform).min.y, Is.GreaterThanOrEqualTo(.0399f));
                }
                Object.DestroyImmediate(frame);
            }
        }
    }
    [Test]
    public void ImportedWorkbenchHasUpwardCountertopFacesAcrossSixYaws()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/RuntimeSource/Objects/station.workbench.fbx");
        Assert.That(prefab, Is.Not.Null);
        prefabHandles["object/station.workbench"] = Activator.CreateInstance(
            typeof(ContentAssetHandle<GameObject>), Instance, null, new object[] { prefab, null }, null);
        for (int yaw = 0; yaw < 360; yaw += 60)
        {
            var view = BedAssembly.BuildFinished("station.workbench"); Assert.That(view, Is.Not.Null); objects.Add(view);
            view.transform.SetPositionAndRotation(new Vector3(12, 3, -7), Quaternion.Euler(0, yaw, 0));
            var tops = view.GetComponentsInChildren<MeshFilter>(true).Where(f =>
                System.Text.RegularExpressions.Regex.IsMatch(f.name, "^board_workbench_[0-9]+$")).ToArray();
            Assert.That(tops.Length, Is.EqualTo(4));
            foreach (var filter in tops)
            {
                var vertices = filter.sharedMesh.vertices.Select(v => view.transform.InverseTransformPoint(
                    filter.transform.TransformPoint(v))).ToArray();
                var triangles = filter.sharedMesh.triangles;
                float highest = float.NegativeInfinity; Vector3 normal = default;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var a = vertices[triangles[i]]; var b = vertices[triangles[i+1]]; var c = vertices[triangles[i+2]];
                    var height = (a.y + b.y + c.y) / 3;
                    if (height <= highest) continue;
                    highest = height; normal = Vector3.Cross(b-a, c-a).normalized;
                }
                Assert.That(highest, Is.GreaterThan(.77f));
                Assert.That(normal.y, Is.GreaterThan(.99f), filter.name + " top face must be visible from above");
            }
            Object.DestroyImmediate(view);
        }
    }
    [Test]
    public void ImportedRoofOwnersKeepRealLeavesWithoutTechnicalBackingAcrossSixYaws()
    {
        foreach (var id in new[] { "architecture.roof.palm", "architecture.roof.palm.flat", "building.hut_1hex" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
            Assert.That(prefab, Is.Not.Null);
            handles["building/" + id + "#main"] = Activator.CreateInstance(
                typeof(ContentAssetHandle<Object>), Instance, null, new object[] { prefab, null }, null);
            for (int yaw = 0; yaw < 360; yaw += 60)
            {
                var view = id == "building.hut_1hex" ? HutAssembly.BuildFinished() :
                    BlueprintArchitectureFactory.InstantiateModel(id, Vector3.zero, Quaternion.identity);
                Assert.That(view, Is.Not.Null); objects.Add(view);
                view.transform.SetPositionAndRotation(new Vector3(12, 3, -7), Quaternion.Euler(0, yaw, 0));
                Assert.That(view.GetComponentsInChildren<Transform>(true).Any(t =>
                    t.name.IndexOf("ThatchBacking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("roof_woven_mat_", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
                var meshes = view.GetComponentsInChildren<MeshFilter>(true);
                var leaves = meshes.Count(f => f.name.IndexOf("_leaf_", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.That(leaves, Is.EqualTo(id == "building.hut_1hex" ? 48 : 27));
                Assert.That(meshes.Length, Is.EqualTo(id == "building.hut_1hex" ? 152 : 32));
                Object.DestroyImmediate(view);
            }
        }
    }
    [Test]
    public void PendingRoofContentDoesNotGenerateAnOpaqueTriangle()
    {
        var terminal = (IDictionary)typeof(AtomicResources).GetField("Terminal", Static).GetValue(null);
        terminal["building/architecture.roof.palm.flat#main"] = AtomicResources.Availability.Loading;
        var element = new BlueprintElementData { Kind = BlueprintElementKind.RoofSector,
            RoofSector = new RoofSectorKey(new TileCoord(0, 0), 0) };
        var draft = new BuildingBlueprintDraft(); draft.Elements.Add(element);
        var pending = BlueprintArchitectureFactory.Build(element, draft); objects.Add(pending);
        Assert.That(pending.GetComponentsInChildren<Renderer>(true), Is.Empty);
        Assert.That(pending.GetComponentsInChildren<MeshFilter>(true), Is.Empty);
    }
    [Test]
    public void ImportedSourceUsesTheSameProductionFactories()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/furniture.wardrobe.fbx");
        Assert.That(prefab, Is.Not.Null); Publish(prefab); Verify(prefab);
    }
    [Test]
    public void RenderImportedWardrobeWithProductionHangersAndBoots()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/furniture.wardrobe.fbx");
        Publish(prefab);
        var frame = new GameObject("Wardrobe visual verification"); objects.Add(frame);
        WardrobeAssembly.BuildFinished().transform.SetParent(frame.transform, false);
        var hangers = new List<GameObject>();
        for (int i = 0; i < WardrobeHangers.SlotCount; i++)
        {
            var hanger = WardrobeHangerFactory.Build(prefab); hanger.transform.SetParent(frame.transform, false);
            WardrobeAssembly.TryGetClothingSlotLocal(i, out var slot); hanger.transform.localPosition = slot; hangers.Add(hanger);
        }
        const string id = "clothing.wrapboots_primal";
        wardrobe[id] = new List<Wear> { AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Wear/Marta_PrimalWrapBoots.prefab").GetComponent<Wear>() };
        variants[id] = AssetDatabase.LoadAssetAtPath<GarmentDefinition>("Assets/HexLiveContent/People/Definitions/clothing.wrapboots_primal.asset");
        var shoes = GarmentDropFactory.Build(id); shoes.transform.SetParent(frame.transform, false);
        shoes.transform.localScale = Vector3.one * .7764706f;
        shoes.transform.localPosition = WardrobeHangers.GroundFootwearOnShelf(shoes.transform, 1);
        Draw(frame, "wardrobe-after");
        foreach (var hanger in hangers) hanger.transform.GetChild(0).localRotation = Quaternion.identity;
        shoes.transform.localPosition -= Vector3.up * .075f;
        Draw(frame, "wardrobe-before");
    }
    static void Draw(GameObject frame, string name)
    {
        var preview = new PreviewRenderUtility();
        var oldAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        try
        {
            var camera = preview.camera; camera.orthographic = true; camera.orthographicSize = .95f;
            camera.transform.position = new Vector3(2.3f, 1.65f, 2.1f); camera.transform.LookAt(new Vector3(0, .8f, 0));
            camera.nearClipPlane = .01f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f,.18f,.20f);
            preview.lights[0].intensity = 1.3f; preview.lights[0].transform.rotation = Quaternion.Euler(35, -110, 0);
            preview.lights[1].intensity = .7f; preview.lights[1].transform.rotation = Quaternion.Euler(25, 60, 0);
            var path = "Assets/HexLiveContent/People/Validation/Review/"; System.IO.Directory.CreateDirectory(path);
            for (int pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0, 0, 960, 960));
                foreach (var f in frame.GetComponentsInChildren<MeshFilter>(false))
                {
                    var renderer = f.GetComponent<MeshRenderer>(); if (renderer == null) continue;
                    for (int m = 0; m < f.sharedMesh.subMeshCount; m++) preview.DrawMesh(f.sharedMesh, f.transform.localToWorldMatrix, renderer.sharedMaterials[m], m);
                }
                camera.Render(); var image = preview.EndStaticPreview();
                if (pass == 1) System.IO.File.WriteAllBytes(path + name + ".png", image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
        }
        finally { preview.Cleanup(); ShaderUtil.allowAsyncCompilation = oldAsync; }
    }
    void Verify(GameObject prefab)
    {
        Assert.That(WardrobeAssembly.TryGetShoeShelfSurfaceLocal(out var top), Is.True);
        // The approved construction artwork lowers this shelf by .055 wu
        // (Docs/ConstructionStagesAuthoring.md); source and bundle must agree.
        Assert.That(top, Is.EqualTo(.210f).Within(.00001f), "Approved construction shelf surface");
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            string id = actor == "Marta" ? "clothing.wrapboots_primal" : "clothing.wrapboots_primal_male";
            var boot = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/People/Prefabs/Wear/" + actor + "_PrimalWrapBoots.prefab");
            wardrobe[id] = new List<Wear> { boot.GetComponent<Wear>() };
            // Production variant data, with no remote lookup in this fixture.
            var catalog = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText("Assets/HexLiveContent/People/catalog.json"));
            var row = catalog["records"].Single(r => (string)r["type"] == "wear" && (string)r["id"] == id);
            variants[id] = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
            for (int yaw = 0; yaw < 360; yaw += 60)
            {
                var frame = new GameObject("Wardrobe placement " + yaw); objects.Add(frame);
                frame.transform.SetPositionAndRotation(new Vector3(12, 3, -7), Quaternion.Euler(0, yaw, 0));
                frame.transform.localScale = Vector3.one * (yaw == 60 ? 1.25f : 1f);
                var assembly = WardrobeAssembly.BuildFinished(); Assert.That(assembly, Is.Not.Null);
                assembly.transform.SetParent(frame.transform, false);
                var hanger = WardrobeHangerFactory.Build(prefab); Assert.That(hanger, Is.Not.Null);
                hanger.transform.SetParent(frame.transform, false);
                Assert.That(WardrobeAssembly.TryGetClothingSlotLocal(0, out var socket), Is.True);
                hanger.transform.localPosition = socket;
                for (int slotIndex = 1; slotIndex < WardrobeHangers.SlotCount; slotIndex++)
                {
                    Assert.That(WardrobeAssembly.TryGetClothingSlotLocal(slotIndex, out var nextSocket), Is.True);
                    Assert.That(Vector3.Distance(socket, nextSocket), Is.GreaterThan(.04f));
                    var nextHanger = WardrobeHangerFactory.Build(prefab); Assert.That(nextHanger, Is.Not.Null);
                    nextHanger.transform.SetParent(frame.transform, false);
                    nextHanger.transform.localPosition = nextSocket;
                    Assert.That(Vector3.Distance(frame.transform.InverseTransformPoint(nextHanger.transform.position), nextSocket), Is.LessThan(.0001f));
                }
                var bounds = Geometry(hanger, frame.transform);
                Assert.That(bounds.size.x, Is.EqualTo(.2525226f).Within(.0001f));
                Assert.That(bounds.size.y, Is.EqualTo(.2387662f).Within(.0001f), "Hanger must stay upright after detaching");
                Assert.That(bounds.size.z, Is.EqualTo(.0166200f).Within(.0001f));
                var shoes = GarmentDropFactory.Build(id); Assert.That(shoes, Is.Not.Null);
                shoes.transform.SetParent(frame.transform, false); shoes.transform.localScale = Vector3.one * .7764706f;
                for (int sync = 0; sync < 4; sync++)
                {
                    shoes.transform.localPosition = WardrobeHangers.GroundFootwearOnShelf(shoes.transform, 0);
                    ObjectFit.WorldBounds(shoes, out var box);
                    Assert.That(box.min.y, Is.EqualTo(frame.transform.TransformPoint(Vector3.up * top).y).Within(.0001f), actor + " shelf sync " + sync);
                }
                Object.DestroyImmediate(frame);
            }
        }
    }
}
}
#endif
