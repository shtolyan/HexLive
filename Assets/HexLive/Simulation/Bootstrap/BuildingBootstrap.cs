using System;
using System.Collections.Generic;
using System.Linq;
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
        var hut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Hut1Hex, center.Fragment, hutTile, anchorId);
        BuildingRules.EnsureHutElements(world, hut, completed: true);
        var desiredDoorYaw = StructurePlacement.FacingYaw(center.WorldPosition, homePosition);
        var localDoorYaw = BuildingRules.DoorOutwardYaw(world, hut);
        hut.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(
            desiredDoorYaw - localDoorYaw);
        CompleteHut(world, hut);
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
        BuildingRules.EnsureHutElements(world, site);
        var localDoorYaw = BuildingRules.DoorOutwardYaw(world, site);
        site.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(
            facingYaw - localDoorYaw);
        return site;
    }

    /// <summary>
    /// Finalises either a bootstrap hut or a normally raised hut. Indoor is
    /// granted only here, after the leaf stage has completed and the site has
    /// already become the finished building object.
    /// </summary>
    public static void CompleteHut(WorldState world, WorldObjectState hut)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Tiles.Items.TryGetValue(hut.Tile, out var tile))
        {
            return;
        }

        // Save/load and debug callers may supply legacy free yaw. Normalise at
        // the architectural boundary before deriving a portal edge or furniture.
        hut.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(hut.RotationDegrees);

        BuildingRules.EnsureHutElements(world, hut, completed: true);
        foreach (var piece in BuildingRules.ArchitectureObjects(world, hut))
            piece.RotationDegrees = hut.RotationDegrees;
        tile.Flags |= TileFlags.HasFloor | TileFlags.Indoor;
        RepairHutTopology(world, hut);
        SpawnCot(world, hut, 0);
        SpawnCot(world, hut, 1);
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

    /// <summary>
    /// Rebuilds the footprint from its persisted LEGO elements. Used both at
    /// completion and after loading an older save whose visual door and saved
    /// junction flags may have been authored by different rules.
    /// </summary>
    public static void RepairHutTopology(WorldState world, WorldObjectState hut)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Tiles.Items.TryGetValue(hut.Tile, out var tile)) return;

        foreach (var blockedId in hut.BlockedJunctions)
        {
            if (world.Junctions.Items.TryGetValue(blockedId, out var blocked))
                blocked.Blocked = false;
        }
        hut.BlockedJunctions.Clear();
        var architecture = BuildingRules.ArchitectureObjects(world, hut).ToArray();
        foreach (var piece in architecture)
        {
            foreach (var blockedId in piece.BlockedJunctions)
                if (world.Junctions.Items.TryGetValue(blockedId, out var blocked))
                    blocked.Blocked = false;
            piece.BlockedJunctions.Clear();
            AnchorArchitecturePiece(world, hut, piece);
        }
        foreach (var junctionId in tile.Junctions)
        {
            if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
                junction.Door = false;
        }

        var local = BuildingRules.DoorLocalCenter(world, hut);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var doorCenter = HexSpatialMath.TileToWorld(hut.Tile) + new Float2(
            local.X * cos - local.Y * sin,
            local.X * sin + local.Y * cos);
        var delta = doorCenter - HexSpatialMath.TileToWorld(hut.Tile);
        var doorYaw = MathF.Atan2(delta.Y, delta.X) * 180f / MathF.PI;
        var doorEdge = DoorEdgeForYaw(doorYaw);
        hut.Variant = $"door:{doorEdge}";
        SealPerimeter(world, hut, architecture, doorEdge, doorCenter);
    }

    private static void SealPerimeter(
        WorldState world, WorldObjectState hut, WorldObjectState[] architecture,
        int doorEdge, Float2 doorCenter)
    {
        var tile = world.Tiles.Items[hut.Tile];
        var portals = new HashSet<JunctionId>();
        for (var edge = 0; edge < HexDirection.All.Length; edge++)
        {
            var direction = HexDirection.All[edge];
            var neighbor = new TileCoord(hut.Tile.Q + direction.DQ, hut.Tile.R + direction.DR);
            if (edge == doorEdge)
            {
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
                    // Three junctions nearest the actual half-edge door module,
                    // not three nearest the abstract centre of the whole edge.
                    var ap = world.Junctions.Items[a].WorldPosition - doorCenter;
                    var bp = world.Junctions.Items[b].WorldPosition - doorCenter;
                    return (ap.X * ap.X + ap.Y * ap.Y).CompareTo(
                        bp.X * bp.X + bp.Y * bp.Y);
                });
                for (var i = 0; i < Math.Min(3, candidates.Count); i++)
                {
                    var portalId = candidates[i];
                    portals.Add(portalId);
                    world.Junctions.Items[portalId].Door = true;
                }
                var doorPiece = architecture.FirstOrDefault(piece =>
                    piece.DefinitionId == "architecture.door.wood");
                if (doorPiece != null)
                {
                    doorPiece.Junctions.Clear();
                    doorPiece.Junctions.AddRange(portals);
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
                var blocker = NearestBlockingPiece(world, hut, architecture, junction.WorldPosition);
                if (blocker != null && !blocker.BlockedJunctions.Contains(junctionId))
                    blocker.BlockedJunctions.Add(junctionId);
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

    private static void AnchorArchitecturePiece(
        WorldState world, WorldObjectState hut, WorldObjectState piece)
    {
        var element = piece.ArchitectureElements.Count == 1
            ? piece.ArchitectureElements[0]
            : null;
        if (element == null) return;
        var target = ArchitectureWorldPosition(hut, element);
        var nearest = world.Tiles.Items[hut.Tile].Junctions
            .Where(id => world.Junctions.Items.ContainsKey(id))
            .OrderBy(id => DistanceSquared(world.Junctions.Items[id].WorldPosition, target))
            .FirstOrDefault();
        piece.Junctions.Clear();
        piece.Junctions.Add(nearest);
    }

    private static WorldObjectState NearestBlockingPiece(
        WorldState world, WorldObjectState hut, WorldObjectState[] pieces, Float2 position)
    {
        WorldObjectState best = null;
        var bestDistance = float.MaxValue;
        foreach (var piece in pieces)
        {
            if (piece.ArchitectureElements.Count != 1) continue;
            var element = piece.ArchitectureElements[0];
            if (!element.Complete || element.DefinitionId is not
                    ("architecture.wall.wood" or "architecture.window.wood" or
                     "architecture.door.wood" or "architecture.support.wood"))
                continue;
            var distance = DistanceSquared(position, ArchitectureWorldPosition(hut, element));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = piece;
        }
        return best;
    }

    private static Float2 ArchitectureWorldPosition(
        WorldObjectState hut, ArchitectureElementState element)
    {
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return HexSpatialMath.TileToWorld(hut.Tile) + new Float2(
            element.LocalX * cos - element.LocalZ * sin,
            element.LocalX * sin + element.LocalZ * cos);
    }

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var delta = a - b;
        return delta.X * delta.X + delta.Y * delta.Y;
    }

    private static void SpawnCot(WorldState world, WorldObjectState hut, int slot)
    {
        if (CotCount(world, hut.Tile) >= 2) return;
        if (FindCotJunction(world, hut, CotLocalPosition(slot)) is not { } junctionId) return;
        var cot = WorldObjectMutations.SpawnObject(
            world, ContentIds.BedBasic, hut.Fragment, hut.Tile, junctionId);
        cot.Variant = ContentIds.HutBedVariant;
        cot.RotationDegrees = StructurePlacement.QuantizeHexYaw(
            hut.RotationDegrees + CotLocalYaw(slot));
        // Architecture owns the room topology. Integrated beds reserve their
        // furniture footprint but must not seal the one-hex interior corridor.
        WorldObjectMutations.SetObstacleBlocking(world, cot, blocked: false);
    }

    private static JunctionId? FindCotJunction(WorldState world, WorldObjectState hut, Float2 local)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(local, radians);
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
                if (FindCotRepairJunction(world, hut, CotLocalPosition(i), used) is not { } anchor) continue;
                cots[i].Junctions.Clear();
                cots[i].Junctions.Add(anchor);
                cots[i].RotationDegrees = StructurePlacement.QuantizeHexYaw(
                    hut.RotationDegrees + CotLocalYaw(i));
                used.Add(anchor);
            }
        }
    }

    private static JunctionId? FindCotRepairJunction(
        WorldState world, WorldObjectState hut, Float2 local, HashSet<JunctionId> used)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(local, radians);
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

    private static Float2 CotLocalPosition(int slot) => slot == 0
        ? new Float2(BuildingRules.HutBed0LocalX, BuildingRules.HutBed0LocalZ)
        : new Float2(BuildingRules.HutBed1LocalX, BuildingRules.HutBed1LocalZ);

    private static float CotLocalYaw(int slot)
    {
        var bed = CotLocalPosition(slot);
        var hearth = new Float2(BuildingRules.HutHearthLocalX, BuildingRules.HutHearthLocalZ);
        var towardHearth = hearth - bed;
        var yaw = slot == 0 ? BuildingRules.HutBed0LocalYaw : BuildingRules.HutBed1LocalYaw;
        var radians = yaw * MathF.PI / 180f;
        // Footprint yaw maps to Unity yaw=-yaw. The sleep head is -Unity-forward,
        // hence its simulation X/Y direction is (sin(yaw), -cos(yaw)). Choose
        // the bed axis' 180° symmetry that points that head toward the hearth.
        var head = new Float2(MathF.Sin(radians), -MathF.Cos(radians));
        if (head.X * towardHearth.X + head.Y * towardHearth.Y < 0f) yaw += 180f;
        return StructurePlacement.QuantizeHexYaw(yaw);
    }

    private static Float2 RotateLocal(Float2 local, float radians) => new(
        local.X * MathF.Cos(radians) - local.Y * MathF.Sin(radians),
        local.X * MathF.Sin(radians) + local.Y * MathF.Cos(radians));

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

    /// <summary>
    /// Re-seats the one household hearth from canonical hut-local geometry.
    /// This deliberately repairs current-version saves too: early v33 builds
    /// persisted the former 90-degree furniture basis.
    /// </summary>
    public static void RepairIntegratedHearthAnchor(WorldState world, WorldObjectState hut)
    {
        if (hut == null || hut.DefinitionId != ContentIds.Hut1Hex ||
            !world.Caches.ObjectsByTile.TryGetValue(hut.Tile, out var objects)) return;

        WorldObjectState hearth = null;
        foreach (var id in objects)
        {
            if (world.Entities.Objects.TryGetValue(id, out var candidate) &&
                candidate.DefinitionId == ContentIds.Campfire)
            {
                hearth = candidate;
                if (candidate.Variant == BuildingRules.HutHearthVariant) break;
            }
        }

        if (hearth == null)
        {
            SpawnHearth(world, hut);
            return;
        }

        foreach (var blockedId in hearth.BlockedJunctions)
        {
            if (world.Junctions.Items.TryGetValue(blockedId, out var blocked)) blocked.Blocked = false;
        }
        hearth.BlockedJunctions.Clear();
        hearth.Junctions.Clear();
        hearth.Variant = BuildingRules.HutHearthVariant;
        hearth.RotationDegrees = hut.RotationDegrees;
        WorldObjectMutations.SetObstacleBlocking(world, hearth, blocked: false);

        if (FindHearthJunction(world, hut) is not { } junctionId) return;
        hearth.Junctions.Add(junctionId);
        if (world.Junctions.Items.TryGetValue(junctionId, out var anchor))
        {
            anchor.Blocked = true;
            hearth.BlockedJunctions.Add(junctionId);
        }
        world.TopologyVersion++;
    }

    private static JunctionId? FindHearthJunction(WorldState world, WorldObjectState hut)
    {
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var desired = center + RotateLocal(
            new Float2(BuildingRules.HutHearthLocalX, BuildingRules.HutHearthLocalZ), radians);
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
