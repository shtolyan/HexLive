#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Spatial;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class WorldHouseGeometryRuntimeTests
{
    [TestCase(0)] [TestCase(60)] [TestCase(120)] [TestCase(180)] [TestCase(240)] [TestCase(300)]
    public void ActualImportedModulesAndWorldEditorProjectionShareSixWayGeometry(float yaw)
    {
        var cache = (IDictionary)typeof(BlueprintArchitectureFactory).GetField("ModelPrefabs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var saved = new Dictionary<string, (bool had, object value)>();
        var roots = new List<GameObject>();
        var plan = BuiltInBuildingBlueprints.Hut1Hex(); var destination = new TileCoord(-6, -26);
        var projected = new BuildingBlueprintDraft { HasAnchor = true };
        foreach (var e in plan.Elements) projected.Elements.Add(ArchitectureWorldEditing.ToWorld(e, plan, destination, yaw));
        var modules = BlueprintBuildingPlan.Modules(plan).ToDictionary(m => m.Key);
        var actualRoot = new GameObject("664 real module views"); roots.Add(actualRoot);
        actualRoot.transform.position = SimulationUnityMapper.ToUnityTilePosition(destination);
        var previewRoot = new GameObject("664 world constructor preview"); roots.Add(previewRoot);
        try
        {
            foreach (var id in new[] { "architecture.support.wood", "architecture.floor.board", "architecture.wall.wood", "architecture.window.wood", "architecture.door.wood", "architecture.roof.palm" })
            {
                saved[id] = (cache.Contains(id), cache[id]);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
                Assert.That(prefab, Is.Not.Null, "Use imported production art, not a generated triangle fallback.");
                Assert.That(prefab.GetComponentsInChildren<MeshFilter>(true).Any(m => m.sharedMesh != null && m.sharedMesh.vertexCount > 0), Is.True);
                cache[id] = prefab;
            }
            foreach (var e in plan.Elements)
            {
                var m = modules[BlueprintBuildingPlan.SlotKey(e)];
                var global = ArchitectureWorldEditing.ToWorld(e, plan, destination, yaw);
                var id = e.Kind switch { BlueprintElementKind.Support => "architecture.support.wood", BlueprintElementKind.FloorSector => "architecture.floor.board",
                    BlueprintElementKind.Window => "architecture.window.wood", BlueprintElementKind.Door => "architecture.door.wood", BlueprintElementKind.RoofSector => "architecture.roof.palm", _ => "architecture.wall.wood" };
                var preview = BlueprintArchitectureFactory.Build(global, projected); preview.transform.SetParent(previewRoot.transform, true);
                var actual = new GameObject("Actual " + m.Key); actual.transform.SetParent(actualRoot.transform, false);
                var view = actual.AddComponent<ArchitectureModuleView>();
                var piece = new ObjectSnapshot { Id = new ObjectId(1000 + m.Index), DefinitionId = id, Tile = destination, RotationDegrees = yaw, ArchitectureOwnerObjectId = 900 };
                var state = new ArchitectureElementSnapshot { DefinitionId = id, SlotKey = m.Key, LocalX = m.LocalX, LocalZ = m.LocalZ, LocalYaw = m.LocalYaw,
                    RequiredSticks = m.Sticks, RequiredBoards = m.Boards, RequiredRope = m.Rope, RequiredLeaves = m.Leaves,
                    DeliveredSticks = m.Sticks, DeliveredBoards = m.Boards, DeliveredRope = m.Rope, DeliveredLeaves = m.Leaves,
                    Buildable = true, WorkRequired = 1, WorkDone = 1 };
                piece.ArchitectureElements.Add(state); view.Sync(piece);
                var actualBounds = BoundsOf(actual); var previewBounds = BoundsOf(preview);
                if (e.Kind is BlueprintElementKind.FloorSector or BlueprintElementKind.RoofSector)
                {
                    Assert.That(Vector3.Distance(actualBounds.center, previewBounds.center), Is.LessThan(.002f), m.Key + " centre");
                    Assert.That(Vector3.Distance(actualBounds.size, previewBounds.size), Is.LessThan(.002f), m.Key + " size");
                }
                // A queued paid wall remains visibly the same until the worker finishes.
                state.DemolitionPlanned = true; view.Sync(piece);
                Assert.That(Vector3.Distance(BoundsOf(actual).center, actualBounds.center), Is.LessThan(.0001f));
            }
            var a = BoundsOf(actualRoot); var b = BoundsOf(previewRoot);
            Assert.That(Vector3.Distance(a.center, b.center), Is.LessThan(.003f), "whole-house centre");
            Assert.That(Vector3.Distance(a.size, b.size), Is.LessThan(.003f), "whole-house size");
            previewRoot.transform.position += Vector3.right * 4.5f;
            foreach (var root in roots) foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 29;
            Capture(a.center + Vector3.right * 2.25f, yaw, roots);
        }
        finally
        {
            foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
            foreach (var p in saved) { if (p.Value.had) cache[p.Key] = p.Value.value; else cache.Remove(p.Key); }
        }
    }

    private static Bounds BoundsOf(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
        Assert.That(renderers, Is.Not.Empty);
        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers)
        {
            bounds.Encapsulate(renderer.bounds);
            Assert.That(renderer.sharedMaterials.All(m => m != null && m.shader != null && m.shader.name != "Hidden/InternalErrorShader"), Is.True);
        }
        Assert.That(float.IsNaN(bounds.size.x) || float.IsInfinity(bounds.size.x), Is.False);
        return bounds;
    }

    private static void Capture(Vector3 look, float yaw, List<GameObject> roots)
    {
        var cameraRoot = new GameObject("664 actual comparison camera"); roots.Add(cameraRoot);
        var camera = cameraRoot.AddComponent<Camera>(); camera.cullingMask = 1 << 29;
        camera.transform.position = look + new Vector3(7, 5, -9); camera.transform.LookAt(look);
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .16f, .20f);
        camera.fieldOfView = 38; camera.nearClipPlane = .03f; camera.farClipPlane = 40;
        var lightRoot = new GameObject("664 acceptance sun"); roots.Add(lightRoot);
        var light = lightRoot.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.4f; light.cullingMask = 1 << 29;
        lightRoot.transform.rotation = Quaternion.Euler(45, -30, 0);
        var target = new RenderTexture(1400, 900, 24); var pixels = new Texture2D(1400, 900, TextureFormat.RGB24, false); var old = RenderTexture.active;
        try
        {
            camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1400, 900), 0, 0); pixels.Apply();
            var folder = "Build/Diagnostics/bug-wave-20261110/construction-664"; Directory.CreateDirectory(folder);
            File.WriteAllBytes(folder + "/actual-vs-preview-yaw" + yaw + ".png", pixels.EncodeToPNG());
        }
        finally { camera.targetTexture = null; RenderTexture.active = old; Object.DestroyImmediate(pixels); Object.DestroyImmediate(target); }
    }
}
}
#endif
