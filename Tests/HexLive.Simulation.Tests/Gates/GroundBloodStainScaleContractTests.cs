using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class GroundBloodStainScaleContractTests
{
    [Test]
    public void GroundBleedingVisuals_UseOneThirdHorizontalScaleFactor()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Assets")))
        {
            root = Directory.GetParent(root)?.FullName;
        }

        Assert.That(root, Is.Not.Null, "project root");
        var source = File.ReadAllText(Path.Combine(root!,
            "Assets/HexLive/UnityPresentation/Environment/GroundBloodStains.cs"));

        Assert.That(source, Does.Contain("private const float StainScaleFactor = 1f / 3f;"));
        Assert.That(source, Does.Contain("0.09f * StainScaleFactor"));
        Assert.That(source, Does.Contain("0.26f * StainScaleFactor"));
        Assert.That(source, Does.Contain("0.46f * StainScaleFactor"));
        Assert.That(source, Does.Contain("0.07f * StainScaleFactor"));
        Assert.That(source, Does.Contain("1.0f * StainScaleFactor"));
        Assert.That(source, Does.Contain("0.16f * StainScaleFactor"));
    }
}
