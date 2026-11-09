#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class TorchGripRuntimeTests : BugWaveArtRuntimeFixture
{
    private const string Output = "Build/Diagnostics/bug-wave-20261109/visual";

    [UnityTest] public IEnumerator TorchMartaRight() => Review("Marta", false);
    [UnityTest] public IEnumerator TorchMartaLeft() => Review("Marta", true);
    [UnityTest] public IEnumerator TorchKshishtofRight() => Review("Kshishtof", false);
    [UnityTest] public IEnumerator TorchKshishtofLeft() => Review("Kshishtof", true);

    private IEnumerator Review(string actorName, bool left)
    {
        var actor = Keep(Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/HexLiveContent/People/Prefabs/Actors/" + actorName + ".prefab")));
        actor.transform.position = new Vector3(1000f, 0f, 1000f);
        actor.transform.localScale = Vector3.one * .7764706f;
        var animator = actor.GetComponent<Animator>();
        animator.enabled = false;
        var bones = actor.GetComponent<BodyBones>();
        bones.Construct((ActorName)Enum.Parse(typeof(ActorName), actorName), 169);
        foreach (var name in actorName == "Marta" ? new[] { "PrimalBriefs", "PrimalTop" } : new[] { "PrimalBriefs" })
            bones.Equip(name, AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/People/Prefabs/Wear/" + actorName + "_" + name + ".prefab").GetComponent<Wear>(), null);
        actor.transform.rotation = Quaternion.Euler(0, 180, 0);
        animator.enabled = true;
        animator.runtimeAnimatorController = null;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind();
        var clip = GearLibrary.ConfigFor(ContentIds.Torch).armedIdle;
        Assert.That(clip, Is.Not.Null);
        var graph = PlayableGraph.Create("Torch authored stance review");
        var play = AnimationClipPlayable.Create(graph, clip);
        AnimationPlayableOutput.Create(graph, "authored idle", animator).SetSourcePlayable(play);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        graph.Play();
        play.SetTime(clip.length * .4);
        graph.Evaluate(0);
        try
        {
            var skins = actor.GetComponentsInChildren<SkinnedMeshRenderer>()
                .Where(s => s.enabled && s.sharedMesh != null && s.sharedMesh.vertexCount > 0).ToArray();
            Assert.That(skins.Length, Is.GreaterThan(0));
            foreach (var skin in skins) skin.updateWhenOffscreen = true;
            // Real PlayerLoop skinning must finish before either measuring the
            // rendered body or reading the acceptance camera's RenderTexture.
            yield return null;
            yield return null;

            var appearance = bones.GetComponent<PeopleAppearance>();
            var bone = bones.GetBone(left ? "lHand" : "rHand");
            Assert.That(skins.Any(s => s.bones.Contains(bone)), Is.True,
                "Attachment uses the rendered body's hand bone");
            var hand = appearance != null ? appearance.PropAnchor(bone) : bone;
            var type = typeof(NpcActorView).Assembly.GetType("HexLive.UnityPresentation.Wearing.HandPropVisual");
            var prop = Keep((GameObject)type.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { ContentIds.Torch, hand, left }));
            Assert.That(ObjectFit.TryMeasuredSize(prop, ContentIds.Torch, out var size), Is.True);
            Assert.That(size, Is.EqualTo(.6f).Within(.001f));
            var point = prop.GetComponentsInChildren<Transform>(true).Single(t => t.name == "fire_point");
            var grip = Vector3.Lerp(prop.transform.position, point.position, .24f / 1.04f);
            var palm = hand.TransformPoint(new Vector3(left ? -.0543f : .0543f, -.0236f, -.051f));
            var gripError = Vector3.Distance(grip, palm);
            Assert.That(gripError, Is.LessThan(.025f), actorName + " grip");
            Assert.That(prop.GetComponentInChildren<Light>().range, Is.EqualTo(7.5f).Within(.001f));
            foreach (var particles in prop.GetComponentsInChildren<ParticleSystem>())
            {
                particles.Simulate(.6f, true, true, false);
                particles.Pause();
            }
            var sun = Keep(new GameObject("Acceptance sun")).AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(40, -25, 0);
            yield return null;
            yield return null;

            grip = Vector3.Lerp(prop.transform.position, point.position, .24f / 1.04f);
            palm = hand.TransformPoint(new Vector3(left ? -.0543f : .0543f, -.0236f, -.051f));
            gripError = Vector3.Distance(grip, palm);
            Assert.That(gripError, Is.LessThan(.025f), "Grip remains attached after real animation/render frames");
            var bodyBounds = skins[0].bounds;
            foreach (var skin in skins.Skip(1)) bodyBounds.Encapsulate(skin.bounds);
            Assert.That(bodyBounds.size.y, Is.InRange(.8f, 2.5f), "The actual rendered body must have human height.");
            var propRenderers = prop.GetComponentsInChildren<MeshRenderer>();
            Assert.That(propRenderers.Length, Is.GreaterThan(0));
            var propBounds = propRenderers[0].bounds;
            foreach (var renderer in propRenderers.Skip(1)) propBounds.Encapsulate(renderer.bounds);
            var overview = bodyBounds;
            overview.Encapsulate(propBounds);
            var file = "torch-" + actorName + (left ? "-left" : "-right");
            var captures = new JArray();
            yield return CaptureFrame(overview, bodyBounds, new Vector3(.45f, .12f, -2.7f), file + "-body", true, captures);
            var close = new Bounds(grip, new Vector3(.48f, .48f, .48f));
            yield return CaptureFrame(close, bodyBounds, new Vector3(.25f, .15f, -1.5f), file + "-grip-front", false, captures);
            yield return CaptureFrame(close, bodyBounds, new Vector3(1.5f, .12f, -.25f), file + "-grip-side", false, captures);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, file + ".json"), new JObject
            {
                ["actor"] = actorName, ["leftHand"] = left, ["clip"] = clip.name,
                ["clipTime"] = clip.length * .4, ["clipMirrored"] = false,
                ["poseSource"] = "Unmodified authored gear armedIdle evaluated through PlayableGraph and PlayerLoop",
                ["bodyBonePath"] = AnimationUtility.CalculateTransformPath(bone, actor.transform),
                ["handWorld"] = V(bone.position), ["anchorWorld"] = V(hand.position),
                ["propOriginWorld"] = V(prop.transform.position), ["flamePointWorld"] = V(point.position),
                ["gripWorld"] = V(grip), ["palmWorld"] = V(palm), ["gripErrorMetres"] = gripError,
                ["physicalSizeMetres"] = size, ["bodyBoundsCenter"] = V(bodyBounds.center),
                ["bodyBoundsSize"] = V(bodyBounds.size), ["propBoundsSize"] = V(propBounds.size),
                ["renderedSkinCount"] = skins.Length, ["captures"] = captures,
                ["bakedMeshSubstitutions"] = false,
            }.ToString());
        }
        finally { if (graph.IsValid()) graph.Destroy(); }
    }

    private IEnumerator CaptureFrame(Bounds frame, Bounds body, Vector3 direction, string file,
        bool requireBody, JArray captures)
    {
        const int width = 1200, height = 1000;
        var camera = Keep(new GameObject("Torch frame acceptance camera")).AddComponent<Camera>();
        camera.transform.position = frame.center + direction.normalized * 3f;
        camera.transform.LookAt(frame.center);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.16f, .20f, .24f);
        camera.nearClipPlane = .01f;
        camera.farClipPlane = 20f;
        camera.useOcclusionCulling = false;
        camera.orthographic = true;
        camera.aspect = (float)width / height;
        var up = camera.transform.up;
        var right = camera.transform.right;
        var ext = frame.extents;
        var vertical = Mathf.Abs(up.x) * ext.x + Mathf.Abs(up.y) * ext.y + Mathf.Abs(up.z) * ext.z;
        var horizontal = Mathf.Abs(right.x) * ext.x + Mathf.Abs(right.y) * ext.y + Mathf.Abs(right.z) * ext.z;
        camera.orthographicSize = Mathf.Max(vertical, horizontal / camera.aspect) * 1.12f;
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        camera.targetTexture = rt;
        camera.enabled = true;
        var renderedFrames = 0;
        Action<ScriptableRenderContext, Camera> afterCamera = (_, completed) =>
        {
            if (completed == camera) renderedFrames++;
        };
        RenderPipelineManager.endCameraRendering += afterCamera;
        Texture2D pixels = null;
        var previous = RenderTexture.active;
        var method = "PlayerLoop endCameraRendering";
        try
        {
            // endCameraRendering is the primary path. A headless Editor with
            // no GameView may need an explicit render after real animation frames.
            for (var i = 0; i < 8 && renderedFrames < 2; i++) yield return null;
            if (renderedFrames == 0)
            {
                method = "Camera.Render after eight PlayerLoop frames";
                camera.Render();
            }
            RenderTexture.active = rt;
            pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            pixels.Apply();
            Directory.CreateDirectory(Output);
            var path = Path.Combine(Output, file + ".png");
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            var roi = ProjectBounds(camera, body, width, height);
            var background = pixels.GetPixel(0, 0);
            var changed = 0;
            var sampled = 0;
            for (var y = roi.yMin; y < roi.yMax; y += 2)
            for (var x = roi.xMin; x < roi.xMax; x += 2)
            {
                var color = pixels.GetPixel(x, y);
                var difference = Mathf.Abs(color.r - background.r) + Mathf.Abs(color.g - background.g) + Mathf.Abs(color.b - background.b);
                if (difference > .08f) changed++;
                sampled++;
            }
            var foreground = sampled > 0 ? (float)changed / sampled : 0f;
            captures.Add(new JObject
            {
                ["path"] = path, ["method"] = method, ["renderedCameraFrames"] = renderedFrames,
                ["bodyRoi"] = new JArray(roi.x, roi.y, roi.width, roi.height),
                ["bodyRoiForegroundFraction"] = foreground, ["cameraWorld"] = V(camera.transform.position),
                ["cameraTargetWorld"] = V(frame.center), ["orthographicSize"] = camera.orthographicSize,
            });
            if (requireBody) Assert.That(foreground, Is.GreaterThan(.08f),
                "The acceptance image must visibly contain the actual skinned body, not just a floating torch: " + path);
        }
        finally
        {
            RenderPipelineManager.endCameraRendering -= afterCamera;
            camera.enabled = false;
            camera.targetTexture = null;
            RenderTexture.active = previous;
            if (pixels != null) Object.DestroyImmediate(pixels);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }

    private static RectInt ProjectBounds(Camera camera, Bounds bounds, int width, int height)
    {
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        for (var i = 0; i < 8; i++)
        {
            var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
            var point = camera.WorldToScreenPoint(bounds.center + Vector3.Scale(bounds.extents, sign));
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        var x0 = Mathf.Clamp(Mathf.FloorToInt(min.x), 0, width - 1);
        var y0 = Mathf.Clamp(Mathf.FloorToInt(min.y), 0, height - 1);
        var x1 = Mathf.Clamp(Mathf.CeilToInt(max.x), x0 + 1, width);
        var y1 = Mathf.Clamp(Mathf.CeilToInt(max.y), y0 + 1, height);
        return new RectInt(x0, y0, x1 - x0, y1 - y0);
    }

    private static JArray V(Vector3 value) => new JArray(value.x, value.y, value.z);
}
}
#endif
