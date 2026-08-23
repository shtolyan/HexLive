using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §80 r3: the baked face photos. Each of these was a shipped defect — a bald
/// colonist photographed before her hair finished loading, a portrait taken
/// mid-stride, and a frame borrowed from the wide identity card that put the
/// head off centre inside the round mask.
/// </summary>
public sealed class NpcPortraitContractTests
{
    private static string Presentation(params string[] parts) => Path.Combine(
        new[] { RepoPaths.Root, "Assets", "HexLive", "UnityPresentation" }
            .Concat(parts).ToArray());

    [Test]
    public void PhotoWaitsForTheHairAndForHerToStandStill()
    {
        var actor = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));
        var renderer = File.ReadAllText(Presentation("Rendering", "HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(actor, Does.Contain("if (_pendingHairLoads > 0)"),
                "A photo taken before the hairstyle arrives is a bald colonist " +
                "in every list until the next in-game day.");
            Assert.That(actor, Does.Contain("public bool IsPortraitPoseSettled"));
            Assert.That(actor, Does.Contain("PortraitStillGait"));
            Assert.That(renderer, Does.Contain("actorView.IsPortraitPoseSettled"),
                "Only the photo path waits for her to stand; §130's glance at the " +
                "game camera reads IsPhotogenic directly and must stay unchanged.");
        });
    }

    [Test]
    public void PhotoFramesTheActualHeadAndCurrentHair()
    {
        var cache = File.ReadAllText(Presentation("UI", "NpcPortraitCache.cs"));
        var actor = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(actor, Does.Contain("GetPortraitHeadExtents("));
            Assert.That(actor, Does.Contain("_bodyBones.HairInstance"),
                "The frame must include the hairstyle that is actually worn now.");
            Assert.That(actor, Does.Contain("renderer.bounds"));
            Assert.That(cache, Does.Contain("actor.GetPortraitHeadExtents("));
            Assert.That(cache, Does.Contain("var aim = face + up * ((above - below) * 0.5f)"));
            Assert.That(cache, Does.Contain("Mathf.Tan(_camera.fieldOfView * 0.5f"));
            Assert.That(cache, Does.Contain("FrameMargin"));
            Assert.That(cache, Does.Contain("TryAimPortraitCamera(out _)"),
                "The final aim must be recomputed at camera render time; LateUpdate " +
                "order cannot promise that IK has already moved the head.");
            Assert.That(cache, Does.Contain("LookRotation(aim - eye"));
            Assert.That(cache, Does.Not.Contain("HeadCentreLiftMeters"));
            Assert.That(cache, Does.Not.Contain("FaceDistanceMeters"));
        });
    }

    [Test]
    public void PhotoCanTemporarilyRenderAFogHiddenActorWithoutRevealingHer()
    {
        var cache = File.ReadAllText(Presentation("UI", "NpcPortraitCache.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(cache, Does.Contain("TryGetActorView(npcId"));
            Assert.That(cache, Does.Contain("_subjectWasActive = _portraitSubject.activeSelf"));
            Assert.That(cache, Does.Contain("_portraitSubject.SetActive(true)"));
            Assert.That(cache, Does.Contain("_portraitSubject.SetActive(false)"));
            Assert.That(cache, Does.Contain("_camera.cullingMask = 1 << _portraitLayer"));
            Assert.That(cache, Does.Contain("IsolatePortraitSubject()"));
            Assert.That(cache, Does.Contain("RestorePortraitSubjectAfterRender()"));
            Assert.That(cache, Does.Contain("RenderPipelineManager.beginCameraRendering"));
            Assert.That(cache, Does.Contain("Camera.onPreCull"));
            Assert.That(cache, Does.Not.Contain("1 << actorsLayer"),
                "A broad Actors mask both misses inactive actors and allows photobombs.");
            Assert.That(cache, Does.Not.Contain("_camera.Render()"));
        });
    }
}
