using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

internal static class HygieneMath
{
    public static bool IsShoreTile(WorldState world, TileCoord tile)
    {
        if (!world.Tiles.Items.TryGetValue(tile, out var here) ||
            here.Flags.HasFlag(TileFlags.Water) || !here.Flags.HasFlag(TileFlags.Walkable))
        {
            return false;
        }

        foreach (var direction in HexDirection.All)
        {
            var neighbour = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            if (world.Tiles.Items.TryGetValue(neighbour, out var adjacent) &&
                adjacent.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsBathingTile(WorldState world, TileCoord tile)
    {
        if (world.Tiles.Items.TryGetValue(tile, out var here) &&
            here.Flags.HasFlag(TileFlags.Water))
        {
            return true;
        }

        foreach (var direction in HexDirection.All)
        {
            var neighbour = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            if (world.Tiles.Items.TryGetValue(neighbour, out var adjacent) &&
                adjacent.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
    }
}

}
