#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class HearthFirePlacementRuntimeTests
{
    const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Test]
    public void AuthoredHearthFireStaysInItsBowlAtEveryFootprintYaw()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/RuntimeSource/Objects/furniture.hearth.fbx");
        Assert.That(prefab, Is.Not.Null);
        var serviceField = typeof(ContentAssetService).GetField("_instance", Static);
        var sourceField = typeof(AtomicResources).GetField("_registrySource", Static);
        var service = FormatterServices.GetUninitializedObject(typeof(ContentAssetService));
        typeof(ContentAssetService).GetField("_pinned", Instance).SetValue(service, new Dictionary<string, ContentRecord>());
        var oldService = serviceField.GetValue(null); var oldSource = sourceField.GetValue(null);
        var handles = (IDictionary)typeof(AtomicResources).GetField("Handles", Static).GetValue(null);
        const string key = "object/furniture.hearth#main";
        var hadHandle = handles.Contains(key); var oldHandle = handles[key];
        var roots = new List<GameObject>(); var measurements = new JArray();
        serviceField.SetValue(null, service); sourceField.SetValue(null, service);
        handles[key] = Activator.CreateInstance(typeof(ContentAssetHandle<Object>), Instance, null, new object[] { prefab, null }, null);
        try
        {
            foreach (var yaw in new[] { 0f, 60f, 120f, 180f, 240f, 300f })
            {
                var root = HutFurnitureFactory.BuildHearth(); roots.Add(root);
                root.transform.SetPositionAndRotation(new Vector3(12, .107475f, -9), Quaternion.Euler(0, yaw, 0));
                var marker = root.transform.Find("fire_point"); Assert.That(marker, Is.Not.Null);
                marker.gameObject.AddComponent<CampfireEffect>().Construct(1.5f * .42f);
                var flame = marker.Find("Flame");
                var p = root.transform.InverseTransformPoint(marker.position);
                var f = root.transform.InverseTransformPoint(flame.position);
                measurements.Add(new JObject { ["yaw"] = yaw,
                    ["marker"] = new JArray(p.x, p.y, p.z), ["flame"] = new JArray(f.x, f.y, f.z),
                    ["flameUp"] = Vector3.Dot(flame.forward, Vector3.up) });
            }
            Directory.CreateDirectory("Build/Diagnostics/20261019-bug491");
            File.WriteAllText("Build/Diagnostics/20261019-bug491/unity-hearth-measurements.json", measurements.ToString());
            foreach (JObject row in measurements)
            {
                Assert.That((float)row["marker"][0], Is.EqualTo(0).Within(.0002f), "fire must be inside the authored bowl, yaw=" + row["yaw"]);
                Assert.That((float)row["marker"][2], Is.EqualTo(0).Within(.0002f));
                Assert.That((float)row["marker"][1], Is.EqualTo(.055f).Within(.0002f));
                Assert.That((float)row["flame"][1], Is.EqualTo(.055f + 1.5f * .42f * .06f).Within(.0002f));
                Assert.That((float)row["flameUp"], Is.GreaterThan(.999f));
            }
            Capture(roots[0]);
        }
        finally
        {
            foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
            if (hadHandle) handles[key] = oldHandle; else handles.Remove(key);
            sourceField.SetValue(null, oldSource); serviceField.SetValue(null, oldService);
        }
    }

    static void Capture(GameObject root)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>())
        {
            ps.useAutoRandomSeed = false; ps.randomSeed = 491;
            ps.Simulate(.7f, true, true);
            Assert.That(ps.particleCount, Is.GreaterThan(0));
        }
        root.GetComponentInChildren<Light>().enabled = true;
        var cameraHost = new GameObject("Hearth verification camera");
        var lightHost = new GameObject("Hearth verification light");
        var target = new RenderTexture(1000, 800, 24);
        var pixels = new Texture2D(1000, 800, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        var asyncCompilation = ShaderUtil.allowAsyncCompilation;
        try
        {
            ShaderUtil.allowAsyncCompilation = false;
            var light = lightHost.AddComponent<Light>(); light.type = LightType.Directional;
            light.intensity = 1.5f; light.cullingMask = 1 << 31;
            lightHost.transform.rotation = Quaternion.Euler(45, -30, 0);
            var camera = cameraHost.AddComponent<Camera>(); camera.cullingMask = 1 << 31;
            camera.orthographic = true; camera.orthographicSize = .55f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .14f, .16f);
            camera.nearClipPlane = .01f; camera.farClipPlane = 10;
            camera.transform.position = root.transform.position + new Vector3(1, .8f, -1.3f);
            camera.transform.LookAt(root.transform.position + Vector3.up * .18f);
            camera.targetTexture = target; camera.Render(); camera.Render();
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1000, 800), 0, 0); pixels.Apply();
            File.WriteAllBytes("Build/Diagnostics/20261019-bug491/hearth-after.png", pixels.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; ShaderUtil.allowAsyncCompilation = asyncCompilation;
            Object.DestroyImmediate(cameraHost); Object.DestroyImmediate(lightHost);
            Object.DestroyImmediate(target); Object.DestroyImmediate(pixels);
        }
    }

    [Test]
    public void ImportedConstructionPassesTheCanonicalBuildGate()
    {
        var gate = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("HexLive.Editor.ConstructionStageBuildGate"))
            .Single(t => t != null);
        gate.GetMethod("ValidateAll").Invoke(null, null);
    }
}
}
#endif
