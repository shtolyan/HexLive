using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class NpcLimpPoseContractTests
{
    [Test]
    public void LimpAnimationIsNeverAppliedToTheActor()
    {
        var path = Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs");
        var source = File.ReadAllText(path);
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "_animator.SetBool(LimpingParam, false);"),
                "The legacy Animator branch must be explicitly cleared for pooled actors.");
            Assert.That(source, Does.Not.Contain(
                "_animator.SetBool(LimpingParam, _posture"),
                "No posture hint may re-enable the removed bent-knee animation.");
            Assert.That(source, Does.Not.Contain("var limpClip = ActiveLimpClip();"),
                "Cadence must follow the ordinary gait that is actually playing.");
        });
    }

    [Test]
    public void SimulationProneHintDrivesTheExistingCrawlOverride()
    {
        var path = Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs");
        var source = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "var missingLeg = _posture == \"Crawl\" ||"),
                "The view must not disagree with BodyState.IsProne when a non-severed leg reaches zero function.");
            Assert.That(source, Does.Contain(
                "_winded = winded;\n        RefreshLeglessPresentation();"),
                "A changed authoritative posture must refresh the locomotion override immediately.");
        });
    }

    [Test]
    public void CrawlSlotUsesXBotCrawlingClip()
    {
        var clipMetaPath = Path.Combine(
            RepoPaths.Root,
            "Assets", "ImportedActors", "AnimLibrary", "X Bot@Crawling.fbx.meta");
        var animSetPath = Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLiveContent", "RuntimeSource", "NpcAnimSet.asset");

        var guidMatch = Regex.Match(
            File.ReadAllText(clipMetaPath),
            @"^guid:\s*([0-9a-f]+)\s*$",
            RegexOptions.Multiline);
        Assert.That(guidMatch.Success, Is.True, "Imported crawl clip must have a Unity GUID.");

        var animSet = File.ReadAllText(animSetPath);
        Assert.That(animSet, Does.Match(
            @"(?m)^\s*crawl:\s*\{[^\r\n]*guid:\s*" + guidMatch.Groups[1].Value + @",[^\r\n]*\}\s*$"),
            "NpcAnimSet.crawl must reference X Bot@Crawling, not the legacy Zombie Crawl take.");
    }
}
