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
        public void CameraDrivenFoliageCullingWasRemovedCompletely()
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
        public void SelectedCharacterSpheresDriveOnlyStandingPalmLeafGreen()
        {
            var renderer = File.ReadAllText(Presentation("Rendering", "HexWorldRenderer.cs"));
            var sphere = File.ReadAllText(Presentation(
                "Rendering", "CharacterPalmCrownCutoutSphere.cs"));
            var standing = File.ReadAllText(Presentation(
                "Environment", "StandingPalmCrownCutout.cs"));
            var palmFactory = File.ReadAllText(Presentation(
                "Environment", "PalmTreeFactory.cs"));
            var fallenFactory = File.ReadAllText(Presentation(
                "Environment", "PalmCrownFactory.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain(
                    "AddComponent<CharacterPalmCrownCutoutSphere>()"));
                Assert.That(renderer, Does.Contain("PalmCrownCutoutRadiusFactor = 3f"));
                Assert.That(renderer, Does.Contain("HexRadius * PalmCrownCutoutRadiusFactor"));
                Assert.That(sphere, Does.Contain("_CharacterPalmCutoutSpheres"));
                Assert.That(sphere, Does.Contain("Construct(int npcId"));
                Assert.That(sphere, Does.Contain("NpcSelection.Contains(marker._npcId)"));
                Assert.That(sphere, Does.Contain("TryGetBodyCenter"));
                Assert.That(standing, Does.Contain("CrownSurface = \"LeafGreen\""));
                Assert.That(standing, Does.Contain("WoodBark was deliberately left untouched"));
                Assert.That(palmFactory, Does.Contain("StandingPalmCrownCutout.Apply(palm)"));
                Assert.That(fallenFactory, Does.Not.Contain("StandingPalmCrownCutout"));
            });
        }

        [Test]
        public void CrownShaderClipsForwardAndDepthButKeepsWholeShadow()
        {
            var shaderPath = Path.Combine(RepoPaths.Root, "Assets", "Resources",
                "HexLive", "Shaders", "StandingPalmCrownCutout.shader");
            var shader = File.ReadAllText(shaderPath);
            var shadowStart = shader.IndexOf(
                "Name \"ShadowCaster\"", System.StringComparison.Ordinal);
            var depthStart = shader.IndexOf(
                "Name \"DepthOnly\"", System.StringComparison.Ordinal);
            var shadowPass = shader.Substring(shadowStart, depthStart - shadowStart);

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain("_CharacterPalmCutoutSpheres[64]"));
                Assert.That(shader, Does.Contain("clip(CharacterSphereOutside(positionWS))"));
                Assert.That(shader, Does.Contain("Name \"ForwardLit\""));
                Assert.That(shader, Does.Contain("Name \"ShadowCaster\""));
                Assert.That(shader, Does.Contain("Name \"DepthOnly\""));
                Assert.That(shadowPass, Does.Not.Contain("ClipCharacterSpheres"));
                Assert.That(shadowPass, Does.Contain("ClipAuthoredAlpha(input.uv);"));
                Assert.That(Count(shader, "ClipCharacterSpheres(input.positionWS);"),
                    Is.EqualTo(2));
            });
        }

        private static int Count(string text, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
