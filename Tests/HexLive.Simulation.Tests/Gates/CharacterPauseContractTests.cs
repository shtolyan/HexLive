using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§31.13C / bug #144: simulation pause freezes character presentation.</summary>
public sealed class CharacterPauseContractTests
{
    [Test]
    public void RunnerPauseOwnsTheScaledPresentationClock()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Bootstrap", "SimulationRunnerBehaviour.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("_backend.Pause();\n        SetPresentationPaused(true);"));
            Assert.That(source, Does.Contain("_backend.Resume();\n        SetPresentationPaused(false);"));
            Assert.That(source, Does.Contain("Time.timeScale = 0f;"));
            Assert.That(source, Does.Contain("Time.timeScale = _timeScaleBeforePause > 0f"));
        });
    }

    [Test]
    public void EscapeMenuPreservesAnExistingManualPause()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "UI", "GameMenu.cs"));

        var capture = source.IndexOf("_timeScaleBefore = Time.timeScale;",
            System.StringComparison.Ordinal);
        var pause = source.IndexOf("_runner.Pause();", System.StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(capture, Is.GreaterThanOrEqualTo(0));
            Assert.That(pause, Is.GreaterThan(capture));
            Assert.That(source, Does.Contain(
                "Time.timeScale = _timeScaleBefore >= 0f ? _timeScaleBefore : 1f;"));
        });
    }
}
