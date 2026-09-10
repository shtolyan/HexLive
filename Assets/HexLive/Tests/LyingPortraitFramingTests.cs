using System;
using System.Collections.Generic;
using System.Reflection;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
/// <summary>§107.4a / #394: real camera projection and production pose selection.</summary>
public sealed class LyingPortraitFramingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void EntireEnvelopeFitsAllSixHeadingsAndActorScales()
    {
        using (var fixture = new Fixture())
        {
            foreach (var size in new[] { 0.5f, 1f, 2f })
            for (var yaw = 0; yaw < 360; yaw += 60)
            {
                var scale = HexWorldRenderer.ActorScale * size;
                var support = new Pose(new Vector3(17f, 1.2f, -9f), Quaternion.Euler(0, yaw, 0));
                var shot = LyingPortraitFraming.CameraPose(support, scale, 22f, fixture.Camera.aspect);
                fixture.Camera.transform.SetPositionAndRotation(shot.position, shot.rotation);
                var bounds = LyingPortraitFraming.Envelope(scale);
                // The live clips move limbs outside the collision footprint
                // and below the support origin. A collision-only frame must
                // fail even if its own eight corners happen to fit perfectly.
                Assert.That(bounds.min.y, Is.LessThan(-0.10f * size));
                Assert.That(bounds.max.y, Is.GreaterThan(0.41f * size));
                Assert.That(bounds.extents.x, Is.GreaterThan(0.58f * size));
                Assert.That(bounds.extents.z, Is.GreaterThan(0.82f * size));
                for (var corner = 0; corner < 8; corner++)
                {
                    var p = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    var screen = fixture.Camera.WorldToViewportPoint(support.position + support.rotation * p);
                    Assert.That(screen.z, Is.GreaterThan(fixture.Camera.nearClipPlane));
                    Assert.That(screen.x, Is.InRange(0.02f, 0.98f), "horizontal clipping at yaw " + yaw);
                    Assert.That(screen.y, Is.InRange(0.02f, 0.98f), "vertical clipping at yaw " + yaw);
                }
                Assert.That(Mathf.Abs(fixture.Camera.transform.forward.y), Is.LessThan(0.00001f));
                Assert.That(Vector3.Dot(fixture.Camera.transform.up, Vector3.up), Is.GreaterThan(0.99999f));
                var head = fixture.Camera.WorldToViewportPoint(support.position - support.rotation * Vector3.forward * bounds.extents.z);
                var feet = fixture.Camera.WorldToViewportPoint(support.position + support.rotation * Vector3.forward * bounds.extents.z);
                Assert.That(head.x, Is.LessThan(feet.x), "head stays on the left, away from the right overlay");
            }
        }
    }

    [Test]
    public void AnimatedEyesAndBodyOffsetCannotMoveLyingCamera()
    {
        using (var fixture = new Fixture())
        {
            var actor = fixture.Actor;
            Set(actor, "_laying", true);
            var left = fixture.Child("LeftEye").transform;
            var right = fixture.Child("RightEye").transform;
            Set(actor, "_lEye", left);
            Set(actor, "_rEye", right);
            fixture.Stage.SetTarget(1);
            fixture.Tick();
            var expected = new Pose(fixture.Camera.transform.position, fixture.Camera.transform.rotation);
            for (var i = 0; i < 30; i++)
            {
                left.position = new Vector3(i * 0.1f, Mathf.Sin(i), Mathf.Cos(i));
                right.position = left.position + new Vector3(0.05f, i * 0.01f, 0.02f);
                fixture.Body.localPosition = new Vector3(0, 4f + i, 1f);
                fixture.Body.localRotation = Quaternion.Euler(i * 7f, i * 9f, i * 13f);
                fixture.Tick();
                AssertPose(fixture.Camera.transform, expected);
            }
        }
    }

    [Test]
    public void BedUsesAuthoredHorizontalAnchorAndSleepSurfaceHeight()
    {
        using (var fixture = new Fixture())
        {
            Set(fixture.Actor, "_laying", true);
            var bed = fixture.Child("BedAttach").transform;
            bed.SetPositionAndRotation(new Vector3(0.4f, 1f, 0.2f), Quaternion.Euler(0, 120, 0));
            Set(fixture.Actor, "_layingAttach", bed);
            Set(fixture.Actor, "_layingSurfaceY", 0.37f);
            Assert.That(fixture.Actor.TryGetLyingPortraitFrame(out var pose, out _), Is.True);
            Assert.That(Vector3.Distance(pose.position, new Vector3(0.4f, 0.37f, 0.2f)), Is.LessThan(0.00001f));
            Assert.That(Quaternion.Angle(pose.rotation, bed.rotation), Is.LessThan(0.001f));
            bed.position = new Vector3(100, 1, 100); // Same stale-attach rejection as the body pin.
            fixture.Actor.TryGetLyingPortraitFrame(out var fallback, out _);
            Assert.That(Vector3.Distance(fallback.position, fixture.Actor.transform.position), Is.LessThan(0.00001f));
        }
    }

    [Test]
    public void WakeRestoresTheExactExistingStandingFaceFrame()
    {
        using (var fixture = new Fixture())
        {
            fixture.Stage.SetTarget(1);
            fixture.Tick();
            var standing = new Pose(fixture.Camera.transform.position, fixture.Camera.transform.rotation);
            Set(fixture.Actor, "_laying", true);
            fixture.Tick();
            Assert.That(Vector3.Distance(fixture.Camera.transform.position, standing.position), Is.GreaterThan(0.1f));
            Set(fixture.Actor, "_laying", false);
            fixture.Tick();
            AssertPose(fixture.Camera.transform, standing);
            Assert.That(fixture.Camera.fieldOfView, Is.EqualTo(22f));
        }
    }

    [Test]
    public void TargetChangeRemovalAndReopenDoNotRetainAFormerLyingShot()
    {
        using (var fixture = new Fixture())
        {
            fixture.Stage.SetTarget(1);
            fixture.Tick();
            var standing = new Pose(fixture.Camera.transform.position, fixture.Camera.transform.rotation);
            var second = fixture.Child("Other").AddComponent<NpcActorView>();
            second.transform.position = new Vector3(40, 0, 40);
            Set(second, "_laying", true);
            fixture.Actors[2] = second;
            fixture.Stage.SetTarget(2);
            fixture.Tick();
            Assert.That(fixture.Camera.transform.position.x, Is.GreaterThan(35));
            fixture.Stage.SetTarget(-1);
            fixture.Tick();
            Assert.That(fixture.Camera.enabled, Is.False);
            fixture.Stage.SetTarget(1);
            fixture.Tick();
            AssertPose(fixture.Camera.transform, standing);
            fixture.Actors.Remove(1);
            fixture.Tick();
            Assert.That(fixture.Camera.enabled, Is.False);
        }
    }

    [Test]
    public void LyingCameraPassStillRestoresEveryActorLayer()
    {
        using (var fixture = new Fixture())
        {
            fixture.Actor.gameObject.layer = 4;
            fixture.Body.gameObject.layer = 7;
            Set(fixture.Actor, "_laying", true);
            Set(fixture.Stage, "_portraitLayer", 31);
            fixture.Stage.SetTarget(1);
            fixture.Tick();
            Invoke(fixture.Stage, "IsolateSelectedActor");
            Assert.That(fixture.Actor.gameObject.layer, Is.EqualTo(31));
            Assert.That(fixture.Body.gameObject.layer, Is.EqualTo(31));
            Invoke(fixture.Stage, "RestoreActorLayers");
            Assert.That(fixture.Actor.gameObject.layer, Is.EqualTo(4));
            Assert.That(fixture.Body.gameObject.layer, Is.EqualTo(7));
        }
    }

    private static void AssertPose(Transform actual, Pose expected)
    {
        Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThan(0.00001f));
        Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThan(0.01f));
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, Private).SetValue(target, value);
    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, Private).Invoke(target, null);

    private sealed class Fixture : IDisposable
    {
        private readonly GameObject _root;
        public readonly Camera Camera;
        public readonly PortraitStage Stage;
        public readonly NpcActorView Actor;
        public readonly Transform Body;
        public readonly Dictionary<int, NpcActorView> Actors;

        public Fixture()
        {
            _root = new GameObject("Bug394CameraTests");
            _root.SetActive(false); // No real world, audio, assets or render callbacks.
            Camera = Child("Camera").AddComponent<Camera>();
            Camera.fieldOfView = 22f;
            Camera.aspect = 512f / 368f;
            Stage = Child("PortraitStage").AddComponent<PortraitStage>();
            Actor = Child("Actor").AddComponent<NpcActorView>();
            Body = Child("Body").transform;
            Body.SetParent(Actor.transform, false);
            Body.localScale = Vector3.one * HexWorldRenderer.ActorScale;
            Set(Actor, "_bodyRoot", Body);
            Set(Actor, "_moveEpsilon", 1.7f * HexWorldRenderer.ActorScale * 0.1f);
            var renderer = Child("Renderer").AddComponent<HexWorldRenderer>();
            Actors = (Dictionary<int, NpcActorView>)typeof(HexWorldRenderer).GetField("_actorViews", Private).GetValue(renderer);
            Actors[1] = Actor;
            Set(Stage, "_worldRenderer", renderer);
            Set(Stage, "_camera", Camera);
        }

        public GameObject Child(string name)
        {
            var child = new GameObject(name);
            child.SetActive(false);
            child.transform.SetParent(_root.transform, false);
            return child;
        }

        public void Tick() => Invoke(Stage, "LateUpdate");
        public void Dispose() => Object.DestroyImmediate(_root);
    }
}
}
