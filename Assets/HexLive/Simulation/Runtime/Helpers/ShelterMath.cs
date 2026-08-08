using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// One simulation contract for a completed roof. A tile becomes Indoor only
/// after BuildingBootstrap closes the contour and finishes the leaf roof, so
/// every weather consumer can trust this flag instead of rediscovering the
/// construction state independently.
/// </summary>
public static class ShelterMath
{
    public static bool IsIndoor(WorldState world, TileCoord tile) =>
        world.Tiles.Items.TryGetValue(tile, out var state) &&
        state.Flags.HasFlag(TileFlags.Indoor);

    public static bool RainReaches(WorldState world, TileCoord tile) =>
        world.Environment.IsRaining && !IsIndoor(world, tile);
}

}
