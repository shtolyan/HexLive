using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class RainEmitterPresentationContractTests
{
    [Test]
    public void RainAndHitBloodKeepTheirBootstrapParticlePipeline_Bug253()
    {
        var graphics = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "ProjectSettings", "GraphicsSettings.asset"));
        var blood = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "BloodSplashVfx.cs"));
        var bloodRoot = Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "HexLive", "VFX", "ToonBlood");

        Assert.Multiple(() =>
        {
            Assert.That(graphics, Does.Contain("0406db5a14f94604a8c57ccfbc9f3b46"),
                "URP Particles/Unlit must not be stripped from the Player.");
            Assert.That(blood, Does.Contain(
                "Resources.Load<GameObject>(\"HexLive/VFX/ToonBlood/BloodSplatDirectional\")"));
            Assert.That(blood, Does.Not.Contain("AtomicResources.Load<GameObject>"),
                "Mandatory hit effects cannot wait for the live content registry.");
            Assert.That(File.Exists(Path.Combine(bloodRoot, "BloodSplatDirectional.prefab")),
                Is.True);
            Assert.That(File.Exists(Path.Combine(bloodRoot, "BloodSplatDirectional2.prefab")),
                Is.True);
            Assert.That(File.Exists(Path.Combine(bloodRoot, "BloodSplatWide.prefab")), Is.True);
        });
    }

    [Test]
    public void DenseRainVolumeFollowsCameraWithoutDraggingWorldParticles_Bug189()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));

        Assert.That(source, Does.Contain(
                "main.simulationSpace = ParticleSystemSimulationSpace.World"),
            "Emitted drops must remain fixed in world space when the camera moves.");
        Assert.That(source, Does.Contain(
                "_rain.transform.position = new Vector3(focus.x, emitterY, focus.z)"),
            "The 60×60 emitter must follow the viewed part of an expanded island.");
        Assert.That(source, Does.Contain("shape.scale = new Vector3(60f, 60f, 1f)"),
            "Keep the measured dense local volume instead of filling BigIsland with particles.");
        Assert.That(source, Does.Not.Contain(
                "_rain.transform.localPosition = new Vector3(0f, 30f, 0f)"),
            "The live emitter must not remain pinned to the old world centre.");
    }

    [Test]
    public void RainContactsUseTheActualHexAndAnimatedWaterHeight_Bug257()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Rendering",
            "HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "HexSpatialMath.WorldToTile(new Float2(position.x, position.z))"));
            Assert.That(source, Does.Contain("var surfaceY = GroundY(tile)"));
            Assert.That(source, Does.Contain("WaterWave.HeightNow(position.x, position.z)"));
            Assert.That(source, Does.Contain("EmitRainSplash(new Vector3(position.x, surfaceY + 0.03f"));
            Assert.That(source, Does.Not.Contain("RainGroundPlane"),
                "An infinite plane can only splash at one elevation band.");
            Assert.That(source, Does.Not.Contain("ParticleSystemCollisionType.Planes"));
        });
    }
}
