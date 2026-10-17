#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class CampfireHumanPartsRuntimeTests
{
    const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    readonly List<GameObject> objects = new();
    readonly Dictionary<object, object> savedHandles = new();
    IDictionary handles;
    object oldService, oldSubscription;
    bool oldAsync;
    Func<string, GameObject> oldObjectResolver;

    static object Swap(Type type, string name, object value)
    {
        var field = type.GetField(name, Static); var old = field.GetValue(null); field.SetValue(null, value); return old;
    }

    [SetUp]
    public void Setup()
    {
        Assert.That(Application.isPlaying, Is.True);
        oldAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        var service = FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        typeof(ContentAssetService).GetField("_pinned", Instance).SetValue(service, new Dictionary<string, ContentRecord>());
        oldService = Swap(typeof(ContentAssetService), "_instance", service);
        oldSubscription = Swap(typeof(ContentPrefabCache), "_registrySource", service);
        handles = (IDictionary)typeof(ContentPrefabCache).GetField("Handles", Static).GetValue(null);
        foreach (DictionaryEntry e in handles) savedHandles[e.Key] = e.Value;
        handles.Clear();
        oldObjectResolver = BedAssembly.EditorObjectPrefabResolver;
        BedAssembly.EditorObjectPrefabResolver = id => id == "campfire.spot"
            ? AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/campfire_final_native.fbx")
            : WorldPropResources.Load(id);
        foreach (var id in HumanMeatCatalog.RawParts.SelectMany(id => new[] { id, HumanMeatCatalog.Cooked(id) }).Append(HumanMeatCatalog.Head))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
            Assert.That(prefab, Is.Not.Null, id);
            handles["object/" + id] = Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>), Instance, null, new object[] { prefab, null }, null);
        }
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in objects) if (go != null) Object.DestroyImmediate(go);
        objects.Clear();
        if (handles != null) { handles.Clear(); foreach (var e in savedHandles) handles[e.Key] = e.Value; }
        savedHandles.Clear();
        BedAssembly.EditorObjectPrefabResolver = oldObjectResolver;
        Swap(typeof(ContentPrefabCache), "_registrySource", oldSubscription);
        Swap(typeof(ContentAssetService), "_instance", oldService);
        ShaderUtil.allowAsyncCompilation = oldAsync;
    }

    [UnityTest]
    public IEnumerator RawCookedPartsStayOnAllSixRealSpitSlotsAcrossFireRotations()
    {
        var fill = new[] { 2, 3, 1, 4, 0, 5 };
        var firstSlots = new Vector3[6];
        foreach (var yaw in new[] { 0f, 60f, 120f, 180f, 240f, 300f })
        {
            var root = BedAssembly.BuildFinished("campfire.spot");
            Assert.That(root, Is.Not.Null); objects.Add(root);
            root.transform.SetPositionAndRotation(new Vector3(3, .2f, -4), Quaternion.Euler(0, yaw, 0));
            var bar = root.GetComponentsInChildren<Transform>().Single(t => t.name == "stick_bar");
            var barPoints = LocalMeshPoints(bar.gameObject, root.transform);
            var barBounds = BoundsOf(barPoints);
            var spit = root.AddComponent<CampfireSpitMeat>();
            foreach (var cooked in new[] { false, true })
            {
                var ids = HumanMeatCatalog.RawParts.Select(id => cooked ? HumanMeatCatalog.Cooked(id) : id).ToArray();
                var contents = ids.Append(HumanMeatCatalog.Head).Select(id => new InventorySlotSnapshot { ItemDefinitionId = id, StackCount = 1 }).ToArray();
                spit.Refresh(cooked ? 0 : 6, cooked ? 6 : 0, contents);
                yield return null; // production Destroy removes old raw views before checking cooked
                var holder = root.transform.Find("SpitMeat");
                Assert.That(holder, Is.Not.Null);
                Assert.That(holder.childCount, Is.EqualTo(6), "head is not an edible spit part");
                var stable = holder.GetChild(0);
                spit.Refresh(cooked ? 0 : 6, cooked ? 6 : 0, contents);
                Assert.That(holder.GetChild(0), Is.SameAs(stable), "unchanged snapshot must not rebuild the row");
                for (var i = 0; i < 6; i++)
                {
                    var piece = holder.GetChild(i).gameObject;
                    Assert.That(piece.name, Is.EqualTo("Spit " + ids[i]));
                    var points = LocalMeshPoints(piece, root.transform);
                    var slot = (Vector3)typeof(CampfireSpitMeat).GetMethod("SpitSlot", Instance).Invoke(spit, new object[] { fill[i], 0f });
                    if (yaw == 0 && !cooked) firstSlots[i] = slot;
                    Assert.That(Vector3.Distance(slot, firstSlots[i]), Is.LessThan(.0002f), "fire yaw must not move spit slots in its own space");
                    var centre = BoundsOf(points).center;
                    Assert.That(Vector3.Distance(centre, slot), Is.LessThan(.0002f), ids[i] + " not attached to bar");
                    Assert.That(centre.y, Is.EqualTo(barBounds.center.y).Within(.0002f));
                    var alongX = barBounds.size.x >= barBounds.size.z;
                    Assert.That(alongX ? centre.z : centre.x, Is.EqualTo(alongX ? barBounds.center.z : barBounds.center.x).Within(.0002f));
                    Assert.That(alongX ? centre.x : centre.z, Is.InRange(alongX ? barBounds.min.x : barBounds.min.z, alongX ? barBounds.max.x : barBounds.max.z));
                    var prefab = WorldPropResources.Load(ids[i]);
                    Assert.That(ObjectFit.WorldBounds(prefab, out var authoredBounds), Is.True);
                    var expectedThickness = authoredBounds.size.y * ObjectFit.FitScaleFactor(prefab, ids[i]);
                    var thickness = points.Max(v => v.y) - points.Min(v => v.y);
                    Assert.That(thickness, Is.EqualTo(expectedThickness).Within(.0002f), ids[i] + " imported basis lost / standing upright on spit");
                }
                if (yaw == 0) Render(root, cooked ? "spit-cooked" : "spit-raw");
            }
            spit.Refresh(0, 0, Array.Empty<InventorySlotSnapshot>());
            yield return null;
            Assert.That(root.transform.Find("SpitMeat").childCount, Is.Zero, "taking food clears visuals");
            Object.DestroyImmediate(root);
        }
    }

    static Vector3[] LocalMeshPoints(GameObject item, Transform root) => item.GetComponentsInChildren<MeshFilter>()
        .SelectMany(f => f.sharedMesh.vertices.Select(v => root.InverseTransformPoint(f.transform.TransformPoint(v)))).ToArray();

    static Bounds BoundsOf(Vector3[] points)
    {
        Assert.That(points, Is.Not.Empty);
        var bounds = new Bounds(points[0], Vector3.zero);
        foreach (var point in points) bounds.Encapsulate(point);
        return bounds;
    }

    static void Render(GameObject root, string name)
    {
        var preview = new PreviewRenderUtility();
        try
        {
            var camera = preview.camera; camera.orthographic = true; camera.orthographicSize = 1.05f;
            camera.transform.position = root.transform.position + new Vector3(1.8f, 1.5f, -2.8f);
            camera.transform.LookAt(root.transform.position + Vector3.up * .5f);
            camera.nearClipPlane = .01f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .14f, .16f);
            preview.lights[0].intensity = 1.2f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 180, 0);
            preview.lights[1].intensity = .7f; preview.lights[1].transform.rotation = Quaternion.Euler(-15, -30, 0);
            var path = Path.GetFullPath("Build/MSIRelease/20261018-butcher-spit"); Directory.CreateDirectory(path);
            for (var pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0, 0, 1200, 800));
                foreach (var mesh in root.GetComponentsInChildren<MeshFilter>())
                {
                    var materials = mesh.GetComponent<MeshRenderer>().sharedMaterials;
                    for (var sub = 0; sub < mesh.sharedMesh.subMeshCount; sub++) preview.DrawMesh(mesh.sharedMesh, mesh.transform.localToWorldMatrix, materials[sub], sub);
                }
                camera.Render(); var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(Path.Combine(path, name + ".png"), image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
        }
        finally { preview.Cleanup(); }
    }
}
}
#endif
