using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{

/// <summary>
/// First vertical slice of the architectural-building grammar: one hex-owned
/// hut, one portal edge, two integrated (non-blocking) cot anchors, and one
/// protected stone hearth at the rear of the room.
/// </summary>
public static class BuildingBootstrap
{
    // The exported pointy-top kit's Bay_00 sits on the lower-right edge.
    // Its outward normal is local -60°, i.e. building yaw +300° once the
    // complete footprint uses identity-preserving X/Y -> X/Z rotation.
    internal const float AuthoredDoorOutwardYaw = 300f;

    public static WorldObjectState SpawnCompletedTestHut(WorldState world, Faction faction)
    {
        var home = ColonyQueries.Home(world, faction) ?? new TileCoord(0, 4);
        if (!world.Tiles.Items.TryGetValue(home, out var homeTile)) return null;

        var candidates = new List<TileCoord>();
        for (var ring = 1; ring <= 2 && candidates.Count == 0; ring++)
        {
            foreach (var pair in world.Tiles.Items)
            {
                var coord = pair.Key;
                var tile = pair.Value;
                if (HexSpatialMath.HexDistance(coord, home) != ring ||
                    tile.Elevation != homeTile.Elevation ||
                    tile.Flags.HasFlag(TileFlags.Indoor) ||
                    !CanPlaceHut(world, coord))
                {
                    continue;
                }

                candidates.Add(coord);
            }
        }

        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
        var roll = MathUtil.Hash01(world.Seed, 1, 1, 6601);
        var index = Math.Min(candidates.Count - 1, (int)(roll * candidates.Count));
        var hutTile = candidates[index];
        var anchor = StructurePlacement.CenterJunction(world, hutTile);
        if (anchor is not { } anchorId ||
            !world.Junctions.Items.TryGetValue(anchorId, out var center))
        {
            return null;
        }

        var homePosition = HexSpatialMath.TileToWorld(home);
        // The authored pointy-top kit has its door bay outward normal at
        // building yaw +300°.
        // Keep the hex itself on one of its six 60° symmetries and choose the
        // symmetry whose door normal is closest to camp. Arbitrary yaw rotates
        // walls off the tile edges; treating local forward as the door normal
        // seals the neighbouring edge instead of the visible doorway.
        var desiredDoorYaw = StructurePlacement.FacingYaw(center.WorldPosition, homePosition);
        var yaw = StructurePlacement.QuantizeHexSymmetryYaw(
            desiredDoorYaw - AuthoredDoorOutwardYaw);
        var hut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Hut1Hex, center.Fragment, hutTile, anchorId);
        hut.RotationDegrees = yaw;
        CompleteHut(world, hut, DoorEdgeFacing(world, hutTile, homePosition));
        return hut;
    }

    /// <summary>Creates an ordinary modular site for tests and future NPC staking.</summary>
    public static WorldObjectState CreateHutSite(WorldState world, TileCoord tile, float facingYaw)
    {
        if (!CanPlaceHut(world, tile) || StructurePlacement.CenterJunction(world, tile) is not { } anchor)
        {
            return null;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, world.Junctions.Items[anchor].Fragment, tile, anchor);
        site.BuildProduct = ContentIds.Hut1Hex;
        site.BillSticks = BuildingRules.TotalSticks;
        site.BillBoards = BuildingRules.TotalBoards;
        site.BillRope = BuildingRules.TotalRope;
        site.BillLeaves = BuildingRules.TotalLeaves;
        site.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(
            facingYaw - AuthoredDoorOutwardYaw);
        BuildingRules.EnsureHutElements(site);
        return site;
    }

    /// <summary>
    /// Finalises either a bootstrap hut or a normally raised hut. Indoor is
    /// granted only here, after the leaf stage has completed and the site has
    /// already become the finished building object.
    /// </summary>
    public static void CompleteHut(WorldState world, WorldObjectState hut, int? authoredDoorEdge = null)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Tiles.Items.TryGetValue(hut.Tile, out var tile))
        {
            return;
        }

        // Save/load and debug callers may supply legacy free yaw. Normalise at
        // the architectural boundary before deriving a portal edge or furniture.
        hut.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(hut.RotationDegrees);

        var doorEdge = authoredDoorEdge ?? DoorEdgeForYaw(
            hut.RotationDegrees + AuthoredDoorOutwardYaw);
        BuildingRules.EnsureHutElements(hut, completed: true);
        hut.Variant = $"door:{doorEdge}";
        tile.Flags |= TileFlags.HasFloor | TileFlags.Indoor;
        SealPerimeter(world, hut, doorEdge);
        SpawnCot(world, hut, -0.75f);
        SpawnCot(world, hut, 0.75f);
        RepairIntegratedCotAnchors(world);
        SpawnHearth(world, hut);
    }

    public static bool CanPlaceHut(WorldState world, TileCoord tile)
    {
        if (!StructurePlacement.HexFreeForBuild(world, tile) ||
            !world.Tiles.Items.TryGetValue(tile, out var footprint))
        {
            return false;
        }

        // Keep the test doorway connected to real land and avoid shoreline
        // half-hexes whose edge junctions were already sealed by worldgen.
        var dryNeighbors = 0;
        foreach (var direction in HexDirection.All)
        {
            var neighbor = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            if (world.Tiles.Items.TryGetValue(neighbor, out var other) &&
                other.Flags.HasFlag(TileFlags.Walkable) &&
                !other.Flags.HasFlag(TileFlags.Water) &&
                !other.Flags.HasFlag(TileFlags.Blocked) &&
                other.Elevation == footprint.Elevation)
            {
                dryNeighbors++;
            }
        }

        return dryNeighbors >= 3;
    }

    private static void SealPerimeter(WorldState world, WorldObjectState hut, int doorEdge)
    {
        var tile = world.Tiles.Items[hut.Tile];
        var portals = new HashSet<JunctionId>();
        for (var edge = 0; edge < HexDirection.All.Length; edge++)
        {
            var direction = HexDirection.All[edge];
            var neighbor = new TileCoord(hut.Tile.Q + direction.DQ, hut.Tile.R + direction.DR);
            if (edge == doorEdge)
            {
                var edgeCenter = (HexSpatialMath.TileToWorld(hut.Tile) +
                    HexSpatialMath.TileToWorld(neighbor)) * 0.5f;
                var candidates = new List<JunctionId>();
                foreach (var junctionId in tile.Junctions)
                {
                    if (world.Junctions.Items.TryGetValue(junctionId, out var candidate) &&
                        candidate.Tiles.Count >= 2 && candidate.Tiles.Contains(neighbor) &&
                        !candidate.Blocked)
                    {
                        candidates.Add(junctionId);
                    }
                }

                candidates.Sort((a, b) =>
                {
                    var ap = world.Junctions.Items[a].WorldPosition - edgeCenter;
                    var bp = world.Junctions.Items[b].WorldPosition - edgeCenter;
                    return (ap.X * ap.X + ap.Y * ap.Y).CompareTo(
                        bp.X * bp.X + bp.Y * bp.Y);
                });
                for (var i = 0; i < Math.Min(3, candidates.Count); i++)
                {
                    var portalId = candidates[i];
                    portals.Add(portalId);
                    world.Junctions.Items[portalId].Door = true;
                }
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (portals.Contains(junctionId)) continue;
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                    junction.Tiles.Count < 2 || !junction.Tiles.Contains(neighbor) || junction.Blocked)
                {
                    continue;
                }

                junction.Blocked = true;
                if (!hut.BlockedJunctions.Contains(junctionId)) hut.BlockedJunctions.Add(junctionId);
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    if (npc.CurrentJunction is { } current && current.Equals(junctionId))
                    {
                        npc.CurrentJunction = null;
                    }
                }
            }
        }

        world.TopologyVersion++;
    }

    private static void SpawnCot(WorldState world, WorldObjectState hut, float localX)
    {
        if (CotCount(world, hut.Tile) >= 2) return;
        if (FindCotJunction(world, hut, localX) is not { } junctionId) return;
        var cot = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, hut.Fragment, hut.Tile, junctionId);
        cot.Variant = ContentIds.HutBedVariant;
        cot.RotationDegrees = StructurePlacement.QuantizeHexYaw(hut.RotationDegrees);
        // Architecture owns the room topology. Integrated beds reserve their
        // furniture footprint but must not seal the one-hex interior corridor.
        WorldObjectMutations.SetObstacleBlocking(world, cot, blocked: false);
    }

    private static JunctionId? FindCotJunction(WorldState world, WorldObjectState hut, float localX)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        // local +Z is building forward; local +X is its right-hand axis.
        var right = new Float2(MathF.Sin(radians), -MathF.Cos(radians));
        var desired = center + right * localX;
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked ||
                CotAlreadyUses(world, hut.Tile, junctionId))
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y > 0.9f * 0.9f) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static bool CotAlreadyUses(WorldState world, TileCoord tile, JunctionId junction)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objects)) return false;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == ContentIds.BedBasic &&
                obj.Variant == ContentIds.HutBedVariant && obj.Junctions.Contains(junction))
            {
                return true;
            }
        }

        return false;
    }

    private static int CotCount(WorldState world, TileCoord tile)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objects)) return 0;
        var count = 0;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == ContentIds.BedBasic &&
                obj.Variant == ContentIds.HutBedVariant)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Old saves may contain both former <c>building.hut_bed</c> objects on the
    /// same junction. Re-seat every integrated pair on the two authored wall
    /// sides while keeping ids, ownership and interaction state intact.
    /// </summary>
    public static void RepairIntegratedCotAnchors(WorldState world)
    {
        foreach (var hut in world.Entities.Objects.Values)
        {
            if (hut.DefinitionId != ContentIds.Hut1Hex ||
                !world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objectIds))
            {
                continue;
            }

            var cots = new List<WorldObjectState>();
            foreach (var id in objectIds)
            {
                if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                    obj.DefinitionId == ContentIds.BedBasic &&
                    obj.Variant == ContentIds.HutBedVariant)
                {
                    WorldObjectMutations.SetObstacleBlocking(world, obj, blocked: false);
                    cots.Add(obj);
                }
            }

            if (cots.Count == 0) continue;
            cots.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            var used = new HashSet<JunctionId>();
            for (var i = 0; i < cots.Count && i < 2; i++)
            {
                var localX = i == 0 ? -0.75f : 0.75f;
                if (FindCotRepairJunction(world, hut, localX, used) is not { } anchor) continue;
                cots[i].Junctions.Clear();
                cots[i].Junctions.Add(anchor);
                used.Add(anchor);
            }
        }
    }

    private static JunctionId? FindCotRepairJunction(
        WorldState world, WorldObjectState hut, float localX, HashSet<JunctionId> used)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var right = new Float2(MathF.Sin(radians), -MathF.Cos(radians));
        var desired = center + right * localX;
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (used.Contains(junctionId) ||
                !world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked)
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y > 0.9f * 0.9f) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static void SpawnHearth(WorldState world, WorldObjectState hut)
    {
        if (world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects))
        {
            foreach (var id in objects)
            {
                if (world.Entities.Objects.TryGetValue(id, out var existing) &&
                    existing.DefinitionId == ContentIds.Campfire &&
                    existing.Variant == BuildingRules.HutHearthVariant)
                {
                    return;
                }
            }
        }

        if (FindHearthJunction(world, hut) is not { } junctionId) return;
        var hearth = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, hut.Fragment, hut.Tile, junctionId);
        hearth.Variant = BuildingRules.HutHearthVariant;
        hearth.RotationDegrees = hut.RotationDegrees;
        hearth.ResourceAmount = 0f;
        AddContents(hearth, ContentIds.Stick, SimBalance.CampfireBillSticks);
        AddContents(hearth, ContentIds.Rope, SimBalance.CampfireBillRope);
        AddContents(hearth, ContentIds.Stone, SimBalance.CampfireBillStones);

        // A normal outdoor campfire blocks its full visual radius. The compact
        // household hearth owns only its stone-lined anchor, leaving routes to
        // both beds and the door open inside this very small room.
        WorldObjectMutations.SetObstacleBlocking(world, hearth, blocked: false);
        if (world.Junctions.Items.TryGetValue(junctionId, out var anchor) && !anchor.Blocked)
        {
            anchor.Blocked = true;
            hearth.BlockedJunctions.Add(junctionId);
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.CurrentJunction is { } current && current.Equals(junctionId))
                {
                    npc.CurrentJunction = null;
                }
            }

            world.TopologyVersion++;
        }
    }

    private static JunctionId? FindHearthJunction(WorldState world, WorldObjectState hut)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var forward = new Float2(MathF.Cos(radians), MathF.Sin(radians));
        var desired = center - forward * 0.55f;
        JunctionId? best = null;
        var bestSq = float.MaxValue;
        foreach (var junctionId in world.Tiles.Items[hut.Tile].Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate) ||
                candidate.Tiles.Count != 1 || candidate.Blocked ||
                InteriorObjectUses(world, hut, junctionId))
            {
                continue;
            }

            var fromCenter = candidate.WorldPosition - center;
            if (fromCenter.X * fromCenter.X + fromCenter.Y * fromCenter.Y > 0.9f * 0.9f) continue;
            var delta = candidate.WorldPosition - desired;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = junctionId;
            }
        }

        return best;
    }

    private static bool InteriorObjectUses(
        WorldState world, WorldObjectState hut, JunctionId junction)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects)) return false;
        foreach (var id in objects)
        {
            if (id.Equals(hut.Id)) continue;
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.Junctions.Contains(junction))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddContents(WorldObjectState target, string definitionId, int count)
    {
        for (var i = 0; i < count; i++) target.Contents.Add(new ItemInstance(definitionId));
    }

    private static int DoorEdgeFacing(WorldState world, TileCoord tile, Float2 focus)
    {
        var bestEdge = 0;
        var bestSq = float.MaxValue;
        for (var edge = 0; edge < HexDirection.All.Length; edge++)
        {
            var direction = HexDirection.All[edge];
            var neighbor = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            var position = HexSpatialMath.TileToWorld(neighbor);
            var delta = position - focus;
            var sq = delta.X * delta.X + delta.Y * delta.Y;
            if (sq < bestSq)
            {
                bestSq = sq;
                bestEdge = edge;
            }
        }

        return bestEdge;
    }

    private static int DoorEdgeForYaw(float yaw)
    {
        var radians = yaw * MathF.PI / 180f;
        var forward = new Float2(MathF.Cos(radians), MathF.Sin(radians));
        var bestEdge = 0;
        var bestDot = float.MinValue;
        for (var edge = 0; edge < HexDirection.All.Length; edge++)
        {
            var direction = HexDirection.All[edge];
            var offset = HexSpatialMath.Normalize(
                HexSpatialMath.TileToWorld(new TileCoord(direction.DQ, direction.DR)));
            var dot = offset.X * forward.X + offset.Y * forward.Y;
            if (dot > bestDot)
            {
                bestDot = dot;
                bestEdge = edge;
            }
        }

        return bestEdge;
    }
}

}
