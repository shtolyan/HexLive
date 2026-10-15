#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.UnityPresentation.Rendering;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// Uses the shipped body/equipment/variant path with local catalog injection only.
public static class PeopleWardrobeFitReview
{
    const string Root = PeopleAssetPreparation.Root;
    const string Output = "Build/MSIRelease/20261015-male-wardrobe/review";

    public static void Run()
    {
        if (!Application.isPlaying)
            throw new InvalidOperationException("Run PairedWardrobeRuntimeTests in graphics PlayMode: SyncWorn removal uses runtime Destroy");
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new InvalidOperationException("Graphics required for actual wardrobe review");
        Directory.CreateDirectory(Output);
        var records = ((JArray)JObject.Parse(File.ReadAllText(Root + "/catalog.json"))["records"])
            .Where(r => (string)r["type"] == "wear").ToArray();
        var definitions = records.ToDictionary(r => (string)r["id"], r =>
            AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)r["definition"]));
        var previous = GarmentLibrary.Active.ToArray();
        var cache = (IDictionary)typeof(ActorWardrobe).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var savedCache = new Dictionary<object, object>();
        foreach (DictionaryEntry entry in cache) savedCache[entry.Key] = entry.Value;
        var variants = (IDictionary)typeof(GarmentVariants).GetField("ById", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var savedVariants = new Dictionary<object, object>();
        foreach (DictionaryEntry entry in variants) savedVariants[entry.Key] = entry.Value;
        var oldAsync = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        var results = new JArray();
        var measured = new HashSet<string>();
        var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx")
            .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
        try
        {
            GarmentLibrary.Override(definitions.Values.Select(d => d.ToParams()));
            foreach (var pair in definitions) variants[pair.Key] = pair.Value;
            foreach (var row in records)
                cache[(string)row["id"]] = new List<Wear> { AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]).GetComponent<Wear>() };
            var scaleAudit = JObject.Parse(File.ReadAllText(Root + "/Validation/scale-audit.json"));
            var scaleRows = new JArray(((JArray)scaleAudit["rows"]).Where(r => (string)r["type"] != "wear").Select(r => r.DeepClone()));
            foreach (var row in records)
            foreach (bool hanging in new[] { false, true })
            {
                string id = (string)row["id"];
                var drop = hanging ? GarmentDropFactory.BuildHanging(id) : GarmentDropFactory.Build(id);
                try
                {
                    if (drop == null) throw new InvalidOperationException(id + ": no ground/hanging geometry");
                    drop.transform.localScale = Vector3.one * HexWorldRenderer.ActorScale;
                    if (!ObjectFit.WorldBounds(drop, out var bounds)) throw new InvalidOperationException(id + ": no bounds");
                    drop.transform.position -= Vector3.up * bounds.min.y; ObjectFit.WorldBounds(drop, out bounds);
                    float span = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                    if (span > 2 || span < .03f) throw new InvalidOperationException(id + ": ground size " + span);
                    scaleRows.Add(new JObject { ["type"] = "wear", ["id"] = id, ["prototypeId"] = definitions[id].ArtId,
                        ["hanging"] = hanging, ["source"] = (string)row["main"], ["bounds"] = new JObject {
                            ["min"] = new JArray(bounds.min.x, bounds.min.y, bounds.min.z),
                            ["max"] = new JArray(bounds.max.x, bounds.max.y, bounds.max.z),
                            ["size"] = new JArray(bounds.size.x, bounds.size.y, bounds.size.z) } });
                }
                finally { if (drop != null) UnityEngine.Object.DestroyImmediate(drop); }
            }
            foreach (ActorName actor in new[] { ActorName.Marta, ActorName.Kshishtof })
            foreach (var requested in records)
            {
                string id = (string)requested["id"], fitted = ActorWardrobe.FittedId(id, actor);
                var row = records.Single(r => (string)r["id"] == fitted);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"]);
                var wear = prefab.GetComponent<Wear>();
                if (wear.Gender != ActorSex.Of(actor)) throw new InvalidOperationException(id + ": wrong body fit");
                var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
                var graph = PlayableGraph.Create("PairedWardrobeRunAudit");
                try
                {
                    var bones = go.GetComponent<BodyBones>(); bones.Construct(actor, 476);
                    var animator = go.GetComponent<Animator>(); animator.enabled = false;
                    var briefs = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + actor + "_PrimalBriefs.prefab").GetComponent<Wear>();
                    if (!((string)row["metadata"]["artId"]).StartsWith("underwear.briefs_primal", StringComparison.Ordinal))
                        bones.Equip("baseline", briefs, null);
                    // Compare exact prefab vertices before/after the runtime bone stitch.
                    var loose = UnityEngine.Object.Instantiate(prefab);
                    Vector3[] before;
                    try { before = Capture(loose.GetComponentInChildren<SkinnedMeshRenderer>()); }
                    finally { UnityEngine.Object.DestroyImmediate(loose); }
                    // Exercise the actual snapshot-to-wardrobe path with the original item id.
                    var view = go.GetComponent<NpcActorView>() ?? go.AddComponent<NpcActorView>();
                    view.enabled = false;
                    typeof(NpcActorView).GetField("_bodyBones", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, bones);
                    typeof(NpcActorView).GetField("_actorMesh", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, actor);
                    view.SyncWorn(new[] { id });
                    var equipped = bones.GetWorn(id + "#0");
                    if (equipped == null) throw new InvalidOperationException(id + ": not equipped");
                    view.SyncWorn(new[] { id });
                    if (equipped != bones.GetWorn(id + "#0")) throw new InvalidOperationException(id + ": repeated snapshot rebuilt item");
                    var renderer = equipped.GetComponentInChildren<SkinnedMeshRenderer>();
                    if (!renderer.enabled) throw new InvalidOperationException(id + ": renderer disabled");
                    var after = Capture(renderer);
                    if (before.Length != after.Length) throw new InvalidOperationException("Vertex count changed");
                    float restError = 0;
                    for (int i = 0; i < after.Length; i++) restError = Mathf.Max(restError, Vector3.Distance(before[i], after[i]));
                    if (restError > .003f) throw new InvalidOperationException(actor + "/" + fitted + ": rest stitch error=" + restError);
                    string prototype = actor + "/" + (string)row["metadata"]["artId"];
                    int runSamples = 0;
                    if (measured.Add(prototype))
                    {
                        if (((string)row["metadata"]["artId"]).Contains("armwraps_primal"))
                        {
                            var weights = renderer.sharedMesh.GetAllBoneWeights();
                            float left = 0, right = 0;
                            foreach (var weight in weights)
                            {
                                string name = renderer.bones[weight.boneIndex].name;
                                if (name.StartsWith("l")) left += weight.weight;
                                if (name.StartsWith("r")) right += weight.weight;
                            }
                            if (left < 500 || right < 500 || Mathf.Abs(left-right) > Mathf.Max(left, right) * .03f)
                                throw new InvalidOperationException(actor + ": sleeve bone weights not paired");
                        }
                        animator.enabled = true; animator.runtimeAnimatorController = null;
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind();
                        var playable = AnimationClipPlayable.Create(graph, clip);
                        AnimationPlayableOutput.Create(graph, "run", animator).SetSourcePlayable(playable);
                        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
                        for (int sample = 0; sample < 5; sample++)
                        {
                            playable.SetTime(clip.length * sample / 5d); graph.Evaluate(0);
                            var points = Capture(renderer); var bounds = new Bounds(points[0], Vector3.zero);
                            foreach (var point in points)
                            {
                                if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
                                    throw new InvalidOperationException(id + ": non-finite skinned vertex");
                                bounds.Encapsulate(point);
                            }
                            if (bounds.size.magnitude > 5 || bounds.size.magnitude < .03f)
                                throw new InvalidOperationException(id + ": invalid running bounds " + bounds);
                            runSamples++;
                        }
                        graph.Stop(); animator.enabled = false;
                    }
                    // A view of every colour for its own body, plus close front/back views of the changed pieces.
                    if ((string)row["id"] == id)
                    {
                        Render(go, Output + "/" + actor + "_" + id + ".png", false);
                        if (fitted == "clothing.top_primal_male" || fitted == "clothing.armwraps_primal_male" || fitted == "clothing.collar_fur_male" || fitted == "clothing.armwraps_primal")
                            Render(go, Output + "/" + actor + "_" + id + "_back.png", true);
                    }
                    results.Add(new JObject { ["actor"] = actor.ToString(), ["requested"] = id, ["fit"] = fitted,
                        ["restStitchMaxErrorMetres"] = restError, ["runSamples"] = runSamples, ["rendered"] = renderer.enabled,
                        ["snapshotSyncVerified"] = true });
                    view.SyncWorn(Array.Empty<string>());
                    if (bones.GetWorn(id + "#0") != null) throw new InvalidOperationException(id + ": removal snapshot left garment on body");
                }
                finally { if (graph.IsValid()) graph.Destroy(); UnityEngine.Object.DestroyImmediate(go); }
                if (results.Count % 20 == 0) Debug.Log("WARDROBE_AUDIT_PROGRESS " + results.Count + "/164");
            }
            if (results.Count != 164 || measured.Count != 22) throw new InvalidOperationException("Incomplete wardrobe audit");
            scaleAudit["rows"] = scaleRows; scaleAudit["garmentsRemeasuredForBug476"] = records.Length;
            File.WriteAllText(Root + "/Validation/scale-audit.json", scaleAudit.ToString());
            File.WriteAllText(Output + "/audit.json", new JObject { ["passed"] = true, ["cases"] = results,
                ["distinctFits"] = measured.Count, ["runSamples"] = results.Sum(r => (int)r["runSamples"]),
                ["bodyClippingAutomaticallyProven"] = false, ["bundleOrPlayerBuilt"] = false }.ToString());
            Debug.Log("PAIRED_WARDROBE_AUDIT PASS: 164 item/body cases,22 fits,110 running samples");
        }
        finally
        {
            GarmentLibrary.Override(previous); variants.Clear();
            cache.Clear(); foreach (var pair in savedCache) cache[pair.Key] = pair.Value;
            foreach (var pair in savedVariants) variants[pair.Key] = pair.Value;
            ShaderUtil.allowAsyncCompilation = oldAsync;
        }
    }

    static Vector3[] Capture(SkinnedMeshRenderer renderer)
    {
        var mesh = new Mesh();
        try { renderer.BakeMesh(mesh, true); return mesh.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
        finally { UnityEngine.Object.DestroyImmediate(mesh); }
    }

    static void Render(GameObject go, string path, bool back)
    {
        var preview = new PreviewRenderUtility(); var meshes = new List<Mesh>();
        try
        {
            var draws = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials)>();
            foreach (var renderer in go.GetComponentsInChildren<SkinnedMeshRenderer>(false).Where(r => r.enabled))
            {
                var mesh = new Mesh(); renderer.BakeMesh(mesh, true); meshes.Add(mesh);
                draws.Add((mesh, renderer.transform.localToWorldMatrix, renderer.sharedMaterials));
            }
            var camera = preview.camera; camera.orthographic = true; camera.orthographicSize = 1.05f;
            camera.transform.position = new Vector3(back ? -2 : 2, 1.4f, back ? -5 : 5);
            camera.transform.LookAt(new Vector3(0, .9f, 0)); camera.nearClipPlane = .01f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .14f, .16f);
            preview.lights[0].intensity = 1.15f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 180, 0);
            preview.lights[1].intensity = .6f; preview.lights[1].transform.rotation = Quaternion.Euler(-15, -30, 0);
            for (int pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0, 0, 512, 640));
                foreach (var draw in draws)
                    for (int sub = 0; sub < draw.mesh.subMeshCount; sub++) preview.DrawMesh(draw.mesh, draw.matrix, draw.materials[sub], sub);
                camera.Render(); var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(path, image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
        finally { foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh); preview.Cleanup(); }
    }
}
}
#endif
