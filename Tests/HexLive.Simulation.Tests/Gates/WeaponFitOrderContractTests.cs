using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§75A / bug #136: world-AABB fitting must observe the final weapon pose.</summary>
public sealed class WeaponFitOrderContractTests
{
    private static readonly string ActorPath = Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs");

    [Test]
    public void HandPosePrecedesFitAndConfigScaleRemainsAMultiplier()
    {
        var source = File.ReadAllText(ActorPath);
        var methodStart = source.IndexOf("private void SetHandProp(string itemId)", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private void SyncHandedness", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];
        var authoredStart = method.IndexOf(
            "if (Config.GearLibrary.TryGetHandPose", StringComparison.Ordinal);
        var authoredEnd = method.IndexOf("// §54.12", authoredStart, StringComparison.Ordinal);
        var authored = method[authoredStart..authoredEnd];

        var rotation = authored.IndexOf("_handProp.transform.localRotation =", StringComparison.Ordinal);
        var fit = authored.IndexOf(
            "ApplyObjectFitScale(_handProp, itemId, prefabLocalScale, cfgScale)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(rotation, Is.GreaterThanOrEqualTo(0));
            Assert.That(fit, Is.GreaterThan(rotation),
                "ObjectFit measured the hand prop before its final authored rotation.");
            Assert.That(method, Does.Contain(
                "Vector3.Scale(prefabLocalScale, fineMultiplier) * fit"),
                "GearConfig scale must multiply the fitted prefab, not replace its authored scale.");
        });
    }

    [Test]
    public void BackRotationPrecedesFitAndFitPrecedesBoundsCentering()
    {
        var source = File.ReadAllText(ActorPath);
        var methodStart = source.IndexOf("public void SetBackWeapon(string itemId)", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public void SetThermal", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        var finalRotation = method.IndexOf(
            "_backProp.transform.localRotation = workingLocal.sqrMagnitude", StringComparison.Ordinal);
        var fittedScale = method.IndexOf(
            "ApplyObjectFitScale(_backProp, itemId, prefabLocalScale, Vector3.one);",
            finalRotation, StringComparison.Ordinal);
        var centreBounds = method.IndexOf("var combined = renderers[0].bounds;", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(finalRotation, Is.GreaterThanOrEqualTo(0));
            Assert.That(fittedScale, Is.GreaterThan(finalRotation),
                "Back ObjectFit still runs before the final one/two-handed rotation.");
            Assert.That(centreBounds, Is.GreaterThan(fittedScale),
                "Back bounds must be recomputed after final rotation and fitting before centering.");
        });
    }

    [Test]
    public void SpearAssetDoesNotCarryAnAbsoluteScaleOverride()
    {
        var spear = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "RuntimeSource", "Gear",
            "spear.asset"));
        var gearConfig = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Config", "GearConfig.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(spear, Does.Contain("handLocalScale: {x: 1, y: 1, z: 1}"));
            Assert.That(gearConfig, Does.Contain("Тонкий МНОЖИТЕЛЬ масштаба поверх ObjectFit"));
        });
    }
}
