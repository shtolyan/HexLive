using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class NpcLimpPoseContractTests
{
    [Test]
    public void LimpAnimationRunsOnlyWhileActorIsWalking()
    {
        var path = Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs");
        var source = File.ReadAllText(path);
        var helperStart = source.IndexOf(
            "private void SyncLimpingAnimator()", StringComparison.Ordinal);
        var helperEnd = source.IndexOf(
            "/// <summary>§118", helperStart, StringComparison.Ordinal);
        Assert.That(helperStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(helperEnd, Is.GreaterThan(helperStart));
        var helper = source[helperStart..helperEnd];

        Assert.Multiple(() =>
        {
            Assert.That(helper, Does.Contain(
                "_posture == \"Limp\" && _wasWalking"),
                "An impaired leg may select the limp gait, but idle must stay straight.");
            Assert.That(source, Does.Contain(
                "_animator.SetFloat(SpeedParam, walking ? 1f : 0f, 0.05f, Time.deltaTime);\n" +
                "        SyncLimpingAnimator();"),
                "The limp flag must follow the same authoritative movement decision as Speed.");
            Assert.That(source, Does.Not.Contain(
                "_animator.SetBool(LimpingParam, _posture == \"Limp\");"),
                "SetPosture must not pin a stationary actor in a looping stride pose.");
        });
    }
}
