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
    public void PhotoOwnsItsFrameInsteadOfBorrowingTheCardAnchor()
    {
        var cache = File.ReadAllText(Presentation("UI", "NpcPortraitCache.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(cache, Does.Contain("HeadCentreLiftMeters"),
                "The shared face anchor is composed for the WIDE card; the round " +
                "photo needs the head centred with its hair.");
            Assert.That(cache, Does.Contain("var aim = face + up * (HeadCentreLiftMeters * scale)"));
            Assert.That(cache, Does.Contain("eye = aim + forward * (FaceDistanceMeters * scale)"),
                "Aim and lens must rise together — lifting the lens alone tilts " +
                "the camera and shoots her from above.");
            Assert.That(cache, Does.Contain("LookRotation(aim - eye"));
            Assert.That(cache, Does.Not.Contain("EyeLiftMeters"));
        });
    }
}
