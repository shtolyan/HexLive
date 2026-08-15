using System.IO;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§31.13B / bug #127: MAX is uncapped for the simulation only.</summary>
public sealed class PresentationSpeedTests
{
    [Test]
    public void ActorNormalizesTheClockBeforeAnyAnimatorOrPoseMathUsesIt()
    {
        var actor = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "NpcActorView.cs"));

        Assert.That(actor, Does.Contain(
            "_simSpeed = PresentationSpeed.Normalize(multiplier);"));
    }

    [Test]
    public void UncappedSimulationUsesTheFastestFiniteVisualClock()
    {
        var speed = PresentationSpeed.Normalize(float.PositiveInfinity);

        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(speed), Is.True);
            Assert.That(speed, Is.EqualTo(PresentationSpeed.MaxVisualMultiplier));
        });
    }

    [Test]
    public void ReturningFromMaxRestoresTheRequestedClockExactly()
    {
        _ = PresentationSpeed.Normalize(float.PositiveInfinity);

        Assert.That(PresentationSpeed.Normalize(1f), Is.EqualTo(1f));
    }

    [TestCase(0f, PresentationSpeed.MinVisualMultiplier)]
    [TestCase(2f, 2f)]
    [TestCase(4f, 4f)]
    [TestCase(50f, PresentationSpeed.MaxVisualMultiplier)]
    [TestCase(500f, PresentationSpeed.MaxVisualMultiplier)]
    public void FiniteSpeedsAreKeptInsideAnimatorSafeBounds(float input, float expected)
    {
        Assert.That(PresentationSpeed.Normalize(input), Is.EqualTo(expected));
    }

    [Test]
    public void InvalidNegativeOrNanSentinelsFallBackToNormalSpeed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PresentationSpeed.Normalize(float.NegativeInfinity), Is.EqualTo(1f));
            Assert.That(PresentationSpeed.Normalize(float.NaN), Is.EqualTo(1f));
        });
    }
}
