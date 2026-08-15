using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§31B.7 / bug #142: Jana's running-shorts fit is one authored step larger.</summary>
public sealed class FitShortsScaleContractTests
{
    [Test]
    public void JanaFitShortsUseExactlyOneStandardScaleStep()
    {
        var prefab = File.ReadAllText(Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLiveContent", "Wear", "clothing.shorts_fit",
            "FitShorts.prefab"));

        var match = Regex.Match(prefab,
            @"- actorName: 5\r?\n\s+scale: (?<scale>[0-9.]+)");

        Assert.That(match.Success, Is.True,
            "FitShorts must retain a dedicated serialized Jana (ActorName=5) fit.");
        Assert.That(float.Parse(match.Groups["scale"].Value,
                System.Globalization.CultureInfo.InvariantCulture),
            Is.EqualTo(1.01f),
            "One normal WardrobeTest scale step is +0.01 from the authored 1.00 fit.");
    }
}

}
