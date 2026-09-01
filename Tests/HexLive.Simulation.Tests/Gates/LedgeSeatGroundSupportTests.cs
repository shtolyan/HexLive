using System.IO;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>Bug #346: the seat tile and its per-step lift share one base.</summary>
public sealed class LedgeSeatGroundSupportTests
{
    private static readonly TileCoord High = new(4, -2);
    private static readonly TileCoord Low = new(5, -2);

    [Test]
    public void LedgeRootKeepsUpperSnapshotTileWhenBoundaryRoundsToLowerNeighbour()
    {
        var npc = LedgeNpc(High, stepsUp: 0);

        Assert.That(NpcGroundSupport.Select(npc, Low), Is.EqualTo(High),
            "PlaceAtEdge already committed the upper support tile; rounding the " +
            "seam position must not lower the rendered root by one elevation step.");
    }

    [Test]
    public void LedgeRootIsUnchangedWhenBoundaryAlreadyRoundsToUpperNeighbour()
    {
        var npc = LedgeNpc(High, stepsUp: 0);

        Assert.That(NpcGroundSupport.Select(npc, High), Is.EqualTo(High));
    }

    [Test]
    public void RestBelowLedgeKeepsLowerBaseAndAddsExactlyOneStep()
    {
        var npc = LedgeNpc(Low, stepsUp: 1);
        var selected = NpcGroundSupport.Select(npc, High);
        const int lowElevation = 1;
        const int highElevation = 2;

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.EqualTo(Low),
                "Rest stays below the ledge; selecting the high rounded tile would double-lift it.");
            Assert.That(lowElevation + npc.LedgeSeatStepsUp, Is.EqualTo(highElevation),
                "The existing per-step pose offset alone must reach the seat surface.");
        });
    }

    [Test]
    public void OrdinaryAndFurnitureActorsStillFollowGroundTileUnderBody()
    {
        var npc = new NpcSnapshot
        {
            Tile = High,
            IsLedgeSit = false
        };

        Assert.That(NpcGroundSupport.Select(npc, Low), Is.EqualTo(Low));
    }

    [Test]
    public void RendererUsesTheSelectorForRootAndWatchdogButNotHouseCutaway()
    {
        var renderer = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Rendering", "HexWorldRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(Count(renderer, "ActorGroundY(ActorSupportTile(npc))"), Is.EqualTo(2),
                "The live root and the watchdog fallback must use the same support tile.");
            Assert.That(renderer, Does.Contain(
                "NpcGroundSupport.Select(npc, GroundTileUnder(npc))"));
            Assert.That(renderer, Does.Contain("selectedTile = GroundTileUnder(npc);"),
                "§141 house cutaway remains tied to the continuous body position.");
        });
    }

    private static NpcSnapshot LedgeNpc(TileCoord tile, int stepsUp) => new()
    {
        Tile = tile,
        IsLedgeSit = true,
        LedgeSeatStepsUp = stepsUp
    };

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
