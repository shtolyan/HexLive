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
            var graphics = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "ProjectSettings", "GraphicsSettings.asset"));

            Assert.Multiple(() =>
            {
                Assert.That(pipeline, Does.Contain("m_GPUResidentDrawerMode: 0"));
                Assert.That(pipeline,
                    Does.Contain("m_GPUResidentDrawerEnableOcclusionCullingInCameras: 0"));
                Assert.That(builder, Does.Contain("ValidateRuntimeGeneratedWorldRendering();"));
                Assert.That(builder, Does.Contain("residentDrawer.intValue != 0"));
                Assert.That(builder, Does.Contain("RuntimeLitShaderGuid"));
                Assert.That(graphics,
                    Does.Contain("guid: 933532a4fcc9baf4fa0491de14d08ed7"),
                    "URP/Lit must survive stripping because the world creates materials at runtime.");
            });
        }

        [Test]
        public void WindowsReleaseForcesD3D11ForStartupStability()
        {
            var builder = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityDebug", "Editor",
                "HexLiveReleaseBuilder.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(builder, Does.Contain("BuildPlayerWithSafeGraphicsApi(buildOptions)"));
                Assert.That(builder, Does.Contain("BuildTarget.StandaloneWindows64"));
                Assert.That(builder,
                    Does.Contain("new[] { GraphicsDeviceType.Direct3D11 }"));
                Assert.That(builder,
                    Does.Not.Contain("GraphicsDeviceType.Direct3D12 }"));
                Assert.That(builder,
                    Does.Contain("SetUseDefaultGraphicsAPIs(target, usedDefaultApis)"),
                    "The batch build must restore the interactive Editor preference.");
            });
        }

        [Test]
        public void AtomicScenePrewarmRoutesWorldObjectsToTheirOwningType()
        {
            var prewarm = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
                "ScenePrewarm.cs"));
            var prosthetics = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
                "ProstheticContent.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(prewarm, Does.Contain("TryWorldObjectContentKey("));
                Assert.That(prewarm, Does.Contain("type = \"wear\";"));
                Assert.That(prewarm, Does.Contain("type = \"prosthetic\";"));
                Assert.That(prewarm, Does.Contain("IsPayloadFreeWorldAnchor(definitionId)"));
                Assert.That(prosthetics, Does.Contain("TryWorldDropObjectId("));
                Assert.That(prosthetics, Does.Contain("TryDescribeWorldDrop("));
            });
        }

        [Test]
        public void LoadingCurtainDoesNotPrewarmBackpackMainBundles_Bug273()
        {
            var prewarm = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
                "ScenePrewarm.cs"));
            var snapshotStart = prewarm.IndexOf(
                "private static void WarmSnapshotNpcs", System.StringComparison.Ordinal);
            var snapshotEnd = prewarm.IndexOf(
                "private static void WarmActorPaintMaps", snapshotStart,
                System.StringComparison.Ordinal);
            var snapshotWarm = prewarm[snapshotStart..snapshotEnd];
            var localStart = prewarm.IndexOf(
                "private static void WarmItems", System.StringComparison.Ordinal);
            var localEnd = prewarm.IndexOf(
                "private static void ResolveWorkingSet", localStart,
                System.StringComparison.Ordinal);
            var localWarm = prewarm[localStart..localEnd];

            Assert.Multiple(() =>
            {
                Assert.That(snapshotWarm, Does.Contain("npc.WornItems"));
                Assert.That(snapshotWarm, Does.Contain("npc.HeldItemId"));
                Assert.That(snapshotWarm, Does.Not.Contain("npc.InventoryItems"));
                Assert.That(snapshotWarm, Does.Not.Contain("npc.InventoryContainers"));
                Assert.That(snapshotWarm, Does.Not.Contain("npc.FavoriteWeaponId"));
                Assert.That(localWarm, Does.Not.Contain("npc.Inventory.Items"));
            });
        }
    }
}
