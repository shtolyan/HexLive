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
/// ⭐ It hashes INTEGERS ONLY, and that is the whole lesson of the first real
/// server test. The original version also hashed the raw float bits of every
/// junction's world position, and that made the fingerprint a fingerprint of the
/// RUNTIME as much as of the world: measured, the editor (Mono) and the server
/// (.NET 9, and IL2CPP with it) disagree by exactly one ULP in
/// <c>HexPointLayout.ToLocalOffset</c> — y = 0.9374999 against 0.9375 — because
/// Mono evaluates a float chain through double and rounds once at the store.
/// Tiles were bit-identical; only the derived positions drifted. So the editor
/// could never watch a server at all, while a build of the same commit could,
/// and the refusal blamed "different builds" for a difference no player could
/// ever see. Ids, touching tiles and the neighbour graph say everything the
/// positions did — a genuinely different island moves them all — and they are
/// exact on every runtime. Do not put a float back in here; if a future field
/// really needs one, quantise it, and remember that <c>MathF.Round</c> has its
/// own .5 boundary for the ULP to sit on.
/// </para>
/// <para>
/// Everything hashed here must be WORLDGEN-static. Junction ids, tiles and
/// neighbours are (they are filled in <c>WorldStateFactory</c> and never touched
/// again), <c>Blocked</c>/<c>Door</c> are not and are deliberately absent. Tile
/// flags are the awkward pair: most are worldgen, but <c>HasFloor</c>/
/// <c>Roofed</c> arrive when the colony builds — which is why the server takes
/// this fingerprint BEFORE it restores a save, not after.
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

            // Which tiles this junction touches, and who it is wired to: the
            // sub-grid's shape in integers. The counts go in too, so a junction
            // that lost an edge cannot hash the same as one that never had it.
            var tiles = junction.Tiles;
            h = Mix(h, (uint)tiles.Count);
            for (var i = 0; i < tiles.Count; i++)
            {
                h = Mix(h, (uint)tiles[i].Q);
                h = Mix(h, (uint)tiles[i].R);
            }

            var neighbors = junction.Neighbors;
            h = Mix(h, (uint)neighbors.Count);
            for (var i = 0; i < neighbors.Count; i++)
            {
                h = Mix(h, (uint)neighbors[i].Value);
            }
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
