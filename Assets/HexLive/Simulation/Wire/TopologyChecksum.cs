using System;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// A fingerprint of the world's static geometry.
/// <para>
/// The client does not download the ~14 000 junctions — it regenerates them from
/// the seed, exactly as loading a save already does ("static topology is rebuilt,
/// never stored"). That is sound because worldgen is a pure function of the seed
/// and the tuned catalogs, with no RNG state and no ambient time.
/// </para>
/// <para>
/// It has one soft spot worth guarding: island height comes out of a
/// <c>MathF.Round(height * 6f)</c>, so a one-ULP float difference right at a .5
/// boundary would flip a single tile's elevation, and from there its water and
/// walkable flags. Comparing this number at connect time turns that from a subtle
/// wrong-looking island into a clean, loud refusal.
/// </para>
/// <para>
/// Lives in the SERVER, not the simulation, on purpose: it is a protocol concern
/// (does your world match mine?), not a rule of the world.
/// </para>
/// </summary>
public static class TopologyChecksum
{
    public static uint Compute(WorldState world)
    {
        var h = 2166136261u;

        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            h = Mix(h, (uint)tile.Coord.Q);
            h = Mix(h, (uint)tile.Coord.R);
            h = Mix(h, (uint)tile.Elevation);
            h = Mix(h, (uint)tile.Flags);
        }

        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            h = Mix(h, (uint)junction.Id.Value);
            // SingleToInt32Bits + cast, NOT SingleToUInt32Bits: bit-identical,
            // but available in the Unity assembly's target framework too (the
            // unsigned variant is .NET 5+ only and broke the headless build).
            h = Mix(h, unchecked((uint)BitConverter.SingleToInt32Bits(junction.WorldPosition.X)));
            h = Mix(h, unchecked((uint)BitConverter.SingleToInt32Bits(junction.WorldPosition.Y)));
        }

        return h;
    }

    private static uint Mix(uint h, uint value)
    {
        unchecked
        {
            return (h ^ value) * 16777619u;
        }
    }
}

}
