#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.HutTest.BlueprintEditor;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class WorldHouseGeometryRuntimeTests
{
    [Test]
    public void LegacyHutSiteAndCampfireShareOneFloorSurface()
    {
        var root = new GameObject("Floor support test");
        try
        {
            var renderer = root.AddComponent<HexWorldRenderer>();
            var tile = new TileCoord(0, 0);
            var floorTiles = (HashSet<TileCoord>)typeof(HexWorldRenderer)
                .GetField("_floorTiles", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
            floorTiles.Add(tile);

            var site = new ObjectSnapshot { Tile = tile, DefinitionId = ContentIds.BuildSite, BuildProduct = ContentIds.Hut1Hex };
            var campfire = new ObjectSnapshot { Tile = tile, DefinitionId = ContentIds.Campfire };
            var campfireSite = new ObjectSnapshot { Tile = tile, DefinitionId = ContentIds.BuildSite, BuildProduct = ContentIds.Campfire };
            var ground = renderer.GroundTopY(tile);
            var floor = ground + HutAssembly.FloorSurfaceLift;

            Assert.That(renderer.ObjectGroundTopY(site), Is.EqualTo(ground));
            Assert.That(renderer.ObjectGroundTopY(campfire), Is.EqualTo(floor));
            Assert.That(renderer.ObjectGroundTopY(campfireSite), Is.EqualTo(floor));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [TestCase(0)] [TestCase(60)] [TestCase(120)] [TestCase(180)] [TestCase(240)] [TestCase(300)]
    public void ActualImportedModulesAndWorldEditorProjectionShareSixWayGeometry(float yaw)
    {
        var cache = (IDictionary)typeof(BlueprintArchitectureFactory).GetField("ModelPrefabs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var saved = new Dictionary<string, (bool had, object value)>();
        var roots = new List<GameObject>();
        var plan = BuiltInBuildingBlueprints.Hut1Hex(); var destination = new TileCoord(-6, -26);
        var snapshot = new WorldSnapshot();
        snapshot.Objects.Add(new ObjectSnapshot { Id = new ObjectId(900), Tile = destination, RotationDegrees = yaw,
            BuildingBlueprintJson = BuildingBlueprintJson.Serialize(plan, false) });
        var projected = Project(snapshot);
        var context = BlueprintArchitectureRenderContext.World(snapshot); context.Prepare(projected);
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
                var global = projected.Elements.Single(p => p.Id == "house_900_" + e.Id);
                var id = e.Kind switch { BlueprintElementKind.Support => "architecture.support.wood", BlueprintElementKind.FloorSector => "architecture.floor.board",
                    BlueprintElementKind.Window => "architecture.window.wood", BlueprintElementKind.Door => "architecture.door.wood", BlueprintElementKind.RoofSector => "architecture.roof.palm", _ => "architecture.wall.wood" };
                var preview = BlueprintArchitectureFactory.BuildInContext(global, projected, context); preview.transform.SetParent(previewRoot.transform, true);
                var actual = new GameObject("Actual " + m.Key); actual.transform.SetParent(actualRoot.transform, false);
                var view = actual.AddComponent<ArchitectureModuleView>();
                var piece = new ObjectSnapshot { Id = new ObjectId(1000 + m.Index), DefinitionId = id, Tile = destination, RotationDegrees = yaw, ArchitectureOwnerObjectId = 900, IsDoorOpen = true };
                var state = new ArchitectureElementSnapshot { DefinitionId = id, SlotKey = m.Key, LocalX = m.LocalX, LocalZ = m.LocalZ, LocalYaw = m.LocalYaw,
                    RequiredSticks = m.Sticks, RequiredBoards = m.Boards, RequiredRope = m.Rope, RequiredLeaves = m.Leaves,
                    DeliveredSticks = m.Sticks, DeliveredBoards = m.Boards, DeliveredRope = m.Rope, DeliveredLeaves = m.Leaves,
                    Buildable = true, WorkRequired = 1, WorkDone = 1 };
                piece.ArchitectureElements.Add(state); view.Sync(piece);
                var actualBounds = BoundsOf(actual); var previewBounds = BoundsOf(preview);
                Assert.That(Vector3.Distance(actualBounds.center, previewBounds.center), Is.LessThan(.002f), m.Key + " centre");
                Assert.That(Vector3.Distance(actualBounds.size, previewBounds.size), Is.LessThan(.002f), m.Key + " size");
                for (var stage = 1; stage <= 3; stage++)
                    Assert.That(BlueprintArchitectureFactory.ResourceCount(preview, stage),
                        Is.EqualTo(BlueprintArchitectureFactory.ResourceCount(actual, stage)), m.Key + " stage units " + stage);
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

    private static BuildingBlueprintDraft Project(WorldSnapshot snapshot) =>
        (BuildingBlueprintDraft)typeof(BuildModePanel).GetMethod("FreeWorldProjection", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { snapshot });

    [TestCase(0)] [TestCase(60)] [TestCase(120)] [TestCase(180)] [TestCase(240)] [TestCase(300)]
    public void AddingARemoteHouseCannotChangeNearHouseModelPosesOrGeometry(float yaw)
    {
        var cache = (IDictionary)typeof(BlueprintArchitectureFactory).GetField("ModelPrefabs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var saved = new Dictionary<string, (bool had, object value)>(); var roots = new List<GameObject>();
        var near = new TileCoord(-6, -26); var far = new TileCoord(1, -26);
        var source = BuiltInBuildingBlueprints.Hut1Hex(); var json = BuildingBlueprintJson.Serialize(source, false);
        var first = new WorldSnapshot(); first.Objects.Add(new ObjectSnapshot { Id = new ObjectId(900), Tile = near, RotationDegrees = yaw, BuildingBlueprintJson = json });
        var both = new WorldSnapshot(); both.Objects.Add(first.Objects[0]);
        both.Objects.Add(new ObjectSnapshot { Id = new ObjectId(901), Tile = far, RotationDegrees = yaw, BuildingBlueprintJson = json });
        var before = Project(first); var after = Project(both);
        Func<TileCoord,float> elevation = tile => tile == near ? .25f : 1f;
        var beforeContext = BlueprintArchitectureRenderContext.World(first, elevation); beforeContext.Prepare(before);
        var afterContext = BlueprintArchitectureRenderContext.World(both, elevation); afterContext.Prepare(after);
        try
        {
            foreach (var id in new[] { "architecture.support.wood", "architecture.floor.board", "architecture.wall.wood", "architecture.window.wood", "architecture.door.wood", "architecture.roof.palm" })
            {
                saved[id] = (cache.Contains(id), cache[id]);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
                Assert.That(prefab, Is.Not.Null); cache[id] = prefab;
            }
            foreach (var element in before.Elements)
            {
                var other = after.Elements.Single(e => e.Id == element.Id);
                var a = BlueprintArchitectureFactory.BuildInContext(element, before, beforeContext); roots.Add(a);
                var b = BlueprintArchitectureFactory.BuildInContext(other, after, afterContext); roots.Add(b);
                Assert.That(Vector3.Distance(a.transform.position, b.transform.position), Is.LessThan(.0001f), element.Id);
                Assert.That(Quaternion.Angle(a.transform.rotation, b.transform.rotation), Is.LessThan(.05f), element.Id);
                Assert.That(Vector3.Distance(BoundsOf(a).center, BoundsOf(b).center), Is.LessThan(.001f), element.Id);
                Assert.That(Vector3.Distance(BoundsOf(a).size, BoundsOf(b).size), Is.LessThan(.001f), element.Id);
                var expectedLift = element.Kind == BlueprintElementKind.RoofSector ? BlueprintArchitectureFactory.RoofEaveHeight : 0f;
                Assert.That(a.transform.position.y, Is.EqualTo(.25f + expectedLift).Within(.0001f), "Own terrain frame stays independent too.");
            }
        }
        finally
        {
            foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
            foreach (var item in saved) { if (item.Value.had) cache[item.Key] = item.Value.value; else cache.Remove(item.Key); }
        }
    }

    [TestCase(false)] [TestCase(true)]
    public void ReusedLocalEditorContextKeepsAddedAndMovedSupportOnTheActualModuleGeometry(bool moveExisting)
    {
        var cache = (IDictionary)typeof(BlueprintArchitectureFactory).GetField("ModelPrefabs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var saved = new Dictionary<string, (bool had, object value)>(); var roots = new List<GameObject>();
        var draft = BuiltInBuildingBlueprints.Hut1Hex(); draft.Furniture.Clear();
        var anchor = BlueprintBuildingPlan.AnchorTile(draft);
        var context = BlueprintArchitectureRenderContext.Local(draft);
        var destination = new TileCoord(-6, -26); const float yaw = 120f;
        var rotation = Quaternion.Euler(0f, SimulationUnityMapper.ToUnityFootprintYawDegrees(yaw), 0f);
        var anchorWorld = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(anchor));
        var editorRoot = new GameObject("664 reused local constructor"); roots.Add(editorRoot);
        editorRoot.transform.SetPositionAndRotation(SimulationUnityMapper.ToUnityTilePosition(destination)
            - rotation * new Vector3(anchorWorld.X, 0f, anchorWorld.Y), rotation);
        var editor = editorRoot.AddComponent<BlueprintPreviewRenderer>();
        var actualRoot = new GameObject("664 actual edited local module"); roots.Add(actualRoot);
        actualRoot.transform.position = SimulationUnityMapper.ToUnityTilePosition(destination);
        try
        {
            foreach (var id in new[] { "architecture.support.wood", "architecture.floor.board", "architecture.wall.wood", "architecture.window.wood", "architecture.door.wood", "architecture.roof.palm" })
            {
                saved[id] = (cache.Contains(id), cache[id]);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
                Assert.That(prefab, Is.Not.Null); cache[id] = prefab;
            }
            editor.Rebuild(draft, BlueprintEditorMode.Architecture, architectureContext: context);
            var support = moveExisting ? draft.Elements.First(e => e.Kind == BlueprintElementKind.Support)
                : new BlueprintElementData { Id = "new_local_support", Kind = BlueprintElementKind.Support };
            support.Node = BlueprintGeometry.HexCorner(new TileCoord(1, 0), 0);
            if (!moveExisting) draft.Elements.Add(support);
            // Same draft and SAME context after a real renderer rebuild: no
            // stale initial-slot cache and no fallback preview-only factory.
            editor.Rebuild(draft, BlueprintEditorMode.Architecture, architectureContext: context);
            Assert.That(editor.TryGetObject(support.Id, out var preview), Is.True);
            var authoritative = draft.Clone(); authoritative.HasAnchor = true;
            authoritative.AnchorQ = anchor.Q; authoritative.AnchorR = anchor.R;
            var module = BlueprintBuildingPlan.Modules(authoritative).Single(m => m.Key == BlueprintBuildingPlan.SlotKey(support));
            var view = actualRoot.AddComponent<ArchitectureModuleView>();
            var piece = new ObjectSnapshot { Id = new ObjectId(1001), DefinitionId = "architecture.support.wood", Tile = destination,
                RotationDegrees = yaw, ArchitectureOwnerObjectId = 900 };
            piece.ArchitectureElements.Add(new ArchitectureElementSnapshot { DefinitionId = piece.DefinitionId, SlotKey = module.Key,
                LocalX = module.LocalX, LocalZ = module.LocalZ, LocalYaw = module.LocalYaw,
                RequiredSticks = module.Sticks, DeliveredSticks = module.Sticks, RequiredRope = module.Rope, DeliveredRope = module.Rope,
                Buildable = true, WorkRequired = 1, WorkDone = 1 });
            view.Sync(piece);
            var actual = BoundsOf(actualRoot); var shown = BoundsOf(preview);
            Assert.That(Vector3.Distance(actual.center, shown.center), Is.LessThan(.002f), "actual local edit centre");
            Assert.That(Vector3.Distance(actual.size, shown.size), Is.LessThan(.002f), "actual local edit size");
            for (var stage = 1; stage <= 3; stage++)
                Assert.That(BlueprintArchitectureFactory.ResourceCount(preview, stage),
                    Is.EqualTo(BlueprintArchitectureFactory.ResourceCount(actualRoot, stage)), "local edit stage units " + stage);
        }
        finally
        {
            foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
            foreach (var item in saved) { if (item.Value.had) cache[item.Key] = item.Value.value; else cache.Remove(item.Key); }
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
