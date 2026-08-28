using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §130 r5: решением игрока взгляд в камеру возвращён к r1 — «просто
/// пялится» (сильные веса, гистерезис по дистанции, 5 секунд). Ревизии
/// r2–r4 (малые веса, блендеры, фронтальный конус, безотзывной таймер
/// bug #134) в игре читались как дёрганье и откачены — см. Spec/130.md
/// §130.3. Эти source-контракты не дают тихо вернуть их назад.
/// </summary>
public sealed class CameraGazeContractTests
{
    private static string Presentation(params string[] parts) => Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
        Path.Combine(parts));

    [Test]
    public void CloseUpGlanceLastsFiveSecondsWithDistanceHysteresis()
    {
        var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(camera, Does.Contain("CloseUpGazeSeconds = 5f"));
            Assert.That(camera, Does.Contain("CloseUpGazeEnterDistance = 2.0f"));
            Assert.That(camera, Does.Contain("CloseUpGazeExitDistance = 2.6f"));
        });
    }

    [Test]
    public void GazeHasNoFrontalConeAndNoWeightBlender()
    {
        var gaze = File.ReadAllText(Presentation("Rendering", "CameraCloseUpGaze.cs"));
        var view = File.ReadAllText(Presentation("Wearing", "NpcActorView.cs"));

        Assert.Multiple(() =>
        {
            // r3: конус «только спереди» мигал на краю и убивал взгляд.
            Assert.That(gaze, Does.Not.Contain("IsFrontal"));
            Assert.That(gaze, Does.Not.Contain("frontalEnterDot"));
            // r2/r3: блендер набора весов давал дёрганье вместо плавности —
            // плавность обеспечивает штатный разгон IKPositionWeight.
            Assert.That(view, Does.Not.Contain("_cameraGazeBlend"));
            // r1-веса: глаза решают, голова доворачивает.
            Assert.That(view, Does.Contain("_lookAtIK.solver.eyesWeight = 1f"));
        });
    }
}
