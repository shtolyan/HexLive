using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§40.10 / bug #203: tearing keeps URP lighting and tangent-space detail.</summary>
public sealed class GarmentTearLightingContractTests
{
    [Test]
    public void TearShaderKeepsAdditionalLightsAndMirroredNormals()
    {
        var shader = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "GarmentTear.shader"));

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("_ADDITIONAL_LIGHT_SHADOWS"));
            Assert.That(shader, Does.Contain("_LIGHT_COOKIES"));
            Assert.That(shader, Does.Contain("input.tangentOS.w * GetOddNegativeScale()"));
            Assert.That(shader, Does.Contain(
                "inputData.vertexLighting = VertexLighting(input.positionWS, normalWS)"));
            Assert.That(shader, Does.Contain(
                "inputData.normalizedScreenSpaceUV"));
            Assert.That(shader, Does.Contain(
                "inputData.shadowMask = half4(1, 1, 1, 1)"));
        });
    }
}
