using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§29C.3: the crab is half its former footprint and its body, not
/// its route root, is rotated sideways to the travel vector.</summary>
public sealed class CrabPresentationContractTests
{
    [Test]
    public void CrabIsSmallAndMovesSideways()
    {
        var config = Read("Assets", "HexLive", "UnityPresentation", "Config", "MobConfig.cs");
        var renderer = Read("Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs");
        var crab = Read("Assets", "HexLiveContent", "RuntimeSource", "Mobs", "crab.asset");

        Assert.Multiple(() =>
        {
            Assert.That(config, Does.Contain("public float visualYawOffsetDegrees"));
            Assert.That(crab, Does.Contain("footprintFraction: 0.16"));
            Assert.That(crab, Does.Contain("visualYawOffsetDegrees: 90"));
            Assert.That(renderer, Does.Contain(
                "body.transform.localRotation *= Quaternion.Euler("));
            Assert.That(renderer, Does.Contain("config.visualYawOffsetDegrees"));
        });
    }

    private static string Read(params string[] path)
    {
        var fullPath = RepoPaths.Root;
        foreach (var part in path)
        {
            fullPath = Path.Combine(fullPath, part);
        }
        return File.ReadAllText(fullPath);
    }
}

}
