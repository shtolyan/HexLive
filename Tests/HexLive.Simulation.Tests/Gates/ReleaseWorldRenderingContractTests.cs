using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class ReleaseWorldRenderingContractTests
    {
        [Test]
        public void ReleaseKeepsRuntimeGeneratedWorldOffGpuResidentDrawer()
        {
            var pipeline = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "Settings", "PC_RPAsset.asset"));
            var builder = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityDebug", "Editor",
                "HexLiveReleaseBuilder.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(pipeline, Does.Contain("m_GPUResidentDrawerMode: 0"));
                Assert.That(pipeline,
                    Does.Contain("m_GPUResidentDrawerEnableOcclusionCullingInCameras: 0"));
                Assert.That(builder, Does.Contain("ValidateRuntimeGeneratedWorldRendering();"));
                Assert.That(builder, Does.Contain("residentDrawer.intValue != 0"));
            });
        }
    }
}
