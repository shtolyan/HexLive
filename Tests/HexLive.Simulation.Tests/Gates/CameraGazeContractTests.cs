using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §130 r4 / bug #134: camera placement starts the glance but must not shorten
/// it. These source-contract gates cover the presentation code without opening
/// a second Unity Editor while the player is testing the game.
/// </summary>
public sealed class CameraGazeContractTests
{
    private static string Presentation(params string[] parts) => Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
        Path.Combine(parts));

    [Test]
    public void CloseUpGlanceLastsSixSeconds()
    {
        var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));

        Assert.That(camera, Does.Contain("CloseUpGazeSeconds = 6f"));
    }

    [Test]
    public void StartedGlanceIsNotCancelledByCameraPlacement()
    {
        var gaze = File.ReadAllText(Presentation("Rendering", "CameraCloseUpGaze.cs"));
        var lateUpdateStart = gaze.IndexOf("private void LateUpdate()", StringComparison.Ordinal);
        var activeStart = gaze.IndexOf(
            "if (_activeView != null)", lateUpdateStart, StringComparison.Ordinal);
        var triggerStart = gaze.IndexOf(
            "if (!_renderer.TryGetNearestCameraGazeCandidate", activeStart,
            StringComparison.Ordinal);
        Assert.That(lateUpdateStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(activeStart, Is.GreaterThan(lateUpdateStart));
        Assert.That(triggerStart, Is.GreaterThan(activeStart));

        var activeBranch = gaze[activeStart..triggerStart];
        Assert.Multiple(() =>
        {
            Assert.That(activeBranch, Does.Contain("_activeView.UpdateCameraGaze(lens)"));
            Assert.That(activeBranch, Does.Not.Contain("IsWithin("));
            Assert.That(activeBranch, Does.Not.Contain("IsFrontal("));
            Assert.That(activeBranch, Does.Not.Contain("_activeView.EndCameraGaze()"));
            Assert.That(gaze, Does.Not.Contain("frontalEnterDot"));
            Assert.That(gaze, Does.Not.Contain("frontalExitDot"));
        });
    }
}
