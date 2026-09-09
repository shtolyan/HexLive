using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Rendering;
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
/// §28.15C / bug #354. Real runtime content, production actor factory and corpse
/// snapshot pass. No synthetic controller and no test-authored animation pose.
/// The report's historical save is unavailable: IDs/tick label this controlled
/// presentation fixture, not a historical replay of seed 12345/tick 215071.
/// </summary>
public sealed class CorpsePresentationRuntimeTests
{
    private const string ContentRequired = "Requires the selected server's runtime actor/carry content";
    private const string SingaporeAssets = "https://vmi3529459.contaboserver.net/api/assets/v1";
    private const string Backpack = "gear.backpack_osiris"; // GarmentLibrary: the reported kind of worn loot.
    private static readonly int FallenIdle = Animator.StringToHash("FallenIdle");
    private static readonly HumanBodyBones[] PoseBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot
    };

    [UnitySetUp]
    public IEnumerator RequireTheSingaporeRuntimeRegistry()
    {
        // A new test scene has no menu selection. Pin the source explicitly in
        // the Editor invocation; never silently use the default NYC registry.
        Assert.That(ContentEndpoint.Current, Is.EqualTo(SingaporeAssets),
            "Launch this regression with -hexlive-assets " + SingaporeAssets);
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
            Assert.That(service.LastError, Is.Empty, "Offline cache fallback is not a verified registry.");
            Assert.That(service.RegistryReady, Is.True);
            Assert.That(service.TryGetRecord("actor", "Molly", out _), Is.True);
        }
        finally { service.RegistryRefreshed -= OnRefreshed; }
    }

    [UnityTest, Explicit(ContentRequired)]
    public IEnumerator FreshAndRestoredBodiesReachTheSameRealFallenPose()
    {
        Quaternion[] freshPose;
        using (var fixture = new Fixture())
        {
            yield return fixture.CreateLiving();
            var actor = fixture.Actor;
            actor.SetDead(0f, fresh: true);
            AssertHidden(actor, "Appearance has not been acknowledged yet.");
            yield return fixture.FinishAppearance(actor);
            AssertGroundPose(actor);
            freshPose = ReadPose(actor);
            for (var i = 0; i < 5; i++) yield return null;
            AssertSamePose(freshPose, ReadPose(actor), "A frozen corpse must remain stable.");
        }
        yield return null;
        using (var fixture = new Fixture())
        {
            yield return fixture.RestoreCorpse();
            AssertGroundPose(fixture.Actor);
            AssertSamePose(freshPose, ReadPose(fixture.Actor),
                "Late connection/save restore must match fresh death, including actual bones.");
        }
    }

    [UnityTest, Explicit(ContentRequired)]
    public IEnumerator DelayedAnimatorAndLateChildrenStayHiddenUntilPoseAndAppearanceAreReady()
    {
        using var fixture = new Fixture();
        yield return fixture.CreateLiving();
        var actor = fixture.Actor;
        var animator = actor.GetComponentInChildren<Animator>(true);
        animator.gameObject.SetActive(false);
        actor.SetDead(0f, fresh: false);
        actor.SetCorpseAppearanceReady(true);
        AssertHidden(actor, "Requested laying is not a ready Animator.");
        for (var i = 0; i < 3; i++) yield return null;
        Assert.That(animator.enabled, Is.True, "Never freeze an unbound standing/bind pose.");
        AssertHidden(actor, "There is no timeout that reveals an unconfirmed pose.");

        actor.SetCorpseAppearanceReady(false);
        animator.gameObject.SetActive(true);
        yield return null;
        Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(FallenIdle));
        AssertHidden(actor, "Confirmed pose alone cannot reveal unfinished appearance.");
        var late = GameObject.CreatePrimitive(PrimitiveType.Cube);
        late.name = "Late arriving garment renderer";
        late.transform.SetParent(actor.transform, false);
        yield return new WaitForEndOfFrame();
        Assert.That(late.GetComponent<Renderer>().forceRenderingOff, Is.True,
            "A child arriving after snapshot sync must join the real gate before rendering.");
        Object.Destroy(late);
        yield return fixture.FinishAppearance(actor);
        AssertGroundPose(actor);
    }

    [UnityTest, Explicit(ContentRequired)]
    public IEnumerator CarriedCorpseRequiresRealHandsAndDropRestoresTheGroundPose()
    {
        using var fixture = new Fixture();
        yield return fixture.RestoreCorpse();
        var corpse = fixture.Actor;
        var groundPose = ReadPose(corpse);
        fixture.Body.CarriedByNpcId = 3002;
        fixture.SyncCorpse();
        yield return new WaitForEndOfFrame();
        AssertHidden(corpse, "Missing carrier cannot confirm a carried pose.");

        var carrierSnapshot = NewBody(3002);
        carrierSnapshot.Health = 1f;
        carrierSnapshot.CarriedNpcId = fixture.Body.Id.Value;
        fixture.Snapshot.Npcs.Add(carrierSnapshot);
        NpcActorView carrier = null;
        yield return fixture.CreateActor(carrierSnapshot, value => carrier = value);
        carrier.SetCarryingPerson(true);
        var deadline = Time.realtimeSinceStartup + 60f;
        do
        {
            fixture.SyncCorpse();
            yield return new WaitForEndOfFrame();
        } while (AllHidden(corpse) && Time.realtimeSinceStartup < deadline);
        Assert.That(AllHidden(corpse), Is.False, "Real BeingCarried clip/hand attachment never became ready.");
        var carrierAnimator = carrier.GetComponentInChildren<Animator>();
        var corpseAnimator = corpse.GetComponentInChildren<Animator>();
        var hands = (carrierAnimator.GetBoneTransform(HumanBodyBones.LeftHand).position +
            carrierAnimator.GetBoneTransform(HumanBodyBones.RightHand).position) * 0.5f;
        Assert.That(Vector3.Distance(corpseAnimator.GetBoneTransform(HumanBodyBones.Hips).position, hands),
            Is.LessThan(0.01f), "§118 carried hips are attached to the actual hand midpoint.");

        fixture.Body.CarriedByNpcId = null;
        carrierSnapshot.CarriedNpcId = null;
        carrier.SetCarryingPerson(false);
        fixture.SyncCorpse();
        // The transition above runs after this frame's rendering (the carry
        // wait resumes at EndOfFrame). Bone transforms update synchronously,
        // but GPU skinning needs the next complete frame before capture.
        carrier.gameObject.SetActive(false); // Isolate the dropped body in the evidence camera.
        yield return null;
        yield return new WaitForEndOfFrame();
        AssertSamePose(groundPose, ReadPose(corpse), "Drop must survive the next full animation frame.");
        yield return null;
        yield return new WaitForEndOfFrame();
        AssertGroundPose(corpse);
        AssertSamePose(groundPose, ReadPose(corpse), "Drop must not freeze the carried pose or a bind pose.");
    }

    private static NpcSnapshot NewBody(int id) => new()
    {
        Id = new EntityId(id), ActorMesh = "Molly", SkinSet = "Molly",
        Hairstyle = "none", UseAuthoredAppearance = true, Hygiene = 1f,
        DisplayName = "Corpse runtime regression"
    };

    private static bool AllHidden(NpcActorView actor)
    {
        var renderers = actor.GetComponentsInChildren<Renderer>(true);
        Assert.That(renderers.Length, Is.GreaterThan(0), "A primitive/empty actor is not evidence.");
        foreach (var renderer in renderers)
            if (renderer.enabled && !renderer.forceRenderingOff) return false;
        return true;
    }

    private static void AssertHidden(NpcActorView actor, string reason) =>
        Assert.That(AllHidden(actor), Is.True, reason);

    private static void AssertGroundPose(NpcActorView actor)
    {
        var animator = actor.GetComponentInChildren<Animator>(true);
        Assert.That(animator, Is.Not.Null);
        Assert.That(animator.isHuman, Is.True);
        Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(FallenIdle));
        Assert.That(animator.enabled, Is.False, "Freeze only after confirmed FallenIdle.");
        Assert.That(AllHidden(actor), Is.False, "The ready body must actually be revealed.");
        Assert.That(actor.GetComponentInChildren<BodyBones>().GetWorn(Backpack + "#0"), Is.Not.Null,
            "Ready must include a real loaded backpack, not an art-less fallback.");
        var axis = animator.GetBoneTransform(HumanBodyBones.Head).position -
            animator.GetBoneTransform(HumanBodyBones.Hips).position;
        TestContext.WriteLine($"Corpse axis={axis} vertical fraction={Mathf.Abs(axis.normalized.y):F4}");
        Assert.That(Mathf.Abs(axis.normalized.y), Is.LessThan(0.65f),
            "State names alone are not proof: actual head-to-hips geometry must be lying.");
        Capture(actor);
    }

    private static void Capture(NpcActorView actor)
    {
        // Keep an actual rendered frame for review, not only state/field assertions.
        var renderers = actor.GetComponentsInChildren<SkinnedMeshRenderer>();
        Assert.That(renderers.Length, Is.GreaterThan(0));
        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
        var stage = new GameObject("Corpse regression capture camera");
        var light = new GameObject("Corpse regression capture light");
        var target = new RenderTexture(640, 480, 24);
        var pixels = new Texture2D(640, 480, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            var camera = stage.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.15f, 0.17f, 0.2f);
            camera.nearClipPlane = 0.01f;
            camera.fieldOfView = 40f;
            var radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            stage.transform.position = bounds.center + new Vector3(1.2f, 1f, -1.8f).normalized * radius * 3.3f;
            stage.transform.LookAt(bounds.center);
            var sun = light.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            target.Create();
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.Not.Null,
                "Evidence must use the production render pipeline.");
            RenderPipeline.SubmitRenderRequest(camera,
                new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 640, 480), 0, 0);
            pixels.Apply();
            var sampled = pixels.GetPixels32();
            var background = sampled[0];
            var silhouettePixels = 0;
            var cyanPixels = 0;
            foreach (var pixel in sampled)
            {
                if (Math.Abs(pixel.r - background.r) + Math.Abs(pixel.g - background.g) +
                    Math.Abs(pixel.b - background.b) > 24) silhouettePixels++;
                if (pixel.r < 32 && pixel.g > 220 && pixel.b > 220) cyanPixels++;
            }
            TestContext.WriteLine("Rendered silhouette pixels: " + silhouettePixels);
            TestContext.WriteLine("Unexpected cyan pixels: " + cyanPixels);
            var directory = Path.Combine(Path.GetTempPath(), "hexlive-bug354-runtime");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, TestContext.CurrentContext.Test.Name + "-" + Time.frameCount + ".png");
            File.WriteAllBytes(file, pixels.EncodeToPNG());
            TestContext.WriteLine("Rendered corpse frame: " + file);
            Assert.That(silhouettePixels, Is.GreaterThan(100),
                "GPU readback is empty/uniform; renderer flags alone are not visual evidence.");
            Assert.That(cyanPixels, Is.LessThan(silhouettePixels / 10),
                "The authored Molly body must render with its skin, not a cyan intermediate material.");
        }
        finally
        {
            RenderTexture.active = previous;
            Object.Destroy(stage);
            Object.Destroy(light);
            Object.Destroy(target);
            Object.Destroy(pixels);
        }
    }

    private static Quaternion[] ReadPose(NpcActorView actor)
    {
        var animator = actor.GetComponentInChildren<Animator>(true);
        var result = new Quaternion[PoseBones.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = animator.GetBoneTransform(PoseBones[i]).localRotation;
        return result;
    }

    private static void AssertSamePose(Quaternion[] expected, Quaternion[] actual, string reason)
    {
        for (var i = 0; i < expected.Length; i++)
            Assert.That(Quaternion.Angle(expected[i], actual[i]), Is.LessThan(1f), $"{reason} Bone={PoseBones[i]}");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GameObject _root = new("Corpse presentation runtime fixture");
        private readonly HexWorldRenderer _renderer;
        public readonly NpcSnapshot Body = NewBody(3001);
        public readonly WorldSnapshot Snapshot = new() { Seed = 12345, Tick = 215071 };
        public NpcActorView Actor;

        public Fixture()
        {
            _renderer = _root.AddComponent<HexWorldRenderer>();
            _renderer.enabled = false; // The test owns snapshots, never a live world/runner.
            Body.WornItems.Add(Backpack);
            Body.WornDirtiness.Add(Backpack + "\t0.3");
            Body.WornBloodiness.Add(Backpack + "\t0.2");
            Snapshot.Corpses.Add(Body);
            Field("_npcsRoot").SetValue(_renderer, _root.transform);
        }

        public IEnumerator CreateLiving() => CreateActor(Body, actor => Actor = actor);

        public IEnumerator CreateActor(NpcSnapshot snapshot, Action<NpcActorView> done)
        {
            GameObject created = null;
            var deadline = Time.realtimeSinceStartup + 70f;
            while (created == null && Time.realtimeSinceStartup < deadline)
            {
                created = (GameObject)Call("CreateNpcView", snapshot);
                if (created == null) yield return null;
            }
            Assert.That(created, Is.Not.Null, "Production actor content did not load.");
            done(created.GetComponent<NpcActorView>());
        }

        public IEnumerator RestoreCorpse()
        {
            var deadline = Time.realtimeSinceStartup + 70f;
            do
            {
                SyncCorpse();
                var actors = (Dictionary<int, NpcActorView>)Field("_corpseActorViews").GetValue(_renderer);
                actors.TryGetValue(Body.Id.Value, out Actor);
                yield return null;
            } while ((Actor == null || AllHidden(Actor)) && Time.realtimeSinceStartup < deadline);
            Assert.That(Actor, Is.Not.Null);
            Assert.That(AllHidden(Actor), Is.False, "Production corpse pass did not finish its appearance/pose gate.");
        }

        public void SyncCorpse() => Call("SyncCorpseViews", Snapshot);

        public IEnumerator FinishAppearance(NpcActorView actor)
        {
            var deadline = Time.realtimeSinceStartup + 70f;
            var ready = false;
            do
            {
                actor.SyncWorn(Body.WornItems);
                actor.SetSkinWeathering(0f, 0f, hygiene: 1f);
                actor.SetBodyCondition(Body.BodyParts, Body.UncoveredParts, 1f, 0f,
                    wornDirtiness: Body.WornDirtiness, wornBloodiness: Body.WornBloodiness,
                    wounds: Body.Wounds, bandagedZones: Body.BandagedZones,
                    severedParts: Body.SeveredParts, partConditions: Body.BodyPartConditions);
                ready = actor.IsCorpsePresentationReady(Body.WornItems, Body.SeveredParts, Body.BodyPartConditions);
                if (!ready) yield return null;
            } while (!ready && Time.realtimeSinceStartup < deadline);
            Assert.That(ready, Is.True, "Real appearance/paint acknowledgement never arrived.");
            actor.SetCorpseAppearanceReady(ready);
            yield return new WaitForEndOfFrame();
        }

        private static FieldInfo Field(string name) => typeof(HexWorldRenderer).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);
        private object Call(string name, object argument)
        {
            var method = typeof(HexWorldRenderer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, name);
            return method.Invoke(_renderer, new[] { argument });
        }
        public void Dispose() => Object.Destroy(_root);
    }
}

}
