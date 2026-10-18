#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.Tests
{
public sealed class HumanButcheryPresentationTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static Type EnvironmentType(string name) => typeof(GroundBloodStains).Assembly.GetType(
        "HexLive.UnityPresentation.Environment." + name, true);

    [Test]
    public void AllThirteenSourceCutsFitAndRestOnGroundInProductionPose()
    {
        var host = new GameObject("ButcheryFitTest");
        var renderer = host.AddComponent<HexWorldRenderer>();
        try
        {
            var ids = HumanMeatCatalog.RawParts.SelectMany(id => new[] { id, HumanMeatCatalog.Cooked(id) })
                .Append(HumanMeatCatalog.Head).ToArray();
            Assert.That(ids.Length, Is.EqualTo(13));
            foreach (var id in ids)
            {
                var path = "Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                Assert.That(ObjectFit.FitScaleFactor(prefab, id),
                    Is.EqualTo(HexWorldRenderer.ActorScale).Within(.0001f), id + " must match live body proportions");
                for (var index = 1; index <= 4; index++)
                {
                    var instance = Object.Instantiate(prefab);
                    try
                    {
                        typeof(HexWorldRenderer).GetMethod("FitObjectPrefab", Hidden).Invoke(renderer,
                            new object[] { instance, id, index, true, false });
                        Assert.That(ObjectFit.WorldBounds(instance, out var bounds), Is.True, id);
                        Assert.That(ObjectFit.TryStaticMeshMinimumY(instance, out var minimum), Is.True);
                        Assert.That(minimum, Is.EqualTo(0).Within(.0001f), id + " floats or sinks");
                        Assert.That(bounds.size.magnitude, Is.GreaterThan(.05f), id);
                        Assert.That(bounds.size.magnitude, Is.LessThan(2.1f), id);
                        foreach (var visual in instance.GetComponentsInChildren<MeshRenderer>())
                        {
                            Assert.That(visual.sharedMaterials.All(m => m != null &&
                                m.name.StartsWith(id.EndsWith("cooked") ? "HumanCooked" : "HumanRaw")), Is.True, id);
                            var mesh = visual.GetComponent<MeshFilter>().sharedMesh;
                            Assert.That(mesh.vertexCount, Is.GreaterThan(30), id);
                            Assert.That(mesh.vertexCount, Is.LessThan(40000), id);
                        }
                    }
                    finally { Object.DestroyImmediate(instance); }
                }
            }
        }
        finally { Object.DestroyImmediate(host); }
    }

    [Test]
    public void AtomicOwnerDiscoveryResolvesEveryNativeModelAndAuthoredIcon()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AtomicContentBatchBuild")).First(t => t != null);
        var find = type.GetMethod("FindObjectMain", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var id in HumanMeatCatalog.RawParts.SelectMany(id => new[] { id, HumanMeatCatalog.Cooked(id) }).Append(HumanMeatCatalog.Head))
        {
            Assert.That(find.Invoke(null, new object[] { id }), Is.EqualTo("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx"), id);
            var icon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + id + ".png");
            Assert.That(icon, Is.Not.Null, id);
            Assert.That(icon.rect.width, Is.EqualTo(512), id);
            Assert.That(icon.rect.height, Is.EqualTo(512), id);
        }
    }

    [Test]
    public void GroundPoseIsStableAndActuallyChangesTheRestingSide()
    {
        var method = EnvironmentType("HumanPartGroundPose").GetMethod("Rotation", BindingFlags.NonPublic | BindingFlags.Static);
        Quaternion Pose(int id) => (Quaternion)method.Invoke(null, new object[] { HumanMeatCatalog.LegRaw, id, Quaternion.Euler(-90, 0, 0) });
        Assert.That(Quaternion.Angle(Pose(11), Pose(11)), Is.LessThan(.001f));
        var ups = Enumerable.Range(1, 4).Select(id => Vector3.Dot(Pose(id) * Vector3.forward, Vector3.up)).ToArray();
        Assert.That(ups.Max() - ups.Min(), Is.GreaterThan(1.5f));
    }

    [Test]
    public void ButcheryEventDoesNotReplayHistoryOrDuplicateOnSnapshotRefresh()
    {
        var type = EnvironmentType("ButcheryBloodEvents");
        var events = Activator.CreateInstance(type, true);
        var consume = type.GetMethod("Consume", Hidden);
        var snapshot = new WorldSnapshot { Seed = 12, Tick = 100 };
        snapshot.Npcs.Add(new NpcSnapshot { Id = new EntityId(7) });
        snapshot.TraceEvents.Add(new TraceEventSnapshot { Seq = 1, Tick = 100, EntityId = 7, Type = "Butchered" });
        var spawned = 0;
        Action<NpcSnapshot, int> callback = (_, _) => spawned++;
        void Apply() => consume.Invoke(events, new object[] { snapshot, callback });
        Apply(); Assert.That(spawned, Is.Zero, "joining must not replay historical blood");
        snapshot.Tick = 101;
        snapshot.TraceEvents.Add(new TraceEventSnapshot { Seq = 2, Tick = 101, EntityId = 7, Type = "Butchered" });
        Apply(); Apply(); Assert.That(spawned, Is.EqualTo(1));
        snapshot.Tick = 130;
        snapshot.TraceEvents.Add(new TraceEventSnapshot { Seq = 3, Tick = 102, EntityId = 7, Type = "Butchered" });
        Apply(); Assert.That(spawned, Is.EqualTo(1), "late event is not the worker's current location");
        snapshot.Seed = 13; Apply(); Assert.That(spawned, Is.EqualTo(1));
        snapshot.Tick = 1; Apply(); Assert.That(spawned, Is.EqualTo(1));
    }

    [Test]
    public void LargeButcheryPoolSurvivesAnOrdinaryDripAndExpiresNormally()
    {
        var host = new GameObject("ButcheryBloodTest");
        var stains = host.AddComponent<GroundBloodStains>();
        var source = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(
            AssetDatabase.FindAssets("BloodStain_01 t:Material").First()));
        Assert.That(source, Is.Not.Null);
        var pigments = new Material[1, 6];
        for (var i = 0; i < 6; i++) pigments[0, i] = new Material(source);
        typeof(GroundBloodStains).GetField("_stainMats", Hidden).SetValue(stains, pigments);
        typeof(GroundBloodStains).GetField("_variantCount", Hidden).SetValue(stains, 1);
        try
        {
            stains.OnButchery(Vector3.zero, 10);
            var projectorType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(
                "UnityEngine.Rendering.Universal.DecalProjector")).First(t => t != null);
            var pool = host.GetComponentInChildren(projectorType);
            Vector3 Size() => (Vector3)projectorType.GetProperty("size").GetValue(pool);
            Assert.That(Size().x, Is.GreaterThan(1.6f));
            stains.OnBleedingSourceTick(7, Vector3.zero, 11);
            Assert.That(host.GetComponentsInChildren(projectorType).Length, Is.EqualTo(1));
            stains.Advance(131);
            Assert.That(Size().x, Is.EqualTo(2.1f).Within(.001f));
            stains.Advance(7212);
            var list = (IList)typeof(GroundBloodStains).GetField("_stains", Hidden).GetValue(stains);
            Assert.That(list.Count, Is.Zero);
        }
        finally { Object.DestroyImmediate(host); }
    }
}
}
#endif
