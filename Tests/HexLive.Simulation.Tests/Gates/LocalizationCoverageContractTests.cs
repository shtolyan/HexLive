using System;
using System.IO;
using System.Text.RegularExpressions;
using HexLive.Simulation.AI;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§58.5: player-facing enum names must never leak into the UI.</summary>
public sealed class LocalizationCoverageContractTests
{
    [Test]
    public void EveryGoalHasEnglishAndRussianLocalization()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        foreach (var goal in Enum.GetNames<GoalType>())
        {
            var marker = $"    - Term: goal.{goal}\n";
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                $"goal.{goal} is missing and would be rendered as a raw enum name");
            var next = source.IndexOf("    - Term: ", start + marker.Length,
                StringComparison.Ordinal);
            var block = source[start..(next < 0 ? source.Length : next)];
            Assert.That(Regex.IsMatch(block,
                    @"Languages:\s*\n\s*-\s+.+\n\s*-\s+.+",
                    RegexOptions.CultureInvariant),
                Is.True, $"goal.{goal} must have non-empty English and Russian translations");
        }
    }
}
