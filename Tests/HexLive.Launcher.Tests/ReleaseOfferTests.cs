using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Launcher.Tests;

public sealed class ReleaseOfferTests
{
    [TestCase("0.1.113", "0.1.112", 19, true)]
    [TestCase("0.1.113", "0.1.113", 19, false)]
    [TestCase("0.1.99", "0.1.112", 19, false)]
    [TestCase("0.1.113", "0.1.112", 20, false)]
    [TestCase("broken", "0.1.112", 19, false)]
    [TestCase("0.1.113", "broken", 19, false)]
    public void OnlyNewerCompatibleReleasesAreOffered(string latest, string installed, int protocol, bool expected)
    {
        var release = new ClientReleaseOffer { version = latest,
            playerRelease = new ClientReleaseOffer.ReleasePayload { protocolVersion = protocol } };
        Assert.That(release.IsNewerCompatible(installed, 19), Is.EqualTo(expected));
    }
}
