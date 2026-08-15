using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Bug #129/#130: a fitted garment's import-pose AABB is not an animation
/// envelope. If Unity stops skinning outside that tiny box, moving the camera
/// can cull the whole garment while its bones are still plainly on screen.
/// </summary>
public sealed class GarmentRendererBoundsGateTests
{
    [Test]
    public void ReportedFighterArmguardsKeepTheirSkinnedBoundsLive()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLiveContent", "Wear", "clothing.armguards_fighter",
            "FighterArmGuards.prefab"));

        Assert.That(source, Does.Contain("m_UpdateWhenOffscreen: 1"),
            "The reported armguards retain a 0.04 x 0.07 import-pose AABB; " +
            "without live skinned bounds Unity culls them as the arms animate.");
    }

    [Test]
    public void ReportedNerdBlouseKeepsItsSkinnedBoundsLive()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLiveContent", "Wear", "clothing.blouse_nerd",
            "NerdBlouse.prefab"));

        Assert.That(source, Does.Contain("m_UpdateWhenOffscreen: 1"),
            "The reported blouse must not rely on its torso-only import AABB.");
    }

    [Test]
    public void EveryEquippedGarmentEnablesLiveSkinnedBounds()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root,
            "Assets", "HexLive", "UnityPresentation", "Wearing", "Wear.cs"));

        Assert.That(source, Does.Contain("_meshRenderer.updateWhenOffscreen = true;"),
            "Fixing two prefabs is not enough: all 192 garments and future " +
            "extracts pass through Wear.Construct and need the same culling contract.");
    }
}

}
