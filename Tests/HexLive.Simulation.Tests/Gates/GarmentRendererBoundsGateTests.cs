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
}

}
