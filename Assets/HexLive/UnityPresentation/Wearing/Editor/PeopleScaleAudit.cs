#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.UnityPresentation.Rendering;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// Uses the game factories. Cache injection supplies authored inputs only;
// measurement, sleeve baking, variant selection and hand fitting are runtime code.
public static class PeopleScaleAudit
{
    private const string Root = PeopleAssetPreparation.Root;
    private static JArray Vec(Vector3 v) => new JArray(v.x, v.y, v.z);
    private static JObject Box(Bounds b) => new JObject { ["min"] = Vec(b.min), ["max"] = Vec(b.max), ["size"] = Vec(b.size) };
    private static float Span(Bounds b) => Mathf.Max(b.size.x, b.size.y, b.size.z);
    public static void RenderReview()
    {
        var objects = new List<GameObject>();
        var generated = new List<Mesh>();
        var preview = new PreviewRenderUtility();
        var oldAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        var cache = (IDictionary)typeof(ActorWardrobe).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var variants = (IDictionary)typeof(GarmentVariants).GetField("ById", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var savedCache = new Dictionary<object, object>(); var savedVariants = new Dictionary<object, object>();
        foreach (DictionaryEntry v in cache) savedCache[v.Key] = v.Value;
        foreach (DictionaryEntry v in variants) savedVariants[v.Key] = v.Value;
        try
        {
            var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
            foreach (var row in catalog["records"].Where(r => (string)r["type"] == "wear"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
                cache[(string)row["id"]] = new List<Wear> { prefab.GetComponent<Wear>() };
                variants[(string)row["id"]] = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
            }
            int index = 0;
            foreach (var actor in new[] { "Marta", "Kshishtof" })
            {
                var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
                objects.Add(go); go.GetComponent<Animator>().enabled = false;
                var bones = go.GetComponent<BodyBones>(); bones.Construct((ActorName)Enum.Parse(typeof(ActorName), actor), 169);
                foreach (var item in actor == "Marta" ? new[] { "PrimalBriefs", "PrimalTop", "PrimalWrapBoots" } : new[] { "PrimalBriefs", "PrimalWrapBoots" })
                    bones.Equip(item, AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + actor + "_" + item + ".prefab").GetComponent<Wear>(), null);
                go.transform.position = new Vector3(-1.2f + index * 2.1f, 0, 1.3f);
                go.transform.localScale = Vector3.one * HexWorldRenderer.ActorScale; index++;
            }
            index = 0;
            foreach (var id in new[] { "clothing.wrapboots_primal", "underwear.briefs_primal", "clothing.top_primal", "clothing.wrapboots_primal_male", "underwear.briefs_primal_male", "gear.backpack_primal_hunter" })
            {
                var drop = GarmentDropFactory.Build(id); if (drop == null) throw new InvalidOperationException(id);
                objects.Add(drop); drop.transform.localScale = Vector3.one * HexWorldRenderer.ActorScale;
                ObjectFit.WorldBounds(drop, out var b);
                drop.transform.position = new Vector3(-1.5f + (index % 3) * 1.25f, -b.min.y, .0f - (index / 3) * .7f); index++;
            }
            var args = System.Environment.GetCommandLineArgs();
            var inventory = JObject.Parse(File.ReadAllText(args[Array.IndexOf(args, "-people-scale-inventory") + 1]));
            var axeRow = inventory["records"].Single(r => (string)r["id"] == "tool.axe_stone" && (string)r["type"] == "object");
            var axe = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>((string)axeRow["main"]));
            objects.Add(axe); axe.transform.localScale *= ObjectFit.FitScaleFactor(axe, "tool.axe_stone");
            axe.transform.rotation = Quaternion.Euler(90, 0, 0); ObjectFit.WorldBounds(axe, out var axeBounds);
            axe.transform.position = new Vector3(2.0f, -axeBounds.min.y, .1f);
            var draws = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials)>();
            foreach (var go in objects)
            {
                foreach (var skin in go.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    var mesh = new Mesh(); skin.BakeMesh(mesh, true); generated.Add(mesh);
                    draws.Add((mesh, skin.transform.localToWorldMatrix, skin.sharedMaterials));
                }
                foreach (var filter in go.GetComponentsInChildren<MeshFilter>(false))
                {
                    var r = filter.GetComponent<MeshRenderer>(); if (r != null) draws.Add((filter.sharedMesh, filter.transform.localToWorldMatrix, r.sharedMaterials));
                }
            }
            var camera = preview.camera; camera.orthographic = true; camera.orthographicSize = 2.25f;
            camera.transform.position = new Vector3(3f, 4f, -7f); camera.transform.LookAt(new Vector3(0, .45f, .3f));
            camera.nearClipPlane = .01f; camera.farClipPlane = 30f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f,.14f,.16f);
            preview.lights[0].intensity = 1.15f; preview.lights[0].transform.rotation = Quaternion.Euler(35f, 180f, 0f);
            preview.lights[1].intensity = .55f; preview.lights[1].transform.rotation = Quaternion.Euler(-15f,-30f,0f);
            Directory.CreateDirectory(Root + "/Validation/Review");
            for (int pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0,0,1280,960));
                foreach (var d in draws) for (int m = 0; m < d.mesh.subMeshCount; m++) preview.DrawMesh(d.mesh,d.matrix,d.materials[m],m);
                camera.Render(); var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(Root + "/Validation/Review/scale-review.png", image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
        finally
        {
            cache.Clear(); variants.Clear();
            foreach (var v in savedCache) cache[v.Key] = v.Value;
            foreach (var v in savedVariants) variants[v.Key] = v.Value;
            foreach (var go in objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            foreach (var mesh in generated) UnityEngine.Object.DestroyImmediate(mesh);
            preview.Cleanup(); ShaderUtil.allowAsyncCompilation = oldAsync;
        }
    }

    public static void Run()
    {
        var rows = new JArray();
        var failures = new JArray();
        var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var cache = (IDictionary)typeof(ActorWardrobe).GetField("Cache", flags).GetValue(null);
        var variants = (IDictionary)typeof(GarmentVariants).GetField("ById", flags).GetValue(null);
        foreach (var row in catalog["records"].Where(r => (string)r["type"] == "wear"))
        {
            string id = (string)row["id"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
            var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
            var saved = cache[id]; var savedDef = variants[id];
            cache[id] = new List<Wear> { prefab.GetComponent<Wear>() }; variants[id] = def;
            try
            {
                foreach (var hanging in new[] { false, true })
                {
                    var drop = hanging ? GarmentDropFactory.BuildHanging(id) : GarmentDropFactory.Build(id);
                    try
                    {
                        if (drop == null) throw new InvalidOperationException("Factory returned null");
                        drop.transform.localScale = Vector3.one * HexWorldRenderer.ActorScale;
                        if (!ObjectFit.WorldBounds(drop, out var bounds)) throw new InvalidOperationException("No geometry");
                        drop.transform.position -= Vector3.up * bounds.min.y;
                        ObjectFit.WorldBounds(drop, out bounds);
                        if (Span(bounds) > 2f || Span(bounds) < .03f) throw new InvalidOperationException("Implausible scale: " + bounds);
                        rows.Add(new JObject { ["type"] = "wear", ["id"] = id, ["prototypeId"] = def.ArtId,
                            ["hanging"] = hanging, ["bounds"] = Box(bounds), ["source"] = (string)row["main"] });
                    }
                    finally { if (drop != null) UnityEngine.Object.DestroyImmediate(drop); }
                }
            }
            catch (Exception e) { failures.Add(id + ": " + e.Message); }
            finally
            {
                if (saved == null) cache.Remove(id); else cache[id] = saved;
                if (savedDef == null) variants.Remove(id); else variants[id] = savedDef;
            }
        }
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
            try
            {
                var view = go.AddComponent<NpcActorView>(); view.enabled = false;
                foreach (var zone in new[] { "ArmL", "ArmR", "LegL", "LegR" })
                {
                    var limb = SeveredLimbFactory.BuildReference(view, zone);
                    try
                    {
                        if (limb == null) throw new InvalidOperationException(actor + "/" + zone + " no reference limb");
                        limb.transform.localScale = Vector3.one * HexWorldRenderer.ActorScale;
                        ObjectFit.WorldBounds(limb, out var bounds);
                        if (Span(bounds) > 1f || Span(bounds) < .1f) throw new InvalidOperationException(actor + "/" + zone + " " + bounds);
                        rows.Add(new JObject { ["type"] = "limb", ["id"] = actor + "/" + zone, ["bounds"] = Box(bounds) });
                    }
                    finally { if (limb != null) UnityEngine.Object.DestroyImmediate(limb); }
                }
            }
            catch (Exception e) { failures.Add(e.Message); }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        var args = System.Environment.GetCommandLineArgs();
        int arg = Array.IndexOf(args, "-people-scale-inventory");
        if (arg < 0 || arg + 1 >= args.Length) throw new InvalidOperationException("Pass the actual package inventory with -people-scale-inventory");
        var inventory = JObject.Parse(File.ReadAllText(args[arg + 1]));
        foreach (var row in inventory["records"].Where(r => (string)r["type"] == "object"))
        {
            string id = (string)row["id"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
            if (prefab == null) { failures.Add(id + ": missing prefab " + (string)row["main"]); continue; }
            var go = UnityEngine.Object.Instantiate(prefab);
            try
            {
                if (!ObjectFit.HasRenderableGeometry(go)) throw new InvalidOperationException("No renderable geometry");
                float target = ObjectFit.TargetWorldSize(id);
                go.transform.localScale *= ObjectFit.FitScaleFactor(go, id);
                ObjectFit.TryMeasuredSize(go, id, out var fitted);
                if (!float.IsFinite(fitted) || Mathf.Abs(fitted - target) > .001f) throw new InvalidOperationException("Ground fit " + fitted + " != " + target);
                var report = new JObject { ["type"] = "object", ["id"] = id, ["source"] = (string)row["main"], ["target"] = target, ["groundFitted"] = fitted };
                if (id.StartsWith("tool.", StringComparison.Ordinal))
                {
                    var parent = new GameObject("hand-audit");
                    try
                    {
                        parent.transform.SetPositionAndRotation(new Vector3(12, 1, -7), Quaternion.Euler(11, 73, 22));
                        parent.transform.localScale = Vector3.one * .007764706f; // actual imported hand scale
                        go.transform.SetParent(parent.transform, false);
                        go.transform.localScale = prefab.transform.localScale;
                        go.transform.localRotation = Quaternion.Euler(27, 69, 11);
                        typeof(HandPropVisual).GetMethod("ApplyObjectFitScale", BindingFlags.NonPublic | BindingFlags.Static)
                            .Invoke(null, new object[] { go, id, prefab.transform.localScale, Vector3.one });
                        ObjectFit.TryMeasuredSize(go, id, out var hand);
                        if (Mathf.Abs(hand - target) > .001f) throw new InvalidOperationException("Hand fit " + hand + " != " + target);
                        report["handFitted"] = hand;
                    }
                    finally { go.transform.SetParent(null); UnityEngine.Object.DestroyImmediate(parent); }
                }
                rows.Add(report);
            }
            catch (Exception e) { failures.Add(id + ": " + e.Message); }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        File.WriteAllText(Root + "/Validation/scale-audit.json", new JObject {
            ["passed"] = failures.Count == 0, ["actorScale"] = HexWorldRenderer.ActorScale,
            ["rows"] = rows, ["errors"] = failures }.ToString());
        if (failures.Count > 0) throw new InvalidOperationException(failures.ToString());
        Debug.Log("PEOPLE_SCALE_AUDIT_PASSED " + rows.Count);
    }
}
}
#endif
