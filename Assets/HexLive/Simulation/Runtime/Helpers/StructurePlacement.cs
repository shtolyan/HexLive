using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// Spec §66: ONE HEX = ONE BUILD. Every raised piece (the hearth, the beds, the
// drying rack — anything a build.site becomes) sits at the CENTRE of its hex and
// owns that hex alone; it may stand at any yaw. Before §66 a site was staked on
// whatever junction of a fireside tile happened to be free, so pieces sat
// off-centre, leaned into their neighbour's hex and read as scattered clutter.
//
// The rules this helper enforces:
//   * anchor  — the junction closest to the tile centre (the sub-grid always has
//               one exactly there: HexPointLayout's (0,0) interior point);
//   * claim   — the hex INTERIOR (HexClaimRadius). The rim the hex shares with
//               its neighbours is deliberately outside the claim, so a build
//               never vetoes the hex next door and the rim stays walkable;
//   * one per hex — a tile already holding a structure (furniture, a site, a
//               grave: anything non-portable) is spoken for;
//   * yaw     — stored on the object (WorldObjectState.RotationDegrees, sim-angle
//               convention: degrees CCW from +X, same as NPCState.RotationDegrees)
//               and rendered by the presentation layer.
//
// Old worlds are untouched: this is placement-time only, and objects staked
// before §66 keep their junction anchor and yaw 0.
internal static class StructurePlacement
{
    // What a centred build occupies inside its own hex. The interior sub-grid
    // tops out at 0.75R from the centre and the shared rim starts at the apothem
    // (0.866R), so 0.8R claims the whole interior and nothing beyond it.
    public const float HexClaimRadius = 0.8f * HexSpatialMath.HexRadius;

    // The junction at the hex centre (HexPointLayout interior point (0,0)).
    public static JunctionId? CenterJunction(WorldState world, TileCoord coord)
    {
        if (!world.Tiles.Items.TryGetValue(coord, out var tile))
        {
            return null;
        }

        var center = HexSpatialMath.TileToWorld(coord);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var jid in tile.Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(jid, out var junction))
            {
                continue;
            }

            var dx = junction.WorldPosition.X - center.X;
            var dy = junction.WorldPosition.Y - center.Y;
            var sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = jid;
            }
        }

        return best;
    }

    // §66: may a build be raised on this hex? The tile must be dry walkable
    // ground, hold no other structure, and its whole INTERIOR must be clear of
    // blocked points (a boulder, a palm, the fire's ember disc) and water — the
    // piece stands in the middle, so the middle has to be free. The shared rim is
    // NOT part of the test: a neighbour's footprint spilling onto the rim must
    // never veto this hex, or a fireside ring could hold at most every other bed.
    public static bool HexFreeForBuild(WorldState world, TileCoord coord)
    {
        if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
            tile.Flags.HasFlag(TileFlags.Water) ||
            tile.Flags.HasFlag(TileFlags.Blocked) ||
            TileHoldsStructure(world, coord))
        {
            return false;
        }

        if (CenterJunction(world, coord) is not { } centerId ||
            !world.Junctions.Items.TryGetValue(centerId, out var center) ||
            center.Blocked ||
            !SpatialQueries.IsJunctionFree(world, centerId))
        {
            return false;
        }

        var claimSq = HexClaimRadius * HexClaimRadius;
        foreach (var jid in tile.Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(jid, out var junction))
            {
                continue;
            }

            var dx = junction.WorldPosition.X - center.WorldPosition.X;
            var dy = junction.WorldPosition.Y - center.WorldPosition.Y;
            if (dx * dx + dy * dy > claimSq)
            {
                continue; // rim — shared with the neighbours, not ours to claim
            }

            if (junction.Blocked || SpatialQueries.IsAllWaterJunction(world, jid))
            {
                return false;
            }
        }

        return true;
    }

    // A non-portable object (no PickUp interaction: a bed, a build-site, the
    // rack, the hearth, a grave…) parked on the tile — the hex is spoken for.
    public static bool TileHoldsStructure(WorldState world, TileCoord coord)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var ids))
        {
            return false;
        }

        foreach (var id in ids)
        {
            if (!world.Entities.Objects.TryGetValue(id, out var obj))
            {
                continue;
            }

            if (BuildSiteMath.IsSite(obj))
            {
                return true;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            var portable = false;
            foreach (var interaction in def.Interactions)
            {
                if (interaction.Type == InteractionType.PickUp)
                {
                    portable = true;
                    break;
                }
            }

            if (!portable)
            {
                return true;
            }
        }

        return false;
    }

    // §66 bed rule: the sleeper warms her SIDE at the fire, never her head or
    // her feet. The bed's long axis (its local forward) is therefore laid at a
    // right angle to the line back to the hearth — this returns that yaw.
    public static float SideOnYaw(Float2 anchor, Float2 focus)
    {
        return Normalize(FacingYaw(anchor, focus) + 90f);
    }

    // Yaw that points the object's forward straight at `focus` (the rack faces
    // the flames). Degenerate (anchor == focus) → 0.
    public static float FacingYaw(Float2 anchor, Float2 focus)
    {
        var dx = focus.X - anchor.X;
        var dy = focus.Y - anchor.Y;
        if (dx * dx + dy * dy < 1e-6f)
        {
            return 0f;
        }

        return Normalize(HexSpatialMath.AngleDegrees(new Float2(dx, dy)));
    }

    // §119: the workbench has an authored work point exactly two interior
    // sub-grid steps (0.75 wu) in front of its local -Z side. Junction spokes
    // sit at 30°+60°k (not 0°+60°k), so quantising to those six axes makes the
    // desired point an exact stable junction instead of merely a nearest one.
    public static float QuantizeHexYaw(float degrees)
    {
        var shifted = Normalize(degrees - 30f);
        var step = (int)System.Math.Round(shifted / 60f) % 6;
        return Normalize(30f + step * 60f);
    }

    public static JunctionId? WorkbenchJunction(
        WorldState world, TileCoord tile, JunctionId anchorId, float stationYaw)
    {
        if (!world.Junctions.Items.TryGetValue(anchorId, out var anchor))
        {
            return null;
        }

        var radians = (stationYaw + 180f) * (System.MathF.PI / 180f);
        var desired = new Float2(
            anchor.WorldPosition.X + System.MathF.Cos(radians) * Spec119.WorkbenchStandDistance,
            anchor.WorldPosition.Y + System.MathF.Sin(radians) * Spec119.WorkbenchStandDistance);
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        for (var dq = -1; dq <= 1; dq++)
        {
            for (var dr = -1; dr <= 1; dr++)
            {
                var coord = new TileCoord(tile.Q + dq, tile.R + dr);
                if (!world.Tiles.Items.TryGetValue(coord, out var candidateTile)) continue;
                foreach (var id in candidateTile.Junctions)
                {
                    if (!world.Junctions.Items.TryGetValue(id, out var junction) ||
                        junction.Blocked || !SpatialQueries.IsJunctionFree(world, id)) continue;
                    var dx = junction.WorldPosition.X - desired.X;
                    var dy = junction.WorldPosition.Y - desired.Y;
                    var sq = dx * dx + dy * dy;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = id;
                    }
                }
            }
        }
        return best;
    }

    // Wrapped into (0, 360] — NOT [0, 360). Exactly 0 is reserved to mean "no
    // §66 facing was ever assigned" (every object predating §66, every loose
    // item), which is how the presentation tells a placed piece from an old one
    // and leaves old worlds rendering exactly as they did. 360 ≡ 0 as a rotation.
    private static float Normalize(float degrees)
    {
        var d = degrees % 360f;
        if (d < 0f)
        {
            d += 360f;
        }

        return d <= 0f ? 360f : d;
    }
}

}
