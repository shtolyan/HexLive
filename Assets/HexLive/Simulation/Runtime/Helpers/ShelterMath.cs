using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// One simulation contract for "what is over this hex".
/// <para>
/// <see cref="IsIndoor"/> is the SANCTUARY (§35.4 r2): house warmth, the point
/// where dogs give up, the colony yard. It needs the whole closed region of a
/// building and is deliberately coarse.
/// </para>
/// <para>
/// <see cref="IsUnderRoof"/> is the PHYSICAL roof, per hex (§35.4 r3, bug #750):
/// a hex whose own six roof sectors are finished keeps the rain off whoever
/// stands on it, even while the neighbouring room of the same house is still
/// open to the sky. One dictionary lookup and a bit test — the flag is
/// maintained when roof sectors complete or are torn down, never rediscovered
/// per query. Sanctuary tiles keep their historical dryness, so a world
/// without such a roof behaves exactly as before.
/// </para>
/// </summary>
public static class ShelterMath
{
    private const TileFlags RoofOrSanctuary = TileFlags.Roofed | TileFlags.Indoor;

    public static bool IsIndoor(WorldState world, TileCoord tile) =>
        world.Tiles.Items.TryGetValue(tile, out var state) &&
        (state.Flags & TileFlags.Indoor) != 0;

    public static bool IsUnderRoof(WorldState world, TileCoord tile) =>
        world.Tiles.Items.TryGetValue(tile, out var state) &&
        (state.Flags & RoofOrSanctuary) != 0;

    public static bool RainReaches(WorldState world, TileCoord tile) =>
        world.Environment.IsRaining && !IsUnderRoof(world, tile);
}

}
