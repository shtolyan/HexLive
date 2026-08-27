using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§21: authored presentation tuning must reach every game backend.</summary>
public sealed class HexTuningAtomicLoadContractTests
{
    [Test]
    public void LoadingScreenAppliesAtomicHexTuningBeforeChoosingBackend()
    {
        var loading = Read("Assets", "HexLive", "UnityPresentation", "UI", "LoadingScreen.cs");
        var load = loading.IndexOf("Config.HexTuning.LoadAtomic", StringComparison.Ordinal);
        var backend = loading.IndexOf("if (_connectChosen)", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(load, Is.GreaterThanOrEqualTo(0));
            Assert.That(backend, Is.GreaterThan(load),
                "Remote and local rendering must receive the same visual tuning.");
            Assert.That(loading, Does.Contain("while (!hexTuningReady)"),
                "World startup must wait for the verified tuning object.");
        });
    }

    [Test]
    public void AuthoredEdgeSeatValuesMatchBothFallbackLayers()
    {
        var asset = Read("Assets", "HexLiveContent", "RuntimeSource", "HexTuningConfig.asset");
        var config = Read("Assets", "HexLive", "UnityPresentation", "Config", "HexTuningConfig.cs");
        var view = Read("Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs");
        var loader = Read("Assets", "HexLive", "UnityPresentation", "Config", "HexTuning.cs");

        Assert.Multiple(() =>
        {
            Assert.That(asset, Does.Contain("ledgeSeatLift: -0.344"));
            Assert.That(asset, Does.Contain("ledgeSeatBack: 0.2"));
            Assert.That(config, Does.Contain("ledgeSeatLift = -0.344f"));
            Assert.That(config, Does.Contain("ledgeSeatBack = 0.2f"));
            Assert.That(view, Does.Contain("LedgeSeatLift = -0.344f"));
            Assert.That(view, Does.Contain("LedgeSeatBack = 0.20f"));
            Assert.That(loader, Does.Contain("\"config\", \"hextuningconfig\""));
            Assert.That(loader, Does.Contain("Apply(loaded.Asset)"));
        });
    }

    [Test]
    public void FurnitureAndBedKeepTheirOwnAuthoredHeightContracts()
    {
        var renderer = Read("Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs");
        var stump = Read("Assets", "HexLive", "UnityPresentation", "Environment",
            "StumpFactory.cs");
        var bed = Read("Assets", "HexLive", "UnityPresentation", "Environment",
            "BedAssembly.cs");

        Assert.Multiple(() =>
        {
            Assert.That(renderer, Does.Contain(
                "pose = new Pose(GetObjectAnchorPosition(snapshot, seat), rotation)"));
            Assert.That(renderer, Does.Contain("surfaceY = point.position.y"));
            Assert.That(stump, Does.Contain("public const float Height = 0.30f"));
            Assert.That(bed, Does.Contain("public const float SleepRootLocalY = 0.37f"));
        });
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoPaths.Root }.Concat(parts).ToArray()));
}
