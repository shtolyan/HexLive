using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class BikerPantsScaleContractTests
{
    [Test]
    public void EveryActressGetsExactlyOneStandardScaleStep_Bug190()
    {
        var prefab = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "Wear",
            "clothing.pants_biker", "BikerPants.prefab"));
        var expected = new Dictionary<int, float>
        {
            [0] = 1.01f,
            [1] = 1.04f,
            [2] = 1.01f,
            [5] = 1.01f
        };

        var matches = Regex.Matches(prefab,
            @"- actorName: (?<actor>[0-9]+)\r?\n\s+scale: (?<scale>[0-9.]+)");
        Assert.That(matches, Has.Count.EqualTo(expected.Count));
        foreach (Match match in matches)
        {
            var actor = int.Parse(match.Groups["actor"].Value, CultureInfo.InvariantCulture);
            var scale = float.Parse(match.Groups["scale"].Value, CultureInfo.InvariantCulture);
            Assert.That(expected, Does.ContainKey(actor));
            Assert.That(scale, Is.EqualTo(expected[actor]),
                $"ActorName={actor} must retain her authored fit plus exactly +0.01.");
        }
    }
}
