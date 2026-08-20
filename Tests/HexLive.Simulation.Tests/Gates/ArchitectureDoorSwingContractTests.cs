using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// #181: the shipping architecture door owns its hinge and both poses in the
/// Blender source. Presentation consumes those markers; it must not repair a
/// wrong sign with a definition-specific runtime angle.
/// </summary>
public sealed class ArchitectureDoorSwingContractTests
{
    [Test]
    public void ProductionAndConstructorConsumeTheSameAuthoredDoorMarkers()
    {
        var production = File.ReadAllText(RepoFile("Assets", "HexLive", "UnityPresentation",
            "Environment", "ArchitectureModuleView.cs"));
        var factory = File.ReadAllText(RepoFile("Assets", "HexLive", "UnityPresentation",
            "Environment", "BlueprintArchitectureFactory.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(production, Does.Contain("BlueprintArchitectureFactory.InstantiateModel("),
                "Production must load the same architecture model as the constructor.");
            Assert.That(production, Does.Contain("HL_Door_State_Closed"));
            Assert.That(production, Does.Contain("HL_Door_State_Open"));
            Assert.That(production, Does.Contain("visual.ConfigurePoses("),
                "Production must consume the FBX poses instead of inventing an angle.");
            Assert.That(factory, Does.Contain("HL_Door_State_Closed"));
            Assert.That(factory, Does.Contain("HL_Door_State_Open"));
            Assert.That(factory, Does.Contain("visual.ConfigurePoses("),
                "Constructor preview must consume the same FBX poses.");
        });
    }

    [Test]
    public void AuthoredOpenPoseSwingsOutwardForAllSixFootprintYaws()
    {
        var source = File.ReadAllText(RepoFile("Tools", "blender", "build_arch_elements.py"));
        var match = Regex.Match(source,
            @"DOOR_OPEN_DEGREES\s*=\s*(?<angle>-?\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant);
        Assert.That(match.Success, Is.True, "Door swing must be an explicit authored constant.");
        var authoredSwing = double.Parse(match.Groups["angle"].Value,
            CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(authoredSwing, Is.EqualTo(-72d),
                "Positive 72° is the inward Blender pose for canonical bay 7.");
            Assert.That(source, Does.Contain("new_empty(\"HL_Door_Pivot\", s2, loc=(0, SEAM, 0))"),
                "The hinge must remain authored on the bay seam.");
            Assert.That(source, Does.Contain("HL_Door_State_Closed"));
            Assert.That(source, Does.Contain("HL_Door_State_Open"));
        });

        var edge = BuildingRules.HutDoorBay / 2;
        var half = BuildingRules.HutDoorBay % 2;
        var a0 = Degrees(90d + edge * 60d);
        var a1 = Degrees(90d + (edge + 1) * 60d);
        var t = half == 0 ? 0.25d : 0.75d;
        var radius = HexSpatialMath.HexRadius;
        var ax = Math.Cos(a0) * radius;
        var az = Math.Sin(a0) * radius;
        var bx = Math.Cos(a1) * radius;
        var bz = Math.Sin(a1) * radius;
        var midpoint = (X: ax + (bx - ax) * t, Z: az + (bz - az) * t);
        var tangent = Normalize((X: bx - ax, Z: bz - az));
        var authoredOpen = Rotate(tangent, authoredSwing);

        for (var yawStep = 0; yawStep < 6; yawStep++)
        {
            var yaw = yawStep * 60d;
            var outward = Normalize(Rotate(midpoint, yaw));
            var closed = Rotate(tangent, yaw);
            var open = Rotate(authoredOpen, yaw);
            var openDot = Dot(open, outward);
            var closedDot = Dot(closed, outward);

            Assert.Multiple(() =>
            {
                Assert.That(openDot, Is.GreaterThan(0.99d),
                    $"yawStep {yawStep}: open leaf must point out of the floored hex.");
                Assert.That(openDot, Is.GreaterThan(closedDot),
                    $"yawStep {yawStep}: opening must move toward outside, not inside.");
            });
        }
    }

    private static double Degrees(double value) => value * Math.PI / 180d;

    private static (double X, double Z) Rotate((double X, double Z) value, double degrees)
    {
        var radians = Degrees(degrees);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return (value.X * cos - value.Z * sin, value.X * sin + value.Z * cos);
    }

    private static (double X, double Z) Normalize((double X, double Z) value)
    {
        var length = Math.Sqrt(value.X * value.X + value.Z * value.Z);
        return (value.X / length, value.Z / length);
    }

    private static double Dot((double X, double Z) a, (double X, double Z) b) =>
        a.X * b.X + a.Z * b.Z;

    private static string RepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            Array.Copy(relativePath, 0, parts, 1, relativePath.Length);
            var candidate = Path.Combine(parts);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(string.Join('/', relativePath));
    }
}
