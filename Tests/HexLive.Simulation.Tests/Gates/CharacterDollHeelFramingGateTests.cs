using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§31.10A / bug #132: heel lift is not a portrait anchor.</summary>
public sealed class CharacterDollHeelFramingGateTests
{
    private static string Presentation(params string[] parts) =>
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            Path.Combine(parts));

    [Test]
    public void BodyBonesExposesTheActuallyAppliedHeelLift()
    {
        var source = File.ReadAllText(Presentation("Wearing", "BodyBones.cs"));

        Assert.That(source, Does.Contain(
            "public float AppliedHeelLift => _heel.Any ? _heel.lift * _heelPoseWeight : 0f;"));
    }

    [Test]
    public void PortraitRemovesCopiedHeelLiftBeforeStudioPose()
    {
        var source = File.ReadAllText(Presentation("UI", "CharacterDollStage.cs"));
        var buildStart = source.IndexOf("private void BuildClone", StringComparison.Ordinal);
        var poseStart = source.IndexOf("private void ApplyStudioPose", buildStart,
            StringComparison.Ordinal);
        var build = source[buildStart..poseStart];

        var capture = build.IndexOf("sourceBodyBones.AppliedHeelLift",
            StringComparison.Ordinal);
        var neutralize = build.IndexOf(
            "clonedHips.position -= _clone.transform.up * copiedHeelLift",
            StringComparison.Ordinal);
        var rebind = build.IndexOf("_animator.Rebind()", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(capture, Is.GreaterThanOrEqualTo(0));
            Assert.That(neutralize, Is.GreaterThan(capture));
            Assert.That(rebind, Is.GreaterThan(neutralize),
                "The copied live offset must be removed before the neutral studio pose is evaluated.");
        });
    }
}

}
