using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class RainEmitterPresentationContractTests
{
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
}
