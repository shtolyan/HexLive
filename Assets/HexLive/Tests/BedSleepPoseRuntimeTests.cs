using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using EntityId = HexLive.Simulation.Common.EntityId;

namespace HexLive.Tests
{

/// <summary>
/// §31C.2 / §105: bug #351. The historical save has already woken NPC 1;
/// this controlled presentation fixture exercises the actual production bed,
/// humanoid and controller, including both stable NPC-id sleep variants.
/// Root rotations alone cannot detect a sideways pose inside an imported clip.
/// </summary>
public sealed class BedSleepPoseRuntimeTests
{
    private const string SingaporeAssets = "https://vmi3529459.contaboserver.net/api/assets/v1";
    private static readonly int Sleep = Animator.StringToHash("Sleep");

    [UnityTest, Explicit("Requires the Singapore runtime actor and bed content")]
    public IEnumerator BothSleepVariantsLieAlongTheRealBedAtEveryHexYaw()
    {
        var validatingLocalContent = Array.IndexOf(System.Environment.GetCommandLineArgs(),
            "-hexlive-local-content-validation") >= 0;
        if (validatingLocalContent)
        {
            Assert.That(Uri.TryCreate(ContentEndpoint.Current, UriKind.Absolute, out var endpoint) &&
                endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback &&
                endpoint.AbsolutePath == "/api/assets/v1" && endpoint.UserInfo.Length == 0 &&
                endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0, Is.True,
                "Local validation requires the explicit loopback HTTP assets proxy.");
        }
        else Assert.That(ContentEndpoint.Current, Is.EqualTo(SingaporeAssets),
            "Launch with -hexlive-assets " + SingaporeAssets);
        var service = ContentAssetService.Instance;
        var refreshed = false;
        void OnRefreshed() => refreshed = true;
        service.RegistryRefreshed += OnRefreshed;
        try
        {
            service.RefreshRegistry();
            var deadline = Time.realtimeSinceStartup + 70f;
            while (!refreshed && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(refreshed, Is.True, "Singapore registry refresh timed out.");
            Assert.That(service.LastError, Is.Empty, "Offline cache fallback is not verified content.");
        }
        finally { service.RegistryRefreshed -= OnRefreshed; }

        var contentDeadline = Time.realtimeSinceStartup + 70f;
        NpcAnimSet animSet;
        do
        {
            animSet = AtomicResources.Load<NpcAnimSet>("HexLive/NpcAnimSet");
            WorldPropResources.Prewarm("bed.basic");
            if (animSet != null && WorldPropResources.Load("bed.basic") != null) break;
            yield return null;
        } while (Time.realtimeSinceStartup < contentDeadline);
        Assert.That(animSet, Is.Not.Null);
        Assert.That(animSet.sleep, Has.Length.EqualTo(2), "Both currently authored variants must be covered.");
        Assert.That(WorldPropResources.Load("bed.basic"), Is.Not.Null);
        if (validatingLocalContent)
        {
            Assert.That(animSet.sleep[1], Is.Not.Null);
            Assert.That(animSet.sleep[1].name, Is.EqualTo("Sleep Mirrored"),
                "A cached old animation set is not candidate validation.");
        }

        var failures = new List<string>();
        for (var npcId = 1; npcId <= 2; npcId++)
        {
            using var fixture = new Fixture();
            yield return fixture.CreateActor(npcId);
            yield return fixture.FinishAppearance();
            var actor = fixture.Actor;
            var animator = actor.GetComponentInChildren<Animator>(true);
            Assert.That(animator, Is.Not.Null);
            Assert.That(animator.isHuman, Is.True);
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            actor.SetInteraction("Sleep", "");
            for (var simYaw = 0; simYaw < 360; simYaw += 60)
            {
                fixture.Bed.transform.rotation = Quaternion.Euler(0f,
                    SimulationUnityMapper.ToUnityFootprintYawDegrees(simYaw), 0f);
                actor.transform.SetPositionAndRotation(new Vector3(fixture.Point.position.x, 0f,
                    fixture.Point.position.z), fixture.Point.rotation);
                actor.SetLaying(true, fixture.Point, fixture.Point.position.y);
                animator.speed = 1f;
                var settleDeadline = Time.realtimeSinceStartup + 12f;
                var minimumSettle = Time.realtimeSinceStartup + 2f;
                do { yield return null; }
                while ((Time.realtimeSinceStartup < minimumSettle || animator.IsInTransition(0) ||
                        animator.GetCurrentAnimatorStateInfo(0).shortNameHash != Sleep) &&
                       Time.realtimeSinceStartup < settleDeadline);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Sleep),
                    "Production controller never reached Sleep.");
                var clips = animator.GetCurrentAnimatorClipInfo(0);
                Assert.That(clips, Has.Length.EqualTo(1));
                Assert.That(clips[0].clip, Is.EqualTo(animSet.sleep[npcId % 2]),
                    "The real id-selected override must be playing.");
                yield return new WaitForEndOfFrame();
                var visibleBody = false;
                foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                    if (skin.enabled && !skin.forceRenderingOff && skin.sharedMesh != null &&
                        skin.sharedMesh.vertexCount > 0) visibleBody = true;
                Assert.That(visibleBody, Is.True, "A hidden body cannot supply rendered pose evidence.");

                // First reach Sleep through its real transition above. Then
                // compare the same imported clip phase across all six yaws.
                // Inspect four phases on every yaw: a straight instant must
                // not hide another sideways part of the cycle.
                var phases = new[] { 0f, 0.25f, 0.5f, 0.75f };
                foreach (var phase in phases)
                {
                    animator.Play(Sleep, 0, phase);
                    animator.Update(0f);
                    animator.speed = 0f;
                    yield return null; // Let the actual skinning/render frame catch up with CPU bones.
                    yield return new WaitForEndOfFrame();
                    var head = Bone(animator, HumanBodyBones.Head);
                    var hips = Bone(animator, HumanBodyBones.Hips);
                    var feet = (Bone(animator, HumanBodyBones.LeftFoot) +
                                Bone(animator, HumanBodyBones.RightFoot)) * 0.5f;
                    var axis = head - feet;
                    var horizontal = Vector3.ProjectOnPlane(axis, Vector3.up);
                    var angle = BedAngle(axis, fixture.Bed.transform.forward);
                    var torsoAngle = BedAngle(head - hips, fixture.Bed.transform.forward);
                    var verticalFraction = Mathf.Abs(axis.normalized.y);
                    // Rotation-only coordinates preserve world-unit lengths;
                    // the bed wrapper has intentional nonuniform width/length.
                    var inverse = Quaternion.Inverse(fixture.Point.rotation);
                    var headLocal = inverse * (head - fixture.Point.position);
                    var hipsLocal = inverse * (hips - fixture.Point.position);
                    var feetLocal = inverse * (feet - fixture.Point.position);
                    var label = $"NPC={npcId}, clip={clips[0].clip.name}, simYaw={simYaw}, " +
                                $"axis={axis:F4}, bedAngle={angle:F2}, vertical={verticalFraction:F3}, " +
                                $"phase={phase:F2}, torsoAngle={torsoAngle:F2}, " +
                                $"headLocal={headLocal:F4}, hipsLocal={hipsLocal:F4}, feetLocal={feetLocal:F4}";
                    TestContext.WriteLine(label);
                    var mattress = MattressBounds(fixture.Bed, fixture.Point);
                    MeasureSupportedBody(animator, fixture.Point, out var headVolume, out var torsoVolume);
                    TestContext.WriteLine($"SUPPORT NPC={npcId}, yaw={simYaw}, phase={phase:F2}, " +
                        $"mattressMin={mattress.min:F4}, mattressMax={mattress.max:F4}, " +
                        $"headMin={headVolume.min:F4}, headMax={headVolume.max:F4}, " +
                        $"torsoMin={torsoVolume.min:F4}, torsoMax={torsoVolume.max:F4}");
                    var headFailure = SupportFailure(headVolume, mattress);
                    var torsoFailure = SupportFailure(torsoVolume, mattress);
                    TestContext.WriteLine($"SUPPORT RESULT NPC={npcId}, yaw={simYaw}, phase={phase:F2}: " +
                        $"head=[{headFailure}] torso=[{torsoFailure}] (empty means supported)");
                    Capture(fixture.Root, npcId, simYaw, phase);
                    if (simYaw == 0 && phase == 0f) Capture(fixture.Root, npcId, simYaw, phase, side: true);
                    // The actual head/torso skin volume must be supported;
                    // an angle alone missed the original phase-0.25 defect.
                    if (headFailure.Length != 0 || torsoFailure.Length != 0 ||
                        horizontal.magnitude < 0.5f || verticalFraction > 0.35f)
                        failures.Add(label + $" (head=[{headFailure}], torso=[{torsoFailure}], " +
                            $"horizontalLength={horizontal.magnitude:F4}, verticalFraction={verticalFraction:F4})");
                }
            }
        }
        Assert.That(failures, Is.Empty, string.Join("\n", failures));
    }

    internal static bool Supported(Bounds body, Bounds mattress) => SupportFailure(body, mattress).Length == 0;

    internal static string SupportFailure(Bounds body, Bounds mattress)
    {
        // The soft leaves form a measured volume, not a flat plane at their
        // highest frond. Skin may compress that layer, but must stay above its
        // bottom. Keep the existing maximum air gap and a 2 cm soft edge.
        const float edgeTolerance = 0.02f;
        var failures = new List<string>();
        if (body.min.x < mattress.min.x - edgeTolerance || body.max.x > mattress.max.x + edgeTolerance ||
            body.min.z < mattress.min.z - edgeTolerance || body.max.z > mattress.max.z + edgeTolerance)
            failures.Add("horizontal: skin leaves the mattress footprint");
        if (body.min.y < mattress.min.y)
            failures.Add("under-mattress: skin below the measured soft layer");
        if (body.min.y - mattress.max.y > 0.15f)
            failures.Add("above: air gap exceeds 0.15 wu");
        return string.Join("; ", failures);
    }

    internal static Bounds MattressBounds(GameObject bed, Transform point)
    {
        var bounds = new Bounds();
        var count = 0;
        var inverse = Quaternion.Inverse(point.rotation);
        foreach (var filter in bed.GetComponentsInChildren<MeshFilter>())
        {
            if (!filter.name.StartsWith("leaf_", StringComparison.Ordinal) || filter.sharedMesh == null) continue;
            var local = filter.sharedMesh.bounds;
            for (var corner = 0; corner < 8; corner++)
            {
                var vertex = new Vector3((corner & 1) == 0 ? local.min.x : local.max.x,
                    (corner & 2) == 0 ? local.min.y : local.max.y,
                    (corner & 4) == 0 ? local.min.z : local.max.z);
                var value = inverse * (filter.transform.TransformPoint(vertex) - point.position);
                if (count++ == 0) bounds = new Bounds(value, Vector3.zero);
                else bounds.Encapsulate(value);
            }
        }
        Assert.That(count, Is.GreaterThan(0), "The actual bed must expose its authored leaf mattress.");
        return bounds;
    }

    internal static void MeasureSupportedBody(Animator animator, Transform point,
        out Bounds headBounds, out Bounds torsoBounds)
    {
        headBounds = new Bounds();
        torsoBounds = new Bounds();
        var headCount = 0;
        var torsoCount = 0;
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        var torso = new HashSet<Transform>();
        foreach (var bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine,
                     HumanBodyBones.Chest, HumanBodyBones.UpperChest })
        {
            var transform = animator.GetBoneTransform(bone);
            if (transform != null) torso.Add(transform);
        }
        var inverse = Quaternion.Inverse(point.rotation);
        // Use the same complete-body contract as production skin painting.
        // Clothing remains rendered, but it is not anatomy or support evidence.
        Assert.That(ActorBodyResolver.TryResolve(animator.gameObject, out var body, out var bodyError),
            Is.True, bodyError);
        foreach (var skin in new[] { body })
        {
            Assert.That(skin.enabled && !skin.forceRenderingOff, Is.True, "The actual body must remain visible.");
            var mesh = skin.sharedMesh;
            Assert.That(mesh.isReadable, Is.True, "Actual skin weights are required for support evidence.");
            var weights = mesh.boneWeights;
            Assert.That(weights.Length, Is.GreaterThan(0), "The resolved body must have real skin weights.");
            var bones = skin.bones;
            var isHead = new bool[bones.Length];
            var isTorso = new bool[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                isHead[i] = bones[i] != null && (bones[i] == head || bones[i].IsChildOf(head));
                isTorso[i] = torso.Contains(bones[i]);
            }
            var posed = new Mesh();
            try
            {
                // Unity 6000.4: the default bake already contains Transform scale.
                // Compensate it before TransformPoint applies that scale once.
                skin.BakeMesh(posed, false);
                var scaledVertices = posed.vertices;
                var scaledBounds = posed.bounds;
                skin.BakeMesh(posed, true);
                var vertices = posed.vertices;
                Assert.That(vertices.Length, Is.EqualTo(weights.Length));
                var maxWorldDifference = 0f;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var compensatedWorld = skin.transform.TransformPoint(vertices[i]);
                    var scaledWorld = skin.transform.position + skin.transform.rotation * scaledVertices[i];
                    maxWorldDifference = Mathf.Max(maxWorldDifference,
                        Vector3.Distance(compensatedWorld, scaledWorld));
                }
                TestContext.WriteLine($"SKIN SCALE {skin.name}: lossy={skin.transform.lossyScale:F6} " +
                    $"bakedFalseSize={scaledBounds.size:F6} bakedTrueSize={posed.bounds.size:F6} " +
                    $"worldDifference={maxWorldDifference:F6}");
                Assert.That(maxWorldDifference, Is.LessThan(0.001f),
                    "Compensated local vertices and scale-baked vertices must map to the same world skin.");
                for (var i = 0; i < vertices.Length; i++)
                {
                    var h = Weight(weights[i], isHead) >= 0.5f;
                    var t = Weight(weights[i], isTorso) >= 0.5f;
                    if (!h && !t) continue;
                    var value = inverse * (skin.transform.TransformPoint(vertices[i]) - point.position);
                    if (h)
                    {
                        if (headCount++ == 0) headBounds = new Bounds(value, Vector3.zero);
                        else headBounds.Encapsulate(value);
                    }
                    if (t)
                    {
                        if (torsoCount++ == 0) torsoBounds = new Bounds(value, Vector3.zero);
                        else torsoBounds.Encapsulate(value);
                    }
                }
            }
            finally { Object.Destroy(posed); }
        }
        Assert.That(headCount, Is.GreaterThan(20), "The actual weighted head volume is required.");
        Assert.That(torsoCount, Is.GreaterThan(20), "The actual weighted torso volume is required.");
    }

    private static float Weight(BoneWeight weight, bool[] selected) =>
        (selected[weight.boneIndex0] ? weight.weight0 : 0f) +
        (selected[weight.boneIndex1] ? weight.weight1 : 0f) +
        (selected[weight.boneIndex2] ? weight.weight2 : 0f) +
        (selected[weight.boneIndex3] ? weight.weight3 : 0f);

    private static Vector3 Bone(Animator animator, HumanBodyBones name)
    {
        var bone = animator.GetBoneTransform(name);
        Assert.That(bone, Is.Not.Null, name.ToString());
        return bone.position;
    }

    private static float BedAngle(Vector3 axis, Vector3 bedForward)
    {
        var angle = Vector3.Angle(Vector3.ProjectOnPlane(axis, Vector3.up), bedForward);
        return Mathf.Min(angle, 180f - angle);
    }

    private static void Capture(GameObject root, int npcId, int yaw, float phase, bool side = false)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        Assert.That(renderers.Length, Is.GreaterThan(1));
        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
        var cameraObject = new GameObject("Bed regression camera");
        var lightObject = new GameObject("Bed regression light");
        var target = new RenderTexture(640, 640, 24);
        var pixels = new Texture2D(640, 640, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(1.1f, bounds.extents.magnitude * 1.15f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.15f, 0.17f, 0.2f);
            cameraObject.transform.position = bounds.center + (side
                ? new Vector3(5f, 0.5f, 0f) : new Vector3(0f, 5f, -0.001f));
            cameraObject.transform.LookAt(bounds.center, side ? Vector3.up : Vector3.forward);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            target.Create();
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.Not.Null);
            RenderPipeline.SubmitRenderRequest(camera,
                new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 640, 640), 0, 0);
            pixels.Apply();
            var sampled = pixels.GetPixels32();
            var background = sampled[0];
            var silhouettePixels = 0;
            foreach (var pixel in sampled)
                if (Math.Abs(pixel.r - background.r) + Math.Abs(pixel.g - background.g) +
                    Math.Abs(pixel.b - background.b) > 24) silhouettePixels++;
            var directory = Path.Combine(Path.GetTempPath(), "hexlive-bug351-runtime");
            Directory.CreateDirectory(directory);
            var suffix = side ? "-side" : "";
            var file = Path.Combine(directory, $"npc{npcId}-yaw{yaw}-phase{Mathf.RoundToInt(phase * 100)}{suffix}.png");
            File.WriteAllBytes(file, pixels.EncodeToPNG());
            TestContext.WriteLine("Rendered bed pose: " + file);
            Assert.That(silhouettePixels, Is.GreaterThan(100), "GPU readback is empty/uniform.");
        }
        finally
        {
            RenderTexture.active = previous;
            Object.Destroy(cameraObject);
            Object.Destroy(lightObject);
            Object.Destroy(target);
            Object.Destroy(pixels);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Root = new("Bed sleep runtime regression");
        public readonly GameObject Bed;
        public readonly Transform Point;
        public NpcActorView Actor;
        private NpcSnapshot _snapshot;

        public Fixture()
        {
            Bed = BedAssembly.BuildFinished("bed.basic");
            Assert.That(Bed, Is.Not.Null);
            Assert.That(Bed.name, Does.Contain("native assembly"), "An emergency primitive is not evidence.");
            Bed.transform.SetParent(Root.transform, false);
            foreach (var transform in Bed.GetComponentsInChildren<Transform>(true))
                if (transform.name == "point") { Point = transform; break; }
            Assert.That(Point, Is.Not.Null);
        }

        public IEnumerator CreateActor(int id)
        {
            var renderer = Root.AddComponent<HexWorldRenderer>();
            renderer.enabled = false;
            typeof(HexWorldRenderer).GetField("_npcsRoot", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(renderer, Root.transform);
            var create = typeof(HexWorldRenderer).GetMethod("CreateNpcView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _snapshot = new NpcSnapshot
            {
                Id = new EntityId(id), ActorMesh = "Marta", SkinSet = "Marta",
                Hairstyle = "none", UseAuthoredAppearance = true, Hygiene = 1f,
                DisplayName = "Bed pose regression", Health = 1f
            };
            var deadline = Time.realtimeSinceStartup + 70f;
            do
            {
                var created = (GameObject)create.Invoke(renderer, new object[] { _snapshot });
                if (created != null) { Actor = created.GetComponent<NpcActorView>(); break; }
                yield return null;
            } while (Time.realtimeSinceStartup < deadline);
            Assert.That(Actor, Is.Not.Null, "Production Marta actor did not load.");
        }

        public IEnumerator FinishAppearance()
        {
            var deadline = Time.realtimeSinceStartup + 70f;
            var ready = false;
            do
            {
                Actor.SyncWorn(_snapshot.WornItems);
                Actor.SetSkinWeathering(0f, 0f, hygiene: 1f);
                Actor.SetBodyCondition(_snapshot.BodyParts, _snapshot.UncoveredParts, 1f, 0f,
                    wounds: _snapshot.Wounds, bandagedZones: _snapshot.BandagedZones,
                    severedParts: _snapshot.SeveredParts, partConditions: _snapshot.BodyPartConditions);
                // The shared appearance gate requests unfinished paint passes;
                // it does not change this living actor into a corpse.
                ready = Actor.IsCorpsePresentationReady(_snapshot.WornItems,
                    _snapshot.SeveredParts, _snapshot.BodyPartConditions);
                if (!ready) yield return null;
            } while (!ready && Time.realtimeSinceStartup < deadline);
            Assert.That(ready, Is.True, "Actual skin paint never became ready for visual evidence.");
            yield return null;
            yield return new WaitForEndOfFrame();
        }

        public void Dispose() => Object.Destroy(Root);
    }
}

}
