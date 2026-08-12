using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class PalmCrownCutoutContractTests
    {
        private static string Presentation(params string[] parts) =>
            Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                Path.Combine(parts));

        [Test]
        public void OldCameraFoliageCullingWasRemovedCompletely()
        {
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Presentation("Rendering", "CameraFoliageCuller.cs")),
                    Is.False);
                Assert.That(File.Exists(Presentation("Environment", "FoliageOccluder.cs")),
                    Is.False);

                var renderer = File.ReadAllText(Presentation("Rendering", "HexWorldRenderer.cs"));
                var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));
                var bootstrap = File.ReadAllText(Presentation(
                    "Bootstrap", "PrototypeRuntimeBootstrap.cs"));
                Assert.That(renderer, Does.Not.Contain("FoliageOccluder"));
                Assert.That(camera, Does.Not.Contain("CameraFoliageCuller"));
                Assert.That(camera, Does.Not.Contain("HasFramedSubject"));
                Assert.That(bootstrap, Does.Not.Contain("CameraFoliageCuller"));
            });
        }

        [Test]
        public void OneCameraManagerSwapsOnlyStandingPalmLeafGreen()
        {
            var renderer = File.ReadAllText(Presentation("Rendering", "HexWorldRenderer.cs"));
            var camera = File.ReadAllText(Presentation("Input", "RtsCameraController.cs"));
            var managerPath = Presentation("Rendering", "CameraPalmCrownVisibility.cs");
            var manager = File.ReadAllText(managerPath);
            var standing = File.ReadAllText(Presentation(
                "Environment", "StandingPalmCrownVisibility.cs"));
            var palmFactory = File.ReadAllText(Presentation(
                "Environment", "PalmTreeFactory.cs"));
            var fallenFactory = File.ReadAllText(Presentation(
                "Environment", "PalmCrownFactory.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(managerPath), Is.True);
                Assert.That(File.Exists(Presentation(
                    "Rendering", "CameraPalmCrownCutoutSphere.cs")), Is.False);
                Assert.That(File.Exists(Presentation(
                    "Rendering", "CharacterPalmCrownCutoutSphere.cs")), Is.False);
                Assert.That(renderer, Does.Not.Contain("CharacterPalmCrownCutoutSphere"));
                Assert.That(camera, Does.Contain("PalmCrownHideDistance = 2.1f"));
                Assert.That(camera, Does.Contain("PalmCrownShowDistance = 2.5f"));
                Assert.That(camera, Does.Contain("PalmCrownCheckMovement = 0.1f"));
                Assert.That(camera, Does.Contain(
                    "gameObject.AddComponent<CameraPalmCrownVisibility>()"));
                Assert.That(manager, Does.Contain("bounds.SqrDistance(cameraPosition)"));
                Assert.That(manager, Does.Contain("StandingPalmCrownVisibility.RegistryVersion"));
                Assert.That(manager, Does.Not.Contain("Shader.SetGlobalVector"));
                Assert.That(manager, Does.Not.Contain("NpcSelection"));
                Assert.That(standing, Does.Contain("CrownSurface = \"LeafGreen\""));
                Assert.That(standing, Does.Contain("mesh.GetSubMesh(materialIndex).bounds"));
                Assert.That(standing, Does.Contain("entry.Renderer.sharedMaterials = hidden"));
                Assert.That(standing, Does.Contain("WoodBark was deliberately left untouched"));
                Assert.That(palmFactory, Does.Contain("StandingPalmCrownVisibility.Apply(palm)"));
                Assert.That(fallenFactory, Does.Not.Contain("StandingPalmCrownVisibility"));
            });
        }

        [Test]
        public void HiddenCrownShaderHasOnlyAnAuthoredAlphaShadowPass()
        {
            var shaderPath = Path.Combine(RepoPaths.Root, "Assets", "Resources",
                "HexLive", "Shaders", "StandingPalmCrownShadowOnly.shader");
            var shader = File.ReadAllText(shaderPath);
            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain("Shader \"HexLive/StandingPalmCrownShadowOnly\""));
                Assert.That(shader, Does.Contain("Name \"ShadowCaster\""));
                Assert.That(shader, Does.Contain("ClipAuthoredAlpha(input.uv);"));
                Assert.That(shader, Does.Not.Contain("Name \"ForwardLit\""));
                Assert.That(shader, Does.Not.Contain("Name \"DepthOnly\""));
                Assert.That(shader, Does.Not.Contain("_CameraPalmCrownCutoutSphere"));
            });
        }
    }
}
