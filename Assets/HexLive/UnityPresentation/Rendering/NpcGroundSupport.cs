using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>
/// Chooses the discrete tile whose surface anchors an actor root.
/// A freely moving body follows the tile reconstructed from its continuous
/// position (§141). A ledge pose is different: its position is authored
/// exactly on a seam junction, while <see cref="NpcSnapshot.Tile"/> and
/// <see cref="NpcSnapshot.LedgeSeatStepsUp"/> describe one coherent seat.
/// Re-rounding that boundary may choose the lower neighbour and lose one
/// elevation step, so the snapshot tile remains authoritative for the pose.
/// </summary>
public static class NpcGroundSupport
{
    public static TileCoord Select(NpcSnapshot npc, TileCoord tileUnderBody) =>
        npc.IsLedgeSit ? npc.Tile : tileUnderBody;
}

}
