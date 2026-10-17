#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Local, read-only source inspection. Never builds or publishes content.
public static class PeopleEyeSurfaceAudit
{
    private static PreviewRenderUtility _preview;
    public static System.Collections.IEnumerator CaptureRuntime()
    {
        _preview = new PreviewRenderUtility();
        try { yield return CaptureAll(); }
        finally { _preview.Cleanup(); _preview = null; }
    }

    private static System.Collections.IEnumerator CaptureAll()
    {
        const string root = "Assets/HexLiveContent/People";
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "-eye-audit-output");
        var output = i >= 0 ? args[i + 1] : "Build/PeopleEyeReview";
        Directory.CreateDirectory(output);
        var report = new JArray();
        foreach (var name in new[] { "Marta", "Kshishtof" })
        {
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Prefabs/Actors/" + name + ".prefab"));
            try
            {
                actor.GetComponent<Animator>().enabled = false;
                var bones = actor.GetComponent<BodyBones>();
                bones.Construct((ActorName)Enum.Parse(typeof(ActorName), name), 482);
                if (!ActorBodyResolver.TryResolve(actor, out var body, out var error)) throw new InvalidOperationException(error);
                var mesh = body.sharedMesh;
                File.WriteAllText(output + "/" + name + "-mesh.json", new JObject {
                    ["vertices"] = new JArray(mesh.vertices.Select(v => new JArray(v.x, v.y, v.z))),
                    ["uv"] = new JArray(mesh.uv.Select(v => new JArray(v.x, v.y))),
                    ["triangles"] = new JArray(mesh.triangles),
                    ["skinTriangles"] = new JArray(mesh.GetTriangles(0)),
                    ["submeshes"] = new JArray(Enumerable.Range(0, mesh.subMeshCount).Select(s => new JArray(mesh.GetTriangles(s))))
                }.ToString());
                var originals = body.sharedMaterials;
                var view = actor.AddComponent<NpcActorView>(); view.enabled = false;
                typeof(NpcActorView).GetField("_bodySkins", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(view, actor.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                foreach (var donor in name == "Marta" ? new[] { "Marta", "Molly", "Jana", "Jolly", "Masha", "Rita" } : new[] { "Kshishtof", "Tonny" })
                {
                    var replacements = actor.GetComponent<PeopleAppearance>().SkinMaterials(donor);
                    body.sharedMaterials = originals.Select(m => replacements.TryGetValue(m.name, out var value) ? value : m).ToArray();
                    typeof(NpcActorView).GetMethod("BuildSkinTintTargets", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
                    view.SetSkinWeathering(0, 0);
                    var head = actor.GetComponentsInChildren<Transform>(true).First(t => t.name == "head");
                    foreach (var yaw in new[] { 0f, 45f })
                        yield return Render(actor, new Bounds(head.position + Vector3.up * .06f, new Vector3(.28f, .29f, .2f)), yaw,
                            output + "/" + name + "-" + donor + "-face-" + yaw + ".png");
                    var catalog = new FaceExpressionCatalog();
                    catalog.AddCustom("closed", "closed", new[] { ("eCTRLEyesClosedL", 100f), ("eCTRLEyesClosedR", 100f) });
                    var rig = new FaceExpressionRig(actor.GetComponentsInChildren<SkinnedMeshRenderer>(true), catalog);
                    rig.Apply(0, 1);
                    yield return Render(actor, new Bounds(head.position + Vector3.up * .06f, new Vector3(.28f, .29f, .2f)), 0,
                        output + "/" + name + "-" + donor + "-closed.png");
                    rig.Clear();
                    foreach (var garment in new[] { "PrimalBriefs", "PrimalTop" })
                        bones.Equip("eye-audit-" + garment, AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Prefabs/Wear/" + name + "_" + garment + ".prefab").GetComponent<Wear>(), null);
                    foreach (var yaw in new[] { 0f, 90f, 180f, 270f })
                        yield return Render(actor, new Bounds(actor.transform.position + Vector3.up * .95f, new Vector3(.9f, 1.85f, .6f)), yaw,
                            output + "/" + name + "-" + donor + "-skin-" + yaw + ".png");
                    foreach (var weather in new[] { "tan", "sunburn" })
                    {
                        view.SetSkinWeathering(1, weather == "sunburn" ? 1 : 0);
                        yield return Render(actor, new Bounds(head.position + Vector3.up * .06f, new Vector3(.28f, .29f, .2f)), 0,
                            output + "/" + name + "-" + donor + "-" + weather + ".png");
                    }
                    view.SetSkinWeathering(0, 0);
                    report.Add(new JObject { ["body"] = name, ["skin"] = donor,
                        ["surfaces"] = new JArray(body.sharedMaterials.Select((m, slot) => new JObject {
                            ["slot"] = slot, ["triangles"] = mesh.GetTriangles(slot).Length / 3,
                            ["material"] = AssetDatabase.GetAssetPath(m), ["shader"] = m.shader.name,
                            ["texture"] = AssetDatabase.GetAssetPath(m.GetTexture("_BaseMap")),
                            ["alphaClip"] = m.GetFloat("_AlphaClip"), ["surface"] = m.GetFloat("_Surface") })) });
                }
                body.sharedMaterials = originals;
            }
            finally { Object.DestroyImmediate(actor); }
        }
        File.WriteAllText(output + "/surfaces.json", report.ToString());
        yield return CaptureLashContribution(output);
        Debug.Log("[PeopleEyeSurfaceAudit] Captured all eight selectable skins, both bodies. " + output);
    }

    private static System.Collections.IEnumerator CaptureLashContribution(string output)
    {
        const string root = "Assets/HexLiveContent/People";
        var report = new JArray();
        foreach (var name in new[] { "Marta", "Kshishtof" })
        {
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Prefabs/Actors/" + name + ".prefab"));
            try
            {
                actor.GetComponent<Animator>().enabled = false;
                var bones = actor.GetComponent<BodyBones>(); bones.Construct((ActorName)Enum.Parse(typeof(ActorName), name), 482);
                var lashes = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "AuthoredEyelashes");
                var eyeBones = actor.GetComponentsInChildren<Transform>(true).Where(t => t.name == "lEye" || t.name == "rEye").ToArray();
                var centre = (eyeBones[0].position + eyeBones[1].position) / 2;
                var catalog = new FaceExpressionCatalog();
                catalog.AddCustom("closed", "closed", new[] { ("eCTRLEyesClosedL", 100f), ("eCTRLEyesClosedR", 100f) });
                var rig = new FaceExpressionRig(actor.GetComponentsInChildren<SkinnedMeshRenderer>(true), catalog);
                foreach (var closed in new[] { false, true })
                {
                    rig.Apply(0, closed ? 1 : 0);
                    ActorBodyResolver.TryResolve(actor, out var body, out _);
                    var head = actor.GetComponentsInChildren<Transform>(true).Single(t => t.name == "head");
                    report.Add(new JObject { ["actor"] = name, ["pose"] = closed ? "closed" : "open",
                        ["head"] = new JArray(head.position.x, head.position.y, head.position.z),
                        ["eyes"] = new JArray(eyeBones.Select(b => new JArray(b.position.x, b.position.y, b.position.z))),
                        ["body"] = DescribeSkin(body), ["lashes"] = DescribeSkin(lashes) });
                    foreach (var enabled in new[] { true, false })
                    {
                        lashes.enabled = enabled;
                        yield return Render(actor, new Bounds(centre, new Vector3(.14f, .08f, .10f)), 0,
                            output + "/" + name + "-lash-" + (closed ? "closed" : "open") + (enabled ? "-ON.png" : "-OFF.png"));
                    }
                }
                var material = lashes.sharedMaterial;
                var bright = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                bright.SetColor("_BaseColor", Color.magenta); bright.SetFloat("_Cull", 0);
                try
                {
                    rig.Clear(); lashes.enabled = true; lashes.sharedMaterial = bright;
                    yield return Render(actor, new Bounds(centre, new Vector3(.14f, .08f, .10f)), 0,
                        output + "/" + name + "-lash-open-DEBUG.png");
                }
                finally { lashes.sharedMaterial = material; Object.DestroyImmediate(bright); }
            }
            finally { Object.DestroyImmediate(actor); }
        }
        File.WriteAllText(output + "/lash-contribution.json", report.ToString());
    }


    private static JObject DescribeSkin(SkinnedMeshRenderer skin)
    {
        var mesh = skin.sharedMesh; var vertices = mesh.vertices;
        for (var shape = 0; shape < mesh.blendShapeCount; shape++)
        {
            var weight = skin.GetBlendShapeWeight(shape); if (Mathf.Abs(weight) < .001f) continue;
            var frame = mesh.GetBlendShapeFrameCount(shape) - 1;
            var delta = new Vector3[vertices.Length]; mesh.GetBlendShapeFrameVertices(shape, frame, delta, null, null);
            var scale = weight / mesh.GetBlendShapeFrameWeight(shape, frame);
            for (var v = 0; v < vertices.Length; v++) vertices[v] += delta[v] * scale;
        }
        var counts = mesh.GetBonesPerVertex().ToArray(); var weights = mesh.GetAllBoneWeights().ToArray();
        var bind = mesh.bindposes; var matrices = skin.bones.Select((b, i) => b.localToWorldMatrix * bind[i]).ToArray();
        var world = new Vector3[vertices.Length]; var cursor = 0;
        for (var v = 0; v < vertices.Length; v++) for (var w = 0; w < counts[v]; w++)
        {
            var weight = weights[cursor++]; world[v] += matrices[weight.boneIndex].MultiplyPoint3x4(vertices[v]) * weight.weight;
        }
        var bounds = new Bounds(world[0], Vector3.zero); foreach (var point in world) bounds.Encapsulate(point);
        var mean = world.Aggregate(Vector3.zero, (a, b) => a + b) / world.Length;
        return new JObject { ["meshBounds"] = mesh.bounds.ToString("R"),
            ["localScale"] = skin.transform.localScale.ToString("R"),
            ["parent"] = skin.transform.parent.name, ["vertices"] = vertices.Length,
            ["worldMin"] = new JArray(bounds.min.x, bounds.min.y, bounds.min.z),
            ["worldMax"] = new JArray(bounds.max.x, bounds.max.y, bounds.max.z),
            ["worldCentroid"] = new JArray(mean.x, mean.y, mean.z) };
    }

    private static System.Collections.IEnumerator Render(GameObject actor, Bounds bounds, float yaw, string path)
    {
        var previous = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        var preview = _preview ?? throw new InvalidOperationException("Eye preview session not initialized");
        try
        {
            // Render actual skinned objects with their real culling and alpha,
            // rather than a baked intermediary with ambiguous FBX unit scale.
            if (actor.scene != preview.camera.scene) preview.AddSingleGO(actor);
            var camera = preview.camera;
            camera.fieldOfView = 30;
            var distance = bounds.extents.magnitude / Mathf.Sin(15 * Mathf.Deg2Rad);
            camera.transform.position = bounds.center + Quaternion.Euler(0, yaw, 0) * new Vector3(0, .02f, distance);
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = .01f; camera.farClipPlane = distance * 4;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.13f, .15f, .17f);
            preview.lights[0].intensity = 1.15f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 180, 0);
            preview.lights[1].intensity = .55f; preview.lights[1].transform.rotation = Quaternion.Euler(-15, -30, 0);
            for (var pass = 0; pass < 2; pass++)
            {
                // Let native skinning, culling and SRP observe the new pose and
                // renderer.enabled state on actual PlayerLoop frames.
                yield return null;
                yield return null;
                preview.BeginStaticPreview(new Rect(0, 0, 768, 768));
                preview.Render(true); var image = preview.EndStaticPreview();
                if (pass == 1)
                {
                    var pixels = image.GetPixels32(); var background = pixels[0];
                    var foreground = pixels.Count(p => Math.Abs(p.r - background.r) + Math.Abs(p.g - background.g) + Math.Abs(p.b - background.b) > 12);
                    if (foreground < 100) throw new InvalidOperationException("Empty eye audit capture: " + path);
                    File.WriteAllBytes(path, image.EncodeToPNG());
                }
                Object.DestroyImmediate(image);
            }
        }
        finally
        {
            ShaderUtil.allowAsyncCompilation = previous;
        }
    }
}
#endif
