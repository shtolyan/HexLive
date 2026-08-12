using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

public sealed class BedSleepContractTests
{
    private static string Read(params string[] parts) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, Path.Combine(parts)));

    [Test]
    public void EveryProductionBedEntryUsesTheAuthoritativeTransition()
    {
        var execution = Read("Assets", "HexLive", "Simulation", "Runtime",
            "Systems", "Ai", "ExecutionSystem.cs");
        var rescue = Read("Assets", "HexLive", "Simulation", "Runtime",
            "Helpers", "KenshiRescueMath.cs");
        var emergency = Read("Assets", "HexLive", "Simulation", "Runtime",
            "Helpers", "LyingSpot.cs");
        var hutTest = Read("Assets", "HexLive", "UnityPresentation",
            "HutTest", "HutTestBootstrap.cs");

        Assert.Multiple(() =>
        {
            Assert.That(execution, Does.Contain("BedSleep.TryEnter("));
            Assert.That(rescue, Does.Contain("BedSleep.TryEnter("));
            Assert.That(emergency, Does.Contain("BedSleep.TryEnter("));
            Assert.That(hutTest, Does.Contain("BedSleep.TryEnter("));

            Assert.That(hutTest, Does.Not.Contain("sleeping.Position ="));
            Assert.That(hutTest, Does.Not.Contain("sleeping.RotationDegrees ="));
            Assert.That(hutTest, Does.Not.Contain(
                "sleeping.Execution.CurrentInteraction = InteractionType.Sleep"));
            Assert.That(execution + rescue + emergency + hutTest,
                Does.Not.Contain("AlignBodyToObject"));
        });
    }
}

}
